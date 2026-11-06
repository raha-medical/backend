-- تراجع الترحيل 026: يحذف إحصاءات المساعد واقتراحاته والإعدادين العامين. عداد assistant_usage يبقى (يعود للساعة مع كود ما قبل 026).
BEGIN;
DROP TABLE IF EXISTS assistant_proposals;
DROP TABLE IF EXISTS assistant_daily_stats;
DELETE FROM assistant_usage;
COMMENT ON COLUMN assistant_usage.window_start IS NULL;
DELETE FROM system_settings WHERE branch_id IS NULL AND key IN ('assistant.enabled', 'assistant.daily_limit');
COMMIT;
