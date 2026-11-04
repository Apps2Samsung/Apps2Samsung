using Apps2Samsung.Certificate;
using Apps2Samsung.Install;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Localization;
using Apps2Samsung.Packaging;

namespace Apps2Samsung.Cli.Commands;

/// <summary>
/// <c>sign &lt;file&gt; -o &lt;out&gt;</c>: signs a local .wgt/.tpk with the certificate that covers a TV and
/// writes the signed copy, without installing it, for tools that install another way (through a root
/// service on the TV, TV-Store's upload page). The same certificate choice and signer as <c>install</c>.
/// </summary>
public static class SignCommand
{
    public static async Task<int> RunAsync(CliServices s, CancellationToken ct)
    {
        var input = s.Options.Arguments.FirstOrDefault();
        var output = s.Options.OutputPath;
        var manual = s.Options.ManualDuids.Split(new[] { ',', ';', ' ', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (string.IsNullOrWhiteSpace(input) || string.IsNullOrWhiteSpace(output) || (string.IsNullOrWhiteSpace(s.Options.Tv) && manual.Length == 0))
            return s.Out.Fail("usage: apps2samsung-cli sign <file> -o <signed file> (--tv <ip> | --duids <DUID>)   (`sign --help` for the options)", Output.Usage);
        if (!File.Exists(input))
            return s.Out.Fail($"No such file: {input}", Output.Usage);
        var ext = Path.GetExtension(input).ToLowerInvariant();
        if (ext != ".wgt" && ext != ".tpk")
            return s.Out.Fail($"{input} is not a .wgt or .tpk.", Output.Usage);
        if (Path.GetFullPath(input) == Path.GetFullPath(output))
            return s.Out.Fail("-o must be another file: the input is never changed.", Output.Usage);

        // The desktop's Automatic / Public / Partner setting, unless --partner; a package that needs
        // Partner gets it either way, as in install.
        var needsPartner = WgtPrivileges.RequiresPartner(input);
        var preference = Certificates.Preference(s);
        var level = CertificatePreference.ResolveLevel(preference, needsPartner);
        if (needsPartner && CertificatePreference.ResolveLevel(preference, false) != CertificatePrivilegeLevel.Partner)
            s.Out.Progress("This package needs a Partner-level certificate; signing as Partner.");
        try
        {
            // --duids names the TV without reading it over SDB (its Host PC IP may point elsewhere).
            var (profile, duid, _) = manual.Length > 0
                ? await Certificates.EnsureForDuidAsync(s, manual[0], level, ct)
                : await Certificates.EnsureAsync(s, s.Options.Tv!, level, ct);

            // The shared signer (Core): the same file the desktop and the phone write.
            var signed = await PackageSigner.SignCopyAsync(s.Sdb, input, output, profile,
                line => s.Out.Progress(ProgressText.Localize(line, s.Strings.GetString)), ct);
            return s.Out.Result(
                new { ok = true, file = signed.Path, appId = signed.AppId, packageId = signed.PackageId, certificate = level.ToString(), duid },
                new[] { $"Signed {Path.GetFileName(input)} for {duid} ({level}): {signed.Path}" });
        }
        catch (PackageSignException ex)
        {
            return s.Out.Fail($"Signing failed: {ex.Message}");
        }
        catch (CertificateNotYetValidException ex)
        {
            return s.Out.Fail(ex.Message, details: new { validFrom = ex.Result.ValidFromLocal });
        }
    }
}
