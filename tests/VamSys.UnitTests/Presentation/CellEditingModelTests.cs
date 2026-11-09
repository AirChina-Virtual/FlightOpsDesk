using VamSys.Core;
using VamSys.Presentation;

namespace VamSys.UnitTests.Presentation;

// What the table's cells know beyond their value: the problems the row checks find, the server's value of a changed
// cell, the values they offer while typing, and how an edit session becomes one undo step.
public class CellEditingModelTests
{
    sealed class Host : IDataGridHost
    {
        public bool CanEdit => true;
        public Workspace Workspace { get; } = new();
        public ResourceKind Resource { get; set; } = ResourceKind.Routes;
        public ResourceData Data => Workspace.Resources[Resource];
        public Dictionary<Guid, DataRow> BaselineRows = [];
        public IReadOnlyDictionary<Guid, DataRow> Baseline => BaselineRows;
        public IReadOnlySet<Guid> Conflicts { get; } = new HashSet<Guid>();
        public IReadOnlyDictionary<string, FleetOption> FleetLabels { get; } = new Dictionary<string, FleetOption>();
        public object? CellTemplates => null;
        public double CellHeight(bool compact) => 32;
        public void CellEdited(Workspace owner, ResourceKind kind, ResourceData data, Guid rowId, int changedDelta) { }
        public Task ChooseFleets(DataRow row) => Task.CompletedTask;
        public Task ChooseParameter(DataRow row) => Task.CompletedTask;
        public readonly List<string> Pasted = [];
        public Task PasteCells(RowViewModel row, string field, string text) { Pasted.Add(field + ":" + text); return Task.CompletedTask; }
        public Task FillDown(RowViewModel row, string field) => Task.CompletedTask;
        public readonly List<Guid> Details = [];
        public void OpenDetails(Guid rowId) => Details.Add(rowId);
    }

    static readonly List<string> Columns = ["_delete", "ID", "Departure Airport (ICAO/IATA)", "Type", "Departure Time (HH:MM)", "Tags", "Is Hidden"];

    static DataRow Route(string departure, string type = "scheduled", string tags = "") => new()
    {
        Fields = new Dictionary<string, string>
        {
            ["ID"] = "", ["Departure Airport (ICAO/IATA)"] = departure, ["Arrival Airport (ICAO/IATA)"] = "ZSSS", ["Type"] = type, ["Callsign"] = "ABCD", ["Flight Number"] = "1234",
            ["Fleet IDs"] = "", ["Departure Time (HH:MM)"] = "08:00", ["Tags"] = tags, ["Is Hidden"] = "FALSE", ["_delete"] = "FALSE"
        }
    };

    static (Host Host, DataGridViewModel Grid, CellEditCommitter Edits) Shown(params DataRow[] routes)
    {
        var host = new Host();
        foreach (var code in new[] { "ZBAA", "ZSSS", "ZGGG" })
            host.Workspace.Resources[ResourceKind.Airports].Draft.Add(new DataRow { Fields = new Dictionary<string, string> { ["ICAO/IATA"] = code, ["Name"] = code == "ZBAA" ? "Beijing Capital" : "Other", ["_delete"] = "FALSE" } });
        host.Data.Draft.AddRange(routes);
        var edits = new CellEditCommitter();
        var language = new LocalizationService();
        language.SetLanguage("en-US");
        var grid = new DataGridViewModel(host, edits, language);
        grid.Bind(Columns, "comfortable", host.Workspace, host.Data, host.Resource);
        return (host, grid, edits);
    }
    static CellViewModel Cell(RowViewModel row, string field) => row.Cells.Single(c => c.Field == field);

    [Fact]
    public void A_cell_shows_the_problem_the_row_checks_find_and_loses_it_once_corrected()
    {
        var route = Route("XXXX", type: "airliner");
        var (host, grid, _) = Shown(route);
        var view = grid.Fill([route.LocalId], host.Data, 1)[0];

        var departure = Cell(view, "Departure Airport (ICAO/IATA)");
        Assert.Equal((true, 2), (departure.HasIssue, departure.Look));
        Assert.Contains("XXXX", departure.Hint, StringComparison.Ordinal);
        Assert.True(Cell(view, "Type").HasIssue);
        Assert.False(Cell(view, "Departure Time (HH:MM)").HasIssue);

        departure.Value = "ZBAA";
        Assert.False(departure.HasIssue);
        Assert.Null(departure.Hint);
        Cell(view, "Departure Time (HH:MM)").Value = "25:99";
        Assert.True(Cell(view, "Departure Time (HH:MM)").HasIssue);
    }

    [Fact]
    public void A_changed_cell_of_a_stored_record_tells_the_value_on_the_server()
    {
        var route = Route("ZBAA");
        var (host, grid, _) = Shown(route);
        host.Data.Snapshot.Add(route);
        host.BaselineRows[route.LocalId] = route;
        var view = grid.Fill([route.LocalId], host.Data, 1)[0];
        var time = Cell(view, "Departure Time (HH:MM)");
        Assert.Equal((false, 0), (time.IsChanged, time.Look));

        time.Value = "09:15";
        Assert.Equal((true, 1), (time.IsChanged, time.Look));
        Assert.Contains("08:00", time.Hint, StringComparison.Ordinal);
        time.Value = "08:00";
        Assert.False(time.IsChanged);
    }

    [Fact]
    public void Each_edit_session_is_one_undo_step_and_one_taken_back_with_Escape_leaves_none()
    {
        var route = Route("ZBAA");
        var (host, grid, _) = Shown(route);
        var view = grid.Fill([route.LocalId], host.Data, 1)[0];
        var time = Cell(view, "Departure Time (HH:MM)");

        time.Value = "0";
        time.Value = "09";
        time.EditEnded!(false);
        time.Value = "09:3";
        time.EditEnded!(false);
        Assert.Equal(2, host.Data.Undo.Count);

        // Typed and taken back: the cell returns to its value and the empty step goes.
        time.Value = "1";
        time.Value = "09:3";
        time.EditEnded!(true);
        Assert.Equal(2, host.Data.Undo.Count);
        Assert.Equal("09:3", host.Data.FindDraftRow(route.LocalId)!.Get("Departure Time (HH:MM)"));
    }

    [Fact]
    public void Cells_offer_the_values_of_their_column()
    {
        var route = Route("ZBAA", tags: "night,cargo");
        var (host, grid, _) = Shown(route, Route("ZSSS", tags: "charter"));
        var view = grid.Fill([route.LocalId], host.Data, 1)[0];

        Assert.Equal(Schemas.RouteTypeOrder, Cell(view, "Type").Choices);
        Assert.Equal(Schemas.RouteTypeOrder, Cell(view, "Type").Suggest!("scheduled").Select(s => s.Value));
        Assert.Equal(["cargo", "charter"],Cell(view, "Type").Suggest!("ar").Select(s => s.Value));

        var airport = Cell(view, "Departure Airport (ICAO/IATA)").Suggest!;
        Assert.Equal(["ZBAA"], airport("zb").Select(s => s.Value));
        Assert.Equal("ZBAA  ·  Beijing Capital", airport("beij").Single().ToString());

        // Tags complete the last entry and leave out the ones already there.
        var tags = Cell(view, "Tags").Suggest!("night,c");
        Assert.Equal(["night,cargo", "night,charter"], tags.Select(s => s.Value));
        Assert.Equal(["cargo", "charter"], tags.Select(s => s.ToString()));

        Assert.Null(Cell(view, "Departure Time (HH:MM)").Suggest);
        Assert.Equal(FieldType.Bool, Cell(view, "Is Hidden").Type);
        Assert.Equal("☐ FALSE", Cell(view, "Is Hidden").VisibleText);
    }

    [Fact]
    public void The_details_pane_edits_the_same_draft_row_as_the_table()
    {
        var route = Route("ZBAA");
        var (host, grid, _) = Shown(route);
        var table = grid.Fill([route.LocalId], host.Data, 1)[0];
        var details = grid.Detached(route.LocalId, Columns)!;
        Assert.NotSame(table, details);

        Cell(details, "Departure Time (HH:MM)").Value = "10:00";
        Assert.Equal("10:00", host.Data.FindDraftRow(route.LocalId)!.Get("Departure Time (HH:MM)"));
        table.Rebind(host.Data.FindDraftRow(route.LocalId)!);
        Assert.Equal("10:00", Cell(table, "Departure Time (HH:MM)").Value);

        Cell(table, "Type").OpenDetails!();
        Assert.Equal([route.LocalId], host.Details);
        Assert.Null(grid.Detached(Guid.NewGuid(), Columns));
    }
}
