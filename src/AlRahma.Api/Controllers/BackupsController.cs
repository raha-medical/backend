using AlRahma.Application.Backups;
using AlRahma.Application.Common;
using AlRahma.Domain.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AlRahma.Api.Controllers;

/// <summary>
/// النسخ الاحتياطي (المدير العام فقط). لا تنزيل للنسخ من الواجهة، والاسترجاع من السيرفر فقط.
/// "تشغيل الآن" يسجل طلباً ينفذه خلال دقيقة سكربت النسخ (Docker) أو المنفذ الأصلي داخل الـ API (Backup:Mode=native).
/// </summary>
[ApiController]
[Route("api/v1/admin/backups")]
[Produces("application/json")]
[Authorize(Roles = Roles.Admin)]
public class BackupsController(BackupService backups) : ControllerBase
{
    [HttpGet("status")]
    [ProducesResponseType<BackupStatusDto>(200)]
    public Task<BackupStatusDto> Status(CancellationToken ct) => backups.StatusAsync(ct);

    [HttpGet]
    [ProducesResponseType<PagedResult<BackupRunDto>>(200)]
    public Task<PagedResult<BackupRunDto>> List(
        [FromQuery(Name = "type")] string? type,
        [FromQuery(Name = "status")] string? status,
        [FromQuery(Name = "page")] int page = 1,
        [FromQuery(Name = "page_size")] int pageSize = 20,
        CancellationToken ct = default) =>
        backups.ListAsync(type, status, new PageQuery(page, pageSize), ct);

    /// <summary>وقت النسخ اليومي (بغداد، HH:mm). يُطبق فوراً بلا إعادة تشغيل.</summary>
    [HttpPut("settings")]
    [ProducesResponseType<BackupStatusDto>(200)]
    public Task<BackupStatusDto> UpdateSettings([FromBody] UpdateBackupSettingsRequest req, CancellationToken ct) =>
        backups.UpdateSettingsAsync(req, ct);

    [HttpPost("run")]
    [ProducesResponseType<BackupRunDto>(202)]
    public async Task<IActionResult> Run([FromBody] RunBackupRequest? req, CancellationToken ct) =>
        StatusCode(202, await backups.RequestRunAsync(req ?? new RunBackupRequest(null), ct));
}
