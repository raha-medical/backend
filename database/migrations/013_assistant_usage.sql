-- =============================================================================
-- ترحيل 013: عداد رسائل المساعد الذكي لكل حساب مريض (حد الاستخدام بالساعة).
-- لا يُخزن نص أي رسالة هنا. إضافة آمنة، ويمكن تشغيله أكثر من مرة.
-- التراجع: down/013_assistant_usage_down.sql
-- =============================================================================

BEGIN;

CREATE TABLE IF NOT EXISTS assistant_usage (
    patient_account_id  UUID NOT NULL REFERENCES patient_accounts(patient_account_id) ON DELETE CASCADE,
    window_start        TIMESTAMPTZ NOT NULL,                  -- بداية الساعة
    messages            INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (patient_account_id, window_start)
);

COMMIT;
