using VamSys.Core;
using VamSys.UnitTests.Application;

namespace VamSys.UnitTests.Domain;

// The data table's column order (FieldSpecs.TableOrder): status and key first, the known columns in the agreed order,
// then the columns the table does not know in the order the data brought them.
public class TableOrderTests
{
    const string Dep = "Departure Airport (ICAO/IATA)", Arr = "Arrival Airport (ICAO/IATA)";

    public static TheoryData<ResourceKind, string[]> Orders => new()
    {
        { ResourceKind.Airports, ["Name", "Category", "Container IDs", "Pax LF ID", "Cargo LF ID"] },
        { ResourceKind.Fleets, ["Name", "Type Code", "Type (pax/cargo/...)", "Max Passengers", "Max Freight", "Container Units", "Hide in Phoenix", "Allowed Prefix IDs", "Parameter IDs"] },
        { ResourceKind.Aircraft, ["Registration", "Name", "Fleet ID", "Passengers", "Freight", "SELCAL", "Hex Code", "Fin Number", "Internal Remarks"] },
        { ResourceKind.Routings, [Dep, Arr, "Route String", "Days of Operation", "Tags", "Remarks", "Internal Remarks"] },
        { ResourceKind.Routes, ["Callsign", "Flight Number", Dep, Arr, "Type", "Departure Time (HH:MM)", "Arrival Time (HH:MM)", "Service Days", "Fleet IDs",
            "Start Date", "End Date", "Routing", "Is Hidden", "Tags", "Altitude", "Cost Index", "Remarks", "Internal Remarks"] }
    };

    [Theory]
    [MemberData(nameof(Orders))]
    public void Known_columns_follow_the_agreed_order_after_the_status_and_key(ResourceKind kind, string[] expected)
    {
        var key = FieldSpecs.Get(kind).Key;
        // The display order names every column of the field table but the key, once.
        Assert.Equal(expected, FieldSpecs.DisplayOrder[kind]);
        Assert.Equal(FieldSpecs.Get(kind).Fields.Select(f => f.Csv).Where(c => c != key).Order(StringComparer.Ordinal), expected.Order(StringComparer.Ordinal));
        // Whatever order the data brought them in (here reversed, the status last as in the schema columns).
        var source = expected.Reverse().Prepend(key).Append("_delete");
        Assert.Equal(new[] { "_delete", key }.Concat(expected), FieldSpecs.TableOrder(kind, source));
        Assert.Equal(new[] { "_delete", key }.Concat(expected), FieldSpecs.TableOrder(kind, Schemas.All[kind].Columns));
    }

    [Fact]
    public void Unknown_columns_go_last_in_their_own_order_and_missing_known_ones_are_not_added()
    {
        var columns = new[] { "Zeta", "Remarks", "ID", "Fleet IDs", "Alpha", "Callsign", "_delete", "Middle", "Alpha" };
        Assert.Equal(["_delete", "ID", "Callsign", "Fleet IDs", "Remarks", "Zeta", "Alpha", "Middle"], FieldSpecs.TableOrder(ResourceKind.Routes, columns));
        // Without status or key in the input, none is invented.
        Assert.Equal(["Name", "Custom"], FieldSpecs.TableOrder(ResourceKind.Airports, ["Custom", "Name"]));
    }

    [Fact]
    public void The_table_always_has_the_status_key_and_fleet_columns_in_their_place()
    {
        // A route CSV without Fleet IDs still shows the fleet column, in its place after Service Days, not third.
        Assert.Equal(["_delete", "ID", "Callsign", "Service Days", "Fleet IDs", "Remarks", "Custom"],
            FieldSpecs.TableColumns(ResourceKind.Routes, ["Custom", "Remarks", "Service Days", "Callsign"]));
        Assert.Equal(["_delete", "ID", "Registration", "Fleet ID", "Custom"], FieldSpecs.TableColumns(ResourceKind.Aircraft, ["Custom", "Registration"]));
        // Other resources get no fleet column; the fleet column is not repeated when the data has it.
        Assert.Equal(["_delete", "ID", "Name"], FieldSpecs.TableColumns(ResourceKind.Fleets, ["Name"]));
        Assert.Equal(["_delete", "ID", "Callsign", "Fleet IDs"], FieldSpecs.TableColumns(ResourceKind.Routes, ["Fleet IDs", "Callsign", "ID"]));
        // Data without columns yet shows the schema's columns.
        Assert.Equal(new[] { "_delete", "ID" }.Concat(FieldSpecs.DisplayOrder[ResourceKind.Routes]), FieldSpecs.TableColumns(ResourceKind.Routes, []));
    }

    [Fact]
    public void Hidden_columns_still_apply_to_the_table_order()
    {
        var storage = new FakeStorage();
        var preference = new HiddenColumnsPreference(storage);
        var all = FieldSpecs.TableOrder(ResourceKind.Aircraft, ["_delete", "ID", "Name", "Fleet ID", "Custom", "Registration", "SELCAL"]);
        Assert.Equal(["_delete", "ID", "Registration", "Name", "Fleet ID", "SELCAL", "Custom"], all);
        preference.Show(ResourceKind.Aircraft, "Name", false, all);
        preference.Show(ResourceKind.Aircraft, "Custom", false, all);
        preference.Show(ResourceKind.Aircraft, "ID", false, all);
        Assert.Equal(["_delete", "ID", "Registration", "Fleet ID", "SELCAL"], preference.Shown(ResourceKind.Aircraft, all));
    }
}
