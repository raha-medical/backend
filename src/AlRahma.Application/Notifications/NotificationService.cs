using AlRahma.Application.Abstractions;
using AlRahma.Application.Common;
using AlRahma.Domain.Common;
using AlRahma.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlRahma.Application.Notifications;

public record NotificationLink(string Type, Guid Id);
public record NotificationDto(Guid NotificationId, string Type, string Title, string Message, NotificationLink? Link, bool IsRead, DateTimeOffset CreatedAt)
{
    /// <summary>low (هادئ بلا تنبيه) | normal | high (حرج، مميز بصرياً).</summary>
    public string Priority { get; init; } = NotificationPriorities.Normal;
}

public static class NotificationPriorities
{
    public const string Low = "low";
    public const string Normal = "normal";
    public const string High = "high";
}
public record NotificationListResult(IReadOnlyList<NotificationDto> Items, int UnreadCount, int Page, int PageSize, int Total);
/// <summary>صف في إشعارات الفرع: نفس الإشعار لعدة موظفين صف واحد (recipients).</summary>
public record BranchNotificationDto(string Type, string Title, string Message, NotificationLink? Link, int Recipients, DateTimeOffset CreatedAt);
public record RegisterDeviceRequest(string? PushToken, string? Platform, string? App);
public record RegisterDeviceResponse(Guid DeviceRegistrationId);
public record SandboxMessageDto(string Channel, string To, string Text, DateTimeOffset CreatedAt)
{
    /// <summary>بيانات Push (للتجربة): type و link و sound و priority و require_ack.</summary>
    public System.Text.Json.JsonElement? Data { get; init; }
}

/// <summary>خيارات Push إضافية: صوت التنبيه، الأولوية، وطلب تأكيد من المريض.</summary>
public record PushOptions(string? Sound = null, string Priority = "normal", bool RequireAck = false);

/// <summary>أنواع الإشعارات (الجدول في القسم 18 من العقد).</summary>
public static class NotificationTypes
{
    public const string LabCriticalValue = "lab.critical_value";
    public const string LabResultReady = "lab.result_ready";
    public const string RadiologyReportReady = "radiology.report_ready";
    public const string QueueNewPatient = "queue.new_patient";
    public const string ShiftClosedWithVariance = "shift.closed_with_variance";
    public const string ShiftLatePayment = "shift.late_payment";
    public const string RefundApprovalNeeded = "refund.approval_needed";
    public const string BookingReminder = "booking.reminder";
    public const string BookingConfirmed = "booking.confirmed";
    public const string BookingEtaChanged = "booking.eta_changed";
    public const string BookingCancelled = "booking.cancelled";
    public const string BookingRescheduled = "booking.rescheduled";
    public const string FastForwardOffer = "fast_forward.offer";
    public const string PatientBanned = "patient.banned";
    public const string OnlinePaymentUpdated = "online_payment.updated";
    public const string OnlinePaymentSucceeded = "online_payment.succeeded";
    public const string OnlinePaymentFailed = "online_payment.failed";
    public const string DoctorDelayed = "booking.doctor_delayed";
    public const string PaymentRefunded = "payment.refunded";
    public const string CreditAdded = "credit.added";
    public const string QueueTurnSoon = "queue.turn_soon";
    public const string QueueCalled = "queue.called";
    public const string QueueCallRepeat = "queue.call_repeat";
    public const string BackupFailed = "backup.failed";
    public const string ReferralCreated = "referral.created";
    public const string ReferralActivated = "referral.activated";
    public const string SupportReplied = "support.replied";
    public const string SupportRequestCreated = "support.request_created";
}

/// <summary>
/// الإشعارات: جرس داخل النظام (جدول notifications + حدث notification.created)، و Push لتطبيق المريض،
/// و SMS للمريض الذي لا يملك التطبيق (أنواع booking.* فقط). في الوضع التجريبي تُسجل الرسائل ولا تُرسل.
/// كل دالة إرسال تحفظ بنفسها، فتُستدعى بعد حفظ العملية الأصلية.
/// </summary>
public class NotificationService(IAppDbContext db, ICurrentUser current, IClock clock, IRealtime realtime, IOptions<MessagingOptions> messaging, IPushProvider push)
{
    public MessagingOptions Messaging => messaging.Value;

    // ------------------------------------------------------------------ الإرسال

    public async Task NotifyUsersAsync(IEnumerable<Guid> userIds, Guid? branchId, string type, string title, string message,
        string? linkType, Guid? linkId, CancellationToken ct, string priority = NotificationPriorities.Normal)
    {
        var list = userIds.Where(u => u != Guid.Empty).Distinct().ToList();
        if (list.Count == 0) return;
        var now = clock.UtcNow;
        var rows = list.Select(u => new Notification
        {
            NotificationId = UuidV7.New(), UserId = u, BranchId = branchId, Type = type, Title = title, Message = message,
            LinkType = linkType, LinkId = linkId, Priority = priority, CreatedAt = now,
        }).ToList();
        db.Notifications.AddRange(rows);
        // الحرج يصل هاتف الموظف أيضاً، والباقي داخل النظام فقط
        if (priority == NotificationPriorities.High)
        {
            var tokens = await db.PushDevices.AsNoTracking().Where(d => d.UserId != null && list.Contains(d.UserId.Value)).Select(d => new { d.UserId, d.PushToken }).ToListAsync(ct);
            var data = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["type"] = type, ["link_type"] = linkType, ["link_id"] = linkId, ["sound"] = "critical", ["priority"] = priority, ["require_ack"] = true,
            });
            foreach (var t in tokens) QueuePush(t.PushToken, null, t.UserId, $"{title}: {message}", data);
        }
        await db.SaveChangesAsync(ct);
        foreach (var n in rows) await realtime.SendAsync(RealtimeGroups.User(n.UserId!.Value), "notification.created", ToDto(n));
    }

    /// <summary>أنواع تصل بـ SMS للمريض بلا تطبيق (غير booking.*): اقتراب الدور والاستدعاء وإرجاع الأموال.</summary>
    public static readonly string[] SmsTypes = [NotificationTypes.QueueTurnSoon, NotificationTypes.QueueCalled, NotificationTypes.PaymentRefunded, NotificationTypes.CreditAdded];

    /// <summary>إشعار لكل الحسابات المربوطة بالمريض (Push)، أو SMS لرقمه إذا لم يكن له حساب والنوع booking.* أو من SmsTypes.</summary>
    public Task NotifyPatientAsync(Guid patientId, Guid? branchId, string type, string title, string message,
        string? linkType, Guid? linkId, CancellationToken ct) =>
        NotifyPatientAsync(patientId, branchId, type, title, message, linkType, linkId, null, ct);

    public async Task NotifyPatientAsync(Guid patientId, Guid? branchId, string type, string title, string message,
        string? linkType, Guid? linkId, PushOptions? push, CancellationToken ct)
    {
        var accounts = await db.PatientAccountLinks.AsNoTracking().Where(l => l.PatientId == patientId)
            .Select(l => l.PatientAccountId).Distinct().ToListAsync(ct);
        var now = clock.UtcNow;
        if (accounts.Count == 0)
        {
            if (!SmsTypes.Contains(type) && !type.StartsWith("booking.")) return;
            var phone = await db.Patients.Where(p => p.PatientId == patientId).Select(p => p.PhoneNumber).FirstOrDefaultAsync(ct);
            if (phone is null) return;
            QueueSms(phone, null, $"{title}: {message}");
            await db.SaveChangesAsync(ct);
            return;
        }
        var rows = accounts.Select(a => new Notification
        {
            NotificationId = UuidV7.New(), PatientAccountId = a, BranchId = branchId, Type = type, Title = title, Message = message,
            LinkType = linkType, LinkId = linkId, CreatedAt = now,
        }).ToList();
        db.Notifications.AddRange(rows);
        var tokens = await db.PushDevices.AsNoTracking().Where(d => d.PatientAccountId != null && accounts.Contains(d.PatientAccountId.Value))
            .Select(d => new { d.PatientAccountId, d.PushToken }).ToListAsync(ct);
        var data = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = type, ["link_type"] = linkType, ["link_id"] = linkId,
            ["sound"] = push?.Sound ?? "default", ["priority"] = push?.Priority ?? "normal", ["require_ack"] = push?.RequireAck ?? false,
        });
        foreach (var t in tokens) QueuePush(t.PushToken, t.PatientAccountId, null, $"{title}: {message}", data);
        await db.SaveChangesAsync(ct);
        foreach (var n in rows) await realtime.SendAsync(RealtimeGroups.PatientAccount(n.PatientAccountId!.Value), "notification.created", ToDto(n));
    }

    /// <summary>إشعار لحساب مريض واحد (Push داخل التطبيق)، مثل الرد على طلب الدعم.</summary>
    public async Task NotifyAccountAsync(Guid accountId, Guid? branchId, string type, string title, string message,
        string? linkType, Guid? linkId, CancellationToken ct)
    {
        var n = new Notification
        {
            NotificationId = UuidV7.New(), PatientAccountId = accountId, BranchId = branchId, Type = type, Title = title, Message = message,
            LinkType = linkType, LinkId = linkId, CreatedAt = clock.UtcNow,
        };
        db.Notifications.Add(n);
        var tokens = await db.PushDevices.AsNoTracking().Where(d => d.PatientAccountId == accountId).Select(d => d.PushToken).ToListAsync(ct);
        var data = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = type, ["link_type"] = linkType, ["link_id"] = linkId, ["sound"] = "default", ["priority"] = "normal", ["require_ack"] = false,
        });
        foreach (var t in tokens) QueuePush(t, accountId, null, $"{title}: {message}", data);
        await db.SaveChangesAsync(ct);
        await realtime.SendAsync(RealtimeGroups.PatientAccount(accountId), "notification.created", ToDto(n));
    }

    /// <summary>حجز نشط في وجبات ستُلغى (يُقرأ قبل تنفيذ الإلغاء): هل دُفع، وكم، وهل إلكترونياً (للاسترجاع).</summary>
    public record CancelledBooking(Guid BookingId, Guid PatientId, Guid BranchId, DateOnly Date, Guid DoctorId, string DoctorName, bool Paid, long Amount, bool PaidOnline);

    /// <summary>إشعار booking.cancelled لمرضى الحجوزات النشطة في الوجبات قبل إلغائها (تُقرأ قبل تنفيذ الإلغاء).</summary>
    public async Task<List<CancelledBooking>> ActiveBookingsAsync(Guid[] sessionIds, CancellationToken ct) =>
        (await (from b in db.Bookings.AsNoTracking()
                join s in db.DoctorSessions.AsNoTracking() on b.SessionId equals s.SessionId
                join d in db.Doctors.AsNoTracking() on s.DoctorId equals d.DoctorId
                where sessionIds.Contains(b.SessionId) && b.Status == BookingStatuses.Booked
                select new
                {
                    b.BookingId, b.PatientId, s.BranchId, s.SessionDate, s.DoctorId, d.FullName, b.PaymentStatus, b.Price,
                    Online = db.OnlinePayments.Any(o => o.BookingId == b.BookingId && o.Purpose == OnlinePaymentPurposes.BookingFull
                                                        && o.Status == OnlinePaymentStatuses.Succeeded && o.ProviderReference != null),
                }).ToListAsync(ct))
        .Select(x => new CancelledBooking(x.BookingId, x.PatientId, x.BranchId, x.SessionDate, x.DoctorId, x.FullName,
            x.PaymentStatus == PaymentStatuses.Paid && x.Price > 0, x.PaymentStatus == PaymentStatuses.Paid ? x.Price : 0,
            x.PaymentStatus == PaymentStatuses.Paid && x.Online)).ToList();

    /// <summary>رابط «حجوزاتي» مع فتح خيارات المبلغ مباشرة (تطبيق المريض: /patient?tab=bookings&amp;cancel_options={id}).</summary>
    public const string CancelOptionsLink = "booking_cancel_options";

    /// <summary>
    /// نص الإلغاء حسب الدفع: المدفوع تُذكر له الخيارات الثلاثة (أو اثنان إن دُفع نقداً) ويُفتح له «حجوزاتي» على الخيارات،
    /// وغير المدفوع لا يُذكر له أي مبلغ ويُقترح عليه إعادة الحجز فقط.
    /// </summary>
    public static (string Title, string Message, string LinkType) CancelledText(CancelledBooking b, string reason)
    {
        var head = $"ألغى {b.DoctorName} موعدك يوم {b.Date:yyyy-MM-dd} ({reason}).";
        if (!b.Paid)
            return ("أُلغي موعدك", $"{head} يمكنك إعادة الحجز على أقرب موعد من «حجوزاتي».", "booking");
        var choices = b.PaidOnline
            ? "إعادة الحجز على أقرب موعد، أو إبقاؤه رصيداً في محفظتك، أو استرجاعه إلى وسيلة الدفع"
            : "إعادة الحجز على أقرب موعد، أو إبقاؤه رصيداً في محفظتك";
        return ("أُلغي موعدك: اختر ما تريد بمبلغك", $"{head} مبلغك {b.Amount:N0} د.ع محفوظ لك. افتح «حجوزاتي» واختر: {choices}.", CancelOptionsLink);
    }

    public async Task BookingsCancelledAsync(IEnumerable<CancelledBooking> bookings, string reason, CancellationToken ct)
    {
        foreach (var b in bookings)
        {
            var (title, message, link) = CancelledText(b, reason);
            await NotifyPatientAsync(b.PatientId, b.BranchId, NotificationTypes.BookingCancelled, title, message, link, b.BookingId, ct);
        }
    }

    /// <summary>المحاسبون ومديرو الفرع ومن يملك صلاحية المراجعة أو اعتماد الاسترجاع في الفرع.</summary>
    public async Task<List<Guid>> FinanceUsersAsync(Guid branchId, CancellationToken ct)
    {
        var extra = new[] { Permissions.ShiftsReview, Permissions.RefundsApprove };
        return await db.Users.AsNoTracking()
            .Where(u => u.IsActive && db.UserBranches.Any(b => b.UserId == u.UserId && b.BranchId == branchId)
                        && (db.RolePermissions.Any(rp => rp.RoleCode == u.Role && extra.Contains(rp.Permission))
                            || db.UserPermissions.Any(p => p.UserId == u.UserId && extra.Contains(p.Permission))))
            .Select(u => u.UserId).ToListAsync(ct);
    }

    /// <summary>رسالة SMS (لا تحفظ؛ SaveChanges على المستدعي).</summary>
    public void QueueSms(string phone, Guid? accountId, string text) =>
        db.OutboundMessages.Add(new OutboundMessage
        {
            MessageId = UuidV7.New(), Channel = "sms", Recipient = phone, PatientAccountId = accountId, Text = text,
            Status = IsSandbox(Messaging.SmsMode) ? "sandbox" : "failed",
            Error = IsSandbox(Messaging.SmsMode) ? null : "لم يُضبط مزود SMS بعد",
            CreatedAt = clock.UtcNow,
        });

    /// <summary>سجل صادر: sandbox لا يُرسل، و queued يرسله ScheduledJobs.DispatchPushAsync عبر IPushProvider.</summary>
    private void QueuePush(string token, Guid? accountId, Guid? userId, string text, string? data = null) =>
        db.OutboundMessages.Add(new OutboundMessage
        {
            MessageId = UuidV7.New(), Channel = "push", Recipient = token, PatientAccountId = accountId, UserId = userId, Text = text,
            Status = IsSandbox(Messaging.PushMode) ? "sandbox" : push.IsSandbox ? "failed" : "queued",
            Error = !IsSandbox(Messaging.PushMode) && push.IsSandbox ? "لم يُضبط مزود Push بعد" : null,
            Data = data,
            CreatedAt = clock.UtcNow,
        });

    private static bool IsSandbox(string mode) => !string.Equals(mode, "live", StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ إشعاراتي

    private IQueryable<Notification> Mine()
    {
        if (current.PatientAccountId is { } a) return db.Notifications.Where(n => n.PatientAccountId == a);
        var u = current.UserId;
        return db.Notifications.Where(n => n.UserId == u);
    }

    public async Task<NotificationListResult> ListAsync(bool unreadOnly, int page, int pageSize, CancellationToken ct)
    {
        var p = new PageQuery(page, pageSize);
        var q = Mine().AsNoTracking();
        var unread = await q.CountAsync(n => !n.IsRead, ct);
        if (unreadOnly) q = q.Where(n => !n.IsRead);
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(n => n.CreatedAt).Skip((p.SafePage - 1) * p.SafePageSize).Take(p.SafePageSize).ToListAsync(ct);
        return new NotificationListResult(rows.Select(ToDto).ToList(), unread, p.SafePage, p.SafePageSize, total);
    }

    /// <summary>إشعارات الموظفين التشغيلية في الفرع الحالي للتوكن (مدير الفرع). إشعارات المرضى لا تظهر هنا.</summary>
    public async Task<PagedResult<BranchNotificationDto>> BranchFeedAsync(string? type, int page, int pageSize, CancellationToken ct)
    {
        var p = new PageQuery(page, pageSize);
        var q = db.Notifications.AsNoTracking().Where(n => n.BranchId == current.BranchId && n.UserId != null);
        if (!string.IsNullOrWhiteSpace(type)) q = q.Where(n => n.Type == type);
        var grouped = q.GroupBy(n => new { n.Type, n.Title, n.Message, n.LinkType, n.LinkId })
            .Select(g => new { g.Key.Type, g.Key.Title, g.Key.Message, g.Key.LinkType, g.Key.LinkId, Recipients = g.Count(), CreatedAt = g.Max(n => n.CreatedAt) });
        var total = await grouped.CountAsync(ct);
        var rows = await grouped.OrderByDescending(x => x.CreatedAt).Skip((p.SafePage - 1) * p.SafePageSize).Take(p.SafePageSize).ToListAsync(ct);
        return new PagedResult<BranchNotificationDto>(rows.Select(x => new BranchNotificationDto(x.Type, x.Title, x.Message,
            x.LinkType is not null && x.LinkId is { } id ? new NotificationLink(x.LinkType, id) : null, x.Recipients, x.CreatedAt)).ToList(),
            p.SafePage, p.SafePageSize, total);
    }

    public async Task MarkReadAsync(Guid notificationId, CancellationToken ct)
    {
        var n = await Mine().FirstOrDefaultAsync(x => x.NotificationId == notificationId, ct)
                ?? throw new AppException(404, "NOT_FOUND", "الإشعار غير موجود");
        if (n.IsRead) return;
        n.IsRead = true;
        n.ReadAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task MarkAllReadAsync(CancellationToken ct)
    {
        var rows = await Mine().Where(n => !n.IsRead).ToListAsync(ct);
        var now = clock.UtcNow;
        foreach (var n in rows) { n.IsRead = true; n.ReadAt = now; }
        await db.SaveChangesAsync(ct);
    }

    public async Task<RegisterDeviceResponse> RegisterDeviceAsync(RegisterDeviceRequest req, CancellationToken ct)
    {
        new Validator()
            .Check(!string.IsNullOrWhiteSpace(req.PushToken) && req.PushToken.Length <= 500, "push_token", "مطلوب (500 حرف كحد أقصى)")
            .Check(req.Platform is "android" or "ios" or "web", "platform", "android | ios | web")
            .Check(req.App is "patient" or "staff", "app", "patient | staff")
            .ThrowIfInvalid();
        if ((req.App == "patient") != current.IsPatient)
            throw AppException.Validation(new Dictionary<string, string[]> { ["app"] = [current.IsPatient ? "تطبيق المريض: app = patient" : "الموظفون: app = staff"] });
        var token = req.PushToken!.Trim();
        // نفس التوكن لحساب آخر ينتقل إلى الحساب الحالي
        var device = await db.PushDevices.FirstOrDefaultAsync(d => d.PushToken == token, ct);
        if (device is null)
        {
            device = new PushDevice { PushDeviceId = UuidV7.New(), PushToken = token, CreatedAt = clock.UtcNow };
            db.PushDevices.Add(device);
        }
        device.Platform = req.Platform!;
        device.App = req.App!;
        device.PatientAccountId = current.PatientAccountId;
        device.UserId = current.IsPatient ? null : current.UserId;
        await db.SaveChangesAsync(ct);
        return new RegisterDeviceResponse(device.PushDeviceId);
    }

    public async Task RemoveDeviceAsync(string pushToken, CancellationToken ct)
    {
        var d = await db.PushDevices.FirstOrDefaultAsync(x => x.PushToken == pushToken, ct);
        if (d is null) return;
        var mine = current.IsPatient ? d.PatientAccountId == current.PatientAccountId : d.UserId == current.UserId;
        if (!mine) return;
        db.PushDevices.Remove(d);
        await db.SaveChangesAsync(ct);
    }

    public async Task<PagedResult<SandboxMessageDto>> SandboxMessagesAsync(string? phone, int page, int pageSize, CancellationToken ct)
    {
        if (!IsSandbox(Messaging.SmsMode) && !IsSandbox(Messaging.PushMode))
            throw new AppException(404, "NOT_FOUND", "الوضع التجريبي غير مفعّل");
        var p = new PageQuery(page, pageSize);
        var q = db.OutboundMessages.AsNoTracking().Where(m => m.Status == "sandbox");
        if (!string.IsNullOrWhiteSpace(phone))
        {
            var normalized = PhoneNumbers.Normalize(phone) ?? phone.Trim();
            var accounts = db.PatientAccounts.Where(a => a.PhoneNumber == normalized).Select(a => (Guid?)a.PatientAccountId);
            q = q.Where(m => m.Recipient == normalized || accounts.Contains(m.PatientAccountId));
        }
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(m => m.CreatedAt).Skip((p.SafePage - 1) * p.SafePageSize).Take(p.SafePageSize)
            .Select(m => new { m.Channel, m.Recipient, m.Text, m.CreatedAt, m.Data }).ToListAsync(ct);
        return new PagedResult<SandboxMessageDto>(rows.Select(m => new SandboxMessageDto(m.Channel, m.Recipient, m.Text, m.CreatedAt)
        {
            Data = m.Data is null ? null : System.Text.Json.JsonDocument.Parse(m.Data).RootElement.Clone(),
        }).ToList(), p.SafePage, p.SafePageSize, total);
    }

    public static NotificationDto ToDto(Notification n) =>
        new(n.NotificationId, n.Type, n.Title, n.Message, n.LinkType is null || n.LinkId is null ? null : new NotificationLink(n.LinkType, n.LinkId.Value),
            n.IsRead, n.CreatedAt) { Priority = n.Priority };
}
