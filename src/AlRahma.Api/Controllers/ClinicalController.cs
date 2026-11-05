using AlRahma.Api.Infrastructure;
using AlRahma.Application.Clinical;
using AlRahma.Application.Common;
using AlRahma.Application.Abstractions;
using AlRahma.Domain.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AlRahma.Api.Controllers;

/// <summary>المعاينة والسجل الطبي والطلبات الطبية.</summary>
[ApiController]
[Produces("application/json")]
public class ClinicalController(ConsultationService consultations, ICurrentUser current) : ControllerBase
{
    [HttpPost("api/v1/consultations")]
    [Authorize(Policy = Permissions.ClinicalWrite)]
    [Idempotent]
    [ProducesResponseType<ConsultationDto>(201)]
    public async Task<IActionResult> Create(CreateConsultationRequest req, CancellationToken ct) => StatusCode(201, await consultations.CreateAsync(req, ct));

    [HttpGet("api/v1/consultations/{consultationId:guid}")]
    [Authorize(Policy = Permissions.ClinicalWrite)]
    [ProducesResponseType<ConsultationDto>(200)]
    public Task<ConsultationDto> Get(Guid consultationId, CancellationToken ct) => consultations.GetAsync(consultationId, ct);

    [HttpGet("api/v1/consultations")]
    [Authorize(Policy = Permissions.ClinicalWrite)]
    [ProducesResponseType<PagedResult<ConsultationDto>>(200)]
    public Task<PagedResult<ConsultationDto>> List([FromQuery(Name = "ticket_id")] Guid? ticketId, [FromQuery(Name = "patient_id")] Guid? patientId,
        [FromQuery(Name = "page")] int page = 1, [FromQuery(Name = "page_size")] int pageSize = 20, CancellationToken ct = default) =>
        consultations.ListAsync(ticketId, patientId, page, pageSize, ct);

    [HttpPatch("api/v1/consultations/{consultationId:guid}")]
    [Authorize(Policy = Permissions.ClinicalWrite)]
    [ProducesResponseType<ConsultationDto>(200)]
    public Task<ConsultationDto> Update(Guid consultationId, UpdateConsultationRequest req, CancellationToken ct) =>
        consultations.UpdateAsync(consultationId, req, ct);

    [HttpPost("api/v1/consultations/{consultationId:guid}/orders")]
    [Authorize(Policy = Permissions.ClinicalWrite)]
    [Idempotent]
    [ProducesResponseType<ConsultationDto>(201)]
    public async Task<IActionResult> AddOrders(Guid consultationId, AddOrdersRequest req, CancellationToken ct) =>
        StatusCode(201, await consultations.AddOrdersAsync(consultationId, req, ct));

    [HttpPost("api/v1/consultations/{consultationId:guid}/addenda")]
    [Authorize(Policy = Permissions.ClinicalWrite)]
    [ProducesResponseType<ConsultationDto>(201)]
    public async Task<IActionResult> AddAddendum(Guid consultationId, AddAddendumRequest req, CancellationToken ct) =>
        StatusCode(201, await consultations.AddAddendumAsync(consultationId, req, ct));

    /// <summary>طلب تحليل أو أشعة مباشر من الاستقبال (ورقة طبيب خارجي).</summary>
    [HttpPost("api/v1/medical-orders")]
    [Authorize(Policy = Policies.OrdersCreate)]
    [Idempotent]
    [ProducesResponseType<DirectOrderResponse>(201)]
    public async Task<IActionResult> DirectOrders(DirectOrderRequest req, CancellationToken ct) =>
        StatusCode(201, await consultations.CreateDirectOrdersAsync(req, ct));

    [HttpPost("api/v1/medical-orders/{orderItemId:guid}/cancel")]
    [Authorize]
    [ProducesResponseType<CancelOrderResponse>(200)]
    public Task<CancelOrderResponse> CancelOrder(Guid orderItemId, CancelOrderRequest req, CancellationToken ct) =>
        consultations.CancelOrderAsync(orderItemId, req, ct);

    /// <summary>السجل الطبي الكامل للمريض في كل الفروع (الأطباء والمدير العام).</summary>
    [HttpGet("api/v1/patients/{patientId:guid}/history")]
    [Authorize]
    [ProducesResponseType<PatientHistoryDto>(200)]
    public Task<PatientHistoryDto> History(Guid patientId, [FromQuery(Name = "page")] int page = 1, [FromQuery(Name = "page_size")] int pageSize = 20,
        CancellationToken ct = default)
    {
        if (!current.Has(Permissions.ClinicalWrite) && current.Role != Roles.Admin) throw AppException.Forbidden("السجل الطبي للأطباء والمدير العام");
        return consultations.HistoryAsync(patientId, page, pageSize, ct);
    }
}

/// <summary>المختبر.</summary>
[ApiController]
[Route("api/v1/lab")]
[Produces("application/json")]
public class LabController(LabService lab) : ControllerBase
{
    [HttpGet("worklist")]
    [Authorize(Policy = Policies.LabRead)]
    [ProducesResponseType<PagedResult<WorklistItemDto>>(200)]
    public Task<PagedResult<WorklistItemDto>> Worklist([FromQuery(Name = "status")] string? status, [FromQuery(Name = "date")] DateOnly? date,
        [FromQuery(Name = "q")] string? q, [FromQuery(Name = "page")] int page = 1, [FromQuery(Name = "page_size")] int pageSize = 20,
        CancellationToken ct = default) =>
        lab.WorklistAsync(new WorklistQuery(status, date, q, null, page, pageSize), ct);

    [HttpPost("orders/{orderItemId:guid}/collect-sample")]
    [Authorize(Policy = Permissions.LabWrite)]
    [ProducesResponseType<CollectSampleResponse>(200)]
    public Task<CollectSampleResponse> Collect(Guid orderItemId, CollectSampleRequest? req, CancellationToken ct) =>
        lab.CollectSampleAsync(orderItemId, req ?? new CollectSampleRequest(null), ct);

    [HttpPost("orders/{orderItemId:guid}/start")]
    [Authorize(Policy = Permissions.LabWrite)]
    [ProducesResponseType<OrderStatusResponse>(200)]
    public Task<OrderStatusResponse> Start(Guid orderItemId, CancellationToken ct) => lab.StartAsync(orderItemId, ct);

    [HttpGet("tests/{serviceId:guid}/template")]
    [Authorize]
    [ProducesResponseType<LabTemplateDto>(200)]
    public Task<LabTemplateDto> Template(Guid serviceId, CancellationToken ct) => lab.TemplateAsync(serviceId, ct);

    [HttpPut("tests/{serviceId:guid}/template")]
    [Authorize(Policy = Permissions.ServicesManage)]
    [ProducesResponseType<LabTemplateDto>(200)]
    public Task<LabTemplateDto> PutTemplate(Guid serviceId, PutLabTemplateRequest req, CancellationToken ct) => lab.PutTemplateAsync(serviceId, req, ct);

    [HttpPut("orders/{orderItemId:guid}/result")]
    [Authorize(Policy = Permissions.LabWrite)]
    [ProducesResponseType<LabResultDto>(200)]
    public Task<LabResultDto> PutResult(Guid orderItemId, PutLabResultRequest req, CancellationToken ct) => lab.PutResultAsync(orderItemId, req, ct);

    [HttpGet("orders/{orderItemId:guid}/result")]
    [Authorize(Policy = Policies.StaffOrPatient)]
    [ProducesResponseType<LabResultDto>(200)]
    public Task<LabResultDto> Result(Guid orderItemId, CancellationToken ct) => lab.ResultAsync(orderItemId, ct);
}

/// <summary>الأشعة والسونار.</summary>
[ApiController]
[Route("api/v1/radiology")]
[Produces("application/json")]
public class RadiologyController(RadiologyService radiology) : ControllerBase
{
    [HttpGet("worklist")]
    [Authorize(Policy = Policies.RadiologyRead)]
    [ProducesResponseType<PagedResult<WorklistItemDto>>(200)]
    public Task<PagedResult<WorklistItemDto>> Worklist([FromQuery(Name = "status")] string? status, [FromQuery(Name = "date")] DateOnly? date,
        [FromQuery(Name = "q")] string? q, [FromQuery(Name = "modality")] string? modality, [FromQuery(Name = "page")] int page = 1,
        [FromQuery(Name = "page_size")] int pageSize = 20, CancellationToken ct = default) =>
        radiology.WorklistAsync(new WorklistQuery(status, date, q, modality, page, pageSize), ct);

    [HttpPost("orders/{orderItemId:guid}/schedule")]
    [Authorize(Policy = Permissions.RadiologyWrite)]
    [ProducesResponseType<ScheduleResponse>(200)]
    public Task<ScheduleResponse> Schedule(Guid orderItemId, ScheduleRequest req, CancellationToken ct) => radiology.ScheduleAsync(orderItemId, req, ct);

    [HttpPost("orders/{orderItemId:guid}/performed")]
    [Authorize(Policy = Permissions.RadiologyWrite)]
    [ProducesResponseType<PerformedResponse>(200)]
    public Task<PerformedResponse> Performed(Guid orderItemId, CancellationToken ct) => radiology.PerformedAsync(orderItemId, ct);

    [HttpPut("orders/{orderItemId:guid}/report")]
    [Authorize(Policy = Permissions.RadiologyWrite)]
    [ProducesResponseType<RadiologyReportDto>(200)]
    public Task<RadiologyReportDto> PutReport(Guid orderItemId, PutRadiologyReportRequest req, CancellationToken ct) =>
        radiology.PutReportAsync(orderItemId, req, ct);

    [HttpGet("orders/{orderItemId:guid}/report")]
    [Authorize(Policy = Policies.StaffOrPatient)]
    [ProducesResponseType<RadiologyReportDto>(200)]
    public Task<RadiologyReportDto> Report(Guid orderItemId, CancellationToken ct) => radiology.ReportAsync(orderItemId, ct);
}

/// <summary>الصيدلية.</summary>
[ApiController]
[Route("api/v1/pharmacy")]
[Produces("application/json")]
public class PharmacyController(PharmacyService pharmacy) : ControllerBase
{
    [HttpGet("prescriptions/{code}")]
    [Authorize(Policy = Permissions.PharmacyDispense)]
    [ProducesResponseType<PrescriptionDto>(200)]
    public Task<PrescriptionDto> Get(string code, CancellationToken ct) => pharmacy.GetByCodeAsync(code, ct);

    [HttpPost("prescriptions/{prescriptionId:guid}/dispense")]
    [Authorize(Policy = Permissions.PharmacyDispense)]
    [Idempotent]
    [ProducesResponseType<PrescriptionDto>(200)]
    public Task<PrescriptionDto> Dispense(Guid prescriptionId, DispenseRequest req, CancellationToken ct) => pharmacy.DispenseAsync(prescriptionId, req, ct);

    [HttpGet("dispensed")]
    [Authorize(Policy = Policies.PharmacyRead)]
    [ProducesResponseType<PagedResult<PrescriptionDto>>(200)]
    public Task<PagedResult<PrescriptionDto>> Dispensed([FromQuery(Name = "date")] DateOnly? date, [FromQuery(Name = "page")] int page = 1,
        [FromQuery(Name = "page_size")] int pageSize = 20, CancellationToken ct = default) =>
        pharmacy.DispensedAsync(date, page, pageSize, ct);
}

/// <summary>دورة التحويل للخدمات (العقد 23). الصلاحية تُفحص في الخدمة حسب نوع الخدمة، والنطاق الفرع الحالي.</summary>
[ApiController]
[Produces("application/json")]
public class ReferralsController(ReferralService referrals) : ControllerBase
{
    [HttpGet("api/v1/referrals")]
    [Authorize]
    [ProducesResponseType<PagedResult<ReferralDto>>(200)]
    public Task<PagedResult<ReferralDto>> List([FromQuery(Name = "service")] string? service, [FromQuery(Name = "status")] string? status,
        [FromQuery(Name = "date")] DateOnly? date, [FromQuery(Name = "q")] string? q, [FromQuery(Name = "page")] int page = 1,
        [FromQuery(Name = "page_size")] int pageSize = 20, CancellationToken ct = default) =>
        referrals.ListAsync(service, status, date, q, page, pageSize, ct);

    [HttpGet("api/v1/referrals/summary")]
    [Authorize]
    [ProducesResponseType<ReferralSummaryDto>(200)]
    public Task<ReferralSummaryDto> Summary([FromQuery(Name = "service")] string? service, CancellationToken ct) => referrals.SummaryAsync(service, ct);

    [HttpPost("api/v1/referrals/{referralId:guid}/activate")]
    [Authorize]
    [ProducesResponseType<ReferralDto>(200)]
    public Task<ReferralDto> Activate(Guid referralId, CancellationToken ct) => referrals.ActivateAsync(referralId, ct);

    [HttpPost("api/v1/referrals/scan")]
    [Authorize]
    [ProducesResponseType<ScanReferralResponse>(200)]
    public Task<ScanReferralResponse> Scan(ScanReferralRequest req, CancellationToken ct) => referrals.ScanAsync(req, ct);

    [HttpPost("api/v1/me/referrals/{referralId:guid}/confirm")]
    [Authorize(Policy = Policies.Patient)]
    [ProducesResponseType<ReferralDto>(200)]
    public Task<ReferralDto> Confirm(Guid referralId, CancellationToken ct) => referrals.ConfirmAsync(referralId, ct);

    [HttpPost("api/v1/me/referrals/{referralId:guid}/external")]
    [Authorize(Policy = Policies.Patient)]
    [ProducesResponseType<ReferralDto>(200)]
    public Task<ReferralDto> External(Guid referralId, CancellationToken ct) => referrals.MarkExternalAsync(referralId, ct);
}
