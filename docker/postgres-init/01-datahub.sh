#!/bin/sh
# Runs once, on an empty data volume (docker-entrypoint-initdb.d). Creates the three databases and
# the roles that use them:
#
#   POSTGRES_USER     owner of app and auth; DbUtils migrates with it (DDL)
#   APP_DB_USER       runtime role of the api (no DDL): SELECT/INSERT/UPDATE/DELETE only
#   KEYCLOAK_DB_USER  owns the keycloak database and nothing else
#
# Default privileges make every table, view and sequence that the owner creates later (by migrations)
# usable by the runtime role without re-granting.
set -eu

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname postgres <<SQL
CREATE ROLE "${APP_DB_USER}" LOGIN PASSWORD '${APP_DB_PASSWORD}';
CREATE ROLE "${KEYCLOAK_DB_USER}" LOGIN PASSWORD '${KEYCLOAK_DB_PASSWORD}';
CREATE DATABASE app OWNER "${POSTGRES_USER}";
CREATE DATABASE auth OWNER "${POSTGRES_USER}";
CREATE DATABASE keycloak OWNER "${KEYCLOAK_DB_USER}";
REVOKE ALL ON DATABASE app, auth, keycloak FROM PUBLIC;
GRANT CONNECT ON DATABASE app, auth TO "${APP_DB_USER}";
SQL

for db in app auth; do
  psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$db" <<SQL
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
GRANT USAGE ON SCHEMA public TO "${APP_DB_USER}";
ALTER DEFAULT PRIVILEGES FOR ROLE "${POSTGRES_USER}" GRANT USAGE ON SCHEMAS TO "${APP_DB_USER}";
ALTER DEFAULT PRIVILEGES FOR ROLE "${POSTGRES_USER}" GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO "${APP_DB_USER}";
ALTER DEFAULT PRIVILEGES FOR ROLE "${POSTGRES_USER}" GRANT USAGE, SELECT ON SEQUENCES TO "${APP_DB_USER}";
SQL
done

echo "datahub databases and roles ready"
