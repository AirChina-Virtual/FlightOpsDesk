using VamSys.Core;
using static VamSys.UnitTests.Review.ReviewPlanFixture;

namespace VamSys.UnitTests.Review;

public class ReviewContentKeyTests
{
    static string Key(Action<Workspace>? change = null, bool connected = true, ISet<string>? verified = null, Action<ChangeItem>? preview = null)
    {
        var w = Mixed();
        change?.Invoke(w);
        return Build(w, connected, verified, preview).ContentKey();
    }

    [Fact]
    public void Identical_inputs_give_one_key_even_though_planner_ids_are_random()
    {
        var w = Mixed();
        var first = Build(w).ContentKey();
        Assert.Equal(first, Build(w).ContentKey());
        Assert.Equal(first, Build(Mixed()).ContentKey());
        Assert.Equal(first, Build(w).ContentKey());
        Assert.Matches("^[0-9A-F]{64}$", first);
    }

    [Fact]
    public void The_shown_plan_and_a_later_plan_of_an_unchanged_workspace_agree_with_the_list_overload()
    {
        var w = Mixed();
        var viaWorkspace = Build(w);
        var viaList = ReviewPlanBuilder.Build(Planner.PlanWorkspace(w), RunMode.Online, true, new HashSet<string>(), _ => { }, [], w.ContractHash);
        Assert.Equal(viaList.ContentKey(), viaWorkspace.ContentKey());
        // A plan rebuilt from the shown plan's entries (as the UI verification does) hashes the same.
        Assert.Equal(viaWorkspace.ContentKey(), new WorkspaceReviewPlan(viaWorkspace.Entries, viaWorkspace.Blockers).ContentKey());
    }

    public static TheoryData<string> Changes() => new(Variations.Keys);

    static readonly Dictionary<string, Action<Workspace>> Variations = new()
    {
        ["changed field value"] = w => Edit(w, ResourceKind.Routes, 30, false, ("Remarks", "other text")),
        ["changed field cleared"] = w => Edit(w, ResourceKind.Routes, 30, false, ("Remarks", "")),
        ["unchanged field of a changed row, in the new row"] = w => { Edit(w, ResourceKind.Routes, 30, false, ("Altitude", "35000")); Edit(w, ResourceKind.Routes, 30, true, ("Altitude", "35000")); },
        ["field only on the baseline row"] = w => Edit(w, ResourceKind.Routes, 30, true, ("Legacy", "x")),
        ["field only on the new row"] = w => Edit(w, ResourceKind.Routes, 30, false, ("Extra", "x")),
        ["baseline value of a changed field"] = w => Edit(w, ResourceKind.Routes, 30, true, ("Remarks", "older")),
        ["row identity remote id"] = w => Replace(w, ResourceKind.Routes, 30, false, r => r.WithIdentity(new("1", "7"))),
        ["row identity connection id"] = w => Replace(w, ResourceKind.Routes, 30, false, r => r.WithIdentity(new("1", "8"))),
        ["row identity parent fleet"] = w => Replace(w, ResourceKind.Routes, 30, false, r => r.WithIdentity(new("1", "7", "3"))),
        ["raw api json"] = w => Replace(w, ResourceKind.Routes, 30, false, r => r.WithRawApiJson("{\"id\":1}")),
        ["row id"] = w => { Replace(w, ResourceKind.Routes, 30, false, r => r.WithLocalId(Id(99))); Replace(w, ResourceKind.Routes, 30, true, r => r.WithLocalId(Id(99))); },
        ["a change reverted"] = w => Edit(w, ResourceKind.Routes, 31, false, ("Remarks", "r31")),
        ["a change added"] = w => Edit(w, ResourceKind.Airports, 3, false, ("Name", "Guangzhou Baiyun")),
        ["kind becomes delete"] = w => Edit(w, ResourceKind.Routes, 31, false, ("_delete", "TRUE")),
        ["a new row"] = w => w.Resources[ResourceKind.Routes].Draft.Add(Row(60, ("Callsign", "QA0060"), ("Type", "scheduled"), ("_delete", "FALSE"))),
        ["entry order"] = w => { var d = w.Resources[ResourceKind.Routes].Draft; (d[0], d[1]) = (d[1], d[0]); },
        ["dependency target"] = w => Edit(w, ResourceKind.Routes, 36, false, ("Fleet IDs", Token(12) + "," + Token(10))),
        ["dependency dropped"] = w => Edit(w, ResourceKind.Routes, 36, false, ("Fleet IDs", Token(10))),
        ["unknown local reference"] = w => Edit(w, ResourceKind.Routes, 36, false, ("Fleet IDs", Token(11) + "," + Token(77))),
        ["resolved reference text"] = w => { Edit(w, ResourceKind.Fleets, 10, false, ("ID", "5")); Edit(w, ResourceKind.Fleets, 10, true, ("ID", "5")); },
        ["pending job item on a changed record"] = w =>
        {
            var route = w.Resources[ResourceKind.Routes].Draft.Single(r => r.LocalId == Id(30));
            w.Jobs.Add(new BatchJob { Mode = RunMode.Online, Items = [new ChangeItem { State = ItemState.Pending, Resource = ResourceKind.Routes, Kind = ChangeKind.Update, Before = route, After = route }] });
        },
        ["contract hash with independent evidence"] = w => w.ContractHash = IndependentApiEvidence.ContractHash,
    };

    [Theory]
    [MemberData(nameof(Changes))]
    public void Any_change_that_affects_the_submission_changes_the_key(string name)
    {
        var baseline = Key();
        Assert.NotEqual(baseline, Key(Variations[name]));
    }

    [Fact]
    public void Connection_state_verification_preview_result_and_mode_change_the_key()
    {
        var baseline = Key();
        Assert.NotEqual(baseline, Key(connected: false));
        Assert.NotEqual(baseline, Key(verified: new HashSet<string> { "Routes:Update" }));
        Assert.NotEqual(baseline, Key(preview: item => { if (Number(item.After.LocalId) == 31) throw MessageErrors.Attach(new InvalidOperationException("x"), Messages.Define("ApiRefreshFirst")); }));
        Assert.NotEqual(baseline, Key(preview: item => { if (Number(item.After.LocalId) == 31) throw MessageErrors.Attach(new InvalidOperationException("x"), Messages.Define("ApiMoveSeparate")); }));
        var offline = Mixed(RunMode.Offline);
        Assert.NotEqual(baseline, ReviewPlanBuilder.BuildForWorkspace(Planner, offline, RunMode.Offline, false, new HashSet<string>(), null).ContentKey());
        var demo = ReviewPlanBuilder.Build(Planner.PlanWorkspace(Mixed(RunMode.Demo)), RunMode.Demo, false, new HashSet<string>());
        Assert.NotEqual(baseline, demo.ContentKey());
        Assert.NotEqual(Key(connected: false), demo.ContentKey());
    }

    [Fact]
    public void Different_blockers_change_the_key_even_when_every_entry_is_blocked_alike()
    {
        var changes = Planner.PlanWorkspace(Mixed());
        string With(params string[] codes) => ReviewPlanBuilder.Build(changes, RunMode.Online, true, new HashSet<string>(), _ => { }, [.. codes.Select(c => Messages.Define(c))]).ContentKey();
        var none = With();
        var one = With("ApiConflict");
        var other = With("ApiPendingJob");
        var two = With("ApiConflict", "ApiPendingJob");
        Assert.Equal(4, new[] { none, one, other, two }.Distinct().Count());
    }

    [Fact]
    public void Item_state_that_planning_overrides_does_not_leak_into_the_key()
    {
        var w = Mixed();
        var first = Build(w).ContentKey();
        // The plan is a pure function of the workspace: building it never edits rows, so a second build is unaffected by the first.
        var snapshot = w.Resources[ResourceKind.Routes].Draft.Select(r => string.Join("|", r.Fields.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value))).ToList();
        _ = Build(w).SubmitItems;
        Assert.Equal(snapshot, w.Resources[ResourceKind.Routes].Draft.Select(r => string.Join("|", r.Fields.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value))));
        Assert.Equal(first, Build(w).ContentKey());
    }
}
