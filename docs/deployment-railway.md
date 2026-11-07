# Stable deployment: Railway API + PostgreSQL, Vercel frontend

## Services and release

The frontend and backend are separate GitHub repositories. Deploy the same reviewed release branch in both; do not connect a production service to an unfinished feature branch. Run CI before promoting it. Keep the previous deployment available for rollback. Run one API replica: scheduled jobs, access caches and SignalR currently assume a single process.

## 1. PostgreSQL

Create a Railway PostgreSQL service named `Postgres` with persistent storage. Use its private hostname in the API. The API accepts an Npgsql connection string, **not** the `postgresql://` URL directly; `deploy/railway.env.example` composes it from Railway references. Do not expose database credentials to Vercel or the browser.

For a **new, empty database only**, initialize `database/schema_v5.sql` with psql (stop on errors):

```sh
psql "$DATABASE_URL" -v ON_ERROR_STOP=1 -f database/schema_v5.sql
```

**This file drops the public schema. Never run it on an existing database.** There is intentionally no automatic schema initialization in the container or deploy hook. For an existing database, back up and verify restoration first, identify its applied migrations, then apply only missing files in `database/migrations` in numeric order through `031_prescription_text.sql`. Do not replay all migrations blindly. The consolidated schema already includes the current migrations. An older application rollback must not drop newly stored prescription text.

For migration from the local installation, use a PostgreSQL dump/restore to a new database after reviewing which data should move. Local smoke-test accounts and patient records should not be copied into a live clinic database automatically.

## 2. API

Connect `raha-medical/backend`, root directory `/`, select the reviewed release branch. Railway detects `Dockerfile` and `railway.json`. The container uses .NET 8, runs as an unprivileged user, honors Railway `PORT`, defaults to 8080 locally, and reports database connectivity at `/health`.

Copy `deploy/railway.env.example` into Railway's Variables RAW Editor, replace every placeholder and adjust service references if the database name differs. Secrets belong only in Railway Variables. Generate a random JWT key of at least 32 characters (e.g. `openssl rand -base64 48`); keep it stable across deployments. Create a unique bootstrap admin password. After initial login/change of password, remove the bootstrap password from service variables. Bootstrap only creates the first administrator; it does not create demo doctors or patients.

Attach an API volume at `/data`, set its ownership/mount permissions to permit the container `app` user to write `/data/keys`, and use `Drawer__KeyFile=/data/keys/drawer-es256.pem`. Alternatively, supply a persistent ES256 PKCS#8 PEM through the secret `Drawer__PrivateKeyPem`; it takes priority and requires no key-file volume. Never generate a new key each deployment. The cash drawer remains disabled as in the current application.

Set `Cors__Origins__0` to the **exact HTTPS frontend origin**, without a path or trailing slash. If a custom domain is used, explicitly list it as another `Cors__Origins__N`. Do not allow every Vercel preview domain. Set public payment, receipt verification and patient app URLs to the same canonical frontend deployment.

## 3. Files are a separate service

Uploaded lab/radiology results and doctor photos use the existing S3-compatible storage implementation; PostgreSQL alone is insufficient. Configure `Storage__*` against an S3-compatible service or a persistent MinIO deployment. The public endpoint must be HTTPS and reachable by patient phones. Create the private `medical` bucket; do not make medical files public. Allow the exact frontend origin in bucket CORS for signed PUT/GET uploads (Content-Type header; expose ETag if needed). Do not use a public CDN URL that changes S3 signed paths/hosts. Storage access keys stay on the backend.

## 4. Vercel

Connect `raha-medical/frontend`, root directory `/`, Next.js framework. `vercel.json` runs `npm ci`, typecheck and build. Copy the variables from `frontend/deploy/vercel.env.example` into Vercel. Use Railway's public HTTPS origin for `API_PROXY_TARGET` (no `/api/v1` suffix). REST calls remain same-origin through Next rewrites; `NEXT_PUBLIC_HUB_URL` connects SignalR directly to Railway. `NEXT_PUBLIC_PATIENT_APP_URL` must point to the public HTTPS `/patient` URL, not localhost or a LAN IP. Public variables are compiled at build time; redeploy after changing them. No frontend Docker image is required on Vercel.

## 5. Current integration limits

- SMS/OTP defaults to sandbox: codes are random and hidden from API responses in Production, but SMS is not delivered to phones. The administration sandbox message viewer is for supervised testing. Real patient rollout requires the approved SMS provider configured and tested.
- Online sandbox payment success is forcibly disabled by Production code. Use reception payment until a real gateway is configured and verified; do not set Development to bypass this.
- Push notifications and assistant are sandbox providers unless configured. Do not describe them as live integrations.
- `Backup__Enabled=false` avoids executing Windows/native backup tools in the API container. Configure Railway database backups and a separate S3 backup/restore procedure, and test restoration before accepting real data. A persistent volume is not a backup.

## 6. Release verification

Run backend Release build and the full database-backed test suite with `TEST_DATABASE_URL` targeting a **test PostgreSQL server**. Tests create and drop uniquely named test databases, not the live clinic database. Frontend: `node scripts/test-session-store.mjs`, `npm run typecheck`, `npm run build`.

After deployment: confirm `/health` returns 200, log in as administrator/change initial password, check staff role restrictions, independent tabs, lab direct/referral workflows, pharmacy quantity/pricing/billing, doctor textarea prescription and printing. Verify SignalR connects directly over HTTPS/WSS and queue updates live. On a real phone open the waiting-screen QR, log in, confirm attendance once and check repeated scanning creates no duplicate attendance. Upload/download a fake lab result and verify it survives redeploy. Check logs for errors without logging patient data or tokens. Cloud and physical-phone checks must be recorded separately from local tests.

## Official references

- [Railway Dockerfiles](https://docs.railway.com/builds/dockerfiles)
- [Railway healthchecks](https://docs.railway.com/deployments/healthchecks)
- [Railway reference variables](https://docs.railway.com/variables/reference)
- [Railway PostgreSQL](https://docs.railway.com/databases/postgresql)
- [Vercel environment variables](https://vercel.com/docs/environment-variables)
- [Next.js environment variables](https://nextjs.org/docs/app/guides/environment-variables)
