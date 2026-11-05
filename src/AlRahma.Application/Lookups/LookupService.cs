using System.Text.RegularExpressions;
using AlRahma.Application.Abstractions;
using AlRahma.Application.Common;
using AlRahma.Domain.Common;
using AlRahma.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AlRahma.Application.Lookups;

public record LookupDto(Guid LookupId, string Category, string Code, string NameAr, int SortOrder, bool IsActive, bool IsSystem);
public record LookupCategoryDto(string Category, string NameAr, int ActiveCount);
public record CreateLookupRequest(string? Code, string? NameAr, int? SortOrder);
public record UpdateLookupRequest(string? NameAr, int? SortOrder, bool? IsActive);

/// <summary>
/// قوائم الاختيار التي يديرها المدير العام: أسباب الإغلاق المؤقت، اختصاصات الأطباء، العناوين الوظيفية.
/// الواجهات تعرضها كقوائم منسدلة، والمدير يضيف ويعدّل ويوقف القيم متى شاء.
/// </summary>
public partial class LookupService(IAppDbContext db, ICurrentUser current, IAuditLogger audit, IClock clock)
{
    [GeneratedRegex("^[a-z][a-z0-9_]{1,49}$")]
    private static partial Regex CodeRegex();

    public static void EnsureCategory(string? category)
    {
        if (category is null || !LookupCategories.Arabic.ContainsKey(category))
            throw AppException.Validation(new Dictionary<string, string[]>
            {
                ["category"] = [string.Join(" | ", LookupCategories.Arabic.Keys)],
            });
    }

    public async Task<IReadOnlyList<LookupCategoryDto>> CategoriesAsync(CancellationToken ct)
    {
        var counts = await db.LookupValues.AsNoTracking().Where(l => l.IsActive)
            .GroupBy(l => l.Category).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        return LookupCategories.Arabic
            .Select(c => new LookupCategoryDto(c.Key, c.Value, counts.FirstOrDefault(x => x.Key == c.Key)?.Count ?? 0))
            .ToList();
    }

    public async Task<IReadOnlyList<LookupDto>> ListAsync(string? category, bool includeInactive, CancellationToken ct)
    {
        EnsureCategory(category);
        if (includeInactive && current.Role != Roles.Admin) includeInactive = false;
        var list = await db.LookupValues.AsNoTracking()
            .Where(l => l.Category == category && (includeInactive || l.IsActive))
            .OrderBy(l => l.SortOrder).ThenBy(l => l.NameAr).ToListAsync(ct);
        return list.Select(ToDto).ToList();
    }

    public async Task<LookupDto> CreateAsync(string category, CreateLookupRequest req, CancellationToken ct)
    {
        EnsureAdmin();
        EnsureCategory(category);
        var name = req.NameAr?.Trim();
        var code = string.IsNullOrWhiteSpace(req.Code) ? null : req.Code.Trim().ToLowerInvariant();
        new Validator()
            .Check(!string.IsNullOrWhiteSpace(name) && name.Length <= 100, "name_ar", "الاسم مطلوب (100 حرف كحد أقصى)")
            .Check(code is null || CodeRegex().IsMatch(code), "code", "حروف إنجليزية صغيرة وأرقام و _ فقط")
            .ThrowIfInvalid();
        code ??= "c_" + Guid.NewGuid().ToString("N")[..10];

        if (await db.LookupValues.AnyAsync(l => l.Category == category && (l.Code == code || l.NameAr == name), ct))
            throw new AppException(409, "DUPLICATE_LOOKUP", "القيمة موجودة مسبقاً في هذه القائمة",
                "إذا كانت موقوفة فعّلها بدل إضافتها من جديد");

        var now = clock.UtcNow;
        var sort = req.SortOrder ?? (await db.LookupValues.Where(l => l.Category == category && !l.IsSystem)
            .Select(l => (int?)l.SortOrder).MaxAsync(ct) ?? 0) + 10;
        var item = new LookupValue
        {
            LookupId = UuidV7.New(),
            Category = category,
            Code = code,
            NameAr = name!,
            SortOrder = sort,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.LookupValues.Add(item);
        audit.Add("LOOKUP_CREATED", "lookup", item.LookupId, after: ToDto(item));
        await db.SaveChangesAsync(ct);
        return ToDto(item);
    }

    /// <summary>تعديل الاسم أو الترتيب أو الإيقاف. تغيير اسم اختصاص أو عنوان وظيفي يُحدّث الأطباء المرتبطين به.</summary>
    public async Task<LookupDto> UpdateAsync(Guid lookupId, UpdateLookupRequest req, CancellationToken ct)
    {
        EnsureAdmin();
        var item = await db.LookupValues.FirstOrDefaultAsync(l => l.LookupId == lookupId, ct)
                   ?? throw AppException.NotFound("القيمة غير موجودة");
        var name = req.NameAr?.Trim();
        new Validator()
            .Check(req.NameAr is null || (!string.IsNullOrWhiteSpace(name) && name.Length <= 100), "name_ar", "الاسم مطلوب (100 حرف كحد أقصى)")
            .ThrowIfInvalid();
        if (req.IsActive == false && item.IsSystem)
            throw new AppException(409, "LOOKUP_IS_SYSTEM", "لا يمكن إيقاف هذه القيمة", "يعتمد عليها النظام؛ يمكنك تعديل اسمها فقط");

        var before = ToDto(item);
        var oldName = item.NameAr;
        if (name is not null && name != item.NameAr)
        {
            if (await db.LookupValues.AnyAsync(l => l.Category == item.Category && l.NameAr == name && l.LookupId != item.LookupId, ct))
                throw new AppException(409, "DUPLICATE_LOOKUP", "القيمة موجودة مسبقاً في هذه القائمة");
            item.NameAr = name;
        }
        if (req.SortOrder is { } sort) item.SortOrder = sort;
        if (req.IsActive is { } active) item.IsActive = active;
        item.UpdatedAt = clock.UtcNow;

        var renamed = 0;
        if (item.NameAr != oldName)
        {
            if (item.Category == LookupCategories.DoctorSpecialty)
                renamed = await db.ExecuteAsync($"UPDATE doctors SET specialty = {item.NameAr} WHERE specialty = {oldName}", ct);
            else if (item.Category == LookupCategories.DoctorJobTitle)
                renamed = await db.ExecuteAsync($"UPDATE doctors SET job_title = {item.NameAr} WHERE job_title = {oldName}", ct);
        }
        audit.Add("LOOKUP_UPDATED", "lookup", item.LookupId, before, new { after = ToDto(item), updated_doctors = renamed });
        await db.SaveChangesAsync(ct);
        return ToDto(item);
    }

    /// <summary>أسماء كل قيم القائمة (الفعالة والموقوفة) حسب الرمز، لعرض السجلات القديمة.</summary>
    public async Task<Dictionary<string, string>> NamesAsync(string category, CancellationToken ct) =>
        await db.LookupValues.AsNoTracking().Where(l => l.Category == category)
            .ToDictionaryAsync(l => l.Code, l => l.NameAr, ct);

    /// <summary>
    /// يتحقق أن القيمة المرسلة (رمزاً أو اسماً) موجودة وفعالة، ويعيد اسمها المعتمد. null إذا كانت غير صالحة.
    /// </summary>
    public async Task<LookupValue?> ResolveActiveAsync(string category, string? value, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim();
        return await db.LookupValues.AsNoTracking()
            .FirstOrDefaultAsync(l => l.Category == category && l.IsActive && (l.Code == v || l.NameAr == v), ct);
    }

    private void EnsureAdmin()
    {
        if (current.Role != Roles.Admin) throw AppException.Forbidden("إدارة القوائم للمدير العام");
    }

    private static LookupDto ToDto(LookupValue l) => new(l.LookupId, l.Category, l.Code, l.NameAr, l.SortOrder, l.IsActive, l.IsSystem);
}
