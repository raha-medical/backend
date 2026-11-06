# PostgreSQL 16 مع pgBackRest للنسخ الاحتياطي المجاني (deploy/backup/README في docs/backup_restore.md)
FROM postgres:16

RUN apt-get update \
 && apt-get install -y --no-install-recommends pgbackrest jq ca-certificates curl \
 && rm -rf /var/lib/apt/lists/* \
 && curl -fsSL -o /usr/local/bin/mc https://dl.min.io/client/mc/release/linux-amd64/archive/mc.RELEASE.2025-04-16T18-13-26Z \
 && chmod +x /usr/local/bin/mc

COPY pgbackrest.conf /etc/pgbackrest/pgbackrest.conf
COPY postgresql.backup.conf /etc/alrahma/postgresql.backup.conf
COPY scripts/ /opt/alrahma-backup/
COPY scripts/initdb-archive.sh /docker-entrypoint-initdb.d/00_archive.sh
RUN mkdir -p /etc/pgbackrest/conf.d /var/lib/pgbackrest /var/log/pgbackrest \
 && chown -R postgres:postgres /etc/pgbackrest /var/lib/pgbackrest /var/log/pgbackrest \
 && chmod +x /opt/alrahma-backup/*.sh

ENV TZ=Asia/Baghdad
ENTRYPOINT ["/opt/alrahma-backup/entrypoint.sh"]
CMD ["postgres"]
