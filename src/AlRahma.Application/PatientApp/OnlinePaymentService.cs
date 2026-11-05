using System.Text.Json;
using AlRahma.Application.Abstractions;
using AlRahma.Application.Billing;
using AlRahma.Application.Clinical;
using AlRahma.Application.Common;
using AlRahma.Application.Notifications;
using AlRahma.Application.Patients;
using AlRahma.Domain.Common;
using AlRahma.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlRahma.Application.PatientApp;

public record CreateOnlinePaymentRequest(Guid? OnlinePaymentId, string? Purpose, Guid? PatientId, Guid? BookingId, Guid? InvoiceId, string? Provider, Guid? InvoiceItemId = null);
public record OnlineReceiptDto(Guid ReceiptId, string ReceiptNumber, string QrCode);
public record OnlinePaymentDto(Guid OnlinePaymentId, string Purpose, Guid PatientId, Guid? BookingId, Guid? InvoiceId, long Amount, string Status,
    string Provider, string? RedirectUrl, DateTimeOffset ExpiresAt, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt, OnlineReceiptDto? Receipt);
public record SandboxPayRequest(string? Result);

/// <summary>
/// الدفع الإلكتروني من تطبيق المريض. المبلغ يحسبه السيرفر، ونجاح الدفع يأتي من البوابة فقط (Webhook)، وليس من رجوع التطبيق.
/// البوابة التجريبية (sandbox) تحاكي البوابة بصفحة فيها زرا "نجاح" و"فشل". الدفع الإلكتروني لا يمر بوردية، وله إيصال online.
/// </summary>
public class OnlinePaymentService(IAppDbContext db, ICurrentUser current, IAuditLogger audit, IClock clock, NumberSequences numbers,
    SettingsReader settings, FinanceCore finance, BanService bans, MedicalOrderSync orders, PatientPortalService portal, IRealtime realtime,
    IOptions<OnlinePaymentOptions> options, NotificationService notifications)
{
    public const string SandboxProvider = "sandbox";
    private OnlinePaymentOptions Options => options.Value;

    public async Task<OnlinePaymentDto> CreateAsync(CreateOnlinePaymentRequest req, CancellationToken ct)
    {
        new Validator()
            .Check(req.Purpose is not null && OnlinePaymentPurposes.All.Contains(req.Purpose), "purpose", string.Join(" | ", OnlinePaymentPurposes.All))
            .Check(req.PatientId is not null, "patient_id", "المريض مطلوب")
            .Check(req.Purpose is not (OnlinePaymentPurposes.BookingFull or OnlinePaymentPurposes.BookingDeposit) || req.BookingId is not null, "booking_id", "الحجز مطلوب")
            .Check(req.Purpose != OnlinePaymentPurposes.Invoice || req.InvoiceId is not null, "invoice_id", "الفاتورة مطلوبة")
            .Check(!string.IsNullOrWhiteSpace(req.Provider), "provider", "البوابة مطلوبة")
            .ThrowIfInvalid();
        if (!(Options.IsSandbox && req.Provider == SandboxProvider))
            throw new AppException(422, "PROVIDER_NOT_AVAILABLE", "بوابة الدفع غير متاحة", Options.IsSandbox ? "المتاح حالياً: sandbox" : "لم تُضبط البوابة بعد");
        await portal.EnsureLinkedAsync(req.PatientId!.Value, ct);
        var id = req.OnlinePaymentId ?? UuidV7.New();
        if (await db.OnlinePayments.AnyAsync(o => o.OnlinePaymentId == id, ct))
            throw new AppException(409, "ONLINE_PAYMENT_EXISTS", "يوجد دفع بنفس المعرف");
        var patient = await db.Patients.AsNoTracking().FirstAsync(p => p.PatientId == req.PatientId, ct);
        var now = clock.UtcNow;

        long amount;
        Guid? bookingId = null, invoiceId = null, invoiceItemId = null;
        switch (req.Purpose)
        {
            case OnlinePaymentPurposes.BookingFull or OnlinePaymentPurposes.BookingDeposit:
            {
                var booking = await db.Bookings.Include(b => b.Session).AsNoTracking()
                                  .FirstOrDefaultAsync(b => b.BookingId == req.BookingId && b.PatientId == patient.PatientId, ct)
                              ?? throw new AppException(404, "BOOKING_NOT_FOUND", "الحجز غير موجود");
                if (booking.Status != BookingStatuses.Booked)
                    throw new AppException(409, "BOOKING_NOT_ACTIVE", "الحجز ملغى أو منتهٍ أو ثُبت حضوره");
                if (booking.PaymentStatus != PaymentStatuses.Unpaid)
                    throw new AppException(409, "ALREADY_PAID", "الحجز مدفوع مسبقاً");
                var doctorDeposit = await db.Doctors.Where(d => d.DoctorId == booking.Session!.DoctorId).Select(d => d.OnlineDepositAmount).FirstOrDefaultAsync(ct);
                var deposit = doctorDeposit ?? await settings.GetLongAsync(SettingsReader.OnlineDepositAmount, booking.Session!.BranchId, 5000, ct);
                if (req.Purpose == OnlinePaymentPurposes.BookingDeposit && deposit <= 0)
                    throw new AppException(422, "DEPOSIT_NOT_AVAILABLE", "هذا الطبيب لا يعمل بالعربون", "ادفع كامل الكشفية أو في العيادة");
                amount = req.Purpose == OnlinePaymentPurposes.BookingFull ? booking.Price : Math.Min(deposit, booking.Price);
                bookingId = booking.BookingId;
                break;
            }
            case OnlinePaymentPurposes.UnbanDeposit:
                if (!patient.IsBanned) throw new AppException(409, "PATIENT_NOT_BANNED", "المريض غير محظور");
                amount = await settings.GetLongAsync(SettingsReader.UnbanDepositAmount, null, 10000, ct);
                break;
            default:
            {
                var invoice = await db.Invoices.AsNoTracking().FirstOrDefaultAsync(i => i.InvoiceId == req.InvoiceId && i.PatientId == patient.PatientId, ct)
                              ?? throw new AppException(404, "INVOICE_NOT_FOUND", "الفاتورة غير موجودة");
                if (req.InvoiceItemId is { } only && !await db.InvoiceItems.AnyAsync(i => i.InvoiceItemId == only && i.InvoiceId == invoice.InvoiceId, ct))
                    throw new AppException(404, "INVOICE_ITEM_NOT_FOUND", "البند غير موجود في هذه الفاتورة");
                amount = (await PayableItemsAsync(invoice.InvoiceId, req.InvoiceItemId, ct)).Sum(i => i.NetPrice);
                if (amount == 0) throw new AppException(409, "ALREADY_PAID", "لا توجد بنود غير مدفوعة في الفاتورة");
                invoiceId = invoice.InvoiceId;
                invoiceItemId = req.InvoiceItemId;
                break;
            }
        }
        if (amount <= 0) throw new AppException(409, "ALREADY_PAID", "لا يوجد مبلغ مستحق");

        await ExpireStaleAsync(patient.PatientId, ct);
        var pending = await db.OnlinePayments.AsNoTracking().FirstOrDefaultAsync(o => o.PatientId == patient.PatientId && o.Purpose == req.Purpose
            && o.BookingId == bookingId && o.InvoiceId == invoiceId && o.InvoiceItemId == invoiceItemId && o.Status == OnlinePaymentStatuses.Pending, ct);
        if (pending is not null)
            throw new AppException(409, "PAYMENT_PENDING", "توجد عملية دفع معلقة لنفس الغرض", "أكملها أو انتظر انتهاءها")
                { Extensions = new Dictionary<string, object?> { ["online_payment_id"] = pending.OnlinePaymentId } };

        var op = new OnlinePayment
        {
            OnlinePaymentId = id, PatientId = patient.PatientId, PatientAccountId = current.PatientAccountId, Purpose = req.Purpose!,
            BookingId = bookingId, InvoiceId = invoiceId, InvoiceItemId = invoiceItemId, Provider = SandboxProvider, Amount = amount,
            RedirectUrl = $"{Options.PublicBaseUrl.TrimEnd('/')}/api/v1/sandbox/pay/{id}", ExpiresAt = now.AddMinutes(Options.ExpiryMinutes), CreatedAt = now,
        };
        db.OnlinePayments.Add(op);
        audit.Add("ONLINE_PAYMENT_CREATED", "online_payment", id, after: new { op.Purpose, op.Amount, op.Provider, op.BookingId, op.InvoiceId, op.InvoiceItemId });
        await db.SaveChangesAsync(ct);
        return await ToDtoAsync(op, ct);
    }

    public async Task<OnlinePaymentDto> GetAsync(Guid onlinePaymentId, CancellationToken ct)
    {
        var op = await db.OnlinePayments.FirstOrDefaultAsync(o => o.OnlinePaymentId == onlinePaymentId, ct)
                 ?? throw new AppException(404, "ONLINE_PAYMENT_NOT_FOUND", "عملية الدفع غير موجودة");
        if (current.IsPatient) await portal.EnsureLinkedAsync(op.PatientId, ct);
        else if (!current.Has(Permissions.ReportsFinance) || !await StaffCanSeeAsync(op, ct))
            throw new AppException(404, "ONLINE_PAYMENT_NOT_FOUND", "عملية الدفع غير موجودة");
        if (op.Status == OnlinePaymentStatuses.Pending && op.ExpiresAt <= clock.UtcNow)
        {
            op.Status = OnlinePaymentStatuses.Expired;
            await db.SaveChangesAsync(ct);
        }
        return await ToDtoAsync(op, ct);
    }

    /// <summary>
    /// عزل الفروع: فرع الدفعة من الحجز أو الفاتورة أو الإيصال. المدير العام كل الفروع، وغيره فروعه المسندة إليه.
    /// عربون فك الحظر قبل الدفع بلا فرع (رصيد المريض مشترك بين الفروع) فيُعرض لمن يملك reports.finance.
    /// </summary>
    private async Task<bool> StaffCanSeeAsync(OnlinePayment op, CancellationToken ct)
    {
        if (current.Role == Roles.Admin) return true;
        Guid? branch = null;
        if (op.BookingId is { } bid)
            branch = await db.Bookings.AsNoTracking().Where(b => b.BookingId == bid).Select(b => (Guid?)b.Session!.BranchId).FirstOrDefaultAsync(ct);
        if (branch is null && op.InvoiceId is { } iid)
            branch = await db.Invoices.AsNoTracking().Where(i => i.InvoiceId == iid).Select(i => (Guid?)i.BranchId).FirstOrDefaultAsync(ct);
        if (branch is null && op.ReceiptId is { } rid)
            branch = await db.Receipts.AsNoTracking().Where(r => r.ReceiptId == rid).Select(r => (Guid?)r.BranchId).FirstOrDefaultAsync(ct);
        if (branch is not { } b) return true;
        return b == current.BranchId || await db.UserBranches.AnyAsync(ub => ub.UserId == current.UserId && ub.BranchId == b, ct);
    }

    // ------------------------------------------------------------------ البوابة

    /// <summary>صفحة البوابة التجريبية (بدل صفحة البوابة الحقيقية).</summary>
    public async Task<string> SandboxPageAsync(Guid onlinePaymentId, CancellationToken ct)
    {
        EnsureSandbox();
        var op = await db.OnlinePayments.AsNoTracking().FirstOrDefaultAsync(o => o.OnlinePaymentId == onlinePaymentId, ct)
                 ?? throw new AppException(404, "ONLINE_PAYMENT_NOT_FOUND", "عملية الدفع غير موجودة");
        static string enc(string s) => System.Net.WebUtility.HtmlEncode(s);
        return $$"""
            <!doctype html><html lang="ar" dir="rtl"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <title>بوابة الدفع التجريبية</title>
            <style>body{font-family:system-ui,sans-serif;max-width:420px;margin:40px auto;padding:0 16px;color:#1f2937}
            .card{border:1px solid #e5e7eb;border-radius:12px;padding:20px}.amount{font-size:28px;font-weight:700;margin:12px 0}
            button{width:100%;padding:14px;margin-top:10px;border:0;border-radius:8px;font-size:16px;cursor:pointer}
            .ok{background:#16a34a;color:#fff}.fail{background:#dc2626;color:#fff}#msg{margin-top:14px;font-weight:600}</style></head>
            <body><div class="card"><div>بوابة دفع تجريبية (Sandbox) - لا يُخصم أي مبلغ</div>
            <div class="amount">{{op.Amount:N0}} د.ع</div><div>الحالة: <span id="st">{{enc(op.Status)}}</span></div>
            <button class="ok" onclick="pay('succeeded')">نجاح الدفع</button><button class="fail" onclick="pay('failed')">فشل الدفع</button>
            <div id="msg"></div></div>
            <script>async function pay(r){const res=await fetch(location.pathname,{method:'POST',headers:{'Content-Type':'application/json'},
            body:JSON.stringify({result:r})});const j=await res.json();document.getElementById('st').textContent=j.status||j.code;
            document.getElementById('msg').textContent=res.ok?'تم. ارجع إلى التطبيق.':(j.title||'خطأ');}</script></body></html>
            """;
    }

    /// <summary>يحاكي الـ Webhook: نتيجة البوابة. Idempotent: العملية المكتملة لا تتغير.</summary>
    public async Task<OnlinePaymentDto> SandboxCompleteAsync(Guid onlinePaymentId, SandboxPayRequest req, CancellationToken ct)
    {
        EnsureSandbox();
        new Validator().Check(req.Result is "succeeded" or "failed", "result", "succeeded | failed").ThrowIfInvalid();
        var op = await CompleteAsync(onlinePaymentId, req.Result == "succeeded", $"SBX-{onlinePaymentId:N}",
            JsonSerializer.Serialize(new { provider = SandboxProvider, result = req.Result }), ct);
        return await ToDtoAsync(op, ct);
    }

    public async Task<OnlinePayment> CompleteAsync(Guid onlinePaymentId, bool succeeded, string transactionId, string raw, CancellationToken ct)
    {
        await using var tx = await db.BeginTransactionAsync(ct);
        await db.ExecuteAsync($"SELECT 1 FROM online_payments WHERE online_payment_id = {onlinePaymentId} FOR UPDATE", ct);
        var op = await db.OnlinePayments.FirstOrDefaultAsync(o => o.OnlinePaymentId == onlinePaymentId, ct)
                 ?? throw new AppException(404, "ONLINE_PAYMENT_NOT_FOUND", "عملية الدفع غير موجودة");
        // H1: نجاح وصل بعد انتهاء المهلة (أو بعد أن عُلّمت منتهية) لا يضيع: المريض دفع فعلاً
        var lateSuccess = succeeded && op.Status == OnlinePaymentStatuses.Expired;
        if (op.Status != OnlinePaymentStatuses.Pending && !lateSuccess) return op;
        var now = clock.UtcNow;
        lateSuccess |= succeeded && op.ExpiresAt <= now;
        op.RawCallback = raw;
        op.CompletedAt = now;
        if (succeeded) await ApplySuccessAsync(op, transactionId, now, ct); // الحجز إن بقي صالحاً يُطبق عليه، وإلا يصبح رصيداً
        else op.Status = op.ExpiresAt <= now ? OnlinePaymentStatuses.Expired : OnlinePaymentStatuses.Failed;
        audit.Add("ONLINE_PAYMENT_" + op.Status.ToUpperInvariant(), "online_payment", op.OnlinePaymentId,
            after: new { op.Amount, op.Purpose, transactionId, late = lateSuccess });
        await db.SaveChangesAsync(ct);
        if (tx is not null) await tx.CommitAsync(ct);
        if (op.Status == OnlinePaymentStatuses.Succeeded && op.Purpose == OnlinePaymentPurposes.Invoice)
            await orders.SyncAsync(await db.Payments.Where(p => p.OnlinePaymentId == op.OnlinePaymentId)
                .Join(db.PaymentAllocations, p => p.PaymentId, a => a.PaymentId, (p, a) => a.InvoiceItemId).ToArrayAsync(ct), ct);
        if (op.PatientAccountId is { } account)
            await realtime.SendAsync(RealtimeGroups.PatientAccount(account), NotificationTypes.OnlinePaymentUpdated, await ToDtoAsync(op, ct));
        if (lateSuccess)
        {
            var branchId = await db.Payments.Where(p => p.OnlinePaymentId == op.OnlinePaymentId).Select(p => p.BranchId).FirstAsync(ct);
            await notifications.NotifyUsersAsync(await notifications.FinanceUsersAsync(branchId, ct), branchId, NotificationTypes.OnlinePaymentUpdated,
                "دفع إلكتروني وصل بعد انتهاء المهلة", $"{op.Amount:N0} د.ع سُجّلت للمريض (على الحجز إن بقي صالحاً، وإلا رصيداً)", "online_payment", op.OnlinePaymentId, ct);
        }
        return op;
    }

    private async Task ApplySuccessAsync(OnlinePayment op, string transactionId, DateTimeOffset now, CancellationToken ct)
    {
        var patient = await db.Patients.FirstAsync(p => p.PatientId == op.PatientId, ct);
        Booking? booking = op.BookingId is null ? null : await db.Bookings.Include(b => b.Session).FirstAsync(b => b.BookingId == op.BookingId, ct);
        Invoice? invoice = op.InvoiceId is null ? null : await db.Invoices.Include(i => i.Items).FirstAsync(i => i.InvoiceId == op.InvoiceId, ct);
        var branchId = booking?.Session!.BranchId ?? invoice?.BranchId ?? patient.HomeBranchId
                       ?? await db.Branches.Where(b => b.IsActive).OrderBy(b => b.Code).Select(b => b.BranchId).FirstAsync(ct);

        var receipt = await finance.NewReceiptAsync(ReceiptTypes.Online, op.Amount, patient.PatientId, null, ct, branchId);
        var payment = new Payment
        {
            PaymentId = UuidV7.New(), BranchId = branchId, PatientId = patient.PatientId, OnlinePaymentId = op.OnlinePaymentId,
            ReceiptId = receipt.ReceiptId, TotalPaid = op.Amount, CreatedAt = now,
        };
        payment.Tenders.Add(new PaymentTender { TenderId = UuidV7.New(), PaymentId = payment.PaymentId, Method = TenderMethods.Online, Amount = op.Amount });
        db.Payments.Add(payment);
        op.Status = OnlinePaymentStatuses.Succeeded;
        op.ProviderTransactionId = transactionId;
        op.ReceiptId = receipt.ReceiptId;

        void Credit(string kind) => db.PatientCredits.Add(new PatientCreditEntry
        {
            EntryId = UuidV7.New(), PatientId = patient.PatientId, BranchId = branchId, Amount = op.Amount, Kind = kind,
            PaymentId = payment.PaymentId, BookingId = op.BookingId, OnlinePaymentId = op.OnlinePaymentId, CreatedAt = now,
        });

        switch (op.Purpose)
        {
            case OnlinePaymentPurposes.BookingFull when booking is { Status: BookingStatuses.Booked, PaymentStatus: PaymentStatuses.Unpaid }:
                booking.PaymentStatus = PaymentStatuses.Paid; // فاتورته تُنشأ مدفوعة عند تثبيت الحضور
                break;
            case OnlinePaymentPurposes.BookingDeposit when booking is { Status: BookingStatuses.Booked, PaymentStatus: PaymentStatuses.Unpaid }:
                booking.PaymentStatus = PaymentStatuses.DepositPaid;
                Credit("booking_deposit"); // يُستخدم من الرصيد عند التحصيل في الاستقبال
                break;
            case OnlinePaymentPurposes.UnbanDeposit:
                Credit("unban_deposit");
                if (patient.IsBanned) await bans.UnbanAsync(patient, "online", op.Amount, "عربون إلكتروني", ct, receipt.ReceiptId);
                break;
            case OnlinePaymentPurposes.Invoice when invoice is not null:
            {
                var payable = await PayableItemsAsync(invoice.InvoiceId, op.InvoiceItemId, ct);
                if (payable.Sum(i => i.NetPrice) == op.Amount)
                {
                    var ids = payable.Select(i => i.InvoiceItemId).ToHashSet();
                    var settles = invoice.Items.Any(i => ids.Contains(i.InvoiceItemId) && i.Status == InvoiceItemStatuses.Deferred);
                    foreach (var i in invoice.Items.Where(i => ids.Contains(i.InvoiceItemId)))
                    {
                        payment.Allocations.Add(new PaymentAllocation { PaymentId = payment.PaymentId, InvoiceItemId = i.InvoiceItemId, Amount = i.NetPrice });
                        i.Status = InvoiceItemStatuses.Paid;
                    }
                    FinanceCore.RefreshInvoice(invoice);
                    await db.SaveChangesAsync(ct);
                    if (settles) await DeferralService.SettlePaidAsync(db, [.. ids], now, ct);
                }
                else Credit("refund_credit"); // تغيرت الفاتورة أثناء الدفع: المبلغ رصيد للمريض ولا يضيع
                break;
            }
            default:
                Credit("booking_cancel_credit"); // الحجز أُلغي أو دُفع أثناء الدفع: المبلغ رصيد للمريض
                break;
        }
    }

    private async Task<List<InvoiceItem>> PayableItemsAsync(Guid invoiceId, Guid? onlyItem, CancellationToken ct) =>
        await db.InvoiceItems.AsNoTracking()
            .Where(i => i.InvoiceId == invoiceId && (onlyItem == null || i.InvoiceItemId == onlyItem) && (i.Status == InvoiceItemStatuses.Unpaid || i.Status == InvoiceItemStatuses.Deferred)
                        && !DeferralService.WrittenOffItemIds(db).Contains(i.InvoiceItemId))
            .ToListAsync(ct);

    private async Task ExpireStaleAsync(Guid patientId, CancellationToken ct)
    {
        var now = clock.UtcNow;
        await db.ExecuteAsync($"""
            UPDATE online_payments SET status = 'expired'
             WHERE patient_id = {patientId} AND status = 'pending' AND expires_at <= {now}
            """, ct);
    }

    private void EnsureSandbox()
    {
        if (!Options.IsSandbox) throw new AppException(404, "NOT_FOUND", "المورد غير موجود");
    }

    private async Task<OnlinePaymentDto> ToDtoAsync(OnlinePayment op, CancellationToken ct)
    {
        OnlineReceiptDto? receipt = null;
        if (op.ReceiptId is { } rid)
        {
            var r = await db.Receipts.AsNoTracking().FirstAsync(x => x.ReceiptId == rid, ct);
            receipt = new OnlineReceiptDto(r.ReceiptId, r.ReceiptNumber, finance.QrUrl(r.VerifyCode));
        }
        return new OnlinePaymentDto(op.OnlinePaymentId, op.Purpose, op.PatientId, op.BookingId, op.InvoiceId, op.Amount, op.Status, op.Provider,
            op.Status == OnlinePaymentStatuses.Pending ? op.RedirectUrl : null, op.ExpiresAt, op.CreatedAt, op.CompletedAt, receipt);
    }
}
