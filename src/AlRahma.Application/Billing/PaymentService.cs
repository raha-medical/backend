using AlRahma.Application.Abstractions;
using AlRahma.Application.Common;
using AlRahma.Application.Queue;
using AlRahma.Domain.Common;
using AlRahma.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AlRahma.Application.Billing;

public record BillableItemDto(Guid InvoiceItemId, Guid InvoiceId, string InvoiceNumber, string ServiceName, string ItemType, string Department,
    string? OrderedBy, DateTimeOffset OrderedAt, short Quantity, long Price, string Status, bool IsCurrentDepartment);
public record BillableItemsDto(Guid PatientId, long DepositCredit, IReadOnlyList<BillableItemDto> Items, long TotalDue);

/// <summary>method: cash | pos | cash_usd. لـ cash_usd يكون amount بالدولار (عدد صحيح)، ويُحسب المقابل بسعر صرف اليوم، والفكة بالدينار.</summary>
public record TenderInput(string? Method, long? Amount, string? PosTerminal, string? PosReference);
public record CollectPaymentRequest(Guid? PaymentId, Guid? PatientId, List<Guid>? InvoiceItemIds, long? UseDepositCredit,
    List<TenderInput>? Tenders, string? OfflineReceiptNumber, DateTimeOffset? CreatedOfflineAt);
public record PaymentResponse(Guid PaymentId, Guid ShiftId, long TotalPaid, long DepositUsed, ReceiptRefDto Receipt, string? DrawerToken, bool OpenDrawer)
{
    /// <summary>الدفع بالدولار: الفكة المعادة بالدينار، وسعر الصرف المستخدم.</summary>
    public long ChangeIqd { get; init; }
    public int? ExchangeRate { get; init; }
}
/// <summary>تحصيل تم أوفلاين ووصل عبر /sync/batch: برقم إيصال من كتلة الجهاز ووقت حدوثه.</summary>
/// <summary>`ShiftId`: الوردية التي كانت مفتوحة على الجهاز وقت التحصيل (C3)؛ بدونها تُستخدم وردية من يزامن (السلوك السابق).</summary>
public record OfflineContext(Guid DeviceId, DateTimeOffset OccurredAt, Guid? TicketId, Guid? ShiftId = null);
public record DepositResult(Receipt Receipt, string? DrawerToken, bool OpenDrawer, long CreditBalance);

/// <summary>
/// التحصيل من سلة المريض الموحدة بطريقة دفع أو أكثر (نقد + POS)، مع استخدام رصيده، وإيصال برقم تسلسلي و QR.
/// لا تحصيل بدون وردية مفتوحة ولا بدون إيصال، والدرج لا ينفتح إلا بتوكن موقّع إذا كان في الدفعة نقد.
/// </summary>
public class PaymentService(IAppDbContext db, ICurrentUser current, IAuditLogger audit, IClock clock, FinanceCore finance,
    IDrawerTokenIssuer drawer, QueueNotifier notifier,
    Clinical.MedicalOrderSync orders, Notifications.NotificationService notifications, SettingsReader settings, CurrencyService currency)
{
    /// <summary>البنود التي يمكن تحصيلها: المصدر الوحيد لسلة التحصيل ومبلغ المستحق في الطابور.</summary>
    public static IQueryable<InvoiceItem> CollectableItems(IAppDbContext db) =>
        from i in db.InvoiceItems.AsNoTracking()
        where (i.Status == InvoiceItemStatuses.Unpaid || i.Status == InvoiceItemStatuses.Deferred)
              && !DeferralService.WrittenOffItemIds(db).Contains(i.InvoiceItemId)
              // تحويل مغلق (خارجي أو منتهي) لا يبقى ديناً في السلة، ويعود إذا فُعّل لاحقاً
              && !(from m in db.MedicalOrders join r in db.Referrals on m.OrderItemId equals r.OrderItemId
                   where m.InvoiceItemId == i.InvoiceItemId && (r.Status == ReferralStatuses.External || r.Status == ReferralStatuses.Expired) select m.OrderItemId).Any()
        select i;

    public async Task<BillableItemsDto> BillableItemsAsync(Guid patientId, string? department, CancellationToken ct)
    {
        await LoadPatientAsync(patientId, ct);
        new Validator().Check(department is null or "clinic" or "lab" or "radiology" or "pharmacy", "department", "clinic | lab | radiology | pharmacy")
            .ThrowIfInvalid();
        var rows = await (
            from i in CollectableItems(db)
            join inv in db.Invoices.AsNoTracking() on i.InvoiceId equals inv.InvoiceId
            join d in db.Doctors.AsNoTracking() on i.DoctorId equals d.DoctorId into dj
            from d in dj.DefaultIfEmpty()
            where inv.PatientId == patientId && inv.BranchId == current.BranchId && inv.Status != InvoiceStatuses.Void
            orderby i.CreatedAt
            select new { i, inv.InvoiceNumber, Doctor = d == null ? null : d.FullName }).ToListAsync(ct);
        var items = rows.Select(r =>
            {
                var dept = FinanceCore.DepartmentOf(r.i.ItemType);
                return new BillableItemDto(r.i.InvoiceItemId, r.i.InvoiceId, r.InvoiceNumber, r.i.ServiceName, r.i.ItemType, dept, r.Doctor,
                    r.i.CreatedAt, r.i.Quantity, r.i.NetPrice, r.i.Status, department is not null && dept == department);
            })
            .OrderByDescending(x => x.IsCurrentDepartment).ThenBy(x => x.OrderedAt).ToList();
        return new BillableItemsDto(patientId, await finance.CreditBalanceAsync(patientId, ct), items, items.Sum(i => i.Price));
    }

    public async Task<PaymentResponse> CollectAsync(CollectPaymentRequest req, CancellationToken ct, OfflineContext? offline = null)
    {
        if (offline?.TicketId is { } tid && (req.InvoiceItemIds is null || req.InvoiceItemIds.Count == 0))
        {
            // الأوفلاين يرسل التذكرة بدل البنود: يُحصّل كشفيتها
            var consult = await db.InvoiceItems.Where(i => i.TicketId == tid && (i.Status == InvoiceItemStatuses.Unpaid || i.Status == InvoiceItemStatuses.Deferred))
                .Select(i => i.InvoiceItemId).ToListAsync(ct);
            if (consult.Count == 0) throw new AppException(409, "ITEM_NOT_PAYABLE", "لا توجد كشفية غير مدفوعة لهذه التذكرة");
            req = req with { InvoiceItemIds = consult };
        }
        var ids = (req.InvoiceItemIds ?? []).Distinct().ToList();
        var tenders = req.Tenders ?? [];
        new Validator()
            .Check(req.PatientId is not null, "patient_id", "المريض مطلوب")
            .Check(ids.Count > 0, "invoice_item_ids", "اختر بنداً واحداً على الأقل")
            .Check(req.UseDepositCredit is null or >= 0, "use_deposit_credit", "قيمة غير صالحة")
            .Check(tenders.All(t => t.Method is TenderMethods.Cash or TenderMethods.Pos or TenderMethods.CashUsd), "tenders", "طريقة الدفع cash أو pos أو cash_usd")
            .Check(offline is null || tenders.All(t => t.Method != TenderMethods.CashUsd), "tenders", "الدفع بالدولار غير متاح أوفلاين")
            .Check(tenders.All(t => t.Amount is > 0), "tenders", "كل مبلغ أكبر من صفر")
            .Check(tenders.All(t => t.Method != TenderMethods.Pos || !string.IsNullOrWhiteSpace(t.PosReference)), "tenders", "رقم عملية POS مطلوب")
            .ThrowIfInvalid();
        if (offline is null && (req.OfflineReceiptNumber is not null || req.CreatedOfflineAt is not null))
            throw new AppException(422, "OFFLINE_NOT_AVAILABLE", "التحصيل الأوفلاين يُرسل عبر POST /sync/batch");
        if (offline is not null && string.IsNullOrWhiteSpace(req.OfflineReceiptNumber))
            throw AppException.Validation(new Dictionary<string, string[]> { ["offline_receipt_number"] = ["رقم الإيصال من كتلة الجهاز مطلوب"] });

        var paymentId = req.PaymentId ?? UuidV7.New();
        if (await db.Payments.AnyAsync(p => p.PaymentId == paymentId, ct))
            throw new AppException(409, "PAYMENT_EXISTS", "يوجد دفعة بنفس المعرف");
        // 7.2: الدولار بسعر اليوم فقط، ويُرفض قبل إدخال سعر اليوم
        var usdRate = tenders.Any(t => t.Method == TenderMethods.CashUsd) ? await currency.RequireTodayRateAsync(current.BranchId, ct) : (int?)null;
        var patient = await LoadPatientAsync(req.PatientId!.Value, ct);
        var offlineShift = offline?.ShiftId is not null;
        var shift = offline?.ShiftId is { } sid ? await finance.OfflineShiftAsync(sid, offline.DeviceId, ct) : await finance.RequireOpenShiftAsync(ct);

        await using var tx = await db.BeginTransactionAsync(ct);
        var idArray = ids.ToArray();
        await db.ExecuteAsync($"SELECT 1 FROM invoice_items WHERE invoice_item_id = ANY({idArray}) FOR UPDATE", ct);
        // ترتيب الأقفال ثابت في كل العمليات المالية: البنود ← المريض ← الوردية
        if ((req.UseDepositCredit ?? 0) > 0) await finance.LockPatientAsync(patient.PatientId, ct);
        // الأوفلاين بورديته الأصلية يُقبل حتى لو أُغلقت (المال في درجها)، وتُعاد مطابقتها بعده
        if (offlineShift) await finance.LockShiftAsync(shift.ShiftId, ct);
        else await finance.LockOpenShiftAsync(shift.ShiftId, ct);
        var invoices = await db.Invoices.Include(i => i.Items)
            .Where(i => i.PatientId == patient.PatientId && i.BranchId == current.BranchId && i.Items.Any(x => ids.Contains(x.InvoiceItemId)))
            .ToListAsync(ct);
        var items = invoices.SelectMany(i => i.Items).Where(x => ids.Contains(x.InvoiceItemId)).ToList();
        var missing = ids.Except(items.Select(i => i.InvoiceItemId)).ToList();
        if (missing.Count > 0)
            throw new AppException(404, "INVOICE_ITEM_NOT_FOUND", "بند غير موجود في سلة هذا المريض")
                { Extensions = new Dictionary<string, object?> { ["invoice_item_ids"] = missing } };
        var writtenOff = await DeferralService.WrittenOffItemIds(db).Where(x => ids.Contains(x)).ToListAsync(ct);
        var notPayable = items.Where(i => i.Status is not (InvoiceItemStatuses.Unpaid or InvoiceItemStatuses.Deferred) || writtenOff.Contains(i.InvoiceItemId))
            .Select(i => i.InvoiceItemId).ToList();
        if (notPayable.Count > 0)
            throw new AppException(409, "ITEM_NOT_PAYABLE", "بند مدفوع أو ملغى مسبقاً", "حدّث السلة وأعد المحاولة")
                { Extensions = new Dictionary<string, object?> { ["invoice_item_ids"] = notPayable } };

        // 3.1: في التحصيل الموزّع يُحصَّل من نقطة الحساب اليومي بنود قسمها فقط. المركزي (الافتراضي) يحصّل الكل
        if (!offlineShift && shift.ClinicId is { } pointId
            && await settings.GetStringAsync(SettingsReader.CollectionMode, current.BranchId, ct) == "distributed")
        {
            var pointDept = await db.Clinics.Where(c => c.ClinicId == pointId).Select(c => c.Department).FirstAsync(ct);
            var outside = items.Where(i => FinanceCore.DepartmentOf(i.ItemType) != pointDept).Select(i => i.InvoiceItemId).ToList();
            if (outside.Count > 0)
                throw new AppException(422, "ITEM_OUTSIDE_COLLECTION_POINT", "البند ليس من قسم نقطة التحصيل", "يُحصَّل في قسمه (التحصيل الموزّع)")
                    { Extensions = new Dictionary<string, object?> { ["invoice_item_ids"] = outside } };
        }

        var due = items.Sum(i => i.NetPrice);
        var deposit = req.UseDepositCredit ?? 0;
        var balance = await finance.CreditBalanceAsync(patient.PatientId, ct);
        if (deposit > balance || deposit > due)
            throw new AppException(422, "INSUFFICIENT_CREDIT", "الرصيد المستخدم أكبر من رصيد المريض أو من المستحق")
                { Extensions = new Dictionary<string, object?> { ["credit_balance"] = balance, ["total_due"] = due } };
        var iqdTendered = tenders.Where(t => t.Method != TenderMethods.CashUsd).Sum(t => t.Amount!.Value);
        var usdEquivalent = usdRate is { } rate0 ? tenders.Where(t => t.Method == TenderMethods.CashUsd).Sum(t => t.Amount!.Value * rate0) : 0;
        long change = 0;
        long paid;
        bool mismatch;
        if (usdRate is null)
        {
            paid = iqdTendered;
            // الأوفلاين بسعر قديم مخزن محلياً: المال قُبض فعلاً، فيُقبل ويُعلَّم للمحاسب بدل الرفض
            mismatch = paid != due - deposit;
            if (mismatch && offline is null)
                throw new AppException(422, "AMOUNT_MISMATCH", "المبلغ المدفوع لا يساوي المستحق")
                    { Extensions = new Dictionary<string, object?> { ["expected_amount"] = due - deposit, ["total_due"] = due, ["deposit_used"] = deposit } };
        }
        else
        {
            // الدفع بالدولار: المقابل بالدينار يغطي المستحق، والزائد يُعاد فكةً بالدينار دائماً (لا يتجاوز جزء الدولار)
            var expected = due - deposit;
            change = iqdTendered + usdEquivalent - expected;
            if (change < 0 || iqdTendered > expected || change >= usdEquivalent)
                throw new AppException(422, "AMOUNT_MISMATCH", "المبلغ المدفوع لا يغطي المستحق أو يزيد عن الحاجة")
                {
                    Extensions = new Dictionary<string, object?>
                    {
                        ["expected_amount"] = expected, ["exchange_rate"] = usdRate, ["usd_equivalent"] = usdEquivalent, ["iqd_tendered"] = iqdTendered,
                    },
                };
            paid = expected;
            mismatch = false;
        }

        var now = clock.UtcNow;
        var receipt = offline is null
            ? await finance.NewReceiptAsync(ReceiptTypes.Payment, paid, patient.PatientId, shift.ShiftId, ct)
            : await OfflineReceiptAsync(req.OfflineReceiptNumber!.Trim().ToUpperInvariant(), offline, paid, patient.PatientId, shift.ShiftId,
                offlineShift ? shift.UserId : current.UserId, ct);
        var payment = new Payment
        {
            PaymentId = paymentId,
            BranchId = current.BranchId,
            PatientId = patient.PatientId,
            ShiftId = shift.ShiftId,
            ReceiptId = receipt.ReceiptId,
            TotalPaid = paid,
            DepositUsed = deposit,
            PriceMismatch = mismatch,
            ChangeIqd = change,
            CreatedBy = offlineShift ? shift.UserId : current.UserId, // باسم صاحب الوردية لا من زامن
            CreatedOfflineAt = offline?.OccurredAt,
            CreatedAt = now,
        };
        var changeLeft = change;
        payment.Tenders.AddRange(tenders.Select(t =>
        {
            if (t.Method != TenderMethods.CashUsd)
                return new PaymentTender
                {
                    TenderId = UuidV7.New(), PaymentId = paymentId, Method = t.Method!, Amount = t.Amount!.Value,
                    PosTerminal = t.PosTerminal?.Trim(), PosReference = t.PosReference?.Trim(),
                };
            // amount = المقابل المطبَّق بالدينار (بعد الفكة)، و original_amount = الدولارات المستلمة
            var eq = t.Amount!.Value * usdRate!.Value;
            var take = Math.Min(changeLeft, eq - 1);
            changeLeft -= take;
            return new PaymentTender
            {
                TenderId = UuidV7.New(), PaymentId = paymentId, Method = TenderMethods.CashUsd, Amount = eq - take, Currency = CurrencyService.Usd,
                OriginalAmount = t.Amount.Value, ExchangeRate = usdRate,
            };
        }));
        payment.Allocations.AddRange(items.Select(i => new PaymentAllocation { PaymentId = paymentId, InvoiceItemId = i.InvoiceItemId, Amount = i.NetPrice }));
        db.Payments.Add(payment);
        if (deposit > 0)
            db.PatientCredits.Add(new PatientCreditEntry
            {
                EntryId = UuidV7.New(), PatientId = patient.PatientId, BranchId = current.BranchId, Amount = -deposit, Kind = "applied_to_payment",
                PaymentId = paymentId, CreatedBy = current.UserId, CreatedAt = now,
            });
        var settlesDeferral = items.Any(i => i.Status == InvoiceItemStatuses.Deferred);
        foreach (var i in items) i.Status = InvoiceItemStatuses.Paid;
        foreach (var inv in invoices) FinanceCore.RefreshInvoice(inv);
        audit.Add("PAYMENT_COLLECTED", "payment", paymentId, after: new
        {
            receipt.ReceiptNumber, patient_id = patient.PatientId, total_paid = paid, deposit_used = deposit,
            items = items.Select(i => new { i.InvoiceItemId, i.NetPrice }),
            tenders = tenders.Select(t => new { t.Method, t.Amount }),
            offline = offline is not null, price_mismatch = mismatch, change_iqd = change, exchange_rate = usdRate,
        });
        await db.SaveChangesAsync(ct);
        if (settlesDeferral) await DeferralService.SettlePaidAsync(db, idArray, now, ct);
        if (tx is not null) await tx.CommitAsync(ct);
        await orders.SyncAsync(idArray, ct, inPerson: true); // التحاليل والأشعة المدفوعة حضورياً تدخل قائمة عمل القسم

        // الكشفية المدفوعة تفتح باب الدخول للطبيب: تحديث مباشر للطابور
        var ticketIds = items.Where(i => i.TicketId != null).Select(i => i.TicketId!.Value).ToList();
        if (ticketIds.Count > 0)
        {
            var sessions = await db.QueueTickets.Where(t => ticketIds.Contains(t.TicketId)).Select(t => t.SessionId).Distinct().ToListAsync(ct);
            foreach (var s in sessions) await notifier.SessionChangedAsync(s, ct);
            // إشعار الطبيب بمريض جديد جاهز في طابوره
            var consult = items.Where(i => i.TicketId != null && i.DoctorId != null && i.ItemType is VisitKinds.NewVisit or VisitKinds.FollowUp).ToList();
            foreach (var i in consult)
            {
                var doctorUser = await db.Doctors.Where(d => d.DoctorId == i.DoctorId).Select(d => d.UserId).FirstOrDefaultAsync(ct);
                if (doctorUser is { } du)
                    await notifications.NotifyUsersAsync([du], current.BranchId, Notifications.NotificationTypes.QueueNewPatient, "مريض جديد في الطابور",
                        $"{patient.FullName} دفع الكشفية", null, null, ct);
            }
        }

        var cash = tenders.Where(t => t.Method is TenderMethods.Cash or TenderMethods.CashUsd).Sum(t => t.Amount!.Value);
        return new PaymentResponse(paymentId, shift.ShiftId, paid, deposit, finance.Ref(receipt),
            cash > 0 && drawer.Enabled ? drawer.Issue(receipt.ReceiptNumber, shift.ShiftId, shift.DeviceId) : null, cash > 0 && drawer.Enabled)
        {
            ChangeIqd = change, ExchangeRate = usdRate,
        };
    }

    /// <summary>قبض عربون (فك الحظر): دفعة بلا بنود تدخل الدرج، وتضيف المبلغ لرصيد المريض، ويصدر إيصال "عربون".</summary>
    public async Task<DepositResult> CollectDepositAsync(Patient patient, string method, long amount, string? posReference, string kind, CancellationToken ct)
    {
        var shift = await finance.RequireOpenShiftAsync(ct);
        await finance.LockOpenShiftAsync(shift.ShiftId, ct); // المستدعي يفتح المعاملة
        var now = clock.UtcNow;
        var receipt = await finance.NewReceiptAsync(ReceiptTypes.Deposit, amount, patient.PatientId, shift.ShiftId, ct);
        var payment = new Payment
        {
            PaymentId = UuidV7.New(),
            BranchId = current.BranchId,
            PatientId = patient.PatientId,
            ShiftId = shift.ShiftId,
            ReceiptId = receipt.ReceiptId,
            TotalPaid = amount,
            CreatedBy = current.UserId,
            CreatedAt = now,
        };
        payment.Tenders.Add(new PaymentTender
        {
            TenderId = UuidV7.New(), PaymentId = payment.PaymentId, Method = method, Amount = amount, PosReference = posReference?.Trim(),
        });
        db.Payments.Add(payment);
        db.PatientCredits.Add(new PatientCreditEntry
        {
            EntryId = UuidV7.New(), PatientId = patient.PatientId, BranchId = current.BranchId, Amount = amount, Kind = kind, PaymentId = payment.PaymentId,
            CreatedBy = current.UserId, CreatedAt = now,
        });
        var balance = await finance.CreditBalanceAsync(patient.PatientId, ct) + amount;
        var cash = method == TenderMethods.Cash;
        return new DepositResult(receipt, cash && drawer.Enabled ? drawer.Issue(receipt.ReceiptNumber, shift.ShiftId, shift.DeviceId) : null, cash && drawer.Enabled, balance);
    }

    /// <summary>إيصال برقم من كتلة الجهاز المحجوزة (ضمن مداها، ولم يُستخدم الرقم من قبل).</summary>
    private async Task<Receipt> OfflineReceiptAsync(string number, OfflineContext offline, long total, Guid patientId, Guid shiftId, Guid issuedBy, CancellationToken ct)
    {
        var blocks = await db.ReceiptBlocks.AsNoTracking().Where(b => b.DeviceId == offline.DeviceId && b.BranchId == current.BranchId).ToListAsync(ct);
        var block = blocks.FirstOrDefault(b => number.StartsWith(b.Prefix) && long.TryParse(number[b.Prefix.Length..], out var n)
                                               && n >= b.RangeFrom && n <= (b.ReturnedAt is null ? b.RangeTo : b.LastUsed ?? b.RangeFrom - 1));
        if (block is null)
            throw new AppException(422, "RECEIPT_NUMBER_NOT_IN_BLOCK", "رقم الإيصال ليس من كتلة هذا الجهاز")
                { Extensions = new Dictionary<string, object?> { ["offline_receipt_number"] = number } };
        if (await db.Receipts.AnyAsync(r => r.ReceiptNumber == number, ct))
            throw new AppException(409, "RECEIPT_NUMBER_USED", "رقم الإيصال مستخدم مسبقاً");
        var receipt = new Receipt
        {
            ReceiptId = UuidV7.New(), ReceiptNumber = number, VerifyCode = FinanceCore.NewVerifyCode(), BranchId = current.BranchId, ShiftId = shiftId,
            PatientId = patientId, Type = ReceiptTypes.Payment, Total = total, IssuedOffline = true, BlockId = block.BlockId,
            IssuedBy = issuedBy, IssuedAt = offline.OccurredAt,
        };
        db.Receipts.Add(receipt);
        return receipt;
    }

    private async Task<Patient> LoadPatientAsync(Guid patientId, CancellationToken ct)
    {
        var p = await db.Patients.AsNoTracking().FirstOrDefaultAsync(x => x.PatientId == patientId, ct)
                ?? throw new AppException(404, "PATIENT_NOT_FOUND", "المريض غير موجود");
        if (p.MergedIntoPatientId is not null)
            throw new AppException(409, "PATIENT_MERGED", "هذا الملف مدموج في ملف آخر")
                { Extensions = new Dictionary<string, object?> { ["merged_into_patient_id"] = p.MergedIntoPatientId } };
        return p;
    }
}
