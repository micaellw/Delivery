using BackendApi.Core.Constants;
using BackendApi.Core.Models.Response;
using BackendApi.Services.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Threading.Tasks;

namespace BackendApi.Controllers.Storage
{
    [ApiController]
    [Route("api/v1/[controller]")]
    [Authorize]
    public class StorageController : ControllerBase
    {
        private readonly IStorageService _storageService;

        public StorageController(IStorageService storageService)
        {
            _storageService = storageService;
        }

        [HttpPost("upload")]
        [RequestSizeLimit(10 * 1024 * 1024)] // 10MB limit
        public async Task<IActionResult> Upload(IFormFile file, [FromForm] string folder = "items")
        {
            if (file == null || file.Length == 0)
                return BadRequest(ApiResponse.Fail("ไม่มีไฟล์ถูกส่งมา"));

            using var stream = file.OpenReadStream();
            var objectName = await _storageService.UploadAsync(stream, file.FileName, file.ContentType, folder);
            
            return Ok(ApiResponse<string>.Ok(_storageService.GetPublicUrl(objectName)));
        }

        [HttpDelete("{*objectName}")]
        [Authorize(Roles = "Admin,StorePartner")]
        public async Task<IActionResult> Delete(string objectName)
        {
            var success = await _storageService.DeleteAsync(objectName);
            if (!success) return BadRequest(ApiResponse.Fail("ลบรูปไม่สำเร็จ"));
            return NoContent();
        }
    }
}



