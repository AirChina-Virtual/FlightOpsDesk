using System.Net;
using System.Text.Json;
using VamSys.Core;
using VamSys.Infrastructure;

namespace VamSys.UnitTests.Api;

/// <summary>The small parts the execution pipeline is made of: item lookup, snapshot positions, checkpoints and the meaning of a failure.</summary>
public class PipelineComponentTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- item index -------------------------------------------------------------------------------------------

    [Fact]
    public void Items_are_found_by_id_and_by_the_local_id_of_their_row()
    {
        var a = ApiKit.Create(ResourceKind.Fleets, ("Name", "a"));
        var b = ApiKit.Create(ResourceKind.Fleets, ("Name", "b"));
        var index = new ItemIndex([a, b]);
        Assert.Same(a, index.ById(a.Id));
        Assert.Same(b, index.ByLocalId(b.After.LocalId));
    }

    [Fact]
    public void A_lookup_has_the_semantics_of_Single()
    {
        var a = ApiKit.Create(ResourceKind.Fleets, ("Name", "a"));
        var twin = ApiKit.Create(ResourceKind.Fleets, ("Name", "twin"));
        twin.Id = a.Id;
        twin.After = twin.After.WithLocalId(a.After.LocalId);
        var index = new ItemIndex([a, twin]);
        Assert.Equal("Sequence contains no matching element", Assert.Throws<InvalidOperationException>(() => index.ById(Guid.NewGuid())).Message);
        Assert.Equal("Sequence contains no matching element", Assert.Throws<InvalidOperationException>(() => index.ByLocalId(Guid.NewGuid())).Message);
        Assert.Equal("Sequence contains more than one matching element", Assert.Throws<InvalidOperationException>(() => index.ById(a.Id)).Message);
        Assert.Equal("Sequence contains more than one matching element", Assert.Throws<InvalidOperationException>(() => index.ByLocalId(a.After.LocalId)).Message);
    }

    // ---- snapshot positions -----------------------------------------------------------------------------------

    static List<DataRow> Rows(int count) => Enumerable.Range(0, count).Select(n => new DataRow { Fields = new Dictionary<string, string> { ["n"] = n.ToString() } }).ToList();

    [Fact]
    public void Removing_and_appending_gives_the_same_list_as_RemoveAll_and_Add()
    {
        foreach (var seed in Enumerable.Range(1, 12))
        {
            var random = new Random(seed);
            var original = Rows(random.Next(0, 80));
            var data = new ResourceData { Snapshot = [.. original] };
            var model = new List<DataRow>(original);
            var positions = new SnapshotPositions(data);
            for (var step = 0; step < 200; step++)
            {
                var pool = model.Count == 0 ? Guid.NewGuid() : model[random.Next(model.Count)].LocalId;
                var id = random.Next(5) == 0 ? Guid.NewGuid() : pool;
                positions.Remove(id);
                model.RemoveAll(r => r.LocalId == id);
                if (random.Next(3) != 0)
                {
                    var row = new DataRow().WithLocalId(id);
                    positions.Append(row);
                    model.Add(row);
                }
                Assert.True(model.SequenceEqual(data.Snapshot, ReferenceEqualityComparer.Instance as IEqualityComparer<DataRow>), $"seed {seed}, step {step}");
            }
        }
    }

    [Fact]
    public void A_snapshot_changed_behind_its_back_or_replaced_is_still_handled_correctly()
    {
        var rows = Rows(10);
        var data = new ResourceData { Snapshot = [.. rows] };
        var positions = new SnapshotPositions(data);
        positions.Remove(rows[3].LocalId);
        // somebody inserts a row at the front
        var intruder = new DataRow();
        data.Snapshot.Insert(0, intruder);
        positions.Remove(rows[5].LocalId);
        Assert.DoesNotContain(rows[5], data.Snapshot);
        Assert.Contains(intruder, data.Snapshot);
        Assert.Equal(9, data.Snapshot.Count);
        // the whole list is replaced
        var fresh = Rows(4);
        data.Snapshot = fresh;
        positions.Remove(fresh[2].LocalId);
        positions.Append(fresh[2]);
        Assert.Equal([fresh[0], fresh[1], fresh[3], fresh[2]], data.Snapshot);
    }

    [Fact]
    public void Repeated_local_ids_fall_back_to_removing_every_match()
    {
        var rows = Rows(5);
        var twin = new DataRow().WithLocalId(rows[1].LocalId);
        var data = new ResourceData { Snapshot = [.. rows, twin] };
        var positions = new SnapshotPositions(data);
        positions.Remove(rows[1].LocalId);
        Assert.Equal([rows[0], rows[2], rows[3], rows[4]], data.Snapshot);
    }

    [Fact]
    public void An_appended_row_can_be_removed_again_and_an_unknown_row_changes_nothing()
    {
        var rows = Rows(3);
        var data = new ResourceData { Snapshot = [.. rows] };
        var positions = new SnapshotPositions(data);
        var added = new DataRow();
        positions.Append(added);
        positions.Remove(added.LocalId);
        positions.Remove(Guid.NewGuid());
        Assert.Equal(rows, data.Snapshot);
    }

    // ---- checkpoints ------------------------------------------------------------------------------------------

    sealed class Writer : IBatchCheckpointWriter
    {
        public readonly List<BatchCheckpoint> Written = [];
        public Exception? Failure;
        public Task WriteAsync(BatchCheckpoint checkpoint, CancellationToken ct)
        {
            if (Failure != null)
                return Task.FromException(Failure);
            Written.Add(checkpoint);
            return Task.CompletedTask;
        }
    }

    static (CheckpointSink Sink, BatchJob Job, ChangeItem Item, Workspace Workspace) Sink(IBatchCheckpointWriter? writer, Func<Task>? persist, Action? committed, Action<BatchProgress>? progress)
    {
        var workspace = new Workspace();
        var item = ApiKit.Create(ResourceKind.Fleets, ("Name", "a"));
        var job = new BatchJob { Mode = RunMode.Online, Items = [item] };
        job.SetStatus(JobStatus.Running, Messages.Define("Text_5026A63B58"));
        return (new CheckpointSink(workspace, job, persist, writer, committed, progress) { Active = item }, job, item, workspace);
    }

    [Fact]
    public async Task A_checkpoint_goes_to_the_writer_and_then_to_the_listeners()
    {
        var writer = new Writer();
        var events = new List<string>();
        var progress = new List<BatchProgress>();
        var (sink, job, item, workspace) = Sink(writer, null, () => events.Add("committed"), p => { events.Add("progress"); progress.Add(p); });
        await sink.SaveAsync(CheckpointKind.Step);
        var written = Assert.Single(writer.Written);
        Assert.Equal(CheckpointKind.Step, written.Kind);
        Assert.Same(job, written.Job);
        Assert.Same(item, written.Item);
        Assert.Equal(["committed", "progress"], events);
        Assert.Equal(new BatchProgress(workspace.Id, job.Id, item.Id, false), Assert.Single(progress));
    }

    [Fact]
    public async Task Progress_is_immediate_when_the_job_stopped_or_the_item_needs_attention()
    {
        var progress = new List<BatchProgress>();
        var (sink, job, item, _) = Sink(new Writer(), null, null, progress.Add);
        item.State = ItemState.Unknown;
        await sink.SaveAsync();
        item.State = ItemState.Running;
        job.SetStatus(JobStatus.Paused, Messages.Define("ApiAuth"));
        await sink.SaveAsync();
        item.State = ItemState.Succeeded;
        job.SetStatus(JobStatus.Running, Messages.Define("Text_5026A63B58"));
        await sink.SaveAsync();
        Assert.Equal([true, true, false], progress.Select(p => p.Immediate));
    }

    [Fact]
    public async Task Without_a_writer_the_persist_callback_is_used()
    {
        var calls = 0;
        var (sink, _, _, _) = Sink(null, () => { calls++; return Task.CompletedTask; }, null, null);
        await sink.SaveAsync();
        Assert.Equal(1, calls);
        Assert.False(sink.HasWriter);
    }

    [Fact]
    public async Task A_failed_save_is_a_persistence_failure_and_nobody_is_told_it_succeeded()
    {
        var writer = new Writer { Failure = new IOException("disk full") };
        var told = false;
        var (sink, _, _, _) = Sink(writer, null, () => told = true, _ => told = true);
        var error = await Assert.ThrowsAsync<PersistenceException>(() => sink.SaveAsync());
        Assert.IsType<IOException>(error.InnerException);
        Assert.False(told);
        var rebase = await Assert.ThrowsAsync<PersistenceException>(() => sink.WriteRebaseAsync(ApiKit.Create(ResourceKind.Fleets), new(ResourceKind.Fleets, Guid.NewGuid(), new(RowMutationKind.Keep), new(RowMutationKind.Keep), DateTimeOffset.UtcNow, true, null)));
        Assert.IsType<IOException>(rebase.InnerException);
    }

    [Fact]
    public void A_listener_that_fails_after_a_rebase_is_a_persistence_failure_too()
    {
        var (sink, _, item, _) = Sink(new Writer(), null, () => throw new InvalidOperationException("listener"), null);
        Assert.Throws<PersistenceException>(() => sink.NotifyRebased(item));
    }

    [Fact]
    public async Task Manual_resolutions_save_a_copy_and_replace_the_item_only_afterwards()
    {
        var writer = new Writer();
        var item = ApiKit.Create(ResourceKind.Fleets, ("Name", "a"));
        item.State = ItemState.Unknown;
        var job = new BatchJob { Mode = RunMode.Online, Items = [item] };
        var candidate = JobCheckpoint.Clone(item);
        Assert.NotSame(item, candidate);
        Assert.Equal(item.Id, candidate.Id);
        Assert.Equal("a", candidate.After.Get("Name"));
        candidate.State = ItemState.Failed;
        var header = JobCheckpoint.Header(job, JobStatus.NeedsAttention, Messages.Define("Text_E9008C53CF"));
        Assert.NotSame(job, header);
        Assert.Same(job.Items, header.Items);
        await JobCheckpoint.WriteAsync(writer, new(CheckpointKind.Task, header, candidate));
        Assert.Same(item, job.Items[0]);
        JobCheckpoint.Replace(job, item, candidate, header);
        Assert.Same(candidate, job.Items[0]);
        Assert.Equal(JobStatus.NeedsAttention, job.StatusCode);
        writer.Failure = new IOException("disk full");
        await Assert.ThrowsAsync<PersistenceException>(() => JobCheckpoint.WriteAsync(writer, new(CheckpointKind.Task, header, candidate)));
    }

    [Fact]
    public void A_removal_takes_the_snapshot_row_and_the_deletion_draft_and_clears_the_history()
    {
        var kit = new ApiKit();
        var row = kit.Map(ResourceKind.Airports, ApiKit.Egll);
        var data = new ResourceData { Snapshot = [row.Dup()] };
        var draft = row.With("_delete", "TRUE");
        data.Snapshot[0] = row.WithLocalId(draft.LocalId);
        data.Draft.Add(draft);
        var at = DateTimeOffset.UtcNow;
        JobCheckpoint.ApplyRemoval(data, new(ResourceKind.Airports, draft.LocalId, new(RowMutationKind.Delete), new(RowMutationKind.Delete), at, true, null));
        Assert.Empty(data.Snapshot);
        Assert.Empty(data.Draft);
        Assert.Equal(at, data.SnapshotAt);
    }

    // ---- item completion --------------------------------------------------------------------------------------

    [Fact]
    public void A_verified_create_replaces_the_snapshot_row_follows_in_the_draft_and_counts_as_verified()
    {
        var kit = new ApiKit();
        kit.LoadStandardAirportsAndFleets();
        var data = kit.Workspace.Resources[ResourceKind.Fleets];
        var draft = new DataRow { Fields = new Dictionary<string, string> { ["Name"] = "New fleet", ["Type Code"] = "B738", ["Max Passengers"] = "180", ["Note"] = "draft only" } };
        data.Draft.Add(draft);
        var item = ApiKit.Create(ResourceKind.Fleets, ("Name", "New fleet"), ("Type Code", "B738"));
        item.After = draft;
        item.Fields["Max Passengers"] = new(FieldIntent.Set, "180");
        var verified = kit.Map(ResourceKind.Fleets, """{"id":90,"airline_id":7,"name":"New fleet","code":"B738","type":"pax","max_pax":180}""");
        var stored = ItemCompletion.ApplyRebase(kit.Workspace, item, verified, null)!;

        Assert.Equal(ItemState.Succeeded, item.State);
        Assert.True(item.Rebased);
        Assert.True(item.CreateCompleted);
        Assert.Equal("90", item.RemoteId);
        Assert.Equal("Text_631F4DBEC2", item.Description!.Code);
        // the snapshot holds the server's row under the draft's local id, with the draft-only column kept
        Assert.Equal(draft.LocalId, stored.LocalId);
        Assert.Equal("90", stored.Identity!.RemoteId);
        Assert.Equal("draft only", stored.Get("Note"));
        Assert.Same(stored, data.Snapshot.Single(r => r.LocalId == draft.LocalId));
        // the draft got the server's identity and values for the fields it had not changed since the create
        var current = data.FindDraftRow(draft.LocalId)!;
        Assert.Equal("90", current.Identity!.RemoteId);
        Assert.Equal("90", current.Get("ID"));
        Assert.Contains("Fleets:Create", kit.Workspace.VerifiedOperations);
        Assert.NotNull(data.SnapshotAt);
    }

    [Fact]
    public void A_verified_delete_removes_the_snapshot_row_and_the_deletion_draft()
    {
        var kit = new ApiKit();
        kit.LoadStandardAirportsAndFleets();
        var data = kit.Workspace.Resources[ResourceKind.Airports];
        var snapshot = data.Snapshot[0];
        var draft = data.Draft[0].With("_delete", "TRUE");
        data.ReplaceDraftRows([draft]);
        var item = ApiKit.Delete(ResourceKind.Airports, snapshot);
        item.After = draft;
        var rebased = ItemCompletion.ApplyRebase(kit.Workspace, item, null, null);
        Assert.Null(rebased);
        Assert.DoesNotContain(data.Snapshot, r => r.LocalId == draft.LocalId);
        Assert.DoesNotContain(data.Draft, r => r.LocalId == draft.LocalId);
        Assert.Equal(ItemState.Succeeded, item.State);
        Assert.Contains("Airports:Delete", kit.Workspace.VerifiedOperations);
    }

    [Fact]
    public void Completing_by_position_and_by_scan_give_the_same_snapshot()
    {
        List<string> Run(bool byPosition)
        {
            var kit = new ApiKit();
            kit.LoadStandardAirportsAndFleets();
            var data = kit.Workspace.Resources[ResourceKind.Airports];
            var target = data.Snapshot[0];
            var item = ApiKit.Update(ResourceKind.Airports, target, ("Name", "Heathrow"));
            item.After = data.Draft[0].With("Name", "Heathrow");
            data.ReplaceDraftRows([item.After]);
            var verified = kit.Map(ResourceKind.Airports, """{"id":101,"airline_id":7,"icao":"EGLL","iata":"LHR","name":"Heathrow","container_ids":[1]}""");
            ItemCompletion.ApplyRebase(kit.Workspace, item, verified, byPosition ? new SnapshotPositions(data) : null);
            return data.Snapshot.Select(r => r.Identity!.RemoteId + ":" + r.Get("Name")).ToList();
        }
        Assert.Equal(Run(false), Run(true));
        Assert.Equal(["102:Gatwick", "101:Heathrow"], Run(true));
    }

    // ---- failures ---------------------------------------------------------------------------------------------

    sealed record FailureCase(string Name, Func<Exception> Error, OperationsBatchExecutor.ExecutionPhase Phase, bool Created, ItemState State, string Code, bool Stops, bool Prepared);

    static ApiDiagnostic Evidence(int status) => new() { Method = "PUT", Path = "/api/v3/operations/fleet/4", Status = status, ServerMessage = "nope" };
    static ApiResponseException Response(HttpStatusCode status, bool details = true) => new(status, "body", details ? Evidence((int)status) : null);

    static IEnumerable<FailureCase> Cases()
    {
        const OperationsBatchExecutor.ExecutionPhase Pre = OperationsBatchExecutor.ExecutionPhase.Preflight, Write = OperationsBatchExecutor.ExecutionPhase.Writing,
            Verify = OperationsBatchExecutor.ExecutionPhase.Verifying, Recover = OperationsBatchExecutor.ExecutionPhase.Recovering;
        // not sent
        yield return new("not sent, fresh item", () => new RequestNotSentException(new OperationCanceledException()), Write, false, ItemState.Pending, "ApiNotSent", false, true);
        yield return new("not sent after an answered post", () => new RequestNotSentException(new OperationCanceledException()), Write, true, ItemState.Unknown, "ApiResumeCreate", false, true);
        yield return new("not sent, authentication, fresh", () => new RequestNotSentException(new ApiAccessException("auth", 401)), Write, false, ItemState.Pending, "ApiAuth", true, true);
        yield return new("not sent, authentication, after a post", () => new RequestNotSentException(new ApiAccessException("auth", 401)), Write, true, ItemState.Unknown, "ApiResumeCreate", true, true);
        // authentication
        yield return new("401 while preflighting", () => new ApiAccessException("auth", 401, Evidence(401)), Pre, false, ItemState.Pending, "ApiAuth", true, false);
        yield return new("403 while writing, fresh", () => new ApiAccessException("auth", 403, Evidence(403)), Write, false, ItemState.Pending, "ApiAuth", true, true);
        yield return new("401 while writing after a post", () => new ApiAccessException("auth", 401, Evidence(401)), Write, true, ItemState.Unknown, "ApiResumeCreate", true, true);
        yield return new("access error without status while writing", () => new ApiAccessException("auth", null, Evidence(0)), Write, false, ItemState.Unknown, "ApiAuth", true, false);
        yield return new("401 while verifying", () => new ApiAccessException("auth", 401, Evidence(401)), Verify, false, ItemState.Unknown, "ApiAuth", true, false);
        yield return new("401 while recovering", () => new ApiAccessException("auth", 401, Evidence(401)), Recover, false, ItemState.Unknown, "ApiAuth", true, false);
        // responses
        yield return new("422 while preflighting", () => Response(HttpStatusCode.UnprocessableEntity), Pre, false, ItemState.Failed, "ApiRejected", false, false);
        yield return new("422 without details", () => Response(HttpStatusCode.UnprocessableEntity, details: false), Pre, false, ItemState.Failed, "ApiHttp", false, false);
        yield return new("500 while preflighting", () => Response(HttpStatusCode.InternalServerError), Pre, false, ItemState.Pending, "ApiRejected", false, false);
        yield return new("429 while preflighting", () => Response(HttpStatusCode.TooManyRequests), Pre, false, ItemState.Pending, "ApiRateLimited", false, false);
        yield return new("429 while writing, fresh", () => Response(HttpStatusCode.TooManyRequests), Write, false, ItemState.Pending, "ApiRateLimited", false, true);
        yield return new("429 while writing after a post", () => Response(HttpStatusCode.TooManyRequests), Write, true, ItemState.Unknown, "ApiResumeCreate", false, true);
        yield return new("500 while writing", () => Response(HttpStatusCode.InternalServerError), Write, false, ItemState.Unknown, "ApiUnknownError", false, false);
        yield return new("503 while writing after a post", () => Response(HttpStatusCode.ServiceUnavailable), Write, true, ItemState.Unknown, "ApiUnknownError", false, false);
        yield return new("404 while writing", () => Response(HttpStatusCode.NotFound), Write, false, ItemState.Failed, "ApiRejected", false, true);
        yield return new("422 while writing, fresh", () => Response(HttpStatusCode.UnprocessableEntity), Write, false, ItemState.Failed, "ApiRejected", false, true);
        yield return new("422 while verifying", () => Response(HttpStatusCode.UnprocessableEntity), Verify, false, ItemState.Unknown, "ApiUnknownError", false, false);
        yield return new("500 while recovering", () => Response(HttpStatusCode.InternalServerError), Recover, false, ItemState.Unknown, "ApiUnknownError", false, false);
        // transport and everything else
        yield return new("timeout while preflighting", () => new TimeoutException(), Pre, false, ItemState.Pending, "ApiPreflight", false, false);
        yield return new("timeout while writing", () => new TimeoutException(), Write, false, ItemState.Unknown, "ApiUnknown", false, false);
        yield return new("connection failure while verifying", () => new HttpRequestException("down"), Verify, false, ItemState.Unknown, "ApiUnknown", false, false);
        yield return new("cancelled while recovering", () => new OperationCanceledException(), Recover, false, ItemState.Unknown, "ApiUnknown", false, false);
        yield return new("unreadable answer while writing", () => new JsonException("bad"), Write, false, ItemState.Unknown, "ApiUnknown", false, false);
        yield return new("blocked while preflighting", () => OperationsAdapter.Block("ApiDuplicate"), Pre, false, ItemState.Failed, "ApiDuplicate", false, false);
        yield return new("blocked while writing", () => OperationsAdapter.Block("ApiInvalid", "x"), Write, false, ItemState.Unknown, "ApiInvalid", false, false);
    }

    [Fact]
    public async Task A_failure_means_what_its_phase_and_the_state_of_the_write_say_it_means()
    {
        foreach (var c in Cases())
        {
            var kit = new ApiKit();
            var item = ApiKit.Create(ResourceKind.Fleets, ("Name", "a"));
            item.ApiSteps = [new BatchStep { Method = "POST", Path = "fleet", State = BatchStepState.InFlight }, new BatchStep { Method = "PUT", Path = "fleet/4", State = BatchStepState.InFlight }];
            item.CreateCompleted = c.Created;
            var job = new BatchJob { Mode = RunMode.Online, Items = [item] };
            job.SetStatus(JobStatus.Running, Messages.Define("Text_5026A63B58"));
            var saves = 0;
            var sink = new CheckpointSink(kit.Workspace, job, () => { saves++; return Task.CompletedTask; }, null, null, null) { Active = item };
            var run = new JobRun(kit.Api, kit.Workspace, job, sink, Ct);
            var stops = await new FailureHandler(run).HandleAsync(c.Error(), new ItemRun(item, c.Phase));
            Assert.True(c.State == item.State, $"{c.Name}: state {item.State}");
            Assert.True(c.Code == item.Description!.Code, $"{c.Name}: message {item.Description!.Code}");
            Assert.True(c.Stops == stops, $"{c.Name}: stops {stops}");
            Assert.True(c.Stops == (job.StatusCode == JobStatus.Paused), $"{c.Name}: job status {job.StatusCode}");
            Assert.True(c.Stops == (saves == 1), $"{c.Name}: saves {saves}");
            var prepared = item.ApiSteps.Any(s => s.State == BatchStepState.Prepared);
            Assert.True(c.Prepared == prepared, $"{c.Name}: prepared {prepared}");
            // a create's later steps address the created record when they are sent
            if (prepared)
                Assert.True(item.ApiSteps[1].Path == "{created}", $"{c.Name}: second step path {item.ApiSteps[1].Path}");
        }
    }

    [Fact]
    public async Task Diagnostic_evidence_lands_in_the_field_of_the_phase()
    {
        var kit = new ApiKit();
        async Task<ChangeItem> Handle(OperationsBatchExecutor.ExecutionPhase phase)
        {
            var item = ApiKit.Create(ResourceKind.Fleets, ("Name", "a"));
            var job = new BatchJob { Mode = RunMode.Online, Items = [item] };
            var run = new JobRun(kit.Api, kit.Workspace, job, new CheckpointSink(kit.Workspace, job, () => Task.CompletedTask, null, null, null), Ct);
            await new FailureHandler(run).HandleAsync(Response(HttpStatusCode.UnprocessableEntity), new ItemRun(item, phase));
            return item;
        }
        var writing = await Handle(OperationsBatchExecutor.ExecutionPhase.Writing);
        Assert.Equal("Writing", writing.DiagnosticDetails!.Phase);
        Assert.Null(writing.ReadbackDetails);
        Assert.Equal("body", writing.Diagnostic);
        var verifying = await Handle(OperationsBatchExecutor.ExecutionPhase.Verifying);
        Assert.Equal("Verifying", verifying.ReadbackDetails!.Phase);
        Assert.Null(verifying.DiagnosticDetails);
        var recovering = await Handle(OperationsBatchExecutor.ExecutionPhase.Recovering);
        Assert.Equal("Recovering", recovering.ReadbackDetails!.Phase);
    }
}
