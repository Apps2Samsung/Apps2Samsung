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

        if (s.Options.ExportPath is { } exportPath)
            return Export(s, exportPath);
        if (s.Options.ImportPath is { } importPath)
            return await ImportAsync(s, importPath, ip, ct);

        if (s.Options.Prepare)
        {
            var manual = s.Options.ManualDuids.Split(new[] { ',', ';', ' ', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (string.IsNullOrWhiteSpace(ip) && manual.Length == 0)
                return s.Out.Fail("--prepare needs --tv <ip>, or --duids <DUID,...> for a TV whose DUID you already know", Output.Usage);
            var (_, duid, raw) = string.IsNullOrWhiteSpace(ip)
                ? await Certificates.EnsureForDuidAsync(s, manual[0], level, ct)
                : await Certificates.EnsureAsync(s, ip, level, ct);
            return s.Out.Result(
                new { ok = true, tv = ip, duid, level = level.ToString(), profile = Certificates.Describe(level) },
                new[] { $"{raw.ProfileName} is ready and covers {(string.IsNullOrWhiteSpace(ip) ? $"DUID {duid}" : $"{ip} (DUID {duid})")}." });
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

    // cert --export <file> [--partner]: the signing certificate on its own, for another head or
    // machine (#921). Every usable level, or the Partner one with --partner.
    private static int Export(CliServices s, string path)
    {
        try
        {
            var level = s.Options.Partner ? CertificatePrivilegeLevel.Partner : (CertificatePrivilegeLevel?)null;
            IReadOnlyList<CertificateArchiveProfile> exported;
            using (var stream = File.Create(path))
                exported = CertificateArchive.Export(stream, CliPaths.CertificateStore, level);

            return s.Out.Result(
                new { ok = true, file = Path.GetFullPath(path), profiles = exported.Select(Json) },
                exported.Select(Line).Prepend($"Exported to {Path.GetFullPath(path)}:"));
        }
        catch (CertificateArchiveException ex)
        {
            return s.Out.Fail(ex.Message);
        }
    }

    // cert --import <file> [--tv <ip>] [--yes]: preview first (what it holds, what it replaces, and
    // with --tv whether that TV is covered); replacing a certificate already here needs --yes.
    private static async Task<int> ImportAsync(CliServices s, string path, string? ip, CancellationToken ct)
    {
        if (!File.Exists(path))
            return s.Out.Fail($"{path} does not exist", Output.Usage);

        string? tvDuid = null;
        if (!string.IsNullOrWhiteSpace(ip))
        {
            s.Out.Progress($"Reading the DUID of {ip}…");
            try { tvDuid = await TizenDuidReader.ReadAsync(s.Sdb, ip, progress: s.Out.Progress); }
            catch (Exception ex) { s.Out.Warn($"could not read the TV's DUID: {ex.Message}"); }
        }

        CertificateArchivePreview preview;
        using (var stream = File.OpenRead(path))
            preview = CertificateArchive.Preview(stream, CliPaths.CertificateStore);

        if (preview.IsEmpty)
            return s.Out.Fail(s.Strings.GetString("certArchiveNoProfiles"));
        var text = CertificateArchive.Summarize(preview, tvDuid, s.Strings.GetString).Split('\n').ToList();

        if (preview.Rejected.Any())
            return s.Out.Fail(text[0], Output.Failed, new { profiles = preview.Profiles.Select(Json) });

        if (preview.Replaces.Count > 0 && !s.Options.Yes)
        {
            text.Add("Nothing was imported: add --yes to replace the certificate on this machine.");
            return s.Out.Result(new { ok = false, imported = false, profiles = preview.Profiles.Select(Json), replaces = preview.Replaces.Select(Json) }, text, Output.Stopped);
        }

        try
        {
            CertificateArchiveImportResult result;
            using (var stream = File.OpenRead(path))
                result = CertificateArchive.Import(stream, CliPaths.CertificateStore);

            try
            {
                s.Desktop.CertificatePreference = CertificateArchive.PreferenceAfterImport(s.Desktop.CertificatePreference, CliPaths.CertificateStore);
            }
            catch (InvalidOperationException ex)
            {
                s.Out.Warn(ex.Message);
            }

            text.Add($"Imported {result.FileCount} file(s) into {CliPaths.CertificateStore}.");
            if (tvDuid is not null)
                text.Add(preview.Covers(tvDuid)
                    ? $"{ip} is covered: installs need no sign-in."
                    : $"{ip} is not covered yet: the first install signs in to Samsung to extend it, or run: cert --tv {ip} --prepare");
            return s.Out.Result(new { ok = true, imported = true, files = result.FileCount, tv = ip, tvDuid, covered = tvDuid is not null && preview.Covers(tvDuid), profiles = result.Imported.Select(Json) }, text);
        }
        catch (CertificateArchiveException ex)
        {
            return s.Out.Fail(ex.Message);
        }
    }

    private static string Line(CertificateArchiveProfile p) =>
        $"  {p.Level?.ToString() ?? p.ProfileName}: {p.AccountEmail ?? p.AuthorSubject ?? "?"}, valid {p.NotBeforeUtc:yyyy-MM-dd} to {p.NotAfterUtc:yyyy-MM-dd}, covers {p.Duids.Count} TV(s)";

    private static object Json(CertificateArchiveProfile p) => new
    {
        level = p.Level?.ToString(),
        profile = p.ProfileName,
        account = p.AccountEmail,
        subject = p.AuthorSubject,
        state = p.State.ToString(),
        validFrom = p.NotBeforeUtc?.ToString("yyyy-MM-dd"),
        validUntil = p.NotAfterUtc?.ToString("yyyy-MM-dd"),
        duids = p.Duids,
    };
}
