-- Removes everything the k6 webhook load test wrote.
-- All k6 readings carry timestamps inside the sentinel window (see loadtest/main.js,
-- SENTINEL_START_MS/SENTINEL_RANGE_MS) and every webhook id starts with 'k6-'.

BEGIN;

DELETE FROM "Temperatures"
WHERE "TimestampUtc" >= '2000-01-01T00:00:00Z'
  AND "TimestampUtc" <  '2000-02-21T00:00:00Z';

DELETE FROM "ProcessedMessages" WHERE "Id" LIKE 'k6-%';

-- Containers the consumer auto-created on an unseeded DB (same SEED-CONT code space
-- as populate-test-data.sql, but not written by it). Safe: only rows that no longer
-- have any readings.
DELETE FROM "Containers" c
WHERE c."Code" LIKE 'SEED-CONT-%'
  AND c."CreatedBy" IS DISTINCT FROM 'bulk-seed'
  AND NOT EXISTS (SELECT 1 FROM "Temperatures" t WHERE t."ContainerId" = c."Id");

COMMIT;
