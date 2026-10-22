namespace VamSys.Infrastructure;

// Compiled only into the isolated test/QA build of the real storage implementation.
internal sealed class ActionStorageFaults(Action<string> handler) : IStorageFaults
{
    public Action<string> Handler { get; } = handler;
    public void Reached(string point) => Handler(point);
}

internal sealed partial class StorageContext
{
    internal IStorageFaults? Faults { get; set; }
}

public sealed partial class WorkspaceStore
{
    internal Action<string>? Barrier
    {
        get => (context.Faults as ActionStorageFaults)?.Handler;
        set => context.Faults = value == null ? null : new ActionStorageFaults(value);
    }
}
