-- =============================================================================
-- تراجع الترحيل 018: التذاكر بانتظار قسم تُنهى (completed)، والعائدون تعود أولويتهم normal، ثم تُحذف الأعمدة والقيود.
-- =============================================================================

BEGIN;
UPDATE queue_tickets SET status = 'completed', completed_at = COALESCE(completed_at, now()) WHERE status = 'awaiting_results';
UPDATE queue_tickets SET priority = 'normal' WHERE priority = 'returning';
ALTER TABLE queue_tickets DROP CONSTRAINT IF EXISTS queue_tickets_status_check;
ALTER TABLE queue_tickets ADD CONSTRAINT queue_tickets_status_check CHECK (status IN (
    'waiting', 'arrived', 'called', 'in_room', 'completed', 'skipped', 'no_show', 'cancelled'));
ALTER TABLE queue_tickets DROP CONSTRAINT IF EXISTS queue_tickets_priority_check;
ALTER TABLE queue_tickets ADD CONSTRAINT queue_tickets_priority_check CHECK (priority IN ('normal', 'emergency', 'late_reinserted'));
DROP INDEX IF EXISTS idx_queue_free_return;
DROP INDEX IF EXISTS idx_queue_awaiting;
ALTER TABLE queue_tickets DROP COLUMN IF EXISTS original_ticket_id;
ALTER TABLE queue_tickets DROP COLUMN IF EXISTS free_return_until;
ALTER TABLE queue_tickets DROP COLUMN IF EXISTS returned_at;
ALTER TABLE queue_tickets DROP COLUMN IF EXISTS awaiting_since;
ALTER TABLE doctors DROP COLUMN IF EXISTS free_return_days;
ALTER TABLE clinics DROP COLUMN IF EXISTS free_return_days;
DELETE FROM system_settings WHERE key = 'queue.free_return_days';
COMMIT;
