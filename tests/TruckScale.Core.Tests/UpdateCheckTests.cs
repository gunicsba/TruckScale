namespace TruckScale.Core.Tests;

public class UpdateCheckTests
{
    [Theory]
    [InlineData("v1.2.0", "1.2.0")]
    [InlineData("1.2", "1.2.0")]
    [InlineData("v2", "2.0.0")]
    [InlineData("1.1.0-dev.5+abc123", "1.1.0")]
    [InlineData("1.0.0+5f2c1e", "1.0.0")]
    public void Versions_are_read_from_tags(string tag, string expected) =>
        Assert.Equal(Version.Parse(expected), UpdateCheck.Parse(tag));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("latest")]
    public void Not_a_version(string? tag) => Assert.Null(UpdateCheck.Parse(tag));

    [Fact]
    public void Only_a_higher_version_is_newer()
    {
        var current = new Version(1, 1, 0);
        Assert.True(UpdateCheck.IsNewer(current, new Version(1, 1, 1)));
        Assert.True(UpdateCheck.IsNewer(current, new Version(2, 0)));
        Assert.False(UpdateCheck.IsNewer(current, new Version(1, 1, 0)));
        Assert.False(UpdateCheck.IsNewer(new Version(1, 1, 0, 0), new Version(1, 1)));
        Assert.False(UpdateCheck.IsNewer(current, new Version(1, 0, 9)));
    }

    [Fact]
    public void GitHub_answer_is_parsed()
    {
        var r = UpdateCheck.ParseLatest("""{"tag_name":"v1.3.0","html_url":"https://github.com/x/y/releases/tag/v1.3.0","draft":false,"prerelease":false}""")!;
        Assert.Equal(new Version(1, 3, 0), r.Version);
        Assert.EndsWith("/v1.3.0", r.Url);
        Assert.Null(UpdateCheck.ParseLatest("""{"tag_name":"v1.4.0","prerelease":true}"""));
        Assert.Null(UpdateCheck.ParseLatest("""{"message":"Not Found"}"""));
    }
}
