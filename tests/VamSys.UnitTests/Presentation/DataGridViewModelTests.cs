using VamSys.Core;
using VamSys.Presentation;

namespace VamSys.UnitTests.Presentation;

public class DataGridViewModelTests
{
    sealed class Host : IDataGridHost
    {
        public bool CanEdit { get; set; } = true;
        public Workspace Workspace { get; set; } = new();
        public ResourceKind Resource { get; set; } = ResourceKind.Routes;
        public ResourceData Data => Workspace.Resources[Resource];
        public Dictionary<Guid, DataRow> BaselineRows = [];
        public IReadOnlyDictionary<Guid, DataRow> Baseline => BaselineRows;
        public IReadOnlySet<Guid> Conflicts { get; set; } = new HashSet<Guid>();
        public IReadOnlyDictionary<string, FleetOption> FleetLabels { get; set; } = new Dictionary<string, FleetOption>();
        public object? CellTemplates { get; set; } = "templates";
        public double CellHeight(bool compact) => compact ? 24 : 32;
        public readonly List<(Workspace Owner, ResourceKind Kind, ResourceData Data, Guid Row, int Delta)> Edited = [];
        public void CellEdited(Workspace owner, ResourceKind kind, ResourceData data, Guid rowId, int changedDelta) => Edited.Add((owner, kind, data, rowId, changedDelta));
        public readonly List<DataRow> FleetChoices = [], ParameterChoices = [];
        public Task ChooseFleets(DataRow row) { FleetChoices.Add(row); return Task.CompletedTask; }
        public Task ChooseParameter(DataRow row) { ParameterChoices.Add(row); return Task.CompletedTask; }
        public Task PasteCells(RowViewModel row, string field, string text) => Task.CompletedTask;
        public Task FillDown(RowViewModel row, string field) => Task.CompletedTask;
        public void OpenDetails(Guid rowId) { }
        public Func<DataRow, IReadOnlyList<string>> Unapproved { get; set; } = _ => [];
        public IReadOnlyList<string> UnapprovedFleets(DataRow route) => Unapproved(route);
    }

    static readonly List<string> Columns = ["_delete", "Name", "Fleet IDs"];

    static DataRow Row(string name) => new() { Fields = new Dictionary<string, string> { ["Name"] = name, ["Fleet IDs"] = "" } };

    // A workspace whose routes are stored rows, with the host's baseline set to their snapshot.
    static (Host Host, DataGridViewModel Grid, List<DataRow> Rows, CellEditCommitter Edits) Shown(int count = 3)
    {
        var host = new Host();
        var rows = Enumerable.Range(0, count).Select(n => Row("r" + n)).ToList();
        host.Data.Snapshot.AddRange(rows);
        host.Data.Draft.AddRange(rows);
        host.BaselineRows = rows.ToDictionary(r => r.LocalId);
        var edits = new CellEditCommitter();
        var language = new LocalizationService();
        language.SetLanguage("en-US");
        var grid = new DataGridViewModel(host, edits, language);
        grid.Bind(Columns, "comfortable", host.Workspace, host.Data, host.Resource);
        return (host, grid, rows, edits);
    }

    static CellViewModel Cell(RowViewModel row, string field) => row.Cells.Single(c => c.Field == field);

    // 2.5: the Fleet IDs cell of a route names the fleets that are not approved for its prefixes.
    [Fact]
    public void A_route_cell_naming_unapproved_fleets_shows_them_as_its_problem()
    {
        var (host, grid, rows, _) = Shown(2);
        foreach (var id in new[] { "4", "5" })
            host.Workspace.Resources[ResourceKind.Fleets].Draft.Add(new() { Fields = new Dictionary<string, string> { ["ID"] = id, ["Name"] = "Fleet " + id, ["_delete"] = "FALSE" } });
        host.FleetLabels = new Dictionary<string, FleetOption> { ["5"] = new("5", "Fleet 5", "B738", "5", true, null) };
        for (var n = 0; n < 2; n++)
        {
            var edited = rows[n].With("Fleet IDs", "4,5").With("Type", "charter");
            host.Data.Draft[n] = edited;
            rows[n] = edited;
        }
        host.Unapproved = route => route.LocalId == rows[0].LocalId ? ["5"] : [];

        var shown = grid.Fill(rows.Select(r => r.LocalId), host.Data, 2);

        var refused = Cell(shown[0], "Fleet IDs");
        Assert.True(refused.HasIssue);
        Assert.Contains("Fleet 5 (5)", refused.Hint, StringComparison.Ordinal);
        Assert.False(Cell(shown[1], "Fleet IDs").HasIssue);
        Assert.False(Cell(shown[0], "Name").HasIssue);
    }

    [Fact]
    public void Rows_are_built_once_and_rebound_in_place_for_another_page_or_another_resource_with_the_same_columns()
    {
        var (host, grid, rows, _) = Shown();
        var first = grid.Fill(rows.Take(2).Select(r => r.LocalId), host.Data, 2);
        Assert.Equal(["r0", "r1"], first.Select(r => Cell(r, "Name").Value));
        Assert.Equal("templates", first[0].CellTemplates);

        var second = grid.Fill([rows[2].LocalId], host.Data, 2);
        Assert.Same(first[0], second[0]);
        Assert.Equal("r2", Cell(second[0], "Name").Value);
        Assert.Same(rows[2], second[0].Row);

        var other = new ResourceData();
        var aircraft = Row("a0");
        other.Draft.Add(aircraft);
        host.Workspace.Resources[ResourceKind.Aircraft] = other;
        host.Resource = ResourceKind.Aircraft;
        Assert.True(grid.Bind(Columns, "comfortable", host.Workspace, other, ResourceKind.Aircraft));
        Assert.False(grid.Bind(Columns, "comfortable", host.Workspace, other, ResourceKind.Aircraft));
        Assert.Same(first[0], grid.Fill([aircraft.LocalId], other, 2)[0]);
        Assert.Same(other, grid.Link!.Data);
    }

    [Fact]
    public void Other_columns_or_density_drop_the_pool_and_detach_the_old_link()
    {
        var (host, grid, rows, _) = Shown();
        var before = grid.Fill(rows.Select(r => r.LocalId), host.Data, 3);
        var oldLink = grid.Link!;

        Assert.True(grid.Bind(Columns, "compact", host.Workspace, host.Data, host.Resource));
        Assert.Null(oldLink.Owner);
        Assert.Null(oldLink.Data);
        Assert.Same(host.Data, grid.Link!.Data);
        var compact = grid.Fill(rows.Select(r => r.LocalId), host.Data, 3);
        Assert.NotSame(before[0], compact[0]);
        Assert.True(Cell(compact[0], "Name").Compact);
        Assert.Equal(24, Cell(compact[0], "Name").Height);

        grid.Bind(["_delete", "Name"], "compact", host.Workspace, host.Data, host.Resource);
        var narrower = grid.Fill(rows.Select(r => r.LocalId), host.Data, 3);
        Assert.Equal(["_delete", "Name"], narrower[0].Cells.Select(c => c.Field));
        // The rebuilt link is bound even though the data did not change, so its cells stay editable.
        Cell(narrower[0], "Name").Value = "edited";
        Assert.Equal("edited", host.Data.FindDraftRow(rows[0].LocalId)!.Get("Name"));
    }

    [Fact]
    public void Missing_ids_are_skipped_and_the_pool_keeps_one_full_page_beyond_a_short_one()
    {
        var (host, grid, rows, _) = Shown(5);
        Assert.Equal(5, grid.Fill(rows.Select(r => r.LocalId), host.Data, 5).Count);
        var shown = grid.Fill([rows[0].LocalId, Guid.NewGuid(), rows[1].LocalId], host.Data, 3);
        Assert.Equal([rows[0], rows[1]], shown.Select(r => r.Row));
        Assert.Equal(3, grid.Pool.Count);
        Assert.Empty(grid.Fill([], host.Data, 2));
        Assert.Equal(2, grid.Pool.Count);
    }

    [Fact]
    public void An_edit_replaces_the_row_refreshes_its_status_and_tells_the_host_once()
    {
        var (host, grid, rows, _) = Shown();
        var view = grid.Fill([rows[0].LocalId], host.Data, 1)[0];
        var status = Cell(view, "_delete");
        status.RefreshLanguage();
        var unchanged = status.DisplayValue;
        var notified = new List<string?>();
        status.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        Cell(view, "Name").Value = "new";
        Assert.Contains(nameof(CellViewModel.DisplayValue), notified);

        var replacement = host.Data.FindDraftRow(rows[0].LocalId)!;
        Assert.Same(replacement, view.Row);
        Assert.Equal("new", replacement.Get("Name"));
        Assert.NotEqual(unchanged, status.DisplayValue);
        var edited = Assert.Single(host.Edited);
        Assert.Same(host.Workspace, edited.Owner);
        Assert.Same(host.Data, edited.Data);
        Assert.Equal((ResourceKind.Routes, rows[0].LocalId, 1), (edited.Kind, edited.Row, edited.Delta));
    }

    [Fact]
    public void Cells_refuse_edits_while_locked_for_another_workspace_for_a_stale_or_deleted_row_and_after_release()
    {
        var (host, grid, rows, _) = Shown();
        var views = grid.Fill(rows.Select(r => r.LocalId), host.Data, 3);
        void Try(int n) => Cell(views[n], "Name").Value = "x" + n;

        host.CanEdit = false;
        Try(0);
        host.CanEdit = true;

        // Another workspace instance shown over the same data (the owner alone tells them apart).
        var shown = host.Workspace;
        host.Workspace = new();
        host.Workspace.Resources[ResourceKind.Routes] = shown.Resources[ResourceKind.Routes];
        Try(0);
        host.Workspace = shown;

        // Replaced elsewhere (an undo, a bulk edit) and not yet rebound.
        host.Data.ReplaceDraftRows([rows[1].With("Name", "elsewhere")]);
        Try(1);

        var deleted = rows[2].With("_delete", "true");
        host.Data.ReplaceDraftRows([deleted]);
        views[2].Rebind(deleted);
        Assert.True(Cell(views[2], "Name").IsReadOnly);
        Try(2);

        Assert.Empty(host.Edited);
        Assert.Equal("r0", host.Data.FindDraftRow(rows[0].LocalId)!.Get("Name"));

        grid.Release();
        Assert.Null(grid.Link!.Owner);
        Try(0);
        Assert.Empty(host.Edited);
        // The next bind reaches the data again with the same rows.
        Assert.True(grid.Bind(Columns, "comfortable", host.Workspace, host.Data, host.Resource));
        Assert.Same(views[0], grid.Fill([rows[0].LocalId], host.Data, 3)[0]);
        Try(0);
        Assert.Single(host.Edited);
    }

    [Fact]
    public void Filling_a_page_ends_the_continued_undo_step()
    {
        var (host, grid, rows, edits) = Shown();
        var view = grid.Fill([rows[0].LocalId], host.Data, 1)[0];
        Cell(view, "Name").Value = "n";
        Assert.NotNull(edits.Continuing);
        grid.Fill([rows[0].LocalId], host.Data, 1);
        Assert.Null(edits.Continuing);
    }

    [Fact]
    public void The_fleet_command_chooses_for_the_row_shown_now()
    {
        var (host, grid, rows, _) = Shown();
        var view = grid.Fill([rows[0].LocalId], host.Data, 1)[0];
        Assert.Null(Cell(view, "Name").ChooseFleet);
        grid.Fill([rows[1].LocalId], host.Data, 1);
        Cell(view, "Fleet IDs").ChooseFleet!.Execute(null);
        Assert.Equal([rows[1]], host.FleetChoices);
    }

    [Fact]
    public void Reset_drops_the_pool_and_detaches_its_link()
    {
        var (host, grid, rows, _) = Shown();
        var before = grid.Fill(rows.Select(r => r.LocalId), host.Data, 3);
        var oldLink = grid.Link!;
        grid.Reset();
        Assert.Empty(grid.Pool);
        Assert.Null(grid.Link);
        Assert.Null(oldLink.Data);
        Assert.Throws<InvalidOperationException>(() => grid.Fill([], host.Data, 1));
        Assert.True(grid.Bind(Columns, "comfortable", host.Workspace, host.Data, host.Resource));
        Assert.NotSame(before[0], grid.Fill([rows[0].LocalId], host.Data, 3)[0]);
    }
}
