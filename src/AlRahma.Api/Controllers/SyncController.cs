using AlRahma.Api.Infrastructure;
using AlRahma.Application.Offline;
using AlRahma.Domain.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AlRahma.Api.Controllers;

/// <summary>العمل أوفلاين: كتل أرقام الإيصالات، ودفعة المزامنة، ولقطة بيانات اليوم.</summary>
[ApiController]
[Produces("application/json")]
public class SyncController(SyncService sync) : ControllerBase
{
    /// <summary>حجز كتلة أرقام إيصالات للجهاز (تعيد الكتلة النشطة إن وُجدت).</summary>
    [HttpPost("api/v1/devices/{deviceId}/receipt-blocks")]
    [Authorize(Policy = Permissions.PaymentsCollect)]
    [ProducesResponseType<ReceiptBlockDto>(201)]
    public async Task<IActionResult> CreateBlock(string deviceId, CreateBlockRequest? req, CancellationToken ct)
    {
        var (block, created) = await sync.CreateBlockAsync(deviceId, req ?? new CreateBlockRequest(null), ct);
        return StatusCode(created ? 201 : 200, block);
    }

    [HttpPost("api/v1/devices/{deviceId}/receipt-blocks/{blockId:guid}/return")]
    [Authorize(Policy = Permissions.PaymentsCollect)]
    [ProducesResponseType<ReturnBlockResponse>(200)]
    public Task<ReturnBlockResponse> ReturnBlock(string deviceId, Guid blockId, ReturnBlockRequest? req, CancellationToken ct) =>
        sync.ReturnBlockAsync(deviceId, blockId, req ?? new ReturnBlockRequest(null), ct);

    /// <summary>رفع عمليات الـ Outbox المسجلة أوفلاين (حتى 500). كل عملية idempotent عبر op_id.</summary>
    [HttpPost("api/v1/sync/batch")]
    [Authorize]
    [ProducesResponseType<SyncBatchResponse>(200)]
    public Task<SyncBatchResponse> Batch(SyncBatchRequest req, CancellationToken ct) => sync.BatchAsync(req, ct);

    [HttpGet("api/v1/sync/snapshot")]
    [Authorize]
    [ProducesResponseType<SnapshotDto>(200)]
    public Task<SnapshotDto> Snapshot([FromQuery(Name = "since")] DateTimeOffset? since, [FromQuery(Name = "device_id")] string? deviceId,
        CancellationToken ct) =>
        sync.SnapshotAsync(since, deviceId, ct);
}
