using System.Net;
using System.Text.Json;
using VamSys.App;
using VamSys.Core;
using VamSys.Infrastructure;

static class TaskSubmissionScenarios
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    static ChangeItem Item() => new() { Resource = ResourceKind.Routes, Kind = ChangeKind.Delete };
    static async Task Blocked(Func<Task> action, string code)
    {
        try { await action(); }
        catch (InvalidOperationException error) when (MessageErrors.Describe(error).Code == code) { return; }
        throw new Exception("Expected " + code);
    }

    // UI boundaries are represented by callbacks; submission and start validation
    // are linked from the actual app. All persistence uses an isolated real store.
    sealed class Shell
    {
        public readonly Workspace Owner;
        public Workspace Current;
        public readonly WorkspaceStore Store;
        public readonly BatchJob History;
        public readonly List<string> Events = [];
        public List<ChangeItem>? Reviewed = [Item()];
        public bool Locked, Busy, Open = true, Querying, SkipSelection;
        public string Page = "review";
        public int Confirmations, Saves, Starts, ReviewWorkers;
        public TaskCompletionSource? BeforeUnlock;
        public readonly TaskCompletionSource Saved = Signal();
        public Action? AfterUnlock;
        public Func<Task>? SaveOverride;
        public Func<BatchJob, Task>? ExecuteOverride;
        public Shell(RunMode mode)
        {
            Owner = new() { Mode = mode, AirlineId = "7" }; Current = Owner;
            History = new() { Mode = mode, Items = [Item(), Item(), Item()] };
            Owner.Jobs.Add(History);
            Store = new(Path.Combine(Path.GetTempPath(), "flightops-submission-" + Guid.NewGuid().ToString("N")));
            Store.Save(Owner);
        }
        async Task LocalOperation(Func<Task> action)
        {
            if (Locked || Busy || !Open || Querying) return;
            Locked = true; Events.Add("lock");
            try { await action(); }
            finally
            {
                if (BeforeUnlock is { } pause) await pause.Task;
                Locked = false; Events.Add("unlock");
                // As in the window, rebuilding review immediately reacquires its
                // read lock. A deliberately slow worker makes the old race deterministic.
                Events.Add("render:" + Page);
                if (Page == "review") { ReviewWorkers++; Locked = true; }
                AfterUnlock?.Invoke();
            }
        }
        Task<List<ChangeItem>?> Confirm()
        {
            Check(Locked, "Confirmation ran outside the local operation");
            Confirmations++; return Task.FromResult(Reviewed);
        }
        async Task Save()
        {
            Check(Locked, "Pending save ran outside the local operation");
            Saves++; Events.Add("save");
            if (SaveOverride is { } save) await save(); else await Store.SaveAsync(Owner);
            Saved.TrySetResult();
        }
        void SelectTasks()
        {
            Check(Locked, "Task page selected after releasing the lock");
            Check(Store.Load(Owner.Id).Jobs.Count == Owner.Jobs.Count, "Selected task page before the job was durable");
            Events.Add("select:tasks"); if (!SkipSelection) Page = "tasks";
        }
        async Task Execute(BatchJob job)
        {
            TaskSubmission.RequireExecution(Current, job, Owner.Mode, Open && !Busy && !Locked && !Querying);
            Starts++; Events.Add("execute");
            Check(ReferenceEquals(job.Items, Reviewed), "Submission replaced the reviewed items");
            if (ExecuteOverride is { } execute) await execute(job);
        }
        public Task Submit() => TaskSubmission.SubmitAsync(Owner, Owner.Mode, LocalOperation, Confirm, Save,
            job => Store.Load(Owner.Id).Jobs.Any(j => j.Id == job.Id), SelectTasks, () => Current, Execute);
        public string StoredHistory() => JsonSerializer.Serialize(Store.Load(Owner.Id).Jobs.Single(j => j.Id == History.Id));
    }

    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("Online and demo submissions select tasks before unlock/render and explicitly start the saved job once", async () =>
        {
            foreach (var mode in new[] { RunMode.Online, RunMode.Demo })
            {
                var shell = new Shell(mode) { BeforeUnlock = Signal() };
                var history = shell.StoredHistory();
                var pending = shell.Submit();
                await shell.Saved.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Check(shell.Starts == 0 && shell.Locked, "Execution began before save/unlock");
                shell.BeforeUnlock.SetResult(); await pending;
                Check(shell.Events.SequenceEqual(new[] { "lock", "save", "select:tasks", "unlock", "render:tasks", "execute" }), "Incorrect handoff order");
                Check(shell.Starts == 1 && shell.ReviewWorkers == 0, "A review render swallowed the startup");
                Check(shell.StoredHistory() == history, "Historical Pending delete job changed");
                var stored = shell.Store.Load(shell.Owner.Id).Jobs.Single(j => j.Id != shell.History.Id);
                Check(stored.StatusCode == JobStatus.Pending && stored.Items.All(i => i.State == ItemState.Pending), "Startup bypassed persistence");
            }
        });
        await test("A review render reacquiring the lock explicitly rejects startup and never retries when unlocked", async () =>
        {
            var shell = new Shell(RunMode.Online) { SkipSelection = true };
            await Blocked(shell.Submit, "TaskStartBlocked");
            Check(shell.ReviewWorkers == 1 && shell.Starts == 0, "Regression control did not reproduce the locked handoff");
            shell.Locked = false;
            Check(shell.Starts == 0 && shell.Store.Load(shell.Owner.Id).Jobs.All(j => j.Items.All(i => i.State == ItemState.Pending)), "Unlock automatically restarted a task");
        });
        await test("Canceled or empty review and rejected local operation create no job or startup intent", async () =>
        {
            foreach (var empty in new List<ChangeItem>?[] { null, [] })
            {
                var shell = new Shell(RunMode.Online) { Reviewed = empty }; var before = shell.StoredHistory();
                await shell.Submit(); shell.Locked = false;
                Check(shell.Saves == 0 && shell.Starts == 0 && shell.Owner.Jobs.Count == 1 && !shell.Events.Contains("select:tasks"), "Canceled submission started or saved a job");
                Check(shell.StoredHistory() == before, "Cancel modified historical task");
            }
            var busy = new Shell(RunMode.Online) { Busy = true }; await busy.Submit();
            Check(busy.Confirmations == 0 && busy.Starts == 0 && busy.Owner.Jobs.Count == 1, "Declined local operation was submitted");
        });
        await test("Pending-save failure or stale revision cannot select tasks or start execution", async () =>
        {
            foreach (var stale in new[] { false, true })
            {
                var shell = new Shell(RunMode.Online); var history = shell.StoredHistory();
                if (stale) { var other = shell.Store.Load(shell.Owner.Id); other.Name = "Changed elsewhere"; shell.Store.Save(other); }
                else shell.SaveOverride = () => throw new IOException("Injected save failure");
                try { await shell.Submit(); throw new Exception("Failed save was accepted"); }
                catch (IOException) when (!stale) { }
                catch (InvalidOperationException e) when (stale && MessageErrors.Describe(e).Code == "StorageStale") { }
                Check(shell.Starts == 0 && !shell.Events.Contains("select:tasks") && shell.Store.Load(shell.Owner.Id).Jobs.Count == 1, "Failed save acquired startup intent");
                Check(shell.StoredHistory() == history, "Failed save changed an existing task");
                Check(shell.Owner.Jobs.Count == 1 && shell.Owner.Jobs[0] == shell.History, "Unsaved job remained in memory for a later save");
                shell.SaveOverride = null; shell.Locked = false;
                if (!stale) { await shell.Store.SaveAsync(shell.Owner); Check(shell.Store.Load(shell.Owner.Id).Jobs.Count == 1, "Later save persisted the failed submission"); }
            }
        });
        await test("Failure after a Pending commit leaves a durable task and reloading never starts it", async () =>
        {
            var shell = new Shell(RunMode.Online);
            shell.SaveOverride = async () => { await shell.Store.SaveAsync(shell.Owner); throw new IOException("Injected completion failure"); };
            try { await shell.Submit(); throw new Exception("Completion failure was accepted"); } catch (IOException) { }
            var reload = shell.Store.Load(shell.Owner.Id);
            Check(reload.Jobs.Count == 2 && reload.Jobs.All(j => j.Items.All(i => i.State == ItemState.Pending)), "Durable Pending job was lost");
            Check(shell.Owner.Jobs.Count == 2, "Durable job was dropped from memory");
            Check(shell.Starts == 0 && !shell.Events.Contains("select:tasks"), "Unacknowledged save started execution");
        });
        await test("Workspace, airline, mode or availability changes during handoff block the explicit saved job", async () =>
        {
            foreach (var change in new[] { "instance", "airline", "mode", "lock", "busy", "query", "closed" })
            {
                var shell = new Shell(RunMode.Online);
                shell.AfterUnlock = () =>
                {
                    if (change == "instance") shell.Current = new() { Id = shell.Owner.Id, Mode = RunMode.Online, AirlineId = "7" };
                    if (change == "airline") shell.Owner.AirlineId = "8";
                    if (change == "mode") shell.Owner.Mode = RunMode.Offline;
                    if (change == "lock") shell.Locked = true;
                    if (change == "busy") shell.Busy = true;
                    if (change == "query") shell.Querying = true;
                    if (change == "closed") shell.Open = false;
                };
                await Blocked(shell.Submit, "TaskStartBlocked");
                Check(shell.Starts == 0 && shell.Store.Load(shell.Owner.Id).Jobs.Count == 2, "Context change launched a saved task");
            }
        });
        await test("Unknown review skips items an earlier step resolved, reviews replacements and recovers once at the end", async () =>
        {
            ChangeItem Unknown() => new() { Resource = ResourceKind.Routes, Kind = ChangeKind.Create, State = ItemState.Unknown };
            var job = new BatchJob { Mode = RunMode.Online, Items = [Unknown(), Unknown(), Unknown(), Item()] };
            var (first, resolved, replaced) = (job.Items[0], job.Items[1], job.Items[2]);
            var replacement = JsonSerializer.Deserialize<ChangeItem>(JsonSerializer.Serialize(replaced))!;
            var reviewed = new List<ChangeItem>(); var events = new List<string>();
            await UnknownReview.RunAsync(job, item =>
            {
                reviewed.Add(item); events.Add("review");
                if (item == first) { resolved.State = ItemState.Succeeded; job.Items[2] = replacement; }
                return Task.FromResult(true);
            }, () => { events.Add("recover"); return Task.CompletedTask; });
            Check(reviewed.SequenceEqual(new[] { first, replacement }), "Review used a stale or resolved item");
            Check(events.SequenceEqual(new[] { "review", "review", "recover" }), "Recovery ran per item or before all reviews");
            var none = 0;
            await UnknownReview.RunAsync(job, _ => Task.FromResult(false), () => { none++; return Task.CompletedTask; });
            Check(none == 0, "Recovery ran without a saved recovery ID");
        });
        await test("Task execution guards reject unavailable, foreign and mismatched jobs with localized startup reasons", async () =>
        {
            var shell = new Shell(RunMode.Online);
            foreach (var candidate in new[]
            {
                (shell.History, RunMode.Online, false),
                (new BatchJob { Id = shell.History.Id, Mode = RunMode.Online }, RunMode.Online, true),
                (shell.History, RunMode.Demo, true)
            })
                await Blocked(() => { TaskSubmission.RequireExecution(shell.Owner, candidate.Item1, candidate.Item2, candidate.Item3); return Task.CompletedTask; }, "TaskStartBlocked");
            foreach (var language in new[] { "zh-CN", "en-US" })
            {
                var text = new LocalizationService(); text.SetLanguage(language);
                Check(!text.Get("TaskStartBlocked").Contains("[TaskStartBlocked]"), "Missing startup localization");
            }
        });
        await test("Actual online submission/checkpoints delete only the explicit synthetic routes once; Unknown resumes only read", async () =>
        {
            var shell = new Shell(RunMode.Online); using var server = new RouteServer { KeepDeletedRows = true }; using var client = new HttpClient(server);
            var time = InstantTime.Requests();
            OperationsAdapter Adapter(Workspace workspace) => new(new OperationsTransport(client, OperationsAdapter.BaseUri, "submission-fixture",
                new TokenProvider(_ => Task.FromResult(new AccessToken("synthetic-token", time.Clock.GetUtcNow().AddHours(1))), time.Clock), time), workspace);
            var api = Adapter(shell.Owner);
            foreach (var (id, code) in new[] { (101, "EGLL"), (102, "EGKK") })
            {
                using var airport = JsonDocument.Parse($$"""{"id":{{id}},"airline_id":7,"icao":"{{code}}"}""");
                api.FromJson(ResourceKind.Airports, airport.RootElement);
            }
            shell.Reviewed = [];
            foreach (var id in new[] { 201, 202, 203 })
            {
                var raw = $$"""{"id":{{id}},"departure_id":101,"arrival_id":102,"type":"scheduled","callsign":"TST{{id}}","flight_number":"TS{{id}}","fleet_ids":[4]}""";
                server.Rows.Add("routes/" + id, raw);
                using var doc = JsonDocument.Parse(raw); var before = api.FromJson(ResourceKind.Routes, doc.RootElement); var after = before.Copy(); after.Fields["_delete"] = "TRUE";
                shell.Owner.Resources[ResourceKind.Routes].Snapshot.Add(before.Copy()); shell.Owner.Resources[ResourceKind.Routes].Draft.Add(after.Copy());
                shell.Reviewed.Add(new() { Resource = ResourceKind.Routes, Kind = ChangeKind.Delete, Before = before, After = after });
            }
            await shell.Store.SaveAsync(shell.Owner); var history = shell.StoredHistory();
            shell.ExecuteOverride = job => new OperationsBatchExecutor(api, shell.Owner).ExecuteAsync(job, shell.Store.CheckpointWriter(shell.Owner), CancellationToken.None);
            await shell.Submit();
            var saved = shell.Store.Load(shell.Owner.Id); var target = saved.Jobs.Single(j => j.Id != shell.History.Id);
            Check(shell.Starts == 1 && server.Deletes.SequenceEqual(new[] { "routes/201", "routes/202", "routes/203" }), "Submission duplicated or expanded writes");
            Check(target.Items.All(i => i.State == ItemState.Unknown && i.WriteAccepted && i.ApiSteps.Count == 1), "Soft-delete checkpoints/evidence missing");
            Check(shell.StoredHistory() == history, "Explicit startup changed historical Pending tasks");
            // Simulate restart and explicit read-only recovery with the real executor.
            await new OperationsBatchExecutor(Adapter(saved), saved).ExecuteAsync(target, shell.Store.CheckpointWriter(saved), CancellationToken.None);
            Check(server.Deletes.Count == 3 && target.Items.All(i => i.State == ItemState.Unknown), "Unknown recovery resubmitted DELETE");
            Check(shell.StoredHistory() == history, "Recovery changed historical Pending tasks");
        });
    }

    sealed class RouteServer : HttpMessageHandler
    {
        public bool KeepDeletedRows;
        public readonly Dictionary<string, string> Rows = [];
        public readonly List<string> Deletes = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath["/api/v3/operations/".Length..];
            if (request.Method == HttpMethod.Get)
                return Task.FromResult(ScriptedHttp.Json(Rows.TryGetValue(path, out var row) ? "{\"data\":" + row + "}" : "{}",
                    Rows.ContainsKey(path) ? HttpStatusCode.OK : HttpStatusCode.NotFound));
            Check(request.Method == HttpMethod.Delete && Rows.ContainsKey(path), "Unexpected fixture write");
            Deletes.Add(path); if(!KeepDeletedRows)Rows.Remove(path);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }
    }
}
