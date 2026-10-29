using CommunityToolkit.Mvvm.Input;
using VamSys.Core;

namespace VamSys.Presentation;

// What the data table's row and cell models read from the window that shows them. Every member is read when used, so a
// pooled cell always acts on what is shown now.
public interface IDataGridHost
{
    // Editing is allowed now (not locked, and not while the window restores the selection).
    bool CanEdit { get; }
    Workspace Workspace { get; }
    ResourceKind Resource { get; }
    ResourceData Data { get; }
    IReadOnlyDictionary<Guid, DataRow> Baseline { get; }
    IReadOnlySet<Guid> Conflicts { get; }
    IReadOnlyDictionary<string, FleetOption> FleetLabels { get; }
    object? CellTemplates { get; }
    double CellHeight(bool compact);
    // A cell edit was committed to the row; changedDelta is how the resource's count of changed rows moved.
    void CellEdited(Workspace owner, ResourceKind kind, ResourceData data, Guid rowId, int changedDelta);
    Task ChooseFleets(DataRow row);
    Task ChooseParameter(DataRow row);
}

// The shown page's row and cell models. Only the current page has models; they are kept in a pool and rebound in place
// across pages, workspaces and resources with the same columns and density, because new row objects make WinUI create
// every row's controls again and keep native memory for the discarded ones.
public sealed class DataGridViewModel(IDataGridHost host, CellEditCommitter edits, ILocalizationService language)
{
    // What the pooled rows' edit closures act on. Released rows are finalized late (their x:Bind event sources sit in
    // the finalizer queue), so the closures reach the workspace and data only through this, and it is detached when
    // the pool is dropped or released.
    public sealed class RowLink
    {
        public Workspace? Owner { get; internal set; }
        public ResourceData? Data { get; internal set; }
        public ResourceKind Kind { get; internal set; }
        internal void Detach() { Owner = null; Data = null; }
    }

    readonly List<RowViewModel> pool = [];
    List<string> columns = [];
    string density = "";
    RowLink? link;
    ResourceData? bound;

    public IReadOnlyList<RowViewModel> Pool => pool;
    public RowLink? Link => link;

    // Prepares the pool for these columns and density and binds it to the data. Returns true when the data changed,
    // so the window refreshes what depends on it (the headers and sort columns).
    public bool Bind(List<string> shownColumns, string shownDensity, Workspace owner, ResourceData data, ResourceKind kind)
    {
        if (!columns.SequenceEqual(shownColumns) || density != shownDensity || link is null)
        {
            link?.Detach();
            pool.Clear();
            columns = shownColumns;
            density = shownDensity;
            link = new();
            bound = null;
        }
        if (ReferenceEquals(bound, data))
            return false;
        bound = data;
        link.Owner = owner;
        link.Data = data;
        link.Kind = kind;
        return true;
    }

    // The page's row models, in order, reusing pooled rows first. Ids no longer in the draft are skipped. The pool keeps
    // up to one full page beyond a short page.
    public List<RowViewModel> Fill(IEnumerable<Guid> ids, ResourceData source, int pageSize)
    {
        if (link is null)
            throw new InvalidOperationException("Bind the grid before filling it.");
        var rows = new List<RowViewModel>(pageSize);
        foreach (var id in ids)
        {
            var row = source.FindDraftRow(id);
            if (row is null)
                continue;
            if (rows.Count < pool.Count)
            {
                var reused = pool[rows.Count];
                reused.Rebind(row);
                rows.Add(reused);
                continue;
            }
            var view = Create(row, link);
            rows.Add(view);
            pool.Add(view);
        }
        var keep = Math.Max(rows.Count, pageSize);
        if (pool.Count > keep)
            pool.RemoveRange(keep, pool.Count - keep);
        edits.ResetContinuation();
        return rows;
    }

    // Drops the pooled models (the page is built again, for example after a density change).
    public void Reset()
    {
        link?.Detach();
        link = null;
        pool.Clear();
        columns = [];
        bound = null;
    }

    // The workspace was replaced: the pooled rows stay for the next bind, but no longer reach the old data.
    public void Release()
    {
        link?.Detach();
        bound = null;
    }

    RowViewModel Create(DataRow row, RowLink rowLink)
    {
        RowViewModel? view = null;
        var compact = density == "compact";
        bool Editable() => host.CanEdit && ReferenceEquals(rowLink.Owner, host.Workspace) && rowLink.Kind == host.Resource
            && view != null && ReferenceEquals(rowLink.Data, host.Data) && ReferenceEquals(host.Data.FindDraftRow(view.Row.LocalId), view.Row)
            && !Schemas.Deleted(view.Row);
        var cells = columns.Select(field => new CellViewModel(field, row.Get(field), value =>
        {
            if (!Editable() || field == "_delete")
                return;
            var owner = rowLink.Owner!;
            var data = rowLink.Data!;
            var kind = rowLink.Kind;
            var id = view!.Row.LocalId;
            var (replacement, changedDelta) = edits.Commit(data, view.Row, field, value, host.Baseline);
            // Rows are replaced, never edited in place: the view follows the replacement.
            view.ReplaceRow(replacement);
            view.Cells.FirstOrDefault(c => c.Field == "_delete")?.RefreshLanguage();
            host.CellEdited(owner, kind, data, id, changedDelta);
        })
        {
            CanEdit = Editable,
            IsReadOnly = DataGridRules.CellReadOnly(row, field, host.Workspace.Mode),
            ReadOnlyForRow = r => DataGridRules.CellReadOnly(r, field, host.Workspace.Mode),
            FleetName = () => field == "Parameter IDs" ? language.Get("AirlineParameter") : language.Get("ChooseFleet"),
            ShowServerNull = () => view?.Row.Identity != null && view.Row.Get("Type") == "jumpseat" && (field is "Callsign" or "Flight Number"),
            ServerNullLabel = () => language.Get("JumpseatNotPreserved"),
            Compact = compact,
            Height = host.CellHeight(compact),
            DisplayText = () => field == "_delete" ? DataGridRules.RowStatus(view!.Row, host.Baseline, host.Conflicts, language)
                : field == "Parameter IDs" ? view!.Row.Get(field)
                : field is "Fleet ID" or "Fleet IDs" ? DataGridRules.FleetLabel(view!.Row.Get(field), host.FleetLabels, language) : "",
            ChooseFleet = field == "Parameter IDs" ? new AsyncRelayCommand(() => host.ChooseParameter(view!.Row))
                : field is "Fleet ID" or "Fleet IDs" ? new AsyncRelayCommand(() => host.ChooseFleets(view!.Row)) : null
        }).ToList();
        view = new(row, cells) { CellTemplates = host.CellTemplates };
        return view;
    }
}
