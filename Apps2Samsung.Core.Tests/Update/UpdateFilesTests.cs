using System.Formats.Tar;
using System.IO.Compression;
using Apps2Samsung.Update;

namespace Apps2Samsung.Tests.Update;

public class UpdateFilesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "a2s-updatefiles-" + Guid.NewGuid().ToString("N"));

    public UpdateFilesTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Dir(string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Replace_PutsNewFilesOverOldOnes_AndLeavesTheRestAlone()
    {
        var target = Dir("install");
        File.WriteAllText(Path.Combine(target, "apps2samsung-cli"), "old");
        File.WriteAllText(Path.Combine(target, "notes.txt"), "the user's own file");
        var source = Dir(Path.Combine("install", ".self-update"));
        File.WriteAllText(Path.Combine(source, "apps2samsung-cli"), "new");
        Directory.CreateDirectory(Path.Combine(source, "lib"));
        File.WriteAllText(Path.Combine(source, "lib", "extra.dll"), "added");

        var count = UpdateFiles.Replace(source, target);

        Assert.Equal(2, count);
        Assert.Equal("new", File.ReadAllText(Path.Combine(target, "apps2samsung-cli")));
        Assert.Equal("added", File.ReadAllText(Path.Combine(target, "lib", "extra.dll")));
        Assert.Equal("the user's own file", File.ReadAllText(Path.Combine(target, "notes.txt")));
        Assert.Empty(Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Replace_OverAFileThatIsOpen_Works()
    {
        var target = Dir("install");
        var running = Path.Combine(target, "apps2samsung-cli");
        File.WriteAllText(running, "old");
        var source = Dir("new");
        File.WriteAllText(Path.Combine(source, "apps2samsung-cli"), "new");

        // As a running program holds its own file: readable, and (as Windows allows for an image) renamable.
        using (new FileStream(running, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            UpdateFiles.Replace(source, target);

        Assert.Equal("new", File.ReadAllText(running));
        UpdateFiles.RemoveLeftovers(target);
        Assert.Empty(Directory.EnumerateFiles(target, "*" + UpdateFiles.LeftoverExtension));
    }

    [Fact]
    public async Task Extract_ReadsATarGz_WithItsFolder()
    {
        var content = Dir("content");
        Directory.CreateDirectory(Path.Combine(content, "apps2samsung-cli"));
        File.WriteAllText(Path.Combine(content, "apps2samsung-cli", "apps2samsung-cli.dll"), "assembly");
        var archive = Path.Combine(_root, "Apps2Samsung.Cli-v9.9.9-linux-bionic-arm64.tar.gz");
        await using (var file = File.Create(archive))
        await using (var gzip = new GZipStream(file, CompressionMode.Compress))
            await TarFile.CreateFromDirectoryAsync(content, gzip, includeBaseDirectory: false);

        var into = Path.Combine(_root, "out");
        await UpdateFiles.ExtractAsync(archive, into);

        Assert.Equal("assembly", File.ReadAllText(Path.Combine(into, "apps2samsung-cli", "apps2samsung-cli.dll")));
    }

    [Fact]
    public async Task Extract_ReadsAZip()
    {
        var content = Dir("content");
        File.WriteAllText(Path.Combine(content, "apps2samsung-cli.exe"), "binary");
        var archive = Path.Combine(_root, "Apps2Samsung.Cli-v9.9.9-win-x64.zip");
        ZipFile.CreateFromDirectory(content, archive);

        var into = Path.Combine(_root, "out");
        await UpdateFiles.ExtractAsync(archive, into);

        Assert.Equal("binary", File.ReadAllText(Path.Combine(into, "apps2samsung-cli.exe")));
    }

    [Fact]
    public async Task Extract_RefusesAnythingElse()
    {
        await Assert.ThrowsAsync<NotSupportedException>(() => UpdateFiles.ExtractAsync(Path.Combine(_root, "x.msi"), Path.Combine(_root, "out")));
    }
}
