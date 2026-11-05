using AlRahma.Api.Infrastructure;
using AlRahma.Application.Abstractions;
using AlRahma.Application.Bookings;
using AlRahma.Application.Clinical;
using AlRahma.Application.Common;
using AlRahma.Application.Notifications;
using AlRahma.Application.PatientApp;
using AlRahma.Domain.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AlRahma.Api.Controllers;

/// <summary>مصادقة تطبيق المريض برقم الهاتف و OTP.</summary>
[ApiController]
[Route("api/v1/patient-auth")]
[Produces("application/json")]
public class PatientAuthController(PatientAuthService auth) : ControllerBase
{
    [HttpPost("otp/request")]
    [AllowAnonymous]
    [ProducesResponseType<OtpRequestResponse>(202)]
    public async Task<IActionResult> RequestOtp(OtpRequest req, CancellationToken ct) => StatusCode(202, await auth.RequestOtpAsync(req, ct));

    [HttpPost("otp/verify")]
    [AllowAnonymous]
    [ProducesResponseType<PatientAuthResponse>(200)]
    public Task<PatientAuthResponse> Verify(OtpVerifyRequest req, CancellationToken ct) => auth.VerifyAsync(req, ct);

    [HttpPost("refresh")]
    [AllowAnonymous]
    [ProducesResponseType<PatientAuthResponse>(200)]
    public Task<PatientAuthResponse> Refresh(PatientRefreshRequest req, CancellationToken ct) => auth.RefreshAsync(req, ct);

    [HttpPost("logout")]
    [Authorize(Policy = Policies.Patient)]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        await auth.LogoutAsync(ct);
        return NoContent();
    }
}

/// <summary>ملفات مرضى الحساب (العائلة) وزياراتهم ووصفاتهم في تطبيق المريض.</summary>
[ApiController]
[Route("api/v1/me/patients")]
[Produces("application/json")]
[Authorize(Policy = Policies.Patient)]
public class MyPatientsController(PatientPortalService portal, ConsultationService consultations, PharmacyService pharmacy, PatientFileService files) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) => Ok(new { items = await portal.ListAsync(ct) });

    [HttpPost]
    [Idempotent]
    [ProducesResponseType<MyPatientDto>(201)]
    public async Task<IActionResult> Create(CreateMyPatientRequest req, CancellationToken ct)
    {
        var (patient, created) = await portal.CreateAsync(req, ct);
        return StatusCode(created ? 201 : 200, patient);
    }

    [HttpPatch("{patientId:guid}")]
    [ProducesResponseType<MyPatientDto>(200)]
    public Task<MyPatientDto> Update(Guid patientId, UpdateMyPatientRequest req, CancellationToken ct) => portal.UpdateAsync(patientId, req, ct);

    [HttpGet("{patientId:guid}/visits")]
    [ProducesResponseType<PagedResult<VisitDto>>(200)]
    public Task<PagedResult<VisitDto>> Visits(Guid patientId, [FromQuery(Name = "page")] int page = 1,
        [FromQuery(Name = "page_size")] int pageSize = 20, CancellationToken ct = default) =>
        consultations.MyVisitsAsync(patientId, page, pageSize, ct);

    /// <summary>الملف الكامل لفرد في الحساب من كل الفروع.</summary>
    [HttpGet("{patientId:guid}/file")]
    [ProducesResponseType<PatientFileDto>(200)]
    public Task<PatientFileDto> File(Guid patientId, CancellationToken ct) => files.FileAsync(patientId, ct);

    /// <summary>كل طلبات المختبر والأشعة: القائمة لا تتوقف على الدفع، والنتيجة فقط مشروطة به.</summary>
    [HttpGet("{patientId:guid}/orders")]
    public async Task<IActionResult> Orders(Guid patientId, [FromQuery(Name = "department")] string? department, CancellationToken ct) =>
        Ok(new { items = await files.OrdersAsync(patientId, department, ct) });

    /// <summary>فواتير الفرد من كل الفروع ببنودها وما يمكن دفعه منها.</summary>
    [HttpGet("{patientId:guid}/invoices")]
    public async Task<IActionResult> Invoices(Guid patientId, CancellationToken ct) => Ok(new { items = await files.InvoicesAsync(patientId, ct) });

    [HttpGet("{patientId:guid}/prescriptions")]
    public async Task<IActionResult> Prescriptions(Guid patientId, CancellationToken ct) =>
        Ok(new { items = await pharmacy.MyPrescriptionsAsync(patientId, ct) });
}

/// <summary>الإشعارات وأجهزة Push لأي موظف أو مريض.</summary>
[ApiController]
[Produces("application/json")]
[Authorize(Policy = Policies.StaffOrPatient)]
public class NotificationsController(NotificationService notifications) : ControllerBase
{
    [HttpGet("api/v1/me/notifications")]
    [ProducesResponseType<NotificationListResult>(200)]
    public Task<NotificationListResult> List([FromQuery(Name = "unread")] bool unread = false, [FromQuery(Name = "page")] int page = 1,
        [FromQuery(Name = "page_size")] int pageSize = 20, CancellationToken ct = default) =>
        notifications.ListAsync(unread, page, pageSize, ct);

    [HttpPost("api/v1/me/notifications/{notificationId:guid}/read")]
    public async Task<IActionResult> Read(Guid notificationId, CancellationToken ct)
    {
        await notifications.MarkReadAsync(notificationId, ct);
        return NoContent();
    }

    [HttpPost("api/v1/me/notifications/read-all")]
    public async Task<IActionResult> ReadAll(CancellationToken ct)
    {
        await notifications.MarkAllReadAsync(ct);
        return NoContent();
    }

    [HttpPost("api/v1/me/devices")]
    [ProducesResponseType<RegisterDeviceResponse>(201)]
    public async Task<IActionResult> RegisterDevice(RegisterDeviceRequest req, CancellationToken ct) =>
        StatusCode(201, await notifications.RegisterDeviceAsync(req, ct));

    [HttpDelete("api/v1/me/devices/{pushToken}")]
    public async Task<IActionResult> RemoveDevice(string pushToken, CancellationToken ct)
    {
        await notifications.RemoveDeviceAsync(pushToken, ct);
        return NoContent();
    }

    /// <summary>ما كان سيُرسل SMS أو Push في الوضع التجريبي (المدير العام فقط).</summary>
    [HttpGet("api/v1/sandbox/messages")]
    [Authorize(Roles = Roles.Admin)]
    [ProducesResponseType<PagedResult<SandboxMessageDto>>(200)]
    public Task<PagedResult<SandboxMessageDto>> SandboxMessages([FromQuery(Name = "phone_number")] string? phone,
        [FromQuery(Name = "page")] int page = 1, [FromQuery(Name = "page_size")] int pageSize = 50, CancellationToken ct = default) =>
        notifications.SandboxMessagesAsync(phone, page, pageSize, ct);
}

/// <summary>الدفع الإلكتروني من تطبيق المريض، والبوابة التجريبية.</summary>
[ApiController]
[Produces("application/json")]
public class OnlinePaymentsController(OnlinePaymentService payments) : ControllerBase
{
    [HttpPost("api/v1/online-payments")]
    [Authorize(Policy = Policies.Patient)]
    [Idempotent]
    [ProducesResponseType<OnlinePaymentDto>(201)]
    public async Task<IActionResult> Create(CreateOnlinePaymentRequest req, CancellationToken ct) => StatusCode(201, await payments.CreateAsync(req, ct));

    [HttpGet("api/v1/online-payments/{onlinePaymentId:guid}")]
    [Authorize(Policy = Policies.StaffOrPatient)]
    [ProducesResponseType<OnlinePaymentDto>(200)]
    public Task<OnlinePaymentDto> Get(Guid onlinePaymentId, CancellationToken ct) => payments.GetAsync(onlinePaymentId, ct);

    /// <summary>صفحة البوابة التجريبية (زر نجاح وزر فشل).</summary>
    [HttpGet("api/v1/sandbox/pay/{onlinePaymentId:guid}")]
    [AllowAnonymous]
    [Produces("text/html")]
    public async Task<ContentResult> SandboxPage(Guid onlinePaymentId, CancellationToken ct) =>
        Content(await payments.SandboxPageAsync(onlinePaymentId, ct), "text/html; charset=utf-8");

    /// <summary>محاكاة إشعار البوابة: { "result": "succeeded" | "failed" }.</summary>
    [HttpPost("api/v1/sandbox/pay/{onlinePaymentId:guid}")]
    [AllowAnonymous]
    [Consumes("application/json", "application/x-www-form-urlencoded")]
    [ProducesResponseType<OnlinePaymentDto>(200)]
    public async Task<IActionResult> SandboxComplete(Guid onlinePaymentId, CancellationToken ct)
    {
        SandboxPayRequest req;
        if (Request.HasFormContentType) req = new SandboxPayRequest((await Request.ReadFormAsync(ct))["result"].ToString());
        else req = await Request.ReadFromJsonAsync<SandboxPayRequest>(new System.Text.Json.JsonSerializerOptions
                   { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower, PropertyNameCaseInsensitive = true }, ct)
               ?? new SandboxPayRequest(null);
        var dto = await payments.SandboxCompleteAsync(onlinePaymentId, req, ct);
        return Request.HasFormContentType ? Content(SandboxDone(dto.Status), "text/html; charset=utf-8") : Ok(dto);
    }

    /// <summary>إشعارات بوابات الدفع الحقيقية: لم تُربط بعد (الوضع التجريبي فقط حالياً).</summary>
    [HttpPost("api/v1/webhooks/payments/{provider}")]
    [AllowAnonymous]
    public IActionResult Webhook(string provider) =>
        throw new AppException(404, "PROVIDER_NOT_AVAILABLE", "مزود الدفع غير مفعّل", $"{provider} لم يُربط بعد");

    private static string SandboxDone(string status) =>
        $"<!doctype html><html lang=\"ar\" dir=\"rtl\"><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\">" +
        $"<body style=\"font-family:sans-serif;text-align:center;padding:40px\"><h2>{(status == "succeeded" ? "تم الدفع بنجاح" : "لم يتم الدفع")}</h2>" +
        "<p>ارجع إلى التطبيق.</p></body></html>";
}

/// <summary>التقديم الذكي: عرض الاستقبال وجواب المريض.</summary>
[ApiController]
[Produces("application/json")]
public class FastForwardController(FastForwardService offers) : ControllerBase
{
    [HttpPost("api/v1/queue/sessions/{sessionId:guid}/fast-forward-offers")]
    [Authorize(Policy = Permissions.QueueManage)]
    [Idempotent]
    [ProducesResponseType<CreateOfferResponse>(201)]
    public async Task<IActionResult> Create(Guid sessionId, CreateOfferRequest req, CancellationToken ct) =>
        StatusCode(201, await offers.CreateAsync(sessionId, req, ct));

    [HttpPost("api/v1/fast-forward-offers/{offerId:guid}/respond")]
    [Authorize(Policy = Policies.Patient)]
    [ProducesResponseType<RespondOfferResponse>(200)]
    public Task<RespondOfferResponse> Respond(Guid offerId, RespondOfferRequest req, CancellationToken ct) => offers.RespondAsync(offerId, req, ct);
}

/// <summary>طابور اليوم في تطبيق المريض: تذاكره (بالحجز أو من الاستقبال) وزر "أنا قادم" على تنبيه الاستدعاء.</summary>
[ApiController]
[Route("api/v1/me/queue-tickets")]
[Produces("application/json")]
[Authorize(Policy = Policies.Patient)]
public class MyQueueController(MyQueueService queue) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Active(CancellationToken ct) => Ok(new { items = await queue.ActiveAsync(ct) });

    [HttpPost("{ticketId:guid}/acknowledge-call")]
    [ProducesResponseType<AcknowledgeCallResponse>(200)]
    public Task<AcknowledgeCallResponse> AcknowledgeCall(Guid ticketId, CancellationToken ct) => queue.AcknowledgeCallAsync(ticketId, ct);
}
