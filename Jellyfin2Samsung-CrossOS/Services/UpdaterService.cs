using Apps2Samsung.Helpers;
using Apps2Samsung.Helpers.Core;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Models;
using Apps2Samsung.Update;
using System;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Apps2Samsung.Services
{
    /// <summary>
    /// Service for checking and applying application updates via GitHub.
    /// Uses the Atom feed endpoint to avoid API rate limiting.
    /// </summary>
    public class UpdaterService : IUpdaterService
    {
        private readonly HttpClient _httpClient;
        private readonly GitHubUpdateChecker _checker;
        private const string RepoOwner = "Apps2Samsung";
        private const string RepoName = "Apps2Samsung";

        public string ReleasesPageUrl => $"https://github.com/{RepoOwner}/{RepoName}/releases";
        public string CurrentVersion => AppSettings.Default.AppVersion;

        public UpdaterService(HttpClient httpClient)
        {
            _httpClient = httpClient;
            _checker = new GitHubUpdateChecker(httpClient, RepoOwner, RepoName);
        }

        /// <inheritdoc />
        public async Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken cancellationToken = default)
        {
            // The portable check (Atom feed + version compare + asset resolution) lives in Core;
            // the desktop supplies its platform-specific asset matcher and automatic-update capability.
            var platformSuffix = GetPlatformSuffix();
            var result = await _checker.CheckForUpdateAsync(
                CurrentVersion,
                includePrereleases: AppSettings.Default.IncludeBetaUpdates,
                assetMatcher: name => name.Contains(platformSuffix, StringComparison.OrdinalIgnoreCase) &&
                    (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                     name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)),
                assetFallbackMatcher: name => name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase),
                cancellationToken: cancellationToken);

            if (result.IsSuccess && result.IsUpdateAvailable)
                result.SupportsAutomaticUpdate = IsAutomaticUpdateSupported();

            return result;
        }

        private static string GetPlatformSuffix()
        {
            // Must match the release asset naming scheme:
            //   Apps2Samsung-v<version>-<platform>-<arch>.<ext>
            // e.g. "win-x64", "linux-arm64", "macos-x64"
            var arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return $"win-{arch}";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return $"linux-{arch}";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return $"macos-{arch}";

            return $"win-{arch}"; // Default fallback
        }

        /// <inheritdoc />
        public async Task<string> DownloadUpdateAsync(
            string downloadUrl,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "Apps2Samsung_Update");
            Directory.CreateDirectory(tempDir);

            var fileName = Path.GetFileName(new Uri(downloadUrl).LocalPath);
            var downloadPath = Path.Combine(tempDir, fileName);

            // Clean up old downloads
            if (File.Exists(downloadPath))
                File.Delete(downloadPath);

            using var response = await _httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1L;
            var downloadedBytes = 0L;

            await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var fileStream = new FileStream(downloadPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

            var buffer = new byte[8192];
            int bytesRead;

            while ((bytesRead = await contentStream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                downloadedBytes += bytesRead;

                if (totalBytes > 0)
                {
                    var percentage = (int)((downloadedBytes * 100) / totalBytes);
                    progress?.Report(percentage);
                }
            }

            progress?.Report(100);
            return downloadPath;
        }

        /// <inheritdoc />
        public async Task<bool> ApplyUpdateAsync(string downloadedFilePath, CancellationToken cancellationToken = default)
        {
            try
            {
                var appDir = AppContext.BaseDirectory;
                var updateDir = Path.Combine(Path.GetTempPath(), "Apps2Samsung_Update", "extracted");
                var backupDir = Path.Combine(Path.GetTempPath(), "Apps2Samsung_Update", "backup");

                // Clean extraction directory
                if (Directory.Exists(updateDir))
                    Directory.Delete(updateDir, true);
                Directory.CreateDirectory(updateDir);

                // Extract update. Windows ships .zip, Linux/macOS ship .tar.gz.
                if (downloadedFilePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    ZipFile.ExtractToDirectory(downloadedFilePath, updateDir);
                }
                else if (downloadedFilePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ||
                         downloadedFilePath.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
                {
                    await ExtractTarGzAsync(downloadedFilePath, updateDir, cancellationToken);
                }
                else
                {
                    throw new NotSupportedException("Only ZIP and TAR.GZ archives are supported for automatic updates.");
                }

                // Find the actual application directory (might be in a subfolder)
                var extractedAppDir = FindApplicationDirectory(updateDir);
                if (extractedAppDir == null)
                {
                    throw new InvalidOperationException("Could not find application files in the update package.");
                }

                // The update may ship a different executable name than the one
                // currently running (rebrand) — relaunch whatever the package contains.
                var newExeName = FindExecutableName(extractedAppDir)
                    ?? throw new InvalidOperationException("Could not find the application executable in the update package.");

                // Create update script
                var scriptPath = CreateUpdateScript(extractedAppDir, appDir, backupDir, newExeName);

                // Launch the update script and exit
                LaunchUpdateScript(scriptPath);

                return true;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Failed to apply update: {ex}");
                throw;
            }
        }

        private static async Task ExtractTarGzAsync(string archivePath, string destinationDir, CancellationToken cancellationToken)
        {
            await using var fileStream = File.OpenRead(archivePath);
            await using var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress);
            // TarFile preserves Unix file permissions (e.g. the executable bit) on extraction.
            await TarFile.ExtractToDirectoryAsync(gzipStream, destinationDir, overwriteFiles: true, cancellationToken);
        }

        /// <summary>
        /// Executable base names this updater recognises, newest first.
        /// The project was rebranded from Apps2Samsung to Apps2Samsung —
        /// accepting both keeps automatic updates working across the rename.
        /// </summary>
        private static readonly string[] ExeBaseNameCandidates = { "Apps2Samsung", "Jellyfin2Samsung" };

        private static string? FindExecutableName(string directory)
        {
            var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            foreach (var baseName in ExeBaseNameCandidates)
            {
                var exeName = isWindows ? $"{baseName}.exe" : baseName;
                if (File.Exists(Path.Combine(directory, exeName)))
                    return exeName;
            }
            return null;
        }

        private string? FindApplicationDirectory(string extractedDir)
        {
            // Check if the main executable is directly in the extracted directory
            if (FindExecutableName(extractedDir) != null)
                return extractedDir;

            // Check subdirectories
            foreach (var subDir in Directory.GetDirectories(extractedDir))
            {
                if (FindExecutableName(subDir) != null)
                    return subDir;

                // Check one level deeper
                foreach (var subSubDir in Directory.GetDirectories(subDir))
                {
                    if (FindExecutableName(subSubDir) != null)
                        return subSubDir;
                }
            }

            return null;
        }

        private string CreateUpdateScript(string sourceDir, string targetDir, string backupDir, string exeName)
        {
            var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            var scriptExtension = isWindows ? ".bat" : ".sh";
            var scriptPath = Path.Combine(Path.GetTempPath(), $"apps2samsung_update{scriptExtension}");

            // When the executable name changes across the update (rebrand), remove
            // the leftover binaries of the previous name so the user doesn't end up
            // with two executables side by side. A full backup is taken first.
            var newBaseName = Path.GetFileNameWithoutExtension(exeName);
            var staleBaseNames = ExeBaseNameCandidates
                .Where(b => !b.Equals(newBaseName, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var processId = Environment.ProcessId;

            string scriptContent;

            if (isWindows)
            {
                scriptContent = $@"@echo off
chcp 65001 > nul
echo Waiting for application to close...
:waitloop
tasklist /FI ""PID eq {processId}"" 2>NUL | find /I ""{processId}"" >NUL
if not errorlevel 1 (
    timeout /t 1 /nobreak > nul
    goto waitloop
)

echo Creating backup...
if exist ""{backupDir}"" rmdir /s /q ""{backupDir}""
mkdir ""{backupDir}""
xcopy ""{targetDir}\*"" ""{backupDir}\"" /E /H /Y /Q

echo Installing update...
xcopy ""{sourceDir}\*"" ""{targetDir}\"" /E /H /Y /Q

echo Removing old binaries...
{string.Join("\r\n", staleBaseNames.Select(b => $@"del /q ""{Path.Combine(targetDir, b)}.*"" 2>nul"))}

echo Starting application...
start """" ""{Path.Combine(targetDir, exeName)}""

echo Cleaning up...
timeout /t 2 /nobreak > nul
rmdir /s /q ""{Path.Combine(Path.GetTempPath(), "Apps2Samsung_Update")}""

del ""%~f0""
";
            }
            else
            {
                scriptContent = $@"#!/bin/bash
echo ""Waiting for application to close...""
while kill -0 {processId} 2>/dev/null; do
    sleep 1
done

echo ""Creating backup...""
rm -rf ""{backupDir}""
mkdir -p ""{backupDir}""
cp -r ""{targetDir}/""* ""{backupDir}/""

echo ""Installing update...""
cp -rf ""{sourceDir}/""* ""{targetDir}/""

echo ""Removing old binaries...""
{string.Join("\n", staleBaseNames.Select(b => $@"rm -f ""{Path.Combine(targetDir, b)}"" ""{Path.Combine(targetDir, b)}.""*"))}

chmod +x ""{Path.Combine(targetDir, exeName)}""

echo ""Starting application...""
nohup ""{Path.Combine(targetDir, exeName)}"" &

echo ""Cleaning up...""
sleep 2
rm -rf ""{Path.Combine(Path.GetTempPath(), "Apps2Samsung_Update")}""
rm -- ""$0""
";
            }

            File.WriteAllText(scriptPath, scriptContent);

            if (!isWindows)
            {
                // Make script executable on Unix
                Process.Start("chmod", $"+x \"{scriptPath}\"")?.WaitForExit();
            }

            return scriptPath;
        }

        private void LaunchUpdateScript(string scriptPath)
        {
            var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

            var startInfo = new ProcessStartInfo
            {
                UseShellExecute = true,
                CreateNoWindow = !isWindows, // Show window on Windows for user feedback
                WindowStyle = isWindows ? ProcessWindowStyle.Normal : ProcessWindowStyle.Hidden
            };

            if (isWindows)
            {
                startInfo.FileName = "cmd.exe";
                startInfo.Arguments = $"/c \"{scriptPath}\"";
            }
            else
            {
                startInfo.FileName = "/bin/bash";
                startInfo.Arguments = scriptPath;
            }

            Process.Start(startInfo);
        }

        /// <inheritdoc />
        public bool IsAutomaticUpdateSupported()
        {
            var appDir = AppContext.BaseDirectory;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // MSI installs into Program Files, which requires elevation to overwrite.
                var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                if (IsUnderDirectory(appDir, programFiles) || IsUnderDirectory(appDir, programFilesX86))
                    return false;
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                // An AppImage runs from a read-only squashfs mount, and the thing a user would want
                // replaced is the .AppImage file itself, not this directory — so there is nothing here
                // to update in place. The runtime sets APPIMAGE to that file's path (#589).
                if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("APPIMAGE")))
                    return false;

                // .deb / .rpm installs land under package-manager-owned locations.
                if (IsUnderDirectory(appDir, "/usr") || IsUnderDirectory(appDir, "/opt"))
                    return false;
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                // Running from inside a .app bundle: the flat-folder replace logic does not apply.
                if (appDir.Contains(".app/Contents/", StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return true;
        }

        private static bool IsUnderDirectory(string path, string? baseDir)
        {
            if (string.IsNullOrEmpty(baseDir))
                return false;

            var fullPath = Path.GetFullPath(path);
            var fullBase = Path.GetFullPath(baseDir);

            // Match on a directory boundary so "/usr" doesn't match "/usrequest".
            if (!fullBase.EndsWith(Path.DirectorySeparatorChar))
                fullBase += Path.DirectorySeparatorChar;

            var comparison = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            return fullPath.StartsWith(fullBase, comparison);
        }

        /// <inheritdoc />
        public void OpenReleasesPage()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = ReleasesPageUrl,
                    UseShellExecute = true
                };
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Failed to open releases page: {ex}");
            }
        }

    }
}
