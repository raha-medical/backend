using AlRahma.Api.Infrastructure;
using AlRahma.Application.Branches;
using AlRahma.Domain.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AlRahma.Api.Controllers;

[ApiController]
[Produces("application/json")]
[Authorize]
public class BranchesController(BranchService branches, ClosureService closures, BranchPortalService portal, BranchSettingsService settings)
    : ControllerBase
{
    [HttpGet("api/v1/branches")]
    public async Task<IActionResult> List([FromQuery(Name = "include_inactive")] bool includeInactive, CancellationToken ct) =>
        Ok(new { items = await branches.ListAsync(includeInactive, ct) });

    /// <summary>الفرع مع حالته اليوم (مفتوح، مغلق مؤقتاً مع السبب، موقوف).</summary>
    [HttpGet("api/v1/branches/{branchId:guid}")]
    [ProducesResponseType<BranchDto>(200)]
    public Task<BranchDto> Get(Guid branchId, CancellationToken ct) => branches.GetAsync(branchId, ct);

    /// <summary>حجوزات قائمة في أيام أغلقها جدول دوام الفرع.</summary>
    [HttpGet("api/v1/branches/{branchId:guid}/schedule-conflicts")]
    [ProducesResponseType<ScheduleConflictsDto>(200)]
    public Task<ScheduleConflictsDto> ScheduleConflicts(Guid branchId, [FromQuery(Name = "from")] DateOnly? from, [FromQuery(Name = "to")] DateOnly? to,
        CancellationToken ct) => portal.ScheduleConflictsAsync(branchId, from, to, ct);

    /// <summary>ما يوجد داخل الفرع: أقسامه وعياداته وأطباؤها، موظفوه، أرقام اليوم، وتنبيهات الإعداد الناقص.</summary>
    [HttpGet("api/v1/branches/{branchId:guid}/overview")]
    [ProducesResponseType<BranchOverviewDto>(200)]
    public Task<BranchOverviewDto> Overview(Guid branchId, CancellationToken ct) => portal.OverviewAsync(branchId, ct);

    /// <summary>سجل الإغلاقات المؤقتة للفرع كاملاً.</summary>
    [HttpGet("api/v1/branches/{branchId:guid}/closures")]
    public async Task<IActionResult> BranchClosures(Guid branchId, CancellationToken ct) =>
        Ok(new { items = await closures.ListBranchAsync(branchId, ct) });

    /// <summary>إغلاق الفرع مؤقتاً (صيانة، عطلة، افتتاح قريب...): يغلق كل أقسامه في الفترة.</summary>
    [HttpPost("api/v1/branches/{branchId:guid}/closures")]
    [Authorize(Policy = Permissions.ServicesManage)]
    [Idempotent]
    [ProducesResponseType<CreateBranchClosureResponse>(201)]
    public async Task<IActionResult> CloseBranch(Guid branchId, CreateClosureRequest req, CancellationToken ct) =>
        StatusCode(201, await closures.CreateBranchAsync(branchId, req, ct));

    /// <summary>إعادة فتح الفرع: ينهي الإغلاق الساري من اليوم، أو يلغي إغلاقاً قادماً.</summary>
    [HttpPost("api/v1/branches/{branchId:guid}/closures/{closureId:guid}/end")]
    [Authorize(Policy = Permissions.ServicesManage)]
    [ProducesResponseType<BranchClosureDto>(200)]
    public Task<BranchClosureDto> ReopenBranch(Guid branchId, Guid closureId, CancellationToken ct) =>
        closures.EndBranchAsync(branchId, closureId, ct);

    /// <summary>إعدادات الفرع: القيمة الفعالة لكل إعداد ومصدرها (الفرع أو العام).</summary>
    [HttpGet("api/v1/branches/{branchId:guid}/settings")]
    public async Task<IActionResult> BranchSettings(Guid branchId, CancellationToken ct) =>
        Ok(new { items = await settings.GetAsync(branchId, ct) });

    /// <summary>تخصيص إعدادات الفرع. القيمة null تحذف التخصيص ويعود الفرع للإعداد العام.</summary>
    [HttpPatch("api/v1/branches/{branchId:guid}/settings")]
    [Authorize(Policy = Permissions.SettingsEdit)]
    public async Task<IActionResult> UpdateBranchSettings(Guid branchId, UpdateSettingsRequest req, CancellationToken ct) =>
        Ok(new { items = await settings.UpdateAsync(branchId, req, ct) });

    /// <summary>الإعدادات العامة لكل الفروع (المدير العام).</summary>
    [HttpGet("api/v1/settings")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> GlobalSettings(CancellationToken ct) => Ok(new { items = await settings.GetAsync(null, ct) });

    [HttpPatch("api/v1/settings")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> UpdateGlobalSettings(UpdateSettingsRequest req, CancellationToken ct) =>
        Ok(new { items = await settings.UpdateAsync(null, req, ct) });

    // ------------------------------------------------------------------ تطبيق المريض (بدون تسجيل دخول)

    /// <summary>كل الفروع الفعالة مع حالتها اليوم، ليختار المريض فرعه.</summary>
    [HttpGet("api/v1/public/branches")]
    [AllowAnonymous]
    public async Task<IActionResult> PublicBranches([FromQuery(Name = "city")] string? city, CancellationToken ct) =>
        Ok(new { items = await portal.PublicListAsync(city, ct) });

    /// <summary>الواجهة الرئيسية للفرع في تطبيق المريض: معلوماته، حالته، أقسامه، الاختصاصات، والأطباء.</summary>
    [HttpGet("api/v1/public/branches/{branchId:guid}")]
    [AllowAnonymous]
    [ProducesResponseType<PublicBranchHomeDto>(200)]
    public Task<PublicBranchHomeDto> PublicBranchHome(Guid branchId, CancellationToken ct) => portal.PublicHomeAsync(branchId, ct);

    /// <summary>فرع جديد (المدير العام). الرمز (مثل B2) يدخل في أرقام الملفات والفواتير ولا يتغير.</summary>
    [HttpPost("api/v1/branches")]
    [Authorize(Policy = Permissions.BranchesCreate)]
    [Idempotent]
    [ProducesResponseType<BranchDto>(201)]
    public async Task<IActionResult> Create(CreateBranchRequest req, CancellationToken ct) =>
        StatusCode(201, await branches.CreateBranchAsync(req, ct));

    [HttpPatch("api/v1/branches/{branchId:guid}")]
    [Authorize(Policy = Permissions.BranchesEdit)]
    [ProducesResponseType<BranchDto>(200)]
    public Task<BranchDto> Update(Guid branchId, UpdateBranchRequest req, CancellationToken ct) =>
        branches.UpdateBranchAsync(branchId, req, ct);

    /// <summary>عيادات وأقسام الفرع. department: clinic | lab | radiology | pharmacy.</summary>
    [HttpGet("api/v1/branches/{branchId:guid}/clinics")]
    public async Task<IActionResult> Clinics(
        Guid branchId,
        [FromQuery(Name = "department")] string? department,
        [FromQuery(Name = "include_inactive")] bool includeInactive,
        CancellationToken ct) =>
        Ok(new { items = await branches.ListClinicsAsync(branchId, department, includeInactive, ct) });

    /// <summary>عيادة أو قسم جديد: عيادة طبيب، مختبر، أشعة وسونار، أو صيدلية.</summary>
    [HttpPost("api/v1/branches/{branchId:guid}/clinics")]
    [Authorize(Policy = Permissions.ServicesManage)]
    [Idempotent]
    [ProducesResponseType<ClinicDto>(201)]
    public async Task<IActionResult> CreateClinic(Guid branchId, CreateClinicRequest req, CancellationToken ct) =>
        StatusCode(201, await branches.CreateClinicAsync(branchId, req, ct));

    [HttpPatch("api/v1/clinics/{clinicId:guid}")]
    [Authorize(Policy = Permissions.ServicesManage)]
    [ProducesResponseType<ClinicDto>(200)]
    public Task<ClinicDto> UpdateClinic(Guid clinicId, UpdateClinicRequest req, CancellationToken ct) =>
        branches.UpdateClinicAsync(clinicId, req, ct);

    [HttpGet("api/v1/clinics/{clinicId:guid}")]
    [ProducesResponseType<ClinicDto>(200)]
    public Task<ClinicDto> GetClinic(Guid clinicId, CancellationToken ct) => branches.GetClinicAsync(clinicId, ct);

    /// <summary>سجل الإغلاقات المؤقتة للعيادة أو القسم.</summary>
    [HttpGet("api/v1/clinics/{clinicId:guid}/closures")]
    public async Task<IActionResult> Closures(Guid clinicId, CancellationToken ct) =>
        Ok(new { items = await closures.ListAsync(clinicId, ct) });

    /// <summary>إغلاق مؤقت (سفر، صيانة، عطلة...) لفترة، أو حتى إعادة الفتح إذا لم يُحدد ends_on.</summary>
    [HttpPost("api/v1/clinics/{clinicId:guid}/closures")]
    [Authorize(Policy = Permissions.ServicesManage)]
    [Idempotent]
    [ProducesResponseType<CreateClosureResponse>(201)]
    public async Task<IActionResult> Close(Guid clinicId, CreateClosureRequest req, CancellationToken ct) =>
        StatusCode(201, await closures.CreateAsync(clinicId, req, ct));

    /// <summary>إعادة الفتح: ينهي الإغلاق الساري من اليوم، أو يلغي إغلاقاً قادماً.</summary>
    [HttpPost("api/v1/clinics/{clinicId:guid}/closures/{closureId:guid}/end")]
    [Authorize(Policy = Permissions.ServicesManage)]
    [ProducesResponseType<ClosureDto>(200)]
    public Task<ClosureDto> Reopen(Guid clinicId, Guid closureId, CancellationToken ct) => closures.EndAsync(clinicId, closureId, ct);
}
