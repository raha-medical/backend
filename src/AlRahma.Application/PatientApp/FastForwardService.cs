using AlRahma.Application.Abstractions;
using AlRahma.Application.Common;
using AlRahma.Application.Notifications;
using AlRahma.Application.Queue;
using AlRahma.Domain.Common;
using AlRahma.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AlRahma.Application.PatientApp;

/// <param name="Mode">gap (الافتراضي): أول من يقبل يأخذ الفجوة | invite: دعوة مفتوحة لكل المرضى اللاحقين.</param>
/// <param name="FromTime">وضع الدعوة: فقط من وقتهم التقديري من هذا الوقت فما بعد (مثلاً من 8 إلى نهاية الوجبة).</param>
/// <param name="ExpiresMinutes">وضع الدعوة: مدة صلاحية الدعوة (5-120، افتراضياً 30).</param>
public record CreateOfferRequest(short? GapMinutes, int? MaxRecipients, string? Mode = null, DateTimeOffset? FromTime = null, short? ExpiresMinutes = null);
public record CreateOfferResponse(Guid OfferId, int SentTo, DateTimeOffset ExpiresAt)
{
    public string Mode { get; init; } = FastForwardService.ModeGap;
}
/// <param name="EtaMinutes">وضع الدعوة مع come_now: كم دقيقة يحتاج للوصول (0-240).</param>
public record RespondOfferRequest(Guid? BookingId, string? Answer, short? EtaMinutes = null);
public record RespondOfferResponse(bool Accepted, DateTimeOffset? NewEstimatedTime, string? Reason);

/// <summary>
/// التقديم الذكي: عند فجوة في جدول الطبيب يُعرض على عدد من المرضى اللاحقين (أصحاب التطبيق) الحضور مبكراً،
/// وأول من يقبل يأخذ الفجوة ويُحسب له وقت تقديري فوري.
/// </summary>
public class FastForwardService(IAppDbContext db, ICurrentUser current, IAuditLogger audit, IClock clock, QueueEngine engine,
    QueueNotifier notifier, NotificationService notifications, PatientPortalService portal)
{
    public const int OfferMinutes = 10;
    public const string ModeGap = "gap";
    public const string ModeInvite = "invite";

    public async Task<CreateOfferResponse> CreateAsync(Guid sessionId, CreateOfferRequest req, CancellationToken ct)
    {
        var mode = req.Mode ?? ModeGap;
        var invite = mode == ModeInvite;
        new Validator()
            .Check(mode is ModeGap or ModeInvite, "mode", "gap | invite")
            .Check(invite ? req.GapMinutes is null or > 0 and <= 240 : req.GapMinutes is > 0 and <= 240, "gap_minutes", "من 1 إلى 240 دقيقة")
            .Check(invite ? req.MaxRecipients is null or > 0 and <= 30 : req.MaxRecipients is null or > 0 and <= 20, "max_recipients",
                invite ? "من 1 إلى 30" : "من 1 إلى 20")
            .Check(req.ExpiresMinutes is null || invite && req.ExpiresMinutes is >= 5 and <= 120, "expires_minutes", "وضع الدعوة فقط: من 5 إلى 120 دقيقة")
            .Check(req.FromTime is null || invite, "from_time", "وضع الدعوة فقط")
            .ThrowIfInvalid();
        var session = await db.DoctorSessions.AsNoTracking().FirstOrDefaultAsync(s => s.SessionId == sessionId && s.BranchId == current.BranchId, ct)
                      ?? throw new AppException(404, "SESSION_NOT_FOUND", "الوجبة غير موجودة في هذا الفرع");
        var now = clock.UtcNow;
        if (session.Status == SessionStatuses.Cancelled || QueueEngine.SessionEndUtc(session) <= now)
            throw new AppException(409, "SESSION_CLOSED", "الوجبة ملغاة أو منتهية");
        // عرض فجوة واحد فعال للوجبة. الدعوات يمكن تكرارها لدفعات جديدة (من دُعي في دعوة فعالة لا يُدعى ثانية)
        if (!invite && await db.FastForwardOffers.AnyAsync(o => o.SessionId == sessionId && o.Mode == ModeGap && o.AcceptedBookingId == null && o.ExpiresAt > now, ct))
            throw new AppException(409, "OFFER_ACTIVE", "يوجد عرض تقديم فعال لهذه الوجبة", "انتظر انتهاءه أو قبوله");

        // المرضى اللاحقون الذين لم يصلوا بعد ولهم حساب في التطبيق، الأقرب دوراً أولاً
        var query =
            from t in db.QueueTickets.AsNoTracking()
            join b in db.Bookings.AsNoTracking() on t.BookingId equals b.BookingId
            where t.SessionId == sessionId && t.Status == TicketStatuses.Waiting && b.Status == BookingStatuses.Booked
                  && db.PatientAccountLinks.Any(l => l.PatientId == b.PatientId)
            select new { t, b };
        if (invite)
        {
            query = query.Where(x => x.t.OnTheWayAt == null && !db.FastForwardRecipients.Any(r => r.BookingId == x.b.BookingId
                && db.FastForwardOffers.Any(o => o.OfferId == r.OfferId && o.Mode == ModeInvite && o.ExpiresAt > now)));
            if (req.FromTime is { } from) query = query.Where(x => x.t.EstimatedTime >= from);
        }
        var candidates = await query.OrderBy(x => x.t.QueuePosition ?? x.t.SequenceNumber)
            .Select(x => new { x.b.BookingId, x.b.PatientId, x.t.EstimatedTime })
            .Take(req.MaxRecipients ?? (invite ? 10 : 5)).ToListAsync(ct);

        var offer = new FastForwardOffer
        {
            OfferId = UuidV7.New(), SessionId = sessionId, Mode = mode, GapMinutes = req.GapMinutes,
            ExpiresAt = now.AddMinutes(invite ? req.ExpiresMinutes ?? 30 : OfferMinutes),
            CreatedBy = current.StaffUserId, CreatedAt = now,
        };
        db.FastForwardOffers.Add(offer);
        db.FastForwardRecipients.AddRange(candidates.Select(c => new FastForwardRecipient { OfferId = offer.OfferId, BookingId = c.BookingId }));
        audit.Add("FAST_FORWARD_OFFERED", "doctor_session", sessionId, after: new { offer.OfferId, offer.Mode, offer.GapMinutes, recipients = candidates.Count });
        await db.SaveChangesAsync(ct);
        var doctor = await db.Doctors.Where(d => d.DoctorId == session.DoctorId).Select(d => d.FullName).FirstAsync(ct);
        foreach (var c in candidates)
            await notifications.NotifyPatientAsync(c.PatientId, session.BranchId, NotificationTypes.FastForwardOffer, "يمكنك الحضور مبكراً",
                invite
                    ? $"العيادة عند {doctor} متاحة الآن قبل موعدك"
                      + (c.EstimatedTime is { } eta ? $" ({eta.ToOffset(ClockExtensions.BaghdadOffset):HH:mm})" : "")
                      + ". إذا كنت تستطيع الحضور الآن أكد ذلك ليعرف الاستقبال أنك في الطريق."
                    : $"توفر وقت أقرب عند {doctor} (حوالي {offer.GapMinutes} دقيقة). أول من يؤكد يأخذه.",
                "fast_forward_offer", offer.OfferId, ct);
        return new CreateOfferResponse(offer.OfferId, candidates.Count, offer.ExpiresAt) { Mode = offer.Mode };
    }

    public async Task<RespondOfferResponse> RespondAsync(Guid offerId, RespondOfferRequest req, CancellationToken ct)
    {
        new Validator()
            .Check(req.Answer is "come_now" or "keep_original", "answer", "come_now | keep_original")
            .Check(req.EtaMinutes is null or >= 0 and <= 240, "eta_minutes", "من 0 إلى 240 دقيقة")
            .ThrowIfInvalid();
        // booking_id اختياري: من الإشعار يكفي offer_id، ويُستنتج حجز أحد مرضى الحساب المدعو للعرض
        var linked = portal.LinkedPatientIds();
        var recipient = await (req.BookingId is { } bid
                                  ? db.FastForwardRecipients.Where(r => r.OfferId == offerId && r.BookingId == bid)
                                  : db.FastForwardRecipients.Where(r => r.OfferId == offerId
                                        && db.Bookings.Any(b => b.BookingId == r.BookingId && linked.Contains(b.PatientId))))
                            .FirstOrDefaultAsync(ct)
                        ?? throw new AppException(404, "NOT_FOUND", "العرض غير موجود");
        var booking = await db.Bookings.Include(b => b.Session).FirstAsync(b => b.BookingId == recipient.BookingId, ct);
        await portal.EnsureLinkedAsync(booking.PatientId, ct);

        await using var tx = await db.BeginTransactionAsync(ct);
        await db.ExecuteAsync($"SELECT 1 FROM fast_forward_offers WHERE offer_id = {offerId} FOR UPDATE", ct);
        var offer = await db.FastForwardOffers.FirstAsync(o => o.OfferId == offerId, ct);
        var now = clock.UtcNow;
        recipient.Answer = req.Answer;
        recipient.AnsweredAt = now;
        recipient.EtaMinutes = req.Answer == "come_now" ? req.EtaMinutes : null;
        if (req.Answer == "keep_original")
        {
            await db.SaveChangesAsync(ct);
            if (tx is not null) await tx.CommitAsync(ct);
            return new RespondOfferResponse(false, booking.EstimatedTime, null);
        }
        var invite = offer.Mode == ModeInvite;
        string? reason = !invite && offer.AcceptedBookingId is not null && offer.AcceptedBookingId != booking.BookingId ? "OFFER_TAKEN"
            : offer.ExpiresAt <= now ? "OFFER_EXPIRED"
            : booking.Status != BookingStatuses.Booked ? "BOOKING_NOT_ACTIVE"
            : null;
        if (reason is not null)
        {
            await db.SaveChangesAsync(ct);
            if (tx is not null) await tx.CommitAsync(ct);
            return new RespondOfferResponse(false, booking.EstimatedTime, reason);
        }
        var ticket = await db.QueueTickets.FirstAsync(t => t.BookingId == booking.BookingId, ct);
        if (invite)
        {
            // الدعوة لا تغير الترتيب: يظهر عند الاستقبال "في الطريق"، ويُسجل حضوره عند وصوله كالمعتاد
            ticket.OnTheWayAt = now;
            ticket.OnTheWayEtaMinutes = req.EtaMinutes;
            audit.Add("EARLY_INVITE_ACCEPTED", "booking", booking.BookingId, after: new { offer_id = offerId, eta_minutes = req.EtaMinutes });
            await db.SaveChangesAsync(ct);
            if (tx is not null) await tx.CommitAsync(ct);
            await notifier.SessionChangedAsync(booking.SessionId, ct);
            return new RespondOfferResponse(true, ticket.EstimatedTime, null);
        }
        offer.AcceptedBookingId = booking.BookingId;
        // يتقدم إلى أول المنتظرين
        ticket.QueuePosition = 0;
        audit.Add("FAST_FORWARD_ACCEPTED", "booking", booking.BookingId, after: new { offer_id = offerId });
        await db.SaveChangesAsync(ct);
        await engine.RecalculateAsync(booking.Session!, ct);
        if (tx is not null) await tx.CommitAsync(ct);
        await notifier.SessionChangedAsync(booking.SessionId, ct);
        var eta = await db.QueueTickets.Where(t => t.TicketId == ticket.TicketId).Select(t => t.EstimatedTime).FirstAsync(ct);
        return new RespondOfferResponse(true, eta, null);
    }
}
