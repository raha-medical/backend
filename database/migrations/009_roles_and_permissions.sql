-- =============================================================================
-- ترحيل 009: الأدوار والصلاحيات من قاعدة البيانات (RBAC من الواجهة)
--   * roles: الأدوار الثمانية كأدوار نظام (is_system) + أدوار مخصصة يضيفها المدير العام
--   * role_permissions: صلاحيات كل دور (نفس صلاحيات Permissions.ByRole تماماً، فلا يتغير أي سلوك)،
--     مع roles.view و branches.view لمدير الفرع (عرض الأدوار ونظرة فرعه كما كان يراها)
--   * users.role: قيد CHECK الثابت يُستبدل بـ FK إلى roles.code
-- إضافات آمنة، ويمكن تشغيله أكثر من مرة. التراجع: down/009_roles_and_permissions_down.sql
-- التشغيل: psql "$DATABASE_URL" -f database/migrations/009_roles_and_permissions.sql
-- =============================================================================

BEGIN;

CREATE TABLE IF NOT EXISTS roles (
    code            VARCHAR(30) PRIMARY KEY CHECK (code ~ '^[a-z][a-z0-9_]{2,29}$'),
    name_ar         VARCHAR(100) NOT NULL,
    description     TEXT,
    home_section    VARCHAR(30),                                -- قسم الواجهة الأول للأدوار المخصصة (null = يُشتق)
    is_system       BOOLEAN NOT NULL DEFAULT FALSE,             -- أدوار النظام: لا تُحذف ولا تُوقف ولا يتغير رمزها
    is_active       BOOLEAN NOT NULL DEFAULT TRUE,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    created_by      UUID REFERENCES users(user_id),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_by      UUID REFERENCES users(user_id)
);
CREATE UNIQUE INDEX IF NOT EXISTS uq_roles_name ON roles(name_ar);

CREATE TABLE IF NOT EXISTS role_permissions (
    role_code       VARCHAR(30) NOT NULL REFERENCES roles(code) ON DELETE CASCADE,
    permission      VARCHAR(60) NOT NULL,
    granted_by      UUID REFERENCES users(user_id),
    granted_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (role_code, permission)
);

INSERT INTO roles (code, name_ar, is_system) VALUES
    ('admin',          'مدير عام',      TRUE),
    ('branch_manager', 'مدير فرع',      TRUE),
    ('doctor',         'طبيب',          TRUE),
    ('receptionist',   'موظف استقبال',  TRUE),
    ('accountant',     'محاسب عام',     TRUE),
    ('lab_tech',       'فني مختبر',     TRUE),
    ('radiologist',    'أخصائي أشعة',   TRUE),
    ('pharmacist',     'صيدلي',         TRUE)
ON CONFLICT (code) DO NOTHING;

-- المدير العام يملك كل الصلاحيات دائماً (تُحسب في الكود ولا تُخزن)
INSERT INTO role_permissions (role_code, permission) VALUES
    ('branch_manager', 'users.manage'), ('branch_manager', 'services.manage'), ('branch_manager', 'patients.write'),
    ('branch_manager', 'patients.unban'), ('branch_manager', 'queue.manage'), ('branch_manager', 'discounts.grant'),
    ('branch_manager', 'refunds.approve'), ('branch_manager', 'shifts.review'), ('branch_manager', 'deferrals.grant'),
    ('branch_manager', 'reports.finance'), ('branch_manager', 'expenses.manage'),
    ('branch_manager', 'roles.view'), ('branch_manager', 'branches.view'),
    ('doctor', 'clinical.write'), ('doctor', 'queue.manage'), ('doctor', 'deferrals.grant'),
    ('receptionist', 'queue.manage'), ('receptionist', 'payments.collect'), ('receptionist', 'patients.write'),
    ('accountant', 'shifts.review'), ('accountant', 'refunds.approve'), ('accountant', 'reports.finance'),
    ('accountant', 'patients.unban'), ('accountant', 'payments.collect'), ('accountant', 'audit.read'),
    ('accountant', 'expenses.manage'),
    ('lab_tech', 'lab.write'),
    ('radiologist', 'radiology.write'),
    ('pharmacist', 'pharmacy.dispense')
ON CONFLICT (role_code, permission) DO NOTHING;

-- users.role: من قيد CHECK ثابت إلى FK (الأدوار المخصصة تعمل عند الدخول)
ALTER TABLE users DROP CONSTRAINT IF EXISTS users_role_check;
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'fk_users_role') THEN
        ALTER TABLE users ADD CONSTRAINT fk_users_role FOREIGN KEY (role) REFERENCES roles(code);
    END IF;
END $$;
CREATE INDEX IF NOT EXISTS idx_users_role ON users(role);

COMMIT;
