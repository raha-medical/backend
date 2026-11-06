using AlRahma.Api.Infrastructure;
using AlRahma.Application.Assistant;
using AlRahma.Application.Backups;
using AlRahma.Application.Common;
using AlRahma.Application.Notifications;
using AlRahma.Domain.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AlRahma.Api.Controllers;

/// <summary>المساعد الذكي لتطبيق المريض. مفتاح المزود يبقى في السيرفر.</summary>
[ApiController]
[Route("api/v1/assistant")]
[Produces("application/json")]
[Authorize(Policy = Policies.Patient)]
public class AssistantController(AssistantService assistant) : ControllerBase
{
    [HttpGet("status")]
    [ProducesResponseType<AssistantStatusDto>(200)]
    public Task<AssistantStatusDto> Status(CancellationToken ct) => assistant.StatusAsync(ct);

    [HttpPost("chat")]
    [RequestSizeLimit(8 * 1024 * 1024)]
    [ProducesResponseType<AssistantChatResponse>(200)]
    public Task<AssistantChatResponse> Chat(AssistantChatRequest req, CancellationToken ct) => assistant.ChatAsync(req, ct);
}

/// <summary>استخدام المساعد للمدير العام: أرقام يومية فقط، بلا أي نص من المحادثات.</summary>
[ApiController]
[Route("api/v1/admin/assistant")]
[Produces("application/json")]
[Authorize(Roles = Roles.Admin)]
public class AdminAssistantController(AssistantService assistant) : ControllerBase
{
    [HttpGet("stats")]
    [ProducesResponseType<AssistantStatsDto>(200)]
    public Task<AssistantStatsDto> Stats([FromQuery(Name = "from")] DateOnly? from, [FromQuery(Name = "to")] DateOnly? to, CancellationToken ct) =>
        assistant.StatsAsync(from, to, ct);
}

/// <summary>قراءة فرع مدير الفرع: إشعارات فرعه وملخص حالة النسخ الاحتياطي.</summary>
[ApiController]
[Route("api/v1/branch")]
[Produces("application/json")]
public class BranchManagerController(NotificationService notifications, BackupService backups) : ControllerBase
{
    [HttpGet("notifications")]
    [Authorize(Policy = Permissions.NotificationsBranchView)]
    [ProducesResponseType<PagedResult<BranchNotificationDto>>(200)]
    public Task<PagedResult<BranchNotificationDto>> Notifications([FromQuery(Name = "type")] string? type, [FromQuery(Name = "page")] int page = 1,
        [FromQuery(Name = "page_size")] int pageSize = 20, CancellationToken ct = default) =>
        notifications.BranchFeedAsync(type, page, pageSize, ct);

    [HttpGet("backup-status")]
    [Authorize(Policy = Permissions.BackupsView)]
    [ProducesResponseType<BackupSummaryDto>(200)]
    public Task<BackupSummaryDto> BackupStatus(CancellationToken ct) => backups.SummaryAsync(ct);
}
