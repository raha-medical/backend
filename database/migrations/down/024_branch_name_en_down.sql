-- تراجع الترحيل 024: يحذف الاسم الإنجليزي للفروع.
ALTER TABLE branches DROP COLUMN IF EXISTS name_en;
