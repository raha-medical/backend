-- =============================================================================
-- قاعدة بيانات نظام مجمع الرحمة الطبي - الإصدار 5
-- Al-Rahma Medical Complex - Database Schema v5
-- PostgreSQL 16
-- مطابقة لـ: وثيقة المتطلبات v2.1 + عقود الـ API v5
--
-- أهم الفروقات عن نسخة v4:
--   * المعرفات UUID (يولدها التطبيق كـ UUIDv7، أو القاعدة احتياطياً) بدل SERIAL،
--     لتعمل الواجهة أوفلاين دون جدول تحويل للمعرفات المؤقتة.
--   * المبالغ BIGINT بالدينار (بدون كسور) بدل NUMERIC(12,2).
--   * أرقام مقروءة للعرض: file_number, invoice_number, receipt_number, ticket_number.
--   * جداول جديدة: العيادات، فروع ودوائر الموظفين، تاريخ نسب الأطباء، جداول الدوام
--     والوجبات، الحجوزات، حسابات المرضى (OTP)، الحظر والغياب، الطلبات الطبية،
--     نتائج المختبر المهيكلة، تقارير الأشعة، الملفات، الدفعات والإيصالات، الاسترجاع،
--     رصيد المريض، الدفع الإلكتروني، الدفع الآجل، الورديات، كتل الإيصالات،
--     الأجهزة، المزامنة، منع التكرار، الإشعارات وأجهزة Push، الإعدادات.
--   * الصيدلية Phase 1: وصفة مهيكلة تُصرف مرة واحدة. جدول المخزون باقٍ لـ Phase 2.
--
-- الترحيلات اللاحقة لقاعدة قائمة: database/migrations/ (هذا الملف يتضمنها أصلاً، حتى 016).
--
-- تحذير: هذا السكربت يحذف الجداول ويعيد إنشاءها. لا تشغله على قاعدة فيها بيانات حقيقية.
-- =============================================================================

BEGIN;

DROP SCHEMA IF EXISTS public CASCADE;
CREATE SCHEMA public;

-- =============================================================================
-- 1. الفروع والعيادات والإعدادات
-- =============================================================================

CREATE TABLE branches (
    branch_id       UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    code            VARCHAR(10)  NOT NULL UNIQUE,              -- مثل B1، يدخل في الأرقام المقروءة
    name            VARCHAR(100) NOT NULL,
    city            VARCHAR(100),
    address         VARCHAR(255),
    phone           VARCHAR(30),
    latitude        NUMERIC(9,6),                               -- موقع الفرع (أُضيف في الترحيل 002)
    longitude       NUMERIC(9,6),
    working_hours   VARCHAR(255),                               -- نص أوقات الدوام للعرض (الجدول المهيكل في branch_working_hours)
    is_active       BOOLEAN NOT NULL DEFAULT TRUE,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT ck_branches_location CHECK (                     -- الترحيل 010
        (latitude IS NULL AND longitude IS NULL)
        OR (latitude BETWEEN -90 AND 90 AND longitude BETWEEN -180 AND 180))
);

-- أوقات الدوام المهيكلة (الترحيل 010): فترة أو فترتان لكل يوم، واليوم بلا صفوف مغلق.
CREATE TABLE branch_working_hours (
    branch_id       UUID NOT NULL REFERENCES branches(branch_id) ON DELETE CASCADE,
    weekday         SMALLINT NOT NULL CHECK (weekday BETWEEN 0 AND 6),     -- 0 = الأحد (مثل جداول الأطباء)
    shift_no        SMALLINT NOT NULL CHECK (shift_no BETWEEN 1 AND 2),    -- فترة صباحية ومسائية
    opens_at        TIME NOT NULL,                                         -- بتوقيت بغداد
    closes_at       TIME NOT NULL,
    PRIMARY KEY (branch_id, weekday, shift_no),
    CHECK (closes_at > opens_at)
);

CREATE TABLE clinics (
    clinic_id       UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    branch_id       UUID NOT NULL REFERENCES branches(branch_id),
    name            VARCHAR(100) NOT NULL,
    department      VARCHAR(20)  NOT NULL CHECK (department IN ('clinic', 'lab', 'radiology', 'pharmacy')),
    floor           SMALLINT,
    is_active       BOOLEAN NOT NULL DEFAULT TRUE,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    free_return_days SMALLINT CHECK (free_return_days IS NULL OR free_return_days BETWEEN 0 AND 90)   -- ترحيل 018: فارغ = إعداد الفرع
);
CREATE INDEX idx_clinics_branch ON clinics(branch_id);

-- الإغلاق المؤقت للعيادات والأقسام (سفر، صيانة، عطلة...). أُضيف في الترحيل 001.
CREATE TABLE clinic_closures (
    closure_id      UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    clinic_id       UUID NOT NULL REFERENCES clinics(clinic_id),
    reason          VARCHAR(50) NOT NULL,                       -- lookup_values.code (category = closure_reason)
    note            TEXT,
    starts_on       DATE NOT NULL,                              -- بتوقيت بغداد
    ends_on         DATE,                                       -- NULL = حتى إعادة الفتح يدوياً
    created_by      UUID,                                       -- FK يضاف بعد إنشاء users
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    ended_at        TIMESTAMPTZ,                                -- أُعيد الفتح مبكراً أو أُلغي الإغلاق
    ended_by        UUID,
    CHECK (ends_on IS NULL OR ends_on >= starts_on)
);
CREATE INDEX idx_clinic_closures_clinic ON clinic_closures(clinic_id, starts_on);

-- الإغلاق المؤقت للفرع كاملاً (صيانة، عطلة، افتتاح قريب...). أُضيف في الترحيل 002.
CREATE TABLE branch_closures (
    closure_id      UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    branch_id       UUID NOT NULL REFERENCES branches(branch_id),
    reason          VARCHAR(50) NOT NULL,                       -- lookup_values.code (category = closure_reason)
    note            TEXT,
    starts_on       DATE NOT NULL,                              -- بتوقيت بغداد
    ends_on         DATE,                                       -- NULL = حتى إعادة الفتح يدوياً
    created_by      UUID,                                       -- FK يضاف بعد إنشاء users
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    ended_at        TIMESTAMPTZ,
    ended_by        UUID,
    CHECK (ends_on IS NULL OR ends_on >= starts_on)
);
CREATE INDEX idx_branch_closures_branch ON branch_closures(branch_id, starts_on);

-- قوائم الاختيار التي يديرها المدير (أسباب الإغلاق، الاختصاصات، العناوين الوظيفية...)
CREATE TABLE lookup_values (
    lookup_id       UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    category        VARCHAR(50) NOT NULL,                       -- closure_reason | doctor_specialty | doctor_job_title
    code            VARCHAR(50) NOT NULL,                       -- ثابت لا يتغير (يُخزن في السجلات)
    name_ar         VARCHAR(100) NOT NULL,                      -- الاسم الظاهر، يمكن تعديله
    sort_order      INT NOT NULL DEFAULT 0,
    is_active       BOOLEAN NOT NULL DEFAULT TRUE,
    is_system       BOOLEAN NOT NULL DEFAULT FALSE,             -- يعتمد عليه النظام: لا يُوقف
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (category, code)
);
CREATE UNIQUE INDEX uq_lookup_values_name ON lookup_values(category, name_ar);

-- إعدادات قابلة للضبط (حد الحظر، فترة المراجعة، حد الاسترجاع...). branch_id = NULL تعني عام.
CREATE TABLE system_settings (
    setting_id      UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    branch_id       UUID REFERENCES branches(branch_id),
    key             VARCHAR(100) NOT NULL,
    value           JSONB NOT NULL,
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX uq_settings_key ON system_settings(key, COALESCE(branch_id, '00000000-0000-0000-0000-000000000000'::uuid));

-- عدادات الأرقام المقروءة لكل فرع (ملف، فاتورة، إيصال، استرجاع، تذكرة...)
CREATE TABLE number_sequences (
    branch_id       UUID NOT NULL REFERENCES branches(branch_id),
    kind            VARCHAR(30) NOT NULL,
    period          VARCHAR(10) NOT NULL DEFAULT 'all',        -- 'all' أو تاريخ اليوم لأرقام التذاكر
    next_value      BIGINT NOT NULL DEFAULT 1,
    PRIMARY KEY (branch_id, kind, period)
);

-- =============================================================================
-- 2. المستخدمون والصلاحيات والأجهزة
-- =============================================================================

-- الأدوار (الترحيل 009): الثمانية أدوار نظام، ويضيف المدير العام أدواراً مخصصة. الصلاحيات في role_permissions.
CREATE TABLE roles (
    code            VARCHAR(30) PRIMARY KEY CHECK (code ~ '^[a-z][a-z0-9_]{2,29}$'),
    name_ar         VARCHAR(100) NOT NULL,
    description     TEXT,
    home_section    VARCHAR(30),                                -- قسم الواجهة الأول للأدوار المخصصة (null = يُشتق)
    is_system       BOOLEAN NOT NULL DEFAULT FALSE,             -- أدوار النظام: لا تُحذف ولا تُوقف ولا يتغير رمزها
    is_active       BOOLEAN NOT NULL DEFAULT TRUE,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    created_by      UUID,                                       -- FK يضاف بعد إنشاء users
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_by      UUID
);
CREATE UNIQUE INDEX uq_roles_name ON roles(name_ar);

CREATE TABLE users (
    user_id               UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    username              VARCHAR(100) NOT NULL UNIQUE,
    password_hash         VARCHAR(255) NOT NULL,               -- Argon2id
    full_name             VARCHAR(150) NOT NULL,
    phone_number          VARCHAR(20),
    role                  VARCHAR(30) NOT NULL,                -- roles.code (FK أدناه، الترحيل 009)
    is_active             BOOLEAN NOT NULL DEFAULT TRUE,
    must_change_password  BOOLEAN NOT NULL DEFAULT TRUE,
    failed_login_count    SMALLINT NOT NULL DEFAULT 0,
    locked_until          TIMESTAMPTZ,
    last_login_at         TIMESTAMPTZ,
    created_at            TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at            TIMESTAMPTZ NOT NULL DEFAULT now()
);

ALTER TABLE users ADD CONSTRAINT fk_users_role FOREIGN KEY (role) REFERENCES roles(code);
CREATE INDEX idx_users_role ON users(role);
ALTER TABLE roles ADD CONSTRAINT fk_roles_created_by FOREIGN KEY (created_by) REFERENCES users(user_id);
ALTER TABLE roles ADD CONSTRAINT fk_roles_updated_by FOREIGN KEY (updated_by) REFERENCES users(user_id);

CREATE TABLE role_permissions (
    role_code       VARCHAR(30) NOT NULL REFERENCES roles(code) ON DELETE CASCADE,
    permission      VARCHAR(60) NOT NULL,
    granted_by      UUID REFERENCES users(user_id),
    granted_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (role_code, permission)
);

-- الموظف قد يعمل في أكثر من فرع
ALTER TABLE clinic_closures ADD CONSTRAINT fk_closures_created_by FOREIGN KEY (created_by) REFERENCES users(user_id);
ALTER TABLE clinic_closures ADD CONSTRAINT fk_closures_ended_by FOREIGN KEY (ended_by) REFERENCES users(user_id);
ALTER TABLE branch_closures ADD CONSTRAINT fk_branch_closures_created_by FOREIGN KEY (created_by) REFERENCES users(user_id);
ALTER TABLE branch_closures ADD CONSTRAINT fk_branch_closures_ended_by FOREIGN KEY (ended_by) REFERENCES users(user_id);

CREATE TABLE user_branches (
    user_id         UUID NOT NULL REFERENCES users(user_id) ON DELETE CASCADE,
    branch_id       UUID NOT NULL REFERENCES branches(branch_id),
    PRIMARY KEY (user_id, branch_id)
);

-- موظف الاستقبال مرتبط بعيادة أو أكثر
CREATE TABLE user_clinics (
    user_id         UUID NOT NULL REFERENCES users(user_id) ON DELETE CASCADE,
    clinic_id       UUID NOT NULL REFERENCES clinics(clinic_id),
    PRIMARY KEY (user_id, clinic_id)
);

-- صلاحيات إضافية فوق صلاحيات الدور (مثل patients.unban, discounts.grant)
CREATE TABLE user_permissions (
    user_id         UUID NOT NULL REFERENCES users(user_id) ON DELETE CASCADE,
    permission      VARCHAR(60) NOT NULL,
    granted_by      UUID REFERENCES users(user_id),
    granted_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (user_id, permission)
);

-- أجهزة الاستقبال وشاشات العرض
CREATE TABLE devices (
    device_id         UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    device_code       VARCHAR(50) NOT NULL UNIQUE,             -- مثل dev_rec_pc_03، يدخل في بادئة كتل الإيصالات
    branch_id         UUID NOT NULL REFERENCES branches(branch_id),
    clinic_id         UUID REFERENCES clinics(clinic_id),
    kind              VARCHAR(20) NOT NULL CHECK (kind IN ('reception_pc', 'display', 'lab_pc', 'radiology_pc', 'pharmacy_pc')),
    name              VARCHAR(100) NOT NULL,
    agent_key_hash    VARCHAR(255),                             -- مفتاح الوكيل المحلي (X-Agent-Key)
    display_key_hash  VARCHAR(255),                             -- مفتاح الشاشة العامة (X-Display-Key)
    is_active         BOOLEAN NOT NULL DEFAULT TRUE,
    created_at        TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- =============================================================================
-- 3. الأطباء والنسب والجداول
-- =============================================================================

CREATE TABLE doctors (
    doctor_id             UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id               UUID UNIQUE REFERENCES users(user_id) ON DELETE SET NULL,
    clinic_id             UUID NOT NULL REFERENCES clinics(clinic_id),
    full_name             VARCHAR(150) NOT NULL,
    specialty             VARCHAR(100) NOT NULL,               -- lookup_values.name_ar (doctor_specialty)
    job_title             VARCHAR(100),                        -- lookup_values.name_ar (doctor_job_title)
    photo_object_key      VARCHAR(500),
    default_slot_minutes  SMALLINT NOT NULL DEFAULT 10 CHECK (default_slot_minutes > 0),
    is_active             BOOLEAN NOT NULL DEFAULT TRUE,
    created_at            TIMESTAMPTZ NOT NULL DEFAULT now(),
    allow_extra           BOOLEAN NOT NULL DEFAULT TRUE,       -- false = «ممنوع الإضافي» فوق السعة (ترحيل 007)
    late_grace_minutes    SMALLINT CHECK (late_grace_minutes IS NULL OR late_grace_minutes BETWEEN 0 AND 180), -- فارغ = إعداد الفرع
    follow_up_days        SMALLINT CHECK (follow_up_days IS NULL OR follow_up_days BETWEEN 0 AND 180),   -- ترحيل 008: فارغ = إعداد الفرع
    follow_up_price       BIGINT   CHECK (follow_up_price IS NULL OR follow_up_price >= 0),              -- فارغ = سعر خدمة المراجعة، 0 = مجانية
    online_deposit_amount BIGINT   CHECK (online_deposit_amount IS NULL OR online_deposit_amount >= 0),  -- فارغ = إعداد الفرع، 0 = لا عربون
    free_return_days      SMALLINT CHECK (free_return_days IS NULL OR free_return_days BETWEEN 0 AND 90)  -- ترحيل 018: فارغ = العيادة ثم الفرع
);
CREATE INDEX idx_doctors_clinic ON doctors(clinic_id);

-- تاريخ النسب: النسبة المطبقة = آخر سجل effective_from <= تاريخ الفاتورة
CREATE TABLE doctor_commission_rates (
    rate_id           UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    doctor_id         UUID NOT NULL REFERENCES doctors(doctor_id),
    new_visit_rate    NUMERIC(5,2) NOT NULL CHECK (new_visit_rate BETWEEN 0 AND 100),
    follow_up_rate    NUMERIC(5,2) NOT NULL CHECK (follow_up_rate BETWEEN 0 AND 100),
    effective_from    DATE NOT NULL,
    created_by        UUID REFERENCES users(user_id),
    created_at        TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (doctor_id, effective_from)
);

-- الجدول الأسبوعي (قالب الوجبات)
CREATE TABLE doctor_schedule_templates (
    session_template_id   UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    doctor_id             UUID NOT NULL REFERENCES doctors(doctor_id) ON DELETE CASCADE,
    branch_id             UUID NOT NULL REFERENCES branches(branch_id),
    weekday               SMALLINT NOT NULL CHECK (weekday BETWEEN 0 AND 6),   -- 0 = الأحد
    start_time            TIME NOT NULL,
    end_time              TIME NOT NULL,
    slot_minutes          SMALLINT NOT NULL CHECK (slot_minutes > 0),
    online_quota_percent  SMALLINT NOT NULL DEFAULT 60 CHECK (online_quota_percent BETWEEN 0 AND 100),
    is_active             BOOLEAN NOT NULL DEFAULT TRUE,
    CHECK (end_time > start_time)
);

-- الوجبة الفعلية ليوم محدد (تُولد من القالب). السعة = مدة الدوام ÷ مدة المعاينة
CREATE TABLE doctor_sessions (
    session_id        UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    doctor_id         UUID NOT NULL REFERENCES doctors(doctor_id),
    branch_id         UUID NOT NULL REFERENCES branches(branch_id),
    clinic_id         UUID NOT NULL REFERENCES clinics(clinic_id),
    session_date      DATE NOT NULL,
    start_time        TIME NOT NULL,
    end_time          TIME NOT NULL,
    slot_minutes      SMALLINT NOT NULL CHECK (slot_minutes > 0),
    capacity_total    SMALLINT NOT NULL CHECK (capacity_total >= 0),
    online_quota      SMALLINT NOT NULL CHECK (online_quota >= 0),
    status            VARCHAR(20) NOT NULL DEFAULT 'open' CHECK (status IN ('open', 'full', 'cancelled', 'closed')),
    cancel_reason     TEXT,
    created_at        TIMESTAMPTZ NOT NULL DEFAULT now(),
    doctor_arrived_at      TIMESTAMPTZ,                    -- «الطبيب وصل» (ترحيل 007)
    extended_end_time      TIME,                           -- «تمديد اليوم»: النهاية الفعلية بدل end_time
    registration_closed_at TIMESTAMPTZ,                    -- «إغلاق التسجيل» للمراجعين بدون حجز
    UNIQUE (doctor_id, session_date, start_time),
    CHECK (online_quota <= capacity_total)
);
CREATE INDEX idx_sessions_branch_date ON doctor_sessions(branch_id, session_date);

-- =============================================================================
-- 4. الخدمات والأسعار
-- =============================================================================

CREATE TABLE services (
    service_id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    branch_id           UUID NOT NULL REFERENCES branches(branch_id),
    clinic_id           UUID REFERENCES clinics(clinic_id),
    name                VARCHAR(150) NOT NULL,
    type                VARCHAR(20) NOT NULL CHECK (type IN ('consultation', 'procedure', 'lab', 'radiology', 'pharmacy', 'fee')),  -- fee: خدمة نظام (الترحيل 011)
    visit_kind          VARCHAR(20) CHECK (visit_kind IN ('new_visit', 'follow_up')),
    modality            VARCHAR(20) CHECK (modality IN ('xray', 'ultrasound', 'ct', 'mri')),
    price               BIGINT NOT NULL CHECK (price >= 0),
    is_commissionable   BOOLEAN NOT NULL DEFAULT FALSE,
    is_active           BOOLEAN NOT NULL DEFAULT TRUE,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT now(),
    CHECK ((type = 'consultation') = (visit_kind IS NOT NULL)),
    CHECK (modality IS NULL OR type = 'radiology')
);
CREATE INDEX idx_services_branch_type ON services(branch_id, type);
-- خدمة «رسوم إلغاء متأخر» واحدة لكل فرع (الترحيل 011)
CREATE UNIQUE INDEX uq_services_fee_per_branch ON services(branch_id) WHERE type = 'fee';

-- قالب نتائج التحليل (البارامترات والمدى الطبيعي والقيم الحرجة)
CREATE TABLE lab_test_parameters (
    parameter_id    UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    service_id      UUID NOT NULL REFERENCES services(service_id) ON DELETE CASCADE,
    code            VARCHAR(30) NOT NULL,
    name            VARCHAR(100) NOT NULL,
    unit            VARCHAR(30),
    ref_low         NUMERIC(12,4),
    ref_high        NUMERIC(12,4),
    critical_low    NUMERIC(12,4),
    critical_high   NUMERIC(12,4),
    sort_order      SMALLINT NOT NULL DEFAULT 0,
    UNIQUE (service_id, code)
);

-- =============================================================================
-- 5. المرضى وحسابات تطبيق المريض والحظر
-- =============================================================================

CREATE TABLE patients (
    patient_id              UUID PRIMARY KEY,                    -- يولده التطبيق (يدعم الأوفلاين)
    file_number             VARCHAR(20) NOT NULL UNIQUE,         -- مثل R1-004512
    full_name               VARCHAR(150) NOT NULL,
    phone_number            VARCHAR(20) NOT NULL,
    birth_date              DATE,
    approx_age              SMALLINT CHECK (approx_age BETWEEN 0 AND 130),
    gender                  VARCHAR(10) CHECK (gender IN ('male', 'female')),
    home_branch_id          UUID REFERENCES branches(branch_id),
    no_show_count           SMALLINT NOT NULL DEFAULT 0,
    is_banned               BOOLEAN NOT NULL DEFAULT FALSE,
    banned_at               TIMESTAMPTZ,
    ban_reason              VARCHAR(30) CHECK (ban_reason IN ('no_show_limit', 'manual')),
    merged_into_patient_id  UUID REFERENCES patients(patient_id),
    created_by              UUID REFERENCES users(user_id),
    created_offline_at      TIMESTAMPTZ,
    created_at              TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at              TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX idx_patients_phone ON patients(phone_number);
CREATE INDEX idx_patients_name ON patients(full_name);

-- سجل حالات الغياب (لتطبيق قاعدة: 3 غيابات خلال 90 يوماً = حظر)
CREATE TABLE patient_no_shows (
    no_show_id      UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    patient_id      UUID NOT NULL REFERENCES patients(patient_id),
    ticket_id       UUID,                                        -- FK يضاف بعد إنشاء queue_tickets
    booking_id      UUID,
    occurred_at     TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX idx_no_shows_patient ON patient_no_shows(patient_id, occurred_at);

-- سجل الحظر وفكه
CREATE TABLE patient_ban_events (
    event_id        UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    patient_id      UUID NOT NULL REFERENCES patients(patient_id),
    action          VARCHAR(10) NOT NULL CHECK (action IN ('ban', 'unban')),
    method          VARCHAR(20) CHECK (method IN ('auto', 'cash', 'pos', 'online', 'waived', 'manual')),
    amount          BIGINT NOT NULL DEFAULT 0 CHECK (amount >= 0),
    note            TEXT,
    receipt_id      UUID,                                        -- FK يضاف بعد إنشاء receipts
    performed_by    UUID REFERENCES users(user_id),
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- مستخدمو تطبيق المريض (الدخول برقم الهاتف + OTP)
CREATE TABLE patient_accounts (
    patient_account_id  UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    phone_number        VARCHAR(20) NOT NULL UNIQUE,
    last_login_at       TIMESTAMPTZ,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- رقم واحد قد يدير أكثر من ملف (أفراد العائلة)
CREATE TABLE patient_account_links (
    patient_account_id  UUID NOT NULL REFERENCES patient_accounts(patient_account_id) ON DELETE CASCADE,
    patient_id          UUID NOT NULL REFERENCES patients(patient_id),
    relation            VARCHAR(20) NOT NULL DEFAULT 'self' CHECK (relation IN ('self', 'child', 'spouse', 'parent', 'other')),
    PRIMARY KEY (patient_account_id, patient_id)
);

CREATE TABLE otp_codes (
    otp_id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    phone_number    VARCHAR(20) NOT NULL,
    code_hash       VARCHAR(255) NOT NULL,
    attempts        SMALLINT NOT NULL DEFAULT 0,
    expires_at      TIMESTAMPTZ NOT NULL,
    consumed_at     TIMESTAMPTZ,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX idx_otp_phone ON otp_codes(phone_number, created_at);

-- توكنات التجديد للموظفين والمرضى
CREATE TABLE refresh_tokens (
    token_id            UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id             UUID REFERENCES users(user_id) ON DELETE CASCADE,
    patient_account_id  UUID REFERENCES patient_accounts(patient_account_id) ON DELETE CASCADE,
    branch_id           UUID REFERENCES branches(branch_id),
    device_id           UUID REFERENCES devices(device_id),
    token_hash          VARCHAR(255) NOT NULL UNIQUE,
    expires_at          TIMESTAMPTZ NOT NULL,
    revoked_at          TIMESTAMPTZ,
    replaced_by         UUID REFERENCES refresh_tokens(token_id),
    created_at          TIMESTAMPTZ NOT NULL DEFAULT now(),
    CHECK ((user_id IS NULL) <> (patient_account_id IS NULL))
);

-- =============================================================================
-- 6. الحجوزات والطابور
-- =============================================================================

CREATE TABLE bookings (
    booking_id              UUID PRIMARY KEY,
    patient_id              UUID NOT NULL REFERENCES patients(patient_id),
    session_id              UUID NOT NULL REFERENCES doctor_sessions(session_id),
    sequence_number         SMALLINT NOT NULL,
    visit_kind              VARCHAR(20) NOT NULL CHECK (visit_kind IN ('new_visit', 'follow_up')),
    price                   BIGINT NOT NULL CHECK (price >= 0),          -- Snapshot وقت الحجز
    source                  VARCHAR(10) NOT NULL CHECK (source IN ('online', 'phone')),
    status                  VARCHAR(20) NOT NULL DEFAULT 'booked' CHECK (status IN ('booked', 'checked_in', 'completed', 'cancelled', 'no_show')),
    payment_status          VARCHAR(20) NOT NULL DEFAULT 'unpaid' CHECK (payment_status IN ('unpaid', 'deposit_paid', 'paid', 'credited', 'refunded')),
    estimated_time          TIMESTAMPTZ,
    booked_by_user_id       UUID REFERENCES users(user_id),
    booked_by_account_id    UUID REFERENCES patient_accounts(patient_account_id),
    cancel_reason           TEXT,
    cancelled_at            TIMESTAMPTZ,
    counted_as_no_show      BOOLEAN NOT NULL DEFAULT FALSE,
    reminder_sent_at        TIMESTAMPTZ,
    day_before_sent_at      TIMESTAMPTZ,                     -- تذكير «موعدك غداً» (ترحيل 008)
    soon_sent_at            TIMESTAMPTZ,                     -- تذكير «بعد 20 دقيقة» (ترحيل 008)
    created_at              TIMESTAMPTZ NOT NULL DEFAULT now(),
    -- الترحيل 011: تغيير الموعد، تثبيت الحضور الذاتي، سياسة الإلغاء
    reschedule_count        SMALLINT NOT NULL DEFAULT 0 CONSTRAINT ck_bookings_reschedule_count CHECK (reschedule_count >= 0),
    last_rescheduled_at     TIMESTAMPTZ,
    original_session_id     UUID REFERENCES doctor_sessions(session_id),   -- الوجبة قبل أول تغيير للموعد
    checked_in_at           TIMESTAMPTZ,
    check_in_method         VARCHAR(10) CONSTRAINT ck_bookings_check_in_method CHECK (check_in_method IN ('reception', 'self_qr')),
    cancelled_by            VARCHAR(10) CONSTRAINT ck_bookings_cancelled_by CHECK (cancelled_by IN ('patient', 'staff', 'system')),
    cancel_fee_item_id      UUID,                            -- FK إلى invoice_items أدناه
    cancel_fee_waived       BOOLEAN NOT NULL DEFAULT FALSE,
    UNIQUE (session_id, sequence_number)
);
-- مريض واحد لا يُحجز مرتين في نفس الوجبة
CREATE UNIQUE INDEX uq_booking_patient_session ON bookings(session_id, patient_id) WHERE status <> 'cancelled';
CREATE INDEX idx_bookings_patient ON bookings(patient_id);

CREATE TABLE queue_tickets (
    ticket_id           UUID PRIMARY KEY,                    -- يولده التطبيق (يدعم الأوفلاين)
    branch_id           UUID NOT NULL REFERENCES branches(branch_id),
    session_id          UUID NOT NULL REFERENCES doctor_sessions(session_id),
    doctor_id           UUID NOT NULL REFERENCES doctors(doctor_id),
    patient_id          UUID NOT NULL REFERENCES patients(patient_id),
    booking_id          UUID UNIQUE REFERENCES bookings(booking_id),
    ticket_number       VARCHAR(20) NOT NULL,                -- مثل A-104
    sequence_number     SMALLINT NOT NULL,
    queue_position      SMALLINT,                            -- الترتيب الحالي بعد الطوارئ/التخطي
    source              VARCHAR(10) NOT NULL CHECK (source IN ('online', 'walk_in', 'phone')),
    priority            VARCHAR(20) NOT NULL DEFAULT 'normal' CHECK (priority IN ('normal', 'emergency', 'late_reinserted', 'returning')),
    status              VARCHAR(20) NOT NULL DEFAULT 'waiting' CHECK (status IN (
                            'waiting', 'arrived', 'called', 'in_room', 'completed', 'skipped', 'no_show', 'cancelled', 'awaiting_results')),
    emergency_reason    TEXT,
    estimated_time      TIMESTAMPTZ,
    arrived_at          TIMESTAMPTZ,
    called_at           TIMESTAMPTZ,
    started_at          TIMESTAMPTZ,
    completed_at        TIMESTAMPTZ,
    created_by          UUID REFERENCES users(user_id),
    created_offline_at  TIMESTAMPTZ,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT now(),
    turn_soon_notified_at TIMESTAMPTZ,                       -- إشعار "اقترب دورك" مرة واحدة
    call_acknowledged_at  TIMESTAMPTZ,                       -- المريض ضغط "أنا قادم" على تنبيه الاستدعاء
    call_alerts_sent      SMALLINT NOT NULL DEFAULT 0,       -- تنبيهات الاستدعاء المرسلة (الأول + التكرار)
    last_call_alert_at    TIMESTAMPTZ,
    on_the_way_at         TIMESTAMPTZ,                       -- أكد أنه في الطريق (دعوة الحضور المبكر)
    on_the_way_eta_minutes SMALLINT,
    is_extra              BOOLEAN NOT NULL DEFAULT FALSE,    -- مراجع فوق السعة بموافقة (ترحيل 007)
    extra_approved_by     UUID REFERENCES users(user_id),
    awaiting_since        TIMESTAMPTZ,                       -- ترحيل 018: بانتظار المختبر/الأشعة/السونار/الصيدلية
    returned_at           TIMESTAMPTZ,                       -- عاد بالنتائج (أولوية returning)
    free_return_until     TIMESTAMPTZ,                       -- أُغلقت الوجبة وهو بانتظار قسم: عودة مجانية حتى هذا الوقت
    original_ticket_id    UUID REFERENCES queue_tickets(ticket_id),   -- عودة بالنتائج في وجبة أخرى
    UNIQUE (session_id, ticket_number)
);
CREATE INDEX idx_queue_session_status ON queue_tickets(session_id, status);
CREATE INDEX idx_queue_free_return ON queue_tickets(patient_id, doctor_id, free_return_until) WHERE free_return_until IS NOT NULL;
CREATE INDEX idx_queue_awaiting ON queue_tickets(patient_id, doctor_id) WHERE status = 'awaiting_results';
CREATE INDEX idx_queue_branch_status ON queue_tickets(branch_id, status);

ALTER TABLE patient_no_shows ADD CONSTRAINT fk_no_shows_ticket FOREIGN KEY (ticket_id) REFERENCES queue_tickets(ticket_id);
ALTER TABLE patient_no_shows ADD CONSTRAINT fk_no_shows_booking FOREIGN KEY (booking_id) REFERENCES bookings(booking_id);

-- التقديم الذكي: عرض الفجوة على مرضى الوجبة اللاحقين
CREATE TABLE fast_forward_offers (
    offer_id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    session_id            UUID NOT NULL REFERENCES doctor_sessions(session_id),
    gap_minutes           SMALLINT CHECK (gap_minutes > 0),  -- NULL في وضع الدعوة
    mode                  VARCHAR(10) NOT NULL DEFAULT 'gap' CONSTRAINT fast_forward_offers_mode_check CHECK (mode IN ('gap', 'invite')),
    expires_at            TIMESTAMPTZ NOT NULL,
    accepted_booking_id   UUID REFERENCES bookings(booking_id),
    created_by            UUID REFERENCES users(user_id),      -- NULL = النظام تلقائياً
    created_at            TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE fast_forward_offer_recipients (
    offer_id        UUID NOT NULL REFERENCES fast_forward_offers(offer_id) ON DELETE CASCADE,
    booking_id      UUID NOT NULL REFERENCES bookings(booking_id),
    answer          VARCHAR(20) CHECK (answer IN ('come_now', 'keep_original')),
    answered_at     TIMESTAMPTZ,
    eta_minutes     SMALLINT,                                   -- وقت الطريق عند تأكيد الحضور (وضع الدعوة)
    PRIMARY KEY (offer_id, booking_id)
);

-- =============================================================================
-- 7. السجل الطبي والوصفات
-- =============================================================================

CREATE TABLE consultations (
    consultation_id       UUID PRIMARY KEY,
    ticket_id             UUID NOT NULL UNIQUE REFERENCES queue_tickets(ticket_id),
    patient_id            UUID NOT NULL REFERENCES patients(patient_id),
    doctor_id             UUID NOT NULL REFERENCES doctors(doctor_id),
    diagnosis             TEXT,
    icd10_code            VARCHAR(10),
    clinical_notes        TEXT,
    private_notes         TEXT,                                 -- لا تظهر للمريض أو الصيدلية
    follow_up_after_days  SMALLINT,
    created_at            TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at            TIMESTAMPTZ NOT NULL DEFAULT now()    -- التعديل مسموح 24 ساعة فقط
);
CREATE INDEX idx_consultations_patient ON consultations(patient_id, created_at DESC);

-- ملحقات بعد انتهاء مهلة التعديل
CREATE TABLE consultation_addenda (
    addendum_id       UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    consultation_id   UUID NOT NULL REFERENCES consultations(consultation_id),
    text              TEXT NOT NULL,
    created_by        UUID NOT NULL REFERENCES users(user_id),
    created_at        TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE prescriptions (
    prescription_id     UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    consultation_id     UUID NOT NULL UNIQUE REFERENCES consultations(consultation_id),
    patient_id          UUID NOT NULL REFERENCES patients(patient_id),
    doctor_id           UUID NOT NULL REFERENCES doctors(doctor_id),
    prescription_code   VARCHAR(20) NOT NULL UNIQUE,            -- مثل RX-7KQ2M9 (يُمسح في الصيدلية)
    status              VARCHAR(20) NOT NULL DEFAULT 'not_dispensed' CHECK (status IN ('not_dispensed', 'dispensed', 'expired')),
    issued_at           TIMESTAMPTZ NOT NULL DEFAULT now(),
    expires_at          TIMESTAMPTZ NOT NULL,
    dispensed_at        TIMESTAMPTZ,
    dispensed_by        UUID REFERENCES users(user_id),
    dispensed_branch_id UUID REFERENCES branches(branch_id),
    CHECK ((status = 'dispensed') = (dispensed_at IS NOT NULL))
);
CREATE INDEX idx_prescriptions_patient ON prescriptions(patient_id, issued_at DESC);

CREATE TABLE prescription_lines (
    prescription_id       UUID NOT NULL REFERENCES prescriptions(prescription_id) ON DELETE CASCADE,
    line_no               SMALLINT NOT NULL,
    drug_name             VARCHAR(200) NOT NULL,
    dose                  VARCHAR(100),
    frequency             VARCHAR(100),
    duration_days         SMALLINT,
    instructions          TEXT,
    dispensed             BOOLEAN,                                -- NULL = لم يُصرف بعد
    substitute_drug_name  VARCHAR(200),
    not_dispensed_reason  TEXT,
    PRIMARY KEY (prescription_id, line_no)
);

-- =============================================================================
-- 8. الفواتير والبنود (Snapshots)
-- =============================================================================

CREATE TABLE invoices (
    invoice_id        UUID PRIMARY KEY,
    invoice_number    VARCHAR(30) NOT NULL UNIQUE,              -- مثل B1-INV-012044
    branch_id         UUID NOT NULL REFERENCES branches(branch_id),
    patient_id        UUID NOT NULL REFERENCES patients(patient_id),
    status            VARCHAR(20) NOT NULL DEFAULT 'unpaid' CHECK (status IN ('unpaid', 'partially_paid', 'paid', 'deferred', 'void')),
    gross_amount      BIGINT NOT NULL DEFAULT 0 CHECK (gross_amount >= 0),
    discount_amount   BIGINT NOT NULL DEFAULT 0 CHECK (discount_amount >= 0),
    net_amount        BIGINT NOT NULL DEFAULT 0 CHECK (net_amount >= 0),
    paid_amount       BIGINT NOT NULL DEFAULT 0 CHECK (paid_amount >= 0),
    discount_reason   TEXT,
    discount_by       UUID REFERENCES users(user_id),
    void_reason       TEXT,
    voided_by         UUID REFERENCES users(user_id),
    voided_at         TIMESTAMPTZ,
    created_by        UUID REFERENCES users(user_id),
    created_at        TIMESTAMPTZ NOT NULL DEFAULT now(),
    CHECK (net_amount = gross_amount - discount_amount)
);
CREATE INDEX idx_invoices_patient ON invoices(patient_id);
CREATE INDEX idx_invoices_branch_date ON invoices(branch_id, created_at);

CREATE TABLE invoice_items (
    invoice_item_id     UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    invoice_id          UUID NOT NULL REFERENCES invoices(invoice_id),
    service_id          UUID NOT NULL REFERENCES services(service_id),
    doctor_id           UUID REFERENCES doctors(doctor_id),
    ticket_id           UUID REFERENCES queue_tickets(ticket_id),
    item_type           VARCHAR(20) NOT NULL CHECK (item_type IN ('new_visit', 'follow_up', 'procedure', 'lab', 'radiology', 'pharmacy', 'cancellation_fee')),  -- الترحيل 011
    service_name        VARCHAR(150) NOT NULL,                  -- Snapshot
    quantity            SMALLINT NOT NULL DEFAULT 1 CHECK (quantity > 0),
    unit_price          BIGINT NOT NULL CHECK (unit_price >= 0), -- Snapshot من جدول الخدمات
    discount            BIGINT NOT NULL DEFAULT 0 CHECK (discount >= 0),
    net_price           BIGINT NOT NULL CHECK (net_price >= 0),
    commission_rate     NUMERIC(5,2) NOT NULL DEFAULT 0 CHECK (commission_rate BETWEEN 0 AND 100),  -- Snapshot
    doctor_share        BIGINT NOT NULL DEFAULT 0,
    complex_share       BIGINT NOT NULL,
    refunded_amount     BIGINT NOT NULL DEFAULT 0 CHECK (refunded_amount >= 0),
    status              VARCHAR(20) NOT NULL DEFAULT 'unpaid' CHECK (status IN (
                            'unpaid', 'paid', 'deferred', 'partially_refunded', 'refunded', 'void')),
    performed_at        TIMESTAMPTZ,                            -- سُحبت العينة / أُجريت الأشعة / تمت المعاينة
    created_at          TIMESTAMPTZ NOT NULL DEFAULT now(),
    CHECK (net_price = unit_price * quantity - discount),
    CHECK (doctor_share + complex_share = net_price),
    CHECK (refunded_amount <= net_price)
);
CREATE INDEX idx_invoice_items_invoice ON invoice_items(invoice_id);
CREATE INDEX idx_invoice_items_doctor ON invoice_items(doctor_id, created_at);
CREATE INDEX idx_invoice_items_unpaid ON invoice_items(status) WHERE status IN ('unpaid', 'deferred');
ALTER TABLE bookings ADD CONSTRAINT bookings_cancel_fee_item_id_fkey FOREIGN KEY (cancel_fee_item_id) REFERENCES invoice_items(invoice_item_id);

-- =============================================================================
-- 9. الطلبات الطبية (مختبر وأشعة) والنتائج والملفات
-- =============================================================================

CREATE TABLE medical_orders (
    order_item_id         UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    consultation_id       UUID REFERENCES consultations(consultation_id),   -- NULL = طلب مباشر من الاستقبال
    patient_id            UUID NOT NULL REFERENCES patients(patient_id),
    branch_id             UUID NOT NULL REFERENCES branches(branch_id),
    invoice_item_id       UUID NOT NULL UNIQUE REFERENCES invoice_items(invoice_item_id),
    service_id            UUID NOT NULL REFERENCES services(service_id),
    department            VARCHAR(20) NOT NULL CHECK (department IN ('lab', 'radiology')),
    ordered_by_doctor_id  UUID REFERENCES doctors(doctor_id),
    referring_doctor_name VARCHAR(150),                                   -- طبيب من خارج المجمع (طلب مباشر)
    note                  TEXT,
    status                VARCHAR(20) NOT NULL DEFAULT 'ordered' CHECK (status IN (
                              'ordered', 'paid', 'deferred',
                              'sample_collected', 'in_progress', 'resulted',      -- المختبر
                              'scheduled', 'performed', 'reported',               -- الأشعة
                              'released', 'cancelled')),
    sample_barcode        VARCHAR(50) UNIQUE,
    sample_collected_at   TIMESTAMPTZ,
    collected_by          UUID REFERENCES users(user_id),
    scheduled_at          TIMESTAMPTZ,
    room                  VARCHAR(30),
    performed_at          TIMESTAMPTZ,
    has_critical          BOOLEAN NOT NULL DEFAULT FALSE,
    released_at           TIMESTAMPTZ,
    released_by           UUID REFERENCES users(user_id),
    amended_at            TIMESTAMPTZ,
    cancel_reason         TEXT,
    created_at            TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX idx_orders_worklist ON medical_orders(branch_id, department, status, created_at);
CREATE INDEX idx_orders_patient ON medical_orders(patient_id, created_at DESC);

CREATE TABLE lab_results (
    order_item_id   UUID PRIMARY KEY REFERENCES medical_orders(order_item_id),
    text_result     TEXT,
    comment         TEXT,
    entered_by      UUID NOT NULL REFERENCES users(user_id),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE lab_result_values (
    order_item_id   UUID NOT NULL REFERENCES lab_results(order_item_id) ON DELETE CASCADE,
    parameter_id    UUID NOT NULL REFERENCES lab_test_parameters(parameter_id),
    value           NUMERIC(12,4) NOT NULL,
    flag            VARCHAR(20) NOT NULL CHECK (flag IN ('normal', 'low', 'high', 'critical_low', 'critical_high')),
    PRIMARY KEY (order_item_id, parameter_id)
);

CREATE TABLE radiology_reports (
    order_item_id   UUID PRIMARY KEY REFERENCES medical_orders(order_item_id),
    findings        TEXT NOT NULL,
    impression      TEXT,
    reported_by     UUID NOT NULL REFERENCES users(user_id),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- الملفات في MinIO (يُخزن المفتاح فقط، والروابط مؤقتة)
CREATE TABLE files (
    file_id         UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    branch_id       UUID NOT NULL REFERENCES branches(branch_id),
    bucket          VARCHAR(63) NOT NULL,
    object_key      VARCHAR(500) NOT NULL,
    file_name       VARCHAR(255) NOT NULL,
    content_type    VARCHAR(100) NOT NULL CHECK (content_type IN ('image/jpeg', 'image/png', 'application/pdf', 'application/dicom')),
    size_bytes      BIGINT NOT NULL CHECK (size_bytes > 0 AND size_bytes <= 209715200),
    purpose         VARCHAR(20) NOT NULL CHECK (purpose IN ('lab', 'radiology', 'doctor_photo')),
    order_item_id   UUID REFERENCES medical_orders(order_item_id),
    status          VARCHAR(10) NOT NULL DEFAULT 'pending' CHECK (status IN ('pending', 'stored')),
    s3_synced_at    TIMESTAMPTZ,                                -- NULL = لم يُنسخ للسحابة بعد
    uploaded_by     UUID REFERENCES users(user_id),
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (bucket, object_key)
);
CREATE INDEX idx_files_order ON files(order_item_id);
CREATE INDEX idx_files_s3_pending ON files(created_at) WHERE s3_synced_at IS NULL;

-- =============================================================================
-- 10. الورديات وكتل الإيصالات والإيصالات
-- =============================================================================

CREATE TABLE shifts (
    shift_id                  UUID PRIMARY KEY,
    branch_id                 UUID NOT NULL REFERENCES branches(branch_id),
    user_id                   UUID NOT NULL REFERENCES users(user_id),
    device_id                 UUID REFERENCES devices(device_id),
    clinic_id                 UUID REFERENCES clinics(clinic_id),  -- نقطة التحصيل (الترحيل 003)
    opening_float             BIGINT NOT NULL DEFAULT 0 CHECK (opening_float >= 0),
    status                    VARCHAR(25) NOT NULL DEFAULT 'open' CHECK (status IN ('open', 'closed_pending_review', 'reviewed')),
    opened_at                 TIMESTAMPTZ NOT NULL DEFAULT now(),
    closed_at                 TIMESTAMPTZ,
    -- ما أدخله الموظف (الإغلاق الأعمى)
    declared_cash             BIGINT,
    declared_pos_total        BIGINT,
    declared_pos_slips_count  SMALLINT,
    cash_denominations        JSONB,
    close_note                TEXT,
    -- ما يحسبه النظام لحظة الإغلاق (لا يُعرض للموظف)
    expected_cash             BIGINT,
    expected_pos              BIGINT,
    expected_online           BIGINT,
    variance_cash             BIGINT,
    variance_pos              BIGINT,
    audit_status              VARCHAR(10) CHECK (audit_status IN ('MATCHED', 'SHORTAGE', 'OVERAGE')),
    -- مراجعة المحاسب
    reviewed_by               UUID REFERENCES users(user_id),
    review_decision           VARCHAR(10) CHECK (review_decision IN ('accepted', 'escalated')),
    review_note               TEXT,
    reviewed_at               TIMESTAMPTZ,
    created_offline_at        TIMESTAMPTZ
);
-- وردية مفتوحة واحدة لكل موظف
CREATE UNIQUE INDEX uq_one_open_shift_per_user ON shifts(user_id) WHERE status = 'open';
CREATE INDEX idx_shifts_branch_status ON shifts(branch_id, status);

-- أرقام إيصالات محجوزة مسبقاً لكل جهاز للعمل أوفلاين
CREATE TABLE receipt_blocks (
    block_id        UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    device_id       UUID NOT NULL REFERENCES devices(device_id),
    branch_id       UUID NOT NULL REFERENCES branches(branch_id),
    prefix          VARCHAR(20) NOT NULL,                       -- مثل B1-D03-
    range_from      BIGINT NOT NULL,
    range_to        BIGINT NOT NULL,
    issued_at       TIMESTAMPTZ NOT NULL DEFAULT now(),
    returned_at     TIMESTAMPTZ,
    last_used       BIGINT,                                     -- آخر رقم مستخدم عند الإعادة
    CHECK (range_to >= range_from)
);
CREATE INDEX idx_receipt_blocks_device ON receipt_blocks(device_id);

CREATE TABLE receipts (
    receipt_id        UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    receipt_number    VARCHAR(30) NOT NULL UNIQUE,
    verify_code       VARCHAR(20) NOT NULL UNIQUE,              -- يدخل في رابط الـ QR
    branch_id         UUID NOT NULL REFERENCES branches(branch_id),
    shift_id          UUID REFERENCES shifts(shift_id),         -- NULL للدفع الأونلاين
    patient_id        UUID REFERENCES patients(patient_id),
    type              VARCHAR(10) NOT NULL CHECK (type IN ('payment', 'refund', 'deposit', 'online')),
    total             BIGINT NOT NULL CHECK (total >= 0),
    status            VARCHAR(20) NOT NULL DEFAULT 'valid' CHECK (status IN ('valid', 'partially_refunded', 'refunded')),
    issued_offline    BOOLEAN NOT NULL DEFAULT FALSE,
    block_id          UUID REFERENCES receipt_blocks(block_id),
    reprint_count     SMALLINT NOT NULL DEFAULT 0,
    issued_by         UUID REFERENCES users(user_id),
    issued_at         TIMESTAMPTZ NOT NULL DEFAULT now(),
    CHECK (NOT issued_offline OR block_id IS NOT NULL)
);
CREATE INDEX idx_receipts_shift ON receipts(shift_id);

ALTER TABLE patient_ban_events ADD CONSTRAINT fk_ban_events_receipt FOREIGN KEY (receipt_id) REFERENCES receipts(receipt_id);

-- =============================================================================
-- 11. الدفع الإلكتروني (أونلاين)
-- =============================================================================

CREATE TABLE online_payments (
    online_payment_id         UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    patient_id                UUID NOT NULL REFERENCES patients(patient_id),
    patient_account_id        UUID REFERENCES patient_accounts(patient_account_id),
    purpose                   VARCHAR(20) NOT NULL CHECK (purpose IN ('booking_full', 'booking_deposit', 'unban_deposit', 'invoice')),
    booking_id                UUID REFERENCES bookings(booking_id),
    invoice_id                UUID REFERENCES invoices(invoice_id),
    invoice_item_id           UUID REFERENCES invoice_items(invoice_item_id),   -- بند واحد من الفاتورة، وNULL يعني كل بنودها (الترحيل 016)
    provider                  VARCHAR(20) NOT NULL CHECK (provider IN ('zaincash', 'qicard', 'card', 'fib', 'sandbox')),
    amount                    BIGINT NOT NULL CHECK (amount > 0),
    status                    VARCHAR(10) NOT NULL DEFAULT 'pending' CHECK (status IN ('pending', 'succeeded', 'failed', 'expired', 'refunded', 'cancelled')),  -- cancelled: الترحيل 019
    provider_transaction_id   VARCHAR(100) UNIQUE,
    redirect_url              VARCHAR(1000),
    expires_at                TIMESTAMPTZ NOT NULL,
    receipt_id                UUID REFERENCES receipts(receipt_id),
    raw_callback              JSONB,
    created_at                TIMESTAMPTZ NOT NULL DEFAULT now(),
    completed_at              TIMESTAMPTZ,
    provider_reference        VARCHAR(60),                          -- مرجع المعاملة لدى البوابة (الترحيل 019)
    is_sandbox                BOOLEAN NOT NULL DEFAULT FALSE,       -- دفعة تجريبية، تظهر للإدارة فقط (الترحيل 019)
    failure_code              VARCHAR(40),                          -- سبب الفشل من البوابة (الترحيل 019)
    CHECK (purpose NOT IN ('booking_full', 'booking_deposit') OR booking_id IS NOT NULL),
    CHECK (purpose <> 'invoice' OR invoice_id IS NOT NULL)
);
CREATE INDEX idx_online_payments_patient ON online_payments(patient_id);
CREATE INDEX idx_online_payments_pending ON online_payments(status, expires_at) WHERE status IN ('pending', 'expired');

-- جانب «البوابة» في الوضع التجريبي لزين كاش وكي كارد (الترحيل 019)
CREATE TABLE mock_gateway_transactions (
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
CREATE INDEX idx_mock_gateway_op ON mock_gateway_transactions(online_payment_id);


-- =============================================================================
-- 12. التحصيل والاسترجاع ورصيد المريض
-- =============================================================================

CREATE TABLE payments (
    payment_id          UUID PRIMARY KEY,                       -- يولده التطبيق (يدعم الأوفلاين)
    branch_id           UUID NOT NULL REFERENCES branches(branch_id),
    patient_id          UUID NOT NULL REFERENCES patients(patient_id),
    shift_id            UUID REFERENCES shifts(shift_id),
    online_payment_id   UUID UNIQUE REFERENCES online_payments(online_payment_id),
    receipt_id          UUID NOT NULL UNIQUE REFERENCES receipts(receipt_id),
    total_paid          BIGINT NOT NULL CHECK (total_paid >= 0),  -- مجموع الـ tenders
    deposit_used        BIGINT NOT NULL DEFAULT 0 CHECK (deposit_used >= 0),
    price_mismatch      BOOLEAN NOT NULL DEFAULT FALSE,         -- أوفلاين بسعر قديم: تنبيه للمحاسب
    created_by          UUID REFERENCES users(user_id),
    created_offline_at  TIMESTAMPTZ,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT now(),
    CHECK ((shift_id IS NULL) <> (online_payment_id IS NULL))   -- إما من الدرج أو أونلاين
);
CREATE INDEX idx_payments_shift ON payments(shift_id);
CREATE INDEX idx_payments_patient ON payments(patient_id);
CREATE INDEX idx_payments_branch_created ON payments(branch_id, created_at);

-- طرق الدفع داخل الدفعة الواحدة (جزء كاش وجزء POS)
CREATE TABLE payment_tenders (
    tender_id       UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    payment_id      UUID NOT NULL REFERENCES payments(payment_id) ON DELETE CASCADE,
    method          VARCHAR(10) NOT NULL CHECK (method IN ('cash', 'pos', 'online')),
    amount          BIGINT NOT NULL CHECK (amount > 0),
    pos_terminal    VARCHAR(50),
    pos_reference   VARCHAR(100),
    CHECK (method <> 'pos' OR pos_reference IS NOT NULL)
);
CREATE INDEX idx_tenders_payment ON payment_tenders(payment_id);

-- توزيع الدفعة على بنود الفواتير (الفاتورة الموحدة)
CREATE TABLE payment_allocations (
    payment_id        UUID NOT NULL REFERENCES payments(payment_id) ON DELETE CASCADE,
    invoice_item_id   UUID NOT NULL REFERENCES invoice_items(invoice_item_id),
    amount            BIGINT NOT NULL CHECK (amount > 0),
    PRIMARY KEY (payment_id, invoice_item_id)
);
CREATE INDEX idx_allocations_item ON payment_allocations(invoice_item_id);

CREATE TABLE refunds (
    refund_id                 UUID PRIMARY KEY,
    invoice_item_id           UUID NOT NULL REFERENCES invoice_items(invoice_item_id),
    amount                    BIGINT NOT NULL CHECK (amount > 0),
    method                    VARCHAR(20) NOT NULL CHECK (method IN ('cash', 'pos', 'online_reversal', 'patient_credit')),
    reason                    TEXT NOT NULL,
    status                    VARCHAR(20) NOT NULL DEFAULT 'pending_approval' CHECK (status IN ('pending_approval', 'completed', 'rejected')),
    requested_by              UUID NOT NULL REFERENCES users(user_id),
    decided_by                UUID REFERENCES users(user_id),
    decided_at                TIMESTAMPTZ,
    reject_reason             TEXT,
    shift_id                  UUID REFERENCES shifts(shift_id),   -- الكاش يُخصم من هذه الوردية
    receipt_id                UUID REFERENCES receipts(receipt_id),
    doctor_share_adjustment   BIGINT NOT NULL DEFAULT 0 CHECK (doctor_share_adjustment <= 0),
    complex_share_adjustment  BIGINT NOT NULL DEFAULT 0 CHECK (complex_share_adjustment <= 0),
    created_at                TIMESTAMPTZ NOT NULL DEFAULT now(),
    CHECK (status <> 'completed' OR doctor_share_adjustment + complex_share_adjustment = -amount),
    CHECK (method <> 'cash' OR status <> 'completed' OR shift_id IS NOT NULL)
);
CREATE INDEX idx_refunds_item ON refunds(invoice_item_id);
CREATE INDEX idx_refunds_pending ON refunds(created_at) WHERE status = 'pending_approval';
CREATE INDEX idx_refunds_decided ON refunds(decided_at) WHERE status = 'completed';

-- رصيد المريض (عربون، رصيد إلغاء حجز، استرجاع كرصيد...). الرصيد = SUM(amount)
CREATE TABLE patient_credit_ledger (
    entry_id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    patient_id        UUID NOT NULL REFERENCES patients(patient_id),
    branch_id         UUID NOT NULL REFERENCES branches(branch_id), -- أين قُبض المبلغ أو استُخدم (الترحيل 003)
    amount            BIGINT NOT NULL CHECK (amount <> 0),      -- موجب = إضافة، سالب = استخدام
    kind              VARCHAR(30) NOT NULL CHECK (kind IN (
                          'unban_deposit', 'booking_deposit', 'booking_cancel_credit',
                          'refund_credit', 'applied_to_payment', 'refunded_out')),
    payment_id        UUID REFERENCES payments(payment_id),
    refund_id         UUID REFERENCES refunds(refund_id),
    booking_id        UUID REFERENCES bookings(booking_id),
    online_payment_id UUID REFERENCES online_payments(online_payment_id),
    created_by        UUID REFERENCES users(user_id),
    created_at        TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX idx_credit_patient ON patient_credit_ledger(patient_id);
CREATE INDEX idx_credit_branch ON patient_credit_ledger(branch_id, created_at);

-- =============================================================================
-- 13. الاستثناء والدفع الآجل
-- =============================================================================

CREATE TABLE deferrals (
    deferral_id         UUID PRIMARY KEY,
    patient_id          UUID NOT NULL REFERENCES patients(patient_id),
    branch_id           UUID NOT NULL REFERENCES branches(branch_id),
    reason              VARCHAR(20) NOT NULL CHECK (reason IN ('emergency', 'humanitarian', 'other')),
    note                TEXT,
    amount              BIGINT NOT NULL CHECK (amount > 0),
    due_date            DATE,
    status              VARCHAR(20) NOT NULL DEFAULT 'active' CHECK (status IN ('active', 'settled', 'written_off')),
    granted_by          UUID NOT NULL REFERENCES users(user_id),
    written_off_by      UUID REFERENCES users(user_id),
    write_off_reason    TEXT,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT now(),
    settled_at          TIMESTAMPTZ
);
CREATE INDEX idx_deferrals_status ON deferrals(branch_id, status);

CREATE TABLE deferral_items (
    deferral_id       UUID NOT NULL REFERENCES deferrals(deferral_id) ON DELETE CASCADE,
    invoice_item_id   UUID NOT NULL UNIQUE REFERENCES invoice_items(invoice_item_id),
    PRIMARY KEY (deferral_id, invoice_item_id)
);

-- مصاريف الفروع (رواتب، إيجار، صيانة...): من درج الوردية أو من صندوق الفرع
CREATE TABLE expenses (
    expense_id        UUID PRIMARY KEY,
    expense_number    VARCHAR(30) NOT NULL UNIQUE,              -- مثل B1-EXP-000123
    branch_id         UUID NOT NULL REFERENCES branches(branch_id),
    category          VARCHAR(50) NOT NULL,                     -- lookup_values.code (category = expense_category)
    amount            BIGINT NOT NULL CHECK (amount > 0),
    expense_date      DATE NOT NULL,                            -- بتوقيت بغداد
    description       TEXT NOT NULL,
    payee             VARCHAR(150),
    paid_from         VARCHAR(20) NOT NULL CHECK (paid_from IN ('shift_cash', 'treasury')),
    shift_id          UUID REFERENCES shifts(shift_id),         -- نقد من درج هذه الوردية
    status            VARCHAR(20) NOT NULL DEFAULT 'recorded' CHECK (status IN ('recorded', 'void')),
    created_by        UUID NOT NULL REFERENCES users(user_id),
    created_at        TIMESTAMPTZ NOT NULL DEFAULT now(),
    voided_by         UUID REFERENCES users(user_id),
    voided_at         TIMESTAMPTZ,
    void_reason       TEXT,
    CHECK ((paid_from = 'shift_cash') = (shift_id IS NOT NULL))
);
CREATE INDEX idx_expenses_branch_date ON expenses(branch_id, expense_date);
CREATE INDEX idx_expenses_shift ON expenses(shift_id) WHERE shift_id IS NOT NULL;

-- =============================================================================
-- 14. الصيدلية - مخزون (Phase 2: البنية جاهزة وغير مستخدمة في Phase 1)
-- =============================================================================

CREATE TABLE inventory_items (
    item_id           UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    branch_id         UUID NOT NULL REFERENCES branches(branch_id),
    drug_name         VARCHAR(200) NOT NULL,
    scientific_name   VARCHAR(200),
    stock_quantity    INT NOT NULL DEFAULT 0 CHECK (stock_quantity >= 0),
    unit_price        BIGINT NOT NULL CHECK (unit_price >= 0),
    expiry_date       DATE,
    created_at        TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX idx_inventory_branch ON inventory_items(branch_id);

-- =============================================================================
-- 15. الإشعارات وأجهزة Push
-- =============================================================================

CREATE TABLE notifications (
    notification_id     UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id             UUID REFERENCES users(user_id) ON DELETE CASCADE,
    patient_account_id  UUID REFERENCES patient_accounts(patient_account_id) ON DELETE CASCADE,
    branch_id           UUID REFERENCES branches(branch_id),
    type                VARCHAR(50) NOT NULL,                   -- lab.critical_value, shift.closed_with_variance...
    title               VARCHAR(150) NOT NULL,
    message             TEXT NOT NULL,
    link_type           VARCHAR(30),
    link_id             UUID,
    is_read             BOOLEAN NOT NULL DEFAULT FALSE,
    read_at             TIMESTAMPTZ,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT now(),
    CHECK ((user_id IS NULL) <> (patient_account_id IS NULL))
);
CREATE INDEX idx_notifications_user_unread ON notifications(user_id, created_at DESC) WHERE NOT is_read;
CREATE INDEX idx_notifications_account ON notifications(patient_account_id, created_at DESC);

CREATE TABLE push_devices (
    push_device_id      UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id             UUID REFERENCES users(user_id) ON DELETE CASCADE,
    patient_account_id  UUID REFERENCES patient_accounts(patient_account_id) ON DELETE CASCADE,
    push_token          VARCHAR(500) NOT NULL UNIQUE,
    platform            VARCHAR(10) NOT NULL CHECK (platform IN ('android', 'ios', 'web')),
    app                 VARCHAR(10) NOT NULL CHECK (app IN ('patient', 'staff')),
    created_at          TIMESTAMPTZ NOT NULL DEFAULT now(),
    CHECK ((user_id IS NULL) <> (patient_account_id IS NULL))
);

-- الرسائل الصادرة (SMS و Push). في الوضع التجريبي تُسجل بحالة sandbox ولا تُرسل
CREATE TABLE outbound_messages (
    message_id          UUID PRIMARY KEY,
    channel             VARCHAR(10) NOT NULL CHECK (channel IN ('sms', 'push')),
    recipient           VARCHAR(500) NOT NULL,                  -- رقم الهاتف أو توكن الجهاز
    patient_account_id  UUID REFERENCES patient_accounts(patient_account_id) ON DELETE SET NULL,
    user_id             UUID REFERENCES users(user_id) ON DELETE SET NULL,
    text                TEXT NOT NULL,
    status              VARCHAR(10) NOT NULL CHECK (status IN ('sandbox', 'sent', 'failed')),
    error               TEXT,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT now(),
    data                JSONB                                   -- بيانات Push: النوع والرابط والصوت والأولوية
);
CREATE INDEX idx_outbound_recipient ON outbound_messages(recipient, created_at DESC);

-- سجل النسخ الاحتياطي (pgBackRest). السكربت في حاوية قاعدة البيانات يكتب هنا، ولوحة المدير تقرأ وتطلب تشغيلاً يدوياً
CREATE TABLE backup_runs (
    run_id            UUID PRIMARY KEY,
    type              VARCHAR(10) NOT NULL CHECK (type IN ('full', 'diff', 'incr', 'verify', 'files')),
    trigger           VARCHAR(10) NOT NULL DEFAULT 'schedule' CHECK (trigger IN ('schedule', 'manual')),
    status            VARCHAR(10) NOT NULL CHECK (status IN ('requested', 'running', 'succeeded', 'failed')),
    requested_by      UUID REFERENCES users(user_id),
    started_at        TIMESTAMPTZ,
    finished_at       TIMESTAMPTZ,
    backup_label      VARCHAR(100),                           -- اسم النسخة في pgBackRest
    size_bytes        BIGINT,                                 -- حجم قاعدة البيانات قبل الضغط
    stored_bytes      BIGINT,                                 -- الحجم المخزن بعد الضغط
    repos             VARCHAR(50),                            -- المستودعات التي وصلتها النسخة: 1 أو 1,2
    error             TEXT,
    alerted_at        TIMESTAMPTZ,                            -- وقت تنبيه المدير بالفشل
    created_at        TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX idx_backup_runs_created ON backup_runs(created_at DESC);
CREATE INDEX idx_backup_runs_pending ON backup_runs(status) WHERE status IN ('requested', 'running');

-- =============================================================================
-- 16. منع التكرار والمزامنة وأحداث الوكيل المحلي
-- =============================================================================

-- Idempotency-Key: إعادة نفس الطلب تُرجع نفس الرد
CREATE TABLE idempotency_keys (
    idempotency_key   UUID NOT NULL,
    scope             VARCHAR(100) NOT NULL,                    -- user_id أو patient_account_id + المسار
    request_hash      VARCHAR(64) NOT NULL,
    response_status   SMALLINT,
    response_body     JSONB,
    created_at        TIMESTAMPTZ NOT NULL DEFAULT now(),
    expires_at        TIMESTAMPTZ NOT NULL DEFAULT now() + INTERVAL '72 hours',
    PRIMARY KEY (idempotency_key, scope)
);
CREATE INDEX idx_idempotency_expiry ON idempotency_keys(expires_at);

-- كل عملية وصلت من صندوق الأوفلاين (Outbox)
CREATE TABLE sync_operations (
    op_id           UUID PRIMARY KEY,
    device_id       UUID REFERENCES devices(device_id),
    user_id         UUID REFERENCES users(user_id),
    type            VARCHAR(40) NOT NULL,
    occurred_at     TIMESTAMPTZ NOT NULL,
    received_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
    status          VARCHAR(10) NOT NULL CHECK (status IN ('applied', 'duplicate', 'conflict', 'rejected')),
    payload         JSONB NOT NULL,
    result          JSONB
);
CREATE INDEX idx_sync_ops_review ON sync_operations(device_id, received_at) WHERE status IN ('conflict', 'rejected');

-- سجل الدرج والطابعة من الوكيل المحلي
CREATE TABLE agent_events (
    event_id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    device_id         UUID NOT NULL REFERENCES devices(device_id),
    local_event_id    BIGINT NOT NULL,
    type              VARCHAR(40) NOT NULL CHECK (type IN (
                          'receipt.printed', 'drawer.opened', 'drawer.opened_without_receipt',
                          'printer.error', 'paper.out')),
    receipt_number    VARCHAR(30),
    occurred_at       TIMESTAMPTZ NOT NULL,
    synced_at         TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (device_id, local_event_id)
);
CREATE INDEX idx_agent_events_alerts ON agent_events(device_id, occurred_at) WHERE type = 'drawer.opened_without_receipt';

-- =============================================================================
-- 17. سجل التدقيق (للإضافة فقط)
-- =============================================================================

CREATE TABLE audit_logs (
    log_id          BIGSERIAL PRIMARY KEY,
    branch_id       UUID REFERENCES branches(branch_id),
    user_id         UUID REFERENCES users(user_id),
    action          VARCHAR(60) NOT NULL,                       -- REFUND_CREATED, SHIFT_CLOSED, PRICE_CHANGED...
    entity_type     VARCHAR(40) NOT NULL,
    entity_id       UUID,
    before          JSONB,
    after           JSONB,
    reason          TEXT,
    ip_address      INET,
    device_id       UUID REFERENCES devices(device_id),
    occurred_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
    synced_offline  BOOLEAN NOT NULL DEFAULT FALSE
);
CREATE INDEX idx_audit_branch_time ON audit_logs(branch_id, occurred_at DESC);
CREATE INDEX idx_audit_user ON audit_logs(user_id, occurred_at DESC);
CREATE INDEX idx_audit_entity ON audit_logs(entity_type, entity_id);

-- منع تعديل أو حذف سجل التدقيق
CREATE FUNCTION audit_logs_block_changes() RETURNS trigger AS $$
BEGIN
    RAISE EXCEPTION 'audit_logs is append-only';
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER trg_audit_logs_no_update BEFORE UPDATE OR DELETE ON audit_logs
    FOR EACH ROW EXECUTE FUNCTION audit_logs_block_changes();

-- =============================================================================
-- 18. الإعدادات الافتراضية
-- =============================================================================

INSERT INTO system_settings (branch_id, key, value) VALUES
    (NULL, 'ban.no_show_limit',             '3'),
    (NULL, 'ban.window_days',               '90'),
    (NULL, 'ban.unban_deposit_amount',      '10000'),
    (NULL, 'booking.cancel_min_hours',      '2'),
    (NULL, 'booking.online_deposit_amount', '5000'),
    (NULL, 'visit.follow_up_window_days',   '14'),
    (NULL, 'refund.cashier_limit',          '50000'),
    (NULL, 'prescription.valid_days',       '30'),
    (NULL, 'consultation.edit_window_hours','24'),
    (NULL, 'receipt.block_size',            '200'),
    (NULL, 'booking.reminder_minutes',      '60'),
    (NULL, 'booking.arrive_early_minutes',  '20'),
    (NULL, 'queue.turn_soon_ahead',         '3'),
    (NULL, 'queue.auto_call_next',          '0'),
    (NULL, 'queue.call_alert_repeats',      '3'),
    (NULL, 'queue.call_next_by',            '"both"');

-- أنظمة الأجور وسجل الصرف (الترحيل 015)
CREATE TABLE staff_pay_plans (
    plan_id         UUID PRIMARY KEY,
    branch_id       UUID NOT NULL REFERENCES branches(branch_id),
    user_id         UUID NOT NULL REFERENCES users(user_id),
    model           VARCHAR(12) NOT NULL CHECK (model IN ('percentage', 'fixed', 'per_case')),
    percent         NUMERIC(5, 2) CHECK (percent IS NULL OR (percent >= 0 AND percent <= 100)),
    fixed_monthly   BIGINT CHECK (fixed_monthly IS NULL OR fixed_monthly >= 0),
    per_case_amount BIGINT CHECK (per_case_amount IS NULL OR per_case_amount >= 0),
    effective_from  DATE NOT NULL,
    created_by      UUID NOT NULL REFERENCES users(user_id),
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (branch_id, user_id, effective_from)
);
CREATE INDEX idx_pay_plans_user ON staff_pay_plans(branch_id, user_id, effective_from DESC);
CREATE TABLE payouts (
    payout_id       UUID PRIMARY KEY,
    branch_id       UUID NOT NULL REFERENCES branches(branch_id),
    user_id         UUID NOT NULL REFERENCES users(user_id),
    period_from     DATE NOT NULL,
    period_to       DATE NOT NULL,
    amount          BIGINT NOT NULL CHECK (amount > 0),
    method          VARCHAR(10) NOT NULL CHECK (method IN ('cash', 'transfer')),
    note            VARCHAR(500),
    created_by      UUID NOT NULL REFERENCES users(user_id),
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    CHECK (period_to >= period_from)
);
CREATE INDEX idx_payouts_branch ON payouts(branch_id, created_at DESC);

-- دورة التحويلات (الترحيل 014): مستقلة عن حالة الطلب والدفع (العقد 23)
CREATE TABLE referrals (
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
CREATE INDEX idx_referrals_branch_service ON referrals(branch_id, service, status, created_at DESC);
CREATE INDEX idx_referrals_patient ON referrals(patient_id, created_at DESC);
CREATE INDEX idx_referrals_expiry ON referrals(expires_at) WHERE status = 'referred';
ALTER TABLE notifications ADD COLUMN priority VARCHAR(6) NOT NULL DEFAULT 'normal';
ALTER TABLE notifications ADD CONSTRAINT notifications_priority_check CHECK (priority IN ('low', 'normal', 'high'));
ALTER TABLE medical_orders ADD COLUMN critical_flagged BOOLEAN NOT NULL DEFAULT FALSE;
ALTER TABLE medical_orders ADD COLUMN critical_notified_at TIMESTAMPTZ;
ALTER TABLE outbound_messages DROP CONSTRAINT outbound_messages_status_check;
ALTER TABLE outbound_messages ADD CONSTRAINT outbound_messages_status_check CHECK (status IN ('sandbox', 'queued', 'sent', 'failed'));
INSERT INTO system_settings (branch_id, key, value) VALUES (NULL, 'referral.expiry_days', '7'), (NULL, 'results.show_deferred_to_patient', '0');
INSERT INTO system_settings (branch_id, key, value) VALUES (NULL, 'queue.free_return_days', '7');   -- الترحيل 018

-- عداد رسائل المساعد الذكي لكل حساب (الترحيل 013). لا يُخزن نص أي رسالة.
CREATE TABLE assistant_usage (
    patient_account_id  UUID NOT NULL REFERENCES patient_accounts(patient_account_id) ON DELETE CASCADE,
    window_start        TIMESTAMPTZ NOT NULL,
    messages            INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (patient_account_id, window_start)
);
CREATE INDEX idx_notifications_branch ON notifications(branch_id, created_at DESC) WHERE user_id IS NOT NULL;  -- الترحيل 012

-- الأدوار الثمانية وصلاحياتها (الترحيل 009). المدير العام يملك كل الصلاحيات دائماً (تُحسب في الكود ولا تُخزن).
INSERT INTO roles (code, name_ar, is_system) VALUES
    ('admin',          'مدير عام',      TRUE),
    ('branch_manager', 'مدير فرع',      TRUE),
    ('doctor',         'طبيب',          TRUE),
    ('receptionist',   'موظف استقبال',  TRUE),
    ('accountant',     'محاسب عام',     TRUE),
    ('lab_tech',       'فني مختبر',     TRUE),
    ('radiologist',    'أخصائي أشعة',   TRUE),
    ('pharmacist',     'صيدلي',         TRUE);

INSERT INTO role_permissions (role_code, permission) VALUES
    ('branch_manager', 'users.manage'), ('branch_manager', 'services.manage'), ('branch_manager', 'patients.write'),
    ('branch_manager', 'patients.unban'), ('branch_manager', 'queue.manage'), ('branch_manager', 'discounts.grant'),
    ('branch_manager', 'refunds.approve'), ('branch_manager', 'shifts.review'), ('branch_manager', 'deferrals.grant'),
    ('branch_manager', 'reports.finance'), ('branch_manager', 'expenses.manage'),
    ('branch_manager', 'roles.view'), ('branch_manager', 'branches.view'),
    ('branch_manager', 'lab.view'), ('branch_manager', 'radiology.view'), ('branch_manager', 'pharmacy.view'),   -- الترحيل 012
    ('branch_manager', 'notifications.branch_view'), ('branch_manager', 'backups.view'), ('branch_manager', 'audit.read'),
    ('branch_manager', 'patients.merge'),                                                                        -- الترحيل 017
    ('doctor', 'clinical.write'), ('doctor', 'queue.manage'), ('doctor', 'deferrals.grant'),
    ('receptionist', 'queue.manage'), ('receptionist', 'payments.collect'), ('receptionist', 'patients.write'),
    ('accountant', 'shifts.review'), ('accountant', 'refunds.approve'), ('accountant', 'reports.finance'),
    ('accountant', 'patients.unban'), ('accountant', 'payments.collect'), ('accountant', 'audit.read'),
    ('accountant', 'expenses.manage'),
    ('lab_tech', 'lab.write'),
    ('radiologist', 'radiology.write'),
    ('pharmacist', 'pharmacy.dispense');

INSERT INTO lookup_values (category, code, name_ar, sort_order, is_system) VALUES
    ('closure_reason',   'travel', 'سفر', 10, FALSE),
    ('closure_reason',   'maintenance', 'صيانة', 20, FALSE),
    ('closure_reason',   'breakdown', 'عطل فني', 30, FALSE),
    ('closure_reason',   'holiday', 'عطلة رسمية', 40, FALSE),
    ('closure_reason',   'staff_shortage', 'نقص كادر', 50, FALSE),
    ('closure_reason',   'opening_soon', 'افتتاح قريباً', 60, FALSE),
    ('closure_reason',   'other', 'أخرى', 999, TRUE),
    ('doctor_specialty', 'internal', 'باطنية', 10, FALSE),
    ('doctor_specialty', 'pediatrics', 'أطفال', 20, FALSE),
    ('doctor_specialty', 'cardiology', 'قلبية', 30, FALSE),
    ('doctor_specialty', 'obgyn', 'نسائية وتوليد', 40, FALSE),
    ('doctor_specialty', 'general_surgery', 'جراحة عامة', 50, FALSE),
    ('doctor_specialty', 'orthopedics', 'عظام وكسور', 60, FALSE),
    ('doctor_specialty', 'dermatology', 'جلدية', 70, FALSE),
    ('doctor_specialty', 'ent', 'أنف وأذن وحنجرة', 80, FALSE),
    ('doctor_specialty', 'ophthalmology', 'عيون', 90, FALSE),
    ('doctor_specialty', 'dentistry', 'أسنان', 100, FALSE),
    ('doctor_specialty', 'urology', 'مسالك بولية', 110, FALSE),
    ('doctor_specialty', 'neurology', 'جملة عصبية', 120, FALSE),
    ('doctor_specialty', 'psychiatry', 'نفسية', 130, FALSE),
    ('doctor_specialty', 'chest', 'صدرية', 140, FALSE),
    ('doctor_specialty', 'endocrinology', 'غدد صماء وسكري', 150, FALSE),
    ('doctor_specialty', 'nephrology', 'كلى', 160, FALSE),
    ('doctor_specialty', 'gastroenterology', 'جهاز هضمي', 170, FALSE),
    ('doctor_specialty', 'nutrition', 'تغذية', 180, FALSE),
    ('doctor_specialty', 'family_medicine', 'طب الأسرة', 190, FALSE),
    ('doctor_specialty', 'physiotherapy', 'علاج طبيعي', 200, FALSE),
    ('doctor_job_title', 'consultant', 'استشاري', 10, FALSE),
    ('doctor_job_title', 'specialist', 'أخصائي', 20, FALSE),
    ('doctor_job_title', 'practitioner', 'طبيب ممارس', 30, FALSE),
    ('doctor_job_title', 'resident', 'طبيب مقيم', 40, FALSE),
    ('doctor_job_title', 'professor', 'أستاذ دكتور', 50, FALSE),
    ('expense_category', 'salaries', 'رواتب وأجور', 10, FALSE),
    ('expense_category', 'rent', 'إيجار', 20, FALSE),
    ('expense_category', 'utilities', 'كهرباء ومولدة وماء', 30, FALSE),
    ('expense_category', 'maintenance', 'صيانة', 40, FALSE),
    ('expense_category', 'medical_supplies', 'مستلزمات طبية', 50, FALSE),
    ('expense_category', 'lab_supplies', 'مواد مختبر', 60, FALSE),
    ('expense_category', 'office_supplies', 'قرطاسية ومستلزمات مكتبية', 70, FALSE),
    ('expense_category', 'cleaning', 'تنظيف وضيافة', 80, FALSE),
    ('expense_category', 'fuel', 'وقود', 90, FALSE),
    ('expense_category', 'other', 'أخرى', 999, TRUE);

COMMIT;
