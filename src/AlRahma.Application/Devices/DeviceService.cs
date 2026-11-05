using System.Security.Cryptography;
using System.Text;
using AlRahma.Application.Abstractions;
using AlRahma.Application.Common;
using AlRahma.Application.Queue;
using AlRahma.Domain.Common;
using AlRahma.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AlRahma.Application.Devices;

public record CreateDeviceRequest(string? Kind, string? Name, Guid? ClinicId);
public record DeviceDto(Guid DeviceId, string DeviceCode, string Kind, string Name, Guid? ClinicId, bool IsActive, DateTimeOffset CreatedAt);
public record CreateDeviceResponse(Guid DeviceId, string DeviceCode, string Kind, string Name, Guid? ClinicId, string? DisplayKey);

/// <summary>أجهزة الفرع: حواسيب الاستقبال والأقسام وشاشات الانتظار. مفتاح الشاشة يظهر مرة واحدة عند الإنشاء.</summary>
public class DeviceService(IAppDbContext db, ICurrentUser current, IAuditLogger audit, IClock clock, NumberSequences numbers, QueueReader reader,
    SettingsReader settings, CheckInTokenService checkInTokens)
{
    public static readonly string[] Kinds = ["reception_pc", "display", "lab_pc", "radiology_pc", "pharmacy_pc"];

    public async Task<IReadOnlyList<DeviceDto>> ListAsync(CancellationToken ct) =>
        await db.Devices.AsNoTracking().Where(d => d.BranchId == current.BranchId)
            .OrderBy(d => d.Kind).ThenBy(d => d.Name)
            .Select(d => new DeviceDto(d.DeviceId, d.DeviceCode, d.Kind, d.Name, d.ClinicId, d.IsActive, d.CreatedAt))
            .ToListAsync(ct);

    public async Task<CreateDeviceResponse> CreateAsync(CreateDeviceRequest req, CancellationToken ct)
    {
        new Validator()
            .Check(req.Kind is not null && Kinds.Contains(req.Kind), "kind", string.Join(" | ", Kinds))
            .Check(!string.IsNullOrWhiteSpace(req.Name) && req.Name.Length <= 100, "name", "اسم الجهاز مطلوب (100 حرف كحد أقصى)")
            .Check(req.Kind != "display" || req.ClinicId is not null, "clinic_id", "الشاشة تُربط بعيادة")
            .ThrowIfInvalid();
        if (req.ClinicId is { } cid && !await db.Clinics.AnyAsync(c => c.ClinicId == cid && c.BranchId == current.BranchId, ct))
            throw AppException.Validation(new Dictionary<string, string[]> { ["clinic_id"] = ["العيادة ليست في هذا الفرع"] });

        var code = await numbers.BranchCodeAsync(current.BranchId, ct);
        var seq = await numbers.NextAsync(current.BranchId, "device", "all", ct);
        string? key = req.Kind == "display" ? NewKey() : null;
        var device = new Device
        {
            DeviceId = UuidV7.New(),
            DeviceCode = $"{code}-{req.Kind}-{seq:D2}".ToLowerInvariant(),
            BranchId = current.BranchId,
            ClinicId = req.ClinicId,
            Kind = req.Kind!,
            Name = req.Name!.Trim(),
            DisplayKeyHash = key is null ? null : Hash(key),
            CreatedAt = clock.UtcNow,
        };
        db.Devices.Add(device);
        audit.Add("DEVICE_CREATED", "device", device.DeviceId, after: new { device.DeviceCode, device.Kind, device.ClinicId });
        await db.SaveChangesAsync(ct);
        return new CreateDeviceResponse(device.DeviceId, device.DeviceCode, device.Kind, device.Name, device.ClinicId, key);
    }

    /// <summary>مفتاح جديد للشاشة (عند فقدانه أو تسريبه). المفتاح القديم يتوقف فوراً.</summary>
    public async Task<CreateDeviceResponse> RotateDisplayKeyAsync(Guid deviceId, CancellationToken ct)
    {
        var device = await db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId && d.BranchId == current.BranchId, ct)
                     ?? throw AppException.NotFound("الجهاز غير موجود");
        if (device.Kind != "display") throw new AppException(422, "NOT_A_DISPLAY", "الجهاز ليس شاشة انتظار");
        var key = NewKey();
        device.DisplayKeyHash = Hash(key);
        audit.Add("DISPLAY_KEY_ROTATED", "device", device.DeviceId);
        await db.SaveChangesAsync(ct);
        return new CreateDeviceResponse(device.DeviceId, device.DeviceCode, device.Kind, device.Name, device.ClinicId, key);
    }

    /// <summary>يتحقق من مفتاح الشاشة ويعيد الجهاز، أو null.</summary>
    public async Task<Device?> AuthenticateDisplayAsync(Guid deviceId, string? key, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.DeviceId == deviceId && d.Kind == "display" && d.IsActive, ct);
        if (device?.DisplayKeyHash is null || device.ClinicId is null) return null;
        var ok = CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(device.DisplayKeyHash), Encoding.ASCII.GetBytes(Hash(key)));
        return ok ? device : null;
    }

    public async Task<DisplayQueueDto> DisplayQueueAsync(Guid deviceId, string? key, CancellationToken ct)
    {
        var device = await AuthenticateDisplayAsync(deviceId, key, ct)
                     ?? throw new AppException(401, "INVALID_DISPLAY_KEY", "مفتاح الشاشة غير صحيح");
        return await reader.DisplayAsync(device.ClinicId!.Value, ct);
    }

    /// <summary>
    /// رمز QR الدوار لتثبيت الحضور الذاتي على هذه الشاشة (المرحلة 3). الشاشة تعيد طلبه بعد refresh_after_seconds.
    /// الفرع الذي أوقف الحضور الذاتي: enabled = false بلا رمز (تُخفي الشاشة الـ QR).
    /// </summary>
    public async Task<CheckInTokenDto> CheckInTokenAsync(Guid deviceId, string? key, CancellationToken ct)
    {
        var device = await AuthenticateDisplayAsync(deviceId, key, ct)
                     ?? throw new AppException(401, "INVALID_DISPLAY_KEY", "مفتاح الشاشة غير صحيح");
        var clinic = await db.Clinics.AsNoTracking().Where(c => c.ClinicId == device.ClinicId).Select(c => c.Name).FirstAsync(ct);
        var enabled = await settings.GetLongAsync(SettingsReader.SelfCheckInEnabled, device.BranchId, 1, ct) == 1;
        var scope = await settings.GetStringAsync(SettingsReader.CheckInQrScope, device.BranchId, ct);
        if (scope is null || !SettingsReader.CheckInQrScopeValues.All.Contains(scope)) scope = SettingsReader.CheckInQrScopeValues.Clinic;
        if (!enabled)
            return new CheckInTokenDto(false, null, null, null, null, 60, checkInTokens.WindowSeconds, device.ClinicId!.Value, clinic, scope);
        var (token, issued, expires, refresh) = checkInTokens.Issue(device.DeviceId);
        return new CheckInTokenDto(true, token, CheckInTokenService.QrScheme + token, issued, expires, refresh, checkInTokens.WindowSeconds,
            device.ClinicId!.Value, clinic, scope);
    }

    private static string NewKey() => "dk_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
