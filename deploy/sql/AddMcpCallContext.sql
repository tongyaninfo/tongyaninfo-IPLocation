START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM iplocation."__EFMigrationsHistory" WHERE "MigrationId" = '20261103140118_AddMcpCallContext') THEN
    ALTER TABLE iplocation."ACCESS_LOG" ADD "MCP_CONTEXT_JSON" jsonb;
    COMMENT ON COLUMN iplocation."ACCESS_LOG"."MCP_CONTEXT_JSON" IS '经白名单清洗及限额的MCP调用上下文；非MCP和历史记录为空';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM iplocation."__EFMigrationsHistory" WHERE "MigrationId" = '20261103140118_AddMcpCallContext') THEN
    INSERT INTO iplocation."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261103140118_AddMcpCallContext', '10.0.9');
    END IF;
END $EF$;
COMMIT;

