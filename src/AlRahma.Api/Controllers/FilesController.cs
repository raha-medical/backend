using AlRahma.Application.Files;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AlRahma.Api.Controllers;

[ApiController]
[Route("api/v1/files")]
[Produces("application/json")]
[Authorize]
public class FilesController(FileService files) : ControllerBase
{
    /// <summary>رابط رفع مؤقت إلى MinIO. الملف يُرفع من المتصفح مباشرة بـ PUT.</summary>
    [HttpPost("upload-url")]
    [ProducesResponseType<UploadUrlResponse>(201)]
    public async Task<IActionResult> UploadUrl(UploadUrlRequest req, CancellationToken ct) =>
        StatusCode(201, await files.CreateUploadUrlAsync(req, ct));

    /// <summary>تأكيد انتهاء الرفع. السيرفر يتحقق من وجود الملف وحجمه.</summary>
    [HttpPost("{fileId:guid}/complete")]
    [ProducesResponseType<FileStatusResponse>(200)]
    public Task<FileStatusResponse> Complete(Guid fileId, CancellationToken ct) => files.CompleteAsync(fileId, ct);

    /// <summary>صور النتائج الخارجية والوصفات لمراجع أو دور، مع روابط تنزيل مؤقتة (purpose = external_result | prescription).</summary>
    [HttpGet]
    public async Task<IActionResult> List([FromQuery(Name = "ticket_id")] Guid? ticketId, [FromQuery(Name = "patient_id")] Guid? patientId,
        [FromQuery(Name = "purpose")] string? purpose, CancellationToken ct) =>
        Ok(new { items = await files.ListPatientFilesAsync(ticketId, patientId, purpose, ct) });

    [HttpGet("{fileId:guid}/download-url")]
    [ProducesResponseType<DownloadUrlResponse>(200)]
    public Task<DownloadUrlResponse> DownloadUrl(Guid fileId, CancellationToken ct) => files.DownloadUrlAsync(fileId, ct);
}
