using Apps2Samsung.Models;
using Apps2Samsung.Sdb;

namespace Apps2Samsung.Tests.Sdb;

public class InstalledAppSortTests
{
    private static InstalledApp App(string title, long size, string installed, bool update = false) =>
        new(title, null, "id." + title, "1.0.0", installed, IsRemovable: true, SizeBytes: size, HasUpdate: update);

    private static readonly InstalledApp[] Apps =
    {
        App("YouTube", 73728, "2025-05-04 14:03:32"),
        App("Netflix", 38658376, "2023-12-15 16:20:06"),
        App("aquaPlay", 1642498, "2026-10-26 10:55:40"),
        App("Jellyfin", 500, "2024-01-01 09:00:00", update: true),
    };

    private static string[] Titles(IEnumerable<InstalledApp> apps) => apps.Select(a => a.Title).ToArray();

    [Fact]
    public void Size_LargestFirst_UpdatesOnTop()
    {
        Assert.Equal(new[] { "Jellyfin", "Netflix", "aquaPlay", "YouTube" }, Titles(InstalledAppSort.Sort(Apps, InstalledAppOrder.Size)));
    }

    [Fact]
    public void Name_AToZ_IgnoringCase_UpdatesOnTop()
    {
        Assert.Equal(new[] { "Jellyfin", "aquaPlay", "Netflix", "YouTube" }, Titles(InstalledAppSort.Sort(Apps, InstalledAppOrder.Name)));
    }

    [Fact]
    public void Newest_MostRecentFirst_UpdatesOnTop()
    {
        Assert.Equal(new[] { "Jellyfin", "aquaPlay", "YouTube", "Netflix" }, Titles(InstalledAppSort.Sort(Apps, InstalledAppOrder.Newest)));
    }

    [Fact]
    public void Newest_UnreadableDate_GoesLast()
    {
        var apps = new[] { App("NoDate", 1, ""), App("Old", 1, "2020-01-01 00:00:00") };

        Assert.Equal(new[] { "Old", "NoDate" }, Titles(InstalledAppSort.Sort(apps, InstalledAppOrder.Newest)));
    }

    [Fact]
    public void SeveralUpdates_KeepTheChosenOrderAmongThemselves()
    {
        var apps = new[] { App("B", 1, "", update: true), App("C", 1, ""), App("A", 1, "", update: true) };

        Assert.Equal(new[] { "A", "B", "C" }, Titles(InstalledAppSort.Sort(apps, InstalledAppOrder.Name)));
    }
}
