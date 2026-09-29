namespace VamSys.Infrastructure;

// Compiled only into the isolated test/QA build of the real storage implementation.
public sealed partial class WorkspaceStore
{
    internal Action<string>? Barrier { get; set; }
    partial void OnStorageBarrier(string point) => Barrier?.Invoke(point);
}
