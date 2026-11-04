using Apps2Samsung.Certificate;
using Apps2Samsung.Install;
using Apps2Samsung.Interfaces;
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

        // A working copy: the signer re-signs in place.
        var staging = Path.Combine(CliPaths.Downloads, "sign");
        Directory.CreateDirectory(staging);
        var work = Path.Combine(staging, Path.GetFileName(input));
        File.Copy(input, work, overwrite: true);
        try
        {
            // The desktop's Automatic / Public / Partner setting, unless --partner; a package that needs
            // Partner gets it either way, as in install.
            var needsPartner = WgtPrivileges.RequiresPartner(work);
            var preference = Certificates.Preference(s);
            var level = CertificatePreference.ResolveLevel(preference, needsPartner);
            if (needsPartner && CertificatePreference.ResolveLevel(preference, false) != CertificatePrivilegeLevel.Partner)
                s.Out.Progress("This package needs a Partner-level certificate; signing as Partner.");
            // --duids names the TV without reading it over SDB (its Host PC IP may point elsewhere).
            var (profile, duid, _) = manual.Length > 0
                ? await Certificates.EnsureForDuidAsync(s, manual[0], level, ct)
                : await Certificates.EnsureAsync(s, s.Options.Tv!, level, ct);

            var validity = CertificateValidity.CheckSigningProfile(profile.AuthorP12, profile.DistributorP12, profile.Password);
            if (validity.IsNotYetValid)
                throw new CertificateNotYetValidException(validity);

            s.Out.Progress($"Signing {Path.GetFileName(input)}…");
            var resign = await s.Sdb.ResignAsync(work, profile.AuthorP12, profile.DistributorP12, profile.Password);
            if (resign.ExitCode != 0)
                return s.Out.Fail($"Signing failed: {(string.IsNullOrWhiteSpace(resign.Error) ? resign.Output.Trim() : resign.Error)}");

            var dir = Path.GetDirectoryName(Path.GetFullPath(output));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.Copy(work, output, overwrite: true);
            var (appId, packageId) = await WgtManifest.ReadIdsAsync(output);
            return s.Out.Result(
                new { ok = true, file = Path.GetFullPath(output), appId, packageId, certificate = level.ToString(), duid },
                new[] { $"Signed {Path.GetFileName(input)} for {duid} ({level}): {Path.GetFullPath(output)}" });
        }
        catch (CertificateNotYetValidException ex)
        {
            return s.Out.Fail(ex.Message, details: new { validFrom = ex.Result.ValidFromLocal });
        }
        finally
        {
            try { File.Delete(work); } catch { /* the cache folder is cleaned on its own */ }
        }
    }
}
