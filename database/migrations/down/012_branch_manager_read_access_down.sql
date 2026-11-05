-- =============================================================================
-- تراجع الترحيل 012: يسحب رموز العرض ويحذف الفهرس. الرموز الجديدة تُحذف من كل الأدوار والموظفين
-- (الكود القديم لا يعرفها)، و audit.read تُسحب من مدير الفرع فقط.
-- =============================================================================

BEGIN;
DELETE FROM role_permissions WHERE role_code = 'branch_manager' AND permission = 'audit.read';
DELETE FROM role_permissions WHERE permission IN ('lab.view', 'radiology.view', 'pharmacy.view', 'notifications.branch_view', 'backups.view');
DELETE FROM user_permissions WHERE permission IN ('lab.view', 'radiology.view', 'pharmacy.view', 'notifications.branch_view', 'backups.view');
DROP INDEX IF EXISTS idx_notifications_branch;
COMMIT;
