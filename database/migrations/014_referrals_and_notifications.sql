-- =============================================================================
-- ترحيل 014: المرحلة 5 - دورة التحويلات والإشعارات الموحدة
--   * جدول referrals: دورة حياة تحويل المراجع لخدمة (محوَّل، مفعّل، مكتمل، منتهي، خارجي، ملغى).
--     الدورة مستقلة عن حالة الطلب والدفع: انظر القسم 23 في العقد.
--   * notifications.priority: low (هادئ) | normal | high (حرج).
--   * medical_orders.critical_notified_at و critical_flagged: علامة الفني وعدم تكرار تنبيه الحرج.
--   * outbound_messages: حالة queued لمنفذ Push الحقيقي.
--   * إعدادات: referral.expiry_days و results.show_deferred_to_patient.
-- كل ما هو قائم يبقى ظاهراً: الطلبات المفتوحة تُسجَّل مفعّلة، والمعتمدة مكتملة.
-- يمكن تشغيله أكثر من مرة. التراجع: down/014_referrals_and_notifications_down.sql
-- =============================================================================

BEGIN;

CREATE TABLE IF NOT EXISTS referrals (
    referral_id         UUID PRIMARY KEY,
    branch_id           UUID NOT NULL REFERENCES branches(branch_id),
    patient_id          UUID NOT NULL REFERENCES patients(patient_id),
    service             VARCHAR(12) NOT NULL CHECK (service IN ('pharmacy', 'lab', 'radiology', 'ultrasound')),
    order_item_id       UUID UNIQUE REFERENCES medical_orders(order_item_id) ON DELETE CASCADE,
    prescription_id     UUID UNIQUE REFERENCES prescriptions(prescription_id) ON DELETE CASCADE,
    status              VARCHAR(10) NOT NULL DEFAULT 'referred' CHECK (status IN ('referred', 'activated', 'completed', 'expired', 'external', 'cancelled')),
    activation_method   VARCHAR(12) CHECK (activation_method IN ('scan', 'staff_search', 'patient_app', 'cashier', 'work', 'migration')),
    activated_at        TIMESTAMPTZ,
    activated_by        UUID REFERENCES users(user_id),
    completed_at        TIMESTAMPTZ,
    closed_at           TIMESTAMPTZ,
    expires_at          TIMESTAMPTZ NOT NULL,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT now(),
    CHECK ((order_item_id IS NULL) <> (prescription_id IS NULL))
);
CREATE INDEX IF NOT EXISTS idx_referrals_branch_service ON referrals(branch_id, service, status, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_referrals_patient ON referrals(patient_id, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_referrals_expiry ON referrals(expires_at) WHERE status = 'referred';

ALTER TABLE notifications ADD COLUMN IF NOT EXISTS priority VARCHAR(6) NOT NULL DEFAULT 'normal';
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'notifications_priority_check') THEN
        ALTER TABLE notifications ADD CONSTRAINT notifications_priority_check CHECK (priority IN ('low', 'normal', 'high'));
    END IF;
END $$;

ALTER TABLE medical_orders ADD COLUMN IF NOT EXISTS critical_flagged BOOLEAN NOT NULL DEFAULT FALSE;
ALTER TABLE medical_orders ADD COLUMN IF NOT EXISTS critical_notified_at TIMESTAMPTZ;

ALTER TABLE outbound_messages DROP CONSTRAINT IF EXISTS outbound_messages_status_check;
ALTER TABLE outbound_messages ADD CONSTRAINT outbound_messages_status_check CHECK (status IN ('sandbox', 'queued', 'sent', 'failed'));

INSERT INTO system_settings (branch_id, key, value)
SELECT NULL, v.key, v.value::jsonb FROM (VALUES ('referral.expiry_days', '7'), ('results.show_deferred_to_patient', '0')) AS v(key, value)
WHERE NOT EXISTS (SELECT 1 FROM system_settings s WHERE s.branch_id IS NULL AND s.key = v.key);

-- تعبئة ما هو قائم: لا يختفي شيء من قوائم العمل
INSERT INTO referrals (referral_id, branch_id, patient_id, service, order_item_id, status, activation_method, activated_at, completed_at, closed_at, expires_at, created_at)
SELECT gen_random_uuid(), m.branch_id, m.patient_id,
       CASE WHEN m.department = 'lab' THEN 'lab' WHEN s.modality = 'ultrasound' THEN 'ultrasound' ELSE 'radiology' END,
       m.order_item_id,
       CASE WHEN m.status = 'released' THEN 'completed' WHEN m.status = 'ordered' THEN 'referred' ELSE 'activated' END,
       CASE WHEN m.status = 'ordered' THEN NULL ELSE 'migration' END,
       CASE WHEN m.status = 'ordered' THEN NULL ELSE m.created_at END,
       CASE WHEN m.status = 'released' THEN m.released_at END,
       CASE WHEN m.status = 'released' THEN m.released_at END,
       now() + interval '7 days', m.created_at
  FROM medical_orders m JOIN services s ON s.service_id = m.service_id
 WHERE m.status <> 'cancelled'
ON CONFLICT DO NOTHING;

INSERT INTO referrals (referral_id, branch_id, patient_id, service, prescription_id, status, completed_at, closed_at, expires_at, created_at)
SELECT gen_random_uuid(), COALESCE(p.dispensed_branch_id, t.branch_id), p.patient_id, 'pharmacy', p.prescription_id,
       CASE WHEN p.status = 'dispensed' THEN 'completed' WHEN p.expires_at <= now() THEN 'expired' ELSE 'referred' END,
       p.dispensed_at,
       CASE WHEN p.status = 'dispensed' THEN p.dispensed_at WHEN p.expires_at <= now() THEN p.expires_at END,
       p.expires_at, p.issued_at
  FROM prescriptions p
  JOIN consultations c ON c.consultation_id = p.consultation_id
  JOIN queue_tickets t ON t.ticket_id = c.ticket_id
ON CONFLICT DO NOTHING;

COMMIT;
