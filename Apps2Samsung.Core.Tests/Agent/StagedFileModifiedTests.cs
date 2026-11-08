using Apps2Samsung.Agent;

namespace Apps2Samsung.Tests.Agent;

/// <summary>
/// A staged file's modified time as the agent reports it (#975): a 2020 set gives every file a time
/// of 0, which must read as unknown instead of showing up as 1969-12-31.
/// </summary>
public class StagedFileModifiedTests
{
    [Theory]
    [InlineData("1970-01-01T00:00:00.000Z")]
    [InlineData("1970-01-01T00:00:00Z")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("not a date")]
    public void Unknown_times_are_null(string? text) =>
        Assert.Null(DebugAgentClient.ParseModified(text));

    [Fact]
    public void A_real_time_is_kept()
    {
        var modified = DebugAgentClient.ParseModified("2026-11-07T13:30:08.000Z");
        Assert.Equal(new DateTimeOffset(2026, 11, 7, 13, 30, 8, TimeSpan.Zero), modified);
    }
}
