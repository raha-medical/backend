using AlRahma.Api.Infrastructure;
using AlRahma.Application.Common;
using AlRahma.Application.Patients;
using AlRahma.Domain.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AlRahma.Api.Controllers;

[ApiController]
[Route("api/v1/patients")]
[Produces("application/json")]
[Authorize]
public class PatientsController(PatientService patients) : ControllerBase
{
    /// <summary>ملف مريض جديد. patient_id اختياري (يُرسل عند الإنشاء أوفلاين).</summary>
    [HttpPost]
    [Authorize(Policy = Permissions.PatientsWrite)]
    [Idempotent]
    [ProducesResponseType<CreatePatientResponse>(201)]
    public async Task<IActionResult> Create(CreatePatientRequest req, CancellationToken ct) =>
        StatusCode(201, await patients.CreateAsync(req, ct));

    /// <summary>البحث على مستوى كل الفروع بالهاتف أو الاسم أو رقم الملف.</summary>
    [HttpGet("search")]
    [ProducesResponseType<PagedResult<PatientSearchItem>>(200)]
    public Task<PagedResult<PatientSearchItem>> Search(
        [FromQuery(Name = "phone")] string? phone,
        [FromQuery(Name = "q")] string? q,
        [FromQuery(Name = "file_number")] string? fileNumber,
        [FromQuery(Name = "page")] int page = 1,
        [FromQuery(Name = "page_size")] int pageSize = 20,
        CancellationToken ct = default) =>
        patients.SearchAsync(phone, q, fileNumber, new PageQuery(page, pageSize), ct);

    [HttpGet("{patientId:guid}")]
    [ProducesResponseType<PatientDto>(200)]
    public Task<PatientDto> Get(Guid patientId, CancellationToken ct) => patients.GetAsync(patientId, ct);

    [HttpPatch("{patientId:guid}")]
    [Authorize(Policy = Permissions.PatientsWrite)]
    [ProducesResponseType<PatientDto>(200)]
    public Task<PatientDto> Update(Guid patientId, UpdatePatientRequest req, CancellationToken ct) =>
        patients.UpdateAsync(patientId, req, ct);

    /// <summary>دمج ملفين مكررين: كل السجلات تنتقل للملف الأساسي.</summary>
    [HttpPost("merge")]
    [Authorize(Policy = Permissions.PatientsMerge)]
    [Idempotent]
    [ProducesResponseType<MergePatientsResponse>(200)]
    public Task<MergePatientsResponse> Merge(MergePatientsRequest req, CancellationToken ct) => patients.MergeAsync(req, ct);

    [HttpGet("{patientId:guid}/ban-status")]
    [ProducesResponseType<BanStatusDto>(200)]
    public Task<BanStatusDto> BanStatus(Guid patientId, CancellationToken ct) => patients.BanStatusAsync(patientId, ct);

    [HttpPost("{patientId:guid}/unban")]
    [Authorize(Policy = Permissions.PatientsUnban)]
    [Idempotent]
    [ProducesResponseType<UnbanResponse>(200)]
    public Task<UnbanResponse> Unban(Guid patientId, UnbanRequest req, CancellationToken ct) => patients.UnbanAsync(patientId, req, ct);
}
