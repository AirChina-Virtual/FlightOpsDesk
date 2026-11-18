using Microsoft.Data.Sqlite;
using VamSys.Core;
using VamSys.Infrastructure;

namespace VamSys.UnitTests.Application;

// The columns hidden from the view menu are kept per resource across runs, in app_settings like the density.
public class HiddenColumnsTests
{
    static readonly IReadOnlyList<string> RouteColumns = ["_delete", "ID", "Fleet IDs", "Callsign", "Flight Number", "Remarks"];

    [Fact]
    public void Hidden_columns_are_saved_per_resource_and_come_back_in_a_new_run()
    {
        var storage = new FakeStorage();
        var first = new HiddenColumnsPreference(storage);
        first.Show(ResourceKind.Routes, "Remarks", false, RouteColumns);
        first.Show(ResourceKind.Routes, "Callsign", false, RouteColumns);
        first.Show(ResourceKind.Routes, "Callsign", true, RouteColumns);
        first.Show(ResourceKind.Airports, "Name", false, ["_delete", "ICAO", "Name"]);

        var next = new HiddenColumnsPreference(storage);
        Assert.Equal(["_delete", "ID", "Fleet IDs", "Callsign", "Flight Number"], next.Shown(ResourceKind.Routes, RouteColumns));
        Assert.Equal(["_delete", "ICAO"], next.Shown(ResourceKind.Airports, ["_delete", "ICAO", "Name"]));
        Assert.Empty(next.Hidden(ResourceKind.Fleets, ["_delete", "ID", "Name"]));
    }

    [Fact]
    public void The_status_and_key_columns_cannot_be_hidden_even_when_stored()
    {
        var storage = new FakeStorage();
        var preference = new HiddenColumnsPreference(storage);
        preference.Show(ResourceKind.Routes, "ID", false, RouteColumns);
        preference.Show(ResourceKind.Routes, "_delete", false, RouteColumns);
        Assert.Empty(storage.LoadHiddenColumns(ResourceKind.Routes));

        // A stored list that names them (written by hand or by an older build) does not hide them either.
        storage.SaveHiddenColumns(ResourceKind.Routes, ["_delete", "ID", "Remarks"]);
        var next = new HiddenColumnsPreference(storage);
        Assert.Equal(["_delete", "ID", "Fleet IDs", "Callsign", "Flight Number"], next.Shown(ResourceKind.Routes, RouteColumns));
        Assert.Equal(["Remarks"], next.Hidden(ResourceKind.Routes, RouteColumns).Order());
    }

    [Fact]
    public void Names_the_table_does_not_have_are_ignored_but_kept_for_tables_that_have_them()
    {
        var storage = new FakeStorage();
        storage.SaveHiddenColumns(ResourceKind.Routes, ["Retired Field", "Imported Note", "Remarks"]);
        var preference = new HiddenColumnsPreference(storage);

        Assert.Equal(["_delete", "ID", "Fleet IDs", "Callsign", "Flight Number"], preference.Shown(ResourceKind.Routes, RouteColumns));
        Assert.Equal(["Remarks"], preference.Hidden(ResourceKind.Routes, RouteColumns));

        // Showing everything this table has leaves the names it does not have: another workspace's imported column stays hidden there.
        preference.ShowAll(ResourceKind.Routes, RouteColumns);
        Assert.Equal(RouteColumns, preference.Shown(ResourceKind.Routes, RouteColumns));
        Assert.Equal(["Imported Note", "Retired Field"], storage.LoadHiddenColumns(ResourceKind.Routes).Order());
        Assert.Equal(["_delete", "ID", "Callsign"], preference.Shown(ResourceKind.Routes, ["_delete", "ID", "Imported Note", "Callsign"]));
    }

    [Fact]
    public void A_failed_save_leaves_the_shown_columns_as_they_were()
    {
        var storage = new FakeStorage { FailHiddenColumns = true };
        var preference = new HiddenColumnsPreference(storage);
        Assert.Throws<IOException>(() => preference.Show(ResourceKind.Routes, "Remarks", false, RouteColumns));
        Assert.Equal(RouteColumns, preference.Shown(ResourceKind.Routes, RouteColumns));
    }

    [Fact]
    public void The_stored_lists_survive_reopening_the_database_and_do_not_touch_workspace_data()
    {
        var directory = Path.Combine(Path.GetTempPath(), "flightops-hidden-columns-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new WorkspaceStore(directory);
            var w = new Workspace();
            store.Save(w);
            var before = store.Load(w.Id).Serialize();
            Assert.Empty(store.LoadHiddenColumns(ResourceKind.Routes));

            store.SaveHiddenColumns(ResourceKind.Routes, ["Remarks", "Callsign, with comma", "名称"]);
            store.SaveHiddenColumns(ResourceKind.Aircraft, ["Name"]);
            var reopened = new WorkspaceStore(directory);
            Assert.Equal(["Remarks", "Callsign, with comma", "名称"], reopened.LoadHiddenColumns(ResourceKind.Routes));
            Assert.Equal(["Name"], reopened.LoadHiddenColumns(ResourceKind.Aircraft));
            Assert.Empty(reopened.LoadHiddenColumns(ResourceKind.Airports));

            reopened.SaveHiddenColumns(ResourceKind.Routes, []);
            Assert.Empty(new WorkspaceStore(directory).LoadHiddenColumns(ResourceKind.Routes));
            Assert.Equal(before, store.Load(w.Id).Serialize());
            Assert.Equal("comfortable", store.LoadTableDensity());
        }
        finally
        {
            using (var pooled = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "workspaces.db") }.ToString()))
                SqliteConnection.ClearPool(pooled);
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void A_stored_value_that_is_not_a_list_of_names_reads_as_nothing_hidden()
    {
        var directory = Path.Combine(Path.GetTempPath(), "flightops-hidden-columns-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new WorkspaceStore(directory);
            store.Save(new Workspace());
            var file = Path.Combine(directory, "workspaces.db");
            foreach (var value in new[] { "not json", "{\"a\":1}", "[1,2]", "[\"Remarks\",null]" })
            {
                using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString()))
                {
                    c.Open();
                    using var cmd = c.CreateCommand();
                    cmd.CommandText = "INSERT INTO app_settings VALUES('hidden_columns.Routes',$v) ON CONFLICT(key) DO UPDATE SET value=$v";
                    cmd.Parameters.AddWithValue("$v", value);
                    cmd.ExecuteNonQuery();
                }
                Assert.Empty(new WorkspaceStore(directory).LoadHiddenColumns(ResourceKind.Routes));
            }
        }
        finally
        {
            using (var pooled = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "workspaces.db") }.ToString()))
                SqliteConnection.ClearPool(pooled);
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }
}
