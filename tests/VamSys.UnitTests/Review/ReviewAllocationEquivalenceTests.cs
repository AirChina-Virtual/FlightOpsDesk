using System.Text.Json;
using VamSys.Core;
using VamSys.TestSupport;
using static VamSys.UnitTests.Review.ReviewPlanFixture;

namespace VamSys.UnitTests.Review;

/// <summary>
/// 2026-11-23 (stage 7 item 2): the review plan copies less — validation skips the groupings that find nothing, the online
/// preview reuses one scratch item, the content key sorts without a delegate per row, a submission item creates its lists on
/// first use and changes of one batch edit share their field names. None of it may change what the review decides: these
/// tests pin everything it decides for fixed workspaces to what the program computed before the change (6292ec1).
/// </summary>
public class ReviewAllocationEquivalenceTests
{
    // SHA-256 of ReviewEquivalence.Describe (content key, every entry's handling, reason, fields and dependencies, the issues,
    // the validation issues, the submission items as saved and the requests they would send), from 6292ec1. Three Online cases changed
    // on purpose on 2026-11-24: their non-scheduled route changes skip the sample by the user's decision
    // (ApiOperationPolicy.ReleaseOf, UserDecided) and carry the ReviewUserDecided reason; with that turned off they still hash as before.
    static readonly Dictionary<string, string> Before = new()
    {
        ["mixed Demo"] = "C5F0A1F990F754AE7BBE8DAD91183124B268222103A9DEB3A0A795FF7903156B",
        ["mixed Online connected"] = "D964B3395B0754EB4981D7C4458DFA27013B43A3D9F0D17F14BFFF683C638574",
        ["mixed Online"] = "9482BC15C71CD5985A50B61C7D76DDB93F094055A7FA0D6B5C2DDD99271E6645",
        ["mixed Offline"] = "0CBC2182BE7FFDDA03A4D4C57AAF43E7A2DEAE780853EBF60B766CFE75E34194",
        ["large 500 rows 0 changes Demo"] = "1C40C6DAA0FE62B6058DB70FB18924DB64856C0811B67A4B8AF0F3D51F408630",
        ["large 500 rows 50 changes Demo"] = "3E771AA3E56EE4BB28F5DEAC1D3ACA6F82D35C502C3F7ECC0BBC160397CC4CC1",
        ["large 500 rows 500 changes Demo"] = "6388109BABA0A017A58CC6F55B9CA12B60EE13EC52D8DF5C5BCC6D0981B9B8BD",
        ["large 500 rows 0 changes Online"] = "BD4CF2F2BAB796DFC9D1645F0E192C22263E8F832F95F7628D0351DB5DECACF6",
        ["large 500 rows 50 changes Online"] = "892D1175D552F1FCC01D6E17DF9E5047D4E744CAB2893E43789CFA2EA85622E6",
        ["large 500 rows 500 changes Online"] = "2C6372AA4AA0C524E556D5A6E8218F0D1C342C13759B8D293ED4550D5CDE75E2",
    };

    public static TheoryData<string> Cases() => new(ReviewEquivalence.SmallCases().Select(c => c.Name));

    [Theory]
    [MemberData(nameof(Cases))]
    public void A_review_decides_exactly_what_it_decided_before(string name)
    {
        var describe = ReviewEquivalence.SmallCases().Single(c => c.Name == name).Describe;
        Assert.Equal(Before[name], ReviewEquivalence.Hash(describe()));
    }

    [Fact]
    public void The_mixed_workspace_reaches_every_rule_the_change_touched()
    {
        var review = ReviewEquivalence.Describe(ReviewEquivalence.Mixed(RunMode.Online), true, true);
        var codes = review["validation"]!.AsObject().SelectMany(p => JsonDocument.Parse(p.Value!.GetValue<string>()).RootElement.EnumerateArray())
            .Select(i => JsonDocument.Parse(i.GetProperty("Description").GetString()!).RootElement.GetProperty("Code").GetString()).ToHashSet();
        // Duplicate keys, a create without a key that shares a signature, unknown references (one in a list with spaces).
        Assert.Contains("Text_DA77CC5B62", codes);
        Assert.Contains("Text_CDDA93CCD1", codes);
        Assert.Contains("Text_5CDAFFB468", codes);
        var reasons = review["entries"]!.AsArray().Select(e => e!["reason"]?.GetValue<string>()).OfType<string>()
            .Select(r => JsonDocument.Parse(r).RootElement.GetProperty("Code").GetString()).ToHashSet();
        Assert.Superset(new HashSet<string?> { "ReviewValidationIssue", "ApiConflict", "ReviewOpenTaskItem", "ReviewDependency" }, reasons);
        Assert.Contains(review["entries"]!.AsArray(), e => e!["dependencies"]!.AsArray().Count > 0);
        Assert.NotEmpty(review["requests"]!.AsArray());
    }

    [Fact]
    public void Validation_without_duplicates_or_rows_without_a_key_reports_nothing_and_with_them_lists_them_in_order()
    {
        var w = new Workspace { Mode = RunMode.Demo };
        var data = w.Resources[ResourceKind.Airports];
        DataRow Airport(int n, string code) => Row(n, ("ICAO/IATA", code), ("Name", "Airport " + n));
        data.Snapshot.AddRange([Airport(1, "ZBAA"), Airport(2, "ZSPD")]);
        data.Draft.AddRange(data.Snapshot);
        Assert.Empty(Schemas.Validate(w, ResourceKind.Airports));

        // Two groups of duplicates (one of them written in lower case with spaces), listed group by group in row order;
        // two rows without a key with the same name, flagged after the row rules.
        data.Draft.AddRange([Airport(3, " zspd"), Airport(4, "ZBAA"), Airport(5, "ZSPD "), Row(6, ("ICAO/IATA", ""), ("Name", "Same")), Row(7, ("ICAO/IATA", " "), ("Name", "Same"))]);
        var issues = Schemas.Validate(w, ResourceKind.Airports);
        var duplicates = issues.Where(i => i.Description.Code == "Text_DA77CC5B62").Select(i => Number(i.RowId)).ToArray();
        Assert.Equal([1, 4, 2, 3, 5], duplicates);
        var unkeyed = issues.Where(i => i.Description.Code == "Text_CDDA93CCD1").Select(i => Number(i.RowId)).ToArray();
        Assert.Equal([6, 7], unkeyed);
        Assert.True(issues.FindIndex(i => i.Description.Code == "Text_CDDA93CCD1") > issues.FindLastIndex(i => i.Description.Code == "Text_DA77CC5B62"));
    }

    [Theory]
    [InlineData("1", new string[0])]
    [InlineData(" 1 ", new string[0])]
    [InlineData("", new string[0])]
    [InlineData("   ", new string[0])]
    [InlineData("9", new[] { "9" })]
    [InlineData(" 9 ", new[] { "9" })]
    [InlineData("1, 9 ,,8", new[] { "9", "8" })]
    [InlineData(",9", new[] { "9" })]
    public void A_reference_is_read_as_the_comma_separated_list_was_read(string fleets, string[] unknown)
    {
        var w = new Workspace { Mode = RunMode.Demo };
        var fleetsData = w.Resources[ResourceKind.Fleets];
        fleetsData.Snapshot.Add(Row(1, ("ID", "1"), ("Name", "Fleet"), ("Type Code", "B738"), ("Type (pax/cargo/...)", "pax"), ("Max Passengers", "189")));
        fleetsData.Draft.AddRange(fleetsData.Snapshot);
        var routes = w.Resources[ResourceKind.Routes];
        routes.Draft.Add(Row(2, ("ID", ""), ("Departure Airport (ICAO/IATA)", ""), ("Arrival Airport (ICAO/IATA)", ""), ("Type", "scheduled"), ("Fleet IDs", fleets)));
        var named = Schemas.Validate(w, ResourceKind.Routes).Where(i => i.Description.Code == "Text_5CDAFFB468" && i.Field == "Fleet IDs")
            .Select(i => i.Description.Arguments["arg0"].Text).ToArray();
        Assert.Equal(unknown, named);
    }

    [Fact]
    public void A_validation_pass_gives_the_same_key_whether_or_not_it_kept_it()
    {
        var pass = new ValidationPass(new Workspace());
        var plain = Row(1, ("ICAO/IATA", "ZBAA"));
        var spaced = Row(2, ("ICAO/IATA", " zbaa "));
        var empty = Row(3, ("Name", "none"));
        foreach (var _ in Enumerable.Range(0, 2))
        {
            Assert.Equal("ZBAA", pass.Key(ResourceKind.Airports, plain));
            Assert.Equal("ZBAA", pass.Key(ResourceKind.Airports, spaced));
            Assert.Equal("", pass.Key(ResourceKind.Airports, empty));
        }
        // A key that needed a copy is computed once: the same instance comes back.
        Assert.Same(pass.Key(ResourceKind.Airports, spaced), pass.Key(ResourceKind.Airports, spaced));
    }

    [Fact]
    public void The_online_preview_sees_each_change_as_its_own_item_would_show_it()
    {
        var w = Mixed();
        var changes = Planner.ScanWorkspace(w);
        var seen = new List<string>();
        static string Describe(ChangeItem item) => $"{item.Resource}|{item.Kind}|{item.Before?.LocalId}|{item.After.LocalId}|{item.RequiresAirlineParameter}|{item.RemoteId}|{item.Dependencies.Count}|"
            + string.Join(";", item.Fields.Select(f => $"{f.Key}={f.Value.Intent}:{f.Value.Value}"));
        var plan = ReviewPlanBuilder.BuildPlanned(changes, RunMode.Online, true, new HashSet<string>(), item => seen.Add(Describe(item)));
        // What the preview was shown is what a separate item per change (as before) shows, minus its dependencies.
        var expected = changes.Select(c => c.ToItem()).Select(i => Describe(new ChangeItem
        {
            Resource = i.Resource, Kind = i.Kind, Before = i.Before, After = i.After, RequiresAirlineParameter = i.RequiresAirlineParameter, Fields = i.Fields
        })).ToList();
        Assert.Equal(expected, seen);
        // The scratch item is not kept: the plan's submission items are the planned changes' own items.
        Assert.All(plan.SubmitItems, item => Assert.Contains(plan.Entries, e => ReferenceEquals(e.Planned.BuiltItem, item)));
    }

    [Fact]
    public void A_preview_that_throws_for_one_change_does_not_affect_the_next()
    {
        var w = Mixed();
        var changes = Planner.ScanWorkspace(w);
        var first = changes[0].After.LocalId;
        var plan = ReviewPlanBuilder.BuildPlanned(changes, RunMode.Online, true, new HashSet<string>(),
            item => { if (item.After.LocalId == first) throw MessageErrors.Attach(new InvalidOperationException("no"), Messages.Define("ApiNoChanges")); });
        Assert.Equal("ApiNoChanges", plan.Entries[0].Reason?.Code);
        Assert.All(plan.Entries.Skip(1), e => Assert.NotEqual("ApiNoChanges", e.Reason?.Code));
    }

    [Fact]
    public void A_submission_item_keeps_its_dependencies_and_is_saved_as_before()
    {
        var plan = Build(Mixed());
        var items = plan.SubmitItems;
        Assert.Contains(items, i => i.Dependencies.Count > 0);
        foreach (var entry in plan.Entries.Where(e => e.Planned.BuiltItem is not null))
            Assert.Equal(entry.Planned.DependencyIds, entry.Planned.BuiltItem!.Dependencies);

        // Lists created on first use are written as the empty lists they used to be, and read back the same.
        var fresh = new ChangeItem();
        var json = JsonSerializer.SerializeToNode(fresh)!.AsObject();
        foreach (var list in new[] { "Dependencies", "ApiSteps", "RecoveryCandidates", "VerificationDifferences" })
            Assert.Equal("[]", json[list]!.ToJsonString());
        Assert.Equal(Messages.Define("Text_637DA56123").ToString(), fresh.Message);
        var read = JsonSerializer.Deserialize<ChangeItem>(JsonSerializer.Serialize(items[0]))!;
        Assert.Equal(JsonSerializer.Serialize(items[0]), JsonSerializer.Serialize(read));
    }

    [Fact]
    public void Changes_that_share_their_field_names_keep_them_apart_when_one_resolves_a_reference()
    {
        var w = new Workspace { Mode = RunMode.Demo };
        var fleets = w.Resources[ResourceKind.Fleets];
        fleets.Draft.Add(Row(1, ("ID", ""), ("Name", "New"), ("Type Code", "A320"), ("Type (pax/cargo/...)", "pax"), ("Max Passengers", "150")));
        var routes = w.Resources[ResourceKind.Routes];
        DataRow Route(int n, string fleet) => Row(n, ("ID", n.ToString()), ("Fleet IDs", fleet), ("Remarks", "old"));
        routes.Snapshot.AddRange([Route(2, "1"), Route(3, Token(1)), Route(4, "1")]);
        routes.Draft.AddRange([With(routes.Snapshot[0], ("Remarks", "new")), With(routes.Snapshot[1], ("Remarks", "new")), With(routes.Snapshot[2], ("Remarks", "new"))]);
        var changes = Planner.ScanWorkspace(w).Where(c => c.Resource == ResourceKind.Routes).ToList();
        string Names(PlannedChange c) { var names = new string[c.FieldCount]; c.CopyFieldNames(names); return string.Join(",", names); }
        Assert.Equal(["Remarks", "Remarks,Fleet IDs", "Remarks"], changes.Select(Names).ToArray());
        Assert.Single(changes[1].DependencyIds);
    }
}
