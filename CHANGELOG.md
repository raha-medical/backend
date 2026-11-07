# سجل التغييرات (backend)

## feature/patient-alerts (2026-11-07)
- رد المراجع على «حان دورك» في `GET /queue/live`: `call_ack_status` (acknowledged | waiting | no_response) و`call_ack_deadline`، و`call_ack_timeout_minutes` للوجبة.
- إعداد الفرع `queue.call_ack_timeout_minutes` (3، من 1 إلى 30).

## fix/notifications (2026-11-07) — الترحيل 027
- «اقترب دورك» يُرسل يوم الموعد فقط وبعد بدء العمل (كان يُرسل فور الحجز لأيام قادمة).
- نص إلغاء الطبيب حسب الدفع: الدافع تُذكر له الخيارات (إعادة الحجز، رصيد، استرجاع) مع رابط `booking_cancel_options`، وغير الدافع بلا مبلغ ويُقترح عليه إعادة الحجز.
- إشعارات محفوظة للمريض: `online_payment.succeeded` و`online_payment.failed`.
- «تأخر الطبيب»: `POST /queue/sessions/{id}/delay` يؤخر بداية العمل ويُخطر المحجوزين بـ `booking.doctor_delayed`.
- دور «المتابعة والإشعارات» (`followup`) في الترحيل بدل بيانات التطوير فقط.

## v1.2-handoff (2026-11-07)

### خريطة الفروع وإحصائياتها (الترحيل 024)
- `GET /reports/stats`: إحصائيات بفترة ومقارنة بالفترة السابقة، الدينار والدولار منفصلان، حسب الأقسام وطرق الدفع والفروع والأطباء وأيام الأسبوع.
- `GET /reports/branches-overview`: الطابور الآن، وإيراد اليوم بالدولار، والحجوزات الإلكترونية، والاسترجاعات، والحسابات المفتوحة لكل فرع.
- `POST /branches`: الموقع (`latitude`/`longitude`) إلزامي، و`name_en` جديد، والاسم لا يتكرر. الإضافة وتغيير الموقع والإيقاف للمدير العام فقط، ولا حذف للفروع.

### النسخ الاحتياطي الأصلي على Windows (الترحيل 025)
- `Backup__Mode=native`: الـ API ينفذ `pg_dump` ويتحقق من النسخة، وينسخ الملفات المرفوعة ومفتاح الدرج.
- نسخة يومية بوقت يختاره المدير العام (`PUT /admin/backups/settings`) مع اللحاق، و«نسخ الآن» للمدير العام فقط، وقرص خارجي اختياري، والاحتفاظ بـ 14، ومهلة 10 دقائق. دليل الاسترجاع: `docs/backup_restore.md`.

### مساعد Gemini في تطبيق المراجعين (الترحيل 026)
- مزود Gemini (`gemini-3.5-flash-lite` افتراضياً): `toolConfig` AUTO، والتفكير minimal، وحفظ توقيعات التفكير، وإعادة المحاولة عند 400 بلا إعداد التفكير وعند 429/503 مرة واحدة. المفتاح من `Assistant__ApiKey` أو `GEMINI_API_KEY`، ولا يظهر في الردود أو السجلات.
- 8 أدوات جديدة: `list_specialties`، `find_doctors`، `get_doctor_availability`، `propose_booking` (اقتراح فقط بلا حجز)، `search_faq`، `get_payment_methods`، `get_my_invoices`، `get_my_credit`.
- حد يومي لكل حساب (`assistant.daily_limit`، 30) وتشغيل من الإعدادات العامة (`assistant.enabled`)، وطوارئ أوسع مع 122، وفحص الرد من الجرعات، ولا قيم نتائج ولا جرعات للنموذج.
- نقاط جديدة: `GET /admin/assistant/stats` (أرقام فقط)، و`GET /me/patients/{patientId}/credit`. وحقول جديدة في `POST /assistant/chat`: `proposal`، `actions`، `daily_limit`، `remaining_today`.
