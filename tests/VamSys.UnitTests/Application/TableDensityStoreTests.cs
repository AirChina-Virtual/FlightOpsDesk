using Microsoft.Data.Sqlite;
using VamSys.Core;
using VamSys.Infrastructure;

namespace VamSys.UnitTests.Application;

public class TableDensityStoreTests
{
    [Fact]
    public void The_density_preference_defaults_persists_and_normalizes_without_changing_workspace_data()
    {
        var directory = Path.Combine(Path.GetTempPath(), "flightops-density-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new WorkspaceStore(directory);
            var w = new Workspace();
            store.Save(w);
            var before = store.Load(w.Id).Serialize();
            Assert.Equal("comfortable", store.LoadTableDensity());

            store.SaveTableDensity("compact");
            Assert.Equal("compact", new WorkspaceStore(directory).LoadTableDensity());

            store.SaveTableDensity("invalid");
            Assert.Equal("comfortable", store.LoadTableDensity());
            Assert.Equal(before, store.Load(w.Id).Serialize());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }
}
