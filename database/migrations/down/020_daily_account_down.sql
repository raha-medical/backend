-- =============================================================================
-- تراجع الترحيل 020.
-- =============================================================================

BEGIN;
DELETE FROM system_settings WHERE key = 'billing.collection_mode';
DROP TABLE IF EXISTS staff_liabilities;
ALTER TABLE shifts DROP COLUMN IF EXISTS auto_closed;
ALTER TABLE receipts DROP COLUMN IF EXISTS unprinted_reason;
ALTER TABLE receipts DROP COLUMN IF EXISTS printed_at;
DROP INDEX IF EXISTS idx_queue_checked_in_by;
ALTER TABLE queue_tickets DROP COLUMN IF EXISTS checked_in_by;
COMMIT;
