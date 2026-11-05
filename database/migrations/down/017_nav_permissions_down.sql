-- =============================================================================
-- تراجع الترحيل 017: يحذف رموز شاشات القائمة الجانبية من كل الأدوار والموظفين (الكود القديم لا يعرفها).
-- =============================================================================

BEGIN;
DELETE FROM role_permissions WHERE permission IN ('patients.merge', 'branches.map', 'lookups.manage', 'backups.manage', 'sandbox.view');
DELETE FROM user_permissions WHERE permission IN ('patients.merge', 'branches.map', 'lookups.manage', 'backups.manage', 'sandbox.view');
COMMIT;
