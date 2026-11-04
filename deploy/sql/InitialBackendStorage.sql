DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM pg_namespace WHERE nspname = 'iplocation') THEN
        CREATE SCHEMA iplocation;
    END IF;
END $EF$;
CREATE TABLE IF NOT EXISTS iplocation."__EFMigrationsHistory" (
    "MigrationId" character varying(150) NOT NULL,
    "ProductVersion" character varying(32) NOT NULL,
    CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
);

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM iplocation."__EFMigrationsHistory" WHERE "MigrationId" = '20261102153235_InitialBackendStorage') THEN
        IF NOT EXISTS(SELECT 1 FROM pg_namespace WHERE nspname = 'iplocation') THEN
            CREATE SCHEMA iplocation;
        END IF;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM iplocation."__EFMigrationsHistory" WHERE "MigrationId" = '20261102153235_InitialBackendStorage') THEN
    CREATE TABLE iplocation."ACCESS_LOG" (
        "ID" bigint NOT NULL,
        "EVENT_KEY" character varying(64) NOT NULL,
        "OCCURRED_AT_UTC" timestamp with time zone NOT NULL,
        "TRACE_ID" character varying(128) NOT NULL,
        "ROUTE_CATEGORY" character varying(32) NOT NULL,
        "METHOD" character varying(16) NOT NULL,
        "PROTOCOL" character varying(16) NOT NULL,
        "FORMAT" character varying(16) NOT NULL,
        "DATABASE" character varying(16),
        "DATABASE_VERSION" character varying(128),
        "STATUS_CODE" integer NOT NULL,
        "ELAPSED_MILLISECONDS" double precision NOT NULL,
        "IS_CANCELLED" boolean NOT NULL,
        "SOURCE_ADDRESS" inet,
        "CLIENT_CATEGORY" character varying(16) NOT NULL,
        CONSTRAINT "PK_ACCESS_LOG" PRIMARY KEY ("ID"),
        CONSTRAINT "CK_ACCESS_LOG_ELAPSED" CHECK ("ELAPSED_MILLISECONDS" >= 0 AND "ELAPSED_MILLISECONDS" < 'Infinity'::double precision),
        CONSTRAINT "CK_ACCESS_LOG_EVENT_KEY" CHECK ("EVENT_KEY" ~ '^[0-9a-fA-F]{64}$'),
        CONSTRAINT "CK_ACCESS_LOG_ID_POSITIVE" CHECK ("ID" > 0),
        CONSTRAINT "CK_ACCESS_LOG_STATUS_CODE" CHECK ("STATUS_CODE" BETWEEN 100 AND 599)
    );
    COMMENT ON TABLE iplocation."ACCESS_LOG" IS '公共访问明细；完整来源IP仅用于获准后台读取，不保存查询目标或完整URL';
    COMMENT ON COLUMN iplocation."ACCESS_LOG"."ID" IS '访问记录ID，由后台消费者使用统一YiteIdService生成';
    COMMENT ON COLUMN iplocation."ACCESS_LOG"."EVENT_KEY" IS '稳定事件摘要，批次重试通过唯一约束去重';
    COMMENT ON COLUMN iplocation."ACCESS_LOG"."OCCURRED_AT_UTC" IS '请求开始时间，UTC';
    COMMENT ON COLUMN iplocation."ACCESS_LOG"."TRACE_ID" IS '请求链路标识，不作为主键';
    COMMENT ON COLUMN iplocation."ACCESS_LOG"."ROUTE_CATEGORY" IS '服务端固定路由类别，不保存原始路径';
    COMMENT ON COLUMN iplocation."ACCESS_LOG"."METHOD" IS 'HTTP方法';
    COMMENT ON COLUMN iplocation."ACCESS_LOG"."PROTOCOL" IS 'HTTP协议版本';
    COMMENT ON COLUMN iplocation."ACCESS_LOG"."FORMAT" IS '实际响应格式';
    COMMENT ON COLUMN iplocation."ACCESS_LOG"."DATABASE" IS '固定来源代码或all；纯IP请求可为空';
    COMMENT ON COLUMN iplocation."ACCESS_LOG"."DATABASE_VERSION" IS '实际查询的数据版本摘要';
    COMMENT ON COLUMN iplocation."ACCESS_LOG"."STATUS_CODE" IS 'HTTP响应状态码';
    COMMENT ON COLUMN iplocation."ACCESS_LOG"."ELAPSED_MILLISECONDS" IS '请求耗时，毫秒';
    COMMENT ON COLUMN iplocation."ACCESS_LOG"."IS_CANCELLED" IS '客户端是否取消或断开';
    COMMENT ON COLUMN iplocation."ACCESS_LOG"."SOURCE_ADDRESS" IS '获准保存的完整IP；关闭完整留存时可保存脱敏网段';
    COMMENT ON COLUMN iplocation."ACCESS_LOG"."CLIENT_CATEGORY" IS '客户端粗分类，不保存完整User-Agent';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM iplocation."__EFMigrationsHistory" WHERE "MigrationId" = '20261102153235_InitialBackendStorage') THEN
    CREATE TABLE iplocation."ADMIN_ACCOUNT" (
        "ID" bigint NOT NULL,
        "ACCOUNT" character varying(128) NOT NULL,
        "NORMALIZED_ACCOUNT" character varying(128) NOT NULL,
        "DISPLAY_NAME" character varying(128) NOT NULL,
        "PASSWORD_HASH" character varying(1024) NOT NULL,
        "ENABLED" boolean NOT NULL,
        "SECURITY_VERSION" bigint NOT NULL,
        "CREATED_AT_UTC" timestamp with time zone NOT NULL,
        "UPDATED_AT_UTC" timestamp with time zone NOT NULL,
        CONSTRAINT "PK_ADMIN_ACCOUNT" PRIMARY KEY ("ID"),
        CONSTRAINT "CK_ADMIN_ACCOUNT_ID_POSITIVE" CHECK ("ID" > 0),
        CONSTRAINT "CK_ADMIN_ACCOUNT_REQUIRED_TEXT" CHECK (btrim("ACCOUNT") <> '' AND btrim("NORMALIZED_ACCOUNT") <> '' AND btrim("PASSWORD_HASH") <> ''),
        CONSTRAINT "CK_ADMIN_ACCOUNT_SECURITY_VERSION" CHECK ("SECURITY_VERSION" > 0)
    );
    COMMENT ON TABLE iplocation."ADMIN_ACCOUNT" IS '管理员账户；禁止默认账户种子和明文密码';
    COMMENT ON COLUMN iplocation."ADMIN_ACCOUNT"."ID" IS '管理员ID，由统一YiteIdService生成';
    COMMENT ON COLUMN iplocation."ADMIN_ACCOUNT"."ACCOUNT" IS '保留显示形式的登录账号';
    COMMENT ON COLUMN iplocation."ADMIN_ACCOUNT"."NORMALIZED_ACCOUNT" IS '服务统一规范化后的唯一登录账号';
    COMMENT ON COLUMN iplocation."ADMIN_ACCOUNT"."DISPLAY_NAME" IS '管理员显示名';
    COMMENT ON COLUMN iplocation."ADMIN_ACCOUNT"."PASSWORD_HASH" IS '带算法信息的安全密码散列，不保存明文';
    COMMENT ON COLUMN iplocation."ADMIN_ACCOUNT"."ENABLED" IS '是否允许登录';
    COMMENT ON COLUMN iplocation."ADMIN_ACCOUNT"."SECURITY_VERSION" IS '身份安全版本；变更时使已有会话失效';
    COMMENT ON COLUMN iplocation."ADMIN_ACCOUNT"."CREATED_AT_UTC" IS '创建时间，UTC';
    COMMENT ON COLUMN iplocation."ADMIN_ACCOUNT"."UPDATED_AT_UTC" IS '最后修改时间，UTC';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM iplocation."__EFMigrationsHistory" WHERE "MigrationId" = '20261102153235_InitialBackendStorage') THEN
    CREATE TABLE iplocation."TRAFFIC_MINUTE_AGGREGATE" (
        "MINUTE_UTC" timestamp with time zone NOT NULL,
        "ROUTE_CATEGORY" character varying(32) NOT NULL,
        "DATABASE" character varying(16) NOT NULL,
        "FORMAT" character varying(16) NOT NULL,
        "COUNT_KIND" character varying(16) NOT NULL,
        "TOTAL_REQUESTS" bigint NOT NULL,
        "HOME_HTML_REQUESTS" bigint NOT NULL,
        "PLAIN_IP_REQUESTS" bigint NOT NULL,
        "QUERY_REQUESTS" bigint NOT NULL,
        "CLIENT_ERRORS" bigint NOT NULL,
        "SERVER_ERRORS" bigint NOT NULL,
        "CANCELLED_REQUESTS" bigint NOT NULL,
        "SUCCESS_COUNT" bigint NOT NULL,
        "NOT_FOUND_COUNT" bigint,
        "TOTAL_ELAPSED_MILLISECONDS" double precision NOT NULL,
        "MIN_ELAPSED_MILLISECONDS" double precision NOT NULL,
        "MAX_ELAPSED_MILLISECONDS" double precision NOT NULL,
        "LATENCY_HISTOGRAM_JSON" jsonb NOT NULL,
        "REVISION" bigint NOT NULL,
        "UPDATED_AT_UTC" timestamp with time zone NOT NULL,
        CONSTRAINT "PK_TRAFFIC_MINUTE_AGGREGATE" PRIMARY KEY ("MINUTE_UTC", "ROUTE_CATEGORY", "DATABASE", "FORMAT", "COUNT_KIND"),
        CONSTRAINT "CK_TRAFFIC_MINUTE_ALIGNMENT" CHECK (date_trunc('minute', "MINUTE_UTC" AT TIME ZONE 'UTC') = "MINUTE_UTC" AT TIME ZONE 'UTC'),
        CONSTRAINT "CK_TRAFFIC_MINUTE_COUNT_KIND" CHECK ("COUNT_KIND" IN ('request', 'source')),
        CONSTRAINT "CK_TRAFFIC_MINUTE_COUNTS" CHECK ("TOTAL_REQUESTS" >= 0 AND "HOME_HTML_REQUESTS" >= 0 AND "PLAIN_IP_REQUESTS" >= 0 AND "QUERY_REQUESTS" >= 0 AND "CLIENT_ERRORS" >= 0 AND "SERVER_ERRORS" >= 0 AND "CANCELLED_REQUESTS" >= 0 AND "SUCCESS_COUNT" >= 0 AND ("NOT_FOUND_COUNT" IS NULL OR "NOT_FOUND_COUNT" >= 0)),
        CONSTRAINT "CK_TRAFFIC_MINUTE_ELAPSED" CHECK ("TOTAL_ELAPSED_MILLISECONDS" >= 0 AND "TOTAL_ELAPSED_MILLISECONDS" < 'Infinity'::double precision AND "MIN_ELAPSED_MILLISECONDS" >= 0 AND "MIN_ELAPSED_MILLISECONDS" < 'Infinity'::double precision AND "MAX_ELAPSED_MILLISECONDS" >= "MIN_ELAPSED_MILLISECONDS" AND "MAX_ELAPSED_MILLISECONDS" < 'Infinity'::double precision),
        CONSTRAINT "CK_TRAFFIC_MINUTE_HISTOGRAM" CHECK (CASE WHEN jsonb_typeof("LATENCY_HISTOGRAM_JSON") = 'array' THEN jsonb_array_length("LATENCY_HISTOGRAM_JSON") = 13 ELSE false END),
        CONSTRAINT "CK_TRAFFIC_MINUTE_REVISION" CHECK ("REVISION" > 0)
    );
    COMMENT ON TABLE iplocation."TRAFFIC_MINUTE_AGGREGATE" IS 'UTC分钟固定维度统计；只聚合已去重事件，直方图合并后计算分位数';
    COMMENT ON COLUMN iplocation."TRAFFIC_MINUTE_AGGREGATE"."MINUTE_UTC" IS 'UTC分钟起点，自然复合键的一部分';
    COMMENT ON COLUMN iplocation."TRAFFIC_MINUTE_AGGREGATE"."ROUTE_CATEGORY" IS '服务端固定路由分类';
    COMMENT ON COLUMN iplocation."TRAFFIC_MINUTE_AGGREGATE"."DATABASE" IS '固定来源代码、all或无来源时的空串';
    COMMENT ON COLUMN iplocation."TRAFFIC_MINUTE_AGGREGATE"."FORMAT" IS '固定响应格式维度';
    COMMENT ON COLUMN iplocation."TRAFFIC_MINUTE_AGGREGATE"."COUNT_KIND" IS 'request为客户端请求，source为实际来源调用';
    COMMENT ON COLUMN iplocation."TRAFFIC_MINUTE_AGGREGATE"."TOTAL_REQUESTS" IS '本维度计数；由CountKind区分请求或来源调用';
    COMMENT ON COLUMN iplocation."TRAFFIC_MINUTE_AGGREGATE"."HOME_HTML_REQUESTS" IS '首页HTML请求计数';
    COMMENT ON COLUMN iplocation."TRAFFIC_MINUTE_AGGREGATE"."PLAIN_IP_REQUESTS" IS '根路径纯IP调用计数';
    COMMENT ON COLUMN iplocation."TRAFFIC_MINUTE_AGGREGATE"."QUERY_REQUESTS" IS '逻辑查询请求计数';
    COMMENT ON COLUMN iplocation."TRAFFIC_MINUTE_AGGREGATE"."CLIENT_ERRORS" IS 'HTTP4xx计数';
    COMMENT ON COLUMN iplocation."TRAFFIC_MINUTE_AGGREGATE"."SERVER_ERRORS" IS 'HTTP5xx计数';
    COMMENT ON COLUMN iplocation."TRAFFIC_MINUTE_AGGREGATE"."CANCELLED_REQUESTS" IS '取消或连接中断计数，可与错误状态重叠';
    COMMENT ON COLUMN iplocation."TRAFFIC_MINUTE_AGGREGATE"."SUCCESS_COUNT" IS 'HTTP2xx成功计数，不代表数据库命中';
    COMMENT ON COLUMN iplocation."TRAFFIC_MINUTE_AGGREGATE"."NOT_FOUND_COUNT" IS '业务无匹配计数；尚未采集该状态时为空';
    COMMENT ON COLUMN iplocation."TRAFFIC_MINUTE_AGGREGATE"."TOTAL_ELAPSED_MILLISECONDS" IS '同一口径耗时总和，毫秒';
    COMMENT ON COLUMN iplocation."TRAFFIC_MINUTE_AGGREGATE"."MIN_ELAPSED_MILLISECONDS" IS '耗时最小值，毫秒';
    COMMENT ON COLUMN iplocation."TRAFFIC_MINUTE_AGGREGATE"."MAX_ELAPSED_MILLISECONDS" IS '耗时最大值，毫秒';
    COMMENT ON COLUMN iplocation."TRAFFIC_MINUTE_AGGREGATE"."LATENCY_HISTOGRAM_JSON" IS '13个固定直方图桶计数；写入端另验证每桶非负和总数';
    COMMENT ON COLUMN iplocation."TRAFFIC_MINUTE_AGGREGATE"."REVISION" IS '统计行并发版本，初始1，合并后递增';
    COMMENT ON COLUMN iplocation."TRAFFIC_MINUTE_AGGREGATE"."UPDATED_AT_UTC" IS '最后成功合并时间，UTC';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM iplocation."__EFMigrationsHistory" WHERE "MigrationId" = '20261102153235_InitialBackendStorage') THEN
    CREATE TABLE iplocation."ADMIN_AUDIT_EVENT" (
        "ID" bigint NOT NULL,
        "ACTOR_ID" bigint,
        "SOURCE_ADDRESS" inet,
        "ACTION" character varying(64) NOT NULL,
        "TARGET_TYPE" character varying(64),
        "TARGET_ID" character varying(128),
        "DIFFERENCE_JSON" jsonb NOT NULL,
        "RESULT" character varying(32) NOT NULL,
        "TRACE_ID" character varying(128) NOT NULL,
        "OCCURRED_AT_UTC" timestamp with time zone NOT NULL,
        CONSTRAINT "PK_ADMIN_AUDIT_EVENT" PRIMARY KEY ("ID"),
        CONSTRAINT "CK_ADMIN_AUDIT_EVENT_ID_POSITIVE" CHECK ("ID" > 0),
        CONSTRAINT "FK_ADMIN_AUDIT_EVENT_ACTOR" FOREIGN KEY ("ACTOR_ID") REFERENCES iplocation."ADMIN_ACCOUNT" ("ID") ON DELETE RESTRICT
    );
    COMMENT ON TABLE iplocation."ADMIN_AUDIT_EVENT" IS '管理操作的可靠审计，不通过可丢弃访问队列写入';
    COMMENT ON COLUMN iplocation."ADMIN_AUDIT_EVENT"."ID" IS '审计事件ID，由统一YiteIdService生成';
    COMMENT ON COLUMN iplocation."ADMIN_AUDIT_EVENT"."ACTOR_ID" IS '操作管理员ID；身份未知的登录失败可为空';
    COMMENT ON COLUMN iplocation."ADMIN_AUDIT_EVENT"."SOURCE_ADDRESS" IS '经可信代理规则识别的操作来源地址';
    COMMENT ON COLUMN iplocation."ADMIN_AUDIT_EVENT"."ACTION" IS '服务固定定义的管理动作';
    COMMENT ON COLUMN iplocation."ADMIN_AUDIT_EVENT"."TARGET_TYPE" IS '被操作对象类型';
    COMMENT ON COLUMN iplocation."ADMIN_AUDIT_EVENT"."TARGET_ID" IS '被操作对象标识，不包含秘密';
    COMMENT ON COLUMN iplocation."ADMIN_AUDIT_EVENT"."DIFFERENCE_JSON" IS '已脱敏的变更差异，不含密码、Token或文件正文';
    COMMENT ON COLUMN iplocation."ADMIN_AUDIT_EVENT"."RESULT" IS '管理操作结果';
    COMMENT ON COLUMN iplocation."ADMIN_AUDIT_EVENT"."TRACE_ID" IS '请求链路关联标识，不充当业务主键';
    COMMENT ON COLUMN iplocation."ADMIN_AUDIT_EVENT"."OCCURRED_AT_UTC" IS '操作发生时间，UTC';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM iplocation."__EFMigrationsHistory" WHERE "MigrationId" = '20261102153235_InitialBackendStorage') THEN
    CREATE TABLE iplocation."ADMIN_IP_ALLOWLIST" (
        "ID" bigint NOT NULL,
        "NETWORK" cidr NOT NULL,
        "ENABLED" boolean NOT NULL,
        "NOTE" character varying(512),
        "CREATED_BY_ID" bigint,
        "UPDATED_BY_ID" bigint,
        "CREATED_AT_UTC" timestamp with time zone NOT NULL,
        "UPDATED_AT_UTC" timestamp with time zone NOT NULL,
        CONSTRAINT "PK_ADMIN_IP_ALLOWLIST" PRIMARY KEY ("ID"),
        CONSTRAINT "CK_ADMIN_IP_ALLOWLIST_ID_POSITIVE" CHECK ("ID" > 0),
        CONSTRAINT "FK_ADMIN_ALLOWLIST_CREATED_BY" FOREIGN KEY ("CREATED_BY_ID") REFERENCES iplocation."ADMIN_ACCOUNT" ("ID") ON DELETE RESTRICT,
        CONSTRAINT "FK_ADMIN_ALLOWLIST_UPDATED_BY" FOREIGN KEY ("UPDATED_BY_ID") REFERENCES iplocation."ADMIN_ACCOUNT" ("ID") ON DELETE RESTRICT
    );
    COMMENT ON TABLE iplocation."ADMIN_IP_ALLOWLIST" IS '管理员入口IP白名单；无启用规则时拒绝访问';
    COMMENT ON COLUMN iplocation."ADMIN_IP_ALLOWLIST"."ID" IS '白名单规则ID，由统一YiteIdService生成';
    COMMENT ON COLUMN iplocation."ADMIN_IP_ALLOWLIST"."NETWORK" IS '允许网段；精确IPv4/IPv6分别为32/128位前缀';
    COMMENT ON COLUMN iplocation."ADMIN_IP_ALLOWLIST"."ENABLED" IS '规则是否启用';
    COMMENT ON COLUMN iplocation."ADMIN_IP_ALLOWLIST"."NOTE" IS '规则用途说明';
    COMMENT ON COLUMN iplocation."ADMIN_IP_ALLOWLIST"."CREATED_BY_ID" IS '创建管理员ID；受控本地初始化可为空';
    COMMENT ON COLUMN iplocation."ADMIN_IP_ALLOWLIST"."UPDATED_BY_ID" IS '最后修改管理员ID';
    COMMENT ON COLUMN iplocation."ADMIN_IP_ALLOWLIST"."CREATED_AT_UTC" IS '创建时间，UTC';
    COMMENT ON COLUMN iplocation."ADMIN_IP_ALLOWLIST"."UPDATED_AT_UTC" IS '最后修改时间，UTC';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM iplocation."__EFMigrationsHistory" WHERE "MigrationId" = '20261102153235_InitialBackendStorage') THEN
    CREATE TABLE iplocation."IP_DATABASE_VERSION" (
        "ID" bigint NOT NULL,
        "PROVIDER_CODE" character varying(16) NOT NULL,
        "VERSION" character varying(128) NOT NULL,
        "FILE_FORMAT" character varying(16) NOT NULL,
        "ADDRESS_FAMILY" character varying(8) NOT NULL,
        "SHA256" character varying(64) NOT NULL,
        "SIZE_BYTES" bigint NOT NULL,
        "STORAGE_PATH" character varying(2048) NOT NULL,
        "OFFICIAL_VERSION" character varying(128),
        "PUBLISHED_ON" date,
        "PUBLISHED_AT_UTC" timestamp with time zone,
        "OFFICIAL_RELEASE_INFO_JSON" jsonb NOT NULL,
        "LICENSE_EVIDENCE_JSON" jsonb NOT NULL,
        "LICENSE_CONFIRMED" boolean NOT NULL,
        "STATUS" character varying(32) NOT NULL,
        "CREATED_BY_ID" bigint NOT NULL,
        "CREATED_AT_UTC" timestamp with time zone NOT NULL,
        "VALIDATED_AT_UTC" timestamp with time zone,
        CONSTRAINT "PK_IP_DATABASE_VERSION" PRIMARY KEY ("ID"),
        CONSTRAINT "AK_IP_DB_VERSION_ID_PROVIDER" UNIQUE ("ID", "PROVIDER_CODE"),
        CONSTRAINT "CK_IP_DB_VERSION_FAMILY" CHECK ("ADDRESS_FAMILY" IN ('ipv4', 'ipv6', 'dual')),
        CONSTRAINT "CK_IP_DB_VERSION_ID_POSITIVE" CHECK ("ID" > 0),
        CONSTRAINT "CK_IP_DB_VERSION_REQUIRED_TEXT" CHECK (btrim("PROVIDER_CODE") <> '' AND btrim("VERSION") <> '' AND btrim("FILE_FORMAT") <> '' AND btrim("STORAGE_PATH") <> '' AND btrim("STATUS") <> ''),
        CONSTRAINT "CK_IP_DB_VERSION_SHA256" CHECK ("SHA256" ~ '^[0-9a-fA-F]{64}$'),
        CONSTRAINT "CK_IP_DB_VERSION_SIZE" CHECK ("SIZE_BYTES" >= 0),
        CONSTRAINT "FK_IP_DB_VERSION_CREATED_BY" FOREIGN KEY ("CREATED_BY_ID") REFERENCES iplocation."ADMIN_ACCOUNT" ("ID") ON DELETE RESTRICT
    );
    COMMENT ON TABLE iplocation."IP_DATABASE_VERSION" IS 'IP数据文件的不可变版本、校验与许可元数据；文件不存入本表';
    COMMENT ON COLUMN iplocation."IP_DATABASE_VERSION"."ID" IS '数据版本ID，由统一YiteIdService生成';
    COMMENT ON COLUMN iplocation."IP_DATABASE_VERSION"."PROVIDER_CODE" IS '服务固定登记的来源代码';
    COMMENT ON COLUMN iplocation."IP_DATABASE_VERSION"."VERSION" IS '本次文件版本标识';
    COMMENT ON COLUMN iplocation."IP_DATABASE_VERSION"."FILE_FORMAT" IS '经校验的文件格式';
    COMMENT ON COLUMN iplocation."IP_DATABASE_VERSION"."ADDRESS_FAMILY" IS '实际覆盖地址族：ipv4、ipv6或dual';
    COMMENT ON COLUMN iplocation."IP_DATABASE_VERSION"."SHA256" IS '原始文件SHA-256十六进制摘要';
    COMMENT ON COLUMN iplocation."IP_DATABASE_VERSION"."SIZE_BYTES" IS '文件字节数';
    COMMENT ON COLUMN iplocation."IP_DATABASE_VERSION"."STORAGE_PATH" IS '仓库外私有存储定位，不对外展示';
    COMMENT ON COLUMN iplocation."IP_DATABASE_VERSION"."OFFICIAL_VERSION" IS '供应商提供的官方版本，未知为空';
    COMMENT ON COLUMN iplocation."IP_DATABASE_VERSION"."PUBLISHED_ON" IS '官方仅提供日期时保留原始日期精度';
    COMMENT ON COLUMN iplocation."IP_DATABASE_VERSION"."PUBLISHED_AT_UTC" IS '官方提供的精确发布时间，UTC，未知为空';
    COMMENT ON COLUMN iplocation."IP_DATABASE_VERSION"."OFFICIAL_RELEASE_INFO_JSON" IS '官方发布信息及原始时间精度说明';
    COMMENT ON COLUMN iplocation."IP_DATABASE_VERSION"."LICENSE_EVIDENCE_JSON" IS '许可依据及适用范围，不含密钥或Token';
    COMMENT ON COLUMN iplocation."IP_DATABASE_VERSION"."LICENSE_CONFIRMED" IS '本项目用途的许可是否已核实';
    COMMENT ON COLUMN iplocation."IP_DATABASE_VERSION"."STATUS" IS '接收、校验、就绪或拒绝等受控版本状态';
    COMMENT ON COLUMN iplocation."IP_DATABASE_VERSION"."CREATED_BY_ID" IS '上传该版本的管理员ID';
    COMMENT ON COLUMN iplocation."IP_DATABASE_VERSION"."CREATED_AT_UTC" IS '版本登记时间，UTC';
    COMMENT ON COLUMN iplocation."IP_DATABASE_VERSION"."VALIDATED_AT_UTC" IS '最后完成校验时间，UTC';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM iplocation."__EFMigrationsHistory" WHERE "MigrationId" = '20261102153235_InitialBackendStorage') THEN
    CREATE TABLE iplocation."SYSTEM_SETTING" (
        "KEY" character varying(128) NOT NULL,
        "VALUE_JSON" jsonb NOT NULL,
        "REVISION" bigint NOT NULL,
        "UPDATED_BY_ID" bigint,
        "UPDATED_AT_UTC" timestamp with time zone NOT NULL,
        CONSTRAINT "PK_SYSTEM_SETTING" PRIMARY KEY ("KEY"),
        CONSTRAINT "CK_SYSTEM_SETTING_KEY" CHECK (btrim("KEY") <> ''),
        CONSTRAINT "CK_SYSTEM_SETTING_REVISION" CHECK ("REVISION" > 0),
        CONSTRAINT "FK_SYSTEM_SETTING_UPDATED_BY" FOREIGN KEY ("UPDATED_BY_ID") REFERENCES iplocation."ADMIN_ACCOUNT" ("ID") ON DELETE RESTRICT
    );
    COMMENT ON TABLE iplocation."SYSTEM_SETTING" IS '非敏感系统设置；禁止保存明文密码或供应商Token';
    COMMENT ON COLUMN iplocation."SYSTEM_SETTING"."KEY" IS '服务端固定登记的唯一设置键';
    COMMENT ON COLUMN iplocation."SYSTEM_SETTING"."VALUE_JSON" IS '经验证的非敏感JSON配置值';
    COMMENT ON COLUMN iplocation."SYSTEM_SETTING"."REVISION" IS '乐观并发版本，初始1，每次成功修改递增';
    COMMENT ON COLUMN iplocation."SYSTEM_SETTING"."UPDATED_BY_ID" IS '修改管理员ID；受控本地初始化可为空';
    COMMENT ON COLUMN iplocation."SYSTEM_SETTING"."UPDATED_AT_UTC" IS '最后修改时间，UTC';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM iplocation."__EFMigrationsHistory" WHERE "MigrationId" = '20261102153235_InitialBackendStorage') THEN
    CREATE TABLE iplocation."DATABASE_UPLOAD_TASK" (
        "ID" bigint NOT NULL,
        "PROVIDER_CODE" character varying(16) NOT NULL,
        "STATE" character varying(32) NOT NULL,
        "RECEIVED_BYTES" bigint NOT NULL,
        "SHA256" character varying(64),
        "VERSION_ID" bigint,
        "PHASE" character varying(32) NOT NULL,
        "ERROR_CODE" character varying(64),
        "OPERATOR_ID" bigint NOT NULL,
        "STARTED_AT_UTC" timestamp with time zone NOT NULL,
        "FINISHED_AT_UTC" timestamp with time zone,
        CONSTRAINT "PK_DATABASE_UPLOAD_TASK" PRIMARY KEY ("ID"),
        CONSTRAINT "CK_DATABASE_UPLOAD_TASK_BYTES" CHECK ("RECEIVED_BYTES" >= 0),
        CONSTRAINT "CK_DATABASE_UPLOAD_TASK_ID_POSITIVE" CHECK ("ID" > 0),
        CONSTRAINT "CK_DATABASE_UPLOAD_TASK_SHA256" CHECK ("SHA256" IS NULL OR "SHA256" ~ '^[0-9a-fA-F]{64}$'),
        CONSTRAINT "CK_DATABASE_UPLOAD_TASK_TIME" CHECK ("FINISHED_AT_UTC" IS NULL OR "FINISHED_AT_UTC" >= "STARTED_AT_UTC"),
        CONSTRAINT "FK_DATABASE_UPLOAD_TASK_OPERATOR" FOREIGN KEY ("OPERATOR_ID") REFERENCES iplocation."ADMIN_ACCOUNT" ("ID") ON DELETE RESTRICT,
        CONSTRAINT "FK_DATABASE_UPLOAD_TASK_VERSION_PROVIDER" FOREIGN KEY ("VERSION_ID", "PROVIDER_CODE") REFERENCES iplocation."IP_DATABASE_VERSION" ("ID", "PROVIDER_CODE") ON DELETE RESTRICT
    );
    COMMENT ON TABLE iplocation."DATABASE_UPLOAD_TASK" IS '库文件上传和校验进度；任务成功不代表版本已经激活';
    COMMENT ON COLUMN iplocation."DATABASE_UPLOAD_TASK"."ID" IS '上传任务ID，由统一YiteIdService生成';
    COMMENT ON COLUMN iplocation."DATABASE_UPLOAD_TASK"."PROVIDER_CODE" IS '目标来源代码';
    COMMENT ON COLUMN iplocation."DATABASE_UPLOAD_TASK"."STATE" IS '任务受控状态';
    COMMENT ON COLUMN iplocation."DATABASE_UPLOAD_TASK"."RECEIVED_BYTES" IS '已接收字节数';
    COMMENT ON COLUMN iplocation."DATABASE_UPLOAD_TASK"."SHA256" IS '接收完成后的文件摘要，未完成可为空';
    COMMENT ON COLUMN iplocation."DATABASE_UPLOAD_TASK"."VERSION_ID" IS '登记的数据版本ID，尚未登记可为空';
    COMMENT ON COLUMN iplocation."DATABASE_UPLOAD_TASK"."PHASE" IS '接收、校验、建索引等任务阶段';
    COMMENT ON COLUMN iplocation."DATABASE_UPLOAD_TASK"."ERROR_CODE" IS '脱敏机器错误码，不保存原始异常';
    COMMENT ON COLUMN iplocation."DATABASE_UPLOAD_TASK"."OPERATOR_ID" IS '操作管理员ID';
    COMMENT ON COLUMN iplocation."DATABASE_UPLOAD_TASK"."STARTED_AT_UTC" IS '开始时间，UTC';
    COMMENT ON COLUMN iplocation."DATABASE_UPLOAD_TASK"."FINISHED_AT_UTC" IS '完成或失败时间，UTC';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM iplocation."__EFMigrationsHistory" WHERE "MigrationId" = '20261102153235_InitialBackendStorage') THEN
    CREATE TABLE iplocation."IP_DATABASE_ACTIVATION" (
        "PROVIDER_CODE" character varying(16) NOT NULL,
        "ADDRESS_FAMILY" character varying(8) NOT NULL,
        "VERSION_ID" bigint NOT NULL,
        "REVISION" bigint NOT NULL,
        "UPDATED_AT_UTC" timestamp with time zone NOT NULL,
        "UPDATED_BY_ID" bigint,
        CONSTRAINT "PK_IP_DATABASE_ACTIVATION" PRIMARY KEY ("PROVIDER_CODE", "ADDRESS_FAMILY"),
        CONSTRAINT "CK_IP_DB_ACTIVATION_FAMILY" CHECK ("ADDRESS_FAMILY" IN ('ipv4', 'ipv6')),
        CONSTRAINT "CK_IP_DB_ACTIVATION_REVISION" CHECK ("REVISION" > 0),
        CONSTRAINT "FK_IP_DB_ACTIVATION_UPDATED_BY" FOREIGN KEY ("UPDATED_BY_ID") REFERENCES iplocation."ADMIN_ACCOUNT" ("ID") ON DELETE RESTRICT,
        CONSTRAINT "FK_IP_DB_ACTIVATION_VERSION_PROVIDER" FOREIGN KEY ("VERSION_ID", "PROVIDER_CODE") REFERENCES iplocation."IP_DATABASE_VERSION" ("ID", "PROVIDER_CODE") ON DELETE RESTRICT
    );
    COMMENT ON TABLE iplocation."IP_DATABASE_ACTIVATION" IS '每来源每地址族的唯一活动版本；切换须核验许可和期望Revision';
    COMMENT ON COLUMN iplocation."IP_DATABASE_ACTIVATION"."PROVIDER_CODE" IS '来源代码，与引用版本保持一致';
    COMMENT ON COLUMN iplocation."IP_DATABASE_ACTIVATION"."ADDRESS_FAMILY" IS '此活动指针适用的地址族';
    COMMENT ON COLUMN iplocation."IP_DATABASE_ACTIVATION"."VERSION_ID" IS '当前活动数据版本ID';
    COMMENT ON COLUMN iplocation."IP_DATABASE_ACTIVATION"."REVISION" IS '乐观并发版本，初始1，每次成功切换递增';
    COMMENT ON COLUMN iplocation."IP_DATABASE_ACTIVATION"."UPDATED_AT_UTC" IS '活动指针更新时间，UTC';
    COMMENT ON COLUMN iplocation."IP_DATABASE_ACTIVATION"."UPDATED_BY_ID" IS '最近切换管理员ID';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM iplocation."__EFMigrationsHistory" WHERE "MigrationId" = '20261102153235_InitialBackendStorage') THEN
    CREATE INDEX "IX_ACCESS_LOG_DATABASE_OCCURRED" ON iplocation."ACCESS_LOG" ("DATABASE", "OCCURRED_AT_UTC");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM iplocation."__EFMigrationsHistory" WHERE "MigrationId" = '20261102153235_InitialBackendStorage') THEN
    CREATE INDEX "IX_ACCESS_LOG_OCCURRED" ON iplocation."ACCESS_LOG" ("OCCURRED_AT_UTC");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM iplocation."__EFMigrationsHistory" WHERE "MigrationId" = '20261102153235_InitialBackendStorage') THEN
    CREATE INDEX "IX_ACCESS_LOG_ROUTE_OCCURRED" ON iplocation."ACCESS_LOG" ("ROUTE_CATEGORY", "OCCURRED_AT_UTC");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM iplocation."__EFMigrationsHistory" WHERE "MigrationId" = '20261102153235_InitialBackendStorage') THEN
    CREATE UNIQUE INDEX "UX_ACCESS_LOG_EVENT_KEY" ON iplocation."ACCESS_LOG" ("EVENT_KEY");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM iplocation."__EFMigrationsHistory" WHERE "MigrationId" = '20261102153235_InitialBackendStorage') THEN
    CREATE UNIQUE INDEX "UX_ADMIN_ACCOUNT_NORMALIZED_ACCOUNT" ON iplocation."ADMIN_ACCOUNT" ("NORMALIZED_ACCOUNT");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM iplocation."__EFMigrationsHistory" WHERE "MigrationId" = '20261102153235_InitialBackendStorage') THEN
    CREATE INDEX "IX_ADMIN_AUDIT_EVENT_ACTOR_OCCURRED" ON iplocation."ADMIN_AUDIT_EVENT" ("ACTOR_ID", "OCCURRED_AT_UTC");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM iplocation."__EFMigrationsHistory" WHERE "MigrationId" = '20261102153235_InitialBackendStorage') THEN
    CREATE INDEX "IX_ADMIN_AUDIT_EVENT_OCCURRED" ON iplocation."ADMIN_AUDIT_EVENT" ("OCCURRED_AT_UTC");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM iplocation."__EFMigrationsHistory" WHERE "MigrationId" = '20261102153235_InitialBackendStorage') THEN
    CREATE INDEX "IX_ADMIN_IP_ALLOWLIST_CREATED_BY_ID" ON iplocation."ADMIN_IP_ALLOWLIST" ("CREATED_BY_ID");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM iplocation."__EFMigrationsHistory" WHERE "MigrationId" = '20261102153235_InitialBackendStorage') THEN
    CREATE INDEX "IX_ADMIN_IP_ALLOWLIST_ENABLED" ON iplocation."ADMIN_IP_ALLOWLIST" ("ENABLED");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM iplocation."__EFMigrationsHistory" WHERE "MigrationId" = '20261102153235_InitialBackendStorage') THEN
    CREATE INDEX "IX_ADMIN_IP_ALLOWLIST_UPDATED_BY_ID" ON iplocation."ADMIN_IP_ALLOWLIST" ("UPDATED_BY_ID");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM iplocation."__EFMigrationsHistory" WHERE "MigrationId" = '20261102153235_InitialBackendStorage') THEN
    CREATE INDEX "IX_DATABASE_UPLOAD_TASK_OPERATOR_ID" ON iplocation."DATABASE_UPLOAD_TASK" ("OPERATOR_ID");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM iplocation."__EFMigrationsHistory" WHERE "MigrationId" = '20261102153235_InitialBackendStorage') THEN
    CREATE INDEX "IX_DATABASE_UPLOAD_TASK_STATE_STARTED" ON iplocation."DATABASE_UPLOAD_TASK" ("STATE", "STARTED_AT_UTC");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM iplocation."__EFMigrationsHistory" WHERE "MigrationId" = '20261102153235_InitialBackendStorage') THEN
    CREATE INDEX "IX_DATABASE_UPLOAD_TASK_VERSION_ID_PROVIDER_CODE" ON iplocation."DATABASE_UPLOAD_TASK" ("VERSION_ID", "PROVIDER_CODE");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM iplocation."__EFMigrationsHistory" WHERE "MigrationId" = '20261102153235_InitialBackendStorage') THEN
    CREATE INDEX "IX_IP_DATABASE_ACTIVATION_UPDATED_BY_ID" ON iplocation."IP_DATABASE_ACTIVATION" ("UPDATED_BY_ID");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM iplocation."__EFMigrationsHistory" WHERE "MigrationId" = '20261102153235_InitialBackendStorage') THEN
    CREATE INDEX "IX_IP_DATABASE_ACTIVATION_VERSION_ID_PROVIDER_CODE" ON iplocation."IP_DATABASE_ACTIVATION" ("VERSION_ID", "PROVIDER_CODE");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM iplocation."__EFMigrationsHistory" WHERE "MigrationId" = '20261102153235_InitialBackendStorage') THEN
    CREATE INDEX "IX_IP_DATABASE_VERSION_CREATED_BY_ID" ON iplocation."IP_DATABASE_VERSION" ("CREATED_BY_ID");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM iplocation."__EFMigrationsHistory" WHERE "MigrationId" = '20261102153235_InitialBackendStorage') THEN
    CREATE INDEX "IX_IP_DB_VERSION_PROVIDER_CREATED" ON iplocation."IP_DATABASE_VERSION" ("PROVIDER_CODE", "CREATED_AT_UTC");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM iplocation."__EFMigrationsHistory" WHERE "MigrationId" = '20261102153235_InitialBackendStorage') THEN
    CREATE INDEX "IX_IP_DB_VERSION_PROVIDER_SHA256" ON iplocation."IP_DATABASE_VERSION" ("PROVIDER_CODE", "SHA256");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM iplocation."__EFMigrationsHistory" WHERE "MigrationId" = '20261102153235_InitialBackendStorage') THEN
    CREATE INDEX "IX_SYSTEM_SETTING_UPDATED_BY_ID" ON iplocation."SYSTEM_SETTING" ("UPDATED_BY_ID");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM iplocation."__EFMigrationsHistory" WHERE "MigrationId" = '20261102153235_InitialBackendStorage') THEN
    INSERT INTO iplocation."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261102153235_InitialBackendStorage', '10.0.9');
    END IF;
END $EF$;
COMMIT;

