-- =============================================================================
-- ترحيل 005: الجزء الثاني (العيادة والمختبر والأشعة والصيدلية وتطبيق المريض والتكاملات)
--   * medical_orders: فرع الطلب، الطبيب المحيل من خارج المجمع، وقت سحب العينة، الإلغاء والتعديل
--   * prescriptions.dispensed_branch_id: الفرع الذي صُرفت فيه الوصفة
--   * receipt_blocks.last_used: آخر رقم مستخدم عند إعادة الكتلة
--   * online_payments.provider: بوابة تجريبية (sandbox) و FIB
--   * outbound_messages: الرسائل الصادرة (SMS و Push)، وفي الوضع التجريبي تُسجل فقط
--   * bookings.reminder_sent_at: تذكير الموعد يُرسل مرة واحدة
--   * إعداد booking.online_deposit_amount (عربون الحجز من التطبيق)
-- يتطلب الترحيلات 001-004. آمن على قاعدة فيها بيانات، ويمكن تشغيله أكثر من مرة.
-- التشغيل: psql "$DATABASE_URL" -f database/migrations/005_clinical_and_patient_app.sql
-- =============================================================================

BEGIN;

ALTER TABLE medical_orders ADD COLUMN IF NOT EXISTS branch_id             UUID REFERENCES branches(branch_id);
ALTER TABLE medical_orders ADD COLUMN IF NOT EXISTS referring_doctor_name VARCHAR(150);
ALTER TABLE medical_orders ADD COLUMN IF NOT EXISTS sample_collected_at   TIMESTAMPTZ;
ALTER TABLE medical_orders ADD COLUMN IF NOT EXISTS collected_by          UUID REFERENCES users(user_id);
ALTER TABLE medical_orders ADD COLUMN IF NOT EXISTS amended_at            TIMESTAMPTZ;
ALTER TABLE medical_orders ADD COLUMN IF NOT EXISTS cancel_reason         TEXT;
UPDATE medical_orders m SET branch_id = i.branch_id
  FROM invoice_items ii JOIN invoices i ON i.invoice_id = ii.invoice_id
 WHERE m.branch_id IS NULL AND ii.invoice_item_id = m.invoice_item_id;
ALTER TABLE medical_orders ALTER COLUMN branch_id SET NOT NULL;
-- قائمة العمل صارت لكل فرع: يُعاد بناء الفهرسين بتعريفهما الجديد
DROP INDEX IF EXISTS idx_orders_worklist;
DROP INDEX IF EXISTS idx_orders_patient;
CREATE INDEX idx_orders_worklist ON medical_orders(branch_id, department, status, created_at);
CREATE INDEX idx_orders_patient ON medical_orders(patient_id, created_at DESC);

ALTER TABLE prescriptions ADD COLUMN IF NOT EXISTS dispensed_branch_id UUID REFERENCES branches(branch_id);
CREATE INDEX IF NOT EXISTS idx_prescriptions_patient ON prescriptions(patient_id, issued_at DESC);

ALTER TABLE receipt_blocks ADD COLUMN IF NOT EXISTS last_used BIGINT;

ALTER TABLE online_payments DROP CONSTRAINT IF EXISTS online_payments_provider_check;
ALTER TABLE online_payments ADD CONSTRAINT online_payments_provider_check
    CHECK (provider IN ('zaincash', 'qicard', 'card', 'fib', 'sandbox'));

CREATE TABLE IF NOT EXISTS outbound_messages (
    message_id          UUID PRIMARY KEY,
    channel             VARCHAR(10) NOT NULL CHECK (channel IN ('sms', 'push')),
    recipient           VARCHAR(500) NOT NULL,                  -- رقم الهاتف أو توكن الجهاز
    patient_account_id  UUID REFERENCES patient_accounts(patient_account_id) ON DELETE SET NULL,
    user_id             UUID REFERENCES users(user_id) ON DELETE SET NULL,
    text                TEXT NOT NULL,
    status              VARCHAR(10) NOT NULL CHECK (status IN ('sandbox', 'sent', 'failed')),
    error               TEXT,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS idx_outbound_recipient ON outbound_messages(recipient, created_at DESC);

ALTER TABLE bookings ADD COLUMN IF NOT EXISTS reminder_sent_at TIMESTAMPTZ;

INSERT INTO system_settings (branch_id, key, value)
SELECT NULL, 'booking.online_deposit_amount', '5000'
 WHERE NOT EXISTS (SELECT 1 FROM system_settings WHERE branch_id IS NULL AND key = 'booking.online_deposit_amount');

COMMIT;
