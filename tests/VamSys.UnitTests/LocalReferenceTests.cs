using VamSys.Core;

namespace VamSys.UnitTests;

public class LocalReferenceTests
{
    static readonly Guid Id = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");

    [Theory]
    [InlineData("local:")]
    [InlineData("LOCAL:")]
    [InlineData("Local:")]
    [InlineData("lOcAl:")]
    public void IsLocal_ignores_case_of_the_prefix(string prefix)
    {
        Assert.True(LocalReference.IsLocal(prefix + Id));
        Assert.True(LocalReference.TryParse(prefix + Id, out var parsed));
        Assert.Equal(Id, parsed);
    }

    [Fact]
    public void TryParse_accepts_uppercase_hex_and_rejects_other_text()
    {
        Assert.True(LocalReference.TryParse("LOCAL:" + Id.ToString().ToUpperInvariant(), out var parsed));
        Assert.Equal(Id, parsed);
        Assert.False(LocalReference.TryParse("local:not-a-guid", out _));
        Assert.False(LocalReference.TryParse("local:", out _));
        Assert.False(LocalReference.TryParse(Id.ToString(), out _));
        Assert.False(LocalReference.TryParse("12", out _));
        Assert.False(LocalReference.TryParse(null, out _));
        Assert.False(LocalReference.IsLocal(null));
        Assert.False(LocalReference.IsLocal("EGLL"));
        Assert.False(LocalReference.IsLocal("xlocal:" + Id));
    }

    [Fact]
    public void Format_builds_the_lowercase_token()
        => Assert.Equal("local:" + Id, LocalReference.Format(Id));

    [Fact]
    public void ContainsLocal_finds_a_token_anywhere_in_a_list_regardless_of_case()
    {
        Assert.True(LocalReference.ContainsLocal("4,LOCAL:" + Id));
        Assert.True(LocalReference.ContainsLocal("local:" + Id));
        Assert.False(LocalReference.ContainsLocal("4,5"));
        Assert.False(LocalReference.ContainsLocal(null));
    }

    [Fact]
    public void Normalize_lowercases_only_the_prefix_of_each_token()
    {
        var upper = Id.ToString().ToUpperInvariant();
        Assert.Equal("4, local:" + upper + ",local:" + Id, LocalReference.Normalize("4, LOCAL:" + upper + ",Local:" + Id));
        Assert.Equal("local:" + Id, LocalReference.Normalize("local:" + Id));
        Assert.Equal("EGLL,5", LocalReference.Normalize("EGLL,5"));
        Assert.Equal("", LocalReference.Normalize(""));
    }
}
