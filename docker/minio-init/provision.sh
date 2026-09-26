#!/bin/sh
# Idempotent, runs on every `up`: creates the buckets and one scoped account per service, so neither
# the api nor the webhook ever holds the MinIO root credentials.
#
#   api      documents + branding (all buckets are private; the api serves branding assets)
#   webhook  webhooks (raw payloads)
set -eu

until mc alias set local http://minio:9000 "$MINIO_ROOT_USER" "$MINIO_ROOT_PASSWORD" >/dev/null 2>&1; do
  sleep 1
done

for bucket in documents branding webhooks; do
  mc mb --ignore-existing "local/$bucket"
done
mc anonymous set none local/branding

policy() { # name bucket...
  name=$1; shift
  resources=""
  for b in "$@"; do
    resources="$resources\"arn:aws:s3:::$b\",\"arn:aws:s3:::$b/*\","
  done
  cat > "/tmp/$name.json" <<JSON
{"Version":"2012-10-17","Statement":[{"Effect":"Allow","Action":["s3:*"],"Resource":[${resources%,}]}]}
JSON
  mc admin policy create local "$name" "/tmp/$name.json" >/dev/null
}

account() { # user secret policy
  mc admin user add local "$1" "$2" >/dev/null
  mc admin policy attach local "$3" --user "$1" >/dev/null 2>&1 || true
}

policy datahub-api documents branding
policy datahub-webhook webhooks
account "$API_S3_ACCESS_KEY" "$API_S3_SECRET_KEY" datahub-api
account "$WEBHOOK_S3_ACCESS_KEY" "$WEBHOOK_S3_SECRET_KEY" datahub-webhook

echo "minio buckets and service accounts ready"
