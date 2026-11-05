namespace AlRahma.Domain.Entities;

public class User
{
    public Guid UserId { get; set; }
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string FullName { get; set; } = "";
    public string? PhoneNumber { get; set; }
    public string Role { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public bool MustChangePassword { get; set; } = true;
    public short FailedLoginCount { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public List<UserBranch> Branches { get; set; } = [];
    public List<UserClinic> Clinics { get; set; } = [];
    public List<UserPermission> ExtraPermissions { get; set; } = [];
    public Doctor? Doctor { get; set; }
}

public class UserBranch
{
    public Guid UserId { get; set; }
    public Guid BranchId { get; set; }
    public Branch? Branch { get; set; }
}

public class UserClinic
{
    public Guid UserId { get; set; }
    public Guid ClinicId { get; set; }
    public Clinic? Clinic { get; set; }
}

public class UserPermission
{
    public Guid UserId { get; set; }
    public string Permission { get; set; } = "";
    public Guid? GrantedBy { get; set; }
    public DateTimeOffset GrantedAt { get; set; }
}

public class Doctor
{
    public Guid DoctorId { get; set; }
    public Guid? UserId { get; set; }
    public Guid ClinicId { get; set; }
    public string FullName { get; set; } = "";
    public string Specialty { get; set; } = "";
    public string? JobTitle { get; set; }
    public string? PhotoObjectKey { get; set; }
    public short DefaultSlotMinutes { get; set; } = 10;
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>false = «ممنوع الإضافي»: لا قبول فوق سعة الوجبة.</summary>
    public bool AllowExtra { get; set; } = true;
    /// <summary>مهلة المحجوز المتأخر بالدقائق؛ null = إعداد الفرع queue.no_show_grace_minutes (20).</summary>
    public short? LateGraceMinutes { get; set; }
    /// <summary>مدة المراجعة بالأيام (اتفاق الطبيب مع المدير)؛ null = إعداد الفرع visit.follow_up_window_days.</summary>
    public short? FollowUpDays { get; set; }
    /// <summary>سعر المراجعة لهذا الطبيب؛ null = سعر خدمة المراجعة، 0 = مجانية.</summary>
    public long? FollowUpPrice { get; set; }
    /// <summary>العربون الإلكتروني لهذا الطبيب؛ null = إعداد الفرع، 0 = لا خيار عربون.</summary>
    public long? OnlineDepositAmount { get; set; }
    /// <summary>مدة العودة المجانية بالنتائج بالأيام؛ null = قيمة العيادة ثم إعداد الفرع queue.free_return_days (ترحيل 018).</summary>
    public short? FreeReturnDays { get; set; }

    public Clinic? Clinic { get; set; }
}

public class Device
{
    public Guid DeviceId { get; set; }
    public string DeviceCode { get; set; } = "";
    public Guid BranchId { get; set; }
    public Guid? ClinicId { get; set; }
    public string Kind { get; set; } = "reception_pc";
    public string Name { get; set; } = "";
    public string? AgentKeyHash { get; set; }
    public string? DisplayKeyHash { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
}

public class RefreshToken
{
    public Guid TokenId { get; set; }
    public Guid? UserId { get; set; }
    public Guid? PatientAccountId { get; set; }
    public Guid? BranchId { get; set; }
    public Guid? DeviceId { get; set; }
    public string TokenHash { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public Guid? ReplacedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>دور (نظامي أو مخصص). الرمز ثابت لا يتغير لأنه مخزن في users.role والتوكنات.</summary>
public class Role
{
    public string Code { get; set; } = "";
    public string NameAr { get; set; } = "";
    public string? Description { get; set; }
    /// <summary>قسم الواجهة الذي يبدأ منه مستخدمو الدور (للأدوار المخصصة)؛ null = يُشتق من الصلاحيات.</summary>
    public string? HomeSection { get; set; }
    public bool IsSystem { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }

    public List<RolePermission> Permissions { get; set; } = [];
}

public class RolePermission
{
    public string RoleCode { get; set; } = "";
    public string Permission { get; set; } = "";
    public Guid? GrantedBy { get; set; }
    public DateTimeOffset GrantedAt { get; set; }
}
