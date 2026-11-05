namespace AlRahma.Application.Auth;

public record LoginRequest(string? Username, string? Password, Guid? BranchId, string? DeviceId);
public record RefreshRequest(string? RefreshToken);
public record SwitchBranchRequest(Guid? BranchId);
public record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);

public record AuthUserDto(
    Guid UserId,
    string FullName,
    string Role,
    Guid BranchId,
    IReadOnlyList<Guid> BranchIds,
    IReadOnlyList<Guid> ClinicIds,
    Guid? DoctorId,
    IReadOnlyList<string> Permissions,
    bool MustChangePassword,
    string? RoleNameAr = null,
    bool IsSystemRole = true,
    string? HomeSection = null,
    IReadOnlyList<string>? Sections = null);

public record AuthResponse(
    string AccessToken,
    int AccessExpiresIn,
    string RefreshToken,
    int RefreshExpiresIn,
    AuthUserDto User);
