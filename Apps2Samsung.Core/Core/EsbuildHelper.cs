using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace Apps2Samsung.Helpers.Core
{
    public static class EsbuildHelper
    {
        public static string? GetEsbuildPath()
        {
            try
            {
                string baseDir = AppContext.BaseDirectory;
                // Shipped esbuild binaries ride next to the executable under Assets/esbuild
                // (identical to the desktop AppSettings.EsbuildPath layout; resolved at runtime
                // against the host process's base dir so behaviour is unchanged per head).
                return PlatformService.GetEsbuildPath(Path.Combine(baseDir, "Assets", "esbuild"));
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Ensures the esbuild binary has the executable bit set on Unix-like systems.
        /// The bundled binaries are shipped non-executable and CopyToOutputDirectory does
        /// not preserve the bit, so it must be set before the binary can be launched.
        /// </summary>
        private static void EnsureExecutable(string path)
        {
            // The OperatingSystem.IsWindows() term is what the platform-compatibility analyzer
            // recognizes to prove the Unix-only File.*UnixFileMode calls below are unreachable
            // on Windows; RequiresExecutablePermissions() carries the same intent for readers.
            if (OperatingSystem.IsWindows() || !PlatformService.RequiresExecutablePermissions())
                return;

            try
            {
                const UnixFileMode executable =
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

                if (File.GetUnixFileMode(path) != executable)
                    File.SetUnixFileMode(path, executable);
            }
            catch (Exception ex)
            {
                // Non-fatal here: if this fails the process launch below fails and the file is skipped.
                Trace.WriteLine($"Failed to set executable bit on esbuild: {ex}");
            }
        }

        /// <summary>
        /// Transpiles plugin JavaScript down to ES2015 using esbuild.
        /// Returns null when esbuild is missing or fails: the caller must then leave the file out,
        /// because untranspiled plugin code does not parse on older TVs (#801).
        /// </summary>
        public static async Task<string?> TranspileAsync(string js, string? relPathForLog = null)
        {
            string name = relPathForLog ?? "unknown";
            try
            {
                string? esbuildPath = GetEsbuildPath();
                if (string.IsNullOrEmpty(esbuildPath))
                {
                    Trace.WriteLine($"⛔ esbuild binary not found, {name} not transpiled and left out of the app.");
                    return null;
                }

                EnsureExecutable(esbuildPath);

                string tempRoot = Path.Combine(Path.GetTempPath(), Constants.Esbuild.TempFolderName);
                Directory.CreateDirectory(tempRoot);

                string inputPath = Path.Combine(tempRoot, Guid.NewGuid().ToString("N") + Constants.FilePatterns.JsExtension);
                string outputPath = Path.Combine(tempRoot, Guid.NewGuid().ToString("N") + Constants.FilePatterns.JsExtension);

                await File.WriteAllTextAsync(inputPath, js, Encoding.UTF8);

                var psi = new ProcessStartInfo
                {
                    FileName = esbuildPath,
                    Arguments = $"\"{inputPath}\" --outfile=\"{outputPath}\" --target={Constants.Esbuild.TargetEs2015}",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var proc = new Process { StartInfo = psi };
                proc.Start();

                string stdout = await proc.StandardOutput.ReadToEndAsync();
                string stderr = await proc.StandardError.ReadToEndAsync();

                proc.WaitForExit();

                if (proc.ExitCode != 0 || !File.Exists(outputPath))
                {
                    Trace.WriteLine($"⛔ esbuild failed for {name} (exit {proc.ExitCode}), left out of the app: {stderr}");
                    return null;
                }

                string transpiled = await File.ReadAllTextAsync(outputPath, Encoding.UTF8);

                try
                {
                    File.Delete(inputPath);
                    File.Delete(outputPath);
                }
                catch
                {
                    // ignore cleanup errors
                }

                Trace.WriteLine($"Transpiled {name} via esbuild");
                return transpiled;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"⛔ esbuild transpile error for {name}, left out of the app: {ex}");
                return null;
            }
        }
    }
}
