using AlRahma.Application.Abstractions;
using AlRahma.Application.Common;
using AlRahma.Domain.Common;
using AlRahma.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AlRahma.Application.Files;

/// <summary>external_result: ticket_id (دور المراجع). prescription: patient_id (صورة وصفة خارجية للصيدلية).</summary>
public record UploadUrlRequest(string? FileName, string? ContentType, long? SizeBytes, string? Purpose, Guid? OrderItemId, Guid? DoctorId,
    Guid? TicketId = null, Guid? PatientId = null);
public record FileInfoDto(Guid FileId, string Purpose, string FileName, string ContentType, long SizeBytes, Guid? TicketId, Guid? PatientId,
    string? UploadedByName, DateTimeOffset CreatedAt, string DownloadUrl);
public record UploadUrlResponse(Guid FileId, string UploadUrl, string Method, IDictionary<string, string> Headers, int ExpiresIn);
public record FileStatusResponse(Guid FileId, string Status);
public record DownloadUrlResponse(string Url, int ExpiresIn);

/// <summary>
/// رفع الملفات إلى MinIO بروابط موقعة: الملف يذهب من المتصفح إلى MinIO مباشرة ولا يمر على الـ API.
/// صور الأطباء، ومرفقات نتائج المختبر وتقارير الأشعة.
/// </summary>
public class FileService(IAppDbContext db, ICurrentUser current, IObjectStorage storage, IClock clock)
{
    public const long DoctorPhotoMaxBytes = 2 * 1024 * 1024;
    public static readonly TimeSpan UploadExpiry = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan DownloadExpiry = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan PhotoUrlExpiry = TimeSpan.FromHours(1);

    public const long OrderFileMaxBytes = 200L * 1024 * 1024;

    private static readonly Dictionary<string, string> OrderExtensions = new()
    {
        ["image/jpeg"] = "jpg",
        ["image/png"] = "png",
        ["application/pdf"] = "pdf",
        ["application/dicom"] = "dcm",
    };

    private static readonly Dictionary<string, string> ImageExtensions = new()
    {
        ["image/jpeg"] = "jpg",
        ["image/png"] = "png",
    };

    public async Task<UploadUrlResponse> CreateUploadUrlAsync(UploadUrlRequest req, CancellationToken ct)
    {
        new Validator()
            .Check(!string.IsNullOrWhiteSpace(req.FileName) && req.FileName.Length <= 255, "file_name", "اسم الملف مطلوب (255 حرفاً كحد أقصى)")
            .Check(!string.IsNullOrWhiteSpace(req.ContentType), "content_type", "نوع الملف مطلوب")
            .Check(req.SizeBytes is > 0, "size_bytes", "حجم الملف مطلوب")
            .Check(!string.IsNullOrWhiteSpace(req.Purpose), "purpose", "الغرض مطلوب")
            .ThrowIfInvalid();

        if (req.Purpose is FilePurposes.Lab or FilePurposes.Radiology) return await CreateOrderUploadAsync(req, ct);
        if (req.Purpose is FilePurposes.ExternalResult or FilePurposes.Prescription) return await CreatePatientUploadAsync(req, ct);
        if (req.Purpose != FilePurposes.DoctorPhoto)
            throw new AppException(422, "INVALID_FILE_PURPOSE", "نوع الملف غير مدعوم", "lab | radiology | doctor_photo | external_result | prescription");

        new Validator()
            .Check(req.DoctorId is not null, "doctor_id", "الطبيب مطلوب لصورة الطبيب")
            .Check(req.ContentType is not null && ImageExtensions.ContainsKey(req.ContentType), "content_type", "الصورة يجب أن تكون JPG أو PNG")
            .Check(req.SizeBytes <= DoctorPhotoMaxBytes, "size_bytes", "حجم الصورة 2MB كحد أقصى")
            .ThrowIfInvalid();

        var doctor = await LoadDoctorForPhotoAsync(req.DoctorId!.Value, ct);

        var fileId = UuidV7.New();
        var key = $"doctors/{doctor.DoctorId:N}/{fileId:N}.{ImageExtensions[req.ContentType!]}";
        db.Files.Add(new StoredFile
        {
            FileId = fileId,
            BranchId = current.BranchId,
            Bucket = storage.Bucket,
            ObjectKey = key,
            FileName = req.FileName!.Trim(),
            ContentType = req.ContentType!,
            SizeBytes = req.SizeBytes!.Value,
            Purpose = FilePurposes.DoctorPhoto,
            Status = FileStatuses.Pending,
            UploadedBy = current.UserId,
            CreatedAt = clock.UtcNow,
        });
        await db.SaveChangesAsync(ct);

        return new UploadUrlResponse(fileId, storage.PresignUpload(key, req.ContentType!, UploadExpiry), "PUT",
            new Dictionary<string, string> { ["Content-Type"] = req.ContentType! }, (int)UploadExpiry.TotalSeconds);
    }

    /// <summary>ملفات نتائج المختبر والأشعة: لفني القسم في فرعه، لطلب مدفوع أو مستثنى.</summary>
    private async Task<UploadUrlResponse> CreateOrderUploadAsync(UploadUrlRequest req, CancellationToken ct)
    {
        var lab = req.Purpose == FilePurposes.Lab;
        if (!current.Has(lab ? Permissions.LabWrite : Permissions.RadiologyWrite))
            throw AppException.Forbidden(lab ? "ملفات المختبر لفني المختبر" : "ملفات الأشعة لأخصائي الأشعة");
        new Validator()
            .Check(req.OrderItemId is not null, "order_item_id", "الطلب مطلوب")
            .Check(req.ContentType is not null && OrderExtensions.ContainsKey(req.ContentType), "content_type", string.Join(" | ", OrderExtensions.Keys))
            .Check(req.SizeBytes <= OrderFileMaxBytes, "size_bytes", "200MB كحد أقصى")
            .ThrowIfInvalid();
        var department = lab ? OrderDepartments.Lab : OrderDepartments.Radiology;
        var order = await db.MedicalOrders.AsNoTracking()
                        .FirstOrDefaultAsync(o => o.OrderItemId == req.OrderItemId && o.Department == department && o.BranchId == current.BranchId, ct)
                    ?? throw new AppException(404, "ORDER_NOT_FOUND", "الطلب غير موجود");
        if (order.Status is OrderStatuses.Ordered)
            throw new AppException(402, "INVOICE_UNPAID", "الطلب غير مدفوع");
        if (order.Status is OrderStatuses.Cancelled)
            throw new AppException(409, "INVALID_ORDER_STATE", "الطلب ملغى");

        var fileId = UuidV7.New();
        var key = $"{(lab ? "lab" : "rad")}/{order.OrderItemId:N}/{fileId:N}.{OrderExtensions[req.ContentType!]}";
        db.Files.Add(new StoredFile
        {
            FileId = fileId, BranchId = current.BranchId, Bucket = storage.Bucket, ObjectKey = key, FileName = req.FileName!.Trim(),
            ContentType = req.ContentType!, SizeBytes = req.SizeBytes!.Value, Purpose = req.Purpose!, OrderItemId = order.OrderItemId,
            Status = FileStatuses.Pending, UploadedBy = current.UserId, CreatedAt = clock.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        return new UploadUrlResponse(fileId, storage.PresignUpload(key, req.ContentType!, UploadExpiry), "PUT",
            new Dictionary<string, string> { ["Content-Type"] = req.ContentType! }, (int)UploadExpiry.TotalSeconds);
    }

    public const long PatientFileMaxBytes = 20L * 1024 * 1024;

    /// <summary>
    /// صورة نتيجة خارجية (1.2-ج3): يرفعها الاستقبال (queue.manage) على دور المراجع في فرعه، ويراها الطبيب في المعاينة.
    /// صورة وصفة خارجية (6.3-ب): يرفعها الصيدلي (pharmacy.dispense) لمراجع، وتُرفق بالصرف للأدوية التي تحتاج وصفة.
    /// JPG أو PNG أو PDF، 20MB كحد أقصى.
    /// </summary>
    private async Task<UploadUrlResponse> CreatePatientUploadAsync(UploadUrlRequest req, CancellationToken ct)
    {
        var external = req.Purpose == FilePurposes.ExternalResult;
        if (!current.Has(external ? Permissions.QueueManage : Permissions.PharmacyDispense))
            throw AppException.Forbidden(external ? "صورة النتيجة الخارجية يرفعها الاستقبال" : "صورة الوصفة يرفعها الصيدلي");
        new Validator()
            .Check(!external || req.TicketId is not null, "ticket_id", "الدور مطلوب")

            .Check(req.ContentType is "image/jpeg" or "image/png" or "application/pdf", "content_type", "image/jpeg | image/png | application/pdf")
            .Check(req.SizeBytes <= PatientFileMaxBytes, "size_bytes", "20MB كحد أقصى")
            .ThrowIfInvalid();
        Guid? ticketId = null;
        Guid? patientId = null;
        if (external)
        {
            var ticket = await db.QueueTickets.AsNoTracking().FirstOrDefaultAsync(t => t.TicketId == req.TicketId && t.BranchId == current.BranchId, ct)
                         ?? throw new AppException(404, "TICKET_NOT_FOUND", "الدور غير موجود في هذا الفرع");
            ticketId = ticket.TicketId;
            patientId = ticket.PatientId;
        }
        else if (req.PatientId is not null)
        {
            // صورة الوصفة قد تُرفع قبل إنشاء ملف المراجع الخارجي؛ تُربط به عند الصرف
            patientId = await db.Patients.AsNoTracking().Where(p => p.PatientId == req.PatientId).Select(p => (Guid?)p.PatientId).FirstOrDefaultAsync(ct)
                        ?? throw new AppException(404, "PATIENT_NOT_FOUND", "المريض غير موجود");
        }
        var fileId = UuidV7.New();
        var folder = external ? "external" : "rx";
        var owner = patientId is { } owned ? owned.ToString("N") : "unassigned";
        var key = $"{folder}/{owner}/{fileId:N}.{OrderExtensions[req.ContentType!]}";
        db.Files.Add(new StoredFile
        {
            FileId = fileId, BranchId = current.BranchId, Bucket = storage.Bucket, ObjectKey = key, FileName = req.FileName!.Trim(),
            ContentType = req.ContentType!, SizeBytes = req.SizeBytes!.Value, Purpose = req.Purpose!, TicketId = ticketId, PatientId = patientId,
            Status = FileStatuses.Pending, UploadedBy = current.UserId, CreatedAt = clock.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        return new UploadUrlResponse(fileId, storage.PresignUpload(key, req.ContentType!, UploadExpiry), "PUT",
            new Dictionary<string, string> { ["Content-Type"] = req.ContentType! }, (int)UploadExpiry.TotalSeconds);
    }

    /// <summary>
    /// ملفات المراجع المرفوعة (النتائج الخارجية وصور الوصفات) مع روابط تنزيل مؤقتة. للطبيب والاستقبال والصيدلي في الفرع.
    /// ticket_id: نتائج دور (ونتائج الزيارة الأصلية إن كان الدور عودة). patient_id: كل ملفات المراجع في الفرع.
    /// </summary>
    public async Task<List<FileInfoDto>> ListPatientFilesAsync(Guid? ticketId, Guid? patientId, string? purpose, CancellationToken ct)
    {
        if (!(current.Has(Permissions.QueueManage) || current.Has(Permissions.ClinicalWrite) || current.Has(Permissions.PharmacyDispense)))
            throw AppException.Forbidden();
        new Validator()
            .Check(ticketId is not null || patientId is not null, "ticket_id", "الدور أو المراجع مطلوب")
            .Check(purpose is null or FilePurposes.ExternalResult or FilePurposes.Prescription, "purpose", "external_result | prescription")
            .ThrowIfInvalid();
        var q = db.Files.AsNoTracking().Where(f => f.BranchId == current.BranchId && f.Status == FileStatuses.Stored
            && (f.Purpose == FilePurposes.ExternalResult || f.Purpose == FilePurposes.Prescription));
        if (purpose is not null) q = q.Where(f => f.Purpose == purpose);
        if (ticketId is { } tid)
        {
            var original = await db.QueueTickets.AsNoTracking().Where(t => t.TicketId == tid).Select(t => t.OriginalTicketId).FirstOrDefaultAsync(ct);
            q = q.Where(f => f.TicketId == tid || (original != null && f.TicketId == original));
        }
        if (patientId is { } pid) q = q.Where(f => f.PatientId == pid);
        var rows = await q.OrderByDescending(f => f.CreatedAt).Take(50).ToListAsync(ct);
        var users = rows.Where(f => f.UploadedBy != null).Select(f => f.UploadedBy!.Value).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => users.Contains(u.UserId)).ToDictionaryAsync(u => u.UserId, u => u.FullName, ct);
        return rows.Select(f => new FileInfoDto(f.FileId, f.Purpose, f.FileName, f.ContentType, f.SizeBytes, f.TicketId, f.PatientId,
            f.UploadedBy is { } u ? names.GetValueOrDefault(u) : null, f.CreatedAt, storage.PresignDownload(f.ObjectKey, DownloadExpiry))).ToList();
    }

    /// <summary>تأكيد الرفع: يتحقق السيرفر من وجود الملف في MinIO ومن حجمه.</summary>
    public async Task<FileStatusResponse> CompleteAsync(Guid fileId, CancellationToken ct)
    {
        var file = await LoadFileAsync(fileId, ct);
        if (file.UploadedBy != current.UserId && !current.Has(Permissions.ServicesManage))
            throw AppException.Forbidden();
        if (file.Status == FileStatuses.Stored) return new FileStatusResponse(file.FileId, file.Status);

        var size = await storage.GetSizeAsync(file.ObjectKey, ct)
                   ?? throw new AppException(422, "FILE_NOT_UPLOADED", "لم يصل الملف إلى التخزين",
                       "ارفع الملف على الرابط أولاً ثم أكّد");
        var limit = file.Purpose switch
        {
            FilePurposes.DoctorPhoto => DoctorPhotoMaxBytes,
            FilePurposes.ExternalResult or FilePurposes.Prescription => PatientFileMaxBytes,
            _ => 200L * 1024 * 1024,
        };
        if (size <= 0 || size > limit)
        {
            await storage.DeleteAsync(file.ObjectKey, ct);
            throw new AppException(422, "FILE_TOO_LARGE", "حجم الملف أكبر من المسموح");
        }

        file.SizeBytes = size;
        file.Status = FileStatuses.Stored;
        await db.SaveChangesAsync(ct);
        return new FileStatusResponse(file.FileId, file.Status);
    }

    public async Task<DownloadUrlResponse> DownloadUrlAsync(Guid fileId, CancellationToken ct)
    {
        var file = await LoadFileAsync(fileId, ct);
        if (file.Status != FileStatuses.Stored)
            throw new AppException(422, "FILE_NOT_STORED", "لم يكتمل رفع الملف");
        return new DownloadUrlResponse(storage.PresignDownload(file.ObjectKey, DownloadExpiry), (int)DownloadExpiry.TotalSeconds);
    }

    public string? PhotoUrl(string? objectKey) =>
        objectKey is null ? null : storage.PresignDownload(objectKey, PhotoUrlExpiry);

    /// <summary>صورة الطبيب يديرها من يملك services.manage، أو الطبيب نفسه.</summary>
    public async Task<Doctor> LoadDoctorForPhotoAsync(Guid doctorId, CancellationToken ct)
    {
        var doctor = await db.Doctors.Include(d => d.Clinic)
                         .FirstOrDefaultAsync(d => d.DoctorId == doctorId && d.Clinic!.BranchId == current.BranchId, ct)
                     ?? throw AppException.NotFound("الطبيب غير موجود في هذا الفرع");
        if (!current.Has(Permissions.ServicesManage) && doctor.UserId != current.UserId)
            throw AppException.Forbidden("صورة الطبيب يغيرها المدير أو الطبيب نفسه");
        return doctor;
    }

    private async Task<StoredFile> LoadFileAsync(Guid fileId, CancellationToken ct) =>
        await db.Files.FirstOrDefaultAsync(f => f.FileId == fileId && f.BranchId == current.BranchId, ct)
        ?? throw new AppException(404, "FILE_NOT_FOUND", "الملف غير موجود");
}
