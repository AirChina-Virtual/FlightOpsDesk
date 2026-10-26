using VamSys.Core;
using VamSys.Infrastructure;

namespace VamSys.App;

// Connects the demo runner to the simulated server.
static class DemoEngine
{
    // The simulated server first learns what earlier demo tasks already created, so they are not created twice.
    public static (IResourceReader Reader, IResourceWriter Writer) Create(Workspace workspace, IReadOnlyList<BatchJob> demoJobs)
    {
        var demo = new DemoService(workspace);
        foreach (var history in demoJobs)
            demo.RestoreSuccessful(history);
        return (demo, demo);
    }
}
