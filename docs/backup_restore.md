# النسخ الاحتياطي والاسترجاع (مجاني، pgBackRest)

> **على Windows بدون Docker** (جهاز المجمع، `start-native.bat`) يعمل النسخ الأصلي `Backup__Mode=native` داخل الـ API نفسه.
> انظر القسم [Windows بدون Docker](#windows-بدون-docker-backup__modenative). باقي هذا الملف لتشغيل Docker و pgBackRest على Linux.

## Windows بدون Docker (Backup__Mode=native)

| البند | القيمة |
|---|---|
| المنفذ | الـ API نفسه (`BackupWorker` كل دقيقة) بـ `pg_dump -Fc` ثم التحقق بـ `pg_restore --list` |
| محتوى كل نسخة | مجلد واحد في `Backup__Path` (افتراضياً `D:\AlRahmaBackups`): `db.dump` + `files\` (نسخة من بيانات تخزين الملفات SeaweedFS) + `keys\drawer-es256.pem` (مفتاح توقيع الإيصالات) + `manifest.json` |
| التوقيت | يومياً في "وقت النسخ اليومي" من شاشة النسخ الاحتياطي (افتراضياً 03:00 بغداد). يُطبق فوراً. إن كان الجهاز مطفأً وقتها تؤخذ عند أول تشغيل بعده |
| القرص الخارجي | اختياري `Backup__OffsitePath`. غير المتصل = "بانتظار القرص الخارجي" بلا فشل، وتُنسخ إليه تلقائياً عند توصيله |
| الاحتفاظ | آخر 14 نسخة ناجحة (`Backup__Keep`)، والأقدم يُحذف مجلده تلقائياً ويبقى سجله |
| المهلة | الطلب أو التنفيذ العالق أكثر من 10 دقائق يصبح "فشلت"، ويصل تنبيه للمدير العام عند الفشل أو 26 ساعة بلا نسخة ناجحة |
| كلمة مرور القاعدة | من `local-env.bat` (لا تُكتب في الكود ولا في مجلد النسخة)؛ تُمرر لـ pg_dump بمتغير البيئة فقط |

`start-native.bat` يضبط: `Backup__Enabled=true`، `Backup__Mode=native`، `Backup__PgBin` (نفس مجلد psql)، `Backup__FilesPath` (مجلد `local-data\s3\data`). لتغيير مجلد النسخ أو إضافة قرص خارجي ضع في `local-env.bat`:

```bat
set "BACKUP_PATH=D:\AlRahmaBackups"
set "BACKUP_OFFSITE_PATH=E:\AlRahmaBackups"
```

> **مهم:** مجلد النسخة يحتوي مفتاح توقيع الإيصالات وبيانات المرضى. لا تشاركه، واحفظ القرص الخارجي في مكان آمن.

### الاسترجاع على Windows

1. أغلق نوافذ "Al-Rahma API" و "Frontend" و "File Storage" حتى لا تُكتب بيانات جديدة.
2. اختر مجلد النسخة، مثل `D:\AlRahmaBackups\alrahma_staging_20261106-180500_manual`، وافتح فيه `cmd`. تحقق من سلامتها:
   ```bat
   "C:\Program Files\PostgreSQL\16\bin\pg_restore.exe" --list db.dump > nul && echo OK
   ```
3. قاعدة البيانات (يحذف البيانات الحالية للقاعدة؛ للتجربة استرجع إلى اسم آخر مثل `alrahma_restore_test`):
   ```bat
   set PGBIN=C:\Program Files\PostgreSQL\16\bin
   "%PGBIN%\dropdb.exe" -U postgres alrahma_staging
   "%PGBIN%\createdb.exe" -U postgres alrahma_staging
   "%PGBIN%\pg_restore.exe" -U postgres -d alrahma_staging --no-owner --no-tablespaces db.dump
   ```
4. الملفات المرفوعة: أعد تسمية `local-data\s3\data` إلى `data.old`، ثم انسخ مجلد `files` من النسخة مكانه:
   `robocopy files D:\Progect\alrahma-staging\local-data\s3\data /E`
5. مفتاح توقيع الإيصالات: انسخ `keys\drawer-es256.pem` إلى `local-data\keys\` (فوق الموجود).
6. شغّل `start-native.bat`، وتأكد من الدخول وفتح ملف مريض فيه مرفقات، ثم احذف `data.old`.

### تجربة فعلية على Windows (2026-11-06، قاعدة `alrahma_staging`)

1. تغيير "وقت النسخ اليومي" من الشاشة إلى 02:30 ثم إعادة تحميل الصفحة: بقي 02:30، وموعد النسخة التالية تغيّر.
2. "نسخ الآن": نجحت خلال ثوانٍ. المجلد فيه `db.dump` (0.4MB، 74 جدول بيانات حسب `pg_restore --list`) و `files\` (كل ملفات التخزين، 59KB؛ تُخطّت ملفات `LOCK` الفارغة لقاعدة الـ filer لأنها مقفلة) و `keys\drawer-es256.pem`.
3. استرجاع `db.dump` في قاعدة جديدة `alrahma_restore_test`: 74 جدولاً بنفس عدد الصفوف، عدا `audit_logs` بفرق سطر واحد (سطر "نجاح النسخة" يُكتب بعد الـ dump). ثم حُذفت القاعدة التجريبية.
4. التدقيق: الطلب اليدوي وتغيير الوقت باسم المدير، ونجاح النسخة باسم "النظام".

## الملخص (Docker و pgBackRest)

| البند | القيمة |
|---|---|
| الأداة | pgBackRest داخل حاوية قاعدة البيانات (`deploy/backup`) |
| الكلفة | مجاني بالكامل. الموقع الثاني السحابي (S3 باشتراك) اختياري ومطفأ، يُفعّل بطلب مدير المجمع |
| التوقيت (بغداد) | كاملة كل جمعة 03:00، تزايدية باقي الأيام 03:00، WAL كل 5 دقائق كحد أقصى |
| اختبار الاسترجاع | تلقائي كل جمعة 05:00 (`verify.sh`) على مجلد مؤقت ومنفذ داخلي |
| الاحتفاظ | 4 نسخ أسبوعية + 12 شهرية، و WAL لآخر نسختين كاملتين (14 يوماً للاسترجاع لأي دقيقة) |
| الضغط والتشفير | zstd، و aes-256-cbc بكلمة سر من `PGBACKREST_REPO1_CIPHER_PASS` |
| أقصى فقد بيانات | 5 دقائق عند تعطل السيرفر بالكامل (archive_timeout = 300) |
| الصلاحية | المدير العام فقط. لا تنزيل من الواجهة، والاسترجاع من السيرفر فقط |

> **مهم:** احفظ كلمة سر التشفير خارج السيرفر (ورقة في خزنة المدير مثلاً). بدونها لا يمكن استرجاع أي نسخة.

## الملفات

- `deploy/backup/Dockerfile`: postgres:16 + pgbackrest + jq + mc.
- `deploy/backup/pgbackrest.conf`: repo1 على القرص المحلي (volume `pgbackrest`).
- `deploy/backup/repo2.conf.example`: الموقع الثاني (قرص ثانٍ، سيرفر فرع آخر عبر SFTP، أو S3 متوافق).
- `deploy/backup/postgresql.backup.conf`: `archive_mode=on`، `archive_command` إلى pgbackrest، `archive_timeout=300`.
- `deploy/backup/scripts/`:
  - `scheduler.sh` يعمل كل دقيقة: الجدول، والطلبات اليدوية من لوحة المدير (`backup_runs.status = requested`).
  - `backup.sh` ينفذ النسخة ويسجلها في `backup_runs`.
  - `retention.sh` سياسة 4 أسبوعية + 12 شهرية.
  - `verify.sh` اختبار الاسترجاع الأسبوعي.
  - `files-sync.sh` نسخ ملفات MinIO للموقع الثاني وتعبئة `files.s3_synced_at` (مطفأ افتراضياً).

الـ API لا ينفذ أي أمر على السيرفر. زر "نسخة الآن" يضيف سطراً بحالة `requested`، والمجدول ينفذه خلال دقيقة.

## التشغيل

1. شغّل الترحيل 006 (ينشئ `backup_runs`).
2. ضع في البيئة: `PGBACKREST_REPO1_CIPHER_PASS` (قوية)، ثم `docker compose up -d --build db`.
3. في الـ API: `Backup__Enabled=true` لتفعيل تنبيهات المدير.
4. تحقق: `docker compose exec -u postgres db pgbackrest --stanza=alrahma info`.

قاعدة موجودة مسبقاً: الحاوية تضيف سطر `include_if_exists` إلى `postgresql.conf` عند التشغيل، وتحتاج إعادة تشغيل واحدة لتفعيل الأرشفة.

## الموقع الثاني (اختياري)

1. انسخ `repo2.conf.example` إلى `/etc/pgbackrest/conf.d/repo2.conf` داخل الحاوية (أو اربطه كـ volume) واختر طريقة واحدة.
2. ضع `PGBACKREST_REPO2_CIPHER_PASS` و `BACKUP_REPO2=1`، وفي الـ API `Backup__Repo2Enabled=true`.
3. نفّذ مرة واحدة: `pgbackrest --stanza=alrahma stanza-create`.

نسخ الملفات (أشعة، نتائج، صور): `BACKUP_FILES_MODE=mirror` و `BACKUP_FILES_TARGET` (مسار قرص أو `s3://bucket/prefix`)، وفي الـ API `Backup__Files__Mode=mirror`.

## المراقبة

مهمة كل دقيقة في الـ API ترسل إشعار `backup.failed` لكل مدير عام عند:
- فشل أي تشغيل (مرة واحدة لكل تشغيل).
- مرور 26 ساعة بلا نسخة ناجحة (يتكرر كل 12 ساعة كحد أقصى).
- تأخر نسخ الملفات أكثر من 24 ساعة (إذا كان مفعلاً).

## الاسترجاع

كل الأوامر داخل الحاوية كمستخدم postgres: `docker compose exec -u postgres db bash`.
**قبل أي استرجاع:** أوقف الـ API (`docker compose stop api`) حتى لا تُكتب بيانات جديدة.

### 1) عرض النسخ المتاحة

```bash
pgbackrest --stanza=alrahma info
```

### 2) استرجاع آخر حالة (تعطل القرص أو تلف القاعدة)

```bash
docker compose stop db
docker compose run --rm -u postgres --entrypoint bash db -c \
  "pgbackrest --stanza=alrahma --delta restore"
docker compose up -d db
```

### 3) الاسترجاع لنقطة زمنية (حذف بالخطأ)

مثال: حُذفت بيانات الساعة 14:32 بتوقيت بغداد. استرجع إلى دقيقة قبلها:

```bash
docker compose stop api db
docker compose run --rm -u postgres --entrypoint bash db -c \
  "pgbackrest --stanza=alrahma --delta --type=time --target='2026-11-02 14:31:00+03' --target-action=promote restore"
docker compose up -d db
# تحقق من البيانات ثم شغّل الـ API
docker compose up -d api
```

بعد الاسترجاع لنقطة زمنية تبدأ القاعدة خطاً زمنياً جديداً. خذ نسخة كاملة فوراً:
`/opt/alrahma-backup/backup.sh full manual`.

### 4) الاسترجاع من الموقع الثاني

أضف `--repo=2` إلى أمر الاسترجاع.

### 5) استرجاع نسخة قديمة محددة (شهرية مثلاً)

```bash
pgbackrest --stanza=alrahma --delta --set=20261002-030000F --type=immediate --target-action=promote restore
```

## تجربة فعلية (2026-11-02)

نُفذت على PostgreSQL 16.15 و pgBackRest 2.50 بنفس السكربتات:
1. نسخة كاملة ثم تزايدية عبر `backup.sh` (سُجلت في `backup_runs` مع الحجم: 33.8MB قبل الضغط، 4MB بعده).
2. إضافة 50 سجلاً قبل التزايدية و 25 بعدها، ثم حذفها كلها.
3. استرجاع لنقطة زمنية قبل الحذف بثانيتين: عادت السجلات الـ 75 كاملة، ومنها الـ 25 التي لم تكن في أي نسخة (من WAL).
4. `verify.sh`: استرجاع آخر نسخة في مجلد مؤقت وتشغيلها والتحقق منها ثم حذفها. نجح.
5. `retention.sh`: بعد 5 نسخ كاملة حُذفت الأقدم مع التزايدية التابعة لها، وبقيت 4.
