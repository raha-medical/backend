using AlRahma.Application.Abstractions;
using AlRahma.Application.Common;
using AlRahma.Domain.Common;
using AlRahma.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AlRahma.Application.Services;

public record ServiceDto(
    Guid ServiceId, string Name, string Type, string? VisitKind, string? Modality,
    Guid? ClinicId, long Price, bool IsCommissionable, bool IsActive, DateTimeOffset UpdatedAt)
{
    /// <summary>IQD | USD (الترحيل 023). سعر خدمة USD بالدولار.</summary>
    public string Currency { get; init; } = "IQD";
    /// <summary>دواء يحتاج وصفة (الترحيل 022).</summary>
    public bool RequiresPrescription { get; init; }
}

/// <summary>currency: IQD (افتراضي) | USD (السعر بالدولار، لغير الكشفيات). requires_prescription للأدوية.</summary>
public record CreateServiceRequest(
    string? Name, string? Type, string? VisitKind, string? Modality,
    Guid? ClinicId, long? Price, bool? IsCommissionable, string? Currency = null, bool? RequiresPrescription = null);

/// <summary>PATCH: النوع ونوع الكشفية لا يتغيران بعد الإنشاء (أنشئ خدمة جديدة بدلاً من ذلك).</summary>
public record UpdateServiceRequest(string? Name, Guid? ClinicId, long? Price, bool? IsCommissionable, bool? IsActive, string? Reason,
    string? Currency = null, bool? RequiresPrescription = null);

public record ServiceListQuery(string? Type, Guid? ClinicId, string? Q, bool IncludeInactive);

/// <summary>الخدمات والأسعار لكل فرع (الفرع من التوكن). الأسعار تُقرأ من هنا فقط، ولا تُرسل من الواجهة أبداً.</summary>
public class ServiceCatalog(IAppDbContext db, ICurrentUser current, IAuditLogger audit, IClock clock)
{
    public const long MaxPrice = 100_000_000; // 100 مليون دينار حد معقول لأي خدمة

    public async Task<IReadOnlyList<ServiceDto>> ListAsync(ServiceListQuery q, CancellationToken ct)
    {
        // خدمة الرسوم الداخلية (رسوم إلغاء متأخر) لا تظهر في الكتالوج
        var query = db.Services.Where(s => s.BranchId == current.BranchId && s.Type != ServiceTypes.Fee);
        if (!(q.IncludeInactive && current.Has(Permissions.ServicesView))) query = query.Where(s => s.IsActive);
        if (!string.IsNullOrWhiteSpace(q.Type)) query = query.Where(s => s.Type == q.Type);
        if (q.ClinicId is { } cid) query = query.Where(s => s.ClinicId == cid);
        if (!string.IsNullOrWhiteSpace(q.Q)) query = query.Where(s => EF.Functions.ILike(s.Name, $"%{q.Q.Trim()}%"));

        var items = await query.OrderBy(s => s.Type).ThenBy(s => s.Name).ToListAsync(ct);
        return items.Select(ToDto).ToList();
    }

    public async Task<ServiceDto> GetAsync(Guid id, CancellationToken ct) => ToDto(await LoadAsync(id, ct));

    public async Task<ServiceDto> CreateAsync(CreateServiceRequest req, CancellationToken ct)
    {
        var type = req.Type ?? "";
        var v = new Validator()
            .Check(!string.IsNullOrWhiteSpace(req.Name) && req.Name.Trim().Length <= 150, "name", "اسم الخدمة مطلوب (150 حرفاً كحد أقصى)")
            .Check(ServiceTypes.All.Contains(type), "type", "النوع غير صالح")
            .Check(req.Price is >= 0 and <= MaxPrice, "price", "السعر مطلوب، عدد صحيح بالدينار بين 0 و 100,000,000")
            .Check(type == ServiceTypes.Consultation ? VisitKinds.All.Contains(req.VisitKind) : req.VisitKind is null,
                "visit_kind", "مطلوب للكشفية فقط: new_visit أو follow_up")
            .Check(req.Modality is null || (type == ServiceTypes.Radiology && Modalities.All.Contains(req.Modality)),
                "modality", "للأشعة فقط: xray أو ultrasound أو ct أو mri")
            .Check(type != ServiceTypes.Radiology || req.Modality is not null, "modality", "نوع الجهاز مطلوب لخدمات الأشعة")
            .Check(type != ServiceTypes.Consultation || req.ClinicId is not null, "clinic_id", "العيادة مطلوبة للكشفية")
            .Check(req.Currency is null or "IQD" or "USD", "currency", "IQD | USD")
            .Check(req.Currency != "USD" || type != ServiceTypes.Consultation, "currency", "الكشفيات بالدينار فقط");
        v.ThrowIfInvalid();

        if (req.ClinicId is { } clinicId) await EnsureClinicFitsAsync(clinicId, type, ct);

        if (type == ServiceTypes.Consultation &&
            await db.Services.AnyAsync(s => s.BranchId == current.BranchId && s.IsActive && s.ClinicId == req.ClinicId
                                            && s.Type == ServiceTypes.Consultation && s.VisitKind == req.VisitKind, ct))
            throw new AppException(409, "DUPLICATE_SERVICE", "توجد كشفية فعالة من نفس النوع لهذه العيادة",
                "عدّل سعر الكشفية الموجودة بدلاً من إنشاء واحدة جديدة");

        var now = clock.UtcNow;
        var service = new Service
        {
            ServiceId = UuidV7.New(),
            BranchId = current.BranchId,
            ClinicId = req.ClinicId,
            Name = req.Name!.Trim(),
            Type = type,
            VisitKind = req.VisitKind,
            Modality = req.Modality,
            Price = req.Price!.Value,
            IsCommissionable = req.IsCommissionable ?? type is ServiceTypes.Consultation or ServiceTypes.Procedure,
            Currency = req.Currency ?? "IQD",
            RequiresPrescription = type == ServiceTypes.Pharmacy && req.RequiresPrescription == true,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Services.Add(service);
        audit.Add("SERVICE_CREATED", "service", service.ServiceId, after: ToDto(service));
        await db.SaveChangesAsync(ct);
        return ToDto(service);
    }

    public async Task<ServiceDto> UpdateAsync(Guid id, UpdateServiceRequest req, CancellationToken ct)
    {
        var service = await LoadAsync(id, ct);
        if (req.IsActive == false && service.IsActive && !current.Has(Permissions.ServicesDelete))
            throw AppException.Forbidden("إيقاف الخدمة يتطلب صلاحية services.delete");
        new Validator()
            .Check(req.Name is null || (!string.IsNullOrWhiteSpace(req.Name) && req.Name.Trim().Length <= 150), "name", "الاسم لا يكون فارغاً")
            .Check(req.Price is null or >= 0 and <= MaxPrice, "price", "السعر عدد صحيح بالدينار بين 0 و 100,000,000")
            .Check(req.ClinicId is not null || service.Type != ServiceTypes.Consultation || service.ClinicId is not null, "clinic_id", "العيادة مطلوبة للكشفية")
            .ThrowIfInvalid();

        if (req.ClinicId is { } clinicId && clinicId != service.ClinicId)
            await EnsureClinicFitsAsync(clinicId, service.Type, ct);

        var before = ToDto(service);
        if (req.Name is not null) service.Name = req.Name.Trim();
        if (req.ClinicId is not null) service.ClinicId = req.ClinicId;
        if (req.Price is { } price) service.Price = price;
        if (req.IsCommissionable is { } c) service.IsCommissionable = c;
        if (req.Currency is not null)
        {
            new Validator().Check(req.Currency is "IQD" or "USD", "currency", "IQD | USD")
                .Check(req.Currency == "IQD" || service.Type != ServiceTypes.Consultation, "currency", "الكشفيات بالدينار فقط").ThrowIfInvalid();
            service.Currency = req.Currency;
        }
        if (req.RequiresPrescription is { } rx) service.RequiresPrescription = service.Type == ServiceTypes.Pharmacy && rx;
        if (req.IsActive is { } active)
        {
            if (active && !service.IsActive && service.Type == ServiceTypes.Consultation &&
                await db.Services.AnyAsync(s => s.ServiceId != service.ServiceId && s.BranchId == service.BranchId && s.IsActive
                                                && s.ClinicId == service.ClinicId && s.Type == ServiceTypes.Consultation
                                                && s.VisitKind == service.VisitKind, ct))
                throw new AppException(409, "DUPLICATE_SERVICE", "توجد كشفية فعالة من نفس النوع لهذه العيادة");
            service.IsActive = active;
        }
        service.UpdatedAt = clock.UtcNow;

        var after = ToDto(service);
        var action = before.Price != after.Price ? "PRICE_CHANGED" : "SERVICE_UPDATED";
        audit.Add(action, "service", service.ServiceId, before, after, req.Reason);
        await db.SaveChangesAsync(ct);
        return after;
    }

    private async Task<Service> LoadAsync(Guid id, CancellationToken ct) =>
        await db.Services.FirstOrDefaultAsync(s => s.ServiceId == id && s.BranchId == current.BranchId && s.Type != ServiceTypes.Fee, ct)
        ?? throw AppException.NotFound("الخدمة غير موجودة في هذا الفرع");

    private async Task EnsureClinicFitsAsync(Guid clinicId, string type, CancellationToken ct)
    {
        var clinic = await db.Clinics.FirstOrDefaultAsync(c => c.ClinicId == clinicId && c.BranchId == current.BranchId && c.IsActive, ct);
        var expected = type switch
        {
            ServiceTypes.Consultation or ServiceTypes.Procedure => "clinic",
            ServiceTypes.Lab => "lab",
            ServiceTypes.Radiology => "radiology",
            _ => "pharmacy",
        };
        if (clinic is null || clinic.Department != expected)
            throw AppException.Validation(new Dictionary<string, string[]>
            {
                ["clinic_id"] = [$"العيادة غير موجودة في هذا الفرع أو ليست من قسم {expected}"],
            });
    }

    private static ServiceDto ToDto(Service s) => new(
        s.ServiceId, s.Name, s.Type, s.VisitKind, s.Modality, s.ClinicId, s.Price, s.IsCommissionable, s.IsActive, s.UpdatedAt)
    {
        Currency = s.Currency, RequiresPrescription = s.RequiresPrescription,
    };
}
