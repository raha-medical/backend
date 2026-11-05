namespace AlRahma.Domain.Common;

/// <summary>
/// رموز الصلاحيات. صلاحيات كل دور تُقرأ من جدول role_permissions (الترحيل 009)؛ ByRole أدناه هو البذرة الأصلية
/// للأدوار الثمانية (نفس ما يُدرج في الترحيل) ويُستخدم احتياطياً فقط. يمكن إضافة صلاحيات لموظف محدد عبر user_permissions.
/// </summary>
public static class Permissions
{
    // ------------------------------------------------------------------ الرموز الأصلية (لا تتغير)
    public const string UsersManage = "users.manage";
    public const string ServicesManage = "services.manage";
    public const string PatientsWrite = "patients.write";
    public const string PatientsUnban = "patients.unban";
    public const string QueueManage = "queue.manage";
    public const string PaymentsCollect = "payments.collect";
    public const string DiscountsGrant = "discounts.grant";
    public const string RefundsApprove = "refunds.approve";
    public const string ShiftsReview = "shifts.review";
    public const string DeferralsGrant = "deferrals.grant";
    public const string ReportsFinance = "reports.finance";
    public const string ClinicalWrite = "clinical.write";
    public const string LabWrite = "lab.write";
    public const string RadiologyWrite = "radiology.write";
    public const string PharmacyDispense = "pharmacy.dispense";
    public const string AuditRead = "audit.read";
    public const string ExpensesManage = "expenses.manage";

    // ------------------------------------------------------------------ رموز دقيقة (الترحيل 009)
    public const string UsersView = "users.view";
    public const string UsersCreate = "users.create";
    public const string UsersEdit = "users.edit";
    public const string UsersDelete = "users.delete";
    public const string RolesView = "roles.view";
    public const string RolesCreate = "roles.create";
    public const string RolesEdit = "roles.edit";
    public const string RolesDelete = "roles.delete";
    public const string BranchesView = "branches.view";
    public const string BranchesCreate = "branches.create";
    public const string BranchesEdit = "branches.edit";
    public const string BranchesDelete = "branches.delete";
    public const string ServicesView = "services.view";
    public const string ServicesCreate = "services.create";
    public const string ServicesEdit = "services.edit";
    public const string ServicesDelete = "services.delete";
    public const string DevicesView = "devices.view";
    public const string DevicesCreate = "devices.create";
    public const string DevicesEdit = "devices.edit";
    public const string SettingsEdit = "settings.edit";

    // ------------------------------------------------------------------ المرحلة 3 (الترحيل 011)
    /// <summary>تجاوز سياسة الإلغاء والتأجيل نيابة عن المريض: إعفاء رسوم الإلغاء المتأخر وعدم احتسابه غياباً، وتجاوز حد/مهلة تغيير الموعد (بسبب، ويُسجل في التدقيق).</summary>
    public const string BookingsPolicyOverride = "bookings.policy_override";

    // ------------------------------------------------------------------ قراءة فقط لمدير الفرع (الترحيل 012)
    public const string LabView = "lab.view";
    public const string RadiologyView = "radiology.view";
    public const string PharmacyView = "pharmacy.view";
    public const string NotificationsBranchView = "notifications.branch_view";
    public const string BackupsView = "backups.view";

    /// <summary>الرموز الأصلية السبعة عشر (قبل الترحيل 009).</summary>
    public static readonly IReadOnlyList<string> Legacy =
    [
        UsersManage, ServicesManage, PatientsWrite, PatientsUnban, QueueManage, PaymentsCollect,
        DiscountsGrant, RefundsApprove, ShiftsReview, DeferralsGrant, ReportsFinance, ClinicalWrite,
        LabWrite, RadiologyWrite, PharmacyDispense, AuditRead, ExpensesManage
    ];

    /// <summary>
    /// الرموز الشاملة القديمة تمنح ما كانت تمنحه: users.manage = عرض وإضافة وتعديل وتعطيل المستخدمين،
    /// services.manage = إدارة الخدمات والأجهزة وإعدادات الفرع (إضافة لما يفحصه بنفسه: الأقسام، الإغلاق، جداول الأطباء...).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[]> Implies = new Dictionary<string, string[]>
    {
        [UsersManage] = [UsersView, UsersCreate, UsersEdit, UsersDelete],
        [ServicesManage] = [ServicesView, ServicesCreate, ServicesEdit, ServicesDelete, DevicesView, DevicesCreate, DevicesEdit, SettingsEdit],
    };

    /// <summary>صلاحيات لا تُمنح إلا للمدير العام (لا تُضاف لدور آخر ولا لموظف): إدارة الأدوار، وإنشاء الفروع وإيقافها.</summary>
    public static readonly IReadOnlySet<string> AdminOnly = new HashSet<string>
    {
        RolesCreate, RolesEdit, RolesDelete, BranchesCreate, BranchesDelete,
    };

    public static readonly IReadOnlyList<PermissionSection> Catalog =
    [
        new("queue", "الطابور والاستقبال", [
            new(QueueManage, "all", "إدارة الطابور", "تثبيت الحضور، الاستدعاء، الطوارئ، الغياب، الحجز الهاتفي"),
            new(BookingsPolicyOverride, "special", "تجاوز سياسة الإلغاء والتأجيل",
                "إعفاء رسوم الإلغاء المتأخر وعدم احتسابه غياباً، وتجاوز حد ومهلة تغيير الموعد، بسبب يُسجل في التدقيق"),
        ]),
        new("patients", "المرضى", [
            new(PatientsWrite, "edit", "تسجيل وتعديل المرضى", "إنشاء ملف مريض وتعديله ودمج الملفات (الدمج لمدير الفرع)"),
            new(PatientsUnban, "special", "فك حظر المريض", "إعفاء أو عربون فك الحظر"),
        ]),
        new("billing", "الفوترة والتحصيل", [
            new(PaymentsCollect, "create", "التحصيل والفواتير", "فتح الوردية، التحصيل، الفاتورة اليدوية، طلب الاسترجاع ضمن الحد"),
            new(DiscountsGrant, "special", "منح الخصم", "خصم على الفاتورة اليدوية"),
        ]),
        new("shifts", "الورديات", [
            new(ShiftsReview, "special", "مراجعة الورديات", "قائمة الورديات، المطابقة، المراجعة، إعادة طباعة بلا حد"),
        ]),
        new("refunds", "الاسترجاع", [
            new(RefundsApprove, "special", "اعتماد الاسترجاع", "الموافقة على الاسترجاع فوق الحد ورفضه، إلغاء الفاتورة"),
        ]),
        new("deferrals", "الدفع الآجل", [
            new(DeferralsGrant, "create", "منح الدفع الآجل", "استثناء بنود من الدفع"),
        ]),
        new("clinical", "العيادة والسجل الطبي", [
            new(ClinicalWrite, "all", "المعاينة والوصفة", "المعاينة، الوصفة، طلبات المختبر والأشعة، السجل الطبي"),
        ]),
        new("lab", "المختبر", [
            new(LabView, "view", "عرض المختبر", "قائمة العمل والنتائج لفرعه، قراءة فقط"),
            new(LabWrite, "all", "عمل المختبر", "قائمة العمل، العينات، النتائج واعتمادها"),
        ]),
        new("radiology", "الأشعة", [
            new(RadiologyView, "view", "عرض الأشعة", "قائمة العمل والتقارير لفرعه، قراءة فقط"),
            new(RadiologyWrite, "all", "عمل الأشعة", "الجدولة، التنفيذ، التقرير والصور"),
        ]),
        new("pharmacy", "الصيدلية", [
            new(PharmacyView, "view", "عرض الصيدلية", "سجل المصروف في فرعه، قراءة فقط"),
            new(PharmacyDispense, "all", "صرف الوصفات", "البحث بكود الوصفة والصرف وسجل المصروف"),
        ]),
        new("reports", "التقارير", [
            new(ReportsFinance, "view", "التقارير المالية", "لوحة الفروع، الإيرادات، أرباح الأطباء، الغياب، التسويات، قائمة الآجل"),
        ]),
        new("audit", "سجل التدقيق", [
            new(AuditRead, "view", "عرض سجل التدقيق", null),
        ]),
        new("expenses", "مصاريف الفروع", [
            new(ExpensesManage, "all", "إدارة المصاريف", "تسجيل المصاريف وعرضها وإلغاؤها"),
        ]),
        new("users", "المستخدمون", [
            new(UsersView, "view", "عرض المستخدمين", null),
            new(UsersCreate, "create", "إضافة مستخدم", null),
            new(UsersEdit, "edit", "تعديل مستخدم", "التعديل، التفعيل، إعادة تعيين كلمة المرور"),
            new(UsersDelete, "delete", "تعطيل مستخدم", "لا يوجد حذف نهائي للمستخدمين: التعطيل يوقف الحساب ويلغي جلساته"),
            new(UsersManage, "all", "إدارة المستخدمين كاملة", "تشمل العرض والإضافة والتعديل والتعطيل", true),
        ]),
        new("roles", "الأدوار والصلاحيات", [
            new(RolesView, "view", "عرض الأدوار والصلاحيات", null),
            new(RolesCreate, "create", "إضافة دور", "للمدير العام فقط"),
            new(RolesEdit, "edit", "تعديل دور", "للمدير العام فقط"),
            new(RolesDelete, "delete", "حذف دور", "للمدير العام فقط"),
        ]),
        new("branches", "الفروع", [
            new(BranchesView, "view", "عرض تفاصيل الفرع", "نظرة الفرع (overview) لفروعه المسندة إليه"),
            new(BranchesCreate, "create", "إضافة فرع", "للمدير العام فقط"),
            new(BranchesEdit, "edit", "تعديل بيانات الفرع", "الاسم والهاتف والعنوان والموقع وأوقات الدوام، لفروعه المسندة إليه"),
            new(BranchesDelete, "delete", "إيقاف فرع نهائياً", "للمدير العام فقط"),
        ]),
        new("services", "الخدمات والأسعار", [
            new(ServicesView, "view", "عرض كتالوج الخدمات", "يشمل الخدمات الموقوفة (الخدمات الفعالة يراها كل موظفي الفرع)"),
            new(ServicesCreate, "create", "إضافة خدمة", null),
            new(ServicesEdit, "edit", "تعديل خدمة وسعرها", null),
            new(ServicesDelete, "delete", "إيقاف خدمة", null),
            new(ServicesManage, "all", "إعداد الفرع كاملاً",
                "تشمل الخدمات والأجهزة وإعدادات الفرع، والأقسام والعيادات، والإغلاق المؤقت، وجداول الأطباء وإعداداتهم، وقوالب التحاليل", true),
        ]),
        new("devices", "الأجهزة وشاشات الانتظار", [
            new(DevicesView, "view", "عرض الأجهزة", null),
            new(DevicesCreate, "create", "إضافة جهاز", null),
            new(DevicesEdit, "edit", "تجديد مفتاح الشاشة", null),
        ]),
        new("settings", "إعدادات الفرع", [
            new(SettingsEdit, "edit", "تعديل إعدادات الفرع", "قراءة الإعدادات متاحة لموظفي الفرع، والإعدادات العامة للمدير العام فقط"),
        ]),
        new("notifications", "إشعارات الفرع", [
            new(NotificationsBranchView, "view", "عرض إشعارات الفرع", "إشعارات الموظفين التشغيلية في فرعه الحالي"),
        ]),
        new("backups", "النسخ الاحتياطي", [
            new(BackupsView, "view", "عرض حالة النسخ الاحتياطي", "ملخص حالة نسخ السيرفر، قراءة فقط. الإدارة للمدير العام"),
        ]),
    ];

    /// <summary>كل الرموز المعروفة (الأصلية والدقيقة).</summary>
    public static readonly IReadOnlyList<string> All = Catalog.SelectMany(s => s.Permissions).Select(p => p.Code).ToList();

    private static readonly HashSet<string> AllSet = [.. All];

    public static bool IsKnown(string? code) => code is not null && AllSet.Contains(code);

    /// <summary>يضيف ما تتضمنه الرموز الشاملة (users.manage ...) ويرتب القائمة.</summary>
    public static List<string> Expand(IEnumerable<string> codes)
    {
        var set = new HashSet<string>(codes);
        foreach (var c in set.ToList())
            if (Implies.TryGetValue(c, out var implied)) set.UnionWith(implied);
        return set.Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>البذرة الأصلية لصلاحيات الأدوار الثمانية (= ما يدرجه الترحيل 009 في role_permissions).</summary>
    public static readonly IReadOnlyDictionary<string, string[]> ByRole = new Dictionary<string, string[]>
    {
        [Roles.Admin] = [.. All],
        [Roles.BranchManager] =
        [
            UsersManage, ServicesManage, PatientsWrite, PatientsUnban, QueueManage, DiscountsGrant,
            RefundsApprove, ShiftsReview, DeferralsGrant, ReportsFinance, ExpensesManage, RolesView, BranchesView,
            LabView, RadiologyView, PharmacyView, AuditRead, NotificationsBranchView, BackupsView
        ],
        [Roles.Doctor] = [ClinicalWrite, QueueManage, DeferralsGrant],
        [Roles.Receptionist] = [QueueManage, PaymentsCollect, PatientsWrite],
        [Roles.Accountant] = [ShiftsReview, RefundsApprove, ReportsFinance, PatientsUnban, PaymentsCollect, AuditRead, ExpensesManage],
        [Roles.LabTech] = [LabWrite],
        [Roles.Radiologist] = [RadiologyWrite],
        [Roles.Pharmacist] = [PharmacyDispense],
    };

    public static readonly IReadOnlyDictionary<string, string> RoleNamesAr = new Dictionary<string, string>
    {
        [Roles.Admin] = "مدير عام",
        [Roles.BranchManager] = "مدير فرع",
        [Roles.Doctor] = "طبيب",
        [Roles.Receptionist] = "موظف استقبال",
        [Roles.Accountant] = "محاسب عام",
        [Roles.LabTech] = "فني مختبر",
        [Roles.Radiologist] = "أخصائي أشعة",
        [Roles.Pharmacist] = "صيدلي",
    };

    public static readonly IReadOnlyList<(string Code, string NameAr)> Actions =
    [
        ("view", "عرض"), ("create", "إضافة"), ("edit", "تعديل"), ("delete", "حذف / إيقاف"), ("all", "كامل القسم"), ("special", "صلاحية خاصة"),
    ];
}

/// <summary>قسم في مصفوفة الصلاحيات.</summary>
public record PermissionSection(string Code, string NameAr, IReadOnlyList<PermissionDef> Permissions);

/// <summary>action: view | create | edit | delete | all (كامل القسم) | special. Legacy = رمز شامل قديم (يتضمن رموز implies).</summary>
public record PermissionDef(string Code, string Action, string NameAr, string? Description, bool Legacy = false);

/// <summary>
/// أقسام الواجهة (/staff/{section}) التي يفتحها المستخدم، وصفحته الرئيسية. للأدوار الثمانية نفس توجيه الواجهة الحالي،
/// وللأدوار المخصصة تُشتق من الصلاحيات الفعلية.
/// </summary>
public static class NavSections
{
    public const string Reception = "reception";
    public const string Doctor = "doctor";
    public const string Lab = "lab";
    public const string Radiology = "radiology";
    public const string Pharmacy = "pharmacy";
    public const string Accountant = "accountant";
    public const string Admin = "admin";

    public static readonly IReadOnlyList<string> All = [Reception, Doctor, Lab, Radiology, Pharmacy, Accountant, Admin];

    private static readonly IReadOnlyDictionary<string, (string Home, string[] Sections)> System = new Dictionary<string, (string, string[])>
    {
        [Roles.Admin] = (Admin, [Reception, Lab, Radiology, Accountant, Admin]),
        [Roles.BranchManager] = (Admin, [Reception, Lab, Radiology, Pharmacy, Accountant, Admin]),
        [Roles.Doctor] = (Doctor, [Doctor]),
        [Roles.Receptionist] = (Reception, [Reception]),
        [Roles.Accountant] = (Accountant, [Accountant]),
        [Roles.LabTech] = (Lab, [Lab]),
        [Roles.Radiologist] = (Radiology, [Radiology]),
        [Roles.Pharmacist] = (Pharmacy, [Pharmacy]),
    };

    /// <summary>القسم الذي تفتحه كل صلاحية للأدوار المخصصة.</summary>
    public static string[] ForPermissions(IReadOnlyCollection<string> perms)
    {
        bool Any(params string[] codes) => codes.Any(perms.Contains);
        var list = new List<string>();
        if (Any(Permissions.QueueManage, Permissions.PaymentsCollect, Permissions.PatientsWrite)) list.Add(Reception);
        if (Any(Permissions.ClinicalWrite)) list.Add(Doctor);
        if (Any(Permissions.LabWrite, Permissions.LabView)) list.Add(Lab);
        if (Any(Permissions.RadiologyWrite, Permissions.RadiologyView)) list.Add(Radiology);
        if (Any(Permissions.PharmacyDispense, Permissions.PharmacyView)) list.Add(Pharmacy);
        if (Any(Permissions.ShiftsReview, Permissions.RefundsApprove, Permissions.ReportsFinance, Permissions.AuditRead, Permissions.ExpensesManage))
            list.Add(Accountant);
        if (Any(Permissions.UsersView, Permissions.RolesView, Permissions.BranchesView, Permissions.BranchesEdit, Permissions.ServicesView,
                Permissions.ServicesManage, Permissions.DevicesView))
            list.Add(Admin);
        return [.. list];
    }

    public static (string? Home, IReadOnlyList<string> Sections) Resolve(string role, string? homeOverride, IReadOnlyCollection<string> perms)
    {
        if (System.TryGetValue(role, out var sys)) return (sys.Home, sys.Sections);
        var sections = ForPermissions(perms);
        var home = homeOverride is not null && sections.Contains(homeOverride) ? homeOverride : sections.FirstOrDefault();
        return (home, sections);
    }
}
