-- =============================================================================
-- تراجع الترحيل 019: الدفعات الملغاة تصبح failed، ثم تُحذف الأعمدة وجدول البوابة التجريبية.
-- =============================================================================

BEGIN;
UPDATE online_payments SET status = 'failed' WHERE status = 'cancelled';
ALTER TABLE online_payments DROP CONSTRAINT IF EXISTS online_payments_status_check;
ALTER TABLE online_payments ADD CONSTRAINT online_payments_status_check
    CHECK (status IN ('pending', 'succeeded', 'failed', 'expired', 'refunded'));
DROP INDEX IF EXISTS idx_online_payments_pending;
ALTER TABLE online_payments DROP COLUMN IF EXISTS failure_code;
ALTER TABLE online_payments DROP COLUMN IF EXISTS is_sandbox;
ALTER TABLE online_payments DROP COLUMN IF EXISTS provider_reference;
DROP TABLE IF EXISTS mock_gateway_transactions;
COMMIT;
