START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM iplocation."__EFMigrationsHistory" WHERE "MigrationId" = '20261103052149_AddAccessRequestMetadata') THEN
    ALTER TABLE iplocation."ACCESS_LOG" ADD "REQUEST_HEADERS_JSON" jsonb;
    COMMENT ON COLUMN iplocation."ACCESS_LOG"."REQUEST_HEADERS_JSON" IS '经允许名单清洗及限额的请求头快照；历史未采集为空';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM iplocation."__EFMigrationsHistory" WHERE "MigrationId" = '20261103052149_AddAccessRequestMetadata') THEN
    ALTER TABLE iplocation."ACCESS_LOG" ADD "SOURCE_LOCATION_JSON" jsonb;
    COMMENT ON COLUMN iplocation."ACCESS_LOG"."SOURCE_LOCATION_JSON" IS '后台解析日志来源IP的位置快照；历史未采集为空';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM iplocation."__EFMigrationsHistory" WHERE "MigrationId" = '20261103052149_AddAccessRequestMetadata') THEN
    INSERT INTO iplocation."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261103052149_AddAccessRequestMetadata', '10.0.9');
    END IF;
END $EF$;
COMMIT;

