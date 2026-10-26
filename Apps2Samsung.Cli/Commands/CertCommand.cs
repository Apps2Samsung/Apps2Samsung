using Apps2Samsung.Certificate;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Sdb;

namespace Apps2Samsung.Cli.Commands;

public static class CertCommand
{
    public static async Task<int> RunAsync(CliServices s, CancellationToken ct)
    {
        var ip = s.Options.Tv ?? s.Options.Arguments.FirstOrDefault();
        var level = Certificates.Level(s);

        if (s.Options.Prepare)
        {
            if (string.IsNullOrWhiteSpace(ip))
                return s.Out.Fail("--prepare needs --tv <ip>: the certificate is made for a TV's DUID", Output.Usage);
            var (_, duid, raw) = await Certificates.EnsureAsync(s, ip, level, ct);
            return s.Out.Result(
                new { ok = true, tv = ip, duid, level = level.ToString(), profile = Certificates.Describe(level) },
                new[] { $"{raw.ProfileName} is ready and covers {ip} (DUID {duid})." });
        }

        string? tvDuid = null;
        if (!string.IsNullOrWhiteSpace(ip))
        {
            s.Out.Progress($"Reading the DUID of {ip}…");
            try { tvDuid = await TizenDuidReader.ReadAsync(s.Sdb, ip, progress: s.Out.Progress); }
            catch (Exception ex) { s.Out.Warn($"could not read the TV's DUID: {ex.Message}"); }
        }

        var profiles = new[] { Certificates.Describe(CertificatePrivilegeLevel.Public), Certificates.Describe(CertificatePrivilegeLevel.Partner) };
        var text = new List<string> { $"Certificate store: {CliPaths.CertificateStore}" };
        foreach (var p in profiles)
        {
            if (!p.Exists)
            {
                text.Add($"{p.Level}: none");
                continue;
            }
            var window = p.ValidFrom is null ? "unreadable" : $"valid {p.ValidFrom} to {p.ValidUntil}{(p.Usable ? "" : " (expired or unreadable)")}";
            text.Add($"{p.Level}: {window}, covers {p.Duids.Count} TV(s)");
            foreach (var d in p.Duids)
                text.Add($"    {d}{(tvDuid is not null && string.Equals(d, tvDuid, StringComparison.OrdinalIgnoreCase) ? $"  <- {ip}" : "")}");
        }
        if (tvDuid is not null)
        {
            var coveredBy = profiles.Where(p => p.Usable && p.Duids.Contains(tvDuid, StringComparer.OrdinalIgnoreCase)).Select(p => p.Level).ToList();
            text.Add(coveredBy.Count > 0
                ? $"{ip} (DUID {tvDuid}) is covered by: {string.Join(", ", coveredBy)}. Installs need no sign-in."
                : $"{ip} (DUID {tvDuid}) is not covered yet. The first install signs in to Samsung, or run: cert --tv {ip} --prepare");
        }
        return s.Out.Result(new { ok = true, store = CliPaths.CertificateStore, tv = ip, tvDuid, profiles }, text);
    }
}
