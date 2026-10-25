using Microsoft.Data.Sqlite;
using VamSys.App;
using VamSys.Core;
using VamSys.Infrastructure;

static class UiInteractionScenarios
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static DataRow Row(params (string, string)[] values) => new() { Fields = values.ToDictionary(p => p.Item1, p => p.Item2) };
    static Task Sync(Action action) { action(); return Task.CompletedTask; }
    static ChangeItem Change(ResourceKind resource, int n) => new() { Resource = resource, Kind = ChangeKind.Create,
        After = Row(("Name", "row " + n)), Fields = new() { ["Name"] = new(FieldIntent.Set, "row " + n) } };

    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("Workspace review includes multiple resources and preserves local dependencies", () => Sync(() =>
        {
            var w = new Workspace { Mode = RunMode.Demo };
            var fleet = Row(("Name", "Fleet")); var aircraft = Row(("Fleet ID", "local:" + fleet.LocalId));
            w.Resources[ResourceKind.Fleets].Draft.Add(fleet); w.Resources[ResourceKind.Aircraft].Draft.Add(aircraft);
            w.Resources[ResourceKind.Airports].Draft.Add(Row(("ICAO/IATA", "EGLL")));
            var changes = new ChangePlanner().PlanWorkspace(w);
            var plan = ReviewPlanBuilder.Build(changes, RunMode.Demo, false, new HashSet<string>());
            Check(plan.SubmitItems.Count == 3, "Review omitted a resource");
            Check(changes.Single(c => c.Resource == ResourceKind.Aircraft).Dependencies.Single() == changes.Single(c => c.Resource == ResourceKind.Fleets).Id, "Dependency missing");
            Check(aircraft.Get("Fleet ID") == "local:" + fleet.LocalId, "Preview modified the draft");
            Check(ReferenceEquals(plan.SubmitItems[0], changes[0]), "Submission did not use reviewed objects");
        }));
        await test("Review applies API blocks, three samples and transitive dependency deferrals", () => Sync(() =>
        {
            var airports = Enumerable.Range(0, 5).Select(n => Change(ResourceKind.Airports, n)).ToArray();
            var routing = Change(ResourceKind.Routings, 9); routing.Dependencies.Add(airports[3].Id);
            var route = Change(ResourceKind.Routes, 10); route.Dependencies.Add(routing.Id);
            var blocked = Change(ResourceKind.Fleets, 11);
            var plan = ReviewPlanBuilder.Build([..airports, routing, route, blocked], RunMode.Online, true, new HashSet<string>(), c =>
            { if (c == blocked) throw new InvalidOperationException("unsupported"); });
            Check(plan.SubmitItems.Count == 3, "Sample/dependency scope changed");
            Check(plan.Entries.Count(e => e.Disposition == ReviewDisposition.Deferred) == 4, "Transitive deferrals missing");
            Check(plan.Entries.Single(e => e.Change == blocked).Disposition == ReviewDisposition.Blocked, "Unsupported item was allowed");
        }));
        await test("Verified operations are not capped and disconnected review never invokes API preview", () => Sync(() =>
        {
            var changes = Enumerable.Range(0, 5).Select(n => Change(ResourceKind.Fleets, n)).ToArray(); var calls = 0;
            var plan = ReviewPlanBuilder.Build(changes, RunMode.Online, true, new HashSet<string> { "Fleets:Create" }, _ => calls++);
            Check(plan.SubmitItems.Count == 5 && calls == 5, "Verified operation was capped");
            plan = ReviewPlanBuilder.Build(changes, RunMode.Offline, false, new HashSet<string>(), _ => calls++);
            Check(plan.SubmitItems.Count == 0 && calls == 5 && plan.Entries.All(e => e.Disposition == ReviewDisposition.AwaitingConnection), "Disconnected preview called API");
        }));
        await test("Review content identity ignores planner IDs but detects fields, dependencies and eligibility", () => Sync(() =>
        {
            var w = new Workspace(); var fleet = Row(("Name", "A"));
            w.Resources[ResourceKind.Fleets].Draft.Add(fleet);
            w.Resources[ResourceKind.Aircraft].Draft.Add(Row(("Fleet ID", "local:" + fleet.LocalId)));
            WorkspaceReviewPlan Build() => ReviewPlanBuilder.Build(new ChangePlanner().PlanWorkspace(w), RunMode.Online, true, new HashSet<string>(), _ => { });
            var original = Build(); Check(original.ContentKey() == Build().ContentKey(), "Random IDs invalidated equivalent review");
            w.Resources[ResourceKind.Fleets].ReplaceDraftRows([fleet.With("Name", "B")]); Check(original.ContentKey() != Build().ContentKey(), "Changed field was not detected");
            var changes = new ChangePlanner().PlanWorkspace(w);
            var before = ReviewPlanBuilder.Build(changes, RunMode.Online, true, new HashSet<string>(), _ => { }).ContentKey();
            changes.Single(c => c.Resource == ResourceKind.Aircraft).Dependencies.Clear();
            Check(before != ReviewPlanBuilder.Build(changes, RunMode.Online, true, new HashSet<string>(), _ => { }).ContentKey(), "Changed dependency was not detected");
            Check(before != ReviewPlanBuilder.Build(changes, RunMode.Online, true, new HashSet<string>(), _ => { }, [Messages.Define("ApiConflict")]).ContentKey(), "Global block was not detected");
            // Hashed key: values moving between adjacent fields or into a field name must not collide.
            string Key(params (string, string)[] fields) { var x = new Workspace(); x.Resources[ResourceKind.Fleets].Draft.Add(Row(fields)); return ReviewPlanBuilder.Build(new ChangePlanner().PlanWorkspace(x), RunMode.Online, true, new HashSet<string>(), _ => { }).ContentKey(); }
            Check(Key(("Name", "ab"), ("Type Code", "")) != Key(("Name", "a"), ("Type Code", "b")), "Adjacent values collided");
            Check(Key(("Name", "a")) != Key(("Namea", "")), "Field name and value collided");
            var cached = Build(); Check(ReferenceEquals(cached.ContentKey(), cached.ContentKey()) && cached.ContentKey().Length == 64, "Key not cached or not SHA-256");
        }));
        await test("Workspace conflict or pending job blocks every ready item", () => Sync(() =>
        {
            foreach (var code in new[] { "ApiConflict", "ApiPendingJob" })
            {
                var plan = ReviewPlanBuilder.Build([Change(ResourceKind.Airports, 1), Change(ResourceKind.Fleets, 2)], RunMode.Online, true, new HashSet<string>(), _ => { }, [Messages.Define(code)]);
                Check(plan.SubmitItems.Count == 0 && plan.Entries.All(e => e.Disposition == ReviewDisposition.Blocked), "Global blocker did not stop the workspace");
            }
        }));
        await test("Actual API review constructs requests without token acquisition or network traffic", () => Sync(() =>
        {
            var w = new Workspace { AirlineId = "7" }; w.Resources[ResourceKind.Airports].Draft.Add(Row(("ICAO/IATA", "EGLL"), ("Name", "London")));
            var handler = new FakeHttp(); using var client = new HttpClient(handler); var tokens = 0;
            var api = new OperationsAdapter(new OperationsTransport(client, OperationsAdapter.BaseUri, "review",
                new TokenProvider(_ => { tokens++; throw new Exception("Preview requested token"); }), InstantTime.Requests()), w);
            var plan = ReviewPlanBuilder.Build(new ChangePlanner().PlanWorkspace(w), RunMode.Online, true, new HashSet<string>(), c => api.PreviewPlan(c));
            Check(plan.SubmitItems.Count == 1 && handler.Calls == 0 && tokens == 0, "Preview sent requests or blocked a valid record");
        }));
        await test("Batch rules require selection and skip deleted rows without touching unselected rows", () => Sync(() =>
        {
            var normal = Row(("Name", "old")); var deleted = Row(("Name", "deleted"), ("_delete", "TRUE")); var unselected = Row(("Name", "untouched"));
            var data = new ResourceData { Draft = [normal, deleted, unselected] };
            var empty = BatchEditPreview.Rule(BatchSelection.Capture([]), new(RuleKind.Set, "Name", "new")); empty.Apply(data);
            Check(data.Undo.Count == 0 && normal.Get("Name") == "old", "Empty selection changed data");
            var preview = BatchEditPreview.Rule(BatchSelection.Capture([normal, deleted]), new(RuleKind.Set, "Name", "new"));
            Check(preview.Selection.Selected.Count == 2 && preview.Selection.Skipped == 1 && preview.Changes.Count == 1, "Wrong mixed counts");
            preview.Apply(data); Check(data.FindDraftRow(normal.LocalId)!.Get("Name") == "new" && deleted.Get("Name") == "deleted" && unselected.Get("Name") == "untouched", "Scope escaped selection");
            data.UndoEdit(); Check(data.FindDraftRow(normal.LocalId)!.Get("Name") == "old", "Batch undo broken");
        }));
        await test("Deletion removes marked new drafts and skips already marked existing rows", () => Sync(() =>
        {
            var draft = Row(("Name", "new"), ("_delete", "TRUE"));
            var deleted = Row(("ID", "1"), ("_delete", "TRUE")); var active = Row(("ID", "2"));
            var w = new Workspace(); var data = w.Resources[ResourceKind.Aircraft];
            data.Draft = [draft, deleted, active]; data.Snapshot = [deleted.Dup(), active.Dup()];
            var selection = BatchSelection.ForDeletion(data.Draft, data.Snapshot);
            Check(selection.Eligible.Count == 2 && selection.Skipped == 1, "Deletion eligibility changed");
            var service = new DeletionService(); var plan = service.Preview(w, ResourceKind.Aircraft, selection.Eligible.Select(r => r.LocalId));
            Check(plan.RemoveDrafts.Single() == draft.LocalId && plan.MarkExisting.Single() == active.LocalId, "Deletion preview counts changed");
            service.Apply(w, plan);
            Check(data.FindDraftRow(draft.LocalId) is null && data.Draft.All(Schemas.Deleted), "Deletion failed to remove a marked new draft");
            data.UndoEdit(); Check(data.Draft.Count == 3 && !Schemas.Deleted(data.FindDraftRow(active.LocalId)!), "Deletion undo changed unaffected markers");
        }));
        await test("No-op batch previews do not create history and partial no-ops are counted", () => Sync(() =>
        {
            var a = Row(("Name", "same")); var b = Row(("Name", "old")); var data = new ResourceData { Draft = [a, b] };
            var preview = BatchEditPreview.Rule(BatchSelection.Capture([a]), new(RuleKind.Set, "Name", "same")); preview.Apply(data);
            Check(preview.Unchanged == 1 && data.Undo.Count == 0, "No-op created a history step");
            preview = BatchEditPreview.Rule(BatchSelection.Capture([a, b]), new(RuleKind.Set, "Name", "same"));
            Check(preview.Unchanged == 1 && preview.Changes.Count == 1 && b.Get("Name") == "old", "Partial preview mutated source or miscounted");
            preview.Apply(data); Check(data.Undo.Count == 1 && data.Draft.All(r => r.Get("Name") == "same"), "Partial apply failed");
        }));
        await test("Restore selects only deleted records and a retirement preview keeps its captured date", () => Sync(() =>
        {
            var a = Row(("End Date", "")); var b = Row(("_delete", "TRUE"));
            var restore = BatchSelection.Capture([a, b], restore: true);
            Check(restore.Eligible.Count == 1 && restore.Eligible[0] == b && restore.Skipped == 1, "Restore scope incorrect");
            var data = new ResourceData { Draft = [a] };
            var preview = BatchEditPreview.Rule(BatchSelection.Capture([a]), new(RuleKind.Retire, "Name", ""));
            var date = preview.Changes.Single().Get("End Date"); preview.Apply(data);
            Check(date.Length == 19 && data.Draft[0].Get("End Date") == date, "Retirement date changed between preview and apply");
        }));
        await test("Cross-page selection feeds only selected row identities into batch changes", () => Sync(() =>
        {
            var rows = Enumerable.Range(0, 501).Select(n => Row(("ID", n.ToString()), ("Tags", ""))).ToArray();
            var model = new DataTableViewModel();
            model.Apply(DataTableViewModel.Query(rows.Select(r => new DataQueryRow(r.LocalId, [], "", false, false)).ToArray(), "", 0, false, CancellationToken.None), "", 0, null);
            model.Selected.Add(rows[0].LocalId); model.SetPage(2); model.Selected.Add(rows[200].LocalId);
            var preview = BatchEditPreview.Rule(BatchSelection.Capture(rows.Where(r => model.Selected.Contains(r.LocalId))), new(RuleKind.Set, "Tags", "selected"));
            Check(preview.Changes.Count == 2 && preview.Changes.Select(r => r.LocalId).ToHashSet().SetEquals(model.Selected), "Cross-page targets changed");
        }));
        await test("Density preference defaults, persists and normalizes without changing workspace data", () => Sync(() =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "flightops-density-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new WorkspaceStore(directory); var w = new Workspace(); store.Save(w); var before = store.Load(w.Id).Serialize();
                Check(store.LoadTableDensity() == "comfortable", "Default density changed");
                store.SaveTableDensity("compact"); Check(new WorkspaceStore(directory).LoadTableDensity() == "compact", "Density did not persist");
                store.SaveTableDensity("invalid"); Check(store.LoadTableDensity() == "comfortable", "Invalid density was not normalized");
                Check(store.Load(w.Id).Serialize() == before, "Preference changed workspace data");
            }
            finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        }));
    }
}
