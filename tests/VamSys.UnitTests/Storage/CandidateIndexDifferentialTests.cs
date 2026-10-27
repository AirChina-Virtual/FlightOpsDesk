using VamSys.Core;
using VamSys.Infrastructure;

namespace VamSys.UnitTests.Storage;

/// <summary>
/// The candidate index (duplicate detection by name signature) against a plain dictionary, with a fixed seed: after every one of
/// 6000 random puts and removals, every name is looked up (66000 queries) and must find exactly the rows a linear search finds,
/// case-insensitively, each row once, and never a row changed after it was put.
/// </summary>
public class CandidateIndexDifferentialTests
{
    [Fact]
    public void The_index_finds_exactly_what_a_linear_search_finds_after_every_mutation()
    {
        var rng = new Random(20260922);
        var names = new[] { "Same", "SAME", "Renamed", "old", "Old", "A|B", "A\nB", "中文", "007", "7", "" };
        foreach (var kind in new[] { ResourceKind.Fleets, ResourceKind.Aircraft, ResourceKind.Airports })
        {
            var index = new CandidateIndex();
            var baseline = new Dictionary<string, string[]>();
            for (var step = 0; step < 2000; step++)
            {
                var id = rng.Next(1, 101).ToString();
                if (rng.Next(4) == 0)
                {
                    index.Remove(id);
                    baseline.Remove(id);
                }
                else
                {
                    string[] keys = kind == ResourceKind.Airports ? [names[rng.Next(names.Length)], names[rng.Next(names.Length)]] : [names[rng.Next(names.Length)]];
                    var row = new DataRow { Identity = new(id, "7"), Fields = new Dictionary<string, string> { ["Name"] = keys[0] } };
                    index.Put(row, keys.Select(n => new CandidateSignature(kind, n)));
                    baseline[id] = keys.ToArray();
                }
                foreach (var name in names)
                {
                    var expected = baseline.Where(p => p.Value.Any(n => StringComparer.OrdinalIgnoreCase.Equals(n, name))).Select(p => p.Key).ToHashSet();
                    var actual = index.Find(new(kind, name)).ToList();
                    Assert.True(!actual.Any(r => r.Get("Name") == "mutated after put")
                        && actual.Count == actual.Select(r => r.Identity!.RemoteId).Distinct().Count()
                        && expected.SetEquals(actual.Select(r => r.Identity!.RemoteId)), $"Differential {kind} step {step}");
                }
            }
        }
    }
}
