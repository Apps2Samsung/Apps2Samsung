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
                return PlatformService.GetEsbuildPath(Path.Combine(baseDir, AppSettings.EsbuildPath));
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Transpiles ES2015+ JavaScript to ES5 using esbuild.
        /// If esbuild is missing or fails, returns the original JS.
        /// </summary>
        public static async Task<string> TranspileAsync(string js, string? relPathForLog = null)
        {
            try
            {
                string? esbuildPath = GetEsbuildPath();
                if (string.IsNullOrEmpty(esbuildPath))
                {
                    Trace.WriteLine($"esbuild binary not found, skipping transpile for {relPathForLog ?? "unknown"}");
                    return js;
                }

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
                    Trace.WriteLine($"esbuild failed for {relPathForLog ?? "unknown"} (exit {proc.ExitCode}): {stderr}");
                    return js;
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

                Trace.WriteLine($"Transpiled {relPathForLog ?? "unknown"} via esbuild");
                return transpiled;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"esbuild transpile error for {relPathForLog ?? "unknown"}: {ex}");
                return js;
            }
        }
    }
}
