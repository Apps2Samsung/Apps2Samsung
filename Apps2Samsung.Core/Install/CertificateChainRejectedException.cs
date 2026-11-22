using System;

namespace Apps2Samsung.Install
{
    /// <summary>
    /// The TV refused the signing certificate's chain ([118, -12] "Invalid certificate chain",
    /// Tizen's &lt;-3&gt;). With a Tizen certificate (#988) this is the TV saying it only takes a
    /// Samsung one; the heads offer that switch. An <see cref="InstallFailedException"/>
    /// (and so an <see cref="InvalidOperationException"/>): every caller that reports the message
    /// as before still does.
    /// </summary>
    public sealed class CertificateChainRejectedException : InstallFailedException
    {
        public CertificateChainRejectedException(string message, string? reason)
            : base(InstallFailure.CertificateChainRejected, message)
        {
            Reason = reason;
        }
    }
}
