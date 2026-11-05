namespace AlRahma.Application.Users;

/// <summary>specialty و job_title: رمز أو اسم من قوائم الاختيار. job_title = "" يمسح العنوان.</summary>
public record DoctorProfileInput(string? Specialty, Guid? ClinicId, short? DefaultSlotMinutes, string? JobTitle = null);

public record CreateUserRequest(
    string? Username,
    string? FullName,
    string? PhoneNumber,
    string? Role,
    List<Guid>? BranchIds,
    List<Guid>? ClinicIds,
    List<string>? ExtraPermissions,
    DoctorProfileInput? DoctorProfile,
    string? TemporaryPassword);

/// <summary>PATCH: الحقول غير المرسلة (null) لا تتغير.</summary>
public record UpdateUserRequest(
    string? FullName,
    string? PhoneNumber,
    string? Role,
    List<Guid>? BranchIds,
    List<Guid>? ClinicIds,
    List<string>? ExtraPermissions,
    DoctorProfileInput? DoctorProfile);

public record DoctorProfileDto(Guid DoctorId, string Specialty, string? JobTitle, Guid ClinicId, short DefaultSlotMinutes);

public record UserDto(
    Guid UserId,
    string Username,
    string FullName,
    string? PhoneNumber,
    string Role,
    bool IsActive,
    bool MustChangePassword,
    IReadOnlyList<Guid> BranchIds,
    IReadOnlyList<Guid> ClinicIds,
    IReadOnlyList<string> ExtraPermissions,
    IReadOnlyList<string> Permissions,
    DoctorProfileDto? DoctorProfile,
    DateTimeOffset? LastLoginAt,
    DateTimeOffset CreatedAt,
    string? RoleNameAr = null);

public record UserListQuery(Guid? BranchId, string? Role, string? Q, bool? IsActive, int Page = 1, int PageSize = 20);

public record ResetPasswordResponse(string TemporaryPassword);
