namespace Apps2Samsung.Services
{
    /// <summary>
    /// Samsung developer REST endpoints used when signing CSRs. Supplied by the host
    /// (from settings) so Core carries no app-config dependency.
    /// </summary>
    public sealed record CertificateEndpoints(
        string AuthorV3,
        string DistributorsV1,
        string DistributorsV3)
    {
        /// <summary>
        /// Samsung's production developer-certificate endpoints (the desktop's AppSettings
        /// defaults). A head with no per-user override can use these directly.
        /// </summary>
        public static CertificateEndpoints Default { get; } = new(
            AuthorV3: "https://svdca.samsungqbe.com/apis/v3/authors",
            DistributorsV1: "https://svdca.samsungqbe.com/apis/v1/distributors",
            DistributorsV3: "https://svdca.samsungqbe.com/apis/v3/distributors");
    }
}
