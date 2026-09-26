#!/bin/sh
# Gives postgres_exporter a role to connect as, and installs pg_stat_statements.
#
# Idempotent by design and re-run by the AppHost (pg-monitor-init) on every start, so an existing
# data volume gets it too.
#
# The role is pg_monitor and nothing else — it can read the statistics views and cannot read a
# single row of application data.
set -eu

until pg_isready -h postgres -U "$POSTGRES_USER" -d "$POSTGRES_DB" -q; do
  sleep 1
done

psql -v ON_ERROR_STOP=1 -h postgres -U "$POSTGRES_USER" -d "$POSTGRES_DB" <<SQL
DO \$\$
BEGIN
  IF EXISTS (SELECT FROM pg_roles WHERE rolname = '${PG_EXPORTER_USER}') THEN
    ALTER ROLE "${PG_EXPORTER_USER}" WITH LOGIN PASSWORD '${PG_EXPORTER_PASSWORD}';
  ELSE
    CREATE ROLE "${PG_EXPORTER_USER}" LOGIN PASSWORD '${PG_EXPORTER_PASSWORD}';
  END IF;
END
\$\$;

GRANT pg_monitor TO "${PG_EXPORTER_USER}";
GRANT CONNECT ON DATABASE "${POSTGRES_DB}" TO "${PG_EXPORTER_USER}";

-- Normalised query statistics. This is the slow-query source: it stores query *shapes* with the
-- literals stripped out, so no user data ends up in the metrics.
CREATE EXTENSION IF NOT EXISTS pg_stat_statements;
SQL

echo "postgres monitoring role ready: ${PG_EXPORTER_USER}"
