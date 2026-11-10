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
    // Text from the clipboard pasted from this cell rightwards and downwards over the shown page.
    Task PasteCells(RowViewModel row, string field, string text);
    // This cell's value copied to the same column of the other selected records.
    Task FillDown(RowViewModel row, string field);
    // Shows the record in the details pane.
    void OpenDetails(Guid rowId);
    // 2.5: the fleets of a route that are not approved for its prefixes, when the airline parameters are known (online).
    IReadOnlyList<string> UnapprovedFleets(DataRow route) => [];
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
        ForgetLookups();
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

    // The checks and suggestions read the other resources, which can only change while this page is not shown: they are
    // built again for every filled page.
    ValidationPass? pass;
    List<CellSuggestion>? airports;
    List<string>? tags;
    readonly List<Issue> found = [];
    void ForgetLookups() { pass = null; airports = null; tags = null; }

    // Marks each cell with the first problem the single-row checks find in it, and with the server's value when the
    // record exists there and the cell differs from it.
    void Check(RowViewModel view)
    {
        if (link?.Owner is not { } owner || link.Data is null || !ReferenceEquals(owner, host.Workspace))
            return;
        pass ??= new ValidationPass(owner);
        found.Clear();
        var row = view.Row;
        host.Baseline.TryGetValue(row.LocalId, out var before);
        Schemas.CheckRow(owner, link.Kind, row, before, pass, found);
        var deleted = Schemas.Deleted(row);
        foreach (var cell in view.Cells)
        {
            if (cell.Field == "_delete")
                continue;
            MessageDescriptor? issue = null;
            foreach (var item in found)
                if (item.Field == cell.Field)
                { issue = item.Description; break; }
            if (issue is null && link.Kind == ResourceKind.Routes && cell.Field == "Fleet IDs" && !deleted
                && host.UnapprovedFleets(row) is { Count: > 0 } unapproved)
                issue = Messages.Define("CellFleetsNotApproved", ("fleets", string.Join(", ",
                    unapproved.Select(t => host.FleetLabels.TryGetValue(t, out var option) ? $"{language.Format(option.Name)} ({t})" : t))));
            var server = before != null && !deleted ? before.Get(cell.Field) : null;
            // The row, not the cell: an edit checks the row before the cell takes the new value.
            cell.ShowState(issue, server != null && server != row.Get(cell.Field) ? server : null);
        }
    }

    const int SuggestionCount = 12;
    IReadOnlyList<CellSuggestion> SuggestAirports(string text)
    {
        airports ??= host.Workspace.Resources[ResourceKind.Airports].Draft.Where(r => !Schemas.Deleted(r))
            .Select(r => new CellSuggestion(Schemas.Key(ResourceKind.Airports, r), r.Get("Name")))
            .Where(s => s.Value.Length > 0).DistinctBy(s => s.Value).OrderBy(s => s.Value, StringComparer.Ordinal).ToList();
        var typed = text.Trim();
        if (typed.Length == 0)
            return airports.Take(SuggestionCount).ToList();
        var starts = airports.Where(s => s.Value.StartsWith(typed, StringComparison.OrdinalIgnoreCase));
        var names = airports.Where(s => !s.Value.StartsWith(typed, StringComparison.OrdinalIgnoreCase) && s.Label.Contains(typed, StringComparison.OrdinalIgnoreCase));
        return starts.Concat(names).Take(SuggestionCount).ToList();
    }
    // Tags complete the last entry of the list; the ones already in the cell are not offered again.
    IReadOnlyList<CellSuggestion> SuggestTags(string field, string text)
    {
        tags ??= host.Data.Draft.SelectMany(r => r.Get(field).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var cut = text.LastIndexOf(',');
        var head = cut < 0 ? "" : text[..(cut + 1)];
        var last = text[(cut + 1)..].Trim();
        var present = head.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        // Lists are kept comma separated without spaces, as the server returns them.
        var prefix = head.Length == 0 ? "" : head.TrimEnd();
        return tags.Where(t => !present.Contains(t) && t.Contains(last, StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.StartsWith(last, StringComparison.OrdinalIgnoreCase) ? 0 : 1).Take(SuggestionCount)
            .Select(t => new CellSuggestion(prefix + t, "", t)).ToList();
    }
    IReadOnlyList<CellSuggestion> SuggestChoices(IReadOnlyList<string> choices, string text)
    {
        var typed = text.Trim();
        // The full list while the cell holds one of the choices, so another can be picked without clearing it.
        return choices.Contains(typed) || typed.Length == 0 ? choices.Select(c => new CellSuggestion(c, "")).ToList()
            : choices.Where(c => c.Contains(typed, StringComparison.OrdinalIgnoreCase)).Select(c => new CellSuggestion(c, "")).ToList();
    }
    static IReadOnlyList<string> ChoicesFor(string field) => field switch
    {
        "Type" => Schemas.RouteTypeOrder,
        "Type (pax/cargo/...)" => Schemas.FleetTypeOrder,
        _ => []
    };
    Func<string, IReadOnlyList<CellSuggestion>>? SuggestFor(string field)
    {
        var choices = ChoicesFor(field);
        if (choices.Count > 0)
            return text => SuggestChoices(choices, text);
        if (FieldSpecs.Column(field) is { Reference: ResourceKind.Airports })
            return SuggestAirports;
        if (FieldSpecs.Is(field, FieldType.TagList))
            return text => SuggestTags(field, text);
        return null;
    }

    // Drops the pooled models (the page is built again, for example after a density change).
    public void Reset()
    {
        ForgetLookups();
        link?.Detach();
        link = null;
        pool.Clear();
        columns = [];
        bound = null;
    }

    // The workspace was replaced: the pooled rows stay for the next bind, but no longer reach the old data.
    public void Release()
    {
        ForgetLookups();
        link?.Detach();
        bound = null;
    }

    // One record's cells outside the table (the record details pane), editing the same draft as the table's cells.
    public RowViewModel? Detached(Guid id, IReadOnlyList<string> fields)
        => link is null || host.Data.FindDraftRow(id) is not { } row ? null : Create(row, link, fields, compact: false);

    RowViewModel Create(DataRow row, RowLink rowLink) => Create(row, rowLink, columns, density == "compact");
    RowViewModel Create(DataRow row, RowLink rowLink, IReadOnlyList<string> fields, bool compact)
    {
        RowViewModel? view = null;
        bool Editable() => host.CanEdit && ReferenceEquals(rowLink.Owner, host.Workspace) && rowLink.Kind == host.Resource
            && view != null && ReferenceEquals(rowLink.Data, host.Data) && ReferenceEquals(host.Data.FindDraftRow(view.Row.LocalId), view.Row)
            && !Schemas.Deleted(view.Row);
        var cells = fields.Select(field => new CellViewModel(field, row.Get(field), value =>
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
            Choices = ChoicesFor(field),
            Suggest = SuggestFor(field),
            Required = FieldSpecs.Get(rowLink.Kind).Required.Contains(field),
            Format = language.Format,
            DayName = day => Enum.TryParse<DayOfWeek>(day, true, out var weekday) ? language.DisplayCulture.DateTimeFormat.GetDayName(weekday) : day,
            Label = key => language.Get(key),
            OriginalLabel = value => language.Get("CellServerValue", ("value", value.Length == 0 ? language.Get("CellEmptyValue") : value)),
            ReadOnlyReason = () => view is null ? "" : Schemas.Deleted(view.Row) ? language.Get("CellReadOnlyDeleted") : language.Get("CellReadOnlyServer"),
            EditEnded = reverted =>
            {
                edits.ResetContinuation();
                if (reverted)
                    rowLink.Data?.History.DiscardEmptyStep();
            },
            Paste = text => view is null ? Task.CompletedTask : host.PasteCells(view, field, text),
            FillDown = () => view is null ? Task.CompletedTask : host.FillDown(view, field),
            OpenDetails = () => { if (view != null) host.OpenDetails(view.Row.LocalId); },
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
        view.Check = Check;
        Check(view);
        return view;
    }
}
