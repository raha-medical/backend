-- =============================================================================
-- تراجع الترحيل 009: يعيد قيد CHECK على users.role ويحذف جدولي الأدوار.
-- يرفض التنفيذ إذا كان هناك موظفون على أدوار مخصصة (انقلهم لأدوار النظام أولاً).
-- الصلاحيات الإضافية للموظفين بالرموز الجديدة (users.view ...) تُحذف لأن الكود القديم لا يعرفها.
-- التشغيل: psql "$DATABASE_URL" -f database/migrations/down/009_roles_and_permissions_down.sql
-- =============================================================================

BEGIN;

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM users WHERE role NOT IN
        ('admin', 'branch_manager', 'doctor', 'receptionist', 'accountant', 'lab_tech', 'radiologist', 'pharmacist')) THEN
        RAISE EXCEPTION 'custom roles still assigned to users: move them to system roles before rolling back 009';
    END IF;
END $$;

ALTER TABLE users DROP CONSTRAINT IF EXISTS fk_users_role;
DROP INDEX IF EXISTS idx_users_role;
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'users_role_check') THEN
        ALTER TABLE users ADD CONSTRAINT users_role_check CHECK (role IN (
            'admin', 'branch_manager', 'doctor', 'receptionist', 'accountant', 'lab_tech', 'radiologist', 'pharmacist'));
    END IF;
END $$;

DELETE FROM user_permissions WHERE permission NOT IN (
    'users.manage', 'services.manage', 'patients.write', 'patients.unban', 'queue.manage', 'payments.collect',
    'discounts.grant', 'refunds.approve', 'shifts.review', 'deferrals.grant', 'reports.finance', 'clinical.write',
    'lab.write', 'radiology.write', 'pharmacy.dispense', 'audit.read', 'expenses.manage');

DROP TABLE IF EXISTS role_permissions;
DROP TABLE IF EXISTS roles;

COMMIT;
