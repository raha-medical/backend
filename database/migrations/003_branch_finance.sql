-- =============================================================================
-- ترحيل 003: ربط أموال الفروع
--   * patient_credit_ledger.branch_id: الفرع الذي قُبض فيه رصيد المريض أو استُخدم فيه،
--     لتقرير التسويات بين الفروع (رصيد يُقبض في فرع ويُستخدم في آخر)
--   * shifts.clinic_id: نقطة التحصيل للوردية (عيادة، مختبر، أشعة، صيدلية)
-- يتطلب الترحيلين 001 و 002. آمن على قاعدة فيها بيانات، ويمكن تشغيله أكثر من مرة.
-- التشغيل: psql "$DATABASE_URL" -f database/migrations/003_branch_finance.sql
-- =============================================================================

BEGIN;

ALTER TABLE patient_credit_ledger ADD COLUMN IF NOT EXISTS branch_id UUID REFERENCES branches(branch_id);

-- تعبئة الحركات السابقة من مصدرها: الدفعة، ثم الاسترجاع، ثم الحجز، ثم الدفع الإلكتروني، ثم فرع الموظف
UPDATE patient_credit_ledger l SET branch_id = p.branch_id
  FROM payments p WHERE l.branch_id IS NULL AND l.payment_id = p.payment_id;
UPDATE patient_credit_ledger l SET branch_id = i.branch_id
  FROM refunds r JOIN invoice_items ii ON ii.invoice_item_id = r.invoice_item_id JOIN invoices i ON i.invoice_id = ii.invoice_id
 WHERE l.branch_id IS NULL AND l.refund_id = r.refund_id;
UPDATE patient_credit_ledger l SET branch_id = s.branch_id
  FROM bookings b JOIN doctor_sessions s ON s.session_id = b.session_id
 WHERE l.branch_id IS NULL AND l.booking_id = b.booking_id;
UPDATE patient_credit_ledger l SET branch_id = s.branch_id
  FROM online_payments o JOIN bookings b ON b.booking_id = o.booking_id JOIN doctor_sessions s ON s.session_id = b.session_id
 WHERE l.branch_id IS NULL AND l.online_payment_id = o.online_payment_id;
UPDATE patient_credit_ledger l SET branch_id = i.branch_id
  FROM online_payments o JOIN invoices i ON i.invoice_id = o.invoice_id
 WHERE l.branch_id IS NULL AND l.online_payment_id = o.online_payment_id;
UPDATE patient_credit_ledger l SET branch_id = (SELECT ub.branch_id FROM user_branches ub WHERE ub.user_id = l.created_by LIMIT 1)
 WHERE l.branch_id IS NULL AND l.created_by IS NOT NULL;
UPDATE patient_credit_ledger SET branch_id = (SELECT branch_id FROM branches ORDER BY code LIMIT 1)
 WHERE branch_id IS NULL;

ALTER TABLE patient_credit_ledger ALTER COLUMN branch_id SET NOT NULL;
CREATE INDEX IF NOT EXISTS idx_credit_branch ON patient_credit_ledger(branch_id, created_at);

ALTER TABLE shifts ADD COLUMN IF NOT EXISTS clinic_id UUID REFERENCES clinics(clinic_id);

COMMIT;
