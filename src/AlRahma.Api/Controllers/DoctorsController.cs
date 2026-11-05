using AlRahma.Api.Infrastructure;
using AlRahma.Application.Doctors;
using AlRahma.Domain.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AlRahma.Api.Controllers;

[ApiController]
[Route("api/v1/doctors")]
[Produces("application/json")]
[Authorize]
public class DoctorsController(DoctorService doctors) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery(Name = "clinic_id")] Guid? clinicId,
        [FromQuery(Name = "specialty")] string? specialty,
        [FromQuery(Name = "q")] string? q,
        CancellationToken ct) =>
        Ok(new { items = await doctors.ListAsync(clinicId, specialty, q, ct) });

    [HttpGet("{doctorId:guid}")]
    [ProducesResponseType<DoctorDto>(200)]
    public Task<DoctorDto> Get(Guid doctorId, CancellationToken ct) => doctors.GetAsync(doctorId, ct);

    /// <summary>ربط صورة مرفوعة (purpose = doctor_photo) بالطبيب. للمدير أو الطبيب نفسه.</summary>
    [HttpPut("{doctorId:guid}/photo")]
    [Idempotent]
    [ProducesResponseType<DoctorDto>(200)]
    public Task<DoctorDto> SetPhoto(Guid doctorId, SetPhotoRequest req, CancellationToken ct) =>
        doctors.SetPhotoAsync(doctorId, req, ct);

    [HttpDelete("{doctorId:guid}/photo")]
    [ProducesResponseType(204)]
    public async Task<IActionResult> DeletePhoto(Guid doctorId, CancellationToken ct)
    {
        await doctors.DeletePhotoAsync(doctorId, ct);
        return NoContent();
    }

    /// <summary>تاريخ نسب الطبيب والنسبة السارية اليوم.</summary>
    [HttpGet("{doctorId:guid}/commissions")]
    [Authorize(Policy = Permissions.ReportsFinance)]
    [ProducesResponseType<CommissionHistoryDto>(200)]
    public Task<CommissionHistoryDto> Commissions(Guid doctorId, CancellationToken ct) => doctors.GetCommissionsAsync(doctorId, ct);

    /// <summary>نسبة جديدة تسري من تاريخ محدد (اليوم أو بعده). لا تغير الفواتير السابقة.</summary>
    [HttpPost("{doctorId:guid}/commissions")]
    [Authorize(Roles = Roles.Admin)]
    [ProducesResponseType<CommissionRateDto>(201)]
    public async Task<IActionResult> AddCommission(Guid doctorId, AddCommissionRequest req, CancellationToken ct) =>
        StatusCode(201, await doctors.AddCommissionAsync(doctorId, req, ct));

    /// <summary>إعدادات الطبيب: «ممنوع الإضافي»، مهلة المتأخر، مدة المراجعة وسعرها، العربون الإلكتروني.</summary>
    [HttpGet("{doctorId:guid}/settings")]
    [ProducesResponseType<DoctorSettingsDto>(200)]
    public Task<DoctorSettingsDto> Settings(Guid doctorId, CancellationToken ct) => doctors.GetSettingsAsync(doctorId, ct);

    [HttpPut("{doctorId:guid}/settings")]
    [Authorize(Policy = Permissions.ServicesManage)]
    [ProducesResponseType<DoctorSettingsDto>(200)]
    public Task<DoctorSettingsDto> PutSettings(Guid doctorId, PutDoctorSettingsRequest req, CancellationToken ct) =>
        doctors.PutSettingsAsync(doctorId, req, ct);

    [HttpGet("{doctorId:guid}/schedule")]
    [ProducesResponseType<WeeklyScheduleDto>(200)]
    public Task<WeeklyScheduleDto> Schedule(Guid doctorId, CancellationToken ct) => doctors.GetScheduleAsync(doctorId, ct);

    /// <summary>استبدال الجدول الأسبوعي كاملاً.</summary>
    [HttpPut("{doctorId:guid}/schedule")]
    [Authorize(Policy = Permissions.ServicesManage)]
    [ProducesResponseType<WeeklyScheduleDto>(200)]
    public Task<WeeklyScheduleDto> PutSchedule(Guid doctorId, PutScheduleRequest req, CancellationToken ct) =>
        doctors.PutScheduleAsync(doctorId, req, ct);

    /// <summary>وجبات الطبيب الفعلية في فترة (الافتراضي: 7 أيام من اليوم).</summary>
    [HttpGet("{doctorId:guid}/sessions")]
    public async Task<IActionResult> Sessions(
        Guid doctorId,
        [FromQuery(Name = "from")] DateOnly? from,
        [FromQuery(Name = "to")] DateOnly? to,
        CancellationToken ct) =>
        Ok(new { doctor_id = doctorId, items = await doctors.GetSessionsAsync(doctorId, from, to, ct) });

    /// <summary>إلغاء وجبات الطبيب في يوم (إجازة). الحجوزات غير المدفوعة تُلغى.</summary>
    [HttpPost("{doctorId:guid}/sessions/{date}/cancel")]
    [Authorize(Policy = Permissions.ServicesManage)]
    [ProducesResponseType<CancelSessionResponse>(200)]
    public Task<CancelSessionResponse> CancelDay(Guid doctorId, DateOnly date, CancelSessionRequest req, CancellationToken ct) =>
        doctors.CancelDayAsync(doctorId, date, req, ct);
}
