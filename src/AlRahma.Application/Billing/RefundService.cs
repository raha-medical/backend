using AlRahma.Application.Abstractions;
using AlRahma.Application.Common;
using AlRahma.Domain.Common;
using AlRahma.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AlRahma.Application.Billing;

public record CreateRefundRequest(Guid? RefundId, long? Amount, string? Method, string? Reason);
public record RejectRefundRequest(string? Reason);
public record RefundDto(Guid RefundId, string Status, Guid InvoiceItemId, Guid InvoiceId, string ServiceName, long Amount, string Method,
    string Reason, Guid RequestedBy, string? RequestedByName, DateTimeOffset CreatedAt, Guid? DecidedBy, DateTimeOffset? DecidedAt,
    string? RejectReason, long DoctorShareAdjustment, long ComplexShareAdjustment, long InvoiceNetAfter, long InvoicePaidAfter,
    ReceiptRefDto? Receipt, string? DrawerToken, bool OpenDrawer);
public record RefundListQuery(string? Status, int Page = 1, int PageSize = 20);

/// <summary>
/// استرجاع كلي أو جزئي لبند مدفوع. حتى حد الصلاحية (refund.cashier_limit) يُنفذ فوراً، وما فوقه ينتظر موافقة المحاسب أو المدير.
/// حصة الطبيب والمجمع تُخفّض بنفس نسبة البند، والنقد يُخصم من متوقع الوردية، ويصدر إيصال استرجاع.
/// </summary>
public class RefundService(IAppDbContext db, ICurrentUser current, IAuditLogger audit, IClock clock, FinanceCore finance,
    SettingsReader settings, IDrawerTokenIssuer drawer, IRealtime realtime,
    Clinical.MedicalOrderSync orders, Notifications.NotificationService notifications)
{
    public const string CashierLimitKey = "refund.cashier_limit";

    public async Task<RefundDto> RequestAsync(Guid itemId, CreateRefundRequest req, CancellationToken ct)
    {
        new Validator()
            .Check(req.Amount is > 0, "amount", "المبلغ أكبر من صفر")
            .Check(req.Method is not null && RefundMethods.All.Contains(req.Method), "method", string.Join(" | ", RefundMethods.All))
            .Check(!string.IsNullOrWhiteSpace(req.Reason) && req.Reason.Length <= 500, "reason", "سبب الاسترجاع مطلوب (500 حرف كحد أقصى)")
            .ThrowIfInvalid();

        var id = req.RefundId ?? UuidV7.New();
        var existing = await db.Refunds.AsNoTracking().FirstOrDefaultAsync(r => r.RefundId == id, ct);
        if (existing is not null)
        {
            if (existing.InvoiceItemId == itemId && existing.RequestedBy == current.UserId) return await ToDtoAsync(existing, null, ct);
            throw new AppException(409, "REFUND_EXISTS", "يوجد استرجاع بنفس المعرف");
        }

        await using var tx = await db.BeginTransactionAsync(ct);
        await db.ExecuteAsync($"SELECT 1 FROM invoice_items WHERE invoice_item_id = {itemId} FOR UPDATE", ct);
        var item = await LoadItemAsync(itemId, ct);
        if (item.Status is not (InvoiceItemStatuses.Paid or InvoiceItemStatuses.PartiallyRefunded))
            throw new AppException(409, "ITEM_NOT_REFUNDABLE", "البند غير مدفوع", "البند غير المدفوع لا يحتاج استرجاعاً؛ ألغِ الفاتورة بدلاً من ذلك");
        var pending = await db.Refunds.Where(r => r.InvoiceItemId == itemId && r.Status == RefundStatuses.PendingApproval).SumAsync(r => (long?)r.Amount, ct) ?? 0;
        var refundable = item.NetPrice - item.RefundedAmount - pending;
        if (req.Amount > refundable)
            throw new AppException(422, "REFUND_EXCEEDS_PAID", "المبلغ أكبر من المتبقي القابل للاسترجاع")
                { Extensions = new Dictionary<string, object?> { ["refundable_amount"] = refundable } };
        if (req.Method == RefundMethods.OnlineReversal && !await PaidOnlineAsync(itemId, ct))
            throw new AppException(422, "REFUND_METHOD_NOT_AVAILABLE", "البند لم يُدفع إلكترونياً", "عكس الدفع الإلكتروني لبند دُفع عبر التطبيق فقط");
        var isApprover = current.Has(Permissions.RefundsApprove);
        // M2: النقد يُسترجع نقداً لما دُفع نقداً، والبطاقة على POS. أي اختلاف يحتاج موافقة المحاسب مهما كان المبلغ
        var paidWith = await PaidMethodsAsync(itemId, ct);
        var methodMismatch = req.Method switch
        {
            RefundMethods.Cash => !paidWith.Contains(TenderMethods.Cash),
            RefundMethods.Pos => !paidWith.Contains(TenderMethods.Pos),
            _ => false,
        };
        if (item.PerformedAt is not null && !isApprover)
            throw new AppException(409, "ITEM_ALREADY_PERFORMED", "البند نُفذ", "استرجاع بند منفذ يتطلب موافقة المحاسب أو المدير");

        var refund = new Refund
        {
            RefundId = id,
            InvoiceItemId = itemId,
            Amount = req.Amount!.Value,
            Method = req.Method!,
            Reason = req.Reason!.Trim(),
            Status = RefundStatuses.PendingApproval,
            RequestedBy = current.UserId,
            CreatedAt = clock.UtcNow,
        };
        db.Refunds.Add(refund);
        var limit = await settings.GetLongAsync(CashierLimitKey, current.BranchId, 50000, ct);
        string? drawerToken = null;
        if (isApprover || (refund.Amount <= limit && !methodMismatch))
            drawerToken = await CompleteAsync(refund, item, await ShiftForAsync(refund, null, ct), ct);
        audit.Add("REFUND_REQUESTED", "refund", id, after: new { refund.InvoiceItemId, refund.Amount, refund.Method, refund.Status }, reason: refund.Reason);
        await db.SaveChangesAsync(ct);
        if (tx is not null) await tx.CommitAsync(ct);
        if (refund.Status == RefundStatuses.Completed)
        {
            await orders.SyncAsync([itemId], ct);
            await NotifyPatientRefundedAsync(refund, item, ct);
        }

        if (refund.Status == RefundStatuses.PendingApproval)
        {
            await realtime.SendAsync(RealtimeGroups.BranchFinance(current.BranchId), "refund.approval_needed",
                new { refund_id = refund.RefundId, amount = refund.Amount, service_name = item.ServiceName });
            await notifications.NotifyUsersAsync((await notifications.FinanceUsersAsync(current.BranchId, ct)).Where(u => u != current.UserId),
                current.BranchId, Notifications.NotificationTypes.RefundApprovalNeeded, "طلب استرجاع بانتظار الموافقة",
                $"{item.ServiceName}: {refund.Amount:N0} د.ع", "refund", refund.RefundId, ct);
        }
        return await ToDtoAsync(refund, drawerToken, ct);
    }

    public async Task<RefundDto> ApproveAsync(Guid refundId, CancellationToken ct)
    {
        await using var tx = await db.BeginTransactionAsync(ct);
        var refund = await LoadPendingAsync(refundId, ct);
        await db.ExecuteAsync($"SELECT 1 FROM invoice_items WHERE invoice_item_id = {refund.InvoiceItemId} FOR UPDATE", ct);
        var item = await LoadItemAsync(refund.InvoiceItemId, ct);
        if (refund.Amount > item.NetPrice - item.RefundedAmount)
            throw new AppException(422, "REFUND_EXCEEDS_PAID", "المبلغ أكبر من المتبقي القابل للاسترجاع");
        var token = await CompleteAsync(refund, item, await ShiftForAsync(refund, refund.RequestedBy, ct), ct);
        audit.Add("REFUND_APPROVED", "refund", refund.RefundId, after: new { refund.Amount, refund.Method });
        await db.SaveChangesAsync(ct);
        if (tx is not null) await tx.CommitAsync(ct);
        await orders.SyncAsync([refund.InvoiceItemId], ct);
        await NotifyPatientRefundedAsync(refund, await db.InvoiceItems.AsNoTracking().FirstAsync(i => i.InvoiceItemId == refund.InvoiceItemId, ct), ct);
        return await ToDtoAsync(refund, token, ct);
    }

    /// <summary>إشعار المريض بإرجاع ماله: نقداً أو POS أو للبطاقة، أو كرصيد في حسابه.</summary>
    private async Task NotifyPatientRefundedAsync(Refund refund, InvoiceItem item, CancellationToken ct)
    {
        var inv = await db.Invoices.AsNoTracking().Where(i => i.InvoiceId == item.InvoiceId).Select(i => new { i.PatientId, i.BranchId }).FirstAsync(ct);
        var how = refund.Method switch
        {
            RefundMethods.Cash => "نقداً",
            RefundMethods.Pos => "إلى البطاقة عبر POS",
            RefundMethods.OnlineReversal => "إلى وسيلة الدفع الإلكتروني",
            _ => "كرصيد في حسابك يُستخدم في زيارتك القادمة",
        };
        var credit = refund.Method == RefundMethods.PatientCredit;
        await notifications.NotifyPatientAsync(inv.PatientId, inv.BranchId,
            credit ? Notifications.NotificationTypes.CreditAdded : Notifications.NotificationTypes.PaymentRefunded,
            credit ? "أُضيف رصيد لحسابك" : "تم إرجاع مبلغ لك",
            $"{refund.Amount:N0} د.ع عن {item.ServiceName} {how}", "refund", refund.RefundId, ct);
    }

    public async Task<RefundDto> RejectAsync(Guid refundId, RejectRefundRequest req, CancellationToken ct)
    {
        new Validator().Check(!string.IsNullOrWhiteSpace(req.Reason), "reason", "سبب الرفض مطلوب").ThrowIfInvalid();
        var refund = await LoadPendingAsync(refundId, ct);
        refund.Status = RefundStatuses.Rejected;
        refund.RejectReason = req.Reason!.Trim();
        refund.DecidedBy = current.UserId;
        refund.DecidedAt = clock.UtcNow;
        audit.Add("REFUND_REJECTED", "refund", refund.RefundId, after: new { refund.Amount }, reason: refund.RejectReason);
        await db.SaveChangesAsync(ct);
        return await ToDtoAsync(refund, null, ct);
    }

    public async Task<RefundDto> GetAsync(Guid refundId, CancellationToken ct)
    {
        var refund = await db.Refunds.AsNoTracking().FirstOrDefaultAsync(r => r.RefundId == refundId, ct)
                     ?? throw new AppException(404, "REFUND_NOT_FOUND", "طلب الاسترجاع غير موجود");
        await LoadItemAsync(refund.InvoiceItemId, ct); // فحص الفرع
        return await ToDtoAsync(refund, null, ct);
    }

    public async Task<PagedResult<RefundDto>> ListAsync(RefundListQuery q, CancellationToken ct)
    {
        var page = new PageQuery(q.Page, q.PageSize);
        var query = from r in db.Refunds.AsNoTracking()
                    join i in db.InvoiceItems on r.InvoiceItemId equals i.InvoiceItemId
                    join inv in db.Invoices on i.InvoiceId equals inv.InvoiceId
                    where inv.BranchId == current.BranchId
                    select r;
        if (!string.IsNullOrWhiteSpace(q.Status)) query = query.Where(r => r.Status == q.Status);
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(r => r.CreatedAt).Skip((page.SafePage - 1) * page.SafePageSize).Take(page.SafePageSize).ToListAsync(ct);
        var list = new List<RefundDto>();
        foreach (var r in rows) list.Add(await ToDtoAsync(r, null, ct));
        return new PagedResult<RefundDto>(list, page.SafePage, page.SafePageSize, total);
    }

    /// <summary>النقد و POS يخرجان من درج: وردية الموظف المنفذ، أو وردية طالب الاسترجاع إن كانت ما زالت مفتوحة.</summary>
    private async Task<Shift?> ShiftForAsync(Refund refund, Guid? requester, CancellationToken ct)
    {
        if (refund.Method is not (RefundMethods.Cash or RefundMethods.Pos)) return null;
        if (await finance.OpenShiftOfAsync(current.UserId, ct) is { } mine) return mine;
        if (requester is { } rid && await finance.OpenShiftOfAsync(rid, ct) is { } theirs) return theirs;
        throw new AppException(409, "NO_OPEN_SHIFT", "لا توجد وردية مفتوحة", "استرجاع النقد أو POS يخرج من درج وردية مفتوحة");
    }

    private async Task<string?> CompleteAsync(Refund refund, InvoiceItem item, Shift? shift, CancellationToken ct)
    {
        if (shift is not null) await finance.LockOpenShiftAsync(shift.ShiftId, ct); // H2 (المعاملة مفتوحة عند المستدعي)
        var now = clock.UtcNow;
        var doctorAdj = item.NetPrice == 0 ? 0 : -(long)Math.Round((decimal)refund.Amount * item.DoctorShare / item.NetPrice, MidpointRounding.AwayFromZero);
        refund.DoctorShareAdjustment = doctorAdj;
        refund.ComplexShareAdjustment = -refund.Amount - doctorAdj;
        refund.Status = RefundStatuses.Completed;
        refund.ShiftId = shift?.ShiftId;
        refund.DecidedBy = current.UserId;
        refund.DecidedAt = now;

        var invoice = await db.Invoices.Include(i => i.Items).FirstAsync(i => i.InvoiceId == item.InvoiceId, ct);
        var tracked = invoice.Items.First(i => i.InvoiceItemId == item.InvoiceItemId);
        tracked.RefundedAmount += refund.Amount;
        tracked.Status = tracked.RefundedAmount >= tracked.NetPrice ? InvoiceItemStatuses.Refunded : InvoiceItemStatuses.PartiallyRefunded;
        FinanceCore.RefreshInvoice(invoice);

        var receipt = await finance.NewReceiptAsync(ReceiptTypes.Refund, refund.Amount, invoice.PatientId, shift?.ShiftId, ct);
        refund.ReceiptId = receipt.ReceiptId;
        if (refund.Method == RefundMethods.PatientCredit)
            db.PatientCredits.Add(new PatientCreditEntry
            {
                EntryId = UuidV7.New(), PatientId = invoice.PatientId, BranchId = invoice.BranchId, Amount = refund.Amount, Kind = "refund_credit",
                RefundId = refund.RefundId, CreatedBy = current.UserId, CreatedAt = now,
            });

        // إيصالات الدفع الأصلية تُعلَّم: مسترجعة كلياً أو جزئياً (تظهر في التحقق من الـ QR)
        var paymentReceipts = await db.PaymentAllocations.Where(a => a.InvoiceItemId == item.InvoiceItemId)
            .Join(db.Payments, a => a.PaymentId, p => p.PaymentId, (a, p) => p).ToListAsync(ct);
        foreach (var p in paymentReceipts)
        {
            var allocatedIds = await db.PaymentAllocations.Where(a => a.PaymentId == p.PaymentId).Select(a => a.InvoiceItemId).ToListAsync(ct);
            var allRefunded = await db.InvoiceItems.Where(i => allocatedIds.Contains(i.InvoiceItemId) && i.InvoiceItemId != item.InvoiceItemId)
                .AllAsync(i => i.Status == InvoiceItemStatuses.Refunded, ct) && tracked.Status == InvoiceItemStatuses.Refunded;
            var r = await db.Receipts.FirstAsync(x => x.ReceiptId == p.ReceiptId, ct);
            r.Status = allRefunded ? "refunded" : "partially_refunded";
        }
        if (refund.Method == RefundMethods.OnlineReversal)
        {
            // في البوابة التجريبية يتم العكس فوراً؛ الدفعة الإلكترونية تصبح refunded عند عكس كامل مبلغها
            var online = await db.PaymentAllocations.Where(a => a.InvoiceItemId == item.InvoiceItemId)
                .Join(db.Payments, a => a.PaymentId, p => p.PaymentId, (a, p) => p.OnlinePaymentId)
                .Where(x => x != null).Select(x => x!.Value).ToListAsync(ct);
            foreach (var opId in online.Distinct())
            {
                var op = await db.OnlinePayments.FirstAsync(o => o.OnlinePaymentId == opId, ct);
                var itemIds = await db.Payments.Where(p => p.OnlinePaymentId == opId)
                    .Join(db.PaymentAllocations, p => p.PaymentId, a => a.PaymentId, (p, a) => a.InvoiceItemId).ToListAsync(ct);
                var reversed = await db.Refunds.Where(r => itemIds.Contains(r.InvoiceItemId) && r.Method == RefundMethods.OnlineReversal
                    && r.Status == RefundStatuses.Completed && r.RefundId != refund.RefundId).SumAsync(r => (long?)r.Amount, ct) ?? 0;
                if (reversed + refund.Amount >= op.Amount) op.Status = OnlinePaymentStatuses.Refunded;
            }
        }
        audit.Add("REFUND_COMPLETED", "refund", refund.RefundId, after: new
        {
            refund.Amount, refund.Method, receipt.ReceiptNumber, refund.DoctorShareAdjustment, refund.ComplexShareAdjustment,
        });
        return refund.Method == RefundMethods.Cash && shift is not null ? drawer.Issue(receipt.ReceiptNumber, shift.ShiftId, shift.DeviceId) : null;
    }

    /// <summary>البند مدفوع (كلياً أو جزئياً) بدفعة إلكترونية من التطبيق.</summary>
    /// <summary>طرق الدفع التي دُفع بها البند (من دفعاته).</summary>
    private async Task<HashSet<string>> PaidMethodsAsync(Guid itemId, CancellationToken ct) =>
        (await db.PaymentAllocations.Where(a => a.InvoiceItemId == itemId)
            .Join(db.PaymentTenders, a => a.PaymentId, t => t.PaymentId, (a, t) => t.Method).Distinct().ToListAsync(ct)).ToHashSet();

    private Task<bool> PaidOnlineAsync(Guid itemId, CancellationToken ct) =>
        db.PaymentAllocations.Where(a => a.InvoiceItemId == itemId)
            .Join(db.Payments, a => a.PaymentId, p => p.PaymentId, (a, p) => p).AnyAsync(p => p.OnlinePaymentId != null, ct);

    private async Task<InvoiceItem> LoadItemAsync(Guid itemId, CancellationToken ct)
    {
        var item = await db.InvoiceItems.AsNoTracking().FirstOrDefaultAsync(i => i.InvoiceItemId == itemId, ct)
                   ?? throw new AppException(404, "INVOICE_ITEM_NOT_FOUND", "البند غير موجود");
        var branch = await db.Invoices.Where(i => i.InvoiceId == item.InvoiceId).Select(i => i.BranchId).FirstAsync(ct);
        if (current.Role != Roles.Admin && branch != current.BranchId) throw new AppException(404, "INVOICE_ITEM_NOT_FOUND", "البند غير موجود");
        return item;
    }

    private async Task<Refund> LoadPendingAsync(Guid refundId, CancellationToken ct)
    {
        var refund = await db.Refunds.FirstOrDefaultAsync(r => r.RefundId == refundId, ct)
                     ?? throw new AppException(404, "REFUND_NOT_FOUND", "طلب الاسترجاع غير موجود");
        await LoadItemAsync(refund.InvoiceItemId, ct);
        if (refund.Status != RefundStatuses.PendingApproval)
            throw new AppException(409, "REFUND_NOT_PENDING", "تم البت في طلب الاسترجاع مسبقاً");
        if (refund.RequestedBy == current.UserId && current.Role != Roles.Admin)
            throw AppException.Forbidden("لا يوافق الموظف على طلبه بنفسه");
        return refund;
    }

    private async Task<RefundDto> ToDtoAsync(Refund r, string? drawerToken, CancellationToken ct)
    {
        var item = await db.InvoiceItems.AsNoTracking().FirstAsync(i => i.InvoiceItemId == r.InvoiceItemId, ct);
        var invoiceItems = await db.InvoiceItems.AsNoTracking().Where(i => i.InvoiceId == item.InvoiceId && i.Status != InvoiceItemStatuses.Void)
            .Select(i => new { i.NetPrice, i.RefundedAmount, i.Status }).ToListAsync(ct);
        var net = invoiceItems.Sum(i => i.NetPrice - i.RefundedAmount);
        var paid = invoiceItems.Where(i => i.Status is InvoiceItemStatuses.Paid or InvoiceItemStatuses.PartiallyRefunded or InvoiceItemStatuses.Refunded)
            .Sum(i => i.NetPrice - i.RefundedAmount);
        var requester = await db.Users.Where(u => u.UserId == r.RequestedBy).Select(u => u.FullName).FirstOrDefaultAsync(ct);
        var receipt = r.ReceiptId is null ? null : await db.Receipts.AsNoTracking().FirstOrDefaultAsync(x => x.ReceiptId == r.ReceiptId, ct);
        return new RefundDto(r.RefundId, r.Status, r.InvoiceItemId, item.InvoiceId, item.ServiceName, r.Amount, r.Method, r.Reason, r.RequestedBy,
            requester, r.CreatedAt, r.DecidedBy, r.DecidedAt, r.RejectReason, r.DoctorShareAdjustment, r.ComplexShareAdjustment, net, paid,
            receipt is null ? null : finance.Ref(receipt), drawerToken, drawerToken is not null);
    }
}
