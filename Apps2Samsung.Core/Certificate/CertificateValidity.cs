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

        /// <summary>
        /// How long until the certificate starts being accepted; <see cref="TimeSpan.Zero"/> once it
        /// is (so a countdown can simply tick this to zero and stop).
        /// </summary>
        public TimeSpan RemainingUntilValid(DateTime? utcNow = null)
        {
            if (NotBeforeUtc is null)
                return TimeSpan.Zero;

            var remaining = NotBeforeUtc.Value - (utcNow ?? DateTime.UtcNow);
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    /// <summary>
    /// Pre-flight validity check for the certificates an install signs with, shared by both heads.
    /// <para>
    /// Tizen verifies the package signature against the TV's clock and refuses a certificate whose
    /// validity period hasn't started yet (<c>install failed[118, -12] ... Certificate in signature
    /// is not valid yet</c>). Nothing about the package can fix that — the only cures are waiting
    /// until the certificate's start date or correcting a wrong clock — so checking before we sign
    /// and push lets the heads hold the install and count down to the start date instead of failing
    /// on the TV. Any <c>NotBefore</c> in the future blocks: there is no grace window, because the
    /// TV grants none either.
    /// </para>
    /// </summary>
    public static class CertificateValidity
    {
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

                var state = notBeforeUtc > now
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
        /// The remaining wait as a countdown clock: <c>mm:ss</c> under an hour, <c>h:mm:ss</c> under a
        /// day, <c>d.hh:mm:ss</c> beyond that. Shared so both heads tick the same way.
        /// </summary>
        public static string FormatCountdown(TimeSpan remaining)
        {
            if (remaining < TimeSpan.Zero)
                remaining = TimeSpan.Zero;

            // Round up: with 0.4s left a countdown should read 00:01, not 00:00 — it hits 00:00 only
            // when the wait is genuinely over.
            var seconds = (long)Math.Ceiling(remaining.TotalSeconds);
            var t = TimeSpan.FromSeconds(seconds);

            if (t.TotalDays >= 1)
                return $"{t.Days}d {t.Hours:00}:{t.Minutes:00}:{t.Seconds:00}";
            if (t.TotalHours >= 1)
                return $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}";
            return $"{t.Minutes:00}:{t.Seconds:00}";
        }

        /// <summary>
        /// Plain-English explanation of a not-yet-valid certificate, for heads without a localization
        /// catalog (the mobile app). The live countdown is rendered separately by the head.
        /// </summary>
        public static string DescribeNotYetValid(CertificateValidityResult result)
        {
            var when = result.ValidFromLocal?.ToString("f", CultureInfo.CurrentCulture);

            return "Your signing certificate isn't valid yet" +
                   (when is null ? "" : $" — it only becomes valid on {when}") +
                   ". A Samsung TV refuses a package signed with a certificate whose validity period " +
                   "hasn't started, so the install has to wait until the countdown reaches zero. If " +
                   "that time looks wrong, this device's clock is off: correct the date, time and time " +
                   "zone, then retry.";
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
