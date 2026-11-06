-- =============================================================================
-- ترحيل 021: الجلسة 4 (التدقيق: قسم الدعم، 1.2-ج3، 5.1-ب)
--   * files: غرض جديد external_result (صورة نتيجة خارجية مربوطة بالدور والمريض) و prescription (صورة وصفة خارجية للصيدلية).
--   * branches.whatsapp: رقم واتساب الفرع لشاشة «تواصل مع المجمع».
--   * faq_items: الأسئلة الشائعة (قراءة عامة، إدارة بصلاحية support.manage) مع عناصر أولية منها سياسات الاسترجاع 5.2-هـ.
--   * support_requests: طلبات الدعم والشكاوى من تطبيق المراجع، ورد الموظف.
--   * bookings.cancel_resolution: اختيار المراجع بعد إلغاء الطبيب جدوله (credit | rebooked | refunded) و rebooked_booking_id.
--   * patient_credit_ledger.kind: booking_rebook (نقل رصيد الحجز الملغى إلى الحجز الجديد) و left_before_entry (دفع نقداً وغادر).
--   * الصلاحية support.manage لمدير الفرع.
-- إضافات آمنة، ويمكن تشغيله أكثر من مرة. التراجع: down/021_support_and_patient_options_down.sql
-- =============================================================================

BEGIN;

-- ---------------------------------------------------------------- الملفات
ALTER TABLE files ADD COLUMN IF NOT EXISTS ticket_id UUID REFERENCES queue_tickets(ticket_id);
ALTER TABLE files ADD COLUMN IF NOT EXISTS patient_id UUID REFERENCES patients(patient_id);
CREATE INDEX IF NOT EXISTS idx_files_ticket ON files(ticket_id) WHERE ticket_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_files_patient ON files(patient_id) WHERE patient_id IS NOT NULL;
DO $$
DECLARE c TEXT;
BEGIN
    FOR c IN SELECT conname FROM pg_constraint WHERE conrelid = 'files'::regclass AND contype = 'c'
              AND pg_get_constraintdef(oid) LIKE '%purpose%' LOOP
        EXECUTE format('ALTER TABLE files DROP CONSTRAINT %I', c);
    END LOOP;
END $$;
ALTER TABLE files ADD CONSTRAINT ck_files_purpose
    CHECK (purpose IN ('lab', 'radiology', 'doctor_photo', 'external_result', 'prescription'));

-- ---------------------------------------------------------------- الفرع
ALTER TABLE branches ADD COLUMN IF NOT EXISTS whatsapp VARCHAR(30);

-- ---------------------------------------------------------------- الأسئلة الشائعة
CREATE TABLE IF NOT EXISTS faq_items (
    faq_id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    category        VARCHAR(20) NOT NULL CHECK (category IN ('booking', 'cancel', 'payment', 'refund', 'hours', 'app', 'other')),
    question_ar     VARCHAR(300) NOT NULL,
    answer_ar       TEXT NOT NULL,
    sort            INTEGER NOT NULL DEFAULT 0,
    is_active       BOOLEAN NOT NULL DEFAULT TRUE,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS uq_faq_question ON faq_items(question_ar);
CREATE INDEX IF NOT EXISTS idx_faq_active ON faq_items(category, sort) WHERE is_active;

INSERT INTO faq_items (category, question_ar, answer_ar, sort) VALUES
    ('booking', 'كيف أحجز موعداً؟', 'اختر الفرع ثم الطبيب ثم اليوم المتاح، وأكّد الحجز. يصلك إشعار بموعدك ورقمك، ويمكنك الدفع إلكترونياً أو عند الوصول.', 10),
    ('booking', 'كيف أؤكد حضوري عند الوصول؟', 'امسح رمز QR الظاهر على شاشة الانتظار من التطبيق، أو راجع الاستقبال.', 20),
    ('cancel', 'إذا ألغى الطبيب جدوله، ماذا يحدث لمبلغي؟', 'يُضاف ما دفعته رصيداً في محفظتك فوراً. يمكنك استخدامه لأي حجز، أو طلب استرجاعه إلى وسيلة الدفع نفسها، أو نقل موعدك إلى أقرب موعد للطبيب نفسه.', 30),
    ('cancel', 'إذا ألغيت حجزي بنفسي؟', 'خلال مدة الإلغاء المجاني يعود المبلغ رصيداً. بعدها قد تُطبَّق رسوم الإلغاء المتأخر الظاهرة عند الإلغاء.', 40),
    ('refund', 'إذا دفعت ولم أحضر؟', 'يتحول المبلغ رصيداً في محفظتك تستخدمه لاحقاً، ولا يُسترجع نقداً.', 50),
    ('refund', 'كيف يتم الاسترجاع النقدي من الاستقبال؟', 'يتم بطلب من الموظف وموافقة محاسب الفرع، مع ذكر السبب، ويصدر لك وصل استرجاع.', 60),
    ('payment', 'ما طرق الدفع المتاحة؟', 'نقداً أو بالبطاقة في الاستقبال، أو إلكترونياً من التطبيق عبر زين كاش أو كي كارد.', 70),
    ('hours', 'ما أوقات الدوام؟', 'أوقات دوام كل فرع ظاهرة في صفحة «تواصل مع المجمع» وفي صفحة الفرع.', 80)
ON CONFLICT (question_ar) DO NOTHING;

-- ---------------------------------------------------------------- طلبات الدعم
CREATE TABLE IF NOT EXISTS support_requests (
    support_request_id  UUID PRIMARY KEY,
    patient_account_id  UUID NOT NULL REFERENCES patient_accounts(patient_account_id),
    patient_id          UUID REFERENCES patients(patient_id),
    booking_id          UUID REFERENCES bookings(booking_id),
    branch_id           UUID REFERENCES branches(branch_id),
    type                VARCHAR(10) NOT NULL CHECK (type IN ('booking', 'payment', 'refund', 'app', 'other')),
    description         TEXT NOT NULL,
    status              VARCHAR(10) NOT NULL DEFAULT 'open' CHECK (status IN ('open', 'replied', 'closed')),
    reply               TEXT,
    replied_by          UUID REFERENCES users(user_id),
    replied_at          TIMESTAMPTZ,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT now(),
    CHECK ((status = 'open') = (replied_at IS NULL))
);
CREATE INDEX IF NOT EXISTS idx_support_account ON support_requests(patient_account_id, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_support_status ON support_requests(status, created_at);

-- ---------------------------------------------------------------- خيارات المراجع بعد إلغاء الطبيب (5.1-ب)
ALTER TABLE bookings ADD COLUMN IF NOT EXISTS cancel_resolution VARCHAR(10);
ALTER TABLE bookings ADD COLUMN IF NOT EXISTS rebooked_booking_id UUID REFERENCES bookings(booking_id);
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_bookings_cancel_resolution') THEN
        ALTER TABLE bookings ADD CONSTRAINT ck_bookings_cancel_resolution
            CHECK (cancel_resolution IS NULL OR cancel_resolution IN ('credit', 'rebooked', 'refunded'));
    END IF;
END $$;

DO $$
DECLARE c TEXT;
BEGIN
    FOR c IN SELECT conname FROM pg_constraint WHERE conrelid = 'patient_credit_ledger'::regclass AND contype = 'c'
              AND pg_get_constraintdef(oid) LIKE '%booking_deposit%' LOOP
        EXECUTE format('ALTER TABLE patient_credit_ledger DROP CONSTRAINT %I', c);
    END LOOP;
END $$;
ALTER TABLE patient_credit_ledger ADD CONSTRAINT ck_credit_kind CHECK (kind IN (
    'unban_deposit', 'booking_deposit', 'booking_cancel_credit', 'refund_credit', 'applied_to_payment', 'refunded_out',
    'booking_rebook', 'left_before_entry'));

-- ---------------------------------------------------------------- الصلاحية
INSERT INTO role_permissions (role_code, permission) VALUES ('branch_manager', 'support.manage')
ON CONFLICT (role_code, permission) DO NOTHING;

COMMIT;
