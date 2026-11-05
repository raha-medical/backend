using AlRahma.Application.Abstractions;
using AlRahma.Application.Common;
using AlRahma.Domain.Common;
using AlRahma.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AlRahma.Application.Files;

public record UploadUrlRequest(string? FileName, string? ContentType, long? SizeBytes, string? Purpose, Guid? OrderItemId, Guid? DoctorId);
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
        if (req.Purpose != FilePurposes.DoctorPhoto)
            throw new AppException(422, "INVALID_FILE_PURPOSE", "نوع الملف غير مدعوم", "lab | radiology | doctor_photo");

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
        var limit = file.Purpose == FilePurposes.DoctorPhoto ? DoctorPhotoMaxBytes : 200L * 1024 * 1024;
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
