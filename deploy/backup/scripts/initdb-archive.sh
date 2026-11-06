#!/usr/bin/env bash
# أول تشغيل (قاعدة جديدة): تفعيل الأرشفة. يُنفذ من /docker-entrypoint-initdb.d
echo "include_if_exists = '/etc/alrahma/postgresql.backup.conf'" >>"$PGDATA/postgresql.conf"
