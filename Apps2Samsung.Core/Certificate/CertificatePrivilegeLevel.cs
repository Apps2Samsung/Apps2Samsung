namespace Apps2Samsung.Interfaces
{
    /// <summary>
    /// Tizen distributor certificate privilege level requested from Samsung's signing service.
    /// <see cref="Public"/> is the default and covers virtually every app; <see cref="Partner"/> is
    /// an opt-in needed only by apps that use restricted privileges (e.g. vpnservice). The value is
    /// sent as the <c>privilege_level</c> form field and selects the matching CA chain.
    /// </summary>
    public enum CertificatePrivilegeLevel
    {
        Public,
        Partner,
    }
}
