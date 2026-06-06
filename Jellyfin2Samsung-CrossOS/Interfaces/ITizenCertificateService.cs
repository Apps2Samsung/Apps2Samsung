using Apps2Samsung.Extensions;
using System.Threading.Tasks;

namespace Apps2Samsung.Interfaces
{
    public interface ITizenCertificateService
    {
        Task<(string authorP12, string distributorP12, string passwordP12)> GenerateProfileAsync(string duid, string accessToken, string userId, string userEmail, string outputPath, ProgressCallback? progress = null);
    }
}
