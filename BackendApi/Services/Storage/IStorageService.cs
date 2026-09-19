using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace BackendApi.Services.Storage
{
    public interface IStorageService
    {
        Task<string> UploadAsync(Stream stream, string fileName, string contentType, string folder = "items", CancellationToken ct = default);
        Task<string> UploadBase64Async(string base64Data, string folder = "items", CancellationToken ct = default);
        Task<bool> DeleteAsync(string objectName, CancellationToken ct = default);
        string GetPublicUrl(string objectName);
    }
}
