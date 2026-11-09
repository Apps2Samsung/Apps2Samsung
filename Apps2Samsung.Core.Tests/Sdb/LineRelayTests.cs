using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Apps2Samsung.Sdb;

namespace Apps2Samsung.Tests.Sdb;

/// <summary>
/// The install log's lines reach the progress handler off the writer's thread (#975). The writer is
/// the console tee, which runs under .NET's Console.Out lock on Linux and Android; the CLI's handler
/// takes its own lock, and the spinner that also holds that lock writes to stderr, which takes
/// Console.Out's lock. Called inline, the two waited on each other and the install froze.
/// </summary>
public class LineRelayTests
{
    [Fact]
    public async Task A_poster_holding_a_lock_never_waits_on_an_observer_that_needs_it()
    {
        var consoleLock = new object();
        var seen = new List<string>();
        var relay = new InProcessSdbEngine.LineRelay(line =>
        {
            lock (consoleLock)
                seen.Add(line);
        });

        var posted = Task.Run(() =>
        {
            // The tee posts while it holds the lock the observer wants: inline, this never returns.
            lock (consoleLock)
            {
                for (var i = 0; i < 100; i++)
                    relay.Post($"installing[{i}]");
                Thread.Sleep(200);
            }
        });

        Assert.True(posted.Wait(5000), "posting blocked on the observer");
        await relay.DisposeAsync();
        Assert.Equal(100, seen.Count);
    }

    [Fact]
    public async Task Dispose_delivers_every_line_in_order_first()
    {
        var seen = new List<string>();
        var relay = new InProcessSdbEngine.LineRelay(line =>
        {
            Thread.Sleep(1);
            seen.Add(line);
        });
        for (var i = 0; i < 50; i++)
            relay.Post(i.ToString());

        await relay.DisposeAsync();

        Assert.Equal(50, seen.Count);
        for (var i = 0; i < 50; i++)
            Assert.Equal(i.ToString(), seen[i]);
    }

    [Fact]
    public async Task An_observer_that_throws_does_not_stop_the_lines_after_it()
    {
        var seen = new List<string>();
        var relay = new InProcessSdbEngine.LineRelay(line =>
        {
            if (line == "bad")
                throw new System.InvalidOperationException("boom");
            seen.Add(line);
        });
        relay.Post("a");
        relay.Post("bad");
        relay.Post("b");

        await relay.DisposeAsync();

        Assert.Equal(new[] { "a", "b" }, seen);
    }
}
