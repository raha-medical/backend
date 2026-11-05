-- تراجع الترحيل 013: يحذف عداد المساعد الذكي (لا يحوي نص رسائل).
BEGIN;
DROP TABLE IF EXISTS assistant_usage;
COMMIT;
