# نظام مجمع الرحمة الطبي - الباك إند

## التشغيل لأول مرة
1. المتطلبات: .NET 8 SDK، PostgreSQL 16، Node.js 20، والمستودعات الثلاثة (backend, frontend, dock) على الفرع `staging` في مجلد واحد، وانسخ ملفات التشغيل من `dock/local-setup` إلى جذر المجلد (انظر README فيه).
2. انسخ `.env.example` إلى `.env` (وكلمة مرور PostgreSQL في `local-env.bat` بجانب المستودعات).
3. `setup-db.bat alrahma_staging`: ينشئ قاعدة فارغة ويطبّق المخطط ثم **كل** ملفات `database/migrations/*.sql` بالترتيب (حالياً 001 إلى 027، أي ملف جديد يُطبَّق تلقائياً) ثم البيانات التجريبية. `start-native.bat` يطبّق الترحيلات نفسها عند كل تشغيل (آمنة للتكرار).
4. `..\start-native.bat`: يشغّل الخادم (8089) والواجهة (3001) والتخزين.
5. الحسابات: [`dock/docs/handoff/accounts.md`](../dock/docs/handoff/accounts.md)، والعقود: [`docs/api_contracts.md`](docs/api_contracts.md).

ASP.NET Core 8 + PostgreSQL 16. المرجع الكامل للـ API في [`docs/api_contracts.md`](docs/api_contracts.md)،
وقاعدة البيانات في [`database/schema_v5.sql`](database/schema_v5.sql).

## البنية

| المشروع | المحتوى |
| --- | --- |
| `src/AlRahma.Domain` | الكيانات، الأدوار والصلاحيات، توليد UUID v7 |
| `src/AlRahma.Application` | منطق العمل (المصادقة، المستخدمين، الفروع) |
| `src/AlRahma.Infrastructure` | EF Core على الجداول الموجودة، Argon2id، JWT، سجل التدقيق |
| `src/AlRahma.Api` | الـ Controllers، صيغة الأخطاء، Idempotency-Key، Swagger |
| `tests/AlRahma.Api.Tests` | اختبارات تكامل على PostgreSQL حقيقي |

قاعدة البيانات هي المرجع: الكود **لا ينشئ ولا يعدل** الجداول. أي تغيير على القاعدة يكون بسكربت SQL جديد في `database/`.

## التشغيل السريع (Docker)

```bash
docker compose up --build
```

- الـ API على `http://localhost:8088`، و Swagger على `http://localhost:8088/swagger`.
- أول تشغيل ينشئ الفرع `B1` والمدير `admin` بكلمة المرور `Admin-Change-Me-2026` (يُطلب تغييرها عند أول دخول).

## التشغيل على قاعدة موجودة

```bash
export ConnectionStrings__Default="Host=localhost;Port=5432;Database=alrahma;Username=...;Password=..."
export Jwt__SigningKey="مفتاح عشوائي طوله 32 حرفاً على الأقل"
export Bootstrap__AdminUsername=admin          # فقط عند أول تشغيل على قاعدة بلا مستخدمين
export Bootstrap__AdminPassword="كلمة-مرور-مؤقتة-قوية"
dotnet run --project src/AlRahma.Api
```

| الإعداد | الوصف |
| --- | --- |
| `ConnectionStrings__Default` | اتصال PostgreSQL |
| `Jwt__SigningKey` | مفتاح توقيع التوكنات (إلزامي، 32 حرفاً فأكثر) |
| `Jwt__AccessTokenMinutes` / `Jwt__RefreshTokenHours` | 15 دقيقة / 14 ساعة افتراضياً |
| `Cors__Origins__0` | عنوان واجهة Next.js |
| `Swagger__Enabled` | `true` لإظهار Swagger خارج بيئة التطوير |
| `Bootstrap__*` | إنشاء الفرع الأول والمدير عند أول تشغيل فقط |
| `Drawer__PrivateKeyPem` | مفتاح ES256 الخاص لتوقيع توكن فتح الدرج (بدونه مفتاح مؤقت يتغير مع كل تشغيل) |
| `Receipts__VerifyBaseUrl` | بادئة رابط QR على الإيصال، مثل `https://verify.example.iq/r/` |
| `Receipts__ComplexName` / `Receipts__Footer` | الاسم والتذييل المطبوعان على الإيصال |
| `Assistant__Provider` / `Assistant__ApiKey` / `Assistant__Model` | المساعد الذكي: `sandbox` (افتراضي، قواعد بسيطة بلا ذكاء اصطناعي) أو `gemini`. المفتاح سري على السيرفر فقط، و`gemini` بلا مفتاح يوقف التشغيل في الإنتاج |
| `Assistant__MaxMessagesPerHour` | حد رسائل المساعد لكل حساب في الساعة (30) |
| `CheckIn__SigningKey` / `CheckIn__WindowSeconds` | مفتاح HMAC لرمز QR تثبيت الحضور على شاشة الانتظار (فارغ = يُشتق من `Jwt__SigningKey`) ومدة تجدده بالثواني (45) |

### ترحيلات قاعدة موجودة

`schema_v5.sql` ينشئ القاعدة من الصفر (ويحذف ما فيها). على قاعدة فيها بيانات، شغّل ملفات `database/migrations` بالترتيب؛ كلها إضافات آمنة يمكن تكرار تشغيلها:

```bash
psql "$DATABASE_URL" -f database/migrations/001_closures_and_lookups.sql
psql "$DATABASE_URL" -f database/migrations/002_branch_management.sql
psql "$DATABASE_URL" -f database/migrations/003_branch_finance.sql
psql "$DATABASE_URL" -f database/migrations/004_admin_finance.sql
psql "$DATABASE_URL" -f database/migrations/005_clinical_and_patient_app.sql
psql "$DATABASE_URL" -f database/migrations/006_backup_and_notifications.sql
psql "$DATABASE_URL" -f database/migrations/007_flexible_clinic_day.sql
psql "$DATABASE_URL" -f database/migrations/008_reminders_and_doctor_settings.sql
psql "$DATABASE_URL" -f database/migrations/009_roles_and_permissions.sql
psql "$DATABASE_URL" -f database/migrations/010_branch_working_hours.sql
psql "$DATABASE_URL" -f database/migrations/011_booking_policy_and_self_checkin.sql
psql "$DATABASE_URL" -f database/migrations/012_branch_manager_read_access.sql
psql "$DATABASE_URL" -f database/migrations/013_assistant_usage.sql
psql "$DATABASE_URL" -f database/migrations/014_referrals_and_notifications.sql
psql "$DATABASE_URL" -f database/migrations/015_entitlements_and_payouts.sql
psql "$DATABASE_URL" -f database/migrations/016_online_payment_invoice_item.sql
psql "$DATABASE_URL" -f database/migrations/017_nav_permissions.sql
psql "$DATABASE_URL" -f database/migrations/018_returning_patients.sql
psql "$DATABASE_URL" -f database/migrations/019_payment_gateways.sql
psql "$DATABASE_URL" -f database/migrations/020_daily_account.sql
psql "$DATABASE_URL" -f database/migrations/021_support_and_patient_options.sql
psql "$DATABASE_URL" -f database/migrations/022_pharmacy_billing.sql
psql "$DATABASE_URL" -f database/migrations/023_currencies.sql
psql "$DATABASE_URL" -f database/migrations/024_branch_name_en.sql
psql "$DATABASE_URL" -f database/migrations/025_native_backup.sql
psql "$DATABASE_URL" -f database/migrations/026_assistant_daily.sql
psql "$DATABASE_URL" -f database/migrations/027_notifications_followup_delay.sql
```

**التراجع:** من الترحيل 009 فصاعداً لكل ترحيل سكربت تراجع في `database/migrations/down/` يُشغل بالترتيب العكسي:

```bash
psql "$DATABASE_URL" -f database/migrations/down/027_notifications_followup_delay_down.sql
psql "$DATABASE_URL" -f database/migrations/down/026_assistant_daily_down.sql
psql "$DATABASE_URL" -f database/migrations/down/025_native_backup_down.sql
psql "$DATABASE_URL" -f database/migrations/down/024_branch_name_en_down.sql
psql "$DATABASE_URL" -f database/migrations/down/023_currencies_down.sql
psql "$DATABASE_URL" -f database/migrations/down/022_pharmacy_billing_down.sql
psql "$DATABASE_URL" -f database/migrations/down/021_support_and_patient_options_down.sql
psql "$DATABASE_URL" -f database/migrations/down/020_daily_account_down.sql
psql "$DATABASE_URL" -f database/migrations/down/019_payment_gateways_down.sql
psql "$DATABASE_URL" -f database/migrations/down/018_returning_patients_down.sql
psql "$DATABASE_URL" -f database/migrations/down/017_nav_permissions_down.sql
psql "$DATABASE_URL" -f database/migrations/down/016_online_payment_invoice_item_down.sql
psql "$DATABASE_URL" -f database/migrations/down/015_entitlements_and_payouts_down.sql
psql "$DATABASE_URL" -f database/migrations/down/014_referrals_and_notifications_down.sql
psql "$DATABASE_URL" -f database/migrations/down/013_assistant_usage_down.sql
psql "$DATABASE_URL" -f database/migrations/down/012_branch_manager_read_access_down.sql
psql "$DATABASE_URL" -f database/migrations/down/011_booking_policy_and_self_checkin_down.sql
psql "$DATABASE_URL" -f database/migrations/down/010_branch_working_hours_down.sql
psql "$DATABASE_URL" -f database/migrations/down/009_roles_and_permissions_down.sql   # يرفض إذا بقي موظفون على أدوار مخصصة
```

## الاختبارات

تحتاج خادم PostgreSQL؛ كل تشغيل ينشئ قاعدة مؤقتة من `schema_v5.sql` ثم يحذفها.

```bash
export TEST_DATABASE_URL="Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=postgres"
dotnet test
```

بدون `TEST_DATABASE_URL` تُتخطى اختبارات التكامل. الـ CI على GitHub Actions يشغلها مع PostgreSQL 16.

## المنجز في المرحلة الأولى

- القواعد العامة: صيغة الأخطاء الموحدة (Problem Details)، `Idempotency-Key`، JSON بصيغة snake_case، صلاحيات حسب الدور.
- المصادقة: login، refresh مع تدوير التوكن وكشف إعادة الاستخدام، logout، switch-branch، me، change-password، قفل الحساب بعد 5 محاولات، إجبار تغيير كلمة المرور المؤقتة.
- المستخدمون: قائمة، إنشاء (مع ملف الطبيب)، تعديل، تعطيل/تفعيل، إعادة تعيين كلمة المرور، الأدوار.
- الفروع والعيادات (عرض). الإضافة والتعديل أُضيفت في المرحلة الثالثة.
- سجل تدقيق لكل عملية حساسة.

## المنجز في المرحلة الثانية

- الخدمات والأسعار لكل فرع، مع سجل تدقيق لتغيير الأسعار.
- الأطباء، وتاريخ نسبهم مع تاريخ السريان (النسبة الجديدة لا تمس الفواتير السابقة).
- الجدول الأسبوعي للطبيب، وتوليد الوجبات اليومية تلقائياً مع حساب السعة وحصة الحجز الإلكتروني، وإلغاء وجبات يوم.
- صورة الطبيب: رفع مباشر من المتصفح إلى MinIO برابط موقّع، وربطها بالطبيب أو حذفها، و`photo_url` مؤقت في قوائم الأطباء.

إعدادات التخزين في قسم `Storage` (`ServiceUrl` الذي يصله السيرفر، و`PublicUrl` الذي يصله المتصفح، والمفاتيح، والـ bucket). `docker compose up` يشغّل MinIO وينشئ bucket باسم `medical`.

## المنجز في المرحلة الثالثة

- إدارة الفروع (المدير العام) والعيادات والمختبرات والأشعة والصيدليات (المدير العام ومدير الفرع).
- المرضى: إنشاء (يدعم المعرف من الواجهة للأوفلاين)، بحث موحد لكل الفروع، تعديل مع تدقيق، دمج الملفات المكررة.
- الحظر: احتساب الغياب تلقائياً والحظر عند بلوغ الحد، وفك الحظر بإعفاء المدير.
- الحجز الهاتفي من الاستقبال، والإتاحة العامة للأطباء، والإلغاء مع تحويل المدفوع إلى رصيد واحتساب الإلغاء المتأخر غياباً.
- الطابور الذكي: تثبيت الحضور (حجز أو QR أو حضور مباشر) مع فاتورة الكشفية ولقطة نسبة الطبيب، منع الدخول قبل الدفع، الاستدعاء، البدء والإنهاء، الطوارئ، التخطي والغياب، إدراج المتأخر، وإعادة حساب الأوقات بمتوسط المعاينة الفعلي.
- شاشات الانتظار بمفتاح لكل شاشة (أرقام فقط بدون أسماء)، والتحديث المباشر عبر SignalR على `/hubs/live`.
- الإغلاق المؤقت للعيادات والمختبرات والأشعة والصيدليات (سفر، صيانة، عطل فني...) مع إلغاء الحجوزات وإعادة الفتح.
- قوائم اختيار يديرها المدير (`/lookups`): أسباب الإغلاق، اختصاصات الأطباء، العناوين الوظيفية.

## المنجز في المرحلة الرابعة

- السلة الموحدة لكل مريض، والتحصيل نقداً و POS معاً مع استخدام رصيد المريض، وإيصال برقم تسلسلي و QR للتحقق العام.
- توكن فتح الدرج موقّع ES256 (60 ثانية) ومفتاحه العام للوكيل المحلي.
- الورديات: فتح بالفكة، إغلاق أعمى، مطابقة للمحاسب (عجز/زيادة) ومراجعة، وتنبيه فوري عند الإغلاق.
- الفاتورة اليدوية للمراجع المباشر مع خصم بصلاحية، وإلغاء الفاتورة غير المدفوعة.
- الاسترجاع الكلي والجزئي (نقد، POS، رصيد) بحد للاستقبال وموافقة المحاسب فوقه، مع تعديل حصة الطبيب بنسبتها.
- فك الحظر بعربون نقدي أو POS يصبح رصيداً للمريض.

## المنجز في المرحلة الخامسة (الجزء الأول من المتبقي)

- الدفع الآجل (الاستثناء) بصلاحية، والتسديد من السلة، والإعفاء النهائي للمدير العام.
- مالية الفروع: إعدادات كل فرع، تسوية الفرع، مصاريف الفروع (من الدرج أو الخزينة).
- التقارير: الإيرادات حسب القسم وطريقة الدفع، أرباح الأطباء، لوحة المدير العام لكل الفروع، وسجل التدقيق.

## المنجز في المرحلة السادسة (الجزء الثاني: اكتمال الباك إند)

- **العيادة:** المعاينة (تشخيص و ICD-10 وملاحظات خاصة للأطباء)، الوصفة المهيكلة بكود `RX-`، طلبات المختبر والأشعة كبنود في سلة المريض، نافذة تعديل ثم ملحق، والسجل الطبي الكامل عبر الفروع.
- **الطلب المباشر من الاستقبال** (ورقة طبيب خارجي) وإلغاء الطلب غير المدفوع.
- **المختبر:** قائمة العمل لكل فرع (بعد الدفع أو الاستثناء فقط)، سحب العينة بباركود تلقائي، قوالب التحاليل وحدودها، إدخال واعتماد النتيجة مع تمييز القيم غير الطبيعية والحرجة وتنبيه الطبيب، والتعديل بسبب.
- **الأشعة:** الجدولة، التنفيذ، التقرير مع الصور (MinIO) والاعتماد.
- **الصيدلية:** البحث بكود الوصفة، الصرف مرة واحدة (مع البديل أو سبب عدم الصرف)، وسجل المصروف.
- **تطبيق المريض:** دخول بالهاتف و OTP (**تجريبي حالياً: الرمز `000000`**)، ربط تلقائي لملفات نفس الرقم، ملفات العائلة، الحجز والإلغاء، الزيارات والوصفات والنتائج المعتمدة المدفوعة.
- **الدفع الإلكتروني:** دفع كامل الحجز أو عربونه، عربون فك الحظر، ودفع فاتورة، عبر **بوابة تجريبية** (`/sandbox/pay/{id}`) بإيصال إلكتروني، والاسترجاع `online_reversal`.
- **الإشعارات:** جرس داخل النظام لكل موظف ومريض، Push للتطبيق و SMS لمن لا يملكه (**تُسجل ولا تُرسل في الوضع التجريبي**، وتُعرض في `/sandbox/messages`)، تذكير قبل الموعد بساعتين، وتنبيهات القيم الحرجة والورديات والاسترجاع.
- **التقديم الذكي:** عرض من الاستقبال لأول المرضى اللاحقين، وأول من يقبل يأخذ الدور.
- **العمل أوفلاين:** كتل أرقام إيصالات موقّعة لكل جهاز، دفعة مزامنة حتى 500 عملية (idempotent بـ `op_id`)، ولقطة بيانات اليوم.
- **مهام دورية** كل دقيقة: تذكير المواعيد وإنهاء الدفعات المعلقة (`Jobs__Enabled=false` لتعطيلها).

### إعدادات الجزء الثاني

| الإعداد | الافتراضي | الوصف |
| --- | --- | --- |
| `Otp__Mode` | `sandbox` | `sandbox`: لا SMS والرمز `Otp__SandboxCode` (`000000`) ويعود في الرد. `sms` عند ربط مزود |
| `Sms__Mode` / `Push__Mode` | `sandbox` | `sandbox`: الرسائل تُسجل في `outbound_messages` فقط. `live` عند ربط المزود |
| `Push__Firebase__ProjectId` / `Push__Firebase__CredentialsFile` | — | بيانات Firebase عند `Push__Mode=live` (العقد 23.4). الملف خارج المستودع ولا يصل المتصفح. لم يُجرَّب الإرسال الفعلي بعد |
| `Payments__Mode` | `sandbox` | البوابة التجريبية فقط حالياً (زين كاش و QiCard و FIB تُربط لاحقاً) |
| `Payments__PublicBaseUrl` | فارغ | عنوان الـ API الذي يفتحه هاتف المريض لصفحة الدفع، مثل `https://api.example.iq` |
| `Payments__ExpiryMinutes` | `15` | صلاحية رابط الدفع |
| `Jwt__PatientAudience` / `Jwt__PatientRefreshDays` | `alrahma-patients` / `30` | توكنات تطبيق المريض منفصلة عن الموظفين |
| `Jobs__Enabled` | `true` | المهام الدورية (تُعطل في نسخ السيرفر الإضافية) |

ترحيل قاعدة قائمة للجزء الثاني:

```bash
psql "$DATABASE_URL" -f database/migrations/005_clinical_and_patient_app.sql
```

## المستخدمون والأدوار والفروع (المرحلة 2 من المواصفات، الترحيلان 009 و 010)

- **الأدوار والصلاحيات من القاعدة:** جدولا `roles` و `role_permissions`، والأدوار الثمانية أدوار نظام بنفس صلاحياتها السابقة. `users.role` مربوط بـ `roles.code` (FK بدل CHECK).
- **أدوار مخصصة** يديرها المدير العام (`GET /permissions/catalog`، `GET/POST/PATCH/DELETE /roles`)، تُسند للموظفين وتعمل عند الدخول. `/auth/me` يعيد `home_section` و `sections` لتوجيه الواجهة بالصلاحيات.
- **صلاحيات دقيقة** (عرض/إضافة/تعديل/حذف) للمستخدمين والأدوار والفروع والخدمات والأجهزة والإعدادات؛ `users.manage` و `services.manage` تتضمنها فلا يتغير سلوك الأدوار الحالية.
- **الصلاحيات تُحسب في كل طلب** من ذاكرة مؤقتة (`AccessCache`: تُمسح عند التعديل، وصلاحيتها 60 ثانية للأدوار و 30 للموظف بين نسخ السيرفر)؛ الموظف المعطل أو المُزال من فرع التوكن يُرفض فوراً بـ 401.
- **هيكل الفرع:** أوقات دوام مهيكلة (فترة أو فترتان لكل يوم) في `branch_working_hours` مع ملخص نصي، وفي شبكة الفروع: المدراء، عدد الأقسام، النشاط، والحالة الحية (`open` / `closed` / `open_after_hours` / `temporarily_closed` / `inactive`)، وفحص مدى الإحداثيات.
- **عزل الفروع:** مراجعة كل الـ endpoints وإغلاق التسريبات (القسم 0.11 في العقد)، مع اختبارات تكامل لمدير فرع ومحاسب من فرع آخر (`BranchIsolationTests`).

## النسخ الاحتياطي (مجاني)

pgBackRest داخل حاوية قاعدة البيانات (`deploy/backup`): كاملة كل جمعة وتزايدية يومياً الساعة 3 فجراً بتوقيت بغداد، WAL كل 5 دقائق، واختبار استرجاع أسبوعي. لوحة المدير: `/api/v1/admin/backups`. دليل الاسترجاع: [docs/backup_restore.md](docs/backup_restore.md).

## نشر Railway

راجع [دليل API وقاعدة البيانات والتخزين](docs/deployment-railway.md) وقالب المتغيرات في deploy/railway.env.example. لا تُشغّل schema_v5.sql على قاعدة قائمة.
