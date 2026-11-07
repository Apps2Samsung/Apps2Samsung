using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Apps2Samsung.Catalog.Repositories
{
    /// <summary>
    /// Checks an independent repository's <c>repository.json.signed</c> (#1018) against a public key:
    /// every rule of the v1 spec, in the order and with the reason codes of tizen-community-packages'
    /// <c>scripts/repo-validate.py</c>, so the shared test vectors in <c>spec/fixtures</c> hold here
    /// too. Never throws for a bad file: the result says why it was rejected.
    /// <para>
    /// The envelope is the official catalog's: line 1 a base64 RSA-SHA256 (PKCS#1 v1.5) signature over
    /// everything after the first newline, then the manifest byte for byte. The key is RSA, at least
    /// 3072 bits, because TV-Store's Node 4.4.3 can verify nothing else; its fingerprint is the
    /// SHA-256 of its DER SubjectPublicKeyInfo, 64 lowercase hex characters.
    /// </para>
    /// </summary>
    public static class RepositoryVerifier
    {
        public const int MaxFileBytes = 8 * 1024 * 1024;
        public const int MaxSignatureLine = 4096;
        public const int MinKeyBits = 3072;
        public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);
        public static readonly TimeSpan FutureSkew = TimeSpan.FromHours(1);

        /// <summary>The reason codes, as the spec and its test vectors name them.</summary>
        public static class Reasons
        {
            public const string Size = "size";
            public const string NoSignature = "no-signature";
            public const string Key = "key";
            public const string Fingerprint = "fingerprint";
            public const string Signature = "signature";
            public const string Json = "json";
            public const string SchemaVersion = "schema-version";
            public const string Schema = "schema";
            public const string Url = "url";
            public const string Future = "future";
            public const string Expired = "expired";
            public const string Rollback = "rollback";
            public const string Duplicate = "duplicate";
            /// <summary>Not a spec code: the manifest is valid but names another repository than the one the user added.</summary>
            public const string RepositoryId = "repository-id";
        }

        /// <summary>What <see cref="Verify"/> found: the manifest when accepted, else the reason code and a sentence.</summary>
        public sealed record Result(bool Ok, string? Reason, string? Message, RepositoryManifest? Manifest, string? Fingerprint)
        {
            public static Result Reject(string reason, string message, string? fingerprint = null) => new(false, reason, message, null, fingerprint);
        }

        /// <param name="file">The whole <c>.signed</c> file.</param>
        /// <param name="publicKeyPem">The key to check it with: the stored one on a refresh, never a newly fetched one.</param>
        /// <param name="now">The client's clock.</param>
        /// <param name="approvedFingerprint">When set, the key must be this one.</param>
        /// <param name="notBefore">The last <c>generatedAt</c> accepted from this repository (rollback guard).</param>
        /// <param name="expectedRepositoryId">When set, <c>repository.id</c> must be this one.</param>
        /// <param name="checkAge">False for a copy this device verified before: its age and the rollback floor are not the question then.</param>
        public static Result Verify(byte[] file, string publicKeyPem, DateTimeOffset now,
            string? approvedFingerprint = null, DateTimeOffset? notBefore = null, string? expectedRepositoryId = null, bool checkAge = true)
        {
            if (file.Length > MaxFileBytes)
                return Result.Reject(Reasons.Size, $"the file is {file.Length} bytes, over the {MaxFileBytes} limit");

            var newline = Array.IndexOf(file, (byte)'\n', 0, Math.Min(file.Length, MaxSignatureLine));
            if (newline < 0)
                return Result.Reject(Reasons.NoSignature, "no signature line: the file is not a signed manifest");
            byte[] signature;
            try
            {
                signature = Convert.FromBase64String(Encoding.ASCII.GetString(file, 0, newline).Trim());
            }
            catch (FormatException)
            {
                return Result.Reject(Reasons.NoSignature, "line 1 is not base64");
            }
            if (signature.Length == 0)
                return Result.Reject(Reasons.NoSignature, "the signature line is empty");
            var payload = file.AsSpan(newline + 1);

            using var rsa = RSA.Create();
            string fingerprint;
            try
            {
                var spki = SubjectPublicKeyInfo(publicKeyPem);
                fingerprint = Convert.ToHexString(SHA256.HashData(spki)).ToLowerInvariant();
                rsa.ImportSubjectPublicKeyInfo(spki, out _);
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
            {
                return Result.Reject(Reasons.Key, "the key is not an RSA public key in PEM (TV-Store can only verify RSA-SHA256)");
            }
            if (rsa.KeySize < MinKeyBits)
                return Result.Reject(Reasons.Key, $"the key is {rsa.KeySize} bits; at least {MinKeyBits} is required", fingerprint);
            if (!string.IsNullOrWhiteSpace(approvedFingerprint) && fingerprint != NormalizeFingerprint(approvedFingerprint))
                return Result.Reject(Reasons.Fingerprint, $"the key's fingerprint is {fingerprint}, not the approved {approvedFingerprint}", fingerprint);
            if (!rsa.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                return Result.Reject(Reasons.Signature, "the signature does not match the key: the file was changed or signed by someone else", fingerprint);

            JsonNode? root;
            try
            {
                root = JsonNode.Parse(payload);
            }
            catch (JsonException ex)
            {
                return Result.Reject(Reasons.Json, $"the payload is not JSON: {ex.Message}", fingerprint);
            }
            if (root is not JsonObject obj || !IsInteger(obj["schemaVersion"], out var version) || version != RepositoryManifest.CurrentSchemaVersion)
                return Result.Reject(Reasons.SchemaVersion, $"schemaVersion is not supported (this build knows {RepositoryManifest.CurrentSchemaVersion})", fingerprint);

            var schemaError = RepositorySchema.Check(obj);
            if (schemaError is not null)
                return Result.Reject(Reasons.Schema, schemaError, fingerprint);

            var urlError = CheckUrls(obj);
            if (urlError is not null)
                return Result.Reject(Reasons.Url, urlError, fingerprint);

            var generatedAt = DateTimeOffset.Parse(obj["generatedAt"]!.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            if (checkAge && generatedAt - now > FutureSkew)
                return Result.Reject(Reasons.Future, $"generatedAt {generatedAt:O} is in the future: check the clock where it was signed", fingerprint);
            if (checkAge && now - generatedAt > MaxAge)
                return Result.Reject(Reasons.Expired, $"generatedAt {generatedAt:O} is more than {MaxAge.Days} days old: the publisher must re-sign", fingerprint);
            if (checkAge && notBefore is { } last && generatedAt < last)
                return Result.Reject(Reasons.Rollback, $"generatedAt {generatedAt:O} is older than the last accepted manifest ({last:O})", fingerprint);

            var duplicate = CheckDuplicates(obj);
            if (duplicate is not null)
                return Result.Reject(Reasons.Duplicate, duplicate, fingerprint);

            var manifest = obj.Deserialize<RepositoryManifest>()!;
            manifest.GeneratedAt = generatedAt;
            if (!string.IsNullOrWhiteSpace(expectedRepositoryId) && !string.Equals(manifest.Repository.Id, expectedRepositoryId, StringComparison.Ordinal))
                return Result.Reject(Reasons.RepositoryId, $"the manifest is repository {manifest.Repository.Id}, not {expectedRepositoryId}", fingerprint);
            return new Result(true, null, null, manifest, fingerprint);
        }

        /// <summary>The key's fingerprint: SHA-256 of its DER SubjectPublicKeyInfo, 64 lowercase hex; null when it is not a PEM public key.</summary>
        public static string? Fingerprint(string publicKeyPem)
        {
            try { return Convert.ToHexString(SHA256.HashData(SubjectPublicKeyInfo(publicKeyPem))).ToLowerInvariant(); }
            catch (Exception ex) when (ex is FormatException or ArgumentException) { return null; }
        }

        /// <summary>"AB:CD..." or "abcd..." to the stored form.</summary>
        public static string NormalizeFingerprint(string fingerprint) =>
            fingerprint.Replace(":", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal).Trim().ToLowerInvariant();

        /// <summary>The fingerprint as people compare it: groups of four, "ab12 cd34 ...".</summary>
        public static string FormatFingerprint(string fingerprint)
        {
            var fp = NormalizeFingerprint(fingerprint);
            return string.Join(' ', Enumerable.Range(0, (fp.Length + 3) / 4).Select(i => fp.Substring(i * 4, Math.Min(4, fp.Length - i * 4))));
        }

        // The DER bytes of a "PUBLIC KEY" PEM block, as openssl pkey -outform DER writes them.
        private static byte[] SubjectPublicKeyInfo(string pem)
        {
            if (!PemEncoding.TryFind(pem, out var fields))
                throw new FormatException("not PEM");
            var label = pem[fields.Label];
            if (!label.SequenceEqual("PUBLIC KEY"))
                throw new FormatException($"a PEM {label.ToString()} block, not PUBLIC KEY");
            return Convert.FromBase64String(pem[fields.Base64Data].ToString());
        }

        private static bool IsInteger(JsonNode? node, out long value)
        {
            value = 0;
            return node is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.TryGetValue(out value);
        }

        private static IEnumerable<(string Where, string Url)> Urls(JsonObject manifest)
        {
            if (manifest["repository"]?["homepage"]?.GetValue<string>() is { } home)
                yield return ("repository/homepage", home);
            var providers = manifest["providers"]!.AsArray();
            for (var i = 0; i < providers.Count; i++)
            {
                var p = providers[i]!.AsObject();
                foreach (var key in new[] { "url", "source_url" })
                    if (p[key]?.GetValue<string>() is { } u)
                        yield return ($"providers/{i}/{key}", u);
                if (p["buildInfo"]?["previewImage"]?.GetValue<string>() is { } preview)
                    yield return ($"providers/{i}/buildInfo/previewImage", preview);
                var releases = p["releases"]!.AsArray();
                for (var j = 0; j < releases.Count; j++)
                {
                    var r = releases[j]!.AsObject();
                    if (r["url"]?.GetValue<string>() is { } ru)
                        yield return ($"providers/{i}/releases/{j}/url", ru);
                    var assets = r["assets"]!.AsArray();
                    for (var k = 0; k < assets.Count; k++)
                        foreach (var key in new[] { "browser_download_url", "icon" })
                            if (assets[k]?[key]?.GetValue<string>() is { } au)
                                yield return ($"providers/{i}/releases/{j}/assets/{k}/{key}", au);
                }
            }
        }

        private static string? CheckUrls(JsonObject manifest)
        {
            foreach (var (where, url) in Urls(manifest))
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                    return $"{where}: not a URL";
                if (!string.IsNullOrEmpty(uri.UserInfo))
                    return $"{where}: credentials in a URL are not allowed";
                var host = uri.Host.ToLowerInvariant();
                if (host == "localhost" || host.EndsWith(".localhost", StringComparison.Ordinal) || host.EndsWith(".local", StringComparison.Ordinal))
                    return $"{where}: {host} is a local address";
                if (uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6 || IPAddress.TryParse(host.Trim('[', ']'), out _))
                    return $"{where}: use a host name, not an IP address";
            }
            return null;
        }

        private static string? CheckDuplicates(JsonObject manifest)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var packages = new Dictionary<(string, string), string>();
            foreach (var p in manifest["providers"]!.AsArray())
            {
                var id = p!["id"]!.GetValue<string>();
                if (!ids.Add(id))
                    return $"provider id {id} appears twice";
                foreach (var r in p["releases"]!.AsArray())
                    foreach (var a in r!["assets"]!.AsArray())
                    {
                        var pid = a?["package_id"]?.GetValue<string>();
                        var ver = a?["package_version"]?.GetValue<string>();
                        // One provider may ship several builds of one version: those are variants.
                        if (string.IsNullOrEmpty(pid) || string.IsNullOrEmpty(ver))
                            continue;
                        if (packages.TryGetValue((pid, ver), out var owner) && owner != id)
                            return $"package {pid} version {ver} is offered by providers {owner} and {id}";
                        packages[(pid, ver)] = id;
                    }
            }
            return null;
        }
    }
}
