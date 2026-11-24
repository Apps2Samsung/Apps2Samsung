using Apps2Samsung.Interfaces;
using Apps2Samsung.Models;
using Apps2Samsung.Sdb;

namespace Apps2Samsung.Tests.Sdb;

// Uninstall is offered for the TV's own apps as well (#1175). Whether an app is gone is read off
// the TV's app list afterwards, never off the words of its answer alone.
public class AppUninstallTests
{
    private const string Id = "9Ur5IzDKqV.TizenYouTube";

    private static InstalledApp App(string tizenId, bool removable = false) => new("YouTube", null, tizenId, "2.1.486", "", removable);

    private static ProcessResult Answer(string output, int exitCode = 0) =>
        new() { ExitCode = exitCode, Output = output, Error = exitCode == 0 ? string.Empty : output };

    [Fact]
    public void StillListed_IsKept_WhateverTheAnswerSaid()
    {
        var verdict = AppUninstall.Judge(Answer("uninstall completed"), new[] { App(Id) }, Id);

        Assert.Equal(UninstallOutcome.Kept, verdict.Outcome);
        Assert.Equal("uninstall completed", verdict.Reply);
        Assert.True(verdict.Verified);
    }

    [Fact]
    public void StillListed_WithNoAnswerAtAll_IsKept_WithNoReply()
    {
        var verdict = AppUninstall.Judge(Answer("  "), new[] { App(Id) }, Id);

        Assert.Equal(UninstallOutcome.Kept, verdict.Outcome);
        Assert.Null(verdict.Reply);
    }

    // What the Tizen 10.0 emulator answers for an id it will not remove: three lines, the last one
    // the timing every Samsung verb ends on.
    [Fact]
    public void TheReply_IsOneLine_WithoutTheTimingLine()
    {
        var verdict = AppUninstall.Judge(
            Answer("uninstall org.tizen.netflix-app\nwas get app info failed\r\nspend time for wascmd is [3]ms\n"),
            new[] { App("org.tizen.netflix-app") }, "org.tizen.netflix-app");

        Assert.Equal("uninstall org.tizen.netflix-app, was get app info failed", verdict.Reply);
        Assert.Null(AppUninstall.Judge(Answer("spend time for wascmd is [3]ms"), null, Id).Reply);
    }

    [Fact]
    public void NoLongerListed_IsRemoved_EvenWhenTheAnswerReadsAsAFailure()
    {
        var verdict = AppUninstall.Judge(Answer("uninstall failed[-3]", exitCode: 1), new[] { App("Other.App") }, Id);

        Assert.Equal(UninstallOutcome.Removed, verdict.Outcome);
        Assert.True(verdict.Verified);
    }

    [Fact]
    public void NotListed_AndTheNotInstalledCode_IsNotInstalled()
    {
        var verdict = AppUninstall.Judge(Answer("uninstall failed[132]", exitCode: 1), Array.Empty<InstalledApp>(), Id);

        Assert.Equal(UninstallOutcome.NotInstalled, verdict.Outcome);
    }

    [Fact]
    public void TheIdIsMatchedWithoutCase()
    {
        var verdict = AppUninstall.Judge(Answer(""), new[] { App(Id.ToUpperInvariant()) }, Id);

        Assert.Equal(UninstallOutcome.Kept, verdict.Outcome);
    }

    [Theory]
    [InlineData("uninstall completed", 0, UninstallOutcome.Removed)]
    [InlineData("uninstall failed[-3]", 1, UninstallOutcome.Kept)]
    [InlineData("uninstall failed[132]", 1, UninstallOutcome.NotInstalled)]
    [InlineData("uninstall failed[132]", 0, UninstallOutcome.NotInstalled)]
    public void WithoutAList_TheAnswerIsAllThereIs(string output, int exitCode, UninstallOutcome expected)
    {
        var verdict = AppUninstall.Judge(Answer(output, exitCode), null, Id);

        Assert.Equal(expected, verdict.Outcome);
        Assert.False(verdict.Verified);
    }

    [Fact]
    public void KeptBuiltInMessage_QuotesTheTv_OrSaysItWasSilent()
    {
        // The wording comes from en.json through the head's lookup, never from Core.
        var strings = new Dictionary<string, string>
        {
            [AppUninstall.KeptBuiltInKey] = "kept {0}: {1}",
            [AppUninstall.KeptBuiltInSilentKey] = "kept {0}, silently",
        };

        Assert.Equal("kept YouTube: uninstall failed[-3]", AppUninstall.KeptBuiltInMessage("YouTube", "uninstall failed[-3]", key => strings[key]));
        Assert.Equal("kept YouTube, silently", AppUninstall.KeptBuiltInMessage("YouTube", null, key => strings[key]));
        Assert.Equal("kept YouTube, silently", AppUninstall.KeptBuiltInMessage("YouTube", "  ", key => strings[key]));
    }

    [Fact]
    public async Task Run_ReadsTheListAgain_AfterAsking()
    {
        var tv = new FakeTv
        {
            UninstallAnswer = Answer("uninstall failed[-3]", exitCode: 1),
            AppsAfter = Answer($"""
                ---------------------------------------------------------------------------------------------
                --------------app_title                             =YouTube-------------
                --------------app_tizen_id                          ={Id}-------------
                --------------is_removable                          =0-------------
                ---------------------------------------------------------------------------------------------
                """),
        };

        var verdict = await AppUninstall.RunAsync(tv, "192.168.1.8", Id);

        Assert.Equal(UninstallOutcome.Kept, verdict.Outcome);
        Assert.Equal("uninstall failed[-3]", verdict.Reply);
        Assert.Equal(new[] { $"uninstall {Id}", "apps" }, tv.Calls);
    }

    [Fact]
    public async Task Run_WithAListThatCannotBeRead_GoesByTheAnswer()
    {
        var tv = new FakeTv { UninstallAnswer = Answer("uninstall completed"), AppsAfter = Answer("Remote closed channel", exitCode: 1) };

        var verdict = await AppUninstall.RunAsync(tv, "192.168.1.8", Id);

        Assert.Equal(UninstallOutcome.Removed, verdict.Outcome);
        Assert.False(verdict.Verified);
    }

    private sealed class FakeTv : ISdbEngine
    {
        public ProcessResult UninstallAnswer { get; init; } = new();
        public ProcessResult AppsAfter { get; init; } = new();
        public List<string> Calls { get; } = new();

        public Task<ProcessResult> UninstallAsync(string tvIpAddress, string packageId)
        {
            Calls.Add($"uninstall {packageId}");
            return Task.FromResult(UninstallAnswer);
        }

        public Task<ProcessResult> AppsAsync(string tvIpAddress)
        {
            Calls.Add("apps");
            return Task.FromResult(AppsAfter);
        }

        public Task<ProcessResult> CapabilityAsync(string tvIpAddress) => throw new NotSupportedException();
        public Task<ProcessResult> DiagnoseAsync(string tvIpAddress) => throw new NotSupportedException();
        public Task<ProcessResult> ResignAsync(string packagePath, string authorP12, string distributorP12, string certPass) => throw new NotSupportedException();
        public Task<ProcessResult> InstallAsync(string tvIpAddress, string packagePath, string sdkToolPath, IProgress<int>? progress = null) => throw new NotSupportedException();
        public Task<ProcessResult> DevicesAsync(string tvIpAddress) => throw new NotSupportedException();
        public Task<ProcessResult> DisconnectAsync(string tvIpAddress) => throw new NotSupportedException();
        public Task<ProcessResult> DuidAsync(string tvIpAddress) => throw new NotSupportedException();
        public Task<ProcessResult> LaunchAsync(string tvIpAddress, string appId) => throw new NotSupportedException();
        public Task<ProcessResult> ExecuteAsync(string tvIpAddress, string appId) => throw new NotSupportedException();
        public Task<ProcessResult> PermitInstallAsync(string tvIpAddress, string deviceXml, string sdkToolPath) => throw new NotSupportedException();
        public Task<ProcessResult> ShellAsync(string tvIpAddress, string command) => throw new NotSupportedException();
        public Task<IAsyncDisposable> ForwardAsync(string tvIpAddress, int localPort, int remotePort) => throw new NotSupportedException();
    }
}
