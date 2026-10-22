using Microsoft.Data.Sqlite;
using VamSys.Core;
using static VamSys.Infrastructure.RowSerializer;
using static VamSys.Infrastructure.Sql;

namespace VamSys.Infrastructure;

// resource_history_state / resource_history_steps: undo and redo stacks, one row per step.
internal static class HistoryRepository
{
    public static void Write(SqliteConnection c, SqliteTransaction tx, Guid id, ResourceKind kind, StoredHistory h, bool full = false)
    {
        var args = new (string, object?)[] { ("$w", id.ToString()), ("$r", (int)kind) };
        foreach (var step in h.Deletes)
            Execute(c, tx, "DELETE FROM resource_history_steps WHERE workspace_id=$w AND resource=$r AND step_id=$id", [.. args, ("$id", step.ToString())]);
        foreach (var step in h.Steps)
            Execute(c, tx, "INSERT INTO resource_history_steps VALUES($w,$r,$id,$json) ON CONFLICT(workspace_id,resource,step_id) DO UPDATE SET json=$json", [.. args, ("$id", step.Id.ToString()), ("$json", step.Json)]);
        if (full)
            Execute(c, tx, "INSERT INTO resource_history_state VALUES($w,$r,$u,$d)", [.. args, ("$u", h.Undo), ("$d", h.Redo)]);
        else if (h.OrderChanged && Execute(c, tx, "UPDATE resource_history_state SET undo_json=$u,redo_json=$d WHERE workspace_id=$w AND resource=$r", [.. args, ("$u", h.Undo), ("$d", h.Redo)]) != 1)
            throw StorageErrors.Block("StorageInvalid");
    }

    public static void ClearSteps(SqliteConnection c, SqliteTransaction tx, string workspace, int kind)
    {
        Execute(c, tx, "DELETE FROM resource_history_steps WHERE workspace_id=$w AND resource=$r", ("$w", workspace), ("$r", kind));
        Execute(c, tx, "UPDATE resource_history_state SET undo_json='[]',redo_json='[]' WHERE workspace_id=$w AND resource=$r", ("$w", workspace), ("$r", kind));
    }

    public static void Read(SqliteConnection c, SqliteTransaction tx, Guid id, ResourceKind kind, ResourceData data)
    {
        var args = new (string, object?)[] { ("$w", id.ToString()), ("$r", (int)kind) };
        Guid[] undo, redo;
        using (var cmd = Command(c, tx, "SELECT undo_json,redo_json FROM resource_history_state WHERE workspace_id=$w AND resource=$r", args))
        using (var r = cmd.ExecuteReader())
        {
            if (!r.Read())
                throw StorageErrors.Block("StorageInvalid");
            undo = Decode<Guid[]>(r.GetString(0))!;
            redo = Decode<Guid[]>(r.GetString(1))!;
        }
        var steps = new Dictionary<Guid, HistoryStep>();
        using (var cmd = Command(c, tx, "SELECT step_id,json FROM resource_history_steps WHERE workspace_id=$w AND resource=$r", args))
        using (var r = cmd.ExecuteReader())
        while (r.Read())
        {
            var step = Decode<HistoryStep>(r.GetString(1))!;
            if (step.StepId != Guid.Parse(r.GetString(0)))
                throw StorageErrors.Block("StorageInvalid");
            steps.Add(step.StepId, step);
        }
        if (undo.Length + redo.Length != steps.Count)
            throw StorageErrors.Block("StorageInvalid");
        data.History = EditHistory.From(new([.. undo.Select(id => steps[id])], [.. redo.Select(id => steps[id])]));
        data.History.Validate(data);
        data.History.Acknowledge(data.History.Capture());
        data.InvalidateDraftIndex();
        if (data.Draft.Count > 0)
            data.FindDraftRow(data.Draft[0].LocalId);
    }
}
