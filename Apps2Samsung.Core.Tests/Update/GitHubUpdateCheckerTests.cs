using System.Net;
using System.Text;
using Apps2Samsung.Update;

namespace Apps2Samsung.Tests.Update;

/// <summary>
/// We ship 3.1 stable, then 3.2 beta, then 3.2 stable, then 3.3 beta. Someone with beta updates on
/// who runs the 3.2 beta must be offered the 3.2 stable once it is out.
/// </summary>
public class GitHubUpdateCheckerTests
{
    [Theory]
    [InlineData("v3.2.0", "v3.2.0-beta", true)]   // stable after its own beta
    [InlineData("v3.2.0-beta", "v3.2.0", false)]  // never "downgrade" to the beta
    [InlineData("v3.2.0-beta", "v3.2.0-beta", false)]
    [InlineData("v3.2.0", "v3.2.0", false)]
    [InlineData("v3.3.0-beta", "v3.2.0", true)]
    [InlineData("v3.2.0-beta", "v3.1.0", true)]
    [InlineData("v3.1.0", "v3.2.0-beta", false)]
    [InlineData("3.2.0", "v3.2.0-beta", true)]    // mobile reports no leading v
    [InlineData("v3.2", "v3.2.0", false)]
    [InlineData("v3.2.0-beta.2", "v3.2.0-beta", true)]
    public void IsVersionGreater_follows_release_order(string latest, string current, bool expected)
    {
        Assert.Equal(expected, GitHubUpdateChecker.IsVersionGreater(latest, current));
    }

    [Theory]
    [InlineData("v3.2.0-beta", true, true, "v3.2.0")]
    [InlineData("v3.2.0-beta", false, true, "v3.2.0")]
    [InlineData("v3.2.0", true, false, "v3.2.0")]
    [InlineData("v3.1.0", false, true, "v3.2.0")]
    public async Task Beta_user_is_offered_the_stable_of_the_same_number(
        string current, bool includeBetas, bool expectUpdate, string expectedLatest)
    {
        // Real order of the feed on 2026-11-09: newest stable on top, its beta right below.
        var checker = Checker(Feed(("v3.2.0", "v3.2.0"), ("v3.2.0-beta", "v3.2.0-beta"), ("v3.1.0", "v3.1.0")));

        var result = await checker.CheckForUpdateAsync(current, includePrereleases: includeBetas);

        Assert.True(result.IsSuccess);
        Assert.Equal(expectUpdate, result.IsUpdateAvailable);
        Assert.Equal(expectedLatest, result.LatestVersion);
    }

    [Fact]
    public async Task Highest_version_wins_over_feed_order()
    {
        // A beta re-published after the stable lands on top of the feed.
        var checker = Checker(Feed(("v3.2.0-beta", "v3.2.0-beta"), ("v3.2.0", "v3.2.0"), ("v3.1.0", "v3.1.0")));

        var result = await checker.CheckForUpdateAsync("v3.2.0-beta", includePrereleases: true);

        Assert.True(result.IsUpdateAvailable);
        Assert.Equal("v3.2.0", result.LatestVersion);
    }

    [Fact]
    public async Task Beta_user_gets_the_next_beta()
    {
        var checker = Checker(Feed(("v3.3.0-beta", "v3.3.0-beta"), ("v3.2.0", "v3.2.0"), ("v3.2.0-beta", "v3.2.0-beta")));

        var beta = await checker.CheckForUpdateAsync("v3.2.0", includePrereleases: true);
        var stable = await checker.CheckForUpdateAsync("v3.2.0", includePrereleases: false);

        Assert.True(beta.IsUpdateAvailable);
        Assert.Equal("v3.3.0-beta", beta.LatestVersion);
        Assert.False(stable.IsUpdateAvailable);
    }

    private static GitHubUpdateChecker Checker(string atom) =>
        new(new HttpClient(new StubHandler(atom)), "Apps2Samsung", "Apps2Samsung");

    private static string Feed(params (string Tag, string Title)[] entries)
    {
        var sb = new StringBuilder("""<?xml version="1.0" encoding="UTF-8"?><feed xmlns="http://www.w3.org/2005/Atom">""");
        foreach (var (tag, title) in entries)
        {
            sb.Append("<entry>")
              .Append($"<id>tag:github.com,2008:Repository/982647000/{tag}</id>")
              .Append("<updated>2026-11-09T12:00:00Z</updated>")
              .Append($"""<link rel="alternate" type="text/html" href="https://github.com/Apps2Samsung/Apps2Samsung/releases/tag/{tag}"/>""")
              .Append($"<title>{title}</title>")
              .Append("<content type=\"html\">&lt;p&gt;notes&lt;/p&gt;</content>")
              .Append("<author><name>PatrickSt1991</name></author>")
              .Append("</entry>");
        }
        return sb.Append("</feed>").ToString();
    }

    private sealed class StubHandler(string atom) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(atom, Encoding.UTF8, "application/atom+xml") });
    }
}
