-- =============================================================================
-- ترحيل 023: الدينار والدولار (التدقيق 7.2، خطة المالك المعتمدة)
--   * services.currency: IQD (افتراضي) أو USD. سعر خدمة USD بالدولار، ويتحول للدينار بسعر اليوم عند إنشاء البند.
--   * invoice_items.currency / original_unit_price / exchange_rate: سعر البند الأصلي بعملته وسعر الصرف المستخدم.
--   * exchange_rates: سعر صرف اليوم لكل فرع (دينار لكل دولار) يحدده محاسب الفرع.
--   * payment_tenders: الطريقة cash_usd (نقد بالدولار) مع العملة والمبلغ الأصلي بالدولار والسعر المستخدم.
--     amount يبقى المقابل بالدينار المطبَّق على البنود (بعد الفكة)، فلا تتغير المجاميع والتقارير.
--   * payments.change_iqd: الفكة المعادة بالدينار دائماً.
--   * refunds: الطريقة cash_usd، و original_amount = المبلغ المسترجع بالدولار بسعر الدفع الأصلي (لا بسعر يوم الاسترجاع).
--   * shifts: نقد الدولار مفصول (المتوقع والمعدود والفرق).
-- إضافات آمنة، ويمكن تشغيله أكثر من مرة. التراجع: down/023_currencies_down.sql
-- =============================================================================

BEGIN;

ALTER TABLE services ADD COLUMN IF NOT EXISTS currency VARCHAR(3) NOT NULL DEFAULT 'IQD';
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_services_currency') THEN
        ALTER TABLE services ADD CONSTRAINT ck_services_currency CHECK (currency IN ('IQD', 'USD'));
    END IF;
END $$;

ALTER TABLE invoice_items ADD COLUMN IF NOT EXISTS currency VARCHAR(3) NOT NULL DEFAULT 'IQD';
ALTER TABLE invoice_items ADD COLUMN IF NOT EXISTS original_unit_price BIGINT;
ALTER TABLE invoice_items ADD COLUMN IF NOT EXISTS exchange_rate INTEGER;

CREATE TABLE IF NOT EXISTS exchange_rates (
    branch_id       UUID NOT NULL REFERENCES branches(branch_id),
    rate_date       DATE NOT NULL,
    usd_to_iqd      INTEGER NOT NULL CHECK (usd_to_iqd BETWEEN 100 AND 10000),
    set_by          UUID REFERENCES users(user_id),
    set_at          TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (branch_id, rate_date)
);

ALTER TABLE payment_tenders ADD COLUMN IF NOT EXISTS currency VARCHAR(3) NOT NULL DEFAULT 'IQD';
ALTER TABLE payment_tenders ADD COLUMN IF NOT EXISTS original_amount BIGINT;
ALTER TABLE payment_tenders ADD COLUMN IF NOT EXISTS exchange_rate INTEGER;
DO $$
DECLARE c TEXT;
BEGIN
    FOR c IN SELECT conname FROM pg_constraint WHERE conrelid = 'payment_tenders'::regclass AND contype = 'c'
              AND pg_get_constraintdef(oid) LIKE '%''online''%' AND pg_get_constraintdef(oid) NOT LIKE '%pos_reference%' LOOP
        EXECUTE format('ALTER TABLE payment_tenders DROP CONSTRAINT %I', c);
    END LOOP;
END $$;
ALTER TABLE payment_tenders ADD CONSTRAINT ck_tenders_method CHECK (method IN ('cash', 'pos', 'online', 'cash_usd'));
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_tenders_usd') THEN
        ALTER TABLE payment_tenders ADD CONSTRAINT ck_tenders_usd
            CHECK ((method = 'cash_usd') = (currency = 'USD') AND (currency = 'IQD' OR (original_amount > 0 AND exchange_rate > 0)));
    END IF;
END $$;

ALTER TABLE payments ADD COLUMN IF NOT EXISTS change_iqd BIGINT NOT NULL DEFAULT 0;

ALTER TABLE refunds ADD COLUMN IF NOT EXISTS original_amount BIGINT;
DO $$
DECLARE c TEXT;
BEGIN
    FOR c IN SELECT conname FROM pg_constraint WHERE conrelid = 'refunds'::regclass AND contype = 'c'
              AND pg_get_constraintdef(oid) LIKE '%patient_credit%' LOOP
        EXECUTE format('ALTER TABLE refunds DROP CONSTRAINT %I', c);
    END LOOP;
END $$;
ALTER TABLE refunds ADD CONSTRAINT ck_refunds_method CHECK (method IN ('cash', 'pos', 'online_reversal', 'patient_credit', 'cash_usd'));

ALTER TABLE shifts ADD COLUMN IF NOT EXISTS declared_usd BIGINT;
ALTER TABLE shifts ADD COLUMN IF NOT EXISTS expected_usd BIGINT;
ALTER TABLE shifts ADD COLUMN IF NOT EXISTS variance_usd BIGINT;

COMMIT;
