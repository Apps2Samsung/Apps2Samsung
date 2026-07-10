namespace Apps2Samsung.Services
{
    /// <summary>
    /// Samsung developer REST endpoints used when signing CSRs. Supplied by the host
    /// (from settings) so Core carries no app-config dependency.
    /// </summary>
    public sealed record CertificateEndpoints(
        string AuthorV3,
        string DistributorsV1,
        string DistributorsV3);
}
