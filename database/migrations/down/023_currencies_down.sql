-- =============================================================================
-- تراجع الترحيل 023. يُرفض إذا وُجدت دفعات أو استرجاعات بالدولار (احذفها أو أبقِ الترحيل).
-- =============================================================================

BEGIN;
ALTER TABLE shifts DROP COLUMN IF EXISTS variance_usd;
ALTER TABLE shifts DROP COLUMN IF EXISTS expected_usd;
ALTER TABLE shifts DROP COLUMN IF EXISTS declared_usd;

ALTER TABLE refunds DROP CONSTRAINT IF EXISTS ck_refunds_method;
ALTER TABLE refunds ADD CONSTRAINT ck_refunds_method CHECK (method IN ('cash', 'pos', 'online_reversal', 'patient_credit'));
ALTER TABLE refunds DROP COLUMN IF EXISTS original_amount;

ALTER TABLE payments DROP COLUMN IF EXISTS change_iqd;

ALTER TABLE payment_tenders DROP CONSTRAINT IF EXISTS ck_tenders_usd;
ALTER TABLE payment_tenders DROP CONSTRAINT IF EXISTS ck_tenders_method;
ALTER TABLE payment_tenders ADD CONSTRAINT ck_tenders_method CHECK (method IN ('cash', 'pos', 'online'));
ALTER TABLE payment_tenders DROP COLUMN IF EXISTS exchange_rate;
ALTER TABLE payment_tenders DROP COLUMN IF EXISTS original_amount;
ALTER TABLE payment_tenders DROP COLUMN IF EXISTS currency;

DROP TABLE IF EXISTS exchange_rates;

ALTER TABLE invoice_items DROP COLUMN IF EXISTS exchange_rate;
ALTER TABLE invoice_items DROP COLUMN IF EXISTS original_unit_price;
ALTER TABLE invoice_items DROP COLUMN IF EXISTS currency;

ALTER TABLE services DROP CONSTRAINT IF EXISTS ck_services_currency;
ALTER TABLE services DROP COLUMN IF EXISTS currency;
COMMIT;
