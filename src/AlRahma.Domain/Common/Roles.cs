namespace AlRahma.Domain.Common;

/// <summary>
/// أدوار النظام الثمانية (is_system في جدول roles، الترحيل 009). يمكن للمدير العام إضافة أدوار مخصصة في الجدول؛
/// users.role مربوط بـ roles.code. السلوك الخاص بدور معين (نطاق كل الفروع للمدير العام، طابور الطبيب...) مرتبط بهذه الرموز.
/// </summary>
public static class Roles
{
    public const string Admin = "admin";
    public const string BranchManager = "branch_manager";
    public const string Doctor = "doctor";
    public const string Receptionist = "receptionist";
    public const string Accountant = "accountant";
    public const string LabTech = "lab_tech";
    public const string Radiologist = "radiologist";
    public const string Pharmacist = "pharmacist";
    /// <summary>مستخدم تطبيق المريض (توكن منفصل، ليس موظفاً ولا يدخل في All).</summary>
    public const string Patient = "patient";

    public static readonly IReadOnlyList<string> All =
        [Admin, BranchManager, Doctor, Receptionist, Accountant, LabTech, Radiologist, Pharmacist];

    /// <summary>دور من الأدوار الثمانية الأصلية.</summary>
    public static bool IsValid(string? role) => role is not null && All.Contains(role);

    public static bool IsSystem(string? role) => IsValid(role);
}
