-- =============================================================================
-- ترحيل 026: المساعد الذكي (المرحلة 2 لـ Gemini)
--   * assistant_usage.window_start صار بداية اليوم بتوقيت بغداد (حد يومي بدل الساعة). صفوف الساعات القديمة تُحذف.
--   * assistant_daily_stats: أرقام يومية فقط، بلا أي نص من المحادثات (الرسائل، المراجعون، الطوارئ، الرفض،
--     تجاوز الحد، الحجوزات المقترحة، الأخطاء حسب النوع، زمن الرد).
--   * assistant_proposals: معرف كل اقتراح حجز (booking_id) ليُحسب عدد الحجوزات المؤكدة فعلاً من الاقتراحات. بلا نص.
--   * الإعدادان العامان assistant.enabled و assistant.daily_limit في system_settings (غير الموجود = 1 و 30).
-- إضافات آمنة، ويمكن تشغيله أكثر من مرة. التراجع: down/026_assistant_daily_down.sql
-- =============================================================================

BEGIN;

-- صفوف الساعات (قبل 026) لا تطابق بداية اليوم: تُحذف مرة واحدة (عداد مؤقت فقط)
DELETE FROM assistant_usage
WHERE window_start <> (date_trunc('day', window_start AT TIME ZONE 'Asia/Baghdad') AT TIME ZONE 'Asia/Baghdad');
COMMENT ON COLUMN assistant_usage.window_start IS 'بداية اليوم بتوقيت بغداد (الترحيل 026)';

CREATE TABLE IF NOT EXISTS assistant_daily_stats (
    stat_date            DATE PRIMARY KEY,                       -- اليوم بتوقيت بغداد
    messages             INTEGER NOT NULL DEFAULT 0,             -- رسائل أُجيبت (مع الطوارئ)
    patients             INTEGER NOT NULL DEFAULT 0,             -- حسابات راسلت المساعد في هذا اليوم
    emergencies          INTEGER NOT NULL DEFAULT 0,
    refusals             INTEGER NOT NULL DEFAULT 0,             -- رفض نصيحة طبية، أو بيانات شخص آخر، أو خارج الاختصاص
    limit_hits           INTEGER NOT NULL DEFAULT 0,             -- رسائل رُفضت لتجاوز الحد اليومي
    proposals            INTEGER NOT NULL DEFAULT 0,             -- اقتراحات حجز
    errors_rate_limited  INTEGER NOT NULL DEFAULT 0,             -- المزود رد 429
    errors_server        INTEGER NOT NULL DEFAULT 0,             -- 5xx
    errors_auth          INTEGER NOT NULL DEFAULT 0,             -- 400/401/403 (مثل مفتاح خاطئ)
    errors_timeout       INTEGER NOT NULL DEFAULT 0,
    errors_network       INTEGER NOT NULL DEFAULT 0,
    errors_other         INTEGER NOT NULL DEFAULT 0,
    latency_ms_total     BIGINT  NOT NULL DEFAULT 0,             -- مجموع زمن الرد (للمتوسط)
    latency_count        INTEGER NOT NULL DEFAULT 0,
    latency_ms_max       INTEGER NOT NULL DEFAULT 0,
    updated_at           TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS assistant_proposals (
    booking_id          UUID PRIMARY KEY,                        -- يرسله التطبيق نفسه في POST /bookings عند التأكيد
    patient_account_id  UUID NOT NULL REFERENCES patient_accounts(patient_account_id) ON DELETE CASCADE,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS idx_assistant_proposals_created ON assistant_proposals(created_at);

COMMIT;
