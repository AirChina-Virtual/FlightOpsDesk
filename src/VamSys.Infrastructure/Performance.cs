using System.Collections.Concurrent;

namespace VamSys.Infrastructure;

public sealed class RequestCoordinator(TimeProvider? timeProvider = null)
{
    public static RequestCoordinator Shared { get; } = new();
    public TimeProvider Clock { get; } = timeProvider ?? TimeProvider.System;
    internal sealed class Budget { internal readonly SemaphoreSlim Gate = new(1); internal DateTimeOffset Next; internal bool Backoff; }
    internal readonly ConcurrentDictionary<string, Budget> Budgets = new();
}
