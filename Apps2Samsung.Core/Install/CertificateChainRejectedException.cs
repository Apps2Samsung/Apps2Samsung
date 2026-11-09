using System;

namespace Apps2Samsung.Install
{
    /// <summary>
    /// The TV refused the signing certificate's chain ([118, -12] "Invalid certificate chain",
    /// Tizen's &lt;-3&gt;). With a Tizen certificate (#988) this is the TV saying it only takes a
    /// Samsung one; the heads offer that switch. Derives from <see cref="InvalidOperationException"/>
    /// so every caller that reports the message as before still does.
    /// </summary>
    public sealed class CertificateChainRejectedException : InvalidOperationException
    {
        public CertificateChainRejectedException(string message, string? reason) : base(message)
        {
            Reason = reason;
        }

        /// <summary>The TV's own words after "Check certificate error", when it gave any.</summary>
        public string? Reason { get; }
    }
}
