-- =============================================================================
-- ترحيل 022: فوترة الصيدلية وسجل الصرف (التدقيق 6.2، 6.3-ب، 9.3-ب، خطة المالك المعتمدة)
--   * services.requires_prescription: دواء لا يُصرف للمراجع الخارجي إلا بصورة الوصفة أو ملاحظة إلزامية.
--   * prescription_lines: ربط السطر بدواء من الكتالوج (service_id) والسعر والكمية وبند الفاتورة.
--   * pharmacy_dispense_lines: سجل الصرف لكل سطر (وصفة داخلية أو مراجع خارجي): المراجع، الدواء، الكمية، السعر،
--     الطبيب المحوِّل أو «مراجع خارجي»، الموظف، والحالة.
--   * موظف الصيدلية يحصّل على حسابه اليومي: payments.collect لدور الصيدلي.
-- إضافات آمنة، ويمكن تشغيله أكثر من مرة. التراجع: down/022_pharmacy_billing_down.sql
-- =============================================================================

BEGIN;

ALTER TABLE services ADD COLUMN IF NOT EXISTS requires_prescription BOOLEAN NOT NULL DEFAULT FALSE;

ALTER TABLE prescription_lines ADD COLUMN IF NOT EXISTS service_id UUID REFERENCES services(service_id);
ALTER TABLE prescription_lines ADD COLUMN IF NOT EXISTS quantity SMALLINT;
ALTER TABLE prescription_lines ADD COLUMN IF NOT EXISTS unit_price BIGINT;
ALTER TABLE prescription_lines ADD COLUMN IF NOT EXISTS invoice_item_id UUID REFERENCES invoice_items(invoice_item_id);

CREATE TABLE IF NOT EXISTS pharmacy_dispense_lines (
    dispense_line_id      UUID PRIMARY KEY,
    branch_id             UUID NOT NULL REFERENCES branches(branch_id),
    patient_id            UUID NOT NULL REFERENCES patients(patient_id),
    prescription_id       UUID REFERENCES prescriptions(prescription_id),
    line_no               SMALLINT,
    invoice_id            UUID REFERENCES invoices(invoice_id),
    invoice_item_id       UUID REFERENCES invoice_items(invoice_item_id),
    service_id            UUID REFERENCES services(service_id),
    drug_name             VARCHAR(200) NOT NULL,
    quantity              SMALLINT NOT NULL DEFAULT 0 CHECK (quantity >= 0),
    unit_price            BIGINT NOT NULL DEFAULT 0 CHECK (unit_price >= 0),
    amount                BIGINT NOT NULL DEFAULT 0 CHECK (amount >= 0),
    -- billed: بانتظار الدفع والتسليم، dispensed: سُلِّم، not_available: غير متوفر، not_wanted: لم يرده المراجع
    status                VARCHAR(15) NOT NULL CHECK (status IN ('billed', 'dispensed', 'not_available', 'not_wanted')),
    is_external           BOOLEAN NOT NULL DEFAULT FALSE,
    doctor_id             UUID REFERENCES doctors(doctor_id),
    referring_doctor_name VARCHAR(150),
    prescription_file_id  UUID REFERENCES files(file_id),
    note                  TEXT,
    reason                TEXT,
    created_by            UUID REFERENCES users(user_id),
    created_at            TIMESTAMPTZ NOT NULL DEFAULT now(),
    dispensed_by          UUID REFERENCES users(user_id),
    dispensed_at          TIMESTAMPTZ
);
CREATE INDEX IF NOT EXISTS idx_pharmacy_lines_branch ON pharmacy_dispense_lines(branch_id, created_at);
CREATE INDEX IF NOT EXISTS idx_pharmacy_lines_rx ON pharmacy_dispense_lines(prescription_id) WHERE prescription_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_pharmacy_lines_invoice ON pharmacy_dispense_lines(invoice_id);

INSERT INTO role_permissions (role_code, permission) VALUES ('pharmacist', 'payments.collect')
ON CONFLICT (role_code, permission) DO NOTHING;

COMMIT;
