using AlRahma.Api.Infrastructure;
using AlRahma.Application.Devices;
using AlRahma.Application.Queue;
using AlRahma.Domain.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AlRahma.Api.Controllers;

[ApiController]
[Produces("application/json")]
[Authorize(Policy = Permissions.QueueManage)]
public class QueueController(QueueService queue, DeviceService devices) : ControllerBase
{
    /// <summary>
    /// الطابور المباشر. مع doctor_id: طابور طبيب واحد (كائن واحد، أو 204 إذا لا توجد وجبة).
    /// بدونه: { sessions: [...] } لعيادة محددة أو عيادات الموظف.
    /// </summary>
    [HttpGet("api/v1/queue/live")]
    public async Task<IActionResult> Live(
        [FromQuery(Name = "doctor_id")] Guid? doctorId,
        [FromQuery(Name = "clinic_id")] Guid? clinicId,
        [FromQuery(Name = "date")] DateOnly? date,
        CancellationToken ct)
    {
        if (doctorId is { } did)
        {
            var live = await queue.LiveForDoctorAsync(did, date, ct);
            return live is null ? NoContent() : Ok(live);
        }
        return Ok(new { sessions = await queue.LiveListAsync(clinicId, date, ct) });
    }

    /// <summary>تثبيت حضور: حجز (booking_id أو booking_qr)، أو حضور مباشر (patient_id + doctor_id).</summary>
    [HttpPost("api/v1/queue/check-in")]
    [Idempotent]
    [ProducesResponseType<CheckInResponse>(201)]
    public async Task<IActionResult> CheckIn(CheckInRequest req, CancellationToken ct) =>
        StatusCode(201, await queue.CheckInAsync(req, ct));

    [HttpPost("api/v1/queue/tickets/{ticketId:guid}/call")]
    [ProducesResponseType<TicketActionResponse>(200)]
    public Task<TicketActionResponse> Call(Guid ticketId, CancellationToken ct) => queue.CallAsync(ticketId, ct);

    /// <summary>استدعاء أول مريض جاهز (واصل ومدفوع) في طابور الطبيب.</summary>
    [HttpPost("api/v1/queue/call-next")]
    [ProducesResponseType<TicketActionResponse>(200)]
    public Task<TicketActionResponse> CallNext([FromQuery(Name = "doctor_id")] Guid doctorId, CancellationToken ct) =>
        queue.CallNextAsync(doctorId, ct);

    [HttpPost("api/v1/queue/tickets/{ticketId:guid}/start")]
    [Authorize(Roles = Roles.Doctor)]
    [ProducesResponseType<TicketActionResponse>(200)]
    public Task<TicketActionResponse> Start(Guid ticketId, CancellationToken ct) => queue.StartAsync(ticketId, ct);

    /// <summary>«حضر»: الاستقبال يؤكد حضور المريض المستدعى نيابة عنه (بلا هاتف أو بلا تطبيق). تختفي «لم يرد».</summary>
    [HttpPost("api/v1/queue/tickets/{ticketId:guid}/present")]
    [ProducesResponseType<TicketActionResponse>(200)]
    public Task<TicketActionResponse> Present(Guid ticketId, CancellationToken ct) => queue.MarkPresentAsync(ticketId, ct);

    /// <summary>
    /// إنهاء المعاينة. الطبيب، أو الاستقبال (لطبيب لا يستخدم النظام) إذا كان queue.call_next_by هو reception أو both، أو مدير الفرع.
    /// call_next: true = "إنهاء واستدعاء التالي".
    /// </summary>
    [HttpPost("api/v1/queue/tickets/{ticketId:guid}/complete")]
    [Authorize(Roles = Roles.Doctor + "," + Roles.Receptionist + "," + Roles.BranchManager)]
    [ProducesResponseType<TicketActionResponse>(200)]
    public Task<TicketActionResponse> Complete(Guid ticketId, [FromBody] CompleteTicketRequest? req, CancellationToken ct) =>
        queue.CompleteAsync(ticketId, req, ct);

    /// <summary>skip: تخطي مؤقت. final: غياب نهائي يُحتسب على المريض وقد يطلق الحظر.</summary>
    [HttpPost("api/v1/queue/tickets/{ticketId:guid}/no-show")]
    [ProducesResponseType<NoShowResponse>(200)]
    public Task<NoShowResponse> NoShow(Guid ticketId, NoShowRequest req, CancellationToken ct) => queue.NoShowAsync(ticketId, req, ct);

    [HttpPost("api/v1/queue/tickets/{ticketId:guid}/emergency")]
    [ProducesResponseType<EmergencyResponse>(200)]
    public Task<EmergencyResponse> Emergency(Guid ticketId, EmergencyRequest req, CancellationToken ct) =>
        queue.EmergencyAsync(ticketId, req, ct);

    /// <summary>«الطبيب وصل»: الأوقات التقديرية تُحسب من وصوله الفعلي.</summary>
    [HttpPost("api/v1/queue/sessions/{sessionId:guid}/doctor-arrived")]
    [ProducesResponseType<SessionControlDto>(200)]
    public Task<SessionControlDto> DoctorArrived(Guid sessionId, CancellationToken ct) => queue.DoctorArrivedAsync(sessionId, ct);

    /// <summary>«تأخر الطبيب»: { "minutes": 30, "reason": "..." } لوجبة اليوم قبل وصوله؛ يُخطر المحجوزين. minutes = 0 يلغيه.</summary>
    [HttpPost("api/v1/queue/sessions/{sessionId:guid}/delay")]
    [ProducesResponseType<DoctorDelayResponse>(200)]
    public Task<DoctorDelayResponse> Delay(Guid sessionId, DoctorDelayRequest req, CancellationToken ct) => queue.DoctorDelayAsync(sessionId, req, ct);

    /// <summary>«تمديد اليوم»: { "end": "23:30" } نهاية فعلية لهذه الوجبة فقط.</summary>
    [HttpPost("api/v1/queue/sessions/{sessionId:guid}/extend")]
    [ProducesResponseType<SessionControlDto>(200)]
    public Task<SessionControlDto> Extend(Guid sessionId, ExtendSessionRequest req, CancellationToken ct) => queue.ExtendAsync(sessionId, req, ct);

    /// <summary>«إغلاق التسجيل» / فتحه: { "closed": true | false }.</summary>
    [HttpPost("api/v1/queue/sessions/{sessionId:guid}/registration")]
    [ProducesResponseType<SessionControlDto>(200)]
    public Task<SessionControlDto> Registration(Guid sessionId, SessionRegistrationRequest req, CancellationToken ct) =>
        queue.RegistrationAsync(sessionId, req, ct);

    /// <summary>«عاد بالنتائج»: دور «بانتظار قسم» يعود واصلاً بأولوية returning، بلا فاتورة ولا رقم جديد. external = نتائج خارجية.</summary>
    [HttpPost("api/v1/queue/tickets/{ticketId:guid}/returned")]
    [ProducesResponseType<CheckInResponse>(200)]
    public Task<CheckInResponse> Returned(Guid ticketId, [FromBody] ReturnedRequest? req, CancellationToken ct) =>
        queue.ReturnedAsync(ticketId, req, ct);

    [HttpPost("api/v1/queue/tickets/{ticketId:guid}/late-arrival")]
    [ProducesResponseType<TicketActionResponse>(200)]
    public Task<TicketActionResponse> LateArrival(Guid ticketId, CancellationToken ct) => queue.LateArrivalAsync(ticketId, ct);

    /// <summary>رمز QR الدوار لتثبيت الحضور الذاتي (يتجدد كل نافذة). تتطلب X-Display-Key.</summary>
    [HttpGet("api/v1/public/displays/{displayId:guid}/checkin-token")]
    [AllowAnonymous]
    [ProducesResponseType<CheckInTokenDto>(200)]
    public async Task<CheckInTokenDto> CheckInToken(Guid displayId, [FromHeader(Name = "X-Display-Key")] string? key, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        return await devices.CheckInTokenAsync(displayId, key, ct);
    }

    /// <summary>جدول «مرضى اليوم» في الاستقبال: الحجوزات والحضور المباشر لعيادات الموظف، مع الأزرار المسموحة.</summary>
    [HttpGet("api/v1/reception/today")]
    [ProducesResponseType<ReceptionTodayDto>(200)]
    public Task<ReceptionTodayDto> ReceptionToday(
        [FromServices] ReceptionService reception,
        [FromQuery(Name = "date")] DateOnly? date,
        [FromQuery(Name = "doctor_id")] Guid? doctorId,
        [FromQuery(Name = "clinic_id")] Guid? clinicId,
        [FromQuery(Name = "status")] string? status,
        [FromQuery(Name = "payment_status")] string? paymentStatus,
        [FromQuery(Name = "q")] string? q,
        [FromQuery(Name = "page")] int? page,
        [FromQuery(Name = "page_size")] int? pageSize,
        CancellationToken ct) =>
        reception.TodayAsync(new ReceptionTodayQuery(date, doctorId, clinicId, status, paymentStatus, q, page, pageSize), ct);

    /// <summary>شاشة الانتظار العامة (أرقام فقط بدون أسماء). تتطلب X-Display-Key.</summary>
    [HttpGet("api/v1/public/displays/{displayId:guid}/queue")]
    [AllowAnonymous]
    [ProducesResponseType<DisplayQueueDto>(200)]
    public Task<DisplayQueueDto> Display(Guid displayId, [FromHeader(Name = "X-Display-Key")] string? key, CancellationToken ct) =>
        devices.DisplayQueueAsync(displayId, key, ct);
}
