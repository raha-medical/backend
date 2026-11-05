-- =============================================================================
-- ترحيل 001: الإغلاق المؤقت للعيادات والأقسام + قوائم الاختيار القابلة للتوسعة
--   * clinic_closures: إغلاق مؤقت (سفر، صيانة، عطل فني، عطلة...)
--   * lookup_values: قوائم يديرها المدير (أسباب الإغلاق، الاختصاصات، العناوين الوظيفية)
--   * doctors.job_title: العنوان الوظيفي للطبيب
-- آمن على قاعدة فيها بيانات: إضافات فقط، ويمكن تشغيله أكثر من مرة.
-- التشغيل: psql "$DATABASE_URL" -f database/migrations/001_closures_and_lookups.sql
-- =============================================================================

BEGIN;

CREATE TABLE IF NOT EXISTS lookup_values (
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
CREATE UNIQUE INDEX IF NOT EXISTS uq_lookup_values_name ON lookup_values(category, name_ar);

INSERT INTO lookup_values (category, code, name_ar, sort_order, is_system) VALUES
    ('closure_reason',   'travel', 'سفر', 10, FALSE),
    ('closure_reason',   'maintenance', 'صيانة', 20, FALSE),
    ('closure_reason',   'breakdown', 'عطل فني', 30, FALSE),
    ('closure_reason',   'holiday', 'عطلة رسمية', 40, FALSE),
    ('closure_reason',   'staff_shortage', 'نقص كادر', 50, FALSE),
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
    ('doctor_job_title', 'professor', 'أستاذ دكتور', 50, FALSE)
ON CONFLICT (category, code) DO NOTHING;

-- الاختصاصات المكتوبة سابقاً لأطباء موجودين تُضاف للقائمة حتى تبقى صالحة
INSERT INTO lookup_values (category, code, name_ar, sort_order)
SELECT 'doctor_specialty', 'custom_' || substr(md5(specialty), 1, 10), specialty, 500
  FROM (SELECT DISTINCT trim(specialty) AS specialty FROM doctors WHERE trim(specialty) <> '') d
 WHERE NOT EXISTS (SELECT 1 FROM lookup_values l WHERE l.category = 'doctor_specialty' AND l.name_ar = d.specialty)
ON CONFLICT DO NOTHING;

ALTER TABLE doctors ADD COLUMN IF NOT EXISTS job_title VARCHAR(100);

CREATE TABLE IF NOT EXISTS clinic_closures (
    closure_id      UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    clinic_id       UUID NOT NULL REFERENCES clinics(clinic_id),
    reason          VARCHAR(50) NOT NULL,                       -- lookup_values.code (category = closure_reason)
    note            TEXT,
    starts_on       DATE NOT NULL,                              -- بتوقيت بغداد
    ends_on         DATE,                                       -- NULL = حتى إعادة الفتح يدوياً
    created_by      UUID REFERENCES users(user_id),
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    ended_at        TIMESTAMPTZ,                                -- أُعيد الفتح مبكراً أو أُلغي الإغلاق
    ended_by        UUID REFERENCES users(user_id),
    CHECK (ends_on IS NULL OR ends_on >= starts_on)
);
CREATE INDEX IF NOT EXISTS idx_clinic_closures_clinic ON clinic_closures(clinic_id, starts_on);

COMMIT;
