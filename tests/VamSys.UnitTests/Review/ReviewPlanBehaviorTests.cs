using VamSys.Core;
using static VamSys.UnitTests.Review.ReviewPlanFixture;

namespace VamSys.UnitTests.Review;

public class ReviewPlanBehaviorTests
{
    // Locks the review as it was before the plan stopped copying rows.

    [Fact]
    public void Online_connected_without_verification_allows_three_per_operation_and_defers_dependents()
    {
        var plan = Build(Mixed());
        Assert.Equal(
            """
            Airports Update #1 Ready - deps[] airlineParam=False fields[Name=Set:Beijing Capital]
            Airports Delete #2 Ready - deps[] airlineParam=False fields[]
            Airports Create #4 Ready - deps[] airlineParam=False fields[ICAO/IATA=Set:ZUUU,Name=Set:Chengdu]
            Fleets Create #11 Ready - deps[] airlineParam=True fields[Name=Set:Beta 1,Type Code=Set:B738,Type (pax/cargo/...)=Set:pax,Max Passengers=Set:180]
            Fleets Create #12 Ready - deps[] airlineParam=True fields[Name=Set:Beta 2,Type Code=Set:B738,Type (pax/cargo/...)=Set:pax,Max Passengers=Set:180]
            Fleets Create #13 Ready - deps[] airlineParam=True fields[Name=Set:Beta 3,Type Code=Set:B738,Type (pax/cargo/...)=Set:pax,Max Passengers=Set:180]
            Fleets Create #14 Deferred ReviewSample deps[] airlineParam=True fields[Name=Set:Beta 4,Type Code=Set:B738,Type (pax/cargo/...)=Set:pax,Max Passengers=Set:180]
            Aircraft Update #20 Ready - deps[] airlineParam=False fields[Fleet ID=Set:2]
            Aircraft Update #21 Ready - deps[] airlineParam=False fields[Name=Set:renamed]
            Routes Update #30 Ready - deps[] airlineParam=False fields[Remarks=Set:edited 30]
            Routes Update #31 Ready - deps[] airlineParam=False fields[Remarks=Set:edited 31]
            Routes Update #32 Ready - deps[] airlineParam=False fields[Remarks=Set:edited 32]
            Routes Update #33 Deferred ReviewSample deps[] airlineParam=False fields[Remarks=Set:edited 33]
            Routes Update #34 Ready - deps[] airlineParam=False fields[Fleet IDs=Set:1,2]
            Routes Delete #35 Ready - deps[] airlineParam=False fields[]
            Routes Create #36 Ready - deps[11] airlineParam=False fields[ID=Clear:,Callsign=Set:QA0036,Flight Number=Set:0036,Type=Set:scheduled,Departure Airport (ICAO/IATA)=Set:ZBAA,Arrival Airport (ICAO/IATA)=Set:ZGGG,Remarks=Set:r36,Altitude=Set:33000,Fleet IDs=Set:local:0000000b-0000-0000-0000-000000000000,1]
            Routes Create #37 Ready - deps[] airlineParam=False fields[ID=Clear:,Callsign=Set:QA0037,Flight Number=Set:0037,Type=Set:jumpseat,Departure Airport (ICAO/IATA)=Set:ZBAA,Arrival Airport (ICAO/IATA)=Set:ZGGG,Remarks=Set:r37,Altitude=Set:33000,Fleet IDs=Clear:]
            Routes Create #38 Deferred ReviewDependency deps[14] airlineParam=False fields[ID=Clear:,Callsign=Set:QA0038,Flight Number=Set:0038,Type=Set:scheduled,Departure Airport (ICAO/IATA)=Set:ZBAA,Arrival Airport (ICAO/IATA)=Set:ZGGG,Remarks=Set:r38,Altitude=Set:33000,Fleet IDs=Set:local:0000000e-0000-0000-0000-000000000000]
            blockers[]
            """.ReplaceLineEndings("\n"), Describe(plan).ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Verified_operations_lift_the_sample_limit_and_dependency_deferral()
    {
        var plan = Build(Mixed(), verified: new HashSet<string> { "Routes:Update", "Fleets:Create" });
        Assert.Equal(["Ready"], plan.Entries.Where(e => e.Change.Resource == ResourceKind.Routes && e.Change.Kind == ChangeKind.Update).Select(e => e.Disposition.ToString()).Distinct());
        Assert.All(plan.Entries, e => Assert.Equal(ReviewDisposition.Ready, e.Disposition));
        Assert.Equal(plan.Entries.Count, plan.SubmitItems.Count);
    }

    [Fact]
    public void Not_connected_waits_for_connection_in_Online_and_Offline_but_not_in_Demo()
    {
        foreach (var mode in new[] { RunMode.Online, RunMode.Offline })
        {
            var plan = ReviewPlanBuilder.BuildForWorkspace(Planner, Mixed(mode), mode, false, new HashSet<string>(), null);
            Assert.All(plan.Entries, e => { Assert.Equal(ReviewDisposition.AwaitingConnection, e.Disposition); Assert.Equal("ReviewConnect", e.Reason!.Code); });
            Assert.Empty(plan.SubmitItems);
        }
        var demo = ReviewPlanBuilder.Build(Planner.PlanWorkspace(Mixed(RunMode.Demo)), RunMode.Demo, false, new HashSet<string>());
        Assert.All(demo.Entries, e => { Assert.Equal(ReviewDisposition.Ready, e.Disposition); Assert.Null(e.Reason); });
        Assert.Equal(demo.Entries.Count, demo.SubmitItems.Count);
    }

    // 3.1 (D6): a validation problem blocks only the change of its row, in Demo and Online alike; the page lists it.
    [Theory]
    [InlineData(RunMode.Demo)]
    [InlineData(RunMode.Online)]
    public void A_validation_issue_blocks_only_its_own_entry_and_what_depends_on_it(RunMode mode)
    {
        var w = Mixed(mode);
        Edit(w, ResourceKind.Fleets, 11, false, ("Type Code", ""));
        var plan = mode == RunMode.Demo ? ReviewPlanBuilder.BuildForWorkspace(Planner, w, mode, false, new HashSet<string>(), null) : Build(w);
        Assert.Empty(plan.Blockers);
        var blocked = Assert.Single(plan.Entries, e => e.Disposition == ReviewDisposition.Blocked);
        Assert.Equal((11, "ReviewValidationIssue"), (Number(blocked.Change.After.LocalId), blocked.Reason!.Code));
        Assert.Equal("ReviewValidationIssue", Assert.Single(plan.Issues).Code);
        // route 36 names the new fleet 11, so it waits for it
        var dependent = plan.Entries.Single(e => Number(e.Change.After.LocalId) == 36);
        Assert.Equal((ReviewDisposition.Deferred, "ReviewDependency"), (dependent.Disposition, dependent.Reason!.Code));
        Assert.NotEmpty(plan.SubmitItems);
        Assert.DoesNotContain(plan.SubmitItems, i => Number(i.After.LocalId) is 11 or 36);
    }

    [Fact]
    public void A_validation_issue_on_a_row_that_did_not_change_blocks_nothing()
    {
        var w = Mixed();
        Edit(w, ResourceKind.Fleets, 15, false, ("Max Passengers", "-1"));
        Edit(w, ResourceKind.Fleets, 15, true, ("Max Passengers", "-1"));
        var plan = Build(w);
        Assert.Empty(plan.Issues);
        Assert.DoesNotContain(plan.Entries, e => e.Disposition == ReviewDisposition.Blocked);
    }

    [Fact]
    public void A_valid_Demo_workspace_has_no_blockers_and_submits_everything()
    {
        var w = new Workspace { Mode = RunMode.Demo };
        var airport = Row(1, ("ICAO/IATA", "ZBAA"), ("Name", "Beijing"), ("_delete", "FALSE"));
        w.Resources[ResourceKind.Airports].Snapshot = [airport];
        w.Resources[ResourceKind.Airports].Draft = [With(airport, ("Name", "Beijing Capital"))];
        var plan = ReviewPlanBuilder.BuildForWorkspace(Planner, w, RunMode.Demo, false, new HashSet<string>(), null);
        Assert.Empty(plan.Blockers);
        Assert.Equal(["Airports Update Ready"], plan.Entries.Select(e => $"{e.Change.Resource} {e.Change.Kind} {e.Disposition}"));
        Assert.Single(plan.SubmitItems);
    }

    [Fact]
    public void Preview_errors_block_only_their_entry_and_Demo_or_disconnected_never_previews()
    {
        var calls = 0;
        var plan = Build(Mixed(), preview: item =>
        {
            calls++;
            if (Number(item.After.LocalId) == 31)
                throw MessageErrors.Attach(new InvalidOperationException("x"), Messages.Define("ApiRefreshFirst"));
        });
        Assert.Equal(plan.Entries.Count, calls); // every entry is previewed before any sample limit applies
        var blocked = plan.Entries.Single(e => e.Disposition == ReviewDisposition.Blocked);
        Assert.Equal(31, Number(blocked.Change.After.LocalId));
        Assert.Equal("ApiRefreshFirst", blocked.Reason!.Code);
        Assert.DoesNotContain(blocked.Change, plan.SubmitItems);

        calls = 0;
        _ = ReviewPlanBuilder.Build(Planner.PlanWorkspace(Mixed(RunMode.Demo)), RunMode.Demo, true, new HashSet<string>(), _ => calls++);
        _ = ReviewPlanBuilder.Build(Planner.PlanWorkspace(Mixed()), RunMode.Online, false, new HashSet<string>(), _ => calls++);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Global_blockers_still_block_every_ready_entry_with_the_first_blocker_and_empty_the_submission()
    {
        var plan = ReviewPlanBuilder.BuildPlanned(Planner.ScanWorkspace(Mixed()), RunMode.Online, true, new HashSet<string>(), _ => { }, [Messages.Define("ApiPendingJob")]);
        Assert.Equal(["ApiPendingJob"], plan.Blockers.Select(b => b.Code));
        Assert.Equal(["Blocked", "Deferred"], plan.Entries.Select(e => e.Disposition.ToString()).Distinct().Order());
        Assert.All(plan.Entries.Where(e => e.Disposition == ReviewDisposition.Blocked), e => Assert.Same(plan.Blockers[0], e.Reason));
        Assert.Empty(plan.SubmitItems);
    }

    // 3.1 (D7): an unfinished online task holds back only changes of the same record (row, remote id or key).
    [Theory]
    [InlineData(ItemState.Pending, true)]
    [InlineData(ItemState.Unknown, true)]
    [InlineData(ItemState.Succeeded, false)]
    public void An_unfinished_task_item_blocks_only_changes_of_its_own_record(ItemState state, bool blocks)
    {
        var w = Mixed();
        var route = w.Resources[ResourceKind.Routes].Draft.Single(r => r.LocalId == Id(30));
        var elsewhere = new DataRow { Fields = new Dictionary<string, string> { ["ICAO/IATA"] = "ZZZZ", ["Name"] = "Elsewhere" } };
        w.Jobs.Add(new BatchJob { Mode = RunMode.Online, Items =
        [
            new ChangeItem { State = state, Resource = ResourceKind.Routes, Kind = ChangeKind.Update, Before = route, After = route },
            new ChangeItem { State = ItemState.Unknown, Resource = ResourceKind.Airports, Kind = ChangeKind.Create, After = elsewhere }
        ] });
        var plan = Build(w);
        Assert.Empty(plan.Blockers);
        var blocked = plan.Entries.Where(e => e.Disposition == ReviewDisposition.Blocked).Select(e => (Number(e.Change.After.LocalId), e.Reason!.Code)).ToArray();
        Assert.Equal(blocks ? [(30, "ReviewOpenTaskItem")] : [], blocked);
        Assert.Equal(blocks ? ["ReviewOpenTaskItems"] : [], plan.Issues.Select(i => i.Code));
        Assert.NotEmpty(plan.SubmitItems);
    }

    // 3.1 (D7): a conflict blocks only its own row's change.
    [Fact]
    public void A_conflict_blocks_only_the_change_of_its_row()
    {
        var w = Mixed();
        w.Resources[ResourceKind.Routes].Conflicts.Add(new(Id(31), "Remarks", "r31", "edited 31", "server 31"));
        var plan = Build(w);
        Assert.Empty(plan.Blockers);
        var blocked = Assert.Single(plan.Entries, e => e.Disposition == ReviewDisposition.Blocked);
        Assert.Equal((31, "ApiConflict"), (Number(blocked.Change.After.LocalId), blocked.Reason!.Code));
        Assert.Equal(["ApiConflict"], plan.Issues.Select(i => i.Code));
        Assert.NotEmpty(plan.SubmitItems);
    }

    [Fact]
    public void A_dependency_on_a_deferred_create_defers_transitively()
    {
        var plan = Build(Mixed());
        var deferred = plan.Entries.Where(e => e.Disposition == ReviewDisposition.Deferred).Select(e => (Number(e.Change.After.LocalId), e.Reason!.Code)).ToArray();
        Assert.Equal([(14, "ReviewSample"), (33, "ReviewSample"), (38, "ReviewDependency")], deferred);
    }

    [Fact]
    public void Plan_matches_the_planner_and_submit_items_are_the_ready_planner_items()
    {
        foreach (var verified in new HashSet<string>[] { [], ["Routes:Update", "Fleets:Create"] })
        {
            var w = Mixed();
            var plan = Build(w, verified: verified);
            var expected = Planner.PlanWorkspace(w);
            Assert.Equal(expected.Count, plan.Entries.Count);
            for (var n = 0; n < expected.Count; n++)
            {
                Assert.Equal(expected[n].Resource, plan.Entries[n].Change.Resource);
                Assert.Equal(expected[n].Kind, plan.Entries[n].Change.Kind);
                Assert.Equal(expected[n].After.LocalId, plan.Entries[n].Change.After.LocalId);
            }
            var ready = expected.Where((_, n) => plan.Entries[n].Disposition == ReviewDisposition.Ready).ToList();
            Assert.Equal(Persisted(ready, expected), Persisted(plan.SubmitItems, plan.Entries.Select(e => e.Change).ToList()));
            // The items carry dependency IDs of items that are themselves submitted.
            var submitted = plan.SubmitItems.Select(i => i.Id).ToHashSet();
            Assert.All(plan.SubmitItems.SelectMany(i => i.Dependencies), id => Assert.Contains(id, submitted));
        }
    }

    [Fact]
    public void Submit_items_are_stable_objects_with_the_dependencies_the_page_showed()
    {
        var plan = Build(Mixed());
        var first = plan.SubmitItems;
        var second = plan.SubmitItems;
        Assert.Equal(first.Count, second.Count);
        for (var n = 0; n < first.Count; n++)
            Assert.Same(first[n], second[n]);
        var fleet = first.Single(i => Number(i.After.LocalId) == 11);
        Assert.Equal([fleet.Id], first.Single(i => Number(i.After.LocalId) == 36).Dependencies);
        Assert.Same(plan.Entries.Single(e => Number(e.Change.After.LocalId) == 11).Change, fleet);
    }

    [Fact]
    public void Items_passed_to_Build_are_the_ones_submitted()
    {
        var changes = Planner.PlanWorkspace(Mixed(RunMode.Demo));
        var plan = ReviewPlanBuilder.Build(changes, RunMode.Demo, false, new HashSet<string>());
        Assert.Equal(changes.Count, plan.SubmitItems.Count);
        for (var n = 0; n < changes.Count; n++)
            Assert.Same(changes[n], plan.SubmitItems[n]);
        Assert.Same(changes[3], plan.Entries[3].Change);
    }

    [Fact]
    public void Reference_fields_are_resolved_on_the_planned_row_without_touching_the_workspace()
    {
        var w = Mixed();
        var route = w.Resources[ResourceKind.Routes].Draft.Single(r => r.LocalId == Id(36));
        var before = route.Get("Fleet IDs");
        var item = Build(w).Entries.Single(e => Number(e.Change.After.LocalId) == 36).Change;
        Assert.Equal(before, route.Get("Fleet IDs"));
        Assert.Equal(Token(11) + "," + Token(10), before);
        Assert.Equal(Token(11) + ",1", item.After.Get("Fleet IDs"));
        Assert.Equal(item.After.Get("Fleet IDs"), item.Fields["Fleet IDs"].Value);
        Assert.Equal(FieldIntent.Set, item.Fields["Fleet IDs"].Intent);
    }

    [Fact]
    public void Planned_rows_do_not_alias_the_workspace_rows_when_persisted()
    {
        var w = Mixed();
        var item = Build(w).SubmitItems.Single(i => Number(i.After.LocalId) == 30);
        Assert.Equal("edited 30", item.After.Get("Remarks"));
        Assert.Equal("r30", item.Before!.Get("Remarks"));
        item.After = item.After.With("Remarks", "mutated by a job");
        item.Before = item.Before.With("Remarks", "mutated by a job");
        Assert.Equal("edited 30", w.Resources[ResourceKind.Routes].Draft.Single(r => r.LocalId == Id(30)).Get("Remarks"));
        Assert.Equal("r30", w.Resources[ResourceKind.Routes].Snapshot.Single(r => r.LocalId == Id(30)).Get("Remarks"));
    }
}
