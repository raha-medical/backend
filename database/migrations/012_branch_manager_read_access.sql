-- =============================================================================
-- ترحيل 012: مدير الفرع يرى كل فرعه للقراءة (عيب D3، مواصفة 2.2)
--   * رموز عرض جديدة: lab.view، radiology.view، pharmacy.view، notifications.branch_view، backups.view
--     و audit.read (سجل التدقيق بنطاق فروعه). لا صلاحية كتابة جديدة.
--   * فهرس لإشعارات الفرع.
-- إضافات آمنة، ويمكن تشغيله أكثر من مرة. التراجع: down/012_branch_manager_read_access_down.sql
-- =============================================================================

BEGIN;

INSERT INTO role_permissions (role_code, permission) VALUES
    ('branch_manager', 'lab.view'), ('branch_manager', 'radiology.view'), ('branch_manager', 'pharmacy.view'),
    ('branch_manager', 'notifications.branch_view'), ('branch_manager', 'backups.view'), ('branch_manager', 'audit.read')
ON CONFLICT (role_code, permission) DO NOTHING;

CREATE INDEX IF NOT EXISTS idx_notifications_branch ON notifications(branch_id, created_at DESC) WHERE user_id IS NOT NULL;

COMMIT;
