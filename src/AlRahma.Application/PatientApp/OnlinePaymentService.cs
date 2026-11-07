using System.Text.Json;
using AlRahma.Application.Abstractions;
using AlRahma.Application.Billing;
using AlRahma.Application.Clinical;
using AlRahma.Application.Common;
using AlRahma.Application.Notifications;
using AlRahma.Application.Patients;
using AlRahma.Domain.Common;
using AlRahma.Domain.Entities;
using AlRahma.Application.PatientApp.Gateways;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlRahma.Application.PatientApp;

public record CreateOnlinePaymentRequest(Guid? OnlinePaymentId, string? Purpose, Guid? PatientId, Guid? BookingId, Guid? InvoiceId, string? Provider, Guid? InvoiceItemId = null);
public record OnlineReceiptDto(Guid ReceiptId, string ReceiptNumber, string QrCode);
public record OnlinePaymentDto(Guid OnlinePaymentId, string Purpose, Guid PatientId, Guid? BookingId, Guid? InvoiceId, long Amount, string Status,
    string Provider, string? RedirectUrl, DateTimeOffset ExpiresAt, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt, OnlineReceiptDto? Receipt,
    string? ProviderReference = null, string? FailureCode = null, bool? IsSandbox = null);

/// <summary>
/// الدفع الإلكتروني من تطبيق المريض. المبلغ يحسبه السيرفر، ونجاح الدفع لا يُعتمد إلا من الاستعلام عن حالة المعاملة لدى البوابة
/// (IPaymentGateway.InquireAsync)، لا من رجوع التطبيق ولا من محتوى الإشعار (3.5-د). البوابة تُختار بالإعداد (mock | live).
/// الدفع الإلكتروني لا يمر بوردية، وله إيصال online.
/// </summary>
public class OnlinePaymentService(IAppDbContext db, ICurrentUser current, IAuditLogger audit, IClock clock, NumberSequences numbers,
    SettingsReader settings, FinanceCore finance, BanService bans, MedicalOrderSync orders, PatientPortalService portal, IRealtime realtime,
    IOptions<OnlinePaymentOptions> options, NotificationService notifications, PaymentGatewayRegistry gateways)
{
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
        var gateway = gateways.Find(req.Provider);
        if (gateway is null)
        {
            var available = gateways.Available().Select(p => p.Code).ToArray();
            throw new AppException(422, "PROVIDER_NOT_AVAILABLE", "بوابة الدفع غير متاحة", available.Length > 0 ? "المتاح حالياً: " + string.Join(" | ", available) : "لم تُضبط البوابة بعد");
        }
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
            BookingId = bookingId, InvoiceId = invoiceId, InvoiceItemId = invoiceItemId, Provider = gateway.Provider, Amount = amount,
            ExpiresAt = now.AddMinutes(Options.ExpiryMinutes), CreatedAt = now, IsSandbox = gateway.IsSandbox,
        };
        // إنشاء المعاملة لدى البوابة مباشرة بعد اختيار الطريقة (الخطوة 2 في 3.5)
        var created = await gateway.CreateAsync(new GatewayCreateRequest(id, amount, "IQD", $"مجمع الرحمة الطبي - {req.Purpose}", op.ExpiresAt), ct);
        op.ProviderReference = created.Reference;
        op.RedirectUrl = created.RedirectUrl;
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
        // مصدر الحقيقة: الاستعلام من البوابة قبل الرد (يشمل نجاحاً متأخراً بعد انتهاء المهلة)
        if (op.Status is OnlinePaymentStatuses.Pending or OnlinePaymentStatuses.Expired)
            op = await SyncAsync(op.OnlinePaymentId, ct);
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

    public IReadOnlyList<PaymentProviderDto> Providers() => gateways.Available();

    /// <summary>
    /// يستعلم عن المعاملة لدى البوابة ويطبق النتيجة. هذا الطريق الوحيد لتعليم الدفعة مدفوعة (من التطبيق بعد العودة،
    /// أو من الإشعار بعد التحقق من توقيعه، أو من المهمة الدورية للمعلّق).
    /// </summary>
    public async Task<OnlinePayment> SyncAsync(Guid onlinePaymentId, CancellationToken ct)
    {
        var op = await db.OnlinePayments.AsNoTracking().FirstAsync(o => o.OnlinePaymentId == onlinePaymentId, ct);
        if (op.ProviderReference is null || op.Status is not (OnlinePaymentStatuses.Pending or OnlinePaymentStatuses.Expired)) return await TrackedAsync(op.OnlinePaymentId, ct);
        var gateway = gateways.Find(op.Provider);
        if (gateway is null) return await TrackedAsync(op.OnlinePaymentId, ct);
        var q = await gateway.InquireAsync(op.ProviderReference, ct);
        return q.Status switch
        {
            GatewayStatuses.Paid => await CompleteAsync(op.OnlinePaymentId, GatewayStatuses.Paid, q.TransactionId ?? op.ProviderReference, q.Raw, ct),
            GatewayStatuses.Failed or GatewayStatuses.Cancelled when op.Status == OnlinePaymentStatuses.Pending =>
                await CompleteAsync(op.OnlinePaymentId, q.Status, null, q.Raw, ct, q.FailureCode),
            _ => await TrackedAsync(op.OnlinePaymentId, ct),
        };
    }

    private async Task<OnlinePayment> TrackedAsync(Guid id, CancellationToken ct) => await db.OnlinePayments.FirstAsync(o => o.OnlinePaymentId == id, ct);

    /// <summary>للتطبيق بعد العودة من صفحة البوابة: استعلام ثم الحالة.</summary>
    public Task<OnlinePaymentDto> VerifyAsync(Guid onlinePaymentId, CancellationToken ct) => GetAsync(onlinePaymentId, ct);

    /// <summary>المهمة الدورية: كل معلّق (أو منتهٍ خلال آخر يوم، لنجاح متأخر) يُستعلم عنه لدى البوابة.</summary>
    public async Task<int> SyncPendingAsync(CancellationToken ct)
    {
        var since = clock.UtcNow.AddDays(-1);
        var ids = await db.OnlinePayments.AsNoTracking()
            .Where(o => o.ProviderReference != null && (o.Status == OnlinePaymentStatuses.Pending || (o.Status == OnlinePaymentStatuses.Expired && o.ExpiresAt > since)))
            .OrderBy(o => o.CreatedAt).Select(o => o.OnlinePaymentId).Take(200).ToListAsync(ct);
        foreach (var id in ids)
        {
            try { await SyncAsync(id, ct); } catch (AppException) { /* غير موجودة لدى البوابة: تبقى حتى المهلة */ }
        }
        return ids.Count;
    }

    /// <summary>
    /// إشعار البوابة (Webhook): لا يُعتمد محتواه. يُقبل فقط بتوقيع صالح (HMAC بسر البوابة)، ويؤدي إلى استعلام رسمي.
    /// </summary>
    public async Task<string> WebhookAsync(string provider, string body, string? signature, CancellationToken ct)
    {
        var gateway = gateways.Find(provider) ?? throw new AppException(404, "PROVIDER_NOT_AVAILABLE", "مزود الدفع غير مفعّل");
        var reference = gateway.VerifyCallback(body, signature) ?? throw new AppException(401, "INVALID_SIGNATURE", "توقيع الإشعار غير صالح");
        var id = await db.OnlinePayments.AsNoTracking().Where(o => o.Provider == provider && o.ProviderReference == reference).Select(o => (Guid?)o.OnlinePaymentId).FirstOrDefaultAsync(ct)
                 ?? throw new AppException(404, "ONLINE_PAYMENT_NOT_FOUND", "عملية الدفع غير موجودة");
        return (await SyncAsync(id, ct)).Status;
    }

    /// <summary>يطبق نتيجة الاستعلام. outcome: paid | failed | cancelled. Idempotent: العملية المكتملة لا تتغير.</summary>
    private async Task<OnlinePayment> CompleteAsync(Guid onlinePaymentId, string outcome, string? transactionId, string raw, CancellationToken ct, string? failureCode = null)
    {
        var succeeded = outcome == GatewayStatuses.Paid;
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
        op.FailureCode = succeeded ? null : failureCode;
        if (succeeded) await ApplySuccessAsync(op, transactionId!, now, ct); // الحجز إن بقي صالحاً يُطبق عليه، وإلا يصبح رصيداً
        else if (outcome == GatewayStatuses.Cancelled) op.Status = OnlinePaymentStatuses.Cancelled;
        else op.Status = op.ExpiresAt <= now || failureCode == "TIMEOUT" ? OnlinePaymentStatuses.Expired : OnlinePaymentStatuses.Failed;
        audit.Add("ONLINE_PAYMENT_" + op.Status.ToUpperInvariant(), "online_payment", op.OnlinePaymentId,
            after: new { op.Amount, op.Purpose, transactionId, late = lateSuccess });
        await db.SaveChangesAsync(ct);
        if (tx is not null) await tx.CommitAsync(ct);
        if (op.Status == OnlinePaymentStatuses.Succeeded && op.Purpose == OnlinePaymentPurposes.Invoice)
            await orders.SyncAsync(await db.Payments.Where(p => p.OnlinePaymentId == op.OnlinePaymentId)
                .Join(db.PaymentAllocations, p => p.PaymentId, a => a.PaymentId, (p, a) => a.InvoiceItemId).ToArrayAsync(ct), ct);
        if (op.PatientAccountId is { } account)
        {
            await realtime.SendAsync(RealtimeGroups.PatientAccount(account), NotificationTypes.OnlinePaymentUpdated, await ToDtoAsync(op, ct));
            await NotifyPatientResultAsync(op, account, lateSuccess, ct);
        }
        if (lateSuccess)
        {
            var branchId = await db.Payments.Where(p => p.OnlinePaymentId == op.OnlinePaymentId).Select(p => p.BranchId).FirstAsync(ct);
            await notifications.NotifyUsersAsync(await notifications.FinanceUsersAsync(branchId, ct), branchId, NotificationTypes.OnlinePaymentUpdated,
                "دفع إلكتروني وصل بعد انتهاء المهلة", $"{op.Amount:N0} د.ع سُجّلت للمريض (على الحجز إن بقي صالحاً، وإلا رصيداً)", "online_payment", op.OnlinePaymentId, ct);
        }
        return op;
    }

    /// <summary>
    /// إشعار محفوظ للمريض بنتيجة الدفع (online_payment.succeeded / online_payment.failed)، مرة واحدة عند اكتمال العملية.
    /// الإلغاء من المريض نفسه لا يُرسل شيئاً.
    /// </summary>
    private async Task NotifyPatientResultAsync(OnlinePayment op, Guid account, bool late, CancellationToken ct)
    {
        if (op.Status is not (OnlinePaymentStatuses.Succeeded or OnlinePaymentStatuses.Failed or OnlinePaymentStatuses.Expired)) return;
        var provider = op.Provider switch { "zaincash" => "زين كاش", "qicard" => "كي كارد", _ => op.Provider };
        var what = op.Purpose switch
        {
            OnlinePaymentPurposes.BookingFull => "لحجزك",
            OnlinePaymentPurposes.BookingDeposit => "عربون حجزك",
            OnlinePaymentPurposes.UnbanDeposit => "عربون فك إيقاف الحجز",
            OnlinePaymentPurposes.Invoice => "لفاتورتك",
            _ => "",
        };
        Guid? branch = op.BookingId is { } bid
            ? await db.Bookings.AsNoTracking().Where(b => b.BookingId == bid).Select(b => (Guid?)b.Session!.BranchId).FirstOrDefaultAsync(ct)
            : op.InvoiceId is { } iid ? await db.Invoices.AsNoTracking().Where(i => i.InvoiceId == iid).Select(i => (Guid?)i.BranchId).FirstOrDefaultAsync(ct) : null;
        var (linkType, linkId) = op.BookingId is { } b2 ? ("booking", b2) : ("online_payment", op.OnlinePaymentId);
        if (op.Status == OnlinePaymentStatuses.Succeeded)
        {
            await notifications.NotifyAccountAsync(account, branch, NotificationTypes.OnlinePaymentSucceeded, "تم الدفع بنجاح",
                $"دفعت {op.Amount:N0} د.ع {what} عبر {provider}." + (late ? " وصل الدفع بعد انتهاء المهلة: سُجّل على الحجز إن بقي صالحاً، وإلا رصيداً في محفظتك." : ""),
                linkType, linkId, ct);
            return;
        }
        var reason = op.FailureCode switch
        {
            "INSUFFICIENT_FUNDS" => "الرصيد غير كافٍ",
            "CARD_DECLINED" => "رُفضت البطاقة",
            "TIMEOUT" => "انتهت مهلة الدفع",
            "GATEWAY_ERROR" => "خطأ لدى بوابة الدفع",
            _ => op.Status == OnlinePaymentStatuses.Expired ? "انتهت مهلة الدفع" : "لم تكتمل العملية",
        };
        await notifications.NotifyAccountAsync(account, branch, NotificationTypes.OnlinePaymentFailed, "لم يكتمل الدفع",
            $"دفع {op.Amount:N0} د.ع {what} عبر {provider} لم يكتمل: {reason}. لم يُسجَّل المبلغ، ويمكنك المحاولة مرة أخرى من التطبيق.",
            linkType, linkId, ct);
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

    private async Task<OnlinePaymentDto> ToDtoAsync(OnlinePayment op, CancellationToken ct)
    {
        OnlineReceiptDto? receipt = null;
        if (op.ReceiptId is { } rid)
        {
            var r = await db.Receipts.AsNoTracking().FirstAsync(x => x.ReceiptId == rid, ct);
            receipt = new OnlineReceiptDto(r.ReceiptId, r.ReceiptNumber, finance.QrUrl(r.VerifyCode));
        }
        return new OnlinePaymentDto(op.OnlinePaymentId, op.Purpose, op.PatientId, op.BookingId, op.InvoiceId, op.Amount, op.Status, op.Provider,
            op.Status == OnlinePaymentStatuses.Pending ? op.RedirectUrl : null, op.ExpiresAt, op.CreatedAt, op.CompletedAt, receipt,
            op.ProviderReference, op.FailureCode, current.IsPatient ? null : op.IsSandbox); // علامة «تجريبي» للإدارة فقط
    }
}
