using Apps2Samsung.Extensions;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Apps2Samsung.Interfaces
{
    public interface ITizenCertificateService
    {
        Task<(string authorP12, string distributorP12, string passwordP12)> GenerateProfileAsync(IReadOnlyCollection<string> duids, string accessToken, string userId, string userEmail, string outputPath, ProgressCallback? progress = null);

        /// <summary>
        /// Regenerates only the distributor certificate to cover <paramref name="duids"/>, reusing
        /// the existing author keypair in <paramref name="certDir"/> so the author identity stays
        /// unchanged (keeps apps installed on other TVs overwritable). Returns the distributor .p12 path.
        /// </summary>
        Task<string> RegenerateDistributorAsync(string certDir, IReadOnlyCollection<string> duids, string accessToken, string userId, string userEmail, ProgressCallback? progress = null);
    }
}
