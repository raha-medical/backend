using AlRahma.Api.Infrastructure;
using AlRahma.Application.Abstractions;
using AlRahma.Application.Bookings;
using AlRahma.Application.Common;
using AlRahma.Domain.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AlRahma.Api.Controllers;

[ApiController]
[Produces("application/json")]
public class BookingsController(BookingService bookings, AlRahma.Application.Queue.QueueService queue, ICurrentUser current,
    DoctorCancelOptionsService cancelOptions) : ControllerBase
{
    /// <summary>5.1-ب: خيارات المراجع بعد إلغاء الطبيب جدوله: rebook (أقرب موعد من الرصيد) | credit (رصيد) | refund (البوابة).</summary>
    [HttpGet("api/v1/me/bookings/{bookingId:guid}/cancellation-options")]
    [Authorize(Policy = Policies.Patient)]
    [ProducesResponseType<CancelOptionsDto>(200)]
    public Task<CancelOptionsDto> CancelOptions(Guid bookingId, CancellationToken ct) => cancelOptions.OptionsAsync(bookingId, ct);

    [HttpPost("api/v1/me/bookings/{bookingId:guid}/cancellation-choice")]
    [Authorize(Policy = Policies.Patient)]
    [ProducesResponseType<CancelChoiceResponse>(200)]
    public Task<CancelChoiceResponse> CancelChoice(Guid bookingId, CancelChoiceRequest req, CancellationToken ct) =>
        cancelOptions.ChooseAsync(bookingId, req, ct);

    /// <summary>حجز هاتفي من الاستقبال (queue.manage)، أو حجز المريض من التطبيق بتوكنه.</summary>
    [HttpPost("api/v1/bookings")]
    [Authorize(Policy = Policies.StaffOrPatient)]
    [Idempotent]
    [ProducesResponseType<BookingDto>(201)]
    public async Task<IActionResult> Create(CreateBookingRequest req, CancellationToken ct)
    {
        RequireStaffPermission();
        return StatusCode(201, await bookings.CreateAsync(req, ct));
    }

    /// <summary>حجوزات مرضى حساب التطبيق.</summary>
    [HttpGet("api/v1/bookings/my")]
    [Authorize(Policy = Policies.Patient)]
    public async Task<IActionResult> My([FromQuery(Name = "patient_id")] Guid? patientId, [FromQuery(Name = "scope")] string? scope, CancellationToken ct) =>
        Ok(new { items = await bookings.MyAsync(patientId, scope, ct) });

    private void RequireStaffPermission()
    {
        if (!current.IsPatient && !current.Has(Permissions.QueueManage)) throw AppException.Forbidden("لا تملك صلاحية لهذه العملية");
    }

    /// <summary>حجوزات مريض أو وجبة.</summary>
    [HttpGet("api/v1/bookings")]
    [Authorize]
    public async Task<IActionResult> List(
        [FromQuery(Name = "patient_id")] Guid? patientId,
        [FromQuery(Name = "session_id")] Guid? sessionId,
        CancellationToken ct) =>
        Ok(new { items = await bookings.ListAsync(patientId, sessionId, ct) });

    [HttpGet("api/v1/bookings/{bookingId:guid}")]
    [Authorize(Policy = Policies.StaffOrPatient)]
    [ProducesResponseType<BookingDto>(200)]
    public Task<BookingDto> Get(Guid bookingId, CancellationToken ct) => bookings.GetAsync(bookingId, ct);

    [HttpPost("api/v1/bookings/{bookingId:guid}/cancel")]
    [Authorize(Policy = Policies.StaffOrPatient)]
    [ProducesResponseType<CancelBookingResponse>(200)]
    public Task<CancelBookingResponse> Cancel(Guid bookingId, CancelBookingRequest req, CancellationToken ct)
    {
        RequireStaffPermission();
        return bookings.CancelAsync(bookingId, req, ct);
    }

    /// <summary>معاينة الإلغاء قبل التأكيد: الرسوم والاحتساب غياباً وأثر المبلغ المدفوع والشروط بالعربي.</summary>
    [HttpGet("api/v1/bookings/{bookingId:guid}/cancel-preview")]
    [Authorize(Policy = Policies.StaffOrPatient)]
    [ProducesResponseType<CancelPreviewDto>(200)]
    public Task<CancelPreviewDto> CancelPreview(Guid bookingId, CancellationToken ct)
    {
        RequireStaffPermission();
        return bookings.CancelPreviewAsync(bookingId, ct);
    }

    /// <summary>معاينة تغيير الموعد: المرات المتبقية والمهلة وأثر الدفع. session_id اختياري (الموعد الجديد المقترح).</summary>
    [HttpGet("api/v1/bookings/{bookingId:guid}/reschedule-preview")]
    [Authorize(Policy = Policies.StaffOrPatient)]
    [ProducesResponseType<ReschedulePreviewDto>(200)]
    public Task<ReschedulePreviewDto> ReschedulePreview(Guid bookingId, [FromQuery(Name = "session_id")] Guid? sessionId, CancellationToken ct)
    {
        RequireStaffPermission();
        return bookings.ReschedulePreviewAsync(bookingId, sessionId, ct);
    }

    /// <summary>تغيير موعد الحجز لوجبة أخرى عند نفس الطبيب (نفس الحجز بدفعه).</summary>
    [HttpPost("api/v1/bookings/{bookingId:guid}/reschedule")]
    [Authorize(Policy = Policies.StaffOrPatient)]
    [Idempotent(required: false)]
    [ProducesResponseType<BookingDto>(200)]
    public Task<BookingDto> Reschedule(Guid bookingId, RescheduleBookingRequest req, CancellationToken ct)
    {
        RequireStaffPermission();
        return bookings.RescheduleAsync(bookingId, req, ct);
    }

    /// <summary>تثبيت الحضور الذاتي: المريض يمسح رمز QR شاشة الانتظار من التطبيق.</summary>
    [HttpPost("api/v1/bookings/{bookingId:guid}/self-check-in")]
    [Authorize(Policy = Policies.Patient)]
    [ProducesResponseType<AlRahma.Application.Queue.SelfCheckInResponse>(200)]
    public Task<AlRahma.Application.Queue.SelfCheckInResponse> SelfCheckIn(Guid bookingId, AlRahma.Application.Queue.SelfCheckInRequest req, CancellationToken ct) =>
        queue.SelfCheckInAsync(bookingId, req, ct);

    /// <summary>نفس تثبيت الحضور الذاتي بدون اختيار الحجز: يُختار حجز اليوم الوحيد لمرضى الحساب في عيادة الشاشة.</summary>
    [HttpPost("api/v1/me/self-check-in")]
    [Authorize(Policy = Policies.Patient)]
    [ProducesResponseType<AlRahma.Application.Queue.SelfCheckInResponse>(200)]
    public Task<AlRahma.Application.Queue.SelfCheckInResponse> MySelfCheckIn(AlRahma.Application.Queue.SelfCheckInRequest req, CancellationToken ct) =>
        queue.SelfCheckInAsync(null, req, ct);

    // ------------------------------------------------------------------ عام (بدون تسجيل دخول)

    [HttpGet("api/v1/public/branches/{branchId:guid}/doctors")]
    [AllowAnonymous]
    public async Task<IActionResult> PublicDoctors(
        Guid branchId,
        [FromQuery(Name = "specialty")] string? specialty,
        [FromQuery(Name = "q")] string? q,
        CancellationToken ct) =>
        Ok(new { items = await bookings.PublicDoctorsAsync(branchId, specialty, q, ct) });

    [HttpGet("api/v1/public/doctors/{doctorId:guid}/availability")]
    [AllowAnonymous]
    [ProducesResponseType<AvailabilityDto>(200)]
    public Task<AvailabilityDto> Availability(
        Guid doctorId,
        [FromQuery(Name = "from")] DateOnly? from,
        [FromQuery(Name = "to")] DateOnly? to,
        CancellationToken ct) =>
        bookings.AvailabilityAsync(doctorId, from, to, ct);
}
