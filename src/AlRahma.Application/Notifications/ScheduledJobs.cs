using AlRahma.Application.Abstractions;
using AlRahma.Application.Common;
using AlRahma.Application.Queue;
using AlRahma.Domain.Common;
using AlRahma.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AlRahma.Application.Notifications;

/// <summary>
/// مهام دورية (كل دقيقة من BackgroundService): تذكير المريض قبل موعده (booking.reminder_minutes، افتراضياً ساعة) (مرة واحدة لكل حجز)،
/// وتكرار تنبيه "تفضل للدخول" لمن لم يضغط "أنا قادم"، وإنهاء الدفعات الإلكترونية المعلقة بعد انتهاء صلاحيتها.
/// </summary>
public class ScheduledJobs(IAppDbContext db, IClock clock, NotificationService notifications, SettingsReader settings, QueueNotifier queue,
    Microsoft.Extensions.Options.IOptions<BackupOptions> backupOptions, QueueEngine engine, Patients.BanService bans, IPushProvider push)
{
    /// <summary>
    /// التحويل غير المفعّل يُغلق بعد مدته (referral.expiry_days). المدفوع لا يُغلق: للمراجع حق الخدمة حتى يُسترجع المبلغ.
    /// يبقى في الملف الطبي، ويعود للعمل إن حضر المراجع لاحقاً.
    /// </summary>
    public async Task<int> ExpireReferralsAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;
        var due = await db.Referrals.Where(r => r.Status == ReferralStatuses.Referred && r.ExpiresAt <= now
                && !(r.OrderItemId != null && db.MedicalOrders.Any(m => m.OrderItemId == r.OrderItemId
                    && db.InvoiceItems.Any(i => i.InvoiceItemId == m.InvoiceItemId && i.Status != InvoiceItemStatuses.Unpaid))))
            .Take(500).ToListAsync(ct);
        foreach (var r in due) { r.Status = ReferralStatuses.Expired; r.ClosedAt = now; }
        if (due.Count > 0) await db.SaveChangesAsync(ct);
        return due.Count;
    }

    /// <summary>منفذ Push: يرسل الصادر queued عبر المزود. لا يعمل شيء في الوضع التجريبي لأن لا شيء يُكتب queued.</summary>
    public async Task<int> DispatchPushAsync(CancellationToken ct)
    {
        if (push.IsSandbox) return 0;
        var rows = await db.OutboundMessages.Where(m => m.Channel == "push" && m.Status == "queued").OrderBy(m => m.CreatedAt).Take(200).ToListAsync(ct);
        foreach (var m in rows)
        {
            var r = await push.SendAsync(m.Recipient, m.Text, m.Data, ct);
            m.Status = r.Ok ? "sent" : "failed";
            m.Error = r.Error;
        }
        if (rows.Count > 0) await db.SaveChangesAsync(ct);
        return rows.Count;
    }

    /// <summary>
    /// H3 + يوم العيادة المرن:
    /// 1) محجوز لم يصل حتى موعده المتوقع + المهلة (للطبيب أو للفرع، 20 دقيقة) → «متخطى» فيخرج من الحساب، وإن وصل يُعامل كمتأخر.
    /// 2) بعد نهاية الوجبة الفعلية + مدة الإغلاق التلقائي → يُغلق التسجيل، والمحجوزون الباقون «غائب» نهائياً:
    ///    غير المدفوع يُحتسب غياباً (وقد يُحظر)، والمدفوع إلكترونياً لا يُحظر ويُعاد مبلغه رصيداً للمريض.
    /// </summary>
    public async Task<int> AutoNoShowAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;
        var today = clock.BaghdadToday();
        var sessions = await db.DoctorSessions
            .Where(s => s.SessionDate >= today.AddDays(-1) && s.SessionDate <= today && s.Status != SessionStatuses.Cancelled)
            .ToListAsync(ct);
        var changed = 0;
        foreach (var s in sessions)
        {
            if (QueueEngine.SessionStartUtc(s) > now) continue;
            var autoClose = await settings.GetLongAsync(SettingsReader.AutoCloseAfterMinutes, s.BranchId, 120, ct);
            var ended = QueueEngine.SessionEndUtc(s).AddMinutes(autoClose) <= now;
            // 5.2-ب للنقد: دفع في الاستقبال وغادر قبل الدخول → مبلغه رصيد في محفظته بنهاية اليوم (قبل تعليم الغياب)
            var left = ended ? await CreditLeftBeforeEntryAsync(s, now, ct) : 0;
            changed += left;
            var tickets = await db.QueueTickets.Where(t => t.SessionId == s.SessionId && t.BookingId != null
                    && (t.Status == TicketStatuses.Waiting || (ended && t.Status == TicketStatuses.Skipped)))
                .ToListAsync(ct);
            var touched = left > 0;
            if (ended && s.RegistrationClosedAt is null) { s.RegistrationClosedAt = now; touched = true; }
            if (ended)
            {
                // 1.2-ج6: أُغلقت الوجبة وهو بانتظار قسم → ينتهي الدور وتبقى له عودة مجانية بالنتائج (الطبيب ثم العيادة ثم الفرع، 7 أيام)
                var awaiting = await db.QueueTickets.Where(t => t.SessionId == s.SessionId && t.Status == TicketStatuses.AwaitingResults).ToListAsync(ct);
                if (awaiting.Count > 0)
                {
                    var days = await engine.FreeReturnDaysAsync(s.DoctorId, ct);
                    foreach (var t in awaiting)
                    {
                        t.Status = TicketStatuses.Completed;
                        t.CompletedAt = now;
                        t.FreeReturnUntil = days > 0 ? now.AddDays(days) : null;
                        if (t.BookingId is { } ab) (await db.Bookings.FirstAsync(b => b.BookingId == ab, ct)).Status = BookingStatuses.Completed;
                    }
                    touched = true;
                    changed += awaiting.Count;
                }
            }
            if (tickets.Count == 0 && !touched) continue;

            var doctor = await db.Doctors.AsNoTracking().FirstAsync(d => d.DoctorId == s.DoctorId, ct);
            var grace = doctor.LateGraceMinutes ?? (int)await settings.GetLongAsync(SettingsReader.NoShowGraceMinutes, s.BranchId, 20, ct);
            var banned = new List<Guid>();
            var events = new List<(Guid BookingId, string Action)>();
            foreach (var t in tickets)
            {
                if (!ended)
                {
                    var scheduled = QueueEngine.ScheduledTime(s, t.SequenceNumber);
                    var due = t.EstimatedTime is { } eta && eta > scheduled ? eta : scheduled;
                    if (due.AddMinutes(grace) > now) continue;
                    t.Status = TicketStatuses.Skipped; // خارج الحساب؛ إن وصل يُدرج كمتأخر في أول فجوة
                    events.Add((t.BookingId!.Value, Queue.BookingChangeActions.Skipped));
                    touched = true;
                    changed++;
                    continue;
                }
                var booking = await db.Bookings.FirstAsync(b => b.BookingId == t.BookingId, ct);
                t.Status = TicketStatuses.NoShow;
                touched = true;
                changed++;
                if (booking.Status is not BookingStatuses.Booked) continue;
                booking.Status = BookingStatuses.NoShow;
                events.Add((booking.BookingId, Queue.BookingChangeActions.NoShow));
                var prepaid = await db.OnlinePayments.Where(o => o.BookingId == booking.BookingId && o.Purpose == OnlinePaymentPurposes.BookingFull
                        && o.Status == OnlinePaymentStatuses.Succeeded)
                    .Select(o => new { o.OnlinePaymentId, o.Amount }).FirstOrDefaultAsync(ct);
                if (prepaid is not null)
                {
                    // دفع ولم يحضر: لا حظر تلقائي، والمبلغ رصيد له (قرار الإدارة)
                    booking.CountedAsNoShow = false;
                    db.PatientCredits.Add(new PatientCreditEntry
                    {
                        EntryId = UuidV7.New(), PatientId = booking.PatientId, BranchId = s.BranchId, Amount = prepaid.Amount,
                        Kind = "booking_cancel_credit", BookingId = booking.BookingId, OnlinePaymentId = prepaid.OnlinePaymentId, CreatedAt = now,
                    });
                    continue;
                }
                booking.CountedAsNoShow = true;
                var patient = await db.Patients.FirstAsync(p => p.PatientId == booking.PatientId, ct);
                if (await bans.RecordNoShowAsync(patient, t.TicketId, booking.BookingId, ct)) banned.Add(patient.PatientId);
            }
            if (!touched) continue;
            await db.SaveChangesAsync(ct);
            await engine.RecalculateAsync(s, ct);
            await queue.SessionChangedAsync(s.SessionId, ct);
            foreach (var (bookingId, action) in events) await queue.BookingChangedAsync(bookingId, action, null, ct);
            foreach (var p in banned) await queue.PatientBannedAsync(p, s.BranchId, ct);
        }
        return changed;
    }

    /// <summary>
    /// مراجع أكّد حضوره ودفع الكشفية (نقداً أو غيره) ثم غادر قبل الدخول، وانتهت الوجبة: نفس سياسة الغائب المدفوع (5.2-ب).
    /// البند يُسترجع بطريقة «رصيد المريض» (refunds.method = patient_credit) بلا حساب يومي: النقد يبقى في درج من قبضه
    /// (الحساب اليومي والمطابقة لا يتغيران)، والإيراد وحصة الطبيب يُخفَّضان، والمبلغ رصيد للمريض (left_before_entry). لا حظر.
    /// </summary>
    public async Task<int> CreditLeftBeforeEntryAsync(DoctorSession s, DateTimeOffset now, CancellationToken ct)
    {
        var waiting = new[] { TicketStatuses.Arrived, TicketStatuses.Called, TicketStatuses.Skipped };
        var tickets = await db.QueueTickets.Where(t => t.SessionId == s.SessionId && waiting.Contains(t.Status) && t.ArrivedAt != null && t.StartedAt == null)
            .ToListAsync(ct);
        var count = 0;
        foreach (var t in tickets)
        {
            var item = await db.InvoiceItems.FirstOrDefaultAsync(i => i.TicketId == t.TicketId && i.Status == InvoiceItemStatuses.Paid
                && i.PerformedAt == null && (i.ItemType == VisitKinds.NewVisit || i.ItemType == VisitKinds.FollowUp) && i.NetPrice > 0, ct);
            t.Status = TicketStatuses.NoShow;
            t.QueuePosition = null;
            if (t.BookingId is { } bid && await db.Bookings.FirstOrDefaultAsync(b => b.BookingId == bid, ct) is { } booking
                && booking.Status == BookingStatuses.CheckedIn)
            {
                booking.Status = BookingStatuses.NoShow;
                booking.CountedAsNoShow = false;
            }
            count++;
            if (item is null) continue; // لم يدفع: لا مبلغ (والغياب لا يُحتسب على من حضر)
            var payment = await db.PaymentAllocations.Where(a => a.InvoiceItemId == item.InvoiceItemId)
                .Join(db.Payments, a => a.PaymentId, p => p.PaymentId, (a, p) => p).OrderByDescending(p => p.CreatedAt).FirstOrDefaultAsync(ct);
            var by = payment?.CreatedBy ?? t.CheckedInBy ?? t.CreatedBy;
            if (by is null) continue;
            var amount = item.NetPrice - item.RefundedAmount;
            if (amount <= 0) continue;
            var doctorAdj = item.NetPrice == 0 ? 0 : -(long)Math.Round((decimal)amount * item.DoctorShare / item.NetPrice, MidpointRounding.AwayFromZero);
            var refund = new Refund
            {
                RefundId = UuidV7.New(), InvoiceItemId = item.InvoiceItemId, Amount = amount, Method = RefundMethods.PatientCredit,
                Reason = "غادر قبل الدخول: المبلغ رصيد في محفظته تلقائياً بنهاية اليوم", Status = RefundStatuses.Completed,
                RequestedBy = by.Value, DecidedAt = now, DoctorShareAdjustment = doctorAdj, ComplexShareAdjustment = -amount - doctorAdj, CreatedAt = now,
            };
            db.Refunds.Add(refund);
            item.RefundedAmount += amount;
            item.Status = InvoiceItemStatuses.Refunded;
            var invoice = await db.Invoices.Include(i => i.Items).FirstAsync(i => i.InvoiceId == item.InvoiceId, ct);
            Billing.FinanceCore.RefreshInvoice(invoice);
            if (payment is not null)
            {
                var allocated = await db.PaymentAllocations.Where(a => a.PaymentId == payment.PaymentId).Select(a => a.InvoiceItemId).ToListAsync(ct);
                var allRefunded = await db.InvoiceItems.Where(i => allocated.Contains(i.InvoiceItemId) && i.InvoiceItemId != item.InvoiceItemId)
                    .AllAsync(i => i.Status == InvoiceItemStatuses.Refunded, ct);
                var receipt = await db.Receipts.FirstAsync(r => r.ReceiptId == payment.ReceiptId, ct);
                receipt.Status = allRefunded ? "refunded" : "partially_refunded";
            }
            db.PatientCredits.Add(new PatientCreditEntry
            {
                EntryId = UuidV7.New(), PatientId = t.PatientId, BranchId = s.BranchId, Amount = amount, Kind = "left_before_entry",
                RefundId = refund.RefundId, BookingId = t.BookingId, CreatedAt = now,
            });
            db.AuditLogs.Add(new AuditLog
            {
                BranchId = s.BranchId, Action = "LEFT_BEFORE_ENTRY_CREDITED", EntityType = "queue_ticket", EntityId = t.TicketId,
                After = System.Text.Json.JsonSerializer.Serialize(new { amount, refund_id = refund.RefundId, invoice_item_id = item.InvoiceItemId }),
                OccurredAt = now,
            });
            await db.SaveChangesAsync(ct);
            await queue.CreditAddedAsync(t.PatientId, s.BranchId, amount, "لمغادرتك قبل الدخول إلى الطبيب", t.BookingId, ct);
        }
        if (count > 0) await db.SaveChangesAsync(ct);
        return count;
    }

    /// <summary>
    /// المرحلة 2ب: تذكير «موعدك غداً» مساء اليوم السابق (booking.day_before_hour)، وتذكير «موعدك قريب» قبل الموعد المتوقع
    /// بـ booking.reminder_soon_minutes (20). كل منهما مرة واحدة لكل حجز. تذكير الساعة في SendBookingRemindersAsync.
    /// </summary>
    public async Task<int> SendDayBeforeAndSoonRemindersAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;
        var today = clock.BaghdadToday();
        var hour = clock.BaghdadNow().Hour;
        var rows = await (
            from b in db.Bookings.AsNoTracking()
            join s in db.DoctorSessions.AsNoTracking() on b.SessionId equals s.SessionId
            join d in db.Doctors.AsNoTracking() on s.DoctorId equals d.DoctorId
            where b.Status == BookingStatuses.Booked
                  && ((s.SessionDate == today.AddDays(1) && b.DayBeforeSentAt == null) || (s.SessionDate == today && b.SoonSentAt == null))
            select new { Booking = b, Session = s, DoctorName = d.FullName }).ToListAsync(ct);
        var sent = 0;
        foreach (var r in rows)
        {
            var at = r.Booking.EstimatedTime ?? QueueEngine.ScheduledTime(r.Session, r.Booking.SequenceNumber);
            var local = at.ToOffset(ClockExtensions.BaghdadOffset);
            if (r.Session.SessionDate == today.AddDays(1))
            {
                var dayBefore = await settings.GetLongAsync(SettingsReader.DayBeforeHour, r.Session.BranchId, 18, ct);
                if (dayBefore == 0 || hour < dayBefore) continue;
                if (await db.ExecuteAsync($"UPDATE bookings SET day_before_sent_at = {now} WHERE booking_id = {r.Booking.BookingId} AND day_before_sent_at IS NULL", ct) == 0)
                    continue;
                await notifications.NotifyPatientAsync(r.Booking.PatientId, r.Session.BranchId, NotificationTypes.BookingReminder, "تذكير: موعدك غداً",
                    $"موعدك غداً مع {r.DoctorName} الساعة {local:HH:mm} تقريباً، رقمك {r.Booking.SequenceNumber}. إن لم تستطع الحضور ألغِ الحجز من التطبيق.",
                    "booking", r.Booking.BookingId, ct);
                sent++;
                continue;
            }
            var soon = await settings.GetLongAsync(SettingsReader.ReminderSoonMinutes, r.Session.BranchId, 20, ct);
            if (at <= now || at - now > TimeSpan.FromMinutes(soon)) continue;
            if (await db.ExecuteAsync($"UPDATE bookings SET soon_sent_at = {now} WHERE booking_id = {r.Booking.BookingId} AND soon_sent_at IS NULL", ct) == 0)
                continue;
            await notifications.NotifyPatientAsync(r.Booking.PatientId, r.Session.BranchId, NotificationTypes.BookingReminder, "موعدك قريب",
                $"دورك عند {r.DoctorName} بعد {Math.Max(1, (int)(at - now).TotalMinutes)} دقيقة تقريباً (الساعة {local:HH:mm}). يُرجى الحضور وتأكيد وصولك في الاستقبال.",
                "booking", r.Booking.BookingId, ct);
            sent++;
        }
        return sent;
    }

    public async Task<int> ExpireOnlinePaymentsAsync(CancellationToken ct) =>
        await db.ExecuteAsync($"UPDATE online_payments SET status = 'expired' WHERE status = 'pending' AND expires_at <= {clock.UtcNow}", ct);

    public async Task<int> SendBookingRemindersAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;
        var today = clock.BaghdadToday();
        var rows = await (
            from b in db.Bookings.AsNoTracking()
            join s in db.DoctorSessions.AsNoTracking() on b.SessionId equals s.SessionId
            join d in db.Doctors.AsNoTracking() on s.DoctorId equals d.DoctorId
            where b.Status == BookingStatuses.Booked && (s.SessionDate == today || s.SessionDate == today.AddDays(1))
                  && b.ReminderSentAt == null
            select new { Booking = b, Session = s, DoctorName = d.FullName }).ToListAsync(ct);
        var sent = 0;
        var leads = new Dictionary<Guid, (long Lead, long Early)>();
        foreach (var r in rows)
        {
            if (!leads.TryGetValue(r.Session.BranchId, out var cfg))
                leads[r.Session.BranchId] = cfg = (await settings.GetLongAsync(SettingsReader.ReminderMinutes, r.Session.BranchId, 60, ct),
                    await settings.GetLongAsync(SettingsReader.ArriveEarlyMinutes, r.Session.BranchId, 20, ct));
            var at = r.Booking.EstimatedTime ?? QueueEngine.ScheduledTime(r.Session, r.Booking.SequenceNumber);
            if (at <= now || at - now > TimeSpan.FromMinutes(cfg.Lead)) continue;
            // الحجز أولاً حتى لا يُرسل التذكير مرتين من نسختين للسيرفر
            if (await db.ExecuteAsync($"UPDATE bookings SET reminder_sent_at = {now} WHERE booking_id = {r.Booking.BookingId} AND reminder_sent_at IS NULL", ct) == 0)
                continue;
            await notifications.NotifyPatientAsync(r.Booking.PatientId, r.Session.BranchId, NotificationTypes.BookingReminder, "تذكير بموعدك",
                $"موعدك مع {r.DoctorName} الساعة {at.ToOffset(ClockExtensions.BaghdadOffset):HH:mm}، رقمك {r.Booking.SequenceNumber}."
                + (cfg.Early > 0 ? $" يُرجى الحضور قبل موعدك بـ {cfg.Early} دقيقة لتأكيد حضورك." : ""),
                "booking", r.Booking.BookingId, ct);
            sent++;
        }
        return sent;
    }

    /// <summary>
    /// تنبيه الاستدعاء يتكرر كل دقيقة (queue.call_alert_repeats مرة، افتراضياً 3) ما دام المريض مستدعى ولم يضغط "أنا قادم".
    /// يتوقف عند التأكيد أو دخوله الغرفة أو تخطيه.
    /// </summary>
    public async Task<int> RepeatCallAlertsAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;
        var due = now.AddSeconds(-55);
        var since = now.AddHours(-12);
        var tickets = await db.QueueTickets.AsNoTracking()
            .Where(t => t.Status == TicketStatuses.Called && t.CallAcknowledgedAt == null && t.CallAlertsSent > 0
                        && t.LastCallAlertAt != null && t.LastCallAlertAt <= due && t.CalledAt >= since)
            .ToListAsync(ct);
        var sent = 0;
        var limits = new Dictionary<Guid, long>();
        foreach (var t in tickets)
        {
            if (!limits.TryGetValue(t.BranchId, out var repeats))
                limits[t.BranchId] = repeats = await settings.GetLongAsync(SettingsReader.CallAlertRepeats, t.BranchId, 3, ct);
            if (t.CallAlertsSent > repeats) continue;
            // الحجز أولاً حتى لا يتكرر التنبيه من نسختين للسيرفر
            if (await db.ExecuteAsync($@"UPDATE queue_tickets SET call_alerts_sent = call_alerts_sent + 1, last_call_alert_at = {now}
                WHERE ticket_id = {t.TicketId} AND status = 'called' AND call_acknowledged_at IS NULL AND call_alerts_sent = {t.CallAlertsSent}", ct) == 0)
                continue;
            await queue.CallAlertRepeatAsync(t, ct);
            sent++;
        }
        if (sent > 0)
            foreach (var s in tickets.Select(t => t.SessionId).Distinct())
                await queue.SessionChangedAsync(s, ct);
        return sent;
    }

    /// <summary>
    /// مراقبة النسخ الاحتياطي: ترسل backup.failed للمدراء العامين عند فشل نسخة، أو مرور 26 ساعة بلا نسخة ناجحة،
    /// أو تأخر نسخ الملفات أكثر من 24 ساعة. تنبيه التأخر يتكرر كل 12 ساعة كحد أقصى ما دامت المشكلة قائمة.
    /// </summary>
    public async Task<int> MonitorBackupsAsync(CancellationToken ct)
    {
        var opt = backupOptions.Value;
        if (!opt.Enabled) return 0;
        var now = clock.UtcNow;
        var admins = await db.Users.AsNoTracking().Where(u => u.IsActive && u.Role == Roles.Admin).Select(u => u.UserId).ToListAsync(ct);
        if (admins.Count == 0) return 0;
        var sent = 0;

        var failed = await db.BackupRuns.Where(r => r.Status == BackupStatuses.Failed && r.AlertedAt == null).OrderBy(r => r.CreatedAt).ToListAsync(ct);
        foreach (var r in failed)
        {
            r.AlertedAt = now;
            await db.SaveChangesAsync(ct);
            var what = r.Type switch
            {
                "full" => "النسخة الكاملة", "diff" or "incr" => "النسخة اليومية", "verify" => "اختبار الاسترجاع", "files" => "نسخ الملفات", _ => "النسخ الاحتياطي",
            };
            await notifications.NotifyUsersAsync(admins, null, NotificationTypes.BackupFailed, "فشل النسخ الاحتياطي",
                $"فشل {what} ({r.CreatedAt.ToOffset(ClockExtensions.BaghdadOffset):yyyy-MM-dd HH:mm})." + (r.Error is { Length: > 0 } e ? $" السبب: {Trim(e)}" : ""),
                "backup_run", r.RunId, ct);
            sent++;
        }

        var lastOk = await db.BackupRuns.AsNoTracking()
            .Where(r => Backups.BackupService.DatabaseTypes.Contains(r.Type) && r.Status == BackupStatuses.Succeeded)
            .MaxAsync(r => r.FinishedAt, ct);
        var since = lastOk ?? await db.BackupRuns.AsNoTracking().MinAsync(r => (DateTimeOffset?)r.CreatedAt, ct);
        if ((since is null || now - since.Value > TimeSpan.FromHours(opt.MaxHoursWithoutSuccess)) && !await RecentlyAlertedAsync("backup_stale", now, ct))
        {
            await notifications.NotifyUsersAsync(admins, null, NotificationTypes.BackupFailed, "لا توجد نسخة احتياطية حديثة",
                lastOk is { } t
                    ? $"آخر نسخة ناجحة كانت {t.ToOffset(ClockExtensions.BaghdadOffset):yyyy-MM-dd HH:mm} (أكثر من {opt.MaxHoursWithoutSuccess} ساعة). "
                      + (opt.IsNative ? "افتح شاشة النسخ الاحتياطي واضغط «نسخ الآن»، وتحقق من مساحة القرص." : "تحقق من سيرفر قاعدة البيانات.")
                    : opt.IsNative
                        ? "لم تُسجل أي نسخة احتياطية ناجحة بعد. افتح شاشة النسخ الاحتياطي واضغط «نسخ الآن»."
                        : "لم تُسجل أي نسخة احتياطية ناجحة بعد. تحقق من تشغيل حاوية النسخ الاحتياطي.",
                "backup_stale", null, ct);
            sent++;
        }

        if (opt.Files.IsOn)
        {
            var oldest = await db.Files.AsNoTracking().Where(f => f.Status == "stored" && f.S3SyncedAt == null).MinAsync(f => (DateTimeOffset?)f.CreatedAt, ct);
            if (oldest is { } o && now - o > TimeSpan.FromHours(opt.Files.MaxLagHours) && !await RecentlyAlertedAsync("backup_files", now, ct))
            {
                await notifications.NotifyUsersAsync(admins, null, NotificationTypes.BackupFailed, "تأخر نسخ الملفات",
                    $"توجد ملفات (أشعة، نتائج، صور) لم تُنسخ للموقع الثاني منذ أكثر من {opt.Files.MaxLagHours} ساعة.", "backup_files", null, ct);
                sent++;
            }
        }
        return sent;
    }

    private Task<bool> RecentlyAlertedAsync(string linkType, DateTimeOffset now, CancellationToken ct) =>
        db.Notifications.AsNoTracking().AnyAsync(n => n.Type == NotificationTypes.BackupFailed && n.LinkType == linkType && n.CreatedAt > now.AddHours(-12), ct);

    private static string Trim(string s) => s.Length <= 300 ? s : s[..300] + "…";
}
