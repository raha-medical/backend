using AlRahma.Api.Infrastructure;
using AlRahma.Application.Billing;
using AlRahma.Domain.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AlRahma.Api.Controllers;

/// <summary>الاستثناء والدفع الآجل (القسم 14).</summary>
[ApiController]
[Route("api/v1/deferrals")]
[Produces("application/json")]
[Authorize]
public class DeferralsController(DeferralService deferrals) : ControllerBase
{
    [HttpPost]
    [Authorize(Policy = Permissions.DeferralsGrant)]
    [Idempotent]
    [ProducesResponseType<DeferralDto>(201)]
    public async Task<IActionResult> Create(CreateDeferralRequest req, CancellationToken ct) => StatusCode(201, await deferrals.CreateAsync(req, ct));

    [HttpGet]
    [Authorize(Policy = Permissions.ReportsFinance)]
    public Task<DeferralListResult> List(
        [FromQuery(Name = "status")] string? status,
        [FromQuery(Name = "patient_id")] Guid? patientId,
        [FromQuery(Name = "branch_id")] Guid? branchId,
        [FromQuery(Name = "overdue")] bool? overdue,
        [FromQuery(Name = "page")] int page = 1,
        [FromQuery(Name = "page_size")] int pageSize = 20,
        CancellationToken ct = default) =>
        deferrals.ListAsync(new DeferralListQuery(status, patientId, branchId, overdue, page, pageSize), ct);

    [HttpGet("{deferralId:guid}")]
    [Authorize(Policy = Policies.DeferralsRead)]
    public Task<DeferralDto> Get(Guid deferralId, CancellationToken ct) => deferrals.GetAsync(deferralId, ct);

    [HttpPost("{deferralId:guid}/write-off")]
    public Task<DeferralDto> WriteOff(Guid deferralId, WriteOffRequest req, CancellationToken ct) => deferrals.WriteOffAsync(deferralId, req, ct);
}

/// <summary>مصاريف الفروع.</summary>
[ApiController]
[Route("api/v1/expenses")]
[Produces("application/json")]
[Authorize(Policy = Permissions.ExpensesManage)]
public class ExpensesController(ExpenseService expenses) : ControllerBase
{
    [HttpPost]
    [Idempotent]
    [ProducesResponseType<ExpenseDto>(201)]
    public async Task<IActionResult> Create(CreateExpenseRequest req, CancellationToken ct) => StatusCode(201, await expenses.CreateAsync(req, ct));

    [HttpGet]
    public Task<ExpenseListResult> List(
        [FromQuery(Name = "branch_id")] Guid? branchId,
        [FromQuery(Name = "category")] string? category,
        [FromQuery(Name = "paid_from")] string? paidFrom,
        [FromQuery(Name = "from")] DateOnly? from,
        [FromQuery(Name = "to")] DateOnly? to,
        [FromQuery(Name = "status")] string? status,
        [FromQuery(Name = "page")] int page = 1,
        [FromQuery(Name = "page_size")] int pageSize = 20,
        CancellationToken ct = default) =>
        expenses.ListAsync(new ExpenseListQuery(branchId, category, paidFrom, from, to, status, page, pageSize), ct);

    [HttpGet("{expenseId:guid}")]
    public Task<ExpenseDto> Get(Guid expenseId, CancellationToken ct) => expenses.GetAsync(expenseId, ct);

    [HttpPost("{expenseId:guid}/void")]
    public Task<ExpenseDto> Void(Guid expenseId, VoidExpenseRequest req, CancellationToken ct) => expenses.VoidAsync(expenseId, req, ct);
}
