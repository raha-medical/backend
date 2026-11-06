using System.Globalization;
using System.Text;
using AlRahma.Application.Abstractions;
using AlRahma.Application.Common;
using AlRahma.Domain.Common;
using AlRahma.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AlRahma.Application.Clinical;

/// <summary>
/// سطر الوصفة عند التسعير: available = متوفر، wanted = يريده المراجع، quantity = الكمية.
/// الدواء من الكتالوج (service_id) أو سعر يدخله الصيدلاني (unit_price) لسطر بنص حر غير موجود في القائمة.
/// </summary>
public record BillLineInput(short? LineNo, bool? Available, bool? Wanted, short? Quantity, Guid? ServiceId, long? UnitPrice, string? Reason);
public record BillPrescriptionRequest(List<BillLineInput>? Lines);
public record PharmacyBillLineDto(Guid DispenseLineId, short? LineNo, string DrugName, short Quantity, long UnitPrice, long Amount, string Status,
    string StatusAr, Guid? InvoiceItemId, string? PaymentStatus);
public record PharmacyBillDto(Guid? InvoiceId, string? InvoiceNumber, Guid PatientId, string PatientName, string? FileNumber, long TotalDue,
    bool AllPaid, IReadOnlyList<PharmacyBillLineDto> Lines);
public record ExternalSaleItemInput(Guid? ServiceId, short? Quantity);
/// <summary>
/// صرف لمراجع خارجي (6.3-ب): patient_id أو new_patient (الاسم والهاتف)، والأدوية من القائمة. الدواء الذي يحتاج وصفة
/// لا يُصرف إلا مع prescription_file_id (صورة الوصفة، purpose=prescription) أو note (ملاحظة إلزامية).
/// </summary>
public record ExternalSaleRequest(Guid? PatientId, ExternalPatientInput? NewPatient, List<ExternalSaleItemInput>? Items, string? ReferringDoctorName,
    Guid? PrescriptionFileId, string? Note);
public record DispenseLogQuery(DateOnly? From, DateOnly? To, string? Drug, Guid? DoctorId, Guid? UserId, string? Status, bool? External);
public record DispenseLogItem(Guid DispenseLineId, DateTimeOffset At, Guid PatientId, string PatientName, string? FileNumber, string DrugName,
    short Quantity, long UnitPrice, long Amount, string DoctorName, bool IsExternal, string? UserName, string Status, string StatusAr,
    Guid? PrescriptionId, string? PrescriptionCode, string? PrescriptionStatus, string? PrescriptionStatusAr, string? Reason);
public record DrugSummary(string DrugName, int Quantity, long Amount);
public record DispenseLogSummary(int PrescriptionsCount, int ExternalSalesCount, int CompleteCount, int PartialCount, int NotAvailableCount,
    long TotalAmount, IReadOnlyList<DrugSummary> PerDrug);
public record DispenseLogDto(DateOnly From, DateOnly To, IReadOnlyList<DispenseLogItem> Items, DispenseLogSummary Summary);

/// <summary>
/// الصيدلية بالأسعار (خطة المالك المعتمدة، 6.2 و6.3-ب): الأدوية في كتالوج الخدمات (pharmacy) بلا مخزون. الصيدلاني يفتح الوصفة ويؤكد
/// الدخول (مسح الكود)، ثم يحدد لكل سطر المتوفر وما يريده المراجع والكمية، فيُنشأ بند فاتورة pharmacy لكل دواء يُصرف، ويُحصَّل على
/// حساب موظف الصيدلية اليومي بشاشة التحصيل والوصل الموجودين، ثم التسليم بعد الدفع. وسجل صرف يومي بالفلاتر والملخص والتصدير.
/// </summary>
public class PharmacyBillingService(IAppDbContext db, ICurrentUser current, IAuditLogger audit, IClock clock, NumberSequences numbers,
    Billing.CurrencyService currency, ConsultationService consultations)
{
    public static string StatusAr(string s) => s switch
    {
        PharmacyLineStatuses.Billed => "بانتظار الدفع والتسليم",
        PharmacyLineStatuses.Dispensed => "صُرف",
        PharmacyLineStatuses.NotAvailable => "غير متوفر",
        PharmacyLineStatuses.NotWanted => "لم يرده المراجع",
        _ => s,
    };

    public static string RxStatusAr(string s) => s switch { "complete" => "كامل", "partial" => "جزئي", "not_available" => "غير متوفر", _ => "بانتظار" };

    // ------------------------------------------------------------------ تسعير وصفة داخلية

    public async Task<PharmacyBillDto> BillAsync(Guid prescriptionId, BillPrescriptionRequest req, CancellationToken ct)
    {
        await using var tx = await db.BeginTransactionAsync(ct);
        await db.ExecuteAsync($"SELECT 1 FROM prescriptions WHERE prescription_id = {prescriptionId} FOR UPDATE", ct);
        var rx = await db.Prescriptions.Include(p => p.Lines).FirstOrDefaultAsync(p => p.PrescriptionId == prescriptionId, ct)
                 ?? throw new AppException(404, "PRESCRIPTION_NOT_FOUND", "الوصفة غير موجودة");
        if (rx.Status == PrescriptionStatuses.Dispensed)
            throw new AppException(409, "PRESCRIPTION_ALREADY_DISPENSED", "صُرفت الوصفة مسبقاً");
        if (ConsultationService.EffectiveStatus(rx, clock.UtcNow) == PrescriptionStatuses.Expired)
            throw new AppException(409, "PRESCRIPTION_EXPIRED", "انتهت صلاحية الوصفة", "يحتاج المريض وصفة جديدة من الطبيب");
        if (await db.PharmacyDispenseLines.AnyAsync(x => x.PrescriptionId == rx.PrescriptionId, ct))
            throw new AppException(409, "PRESCRIPTION_ALREADY_BILLED", "سُعّرت الوصفة مسبقاً", "حصّل الفاتورة ثم سلّم الأدوية");

        var input = req.Lines ?? [];
        var expected = rx.Lines.Select(l => l.LineNo).OrderBy(x => x).ToList();
        var given = input.Where(l => l.LineNo is not null).Select(l => l.LineNo!.Value).ToList();
        var v = new Validator();
        v.Check(given.Count == input.Count && given.Distinct().Count() == given.Count && given.OrderBy(x => x).SequenceEqual(expected),
            "lines", $"أرسل كل الأسطر مرة واحدة ({string.Join("، ", expected)})");
        var serviceIds = input.Where(l => l.ServiceId is not null).Select(l => l.ServiceId!.Value).Distinct().ToList();
        var drugs = await db.Services.AsNoTracking()
            .Where(s => serviceIds.Contains(s.ServiceId) && s.BranchId == current.BranchId && s.Type == ServiceTypes.Pharmacy && s.IsActive)
            .ToDictionaryAsync(s => s.ServiceId, ct);
        for (var i = 0; i < input.Count; i++)
        {
            var l = input[i];
            v.Check(l.Available is not null, $"lines[{i}].available", "حدد متوفر أو غير متوفر");
            var sell = l.Available == true && l.Wanted != false;
            v.Check(!sell || l.Quantity is > 0 and <= 1000, $"lines[{i}].quantity", "الكمية من 1 إلى 1000");
            v.Check(!sell || l.ServiceId is not null || l.UnitPrice is >= 0, $"lines[{i}].service_id",
                "السطر غير موجود في القائمة: اربطه بدواء من القائمة أو أدخل سعره");
            v.Check(l.ServiceId is null || drugs.ContainsKey(l.ServiceId.Value), $"lines[{i}].service_id", "دواء فعال من قائمة الصيدلية في هذا الفرع");
            v.Check(l.UnitPrice is null or >= 0 and <= 100_000_000, $"lines[{i}].unit_price", "سعر غير صالح");
            v.Check(l.Reason is null || l.Reason.Length <= 300, $"lines[{i}].reason", "300 حرف كحد أقصى");
        }
        v.ThrowIfInvalid();

        var now = clock.UtcNow;
        Invoice? invoice = null;
        var log = new List<PharmacyDispenseLine>();
        var links = new List<(PrescriptionLine Line, Guid ItemId)>();
        foreach (var l in input.OrderBy(x => x.LineNo))
        {
            var line = rx.Lines.First(x => x.LineNo == l.LineNo);
            var row = new PharmacyDispenseLine
            {
                DispenseLineId = UuidV7.New(), BranchId = current.BranchId, PatientId = rx.PatientId, PrescriptionId = rx.PrescriptionId,
                LineNo = line.LineNo, DrugName = line.DrugName, DoctorId = rx.DoctorId, CreatedBy = current.UserId, CreatedAt = now,
                Reason = string.IsNullOrWhiteSpace(l.Reason) ? null : l.Reason.Trim(),
            };
            if (l.Available == false || l.Wanted == false)
            {
                row.Status = l.Available == false ? PharmacyLineStatuses.NotAvailable : PharmacyLineStatuses.NotWanted;
                log.Add(row);
                continue;
            }
            var drug = l.ServiceId is { } sid ? drugs[sid] : null;
            var price = drug is null ? new Billing.ItemPrice(l.UnitPrice!.Value, "IQD", null, null) : await currency.PriceAsync(drug, current.BranchId, ct);
            invoice ??= await NewInvoiceAsync(rx.PatientId, now, ct);
            var item = NewItem(invoice, drug ?? await ManualDrugServiceAsync(now, ct), drug?.Name ?? line.DrugName, l.Quantity!.Value, price, rx.DoctorId, now);
            line.ServiceId = drug?.ServiceId;
            line.Quantity = l.Quantity;
            line.UnitPrice = price.UnitPriceIqd;
            links.Add((line, item.InvoiceItemId)); // يُربط بعد حفظ البند (ترتيب الحفظ)
            row.DrugName = drug?.Name ?? line.DrugName;
            row.ServiceId = drug?.ServiceId;
            row.Quantity = l.Quantity.Value;
            row.UnitPrice = price.UnitPriceIqd;
            row.Amount = item.NetPrice;
            row.InvoiceId = invoice.InvoiceId;
            row.InvoiceItemId = item.InvoiceItemId;
            row.Status = PharmacyLineStatuses.Billed;
            log.Add(row);
        }
        if (invoice is not null) Finish(invoice);
        await db.SaveChangesAsync(ct); // الفاتورة وبنودها أولاً: السجل يشير إليها (لا علاقة ملاحة في EF تضمن الترتيب)
        db.PharmacyDispenseLines.AddRange(log);
        foreach (var (line, itemId) in links) line.InvoiceItemId = itemId;
        audit.Add("PRESCRIPTION_BILLED", "prescription", rx.PrescriptionId, after: new
        {
            rx.PrescriptionCode, invoice = invoice?.InvoiceNumber, lines = log.Select(x => new { x.LineNo, x.DrugName, x.Quantity, x.Amount, x.Status }),
        });
        await db.SaveChangesAsync(ct);
        if (tx is not null) await tx.CommitAsync(ct);
        return await BillOfPrescriptionAsync(rx.PrescriptionId, ct);
    }

    public async Task<PharmacyBillDto> BillOfPrescriptionAsync(Guid prescriptionId, CancellationToken ct)
    {
        var rx = await db.Prescriptions.AsNoTracking().FirstOrDefaultAsync(p => p.PrescriptionId == prescriptionId, ct)
                 ?? throw new AppException(404, "PRESCRIPTION_NOT_FOUND", "الوصفة غير موجودة");
        var rows = await db.PharmacyDispenseLines.AsNoTracking().Where(x => x.PrescriptionId == prescriptionId).OrderBy(x => x.LineNo).ToListAsync(ct);
        return await ToBillAsync(rx.PatientId, rows, ct);
    }

    // ------------------------------------------------------------------ المراجع الخارجي

    public async Task<PharmacyBillDto> ExternalSaleAsync(ExternalSaleRequest req, CancellationToken ct)
    {
        var items = req.Items ?? [];
        var v = new Validator()
            .Check(req.PatientId is not null || req.NewPatient is not null, "patient_id", "اختر المراجع أو أدخل اسمه وهاتفه")
            .Check(items.Count is > 0 and <= 30, "items", "من 1 إلى 30 دواء")
            .Check(req.ReferringDoctorName is null || req.ReferringDoctorName.Length <= 150, "referring_doctor_name", "150 حرفاً كحد أقصى")
            .Check(req.Note is null || req.Note.Length <= 1000, "note", "1000 حرف كحد أقصى");
        var ids = items.Where(i => i.ServiceId is not null).Select(i => i.ServiceId!.Value).Distinct().ToList();
        var drugs = await db.Services.AsNoTracking()
            .Where(s => ids.Contains(s.ServiceId) && s.BranchId == current.BranchId && s.Type == ServiceTypes.Pharmacy && s.IsActive)
            .ToDictionaryAsync(s => s.ServiceId, ct);
        for (var i = 0; i < items.Count; i++)
        {
            v.Check(items[i].ServiceId is { } sid && drugs.ContainsKey(sid), $"items[{i}].service_id", "دواء فعال من قائمة الصيدلية في هذا الفرع");
            v.Check(items[i].Quantity is null or > 0 and <= 1000, $"items[{i}].quantity", "الكمية من 1 إلى 1000");
        }
        v.ThrowIfInvalid();

        // 9.3-ب: الدواء الذي يحتاج وصفة لا يُصرف للخارجي إلا بصورة الوصفة أو ملاحظة إلزامية
        var restricted = items.Select(i => drugs[i.ServiceId!.Value]).Where(d => d.RequiresPrescription).Select(d => d.Name).Distinct().ToList();
        StoredFile? file = null;
        if (req.PrescriptionFileId is { } fid)
            file = await db.Files.FirstOrDefaultAsync(f => f.FileId == fid && f.BranchId == current.BranchId && f.Purpose == FilePurposes.Prescription
                                                            && f.Status == FileStatuses.Stored, ct)
                   ?? throw AppException.Validation(new Dictionary<string, string[]> { ["prescription_file_id"] = ["صورة الوصفة غير موجودة أو لم يكتمل رفعها"] });
        var note = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim();
        if (restricted.Count > 0 && file is null && (note is null || note.Length < 5))
            throw new AppException(422, "PRESCRIPTION_REQUIRED", "دواء يحتاج وصفة", "أرفق صورة الوصفة أو اكتب ملاحظة إلزامية (5 أحرف على الأقل)")
                { Extensions = new Dictionary<string, object?> { ["drugs"] = restricted } };

        var (patientId, _) = await consultations.ResolveExternalPatientAsync(req.PatientId, req.NewPatient, ct);
        var patient = await db.Patients.AsNoTracking().FirstOrDefaultAsync(p => p.PatientId == patientId, ct)
                      ?? throw new AppException(404, "PATIENT_NOT_FOUND", "المريض غير موجود");
        if (file is not null)
        {
            if (file.PatientId is not null && file.PatientId != patientId)
                throw AppException.Validation(new Dictionary<string, string[]> { ["prescription_file_id"] = ["صورة الوصفة لمراجع آخر"] });
            file.PatientId = patientId;
        }
        var now = clock.UtcNow;
        var invoice = await NewInvoiceAsync(patientId, now, ct);
        var rows = new List<PharmacyDispenseLine>();
        foreach (var i in items)
        {
            var drug = drugs[i.ServiceId!.Value];
            var qty = i.Quantity ?? 1;
            var price = await currency.PriceAsync(drug, current.BranchId, ct);
            var item = NewItem(invoice, drug, drug.Name, qty, price, null, now);
            rows.Add(new PharmacyDispenseLine
            {
                DispenseLineId = UuidV7.New(), BranchId = current.BranchId, PatientId = patientId, InvoiceId = invoice.InvoiceId,
                InvoiceItemId = item.InvoiceItemId, ServiceId = drug.ServiceId, DrugName = drug.Name, Quantity = qty, UnitPrice = price.UnitPriceIqd,
                Amount = item.NetPrice, Status = PharmacyLineStatuses.Billed, IsExternal = true,
                ReferringDoctorName = string.IsNullOrWhiteSpace(req.ReferringDoctorName) ? null : req.ReferringDoctorName.Trim(),
                PrescriptionFileId = drug.RequiresPrescription ? file?.FileId : null, Note = note, CreatedBy = current.UserId, CreatedAt = now,
            });
        }
        Finish(invoice);
        await db.SaveChangesAsync(ct);
        db.PharmacyDispenseLines.AddRange(rows);
        audit.Add("PHARMACY_EXTERNAL_SALE", "invoice", invoice.InvoiceId, after: new
        {
            invoice.InvoiceNumber, patient_id = patientId, items = rows.Select(r => new { r.DrugName, r.Quantity, r.Amount }),
            restricted, prescription_file_id = file?.FileId, note,
        });
        await db.SaveChangesAsync(ct);
        return await ToBillAsync(patient.PatientId, rows, ct);
    }

    /// <summary>التسليم للمراجع الخارجي بعد الدفع.</summary>
    public async Task<PharmacyBillDto> DeliverExternalAsync(Guid invoiceId, CancellationToken ct)
    {
        var rows = await db.PharmacyDispenseLines.Where(x => x.InvoiceId == invoiceId && x.IsExternal && x.BranchId == current.BranchId).ToListAsync(ct);
        if (rows.Count == 0) throw new AppException(404, "SALE_NOT_FOUND", "فاتورة الصيدلية غير موجودة");
        var pending = rows.Where(r => r.Status == PharmacyLineStatuses.Billed).ToList();
        if (pending.Count == 0) return await ToBillAsync(rows[0].PatientId, rows, ct);
        var itemIds = pending.Select(r => r.InvoiceItemId!.Value).ToList();
        var unpaid = await db.InvoiceItems.AsNoTracking().Where(i => itemIds.Contains(i.InvoiceItemId) && i.Status != InvoiceItemStatuses.Paid)
            .Select(i => i.InvoiceItemId).ToListAsync(ct);
        if (unpaid.Count > 0)
            throw new AppException(402, "INVOICE_UNPAID", "الفاتورة غير مدفوعة", "التسليم بعد التحصيل")
                { Extensions = new Dictionary<string, object?> { ["invoice_item_ids"] = unpaid } };
        var now = clock.UtcNow;
        foreach (var r in pending) { r.Status = PharmacyLineStatuses.Dispensed; r.DispensedBy = current.UserId; r.DispensedAt = now; }
        audit.Add("PHARMACY_EXTERNAL_DELIVERED", "invoice", invoiceId, after: new { lines = pending.Count });
        await db.SaveChangesAsync(ct);
        return await ToBillAsync(rows[0].PatientId, rows, ct);
    }

    // ------------------------------------------------------------------ الصرف (يُستدعى من PharmacyService)

    /// <summary>
    /// قبل الصرف: الوصفة سُعّرت، وكل سطر يُصرف له بند مدفوع (التسليم بعد الدفع). سطر بلا تسعير لا يُصرف.
    /// بعد الصرف: الأسطر المصروفة «صُرف»، والمسعّرة غير المصروفة «لم يرده المراجع».
    /// </summary>
    public async Task<List<PharmacyDispenseLine>> EnsurePaidForDispenseAsync(Prescription rx, IReadOnlyCollection<short> dispensedLines, CancellationToken ct)
    {
        var rows = await db.PharmacyDispenseLines.Where(x => x.PrescriptionId == rx.PrescriptionId).ToListAsync(ct);
        if (dispensedLines.Count == 0) return rows;
        if (rows.Count == 0)
            throw new AppException(422, "PRESCRIPTION_NOT_BILLED", "سعّر الوصفة أولاً", "حدد المتوفر والكمية لكل سطر فتُنشأ الفاتورة، ثم التسليم بعد الدفع");
        var notBilled = rows.Where(r => r.LineNo is { } n && dispensedLines.Contains(n) && r.InvoiceItemId is null).Select(r => r.LineNo).ToList();
        if (notBilled.Count > 0)
            throw new AppException(422, "LINE_NOT_BILLED", "سطر غير مسعّر لا يُصرف", "اربطه بدواء من القائمة أو أدخل سعره")
                { Extensions = new Dictionary<string, object?> { ["line_nos"] = notBilled } };
        var itemIds = rows.Where(r => r.LineNo is { } n && dispensedLines.Contains(n)).Select(r => r.InvoiceItemId!.Value).ToList();
        var unpaid = await db.InvoiceItems.AsNoTracking().Where(i => itemIds.Contains(i.InvoiceItemId) && i.Status != InvoiceItemStatuses.Paid)
            .Select(i => i.InvoiceItemId).ToListAsync(ct);
        if (unpaid.Count > 0)
            throw new AppException(402, "INVOICE_UNPAID", "فاتورة الأدوية غير مدفوعة", "التسليم بعد التحصيل")
                { Extensions = new Dictionary<string, object?> { ["invoice_item_ids"] = unpaid } };
        return rows;
    }

    public void MarkDispensed(List<PharmacyDispenseLine> rows, IReadOnlyCollection<short> dispensedLines, IReadOnlyDictionary<short, string?> reasons, DateTimeOffset now)
    {
        foreach (var r in rows)
        {
            if (r.LineNo is not { } n) continue;
            if (dispensedLines.Contains(n) && r.Status == PharmacyLineStatuses.Billed)
            {
                r.Status = PharmacyLineStatuses.Dispensed;
                r.DispensedBy = current.UserId;
                r.DispensedAt = now;
            }
            else if (!dispensedLines.Contains(n) && r.Status == PharmacyLineStatuses.Billed)
            {
                r.Status = PharmacyLineStatuses.NotWanted;
                r.Reason ??= reasons.GetValueOrDefault(n);
            }
        }
    }

    // ------------------------------------------------------------------ سجل الصرف اليومي

    public async Task<DispenseLogDto> LogAsync(DispenseLogQuery q, CancellationToken ct)
    {
        var today = clock.BaghdadToday();
        var from = q.From ?? today;
        var to = q.To ?? from;
        new Validator()
            .Check(to >= from && to.DayNumber - from.DayNumber <= 92, "to", "الفترة 93 يوماً كحد أقصى")
            .Check(q.Status is null or "dispensed" or "billed" or "not_available" or "not_wanted", "status", "dispensed | billed | not_available | not_wanted")
            .ThrowIfInvalid();
        DateTimeOffset start = ClockExtensions.ToUtc(from, TimeOnly.MinValue), end = ClockExtensions.ToUtc(to.AddDays(1), TimeOnly.MinValue);
        var rows = db.PharmacyDispenseLines.AsNoTracking().Where(x => x.BranchId == current.BranchId
            && (x.DispensedAt ?? x.CreatedAt) >= start && (x.DispensedAt ?? x.CreatedAt) < end);
        if (!string.IsNullOrWhiteSpace(q.Drug)) rows = rows.Where(x => EF.Functions.ILike(x.DrugName, "%" + q.Drug.Trim() + "%"));
        if (q.DoctorId is { } did) rows = rows.Where(x => x.DoctorId == did);
        if (q.UserId is { } uid) rows = rows.Where(x => (x.DispensedBy ?? x.CreatedBy) == uid);
        if (q.Status is not null) rows = rows.Where(x => x.Status == q.Status);
        if (q.External is { } ext) rows = rows.Where(x => x.IsExternal == ext);
        var list = await rows.OrderByDescending(x => x.DispensedAt ?? x.CreatedAt).Take(2000).ToListAsync(ct);

        var patientIds = list.Select(x => x.PatientId).Distinct().ToList();
        var patients = await db.Patients.AsNoTracking().Where(p => patientIds.Contains(p.PatientId)).ToDictionaryAsync(p => p.PatientId, ct);
        var doctorIds = list.Where(x => x.DoctorId != null).Select(x => x.DoctorId!.Value).Distinct().ToList();
        var doctors = await db.Doctors.AsNoTracking().Where(d => doctorIds.Contains(d.DoctorId)).ToDictionaryAsync(d => d.DoctorId, d => d.FullName, ct);
        var userIds = list.Select(x => x.DispensedBy ?? x.CreatedBy).Where(x => x != null).Select(x => x!.Value).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.UserId)).ToDictionaryAsync(u => u.UserId, u => u.FullName, ct);
        var rxIds = list.Where(x => x.PrescriptionId != null).Select(x => x.PrescriptionId!.Value).Distinct().ToList();
        var codes = await db.Prescriptions.AsNoTracking().Where(p => rxIds.Contains(p.PrescriptionId)).ToDictionaryAsync(p => p.PrescriptionId, p => p.PrescriptionCode, ct);
        // حالة الوصفة كاملة (من كل أسطرها، لا من المفلتر فقط): كامل / جزئي / غير متوفر
        var allRxLines = await db.PharmacyDispenseLines.AsNoTracking().Where(x => x.PrescriptionId != null && rxIds.Contains(x.PrescriptionId.Value))
            .Select(x => new { x.PrescriptionId, x.Status }).ToListAsync(ct);
        var rxStatus = allRxLines.GroupBy(x => x.PrescriptionId!.Value).ToDictionary(g => g.Key, g =>
        {
            var dispensed = g.Count(x => x.Status == PharmacyLineStatuses.Dispensed);
            if (g.Any(x => x.Status == PharmacyLineStatuses.Billed)) return "pending";
            return dispensed == g.Count() ? "complete" : dispensed > 0 ? "partial" : "not_available";
        });

        var items = list.Select(x =>
        {
            var p = patients.GetValueOrDefault(x.PatientId);
            var by = x.DispensedBy ?? x.CreatedBy;
            string? rs = x.PrescriptionId is { } rid ? rxStatus.GetValueOrDefault(rid) : null;
            return new DispenseLogItem(x.DispenseLineId, x.DispensedAt ?? x.CreatedAt, x.PatientId, p?.FullName ?? "", p?.FileNumber, x.DrugName,
                x.Quantity, x.UnitPrice, x.Amount,
                x.IsExternal ? (x.ReferringDoctorName is null ? "مراجع خارجي" : $"مراجع خارجي ({x.ReferringDoctorName})")
                    : x.DoctorId is { } d ? doctors.GetValueOrDefault(d, "") : "",
                x.IsExternal, by is { } u ? users.GetValueOrDefault(u) : null, x.Status, StatusAr(x.Status),
                x.PrescriptionId, x.PrescriptionId is { } r2 ? codes.GetValueOrDefault(r2) : null, rs, rs is null ? null : RxStatusAr(rs), x.Reason);
        }).ToList();
        var dispensedRows = list.Where(x => x.Status == PharmacyLineStatuses.Dispensed).ToList();
        var summary = new DispenseLogSummary(
            rxIds.Count, list.Where(x => x.IsExternal).Select(x => x.InvoiceId).Distinct().Count(),
            rxIds.Count(r => rxStatus.GetValueOrDefault(r) == "complete"), rxIds.Count(r => rxStatus.GetValueOrDefault(r) == "partial"),
            rxIds.Count(r => rxStatus.GetValueOrDefault(r) == "not_available"),
            dispensedRows.Sum(x => x.Amount),
            dispensedRows.GroupBy(x => x.DrugName).Select(g => new DrugSummary(g.Key, g.Sum(x => x.Quantity), g.Sum(x => x.Amount)))
                .OrderByDescending(g => g.Quantity).ToList());
        return new DispenseLogDto(from, to, items, summary);
    }

    /// <summary>CSV بترميز UTF-8 مع BOM (يفتح في Excel بالعربية).</summary>
    public static byte[] ToCsv(DispenseLogDto log)
    {
        static string E(string? s) => s is null ? "" : s.Contains(',') || s.Contains('"') || s.Contains('\n') ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
        var sb = new StringBuilder();
        sb.AppendLine("الوقت,المراجع,رقم الملف,الدواء,الكمية,السعر,المبلغ,الطبيب المحوِّل,الموظف,الحالة,الوصفة,حالة الوصفة");
        foreach (var i in log.Items)
            sb.AppendLine(string.Join(',', E(i.At.ToOffset(ClockExtensions.BaghdadOffset).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)),
                E(i.PatientName), E(i.FileNumber), E(i.DrugName), i.Quantity, i.UnitPrice, i.Amount, E(i.DoctorName), E(i.UserName), E(i.StatusAr),
                E(i.PrescriptionCode), E(i.PrescriptionStatusAr)));
        sb.AppendLine();
        sb.AppendLine($"عدد الوصفات,{log.Summary.PrescriptionsCount}");
        sb.AppendLine($"مبيعات المراجعين الخارجيين,{log.Summary.ExternalSalesCount}");
        sb.AppendLine($"إجمالي المبالغ,{log.Summary.TotalAmount}");
        foreach (var d in log.Summary.PerDrug) sb.AppendLine($"{E(d.DrugName)},{d.Quantity},{d.Amount}");
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];
    }

    // ------------------------------------------------------------------ أدوات

    private async Task<Invoice> NewInvoiceAsync(Guid patientId, DateTimeOffset now, CancellationToken ct)
    {
        var invoice = new Invoice
        {
            InvoiceId = UuidV7.New(),
            InvoiceNumber = $"{await numbers.BranchCodeAsync(current.BranchId, ct)}-INV-{await numbers.NextAsync(current.BranchId, "invoice", "all", ct):D6}",
            BranchId = current.BranchId, PatientId = patientId, CreatedBy = current.UserId, CreatedAt = now,
        };
        db.Invoices.Add(invoice);
        return invoice;
    }

    public const string ManualDrugName = "دواء خارج القائمة (سعر يدوي)";

    /// <summary>بند الفاتورة يحتاج خدمة: سطر بنص حر بسعر يدخله الصيدلاني يُربط بخدمة نظام موقوفة لكل فرع (لا تظهر للبيع).</summary>
    private async Task<Service> ManualDrugServiceAsync(DateTimeOffset now, CancellationToken ct)
    {
        var s = await db.Services.FirstOrDefaultAsync(x => x.BranchId == current.BranchId && x.Type == ServiceTypes.Pharmacy && x.Name == ManualDrugName, ct);
        if (s is not null) return s;
        s = new Service
        {
            ServiceId = UuidV7.New(), BranchId = current.BranchId, Name = ManualDrugName, Type = ServiceTypes.Pharmacy, Price = 0, IsActive = false,
            CreatedAt = now, UpdatedAt = now,
        };
        db.Services.Add(s);
        return s;
    }

    private static InvoiceItem NewItem(Invoice invoice, Service drug, string name, short qty, Billing.ItemPrice price, Guid? doctorId, DateTimeOffset now)
    {
        var net = price.UnitPriceIqd * qty;
        var item = new InvoiceItem
        {
            InvoiceItemId = UuidV7.New(), InvoiceId = invoice.InvoiceId, ServiceId = drug.ServiceId, DoctorId = doctorId, ItemType = ServiceTypes.Pharmacy,
            ServiceName = name.Length > 150 ? name[..150] : name, Quantity = qty, UnitPrice = price.UnitPriceIqd, NetPrice = net, DoctorShare = 0, ComplexShare = net,
            Status = net == 0 ? InvoiceItemStatuses.Paid : InvoiceItemStatuses.Unpaid, CreatedAt = now,
            Currency = price.Currency, OriginalUnitPrice = price.OriginalUnitPrice, ExchangeRate = price.Rate,
        };
        invoice.Items.Add(item);
        return item;
    }

    private static void Finish(Invoice invoice)
    {
        invoice.GrossAmount = invoice.Items.Sum(i => i.NetPrice);
        invoice.NetAmount = invoice.GrossAmount;
        Billing.FinanceCore.RefreshInvoice(invoice);
    }

    private async Task<PharmacyBillDto> ToBillAsync(Guid patientId, List<PharmacyDispenseLine> rows, CancellationToken ct)
    {
        var patient = await db.Patients.AsNoTracking().FirstAsync(p => p.PatientId == patientId, ct);
        var itemIds = rows.Where(r => r.InvoiceItemId != null).Select(r => r.InvoiceItemId!.Value).ToList();
        var statuses = await db.InvoiceItems.AsNoTracking().Where(i => itemIds.Contains(i.InvoiceItemId)).ToDictionaryAsync(i => i.InvoiceItemId, i => i.Status, ct);
        var invoiceId = rows.FirstOrDefault(r => r.InvoiceId != null)?.InvoiceId;
        var number = invoiceId is { } iid ? await db.Invoices.AsNoTracking().Where(i => i.InvoiceId == iid).Select(i => i.InvoiceNumber).FirstOrDefaultAsync(ct) : null;
        var lines = rows.OrderBy(r => r.LineNo).ThenBy(r => r.CreatedAt).Select(r => new PharmacyBillLineDto(r.DispenseLineId, r.LineNo, r.DrugName, r.Quantity,
            r.UnitPrice, r.Amount, r.Status, StatusAr(r.Status), r.InvoiceItemId,
            r.InvoiceItemId is { } i ? statuses.GetValueOrDefault(i) : null)).ToList();
        var due = rows.Where(r => r.InvoiceItemId is { } i && statuses.GetValueOrDefault(i) == InvoiceItemStatuses.Unpaid).Sum(r => r.Amount);
        return new PharmacyBillDto(invoiceId, number, patient.PatientId, patient.FullName, patient.FileNumber, due,
            lines.Where(l => l.InvoiceItemId is not null).All(l => l.PaymentStatus == InvoiceItemStatuses.Paid), lines);
    }
}
