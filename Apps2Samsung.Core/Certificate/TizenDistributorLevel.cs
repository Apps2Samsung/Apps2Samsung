namespace Apps2Samsung.Certificate
{
    /// <summary>
    /// The distributor certificate a Tizen (SDK) profile signs with (#988). The Tizen SDK ships one
    /// ready-made distributor signer per level; nothing is generated and no TV is named in it, unlike
    /// Samsung's <see cref="Interfaces.CertificatePrivilegeLevel"/>, which binds the distributor to
    /// the TV's DUID. Platform exists because the SDK ships it; what a platform-signed package may do
    /// is the TV's call.
    /// </summary>
    public enum TizenDistributorLevel
    {
        Public,
        Partner,
        Platform,
    }
}
