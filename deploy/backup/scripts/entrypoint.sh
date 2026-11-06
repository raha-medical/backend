#!/usr/bin/env bash
# يضيف إعدادات الأرشفة إلى postgresql.conf ويشغل مجدول النسخ بجانب PostgreSQL.
set -euo pipefail
INCLUDE="include_if_exists = '/etc/alrahma/postgresql.backup.conf'"
if [[ -s "${PGDATA:-/var/lib/postgresql/data}/PG_VERSION" ]] && ! grep -qF "$INCLUDE" "$PGDATA/postgresql.conf"; then
  echo "$INCLUDE" >>"$PGDATA/postgresql.conf"
fi
mkdir -p /var/lib/pgbackrest /var/log/pgbackrest && chown -R postgres:postgres /var/lib/pgbackrest /var/log/pgbackrest
gosu postgres /opt/alrahma-backup/scheduler.sh >>/var/log/pgbackrest/scheduler.log 2>&1 &
exec docker-entrypoint.sh "$@"
