namespace AlRahma.Api.Infrastructure;

/// <summary>سياسات تجمع أكثر من صلاحية (يكفي امتلاك إحداها).</summary>
public static class Policies
{
    public const string BillingRead = "billing.read";
    public const string RefundsRequest = "refunds.request";
    public const string DeferralsRead = "deferrals.read";
    /// <summary>توكن تطبيق المريض فقط.</summary>
    public const string Patient = "patient";
    /// <summary>موظف أو مريض؛ الخدمة تفحص الصلاحية أو الربط بالحساب.</summary>
    public const string StaffOrPatient = "staff_or_patient";
    public const string ClinicalRead = "clinical.read";
    public const string OrdersCreate = "orders.create";
    /// <summary>عرض الأدوار: roles.view أو users.view.</summary>
    public const string RolesRead = "roles.read";
    /// <summary>القراءة: من يعمل في القسم أو من يملك رمز العرض.</summary>
    public const string LabRead = "lab.read";
    public const string RadiologyRead = "radiology.read";
    public const string PharmacyRead = "pharmacy.read";
}

/// <summary>مخططات المصادقة: Bearer (الموظفون، الافتراضي) و Patient (تطبيق المريض).</summary>
public static class AuthSchemes
{
    public const string Patient = "Patient";
}
