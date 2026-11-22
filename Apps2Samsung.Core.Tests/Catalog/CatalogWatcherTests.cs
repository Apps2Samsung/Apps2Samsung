using System.Net;
using System.Text.Json;
using Apps2Samsung.Catalog;
using Apps2Samsung.Models;
using Apps2Samsung.Helpers.Core;

namespace Apps2Samsung.Tests.Catalog;

// The watcher behind "always the latest catalog" (#1157): true only when the server has a catalog
// generated after the one the list on screen was built from.
public class CatalogWatcherTests
{
    private static string Snapshot(DateTimeOffset generatedAt) =>
        JsonSerializer.Serialize(new ResolvedCatalog { GeneratedAt = generatedAt }, JsonSerializerOptionsProvider.Default);

    private sealed class Server : HttpMessageHandler
    {
        public string Body { get; set; } = string.Empty;
        public bool Down { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Down
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Body) });
    }

    private static (CatalogWatcher Watcher, ResolvedCatalogService Service, Server Server, Action<TimeSpan> Wait) Setup(DateTimeOffset start)
    {
        var now = start;
        var server = new Server { Body = Snapshot(start.AddMinutes(-5)) };
        var service = new ResolvedCatalogService(new HttpClient(server), cachePath: null, clock: () => now);
        return (new CatalogWatcher(service), service, server, span => now += span);
    }

    [Fact]
    public async Task Nothing_is_newer_before_a_list_was_shown()
    {
        var (watcher, _, _, _) = Setup(DateTimeOffset.UtcNow);

        Assert.False(await watcher.HasNewerAsync());
    }

    [Fact]
    public async Task The_same_catalog_is_not_newer()
    {
        var (watcher, service, _, wait) = Setup(DateTimeOffset.UtcNow);
        Assert.NotNull(await service.GetAsync());
        watcher.MarkShown();

        wait(CatalogWatcher.Interval);

        Assert.False(await watcher.HasNewerAsync());
    }

    [Fact]
    public async Task A_catalog_generated_later_is_newer_until_it_is_shown()
    {
        var start = DateTimeOffset.UtcNow;
        var (watcher, service, server, wait) = Setup(start);
        await service.GetAsync();
        watcher.MarkShown();

        wait(CatalogWatcher.Interval);
        server.Body = Snapshot(start.AddMinutes(10));

        Assert.True(await watcher.HasNewerAsync());

        watcher.MarkShown();
        Assert.False(await watcher.HasNewerAsync());
    }

    // A refresh by hand (#1162) does not wait out the ten minutes a download is reused for.
    [Fact]
    public async Task A_refresh_asked_for_goes_to_the_server_at_once()
    {
        var start = DateTimeOffset.UtcNow;
        var (watcher, service, server, _) = Setup(start);
        await service.GetAsync();
        watcher.MarkShown();
        server.Body = Snapshot(start.AddMinutes(-1));

        Assert.False(await watcher.HasNewerAsync());

        watcher.Invalidate();
        Assert.True(await watcher.HasNewerAsync());
    }

    [Fact]
    public async Task A_server_that_is_down_is_not_news()
    {
        var (watcher, service, server, wait) = Setup(DateTimeOffset.UtcNow);
        await service.GetAsync();
        watcher.MarkShown();

        wait(CatalogWatcher.Interval);
        server.Down = true;

        Assert.False(await watcher.HasNewerAsync());
    }
}
