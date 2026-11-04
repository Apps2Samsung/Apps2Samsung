namespace Apps2Samsung.Certificate
{
    /// <summary>
    /// Which of the SDK's two distributor signer generations a Tizen profile uses (#988). The SDK
    /// ships both and its tools call them "legacy" and "latest".
    /// </summary>
    public enum TizenDistributorChain
    {
        /// <summary>
        /// The 2012 "Tizen Test CA" signers (<c>tizen-distributor-signer.p12</c>). Expired since
        /// October 2022, which the TVs do not check: a Tizen 5.0 (2019) set installs and runs a
        /// package signed with the Public one. There is no Platform signer in this generation.
        /// </summary>
        Legacy,

        /// <summary>
        /// The 2022 "Tizen Studio ... CA Class" signers (<c>tizen-distributor-signer-new.p12</c>),
        /// valid until 2032. A Tizen 5.0 set refuses all three; kept for data points on other sets.
        /// </summary>
        New,
    }
}
