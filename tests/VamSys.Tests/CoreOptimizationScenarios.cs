using System.Collections.Immutable;
using System.Text.Json;
using VamSys.Core;

static class CoreOptimizationScenarios
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("Optimized planning matches the original full-row oracle without sharing drafts", () =>
        {
            var random = new Random(20261015);
            foreach (var kind in Enum.GetValues<ResourceKind>())
            {
                var data = CorePerformanceBenchmark.Fixture(1000);
                foreach (var row in data.Draft)
                {
                    switch (random.Next(7))
                    {
                        case 0: row.Fields["Remarks"] = "edited"; break;
                        case 1: row.Fields["Remarks"] = ""; break;
                        case 2: row.Fields.Remove("Remarks"); break;
                        case 3: row.Fields["Unknown CSV"] = "unknown"; break;
                        case 4: row.Fields["_delete"] = "true"; break;
                    }
                }
                data.Draft.Add(new()); data.Draft.Add(new() { Fields = new() { ["_delete"] = "TRUE" } });
                var expected = OriginalPlan(kind, data); var actual = new ChangePlanner().Plan(kind, data);
                Check(Comparable(expected) == Comparable(actual), "Planner output changed");
                var baseline = data.Snapshot.ToDictionary(r => r.LocalId);
                var actualKinds = actual.ToDictionary(c => c.After.LocalId, c => c.Kind);
                foreach (var row in data.Draft)
                    Check(ChangePlanner.GetChangeKind(row, baseline.GetValueOrDefault(row.LocalId)) == (actualKinds.TryGetValue(row.LocalId, out var type) ? type : (ChangeKind?)null), "Classification changed");
                actual[0].After.Fields["Remarks"] = "detached";
                Check(data.FindDraftRow(actual[0].After.LocalId)!.Get("Remarks") != "detached", "Plan retained mutable draft");
            }
            return Task.CompletedTask;
        });
        await test("Position index survives same-ID replacements and rebuilds after structural edits", () =>
        {
            var data = CorePerformanceBenchmark.Fixture(20000); var list = data.Draft;
            var first = data.Draft[0]; var other = data.Draft[^1]; data.FindDraftRow(first.LocalId);
            var replacement = first.Copy(); replacement.Fields["Remarks"] = "replacement";
            data.Edit([first.LocalId], () => data.ReplaceDraftRows([replacement]));
            Check(ReferenceEquals(data.FindDraftRow(first.LocalId), replacement), "Index returned replaced object");
            data.UndoEdit(); Check(data.FindDraftRow(first.LocalId)!.Get("Remarks") == first.Get("Remarks"), "Undo index stale");
            data.RedoEdit(); Check(data.FindDraftRow(first.LocalId)!.Get("Remarks") == "replacement", "Redo index stale");
            Check(ReferenceEquals(list, data.Draft) && ReferenceEquals(other, data.Draft[^1]), "Local undo rebuilt untouched data");
            var added = new DataRow(); data.Edit([added.LocalId], () => data.Draft.Insert(0, added), structural:true);
            Check(data.FindDraftPosition(first.LocalId) == 1 && data.FindDraftRow(added.LocalId) == added, "Insert index stale");
            data.UndoEdit(); Check(data.FindDraftPosition(first.LocalId) == 0 && data.FindDraftRow(added.LocalId) is null, "Structural undo index stale");
            data.RedoEdit(); Check(data.FindDraftPosition(first.LocalId) == 1, "Structural redo index stale");
            data.Draft = data.Draft.AsEnumerable().Reverse().ToList();
            Check(data.FindDraftPosition(added.LocalId) == data.Draft.Count - 1, "Reorder index stale");
            var before = JsonSerializer.Serialize(data.Draft);
            try { data.ReplaceDraftRows([replacement, replacement]); throw new Exception("Duplicate accepted"); } catch (ArgumentException) { }
            Check(before == JsonSerializer.Serialize(data.Draft), "Replacement partially applied");
            return Task.CompletedTask;
        });
        await test("History rejects damaged later patches and structural order before changing any row or stack", () =>
        {
            var data = CorePerformanceBenchmark.Fixture(3); var ids = data.Draft.Take(2).Select(r => r.LocalId).ToArray();
            data.Edit(ids, () => { foreach (var id in ids) data.FindDraftRow(id)!.Fields["Remarks"] = "new"; });
            var step = data.Undo[0]; var edits = step.Rows.ToArray();
            edits[1] = edits[1] with { Before = edits[1].Before! with { FieldOrder = ImmutableArray.Create("missing") } };
            data.History = EditHistory.From(new([step with { Rows = [..edits] }], []));
            var before = JsonSerializer.Serialize(data.Draft); var list = data.Draft;
            try { data.UndoEdit(); throw new Exception("Invalid history accepted"); } catch (InvalidDataException) { }
            Check(before == JsonSerializer.Serialize(data.Draft) && ReferenceEquals(list, data.Draft) && data.Undo.Count == 1 && data.Redo.Count == 0, "History partially applied");
            var structure=CorePerformanceBenchmark.Fixture(3);var added=new DataRow();
            structure.Edit([added.LocalId],()=>structure.Draft.Add(added),structural:true);
            var order=structure.Undo[0].BeforeOrder!.Value.ToArray();order[1]=order[0];
            structure.History=EditHistory.From(new([structure.Undo[0] with{BeforeOrder=[..order]}],[]));
            var original=JsonSerializer.Serialize(structure.Draft);var originalList=structure.Draft;
            try{structure.UndoEdit();throw new Exception("Duplicate order accepted");}catch(InvalidDataException){}
            Check(original==JsonSerializer.Serialize(structure.Draft)&&ReferenceEquals(originalList,structure.Draft)&&structure.Undo.Count==1&&structure.Redo.Count==0,"Structural history partially applied");
            return Task.CompletedTask;
        });
        await test("History validation keeps live rows independent across both replay directions", () =>
        {
            var data = CorePerformanceBenchmark.Fixture(1000);
            for (var i = 0; i < 20; i++) { var row = data.Draft[i]; data.Edit([row.LocalId], () => row.Fields["Remarks"] = "edit " + i); }
            for (var i = 0; i < 5; i++) data.UndoEdit();
            var rows = data.Draft.ToArray(); var before = JsonSerializer.Serialize(data.Draft);
            data.History.Validate(data);
            Check(before == JsonSerializer.Serialize(data.Draft) && rows.Zip(data.Draft).All(p => ReferenceEquals(p.First, p.Second)), "Validation mutated live state");
            return Task.CompletedTask;
        });
    }
    static string Comparable(IEnumerable<ChangeItem> items) => JsonSerializer.Serialize(items.Select(c => new { c.Resource, c.Kind, c.Before, c.After, c.Fields }));
    static List<ChangeItem> OriginalPlan(ResourceKind kind, ResourceData data)
    {
        var baseline = data.Snapshot.ToDictionary(r => r.LocalId); var result = new List<ChangeItem>();
        foreach (var row in data.Draft)
        {
            baseline.TryGetValue(row.LocalId, out var before);
            var fields = row.Fields.Where(p => p.Key != "_delete" && (before is null || !before.Fields.TryGetValue(p.Key, out var old) || old != p.Value))
                .ToDictionary(p => p.Key, p => new FieldChange(p.Value == "" ? FieldIntent.Clear : FieldIntent.Set, p.Value));
            var delete = Schemas.Deleted(row);
            if (before != null && !delete && fields.Count == 0 || before is null && delete) continue;
            result.Add(new() { Resource=kind, Kind=delete ? ChangeKind.Delete : before is null ? ChangeKind.Create : ChangeKind.Update, Before=before?.Copy(), After=row.Copy(), Fields=fields });
        }
        return result;
    }
}
