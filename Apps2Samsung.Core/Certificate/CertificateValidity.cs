using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography.X509Certificates;

namespace Apps2Samsung.Certificate
{
    /// <summary>Where a certificate sits relative to its own validity window.</summary>
    public enum CertificateValidityState
    {
        /// <summary>Inside its validity window — usable for signing right now.</summary>
        Valid,

        /// <summary>Its validity period hasn't started yet (<c>NotBefore</c> is in the future).</summary>
        NotYetValid,

        /// <summary>Past <c>NotAfter</c>.</summary>
        Expired,

        /// <summary>Couldn't be read (missing, wrong password, corrupt) — nothing to conclude.</summary>
        Unreadable
    }

    /// <summary>The verdict for one certificate, with the window it was judged against.</summary>
    public sealed record CertificateValidityResult(
        CertificateValidityState State,
        string FilePath,
        DateTime? NotBeforeUtc,
        DateTime? NotAfterUtc)
    {
        public bool IsNotYetValid => State == CertificateValidityState.NotYetValid;

        /// <summary>Local time the certificate starts being accepted (null if unreadable).</summary>
        public DateTime? ValidFromLocal => NotBeforeUtc?.ToLocalTime();
    }

    /// <summary>
    /// Pre-flight validity check for the certificates an install signs with, shared by both heads.
    /// <para>
    /// Tizen verifies the package signature against the TV's clock and refuses a certificate whose
    /// validity period hasn't started yet (<c>install failed[118, -12] ... Certificate in signature
    /// is not valid yet</c>). Nothing about the package can fix that — the only cures are waiting
    /// until the certificate's start date or correcting a wrong clock — so checking before we sign
    /// and push lets the heads say so instead of failing on the TV.
    /// </para>
    /// </summary>
    public static class CertificateValidity
    {
        /// <summary>
        /// How far into the future <c>NotBefore</c> may sit before we call a certificate unusable.
        /// A freshly minted Samsung certificate carries the issuing server's timestamp, which can be
        /// slightly ahead of this machine's clock — blocking on that would break the normal "mint a
        /// certificate, install immediately" flow.
        /// </summary>
        public static readonly TimeSpan NotBeforeTolerance = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Checks the certificates an install signs with (author + distributor) and reports the first
        /// blocking problem, preferring "not yet valid" — the one the user can only wait out.
        /// </summary>
        public static CertificateValidityResult CheckSigningProfile(
            string authorP12, string distributorP12, string password, DateTime? utcNow = null)
        {
            var author = Check(authorP12, password, utcNow);
            var distributor = Check(distributorP12, password, utcNow);

            if (author.IsNotYetValid)
                return author;
            if (distributor.IsNotYetValid)
                return distributor;

            // Neither blocks the install; hand back a readable verdict so callers can log a window.
            return author.State == CertificateValidityState.Unreadable ? distributor : author;
        }

        /// <summary>
        /// Checks one PKCS#12 against the clock. An unreadable file is reported as
        /// <see cref="CertificateValidityState.Unreadable"/>, never as a failure — the re-sign step
        /// already fails loudly on a broken certificate.
        /// </summary>
        public static CertificateValidityResult Check(string p12Path, string password, DateTime? utcNow = null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(p12Path) || !File.Exists(p12Path))
                    return new CertificateValidityResult(CertificateValidityState.Unreadable, p12Path ?? string.Empty, null, null);

#pragma warning disable SYSLIB0057 // matches the loader used elsewhere; legacy PKCS#12 support
                using var cert = new X509Certificate2(p12Path, password, X509KeyStorageFlags.Exportable);
#pragma warning restore SYSLIB0057

                // X509Certificate2 exposes NotBefore/NotAfter in local time; compare in UTC so a
                // machine whose time zone is wrong (a common cause of this failure) still yields the
                // right answer.
                var now = utcNow ?? DateTime.UtcNow;
                var notBeforeUtc = cert.NotBefore.ToUniversalTime();
                var notAfterUtc = cert.NotAfter.ToUniversalTime();

                var state = notBeforeUtc - now > NotBeforeTolerance
                    ? CertificateValidityState.NotYetValid
                    : notAfterUtc < now
                        ? CertificateValidityState.Expired
                        : CertificateValidityState.Valid;

                if (state != CertificateValidityState.Valid)
                {
                    Trace.WriteLine($"[Cert] '{Path.GetFileName(p12Path)}' is {state}: valid " +
                        $"{notBeforeUtc:u} .. {notAfterUtc:u}, now {now:u}.");
                }

                return new CertificateValidityResult(state, p12Path, notBeforeUtc, notAfterUtc);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[Cert] Validity check failed for '{p12Path}': {ex.Message}");
                return new CertificateValidityResult(CertificateValidityState.Unreadable, p12Path, null, null);
            }
        }

        /// <summary>
        /// Plain-English explanation of a not-yet-valid certificate, for heads without a localization
        /// catalog (the mobile app).
        /// </summary>
        public static string DescribeNotYetValid(CertificateValidityResult result)
        {
            var when = result.ValidFromLocal?.ToString("f", CultureInfo.CurrentCulture);

            return "Your signing certificate isn't valid yet" +
                   (when is null ? "" : $" — it only becomes valid on {when}") +
                   ". A Samsung TV refuses a package signed with a certificate whose validity period " +
                   "hasn't started, so wait until then and install again. If that date looks wrong, " +
                   "this device's clock is off: correct the date, time and time zone, then retry.";
        }
    }

    /// <summary>
    /// Thrown when an install is stopped because the signing certificate's validity period hasn't
    /// started yet. Carries the window so a head can render its own message/overlay.
    /// </summary>
    public sealed class CertificateNotYetValidException : Exception
    {
        public CertificateNotYetValidException(CertificateValidityResult result)
            : base(CertificateValidity.DescribeNotYetValid(result))
        {
            Result = result;
        }

        public CertificateValidityResult Result { get; }

        /// <summary>Local time the certificate starts being accepted, if known.</summary>
        public DateTime? ValidFromLocal => Result.ValidFromLocal;
    }
}
