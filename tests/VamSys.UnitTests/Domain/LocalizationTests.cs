using System.Collections;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using VamSys.Core;
using static VamSys.UnitTests.Domain.TestRows;

namespace VamSys.UnitTests.Domain;

/// <summary>
/// F22 / I10: language switching in the domain (service, structured messages, catalogs). The resource key and placeholder
/// lists themselves are locked by ResourceKeyInventoryTests; the host's missing-key log by LocalizationDiagnosticsTests.
/// What the store remembers as the language is storage (R3).
/// </summary>
public class LocalizationTests
{
    static Dictionary<string, string> Catalog(CultureInfo culture)
        => LocalizationService.Resources.GetResourceSet(culture, true, false)!.Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!);

    static string[] Slots(string text) => [.. Regex.Matches(text, @"\{\w+(?::[^}]+)?\}").Select(m => m.Value).Order(StringComparer.Ordinal)];

    // ---- catalogs ----

    [Fact]
    public void Both_catalogs_have_the_same_keys_and_a_sizeable_set()
    {
        var zh = Catalog(CultureInfo.InvariantCulture);
        var en = Catalog(CultureInfo.GetCultureInfo("en-US"));

        Assert.True(zh.Keys.ToHashSet().SetEquals(en.Keys));
        Assert.True(zh.Count >= 250);
    }

    [Fact]
    public void Every_text_has_the_same_named_placeholders_in_both_languages_and_english_has_no_chinese()
    {
        var zh = Catalog(CultureInfo.InvariantCulture);
        var en = Catalog(CultureInfo.GetCultureInfo("en-US"));

        foreach (var key in zh.Keys)
        {
            Assert.True(Slots(zh[key]).SequenceEqual(Slots(en[key])), "placeholders differ: " + key);
            Assert.False(string.IsNullOrWhiteSpace(en[key]), "empty English text: " + key);
            Assert.DoesNotContain(en[key], c => c is >= '一' and <= '鿿');
        }
    }

    // ---- the service ----

    [Fact]
    public void The_default_language_is_chinese_and_switching_notifies_only_on_a_real_change()
    {
        var loc = new LocalizationService();
        var events = 0;
        loc.LanguageChanged += (_, _) => events++;
        var culture = CultureInfo.CurrentCulture;

        Assert.Equal("zh-CN", loc.Language);
        loc.SetLanguage("en-US");
        loc.SetLanguage("en-US");

        Assert.Equal(1, events);
        Assert.Equal("en-US", loc.Language);
        Assert.Equal("Workspaces", loc.Get("Text_3797982942"));
        Assert.Equal(culture, CultureInfo.CurrentCulture);
        Assert.Equal("en-US", loc.DisplayCulture.Name);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("")]
    [InlineData("EN-us")]
    [InlineData("fr-FR")]
    public void Any_language_other_than_english_us_means_chinese(string language)
    {
        var loc = new LocalizationService();
        loc.SetLanguage("en-US");
        var events = 0;
        loc.LanguageChanged += (_, _) => events++;

        loc.SetLanguage(language);

        Assert.Equal("zh-CN", loc.Language);
        Assert.Equal(1, events);
        loc.SetLanguage("zh-CN");
        Assert.Equal(1, events);
    }

    [Fact]
    public void The_same_key_reads_differently_per_language_and_a_missing_key_shows_its_name_and_is_reported()
    {
        var loc = new LocalizationService();
        var zh = loc.Get("Text_3797982942");
        string? missing = null;
        loc.MissingResource += key => missing = key;

        Assert.NotEqual("Workspaces", zh);
        Assert.False(string.IsNullOrWhiteSpace(zh));
        Assert.Equal("[MissingTest]", loc.Get("MissingTest"));
        Assert.Equal("MissingTest", missing);
    }

    [Fact]
    public void Placeholders_are_filled_by_name_once_so_user_text_with_braces_is_never_reinterpreted()
    {
        var message = Messages.Define("Text_CCEF575C0D", ("arg0", Schemas.All[ResourceKind.Routes].Name), ("arg1", "001"), ("arg2", "我的航空公司 {arg5}"), ("arg3", ""), ("arg4", "abcd"), ("arg5", "Fleet IDs"));
        var restored = JsonSerializer.Deserialize<MessageDescriptor>(JsonSerializer.Serialize(message))!;
        var loc = new LocalizationService();
        loc.SetLanguage("en-US");

        var text = loc.Format(restored);

        Assert.Contains("Routes 001", text, StringComparison.Ordinal);
        Assert.Contains("我的航空公司 {arg5}", text, StringComparison.Ordinal);
        Assert.EndsWith("Fleet IDs", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_placeholder_without_an_argument_stays_visible()
    {
        var loc = new LocalizationService();
        loc.SetLanguage("en-US");

        Assert.Contains("{arg0}", loc.Format(Messages.Define("Text_CCEF575C0D")), StringComparison.Ordinal);
    }

    [Fact]
    public void A_literal_message_is_shown_as_written_in_either_language()
    {
        var loc = new LocalizationService();
        MessageDescriptor literal = "user typed {this}";

        Assert.Equal("user typed {this}", loc.Format(literal));
        loc.SetLanguage("en-US");
        Assert.Equal("user typed {this}", loc.Format(literal));
        Assert.Equal("user typed {this}", literal.ToString());
    }

    [Fact]
    public void A_nested_message_argument_is_formatted_in_the_current_language()
    {
        var inner = Schemas.All[ResourceKind.Airports].Name;
        var message = Messages.Define("Text_CCEF575C0D", ("arg0", inner), ("arg1", "1"), ("arg2", ""), ("arg3", ""), ("arg4", ""), ("arg5", "f"));
        var loc = new LocalizationService();

        var zh = loc.Format(message);
        loc.SetLanguage("en-US");
        var en = loc.Format(message);

        Assert.Contains(loc.Format(inner), en, StringComparison.Ordinal);
        Assert.NotEqual(zh, en);
    }

    // ---- structured messages and legacy text ----

    [Fact]
    public void Old_saved_text_maps_to_its_key_when_it_is_exactly_a_known_message_and_stays_literal_otherwise()
    {
        var loc = new LocalizationService();
        loc.SetLanguage("en-US");

        Assert.Equal("Verified by read-back", loc.Legacy("已回读核对"));
        Assert.Equal("历史外部错误 123", loc.Legacy("历史外部错误 123"));
        var old = Workspace.Deserialize("{\"Name\":\"用户名称\",\"Jobs\":[{\"Status\":\"旧的动态状态 42\",\"Items\":[{\"Message\":\"待执行\"}]}]}");
        Assert.Equal("用户名称", old.Name);
        Assert.Null(old.Jobs[0].Description);
        Assert.Equal("旧的动态状态 42", loc.Legacy(old.Jobs[0].Status));
    }

    [Fact]
    public void An_error_carries_its_message_descriptor_and_a_plain_error_is_described_from_its_text()
    {
        var descriptor = Messages.Define("Text_CCEF575C0D", ("arg0", "x"));
        var attached = MessageErrors.Attach(new InvalidOperationException("technical"), descriptor);

        Assert.Same(descriptor, MessageErrors.Describe(attached));
        Assert.Equal("Something odd", MessageErrors.Describe(new Exception("Something odd")).Literal);
        Assert.Equal("Text_140197D868", MessageErrors.Describe(new Exception(new LocalizationService().Get("Text_140197D868"))).Code);
    }

    [Fact]
    public void Message_arguments_keep_numbers_dates_messages_and_text_apart()
    {
        var date = new DateTimeOffset(2026, 10, 22, 8, 30, 0, TimeSpan.Zero);
        var nested = Messages.Define("Text_140197D868");

        Assert.Equal(7m, MessageArgument.From(7).Number);
        Assert.Equal(1.5m, MessageArgument.From(1.5d).Number);
        Assert.Equal(date, MessageArgument.From(date).Date);
        Assert.Equal(new DateTimeOffset(new DateTime(2026, 1, 2)), MessageArgument.From(new DateTime(2026, 1, 2)).Date);
        Assert.Same(nested, MessageArgument.From(nested).Message);
        Assert.Equal("abc", MessageArgument.From("abc").Text);
        Assert.Equal("", MessageArgument.From(null).Text);
    }

    [Fact]
    public void A_number_argument_is_formatted_for_the_display_culture()
    {
        var loc = new LocalizationService();
        var message = new MessageDescriptor { Code = "FormatProbe", Literal = "{n:N1}", Arguments = new() { ["n"] = MessageArgument.From(1234.5m) } };

        // No resource has this key, so the descriptor's literal is the template: the number is formatted for the language.
        Assert.Equal("1,234.5", loc.Format(message));
        loc.SetLanguage("en-US");
        Assert.Equal("1,234.5", loc.Format(message));
    }

    // ---- data rules do not depend on the language ----

    [Fact]
    public void CSV_validation_fleet_assignment_and_deletion_give_the_same_results_in_both_languages()
    {
        var w = new Workspace();
        CsvAdapter.Import(ResourceKind.Fleets, w.Resources[ResourceKind.Fleets], [Row(("ID", "01"), ("Name", "用户机型")), Row(("ID", "02"), ("Name", "Other"))], true);
        var routes = w.Resources[ResourceKind.Routes];
        CsvAdapter.Import(ResourceKind.Routes, routes, [Row(("ID", "001"), ("Type", "scheduled"), ("Fleet IDs", "01"), ("Unknown", "中文 {arg0},0001"))], true);
        var loc = new LocalizationService();
        var before = w.Serialize();
        var assign = new FleetAssignmentService();
        var csv = new CsvAdapter();
        var planner = new ChangePlanner();

        var p1 = assign.Preview(w, ResourceKind.Routes, routes.Draft, ["02"], FleetAssignmentMode.Add);
        var issues1 = Schemas.Validate(w, ResourceKind.Routes);
        loc.SetLanguage("en-US");
        var p2 = assign.Preview(w, ResourceKind.Routes, routes.Draft, ["02"], FleetAssignmentMode.Add);
        var issues2 = Schemas.Validate(w, ResourceKind.Routes);

        Assert.Equal(before, w.Serialize());
        Assert.Equal(p1.Rows[0].Get("Fleet IDs"), p2.Rows[0].Get("Fleet IDs"));
        Assert.Equal(issues1.Select(i => i.Description.Code), issues2.Select(i => i.Description.Code));

        assign.Apply(routes, p2);
        var english = csv.Export(ResourceKind.Routes, planner.Plan(ResourceKind.Routes, routes)).Single();
        loc.SetLanguage("zh-CN");
        Assert.Equal(english, csv.Export(ResourceKind.Routes, planner.Plan(ResourceKind.Routes, routes)).Single());

        var deletion = new DeletionService();
        var fleets = w.Resources[ResourceKind.Fleets];
        var a = deletion.Preview(w, ResourceKind.Fleets, fleets.Draft.Select(r => r.LocalId));
        loc.SetLanguage("en-US");
        var b = deletion.Preview(w, ResourceKind.Fleets, fleets.Draft.Select(r => r.LocalId));
        Assert.Equal(a.Issues.Count, b.Issues.Count);
        Assert.Equal(2, a.Issues.Count);
        Assert.DoesNotContain("仍被", loc.Format(b.Issues[0].Description), StringComparison.Ordinal);
    }

    [Fact]
    public void Fleet_picker_labels_translate_generated_annotations_but_never_the_users_fleet_names()
    {
        var w = new Workspace();
        w.Resources[ResourceKind.Fleets].Draft = [Row(("Name", "待删除"), ("Type Code", "A320"))];
        var options = new FleetAssignmentService().Options(w, ["missing"]);
        var loc = new LocalizationService();
        loc.SetLanguage("en-US");

        var created = options[0].Describe(loc);
        var unresolved = options[1].Describe(loc);

        Assert.Contains("待删除", created, StringComparison.Ordinal);
        Assert.Contains("Pending creation", created, StringComparison.Ordinal);
        Assert.Contains("Unresolved fleet", unresolved, StringComparison.Ordinal);
        Assert.Contains("Reference needs verification", unresolved, StringComparison.Ordinal);
    }
}
