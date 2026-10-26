namespace VamSys.Core;

// Runs a piece of work on a worker thread while the window is locked against edits, jobs and navigation.
public interface IWorkerLock
{
    Task<T> RunAsync<T>(Func<T> work);
}

// The plan the review page shows, with what it was built for.
public sealed record ShownReview(WorkspaceReviewPlan Plan, Guid WorkspaceId, string? AirlineId, RunMode Mode);

// The result of comparing the shown plan with the workspace as it is now. Plan is set when the shown plan may be
// submitted; Changed is set when the page is out of date and has to be rebuilt and reviewed again.
public sealed record ReviewCheck(WorkspaceReviewPlan? Plan, bool Changed);

// The workspace review: what the whole workspace would submit, which changes are ready, deferred or blocked, and
// whether the plan on screen still describes the workspace. Building the plan only reads.
public sealed class ReviewService(WorkspaceSession session, IOnlineGateway online)
{
    readonly ChangePlanner planner = new();
    long generation;

    public ShownReview? Shown { get; private set; }

    // Reads the window state on the UI thread; the returned work only reads the captured workspace and may run on a
    // worker while the UI is locked (no edits, jobs or navigation). It also computes the content key.
    public Func<WorkspaceReviewPlan> Capture()
    {
        var w = session.Workspace;
        var mode = w.Mode;
        var verified = w.VerifiedOperations.ToHashSet(StringComparer.Ordinal);
        var onlineSession = online.Connected(w.Id);
        var connected = onlineSession != null;
        Action<ChangeItem>? preview = connected ? c => onlineSession!.PreviewPlan(c) : null;
        return () => ReviewPlanBuilder.BuildForWorkspace(planner, w, mode, connected, verified, preview);
    }
    public WorkspaceReviewPlan Build() => Capture()();

    // A page that loads its plan on a worker takes a generation; a plan that arrives for an older one is dropped.
    public long BeginLoad() => ++generation;
    public bool IsCurrent(long loadGeneration) => loadGeneration == generation;
    public void Show(WorkspaceReviewPlan plan, Guid workspaceId, string? airlineId, RunMode mode) => Shown = new(plan, workspaceId, airlineId, mode);
    // The workspace under the page was replaced or cleared, or the page is about to be left for the task page.
    public void Invalidate(bool forget = true)
    {
        generation++;
        if (forget)
            Shown = null;
    }

    // The shown plan when the live workspace still yields the same review. The live plan is rebuilt on a worker; the
    // shown plan's key was computed when it was built.
    public async Task<ReviewCheck> CheckCurrentAsync(RunMode mode, IWorkerLock locker)
    {
        // The worker also builds the shown plan's submission items, so the UI thread never has to.
        var build = Capture();
        var shown = Shown?.Plan;
        var current = await locker.RunAsync(() =>
        {
            var plan = build();
            if (shown is not null && shown.Blockers.Count == 0 && plan.Blockers.Count == 0 && shown.ContentKey() == plan.ContentKey())
                _ = shown.SubmitItems;
            return plan;
        });
        var w = session.Workspace;
        if (Shown is not { } latest || latest.WorkspaceId != w.Id || latest.AirlineId != w.AirlineId || latest.Mode != mode || latest.Plan.ContentKey() != current.ContentKey())
            return new(null, true);
        if (current.Blockers.Count > 0 || current.SubmitCount == 0)
            return new(null, false);
        return new(latest.Plan, false);
    }
}
