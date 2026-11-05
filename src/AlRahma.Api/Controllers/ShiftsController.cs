using AlRahma.Api.Infrastructure;
using AlRahma.Application.Billing;
using AlRahma.Application.Common;
using AlRahma.Domain.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AlRahma.Api.Controllers;

/// <summary>الورديات والإغلاق الأعمى (القسم 10 من العقد).</summary>
[ApiController]
[Route("api/v1/shifts")]
[Produces("application/json")]
[Authorize]
public class ShiftsController(ShiftService shifts) : ControllerBase
{
    [HttpPost("open")]
    [Authorize(Policy = Permissions.PaymentsCollect)]
    [Idempotent]
    [ProducesResponseType<ShiftDto>(201)]
    public async Task<IActionResult> Open(OpenShiftRequest req, CancellationToken ct) => StatusCode(201, await shifts.OpenAsync(req, ct));

    [HttpGet("current")]
    public Task<ShiftDto> Current(CancellationToken ct) => shifts.CurrentAsync(ct);

    [HttpPost("{shiftId:guid}/close")]
    [Idempotent]
    public Task<ShiftDto> Close(Guid shiftId, CloseShiftRequest req, CancellationToken ct) => shifts.CloseAsync(shiftId, req, ct);

    [HttpGet("{shiftId:guid}/reconciliation")]
    [Authorize(Policy = Permissions.ShiftsReview)]
    public Task<ReconciliationDto> Reconciliation(Guid shiftId, CancellationToken ct) => shifts.ReconciliationAsync(shiftId, ct);

    [HttpPost("{shiftId:guid}/review")]
    [Authorize(Policy = Permissions.ShiftsReview)]
    public Task<ReconciliationDto> Review(Guid shiftId, ReviewShiftRequest req, CancellationToken ct) => shifts.ReviewAsync(shiftId, req, ct);

    [HttpGet]
    [Authorize(Policy = Permissions.ShiftsReview)]
    public Task<PagedResult<ShiftListItem>> List(
        [FromQuery(Name = "status")] string? status,
        [FromQuery(Name = "cashier_id")] Guid? cashierId,
        [FromQuery(Name = "from")] DateOnly? from,
        [FromQuery(Name = "to")] DateOnly? to,
        [FromQuery(Name = "clinic_id")] Guid? clinicId,
        [FromQuery(Name = "page")] int page = 1,
        [FromQuery(Name = "page_size")] int pageSize = 20,
        CancellationToken ct = default) =>
        shifts.ListAsync(new ShiftListQuery(status, cashierId, from, to, page, pageSize, clinicId), ct);
}
