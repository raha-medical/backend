using AlRahma.Api.Infrastructure;
using AlRahma.Application.Common;
using AlRahma.Application.Reports;
using AlRahma.Domain.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AlRahma.Api.Controllers;

/// <summary>التقارير ولوحة المدير العام (القسم 19). المدير العام يرى كل الفروع، وغيره فروعه المسندة إليه.</summary>
[ApiController]
[Route("api/v1/reports")]
[Produces("application/json")]
[Authorize]
public class ReportsController(BranchSettlementService settlements, RevenueService revenue, DoctorReportService doctors, DashboardService dashboard,
    AuditLogService auditLogs, EntitlementService entitlements) : ControllerBase
{
    /// <summary>خريطة المدير العام: الحالة الحية ومؤشرات كل فرع.</summary>
    [HttpGet("branches-overview")]
    [Authorize(Roles = Roles.Admin)]
    [ProducesResponseType<BranchesOverview>(200)]
    public Task<BranchesOverview> BranchesOverview(CancellationToken ct) => entitlements.BranchesOverviewAsync(ct);

    [HttpGet("statement")]
    [Authorize(Policy = Permissions.ReportsFinance)]
    [ProducesResponseType<FinancialStatement>(200)]
    public Task<FinancialStatement> Statement([FromQuery(Name = "branch_id")] Guid? branchId, [FromQuery(Name = "from")] DateOnly? from, [FromQuery(Name = "to")] DateOnly? to,
        [FromQuery(Name = "group")] string? group, CancellationToken ct) => entitlements.FinancialStatementAsync(branchId, from, to, group, ct);

    [HttpGet("payments")]
    [Authorize(Policy = Permissions.ReportsFinance)]
    [ProducesResponseType<PagedResult<PaymentRow>>(200)]
    public Task<PagedResult<PaymentRow>> Payments([FromQuery(Name = "branch_id")] Guid? branchId, [FromQuery(Name = "from")] DateOnly? from, [FromQuery(Name = "to")] DateOnly? to,
        [FromQuery(Name = "page")] int page = 1, [FromQuery(Name = "page_size")] int pageSize = 50, CancellationToken ct = default) =>
        entitlements.PaymentsAsync(branchId, from, to, page, pageSize, ct);

    [HttpGet("entitlements")]
    [Authorize(Policy = Permissions.ReportsFinance)]
    [ProducesResponseType<EntitlementStatement>(200)]
    public Task<EntitlementStatement> Entitlements([FromQuery(Name = "branch_id")] Guid? branchId, [FromQuery(Name = "from")] DateOnly? from, [FromQuery(Name = "to")] DateOnly? to, CancellationToken ct) =>
        entitlements.StatementAsync(branchId, from, to, ct);

    [HttpGet("pay-plans")]
    [Authorize(Policy = Permissions.ReportsFinance)]
    public async Task<IActionResult> PayPlans([FromQuery(Name = "branch_id")] Guid? branchId, CancellationToken ct) => Ok(new { items = await entitlements.PlansAsync(branchId, ct), staff = await entitlements.PlanStaffAsync(branchId, ct) });

    [HttpPost("pay-plans")]
    [Authorize(Policy = Permissions.ExpensesManage)]
    [ProducesResponseType<PayPlanDto>(200)]
    public Task<PayPlanDto> SavePayPlan([FromQuery(Name = "branch_id")] Guid? branchId, PayPlanRequest req, CancellationToken ct) => entitlements.SavePlanAsync(branchId, req, ct);

    [HttpGet("payouts")]
    [Authorize(Policy = Permissions.ReportsFinance)]
    public async Task<IActionResult> Payouts([FromQuery(Name = "branch_id")] Guid? branchId, [FromQuery(Name = "from")] DateOnly? from, [FromQuery(Name = "to")] DateOnly? to, CancellationToken ct) =>
        Ok(new { items = await entitlements.PayoutsAsync(branchId, from, to, ct) });

    [HttpPost("payouts")]
    [Authorize(Policy = Permissions.ExpensesManage)]
    [Idempotent]
    [ProducesResponseType<PayoutDto>(201)]
    public async Task<IActionResult> RecordPayout([FromQuery(Name = "branch_id")] Guid? branchId, PayoutRequest req, CancellationToken ct) =>
        StatusCode(201, await entitlements.RecordPayoutAsync(branchId, req, ct));

    /// <summary>لوحة المدير العام: كل فرع بإيراده ومصاريفه ونشاطه وتنبيهاته.</summary>
    [HttpGet("dashboard")]
    [Authorize(Policy = Permissions.ReportsFinance)]
    public Task<DashboardDto> Dashboard([FromQuery(Name = "date")] DateOnly? date, [FromQuery(Name = "branch_id")] Guid? branchId, CancellationToken ct) =>
        dashboard.GetAsync(date, branchId, ct);

    /// <summary>الإيراد حسب القسم وطريقة الدفع والفرع: ليوم (date) أو لفترة (from/to).</summary>
    [HttpGet("daily-revenue")]
    [Authorize(Policy = Permissions.ReportsFinance)]
    public Task<RevenueReport> DailyRevenue(
        [FromQuery(Name = "branch_id")] Guid? branchId,
        [FromQuery(Name = "date")] DateOnly? date,
        [FromQuery(Name = "from")] DateOnly? from,
        [FromQuery(Name = "to")] DateOnly? to,
        CancellationToken ct) =>
        revenue.ReportAsync(branchId, date, from, to, ct);

    /// <summary>أرباح الأطباء. الطبيب يرى أرباحه فقط.</summary>
    [HttpGet("doctor-earnings")]
    public Task<object> DoctorEarnings(
        [FromQuery(Name = "doctor_id")] Guid? doctorId,
        [FromQuery(Name = "from")] DateOnly? from,
        [FromQuery(Name = "to")] DateOnly? to,
        [FromQuery(Name = "branch_id")] Guid? branchId,
        CancellationToken ct) =>
        doctors.EarningsAsync(doctorId, from, to, branchId, ct);

    [HttpGet("no-shows")]
    [Authorize(Policy = Permissions.ReportsFinance)]
    public Task<NoShowReport> NoShows(
        [FromQuery(Name = "from")] DateOnly? from,
        [FromQuery(Name = "to")] DateOnly? to,
        [FromQuery(Name = "doctor_id")] Guid? doctorId,
        [FromQuery(Name = "branch_id")] Guid? branchId,
        CancellationToken ct) =>
        doctors.NoShowsAsync(from, to, doctorId, branchId, ct);

    [HttpGet("audit-logs")]
    [Authorize(Policy = Permissions.AuditRead)]
    public Task<PagedResult<AuditLogDto>> AuditLogs(
        [FromQuery(Name = "branch_id")] Guid? branchId,
        [FromQuery(Name = "user_id")] Guid? userId,
        [FromQuery(Name = "action")] string? action,
        [FromQuery(Name = "entity_type")] string? entityType,
        [FromQuery(Name = "entity_id")] Guid? entityId,
        [FromQuery(Name = "from")] DateOnly? from,
        [FromQuery(Name = "to")] DateOnly? to,
        [FromQuery(Name = "page")] int page = 1,
        [FromQuery(Name = "page_size")] int pageSize = 50,
        CancellationToken ct = default) =>
        auditLogs.ListAsync(new AuditLogQuery(branchId, userId, action, entityType, entityId, from, to, page, pageSize), ct);

    /// <summary>التسويات بين الفروع: رصيد مريض قُبض في فرع واستُخدم في آخر، وأرصدة المرضى عند كل فرع.</summary>
    [HttpGet("branch-settlements")]
    [Authorize(Policy = Permissions.ReportsFinance)]
    [ProducesResponseType<BranchSettlementReport>(200)]
    public Task<BranchSettlementReport> BranchSettlements(
        [FromQuery(Name = "from")] DateOnly? from,
        [FromQuery(Name = "to")] DateOnly? to,
        [FromQuery(Name = "branch_id")] Guid? branchId,
        CancellationToken ct) =>
        settlements.ReportAsync(from, to, branchId, ct);
}
