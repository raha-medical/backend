-- تراجع الترحيل 014: يحذف جدول التحويلات والأعمدة المضافة. الطلبات والوصفات نفسها لا تُمس.
BEGIN;
DROP TABLE IF EXISTS referrals;
ALTER TABLE notifications DROP CONSTRAINT IF EXISTS notifications_priority_check;
ALTER TABLE notifications DROP COLUMN IF EXISTS priority;
ALTER TABLE medical_orders DROP COLUMN IF EXISTS critical_flagged;
ALTER TABLE medical_orders DROP COLUMN IF EXISTS critical_notified_at;
UPDATE outbound_messages SET status = 'failed', error = COALESCE(error, 'تراجع الترحيل 014') WHERE status = 'queued';
ALTER TABLE outbound_messages DROP CONSTRAINT IF EXISTS outbound_messages_status_check;
ALTER TABLE outbound_messages ADD CONSTRAINT outbound_messages_status_check CHECK (status IN ('sandbox', 'sent', 'failed'));
DELETE FROM system_settings WHERE branch_id IS NULL AND key IN ('referral.expiry_days', 'results.show_deferred_to_patient');
COMMIT;
