-- تراجع الترحيل 015: يحذف أنظمة الأجور وسجل الصرف. سجل التدقيق يبقى.
BEGIN;
DROP TABLE IF EXISTS payouts;
DROP TABLE IF EXISTS staff_pay_plans;
COMMIT;
