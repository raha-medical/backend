-- =============================================================================
-- ترحيل 017: ربط كل شاشة في القائمة الجانبية برمز صلاحية لا بدور (المواصفة 8.6-ق1)
--   * patients.merge: دمج ملفات المرضى. يُمنح لمدير الفرع (كان مسموحاً له بالدور) فلا يفقد أحد الوصول.
--   * branches.map، lookups.manage، backups.manage، sandbox.view: للمدير العام فقط (AdminOnly في الكود)،
--     والمدير العام يملك كل الرموز من الكود مباشرة فلا تحتاج صفوفاً هنا.
-- إضافات آمنة، ويمكن تشغيله أكثر من مرة. التراجع: down/017_nav_permissions_down.sql
-- =============================================================================

BEGIN;

INSERT INTO role_permissions (role_code, permission) VALUES
    ('branch_manager', 'patients.merge')
ON CONFLICT (role_code, permission) DO NOTHING;

COMMIT;
