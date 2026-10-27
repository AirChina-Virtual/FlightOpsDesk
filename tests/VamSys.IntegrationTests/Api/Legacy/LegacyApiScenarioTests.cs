using VamSys.TestSupport;

namespace VamSys.IntegrationTests.Api.Legacy;

/// <summary>
/// The scenarios of the former legacy runner (API basics, contract audit, audit and re-audit regressions, lifecycle),
/// one xunit case per scenario. Each case runs exactly the scenario of its name; a failed assertion throws.
/// </summary>
public class LegacyApiScenarioTests
{
    delegate Task Group(Func<string, Func<Task>, Task> test);

    static readonly Dictionary<string, Group> Groups = new()
    {
        ["Api"] = ApiScenarios.Run,
        ["Contract"] = ContractScenarios.Run,
        ["Audit"] = AuditScenarios.Run,
        ["Reaudit"] = ReauditScenarios.Run,
        ["Lifecycle"] = LifecycleScenarios.Run,
    };

    public static IEnumerable<object[]> Cases()
    {
        foreach (var (group, run) in Groups)
        {
            var names = new List<string>();
            run((name, _) => { names.Add(name); return Task.CompletedTask; }).GetAwaiter().GetResult();
            foreach (var name in names)
                yield return [group, name];
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Scenario(string group, string name)
    {
        var ran = false;
        await Groups[group]((n, body) =>
        {
            if (n != name) return Task.CompletedTask;
            ran = true;
            return body();
        });
        Assert.True(ran, "Scenario not found: " + name);
    }
}
