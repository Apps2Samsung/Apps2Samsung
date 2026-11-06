using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using Apps2Samsung.Certificate;
using Apps2Samsung.Helpers.Tizen.Certificate;

namespace Apps2Samsung.TvStore
{
    /// <summary>
    /// The log lines Arm TV-Store writes about what it hands over and what TV-Store kept (#1012), so a
    /// debug log shows which profiles reached the TV and at which level. Never a key, never a whole
    /// certificate: the profile name, the level the TV will read, the author fingerprint (the first
    /// characters of the SHA-256 of the author leaf, the id TV-Store keeps a profile under, so the two
    /// logs can be matched), the author's expiry and the TVs the distributor certificate covers.
    /// </summary>
    public static class TvStoreArmLog
    {
        private const int FingerprintLength = 12;

        /// <summary>
        /// The privilege level TV-Store reads from a distributor chain (service/certs.js): Partner or
        /// Public when Samsung's CA names it, "unknown" otherwise, which the TV treats as Public.
        /// </summary>
        public static string LevelOf(string distributorCertPem)
        {
            var names = new List<string>();
            foreach (var cert in Certificates(distributorCertPem))
            {
                using (cert)
                {
                    names.Add(cert.Subject);
                    names.Add(cert.Issuer);
                }
            }
            if (names.Any(n => n.Contains("VD DEVELOPER Partner CA", StringComparison.Ordinal)))
                return "Partner";
            if (names.Any(n => n.Contains("VD DEVELOPER Public CA", StringComparison.Ordinal)))
                return "Public";
            return "unknown (TV-Store treats it as Public)";
        }

        /// <summary>The first characters of the SHA-256 of the author leaf, lower-case hex; "?" when the PEM holds no certificate.</summary>
        public static string FingerprintOf(string authorCertPem)
        {
            using var leaf = Certificates(authorCertPem).FirstOrDefault();
            return leaf is null ? "?" : Convert.ToHexStringLower(SHA256.HashData(leaf.RawData))[..FingerprintLength];
        }

        /// <summary>One line for one profile that is handed over.</summary>
        public static string Describe(SigningCertificatePem certificate, int index)
        {
            ArgumentNullException.ThrowIfNull(certificate);
            var name = string.IsNullOrWhiteSpace(certificate.Name) ? $"certificate {index + 1}" : certificate.Name;
            var parts = new List<string>
            {
                LevelOf(certificate.DistributorCertPem),
                "author " + FingerprintOf(certificate.AuthorCertPem),
            };

            using (var author = Certificates(certificate.AuthorCertPem).FirstOrDefault())
            {
                if (author is not null)
                    parts.Add("author valid until " + author.NotAfter.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            }

            using (var distributor = Certificates(certificate.DistributorCertPem).FirstOrDefault())
            {
                if (distributor is not null)
                {
                    var duids = CertificateHelper.DuidsOf(distributor).OrderBy(d => d, StringComparer.Ordinal).ToList();
                    parts.Add(duids.Count == 0 ? "distributor lists no TV" : "distributor covers " + string.Join(", ", duids));
                }
            }

            return $"#{index + 1} {name}{(index == 0 ? " (first choice)" : string.Empty)}: {string.Join(", ", parts)}";
        }

        /// <summary>
        /// What TV-Store answered to the hand-over: how many profiles it holds now and each one's name
        /// and level, as its service reports them. An older TV-Store answers with less, which is said.
        /// </summary>
        public static string DescribeReply(JsonNode? reply)
        {
            if (reply is null)
                return "no reply";
            if (reply["ok"]?.GetValue<bool>() != true)
                return "refused: " + (reply["error"]?.ToString() ?? "no reason given");

            var count = reply["certCount"]?.ToString() ?? "?";
            var list = reply["certificates"] as JsonArray;
            var line = $"accepted, {count} certificate(s) on the TV now";
            if (list is null || list.Count == 0)
                return line + " (this TV-Store does not list them)";

            var entries = list.Select(c =>
            {
                var name = c?["name"]?.ToString() ?? "?";
                var level = c?["level"]?.ToString();
                return string.IsNullOrWhiteSpace(level) ? name : $"{name} ({level})";
            });
            line += ": " + string.Join(", ", entries);
            var certError = reply["certError"]?.ToString();
            return string.IsNullOrWhiteSpace(certError) ? line : $"{line}; storing failed: {certError}";
        }

        // Every CERTIFICATE block in a PEM chain, in order; the ones that do not parse are left out.
        private static IEnumerable<X509Certificate2> Certificates(string? pem)
        {
            if (string.IsNullOrWhiteSpace(pem))
                yield break;
            const string begin = "-----BEGIN CERTIFICATE-----";
            const string end = "-----END CERTIFICATE-----";
            var at = 0;
            while ((at = pem.IndexOf(begin, at, StringComparison.Ordinal)) >= 0)
            {
                var stop = pem.IndexOf(end, at, StringComparison.Ordinal);
                if (stop < 0)
                    yield break;
                X509Certificate2? cert = null;
                try
                {
                    cert = X509Certificate2.CreateFromPem(pem.AsSpan(at, stop + end.Length - at));
                }
                catch (CryptographicException)
                {
                }
                at = stop + end.Length;
                if (cert is not null)
                    yield return cert;
            }
        }
    }
}
