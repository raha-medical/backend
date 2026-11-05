using AlRahma.Application.Abstractions;
using AlRahma.Application.Common;
using AlRahma.Domain.Common;
using AlRahma.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AlRahma.Application.PatientApp;

public record MyPatientDto(Guid PatientId, string FullName, string FileNumber, string Relation, string? Gender, DateOnly? BirthDate, bool IsBanned);
public record CreateMyPatientRequest(Guid? PatientId, string? FullName, string? Gender, DateOnly? BirthDate, short? ApproxAge, string? Relation);
public record UpdateMyPatientRequest(string? Relation);

/// <summary>ملفات المرضى المربوطة بحساب تطبيق المريض (هو وأفراد عائلته).</summary>
public class PatientPortalService(IAppDbContext db, ICurrentUser current, IAuditLogger audit, IClock clock, NumberSequences numbers)
{
    public Guid AccountId => current.PatientAccountId ?? throw new AppException(401, "UNAUTHORIZED", "يلزم تسجيل الدخول");

    /// <summary>يتحقق أن المريض مربوط بحساب المريض الحالي، وإلا 404 PATIENT_NOT_FOUND.</summary>
    public async Task EnsureLinkedAsync(Guid patientId, CancellationToken ct)
    {
        var account = AccountId;
        if (!await db.PatientAccountLinks.AnyAsync(l => l.PatientAccountId == account && l.PatientId == patientId, ct))
            throw new AppException(404, "PATIENT_NOT_FOUND", "المريض غير موجود");
    }

    public IQueryable<Guid> LinkedPatientIds() =>
        db.PatientAccountLinks.Where(l => l.PatientAccountId == AccountId).Select(l => l.PatientId);

    public async Task<List<MyPatientDto>> MyPatientsAsync(Guid accountId, CancellationToken ct) =>
        await (from l in db.PatientAccountLinks.AsNoTracking()
               join p in db.Patients.AsNoTracking() on l.PatientId equals p.PatientId
               where l.PatientAccountId == accountId && p.MergedIntoPatientId == null
               orderby l.Relation == PatientRelations.Self descending, p.CreatedAt
               select new MyPatientDto(p.PatientId, p.FullName, p.FileNumber, l.Relation, p.Gender, p.BirthDate, p.IsBanned)).ToListAsync(ct);

    public Task<List<MyPatientDto>> ListAsync(CancellationToken ct) => MyPatientsAsync(AccountId, ct);

    public async Task<(MyPatientDto Patient, bool Created)> CreateAsync(CreateMyPatientRequest req, CancellationToken ct)
    {
        var account = await db.PatientAccounts.FirstAsync(a => a.PatientAccountId == AccountId, ct);
        var today = clock.BaghdadToday();
        new Validator()
            .Check(!string.IsNullOrWhiteSpace(req.FullName) && req.FullName.Trim().Length is >= 3 and <= 150, "full_name", "الاسم الكامل مطلوب (3-150 حرفاً)")
            .Check(req.Gender is null or "male" or "female", "gender", "male | female")
            .Check(req.BirthDate is not null || req.ApproxAge is not null, "birth_date", "تاريخ الميلاد أو العمر التقريبي مطلوب")
            .Check(req.BirthDate is null || req.BirthDate <= today, "birth_date", "تاريخ الميلاد في المستقبل")
            .Check(req.ApproxAge is null or >= 0 and <= 130, "approx_age", "العمر من 0 إلى 130")
            .Check(req.Relation is null || PatientRelations.All.Contains(req.Relation), "relation", string.Join(" | ", PatientRelations.All))
            .ThrowIfInvalid();
        var name = string.Join(' ', req.FullName!.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var relation = req.Relation ?? PatientRelations.Self;

        // ملف بنفس الاسم والرقم: يُربط بدل إنشاء ملف مكرر
        var existing = await db.Patients.FirstOrDefaultAsync(p => p.PhoneNumber == account.PhoneNumber && p.FullName == name && p.MergedIntoPatientId == null, ct);
        if (existing is not null)
        {
            if (!await db.PatientAccountLinks.AnyAsync(l => l.PatientAccountId == account.PatientAccountId && l.PatientId == existing.PatientId, ct))
            {
                db.PatientAccountLinks.Add(new PatientAccountLink { PatientAccountId = account.PatientAccountId, PatientId = existing.PatientId, Relation = relation });
                await db.SaveChangesAsync(ct);
            }
            return ((await ListAsync(ct)).First(p => p.PatientId == existing.PatientId), false);
        }

        var id = req.PatientId ?? UuidV7.New();
        if (await db.Patients.AnyAsync(p => p.PatientId == id, ct)) throw new AppException(409, "PATIENT_EXISTS", "يوجد مريض بنفس المعرف");
        // رقم الملف من الفرع الرئيسي (أول فرع فعال بالرمز)؛ الملف صالح في كل الفروع
        var branchId = await db.Branches.Where(b => b.IsActive).OrderBy(b => b.Code).Select(b => b.BranchId).FirstAsync(ct);
        var now = clock.UtcNow;
        var patient = new Patient
        {
            PatientId = id,
            FileNumber = $"{await numbers.BranchCodeAsync(branchId, ct)}-{await numbers.NextAsync(branchId, "patient_file", "all", ct):D6}",
            FullName = name,
            PhoneNumber = account.PhoneNumber,
            BirthDate = req.BirthDate,
            ApproxAge = req.BirthDate is null ? req.ApproxAge : null,
            Gender = req.Gender,
            HomeBranchId = branchId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Patients.Add(patient);
        db.PatientAccountLinks.Add(new PatientAccountLink { PatientAccountId = account.PatientAccountId, PatientId = id, Relation = relation });
        audit.Add("PATIENT_CREATED_BY_APP", "patient", id, after: new { patient.FileNumber, account_id = account.PatientAccountId, relation });
        await db.SaveChangesAsync(ct);
        return ((await ListAsync(ct)).First(p => p.PatientId == id), true);
    }

    public async Task<MyPatientDto> UpdateAsync(Guid patientId, UpdateMyPatientRequest req, CancellationToken ct)
    {
        new Validator().Check(req.Relation is not null && PatientRelations.All.Contains(req.Relation), "relation", string.Join(" | ", PatientRelations.All))
            .ThrowIfInvalid();
        var account = AccountId;
        var link = await db.PatientAccountLinks.FirstOrDefaultAsync(l => l.PatientAccountId == account && l.PatientId == patientId, ct)
                   ?? throw new AppException(404, "PATIENT_NOT_FOUND", "المريض غير موجود");
        link.Relation = req.Relation!;
        await db.SaveChangesAsync(ct);
        return (await ListAsync(ct)).First(p => p.PatientId == patientId);
    }

    /// <summary>عمر المريض بالسنوات من تاريخ الميلاد أو العمر التقريبي.</summary>
    public static int? AgeOf(Patient p, DateOnly today)
    {
        if (p.BirthDate is { } b)
        {
            var age = today.Year - b.Year;
            if (b > today.AddYears(-age)) age--;
            return Math.Max(0, age);
        }
        return p.ApproxAge;
    }
}
