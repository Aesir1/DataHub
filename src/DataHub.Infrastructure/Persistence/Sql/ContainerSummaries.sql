-- Per-container statistics over all readings (Celsius). Recreated idempotently by migrations.
CREATE OR REPLACE VIEW "ContainerSummaries" AS
SELECT
    c."Id"                    AS "ContainerId",
    count(t."Id")::int        AS "Readings",
    min(t."Celsius")          AS "MinCelsius",
    max(t."Celsius")          AS "MaxCelsius",
    round(avg(t."Celsius"), 3) AS "AvgCelsius",
    max(t."TimestampUtc")     AS "LastTimestampUtc",
    (SELECT l."Celsius" FROM "Temperatures" l
      WHERE l."ContainerId" = c."Id"
      ORDER BY l."TimestampUtc" DESC, l."Id" DESC
      LIMIT 1)                AS "LastCelsius"
FROM "Containers" c
LEFT JOIN "Temperatures" t ON t."ContainerId" = c."Id"
GROUP BY c."Id";
