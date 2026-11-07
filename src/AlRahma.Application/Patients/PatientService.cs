using AlRahma.Application.Abstractions;
using AlRahma.Application.Common;
using AlRahma.Domain.Common;
using AlRahma.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AlRahma.Application.Patients;

public record CreatePatientRequest(Guid? PatientId, string? FullName, string? PhoneNumber, DateOnly? BirthDate, short? ApproxAge,
    string? Gender, DateTimeOffset? CreatedOfflineAt);
public record UpdatePatientRequest(string? FullName, string? PhoneNumber, DateOnly? BirthDate, short? ApproxAge, string? Gender);
public record PatientBriefDto(Guid PatientId, string FileNumber, string FullName, string PhoneNumber);
public record CreatePatientResponse(Guid PatientId, string FileNumber, string FullName, string PhoneNumber, IReadOnlyList<PatientBriefDto> PossibleDuplicates);
public record PatientSearchItem(Guid PatientId, string FileNumber, string FullName, string PhoneNumber, DateOnly? LastVisitDate,
    bool IsBanned, long OutstandingBalance);
public record PatientDto(Guid PatientId, string FileNumber, string FullName, string PhoneNumber, DateOnly? BirthDate, short? ApproxAge,
    string? Gender, Guid? HomeBranchId, bool IsBanned, short NoShowCount, long CreditBalance, Guid? MergedIntoPatientId,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public record MergePatientsRequest(Guid? PrimaryPatientId, Guid? DuplicatePatientId);
public record MergePatientsResponse(Guid PatientId, int MovedRecords);
public record BanStatusDto(bool IsBanned, string? Reason, int NoShowCount, DateTimeOffset? BannedAt, long UnbanDepositAmount);
public record UnbanRequest(string? Method, long? Amount, string? PosReference, string? Note);
public record UnbanResponse(bool IsBanned, long DepositCredit, Billing.ReceiptRefDto? Receipt, string? DrawerToken = null, bool OpenDrawer = false);

public class PatientService(IAppDbContext db, ICurrentUser current, IAuditLogger audit, IClock clock,
    NumberSequences numbers, SettingsReader settings, BanService bans, Billing.PaymentService payments, Billing.FinanceCore finance)
{
    public async Task<CreatePatientResponse> CreateAsync(CreatePatientRequest req, CancellationToken ct)
    {
        var phone = PhoneNumbers.Normalize(req.PhoneNumber);
        ValidateFields(req.FullName, req.PhoneNumber, phone, req.BirthDate, req.ApproxAge, req.Gender, required: true);

        var id = req.PatientId ?? UuidV7.New();
        if (await db.Patients.AnyAsync(p => p.PatientId == id, ct))
            throw new AppException(409, "PATIENT_EXISTS", "يوجد مريض بنفس المعرف");

        var name = NormalizeName(req.FullName!);
        var duplicates = await db.Patients.AsNoTracking()
            .Where(p => p.PhoneNumber == phone && p.MergedIntoPatientId == null)
            .OrderBy(p => p.CreatedAt)
            .Select(p => new PatientBriefDto(p.PatientId, p.FileNumber, p.FullName, p.PhoneNumber))
            .Take(10).ToListAsync(ct);

        var seq = await numbers.NextAsync(current.BranchId, "patient_file", "all", ct);
        var code = await numbers.BranchCodeAsync(current.BranchId, ct);
        var now = clock.UtcNow;
        var patient = new Patient
        {
            PatientId = id,
            FileNumber = $"{code}-{seq:D6}",
            FullName = name,
            PhoneNumber = phone ?? "",
            BirthDate = req.BirthDate,
            ApproxAge = req.BirthDate is null ? req.ApproxAge : null,
            Gender = req.Gender,
            HomeBranchId = current.BranchId,
            CreatedBy = current.UserId,
            CreatedOfflineAt = req.CreatedOfflineAt,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Patients.Add(patient);
        await db.SaveChangesAsync(ct);
        return new CreatePatientResponse(patient.PatientId, patient.FileNumber, patient.FullName, patient.PhoneNumber, duplicates);
    }

    public async Task<PagedResult<PatientSearchItem>> SearchAsync(string? phone, string? q, string? fileNumber, PageQuery paging, CancellationToken ct)
    {
        var query = db.Patients.AsNoTracking().Where(p => p.MergedIntoPatientId == null);
        if (!string.IsNullOrWhiteSpace(phone))
        {
            var normalized = PhoneNumbers.Normalize(phone);
            var digits = new string(phone.Where(char.IsDigit).ToArray());
            query = normalized is not null
                ? query.Where(p => p.PhoneNumber == normalized)
                : query.Where(p => p.PhoneNumber.Contains(digits));
        }
        else if (!string.IsNullOrWhiteSpace(fileNumber))
        {
            var fn = fileNumber.Trim().ToUpperInvariant();
            query = query.Where(p => p.FileNumber == fn);
        }
        else if (!string.IsNullOrWhiteSpace(q))
        {
            var term = NormalizeName(q);
            query = query.Where(p => EF.Functions.ILike(p.FullName, $"%{term}%") || p.FileNumber == term.ToUpper());
        }
        else
        {
            throw AppException.Validation(new Dictionary<string, string[]> { ["q"] = ["أرسل phone أو q أو file_number"] });
        }

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderBy(p => p.FullName)
            .Skip((paging.SafePage - 1) * paging.SafePageSize).Take(paging.SafePageSize)
            .Select(p => new PatientSearchItem(
                p.PatientId, p.FileNumber, p.FullName, p.PhoneNumber,
                db.QueueTickets.Where(t => t.PatientId == p.PatientId && t.Status == TicketStatuses.Completed)
                    .Max(t => (DateOnly?)t.Session!.SessionDate),
                p.IsBanned,
                db.Invoices.Where(i => i.PatientId == p.PatientId &&
                                       (i.Status == InvoiceStatuses.Unpaid || i.Status == InvoiceStatuses.PartiallyPaid))
                    .Sum(i => (long?)(i.NetAmount - i.PaidAmount)) ?? 0))
            .ToListAsync(ct);
        return new PagedResult<PatientSearchItem>(items, paging.SafePage, paging.SafePageSize, total);
    }

    public async Task<PatientDto> GetAsync(Guid patientId, CancellationToken ct)
    {
        var p = await LoadAsync(patientId, ct);
        return await ToDtoAsync(p, ct);
    }

    public async Task<PatientDto> UpdateAsync(Guid patientId, UpdatePatientRequest req, CancellationToken ct)
    {
        var p = await LoadAsync(patientId, ct);
        if (p.MergedIntoPatientId is not null)
            throw new AppException(409, "PATIENT_MERGED", "هذا الملف مدموج في ملف آخر", "عدّل الملف الأساسي");
        var phone = req.PhoneNumber is null ? null : PhoneNumbers.Normalize(req.PhoneNumber);
        ValidateFields(req.FullName, req.PhoneNumber, phone, req.BirthDate, req.ApproxAge, req.Gender, required: false);

        var before = Snapshot(p);
        if (req.FullName is not null) p.FullName = NormalizeName(req.FullName);
        if (phone is not null) p.PhoneNumber = phone;
        if (req.BirthDate is not null) { p.BirthDate = req.BirthDate; p.ApproxAge = null; }
        else if (req.ApproxAge is not null) { p.ApproxAge = req.ApproxAge; p.BirthDate = null; }
        if (req.Gender is not null) p.Gender = req.Gender;
        p.UpdatedAt = clock.UtcNow;
        audit.Add("PATIENT_UPDATED", "patient", p.PatientId, before, Snapshot(p));
        await db.SaveChangesAsync(ct);
        return await ToDtoAsync(p, ct);
    }

    /// <summary>كل الجداول التي تشير إلى المريض وتنتقل للملف الأساسي عند الدمج.</summary>
    private static readonly string[] PatientTables =
    [
        "bookings", "queue_tickets", "invoices", "patient_no_shows", "patient_ban_events", "patient_credit_ledger",
        "consultations", "prescriptions", "medical_orders", "online_payments", "payments", "receipts", "deferrals",
    ];

    public async Task<MergePatientsResponse> MergeAsync(MergePatientsRequest req, CancellationToken ct)
    {
        new Validator()
            .Check(req.PrimaryPatientId is not null, "primary_patient_id", "الملف الأساسي مطلوب")
            .Check(req.DuplicatePatientId is not null, "duplicate_patient_id", "الملف المكرر مطلوب")
            .Check(req.PrimaryPatientId != req.DuplicatePatientId, "duplicate_patient_id", "لا يمكن دمج الملف مع نفسه")
            .ThrowIfInvalid();

        await using var tx = await db.BeginTransactionAsync(ct);
        var primary = await LoadAsync(req.PrimaryPatientId!.Value, ct);
        var duplicate = await LoadAsync(req.DuplicatePatientId!.Value, ct);
        if (primary.MergedIntoPatientId is not null || duplicate.MergedIntoPatientId is not null)
            throw new AppException(409, "PATIENT_MERGED", "أحد الملفين مدموج مسبقاً");

        var moved = 0;
        try
        {
            foreach (var table in PatientTables)
                moved += await MoveAsync(table, duplicate.PatientId, primary.PatientId, ct);
            // روابط حسابات التطبيق: تُنقل، والمكرر منها لنفس الحساب يُحذف
            await db.ExecuteAsync($"""
                DELETE FROM patient_account_links d USING patient_account_links p
                 WHERE d.patient_id = {duplicate.PatientId} AND p.patient_id = {primary.PatientId}
                   AND d.patient_account_id = p.patient_account_id
                """, ct);
            moved += await db.ExecuteAsync($"UPDATE patient_account_links SET patient_id = {primary.PatientId} WHERE patient_id = {duplicate.PatientId}", ct);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new AppException(409, "MERGE_CONFLICT", "لا يمكن الدمج: الملفان محجوزان في نفس الوجبة",
                "ألغِ أحد الحجزين ثم أعد الدمج");
        }

        primary.NoShowCount = (short)(primary.NoShowCount + duplicate.NoShowCount);
        if (duplicate.IsBanned && !primary.IsBanned)
        {
            primary.IsBanned = true;
            primary.BannedAt = duplicate.BannedAt;
            primary.BanReason = duplicate.BanReason;
        }
        primary.BirthDate ??= duplicate.BirthDate;
        primary.Gender ??= duplicate.Gender;
        primary.UpdatedAt = clock.UtcNow;
        duplicate.MergedIntoPatientId = primary.PatientId;
        duplicate.UpdatedAt = clock.UtcNow;
        audit.Add("PATIENTS_MERGED", "patient", primary.PatientId,
            new { duplicate_patient_id = duplicate.PatientId, duplicate.FileNumber }, new { moved_records = moved });
        await db.SaveChangesAsync(ct);
        if (tx is not null) await tx.CommitAsync(ct);
        return new MergePatientsResponse(primary.PatientId, moved);
    }

    private Task<int> MoveAsync(string table, Guid from, Guid to, CancellationToken ct) => table switch
    {
        // أسماء الجداول ثابتة (ليست من المستخدم)؛ كل جملة مكتوبة صراحة لأن أسماء الجداول لا تُمرر كمعاملات
        "bookings" => db.ExecuteAsync($"UPDATE bookings SET patient_id = {to} WHERE patient_id = {from}", ct),
        "queue_tickets" => db.ExecuteAsync($"UPDATE queue_tickets SET patient_id = {to} WHERE patient_id = {from}", ct),
        "invoices" => db.ExecuteAsync($"UPDATE invoices SET patient_id = {to} WHERE patient_id = {from}", ct),
        "patient_no_shows" => db.ExecuteAsync($"UPDATE patient_no_shows SET patient_id = {to} WHERE patient_id = {from}", ct),
        "patient_ban_events" => db.ExecuteAsync($"UPDATE patient_ban_events SET patient_id = {to} WHERE patient_id = {from}", ct),
        "patient_credit_ledger" => db.ExecuteAsync($"UPDATE patient_credit_ledger SET patient_id = {to} WHERE patient_id = {from}", ct),
        "consultations" => db.ExecuteAsync($"UPDATE consultations SET patient_id = {to} WHERE patient_id = {from}", ct),
        "prescriptions" => db.ExecuteAsync($"UPDATE prescriptions SET patient_id = {to} WHERE patient_id = {from}", ct),
        "medical_orders" => db.ExecuteAsync($"UPDATE medical_orders SET patient_id = {to} WHERE patient_id = {from}", ct),
        "online_payments" => db.ExecuteAsync($"UPDATE online_payments SET patient_id = {to} WHERE patient_id = {from}", ct),
        "payments" => db.ExecuteAsync($"UPDATE payments SET patient_id = {to} WHERE patient_id = {from}", ct),
        "receipts" => db.ExecuteAsync($"UPDATE receipts SET patient_id = {to} WHERE patient_id = {from}", ct),
        "deferrals" => db.ExecuteAsync($"UPDATE deferrals SET patient_id = {to} WHERE patient_id = {from}", ct),
        _ => throw new InvalidOperationException(table),
    };

    // ------------------------------------------------------------------ الحظر

    public async Task<BanStatusDto> BanStatusAsync(Guid patientId, CancellationToken ct)
    {
        var p = await LoadAsync(patientId, ct);
        var count = await bans.CountedNoShowsAsync(p.PatientId, ct);
        var deposit = await settings.GetLongAsync(SettingsReader.UnbanDepositAmount, current.BranchId, 10000, ct);
        return new BanStatusDto(p.IsBanned, p.IsBanned ? p.BanReason : null, count, p.IsBanned ? p.BannedAt : null, deposit);
    }

    public async Task<UnbanResponse> UnbanAsync(Guid patientId, UnbanRequest req, CancellationToken ct)
    {
        var p = await LoadAsync(patientId, ct);
        new Validator()
            .Check(req.Method is "cash" or "pos" or "waived", "method", "طريقة فك الحظر: cash أو pos أو waived")
            .ThrowIfInvalid();
        if (!p.IsBanned)
            throw new AppException(409, "PATIENT_NOT_BANNED", "المريض غير محظور");

        if (req.Method != "waived")
        {
            // فك الحظر بعربون نقدي أو POS: يدخل الدرج، ويصبح رصيداً يُخصم من أول فاتورة قادمة
            var required = await settings.GetLongAsync(SettingsReader.UnbanDepositAmount, current.BranchId, 10000, ct);
            new Validator()
                .Check(req.Amount >= required, "amount", $"العربون {required} على الأقل")
                .Check(req.Method != "pos" || !string.IsNullOrWhiteSpace(req.PosReference), "pos_reference", "رقم عملية POS مطلوب")
                .ThrowIfInvalid();
            await using var tx = await db.BeginTransactionAsync(ct);
            var tracked = await db.Patients.FirstAsync(x => x.PatientId == p.PatientId, ct);
            var deposit = await payments.CollectDepositAsync(tracked, req.Method!, req.Amount!.Value, req.PosReference, "unban_deposit", ct);
            await bans.UnbanAsync(tracked, req.Method!, req.Amount!.Value, req.Note?.Trim(), ct, deposit.Receipt.ReceiptId);
            await db.SaveChangesAsync(ct);
            if (tx is not null) await tx.CommitAsync(ct);
            return new UnbanResponse(false, deposit.CreditBalance, finance.Ref(deposit.Receipt), deposit.DrawerToken, deposit.OpenDrawer);
        }

        if (current.Role is not (Roles.Admin or Roles.BranchManager))
            throw AppException.Forbidden("فك الحظر بدون عربون للمدير فقط");
        new Validator().Check(!string.IsNullOrWhiteSpace(req.Note), "note", "سبب الإعفاء مطلوب").ThrowIfInvalid();

        await bans.UnbanAsync(p, "waived", 0, req.Note!.Trim(), ct);
        await db.SaveChangesAsync(ct);
        return new UnbanResponse(false, await finance.CreditBalanceAsync(p.PatientId, ct), null);
    }

    // ------------------------------------------------------------------ مساعدات

    private async Task<Patient> LoadAsync(Guid patientId, CancellationToken ct) =>
        await db.Patients.FirstOrDefaultAsync(p => p.PatientId == patientId, ct)
        ?? throw new AppException(404, "PATIENT_NOT_FOUND", "المريض غير موجود");

    private async Task<PatientDto> ToDtoAsync(Patient p, CancellationToken ct)
    {
        var credit = await db.PatientCredits.Where(c => c.PatientId == p.PatientId).SumAsync(c => (long?)c.Amount, ct) ?? 0;
        return new PatientDto(p.PatientId, p.FileNumber, p.FullName, p.PhoneNumber, p.BirthDate, p.ApproxAge, p.Gender,
            p.HomeBranchId, p.IsBanned, p.NoShowCount, credit, p.MergedIntoPatientId, p.CreatedAt, p.UpdatedAt);
    }

    private static object Snapshot(Patient p) =>
        new { p.FullName, p.PhoneNumber, p.BirthDate, p.ApproxAge, p.Gender };

    public static string NormalizeName(string name) =>
        string.Join(' ', name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static void ValidateFields(string? name, string? rawPhone, string? phone, DateOnly? birth, short? age, string? gender, bool required)
    {
        new Validator()
            .Check(!required || !string.IsNullOrWhiteSpace(name), "full_name", "الاسم مطلوب")
            .Check(name is null || (name.Trim().Length is >= 2 and <= 150), "full_name", "الاسم بين 2 و 150 حرفاً")
            .Check(string.IsNullOrWhiteSpace(rawPhone) || phone is not null, "phone_number", "رقم هاتف عراقي غير صالح (07XXXXXXXXX)")
            .Check(birth is null || (birth.Value.Year >= 1890 && birth.Value <= DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3))), "birth_date", "تاريخ ميلاد غير صالح")
            .Check(age is null || age is >= 0 and <= 130, "approx_age", "العمر بين 0 و 130")
            .Check(gender is null or "male" or "female", "gender", "الجنس: male أو female")
            .ThrowIfInvalid();
    }
}
