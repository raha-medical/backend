-- =============================================================================
-- ترحيل 019: طبقة بوابات الدفع الموحدة والبوابة التجريبية لزين كاش وكي كارد (التدقيق 3.5)
--   * online_payments: حالة جديدة cancelled (المراجع ألغى)، ومرجع المعاملة لدى البوابة provider_reference،
--     وعلامة is_sandbox (للإدارة فقط)، وسبب الفشل failure_code.
--   * mock_gateway_transactions: جانب «البوابة» في الوضع التجريبي (بديل خادم زين كاش / كي كارد).
--     النظام لا يقرأ هذا الجدول إلا عبر واجهة البوابة (InquireAsync)، كما مع البوابة الحقيقية. لا يُحفظ PIN ولا CVV.
-- إضافات آمنة، ويمكن تشغيله أكثر من مرة. التراجع: down/019_payment_gateways_down.sql
-- =============================================================================

BEGIN;

ALTER TABLE online_payments DROP CONSTRAINT IF EXISTS online_payments_status_check;
ALTER TABLE online_payments ADD CONSTRAINT online_payments_status_check
    CHECK (status IN ('pending', 'succeeded', 'failed', 'expired', 'refunded', 'cancelled'));
ALTER TABLE online_payments ADD COLUMN IF NOT EXISTS provider_reference VARCHAR(60);
ALTER TABLE online_payments ADD COLUMN IF NOT EXISTS is_sandbox BOOLEAN NOT NULL DEFAULT FALSE;
ALTER TABLE online_payments ADD COLUMN IF NOT EXISTS failure_code VARCHAR(40);
CREATE INDEX IF NOT EXISTS idx_online_payments_pending ON online_payments(status, expires_at) WHERE status IN ('pending', 'expired');

CREATE TABLE IF NOT EXISTS mock_gateway_transactions (
    reference           VARCHAR(60) PRIMARY KEY,
    provider            VARCHAR(20) NOT NULL CHECK (provider IN ('zaincash', 'qicard')),
    online_payment_id   UUID NOT NULL,
    amount              BIGINT NOT NULL CHECK (amount > 0),
    currency            VARCHAR(3) NOT NULL DEFAULT 'IQD',
    status              VARCHAR(10) NOT NULL DEFAULT 'pending' CHECK (status IN ('pending', 'paid', 'failed', 'cancelled', 'refunded')),
    step                VARCHAR(12) NOT NULL DEFAULT 'credentials' CHECK (step IN ('credentials', 'otp', 'processing', 'done')),
    test_account        VARCHAR(30),
    account_masked      VARCHAR(30),
    failure_code        VARCHAR(40),
    secret_attempts     SMALLINT NOT NULL DEFAULT 0,
    otp_attempts        SMALLINT NOT NULL DEFAULT 0,
    otp_sends           SMALLINT NOT NULL DEFAULT 0,
    otp_expires_at      TIMESTAMPTZ,
    expires_at          TIMESTAMPTZ NOT NULL,
    transaction_id      VARCHAR(60) UNIQUE,
    refunded_amount     BIGINT NOT NULL DEFAULT 0,
    paid_at             TIMESTAMPTZ,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS idx_mock_gateway_op ON mock_gateway_transactions(online_payment_id);

COMMIT;
