using System.Text.Json;
using System.Text.Json.Serialization;

namespace Apps2Samsung.Cli;

/// <summary>
/// What a command prints. Results go to stdout: readable text by default, one JSON document with
/// <c>--json</c>. Progress and warnings go to stderr either way, so a script or an agent can read
/// stdout as the answer and still see what happened. Exit codes: 0 done, 1 failed, 2 bad usage,
/// 3 stopped at a check that <c>--yes</c> would have waved through.
/// </summary>
public sealed class Output
{
    public const int Ok = 0;
    public const int Failed = 1;
    public const int Usage = 2;
    public const int Stopped = 3;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public bool AsJson { get; }
    public bool Quiet { get; }

    public Output(bool asJson, bool quiet)
    {
        AsJson = asJson;
        Quiet = quiet;
    }

    /// <summary>A progress line (stderr); silenced by <c>--quiet</c>.</summary>
    public void Progress(string message)
    {
        if (!Quiet)
            Console.Error.WriteLine(message);
    }

    /// <summary>A warning (stderr), never silenced.</summary>
    public void Warn(string message) => Console.Error.WriteLine($"warning: {message}");

    /// <summary>The command's result: the text lines, or the object as JSON.</summary>
    public int Result(object data, IEnumerable<string> text, int exitCode = Ok)
    {
        if (AsJson)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(data, data.GetType(), Json));
        }
        else
        {
            foreach (var line in text)
                Console.Out.WriteLine(line);
        }
        return exitCode;
    }

    /// <summary>A failure: the reason on stderr (text mode) or as the JSON result, with the exit code.</summary>
    public int Fail(string message, int exitCode = Failed, object? details = null)
    {
        if (AsJson)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(new { ok = false, error = message, details }, Json));
        }
        else
        {
            Console.Error.WriteLine($"error: {message}");
        }
        return exitCode;
    }
}
