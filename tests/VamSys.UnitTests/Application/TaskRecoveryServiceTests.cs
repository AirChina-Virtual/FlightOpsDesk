using VamSys.Core;

namespace VamSys.UnitTests.Application;

public class TaskRecoveryServiceTests
{
    sealed class Ui : IRecoveryUi
    {
        public readonly List<string> Calls = [];
        public readonly List<RecoveryOutcome> Reported = [];
        public bool AllowReads = true, ConfirmAnything = true, VerifyChosen;
        public RecoveryIdAnswer Answer = RecoveryIdAnswer.Cancel;
        public bool? LastCanVerify, LastOnline;
        public Task RunReadAsync(Func<CancellationToken, Task> read)
        {
            Calls.Add("read");
            return AllowReads ? read(CancellationToken.None) : Task.CompletedTask;
        }
        public void ItemChanged(BatchJob job, ChangeItem item) => Calls.Add("changed:" + item.Id.ToString("N")[..4]);
        public Task<RecoveryIdAnswer> AskRecoveryIdAsync(ChangeItem item, bool online, bool canVerifyNotApplied)
        { Calls.Add("ask"); LastOnline = online; LastCanVerify = canVerifyNotApplied; return Task.FromResult(Answer); }
        public Task<bool> ConfirmManualDeleteAsync(ChangeItem item) { Calls.Add("manual?"); return Task.FromResult(ConfirmAnything); }
        public Task ShowStillReadableAsync(ChangeItem item, NoReceiptInspection review) { Calls.Add("still-readable"); return Task.CompletedTask; }
        public Task<bool> ConfirmNoReceiptAsync(ChangeItem item, NoReceiptInspection review, bool deleted) { Calls.Add("no-receipt?" + deleted); return Task.FromResult(ConfirmAnything); }
        public Task ShowCannotAcceptAsync(ChangeItem item, ServerValueInspection review) { Calls.Add("cannot-accept"); return Task.CompletedTask; }
        public Task<bool> ConfirmAcceptAsync(ChangeItem item, ServerValueInspection review) { Calls.Add("accept?"); return Task.FromResult(ConfirmAnything); }
        public Task<bool> ShowUnknownAsync(ChangeItem item, bool canVerifyNotApplied) { Calls.Add("unknown:" + canVerifyNotApplied); return Task.FromResult(VerifyChosen); }
        public void Report(RecoveryOutcome outcome) => Reported.Add(outcome);
    }

    sealed class Fixture
    {
        public readonly FakeStorage Storage = new();
        public readonly InteractionGate Gate = new();
        public readonly FakeOnline Online = new();
        public readonly WorkspaceSession Session;
        public readonly TaskRecoveryService Service;
        public readonly Ui UiScript = new();
        public readonly BatchJob Job;
        public Fixture(RunMode mode = RunMode.Online)
        {
            Storage.Seed("va", mode);
            Session = new(Storage, Gate, new FakeTimer());
            Session.Open("default");
            Session.Workspace.Mode = mode;
            Session.Workspace.AirlineId = "7";
            Job = new BatchJob { Mode = RunMode.Online };
            Session.Workspace.Jobs.Add(Job);
            Storage.Stored[Session.Workspace.Id] = Session.Workspace.Serialize();
            Service = new(Session, Online);
        }
        public FakeOnlineSession Api => Online.For(Session.Workspace);
        public ChangeItem Add(ItemState state, ChangeKind kind = ChangeKind.Create, ResourceKind resource = ResourceKind.Routes)
        {
            var item = new ChangeItem { State = state, Kind = kind, Resource = resource };
            Job.Items.Add(item);
            return item;
        }
        public Task Review(Func<Task>? recover = null, bool available = true)
            => Service.ReviewUnknownAsync(Job, available, UiScript, recover ?? (() => Task.CompletedTask));
    }

    // --- Running, verifying and discarding ---

    [Fact]
    public async Task Executing_runs_the_task_on_the_session_with_the_checkpoint_writer_and_reports_progress()
    {
        var f = new Fixture();
        var progress = new List<BatchProgress>();
        f.Api.OnExecute = async (job, writer) =>
        {
            await writer.WriteAsync(new(CheckpointKind.Task, job), CancellationToken.None);
        };
        await f.Service.ExecuteAsync(f.Job, progress.Add, CancellationToken.None);
        Assert.Equal(["execute"], f.Api.Calls);
        Assert.Same(f.Job, Assert.Single(f.Storage.Checkpoints).Job);
    }

    [Fact]
    public async Task A_checkpoint_that_cannot_be_written_reloads_the_workspace_drops_the_session_and_rethrows()
    {
        var f = new Fixture();
        var before = f.Session.Workspace;
        f.Api.ExecuteError = new PersistenceException(new IOException("disk"));
        await Assert.ThrowsAsync<PersistenceException>(() => f.Service.ExecuteAsync(f.Job, _ => { }, CancellationToken.None));
        Assert.Contains(before.Id, f.Online.Released);
        Assert.NotSame(before, f.Session.Workspace);
        Assert.Equal(RunMode.Offline, f.Session.Workspace.Mode);
        Assert.False(f.Gate.StorageBlocked);
    }

    [Fact]
    public async Task Other_errors_leave_the_workspace_and_the_session_alone()
    {
        var f = new Fixture();
        var before = f.Session.Workspace;
        f.Api.ExecuteError = new HttpRequestException("down");
        await Assert.ThrowsAsync<HttpRequestException>(() => f.Service.ExecuteAsync(f.Job, _ => { }, CancellationToken.None));
        Assert.Same(before, f.Session.Workspace);
        Assert.Empty(f.Online.Released);
    }

    [Fact]
    public async Task Without_credentials_nothing_runs()
    {
        var f = new Fixture();
        f.Online.NoCredentials = true;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ExecuteAsync(f.Job, _ => { }, CancellationToken.None));
        Assert.Equal("ApiConnectFirst", MessageErrors.Describe(error).Code);
    }

    [Fact]
    public async Task Verifying_deletions_reads_and_reloads_after_a_checkpoint_failure()
    {
        var f = new Fixture();
        await f.Service.VerifyDeletionsAsync(f.Job, _ => { }, CancellationToken.None);
        Assert.Equal(["verify"], f.Api.Calls);
        f.Api.VerifyError = new PersistenceException(new IOException("disk"));
        var before = f.Session.Workspace;
        await Assert.ThrowsAsync<PersistenceException>(() => f.Service.VerifyDeletionsAsync(f.Job, _ => { }, CancellationToken.None));
        Assert.NotSame(before, f.Session.Workspace);
    }

    [Fact]
    public async Task Only_never_sent_items_of_an_online_task_can_be_dropped_and_dropping_reports_how_many()
    {
        var f = new Fixture();
        var pending = f.Add(ItemState.Pending);
        f.Add(ItemState.Succeeded);
        Assert.Equal([pending.Id], f.Service.DiscardableItems(f.Job));
        Assert.Empty(f.Service.DiscardableItems(new BatchJob { Mode = RunMode.Demo, Items = [new ChangeItem { State = ItemState.Pending }] }));
        f.Online.Discarded = 1;
        Assert.Equal(1, await f.Service.DiscardUnsentAsync(f.Job));
    }

    [Fact]
    public async Task A_checkpoint_failure_while_dropping_reloads_the_workspace()
    {
        var f = new Fixture();
        f.Online.DiscardError = new PersistenceException(new IOException("disk"));
        var before = f.Session.Workspace;
        await Assert.ThrowsAsync<PersistenceException>(() => f.Service.DiscardUnsentAsync(f.Job));
        Assert.NotSame(before, f.Session.Workspace);
        Assert.Contains(before.Id, f.Online.Released);
    }

    [Fact]
    public void A_plan_reads_as_its_dependencies_its_requests_or_the_reason_it_cannot_be_planned()
    {
        var f = new Fixture();
        var item = new ChangeItem { Kind = ChangeKind.Create };
        Assert.Equal(["POST /thing"], f.Service.Describe(item).Steps);
        item.Dependencies.AddRange([Guid.NewGuid(), Guid.NewGuid()]);
        Assert.Equal(2, f.Service.Describe(item).Dependencies);
        item.Dependencies.Clear();
        f.Api.Plan = _ => throw new InvalidOperationException("cannot plan");
        Assert.NotNull(f.Service.Describe(item).Error);
        f.Online.NoCredentials = true;
        Assert.Equal("ApiConnectFirst", f.Service.Describe(item).Error!.Code);
    }

    // --- Reviewing Unknown items ---

    [Fact]
    public async Task Review_only_runs_for_an_online_task_of_a_non_demo_workspace_while_the_window_is_available()
    {
        var f = new Fixture();
        f.Add(ItemState.Unknown);
        await f.Review(available: false);
        var demoJob = new BatchJob { Mode = RunMode.Demo, Items = [new ChangeItem { State = ItemState.Unknown }] };
        await f.Service.ReviewUnknownAsync(demoJob, true, f.UiScript, () => Task.CompletedTask);
        f.Session.Workspace.Mode = RunMode.Demo;
        await f.Review();
        Assert.Empty(f.UiScript.Calls);
    }

    [Fact]
    public async Task A_proposed_ID_is_saved_as_a_candidate_and_an_online_review_reads_the_task_back_once_at_the_end()
    {
        var f = new Fixture();
        var first = f.Add(ItemState.Unknown);
        var second = f.Add(ItemState.Unknown);
        f.UiScript.Answer = new("123", false);
        var recovered = 0;
        await f.Review(() => { recovered++; return Task.CompletedTask; });
        Assert.Equal(1, recovered);
        Assert.Equal(2, f.Storage.Checkpoints.Count);
        Assert.Equal("123", f.Job.Items[0].RecoveryCandidateId);
        Assert.Equal("123", f.Job.Items[1].RecoveryCandidateId);
        Assert.NotSame(first, f.Job.Items[0]);
        Assert.NotSame(second, f.Job.Items[1]);
        Assert.Equal(2, f.UiScript.Calls.Count(c => c.StartsWith("changed")));
        Assert.Equal(2, f.UiScript.Reported.Count(o => o == RecoveryOutcome.RecoverNow));
        Assert.Equal(true, f.UiScript.LastOnline);
    }

    [Fact]
    public async Task Offline_a_proposed_ID_is_only_kept_and_nothing_is_read_back()
    {
        var f = new Fixture(RunMode.Offline);
        f.Add(ItemState.Unknown);
        f.UiScript.Answer = new("5", false);
        var recovered = 0;
        await f.Review(() => { recovered++; return Task.CompletedTask; });
        Assert.Equal(0, recovered);
        Assert.Equal([RecoveryOutcome.CandidateSaved], f.UiScript.Reported);
        Assert.Equal("5", f.Job.Items[0].RecoveryCandidateId);
    }

    [Fact]
    public async Task An_ID_that_is_not_a_positive_number_is_refused_and_nothing_is_written()
    {
        var f = new Fixture();
        var item = f.Add(ItemState.Unknown);
        f.UiScript.Answer = new("abc", false);
        await Assert.ThrowsAsync<FormatException>(() => f.Review());
        Assert.Empty(f.Storage.Checkpoints);
        Assert.Same(item, f.Job.Items[0]);
        Assert.Null(item.RecoveryCandidateId);
    }

    [Fact]
    public async Task Cancelling_the_ID_question_changes_nothing()
    {
        var f = new Fixture();
        f.Add(ItemState.Unknown);
        await f.Review();
        Assert.Empty(f.Storage.Checkpoints);
        Assert.Equal([RecoveryOutcome.Nothing], f.UiScript.Reported);
    }

    [Fact]
    public async Task A_failed_checkpoint_while_saving_a_candidate_leaves_the_item_as_it_was()
    {
        var f = new Fixture();
        var item = f.Add(ItemState.Unknown);
        f.UiScript.Answer = new("9", false);
        f.Session.Gate.BlockStorage();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Review());
        Assert.Same(item, f.Job.Items[0]);
        Assert.Null(item.RecoveryCandidateId);
    }

    [Theory]
    [InlineData(true, "NotAppliedVerified")]
    [InlineData(false, "NotAppliedUnproven")]
    public async Task Proving_a_write_was_not_applied_reports_whether_the_read_proved_it(bool proven, string expected)
    {
        var f = new Fixture();
        var item = f.Add(ItemState.Unknown);
        f.Online.VerifyNotApplied = true;
        f.UiScript.Answer = new(null, true);
        f.Api.NotApplied = proven;
        await f.Review();
        Assert.Equal(expected, Assert.Single(f.UiScript.Reported).Name);
        Assert.Equal(["read", "changed:" + item.Id.ToString("N")[..4]], f.UiScript.Calls.Skip(1));
        Assert.Equal(true, f.UiScript.LastCanVerify);
    }

    [Fact]
    public async Task A_window_that_refuses_the_read_ends_the_proof_without_a_result()
    {
        var f = new Fixture();
        f.Add(ItemState.Unknown);
        f.Online.VerifyNotApplied = true;
        f.UiScript.Answer = new(null, true);
        f.UiScript.AllowReads = false;
        await f.Review();
        Assert.Equal([RecoveryOutcome.Nothing], f.UiScript.Reported);
        Assert.DoesNotContain("not-applied", f.Api.Calls);
    }

    [Fact]
    public async Task A_checkpoint_failure_while_proving_reloads_the_workspace()
    {
        var f = new Fixture();
        f.Add(ItemState.Unknown);
        f.Online.VerifyNotApplied = true;
        f.UiScript.Answer = new(null, true);
        f.Api.NotAppliedError = new PersistenceException(new IOException("disk"));
        var before = f.Session.Workspace;
        await Assert.ThrowsAsync<PersistenceException>(() => f.Review());
        Assert.NotSame(before, f.Session.Workspace);
        Assert.Contains(before.Id, f.Online.Released);
    }

    [Fact]
    public async Task A_soft_deleted_record_the_server_accepted_can_be_confirmed_by_hand_for_airports_and_routes_only()
    {
        var f = new Fixture();
        var airport = f.Add(ItemState.Unknown, ChangeKind.Delete, ResourceKind.Airports);
        airport.WriteAccepted = true;
        var fleet = f.Add(ItemState.Unknown, ChangeKind.Delete, ResourceKind.Fleets);
        fleet.WriteAccepted = true;
        await f.Review();
        Assert.Equal(["manual?", "changed:" + airport.Id.ToString("N")[..4]], f.UiScript.Calls.Take(2));
        Assert.Contains("manual", f.Api.Calls);
        Assert.Contains("unknown:False", f.UiScript.Calls);
        Assert.Equal(1, f.Api.Calls.Count(c => c == "manual"));
    }

    [Fact]
    public async Task Declining_the_manual_confirmation_changes_nothing()
    {
        var f = new Fixture();
        var item = f.Add(ItemState.Unknown, ChangeKind.Delete, ResourceKind.Routes);
        item.WriteAccepted = true;
        f.UiScript.ConfirmAnything = false;
        await f.Review();
        Assert.DoesNotContain("manual", f.Api.Calls);
        Assert.Equal(["manual?"], f.UiScript.Calls);
    }

    [Theory]
    [InlineData(true, false, "DeletedWithoutReceipt", "confirm-no-receipt")]
    [InlineData(false, true, "UnchangedWithoutReceipt", "mark-unchanged")]
    public async Task A_delete_without_a_receipt_is_confirmed_deleted_or_marked_unchanged_after_a_read(bool deleted, bool unchanged, string expected, string call)
    {
        var f = new Fixture();
        f.Add(ItemState.Unknown, ChangeKind.Delete);
        f.Online.NoReceiptAllowed = true;
        f.Api.NoReceipt = new(deleted, unchanged, [], new object());
        await f.Review();
        Assert.Equal(expected, Assert.Single(f.UiScript.Reported).Name);
        Assert.Contains(call, f.Api.Calls);
        Assert.Contains("no-receipt?" + deleted, f.UiScript.Calls);
    }

    [Fact]
    public async Task A_record_that_is_still_readable_and_changed_is_only_shown()
    {
        var f = new Fixture();
        f.Add(ItemState.Unknown, ChangeKind.Delete);
        f.Online.NoReceiptAllowed = true;
        f.Api.NoReceipt = new(false, false, [new("Name", "a", "b")], new object());
        await f.Review();
        Assert.Contains("still-readable", f.UiScript.Calls);
        Assert.DoesNotContain("confirm-no-receipt", f.Api.Calls);
        Assert.DoesNotContain("mark-unchanged", f.Api.Calls);
        Assert.Equal([RecoveryOutcome.Nothing], f.UiScript.Reported);
    }

    [Fact]
    public async Task Declining_the_no_receipt_confirmation_writes_nothing()
    {
        var f = new Fixture();
        f.Add(ItemState.Unknown, ChangeKind.Delete);
        f.Online.NoReceiptAllowed = true;
        f.Api.NoReceipt = new(true, false, [], new object());
        f.UiScript.ConfirmAnything = false;
        await f.Review();
        Assert.DoesNotContain("confirm-no-receipt", f.Api.Calls);
    }

    [Theory]
    [InlineData(ChangeKind.Update, "ServerValueAccepted")]
    [InlineData(ChangeKind.Delete, "ServerRecordAccepted")]
    public async Task The_server_value_can_be_accepted_after_a_read_and_a_confirmation(ChangeKind kind, string expected)
    {
        var f = new Fixture();
        f.Add(ItemState.Unknown, kind);
        f.Online.ServerValueAllowed = true;
        f.Api.ServerValue = new(true, true, "55", [new("Name", "a", "b")], new object());
        await f.Review();
        Assert.Equal(expected, Assert.Single(f.UiScript.Reported).Name);
        Assert.Contains("accept", f.Api.Calls);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task A_server_value_that_cannot_be_accepted_is_only_shown(bool canAccept, bool hasCurrent)
    {
        var f = new Fixture();
        f.Add(ItemState.Unknown, ChangeKind.Update);
        f.Online.ServerValueAllowed = true;
        f.Api.ServerValue = new(canAccept, hasCurrent, null, [], new object());
        await f.Review();
        Assert.Contains("cannot-accept", f.UiScript.Calls);
        Assert.DoesNotContain("accept", f.Api.Calls);
    }

    [Fact]
    public async Task Declining_to_accept_the_server_value_writes_nothing()
    {
        var f = new Fixture();
        f.Add(ItemState.Unknown, ChangeKind.Update);
        f.Online.ServerValueAllowed = true;
        f.Api.ServerValue = new(true, true, "55", [], new object());
        f.UiScript.ConfirmAnything = false;
        await f.Review();
        Assert.DoesNotContain("accept", f.Api.Calls);
        Assert.Equal([RecoveryOutcome.Nothing], f.UiScript.Reported);
    }

    [Fact]
    public async Task An_item_nothing_can_resolve_is_shown_and_the_person_may_still_ask_for_the_proof()
    {
        var f = new Fixture();
        f.Add(ItemState.Unknown, ChangeKind.Update);
        f.Online.VerifyNotApplied = true;
        f.UiScript.VerifyChosen = true;
        await f.Review();
        Assert.Contains("unknown:True", f.UiScript.Calls);
        Assert.Contains("not-applied", f.Api.Calls);

        var g = new Fixture();
        g.Add(ItemState.Unknown, ChangeKind.Update);
        g.UiScript.VerifyChosen = true;
        await g.Review();
        Assert.Contains("unknown:False", g.UiScript.Calls);
        Assert.DoesNotContain("not-applied", g.Api.Calls);
    }

    [Fact]
    public async Task Items_that_are_not_unknown_are_never_reviewed()
    {
        var f = new Fixture();
        f.Add(ItemState.Pending);
        f.Add(ItemState.Succeeded);
        f.Add(ItemState.Failed);
        await f.Review();
        Assert.Empty(f.UiScript.Calls);
    }
}
