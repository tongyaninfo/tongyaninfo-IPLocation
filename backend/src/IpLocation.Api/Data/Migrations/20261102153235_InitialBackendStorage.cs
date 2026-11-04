using System;
using System.Net;
using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;

#nullable disable

namespace IpLocation.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialBackendStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "iplocation");

            migrationBuilder.CreateTable(
                name: "ACCESS_LOG",
                schema: "iplocation",
                columns: table => new
                {
                    ID = table.Column<long>(type: "bigint", nullable: false, comment: "访问记录ID，由后台消费者使用统一YiteIdService生成"),
                    EVENT_KEY = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, comment: "稳定事件摘要，批次重试通过唯一约束去重"),
                    OCCURRED_AT_UTC = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, comment: "请求开始时间，UTC"),
                    TRACE_ID = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false, comment: "请求链路标识，不作为主键"),
                    ROUTE_CATEGORY = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, comment: "服务端固定路由类别，不保存原始路径"),
                    METHOD = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, comment: "HTTP方法"),
                    PROTOCOL = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, comment: "HTTP协议版本"),
                    FORMAT = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, comment: "实际响应格式"),
                    DATABASE = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true, comment: "固定来源代码或all；纯IP请求可为空"),
                    DATABASE_VERSION = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true, comment: "实际查询的数据版本摘要"),
                    STATUS_CODE = table.Column<int>(type: "integer", nullable: false, comment: "HTTP响应状态码"),
                    ELAPSED_MILLISECONDS = table.Column<double>(type: "double precision", nullable: false, comment: "请求耗时，毫秒"),
                    IS_CANCELLED = table.Column<bool>(type: "boolean", nullable: false, comment: "客户端是否取消或断开"),
                    SOURCE_ADDRESS = table.Column<NpgsqlInet>(type: "inet", nullable: true, comment: "获准保存的完整IP；关闭完整留存时可保存脱敏网段"),
                    CLIENT_CATEGORY = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, comment: "客户端粗分类，不保存完整User-Agent")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ACCESS_LOG", x => x.ID);
                    table.CheckConstraint("CK_ACCESS_LOG_ELAPSED", "\"ELAPSED_MILLISECONDS\" >= 0 AND \"ELAPSED_MILLISECONDS\" < 'Infinity'::double precision");
                    table.CheckConstraint("CK_ACCESS_LOG_EVENT_KEY", "\"EVENT_KEY\" ~ '^[0-9a-fA-F]{64}$'");
                    table.CheckConstraint("CK_ACCESS_LOG_ID_POSITIVE", "\"ID\" > 0");
                    table.CheckConstraint("CK_ACCESS_LOG_STATUS_CODE", "\"STATUS_CODE\" BETWEEN 100 AND 599");
                },
                comment: "公共访问明细；完整来源IP仅用于获准后台读取，不保存查询目标或完整URL");

            migrationBuilder.CreateTable(
                name: "ADMIN_ACCOUNT",
                schema: "iplocation",
                columns: table => new
                {
                    ID = table.Column<long>(type: "bigint", nullable: false, comment: "管理员ID，由统一YiteIdService生成"),
                    ACCOUNT = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false, comment: "保留显示形式的登录账号"),
                    NORMALIZED_ACCOUNT = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false, comment: "服务统一规范化后的唯一登录账号"),
                    DISPLAY_NAME = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false, comment: "管理员显示名"),
                    PASSWORD_HASH = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false, comment: "带算法信息的安全密码散列，不保存明文"),
                    ENABLED = table.Column<bool>(type: "boolean", nullable: false, comment: "是否允许登录"),
                    SECURITY_VERSION = table.Column<long>(type: "bigint", nullable: false, comment: "身份安全版本；变更时使已有会话失效"),
                    CREATED_AT_UTC = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, comment: "创建时间，UTC"),
                    UPDATED_AT_UTC = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, comment: "最后修改时间，UTC")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ADMIN_ACCOUNT", x => x.ID);
                    table.CheckConstraint("CK_ADMIN_ACCOUNT_ID_POSITIVE", "\"ID\" > 0");
                    table.CheckConstraint("CK_ADMIN_ACCOUNT_REQUIRED_TEXT", "btrim(\"ACCOUNT\") <> '' AND btrim(\"NORMALIZED_ACCOUNT\") <> '' AND btrim(\"PASSWORD_HASH\") <> ''");
                    table.CheckConstraint("CK_ADMIN_ACCOUNT_SECURITY_VERSION", "\"SECURITY_VERSION\" > 0");
                },
                comment: "管理员账户；禁止默认账户种子和明文密码");

            migrationBuilder.CreateTable(
                name: "TRAFFIC_MINUTE_AGGREGATE",
                schema: "iplocation",
                columns: table => new
                {
                    MINUTE_UTC = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, comment: "UTC分钟起点，自然复合键的一部分"),
                    ROUTE_CATEGORY = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, comment: "服务端固定路由分类"),
                    DATABASE = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, comment: "固定来源代码、all或无来源时的空串"),
                    FORMAT = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, comment: "固定响应格式维度"),
                    COUNT_KIND = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, comment: "request为客户端请求，source为实际来源调用"),
                    TOTAL_REQUESTS = table.Column<long>(type: "bigint", nullable: false, comment: "本维度计数；由CountKind区分请求或来源调用"),
                    HOME_HTML_REQUESTS = table.Column<long>(type: "bigint", nullable: false, comment: "首页HTML请求计数"),
                    PLAIN_IP_REQUESTS = table.Column<long>(type: "bigint", nullable: false, comment: "根路径纯IP调用计数"),
                    QUERY_REQUESTS = table.Column<long>(type: "bigint", nullable: false, comment: "逻辑查询请求计数"),
                    CLIENT_ERRORS = table.Column<long>(type: "bigint", nullable: false, comment: "HTTP4xx计数"),
                    SERVER_ERRORS = table.Column<long>(type: "bigint", nullable: false, comment: "HTTP5xx计数"),
                    CANCELLED_REQUESTS = table.Column<long>(type: "bigint", nullable: false, comment: "取消或连接中断计数，可与错误状态重叠"),
                    SUCCESS_COUNT = table.Column<long>(type: "bigint", nullable: false, comment: "HTTP2xx成功计数，不代表数据库命中"),
                    NOT_FOUND_COUNT = table.Column<long>(type: "bigint", nullable: true, comment: "业务无匹配计数；尚未采集该状态时为空"),
                    TOTAL_ELAPSED_MILLISECONDS = table.Column<double>(type: "double precision", nullable: false, comment: "同一口径耗时总和，毫秒"),
                    MIN_ELAPSED_MILLISECONDS = table.Column<double>(type: "double precision", nullable: false, comment: "耗时最小值，毫秒"),
                    MAX_ELAPSED_MILLISECONDS = table.Column<double>(type: "double precision", nullable: false, comment: "耗时最大值，毫秒"),
                    LATENCY_HISTOGRAM_JSON = table.Column<string>(type: "jsonb", nullable: false, comment: "13个固定直方图桶计数；写入端另验证每桶非负和总数"),
                    REVISION = table.Column<long>(type: "bigint", nullable: false, comment: "统计行并发版本，初始1，合并后递增"),
                    UPDATED_AT_UTC = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, comment: "最后成功合并时间，UTC")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TRAFFIC_MINUTE_AGGREGATE", x => new { x.MINUTE_UTC, x.ROUTE_CATEGORY, x.DATABASE, x.FORMAT, x.COUNT_KIND });
                    table.CheckConstraint("CK_TRAFFIC_MINUTE_ALIGNMENT", "date_trunc('minute', \"MINUTE_UTC\" AT TIME ZONE 'UTC') = \"MINUTE_UTC\" AT TIME ZONE 'UTC'");
                    table.CheckConstraint("CK_TRAFFIC_MINUTE_COUNT_KIND", "\"COUNT_KIND\" IN ('request', 'source')");
                    table.CheckConstraint("CK_TRAFFIC_MINUTE_COUNTS", "\"TOTAL_REQUESTS\" >= 0 AND \"HOME_HTML_REQUESTS\" >= 0 AND \"PLAIN_IP_REQUESTS\" >= 0 AND \"QUERY_REQUESTS\" >= 0 AND \"CLIENT_ERRORS\" >= 0 AND \"SERVER_ERRORS\" >= 0 AND \"CANCELLED_REQUESTS\" >= 0 AND \"SUCCESS_COUNT\" >= 0 AND (\"NOT_FOUND_COUNT\" IS NULL OR \"NOT_FOUND_COUNT\" >= 0)");
                    table.CheckConstraint("CK_TRAFFIC_MINUTE_ELAPSED", "\"TOTAL_ELAPSED_MILLISECONDS\" >= 0 AND \"TOTAL_ELAPSED_MILLISECONDS\" < 'Infinity'::double precision AND \"MIN_ELAPSED_MILLISECONDS\" >= 0 AND \"MIN_ELAPSED_MILLISECONDS\" < 'Infinity'::double precision AND \"MAX_ELAPSED_MILLISECONDS\" >= \"MIN_ELAPSED_MILLISECONDS\" AND \"MAX_ELAPSED_MILLISECONDS\" < 'Infinity'::double precision");
                    table.CheckConstraint("CK_TRAFFIC_MINUTE_HISTOGRAM", "CASE WHEN jsonb_typeof(\"LATENCY_HISTOGRAM_JSON\") = 'array' THEN jsonb_array_length(\"LATENCY_HISTOGRAM_JSON\") = 13 ELSE false END");
                    table.CheckConstraint("CK_TRAFFIC_MINUTE_REVISION", "\"REVISION\" > 0");
                },
                comment: "UTC分钟固定维度统计；只聚合已去重事件，直方图合并后计算分位数");

            migrationBuilder.CreateTable(
                name: "ADMIN_AUDIT_EVENT",
                schema: "iplocation",
                columns: table => new
                {
                    ID = table.Column<long>(type: "bigint", nullable: false, comment: "审计事件ID，由统一YiteIdService生成"),
                    ACTOR_ID = table.Column<long>(type: "bigint", nullable: true, comment: "操作管理员ID；身份未知的登录失败可为空"),
                    SOURCE_ADDRESS = table.Column<IPAddress>(type: "inet", nullable: true, comment: "经可信代理规则识别的操作来源地址"),
                    ACTION = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, comment: "服务固定定义的管理动作"),
                    TARGET_TYPE = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true, comment: "被操作对象类型"),
                    TARGET_ID = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true, comment: "被操作对象标识，不包含秘密"),
                    DIFFERENCE_JSON = table.Column<string>(type: "jsonb", nullable: false, comment: "已脱敏的变更差异，不含密码、Token或文件正文"),
                    RESULT = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, comment: "管理操作结果"),
                    TRACE_ID = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false, comment: "请求链路关联标识，不充当业务主键"),
                    OCCURRED_AT_UTC = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, comment: "操作发生时间，UTC")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ADMIN_AUDIT_EVENT", x => x.ID);
                    table.CheckConstraint("CK_ADMIN_AUDIT_EVENT_ID_POSITIVE", "\"ID\" > 0");
                    table.ForeignKey(
                        name: "FK_ADMIN_AUDIT_EVENT_ACTOR",
                        column: x => x.ACTOR_ID,
                        principalSchema: "iplocation",
                        principalTable: "ADMIN_ACCOUNT",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                },
                comment: "管理操作的可靠审计，不通过可丢弃访问队列写入");

            migrationBuilder.CreateTable(
                name: "ADMIN_IP_ALLOWLIST",
                schema: "iplocation",
                columns: table => new
                {
                    ID = table.Column<long>(type: "bigint", nullable: false, comment: "白名单规则ID，由统一YiteIdService生成"),
                    NETWORK = table.Column<IPNetwork>(type: "cidr", nullable: false, comment: "允许网段；精确IPv4/IPv6分别为32/128位前缀"),
                    ENABLED = table.Column<bool>(type: "boolean", nullable: false, comment: "规则是否启用"),
                    NOTE = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true, comment: "规则用途说明"),
                    CREATED_BY_ID = table.Column<long>(type: "bigint", nullable: true, comment: "创建管理员ID；受控本地初始化可为空"),
                    UPDATED_BY_ID = table.Column<long>(type: "bigint", nullable: true, comment: "最后修改管理员ID"),
                    CREATED_AT_UTC = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, comment: "创建时间，UTC"),
                    UPDATED_AT_UTC = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, comment: "最后修改时间，UTC")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ADMIN_IP_ALLOWLIST", x => x.ID);
                    table.CheckConstraint("CK_ADMIN_IP_ALLOWLIST_ID_POSITIVE", "\"ID\" > 0");
                    table.ForeignKey(
                        name: "FK_ADMIN_ALLOWLIST_CREATED_BY",
                        column: x => x.CREATED_BY_ID,
                        principalSchema: "iplocation",
                        principalTable: "ADMIN_ACCOUNT",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ADMIN_ALLOWLIST_UPDATED_BY",
                        column: x => x.UPDATED_BY_ID,
                        principalSchema: "iplocation",
                        principalTable: "ADMIN_ACCOUNT",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                },
                comment: "管理员入口IP白名单；无启用规则时拒绝访问");

            migrationBuilder.CreateTable(
                name: "IP_DATABASE_VERSION",
                schema: "iplocation",
                columns: table => new
                {
                    ID = table.Column<long>(type: "bigint", nullable: false, comment: "数据版本ID，由统一YiteIdService生成"),
                    PROVIDER_CODE = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, comment: "服务固定登记的来源代码"),
                    VERSION = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false, comment: "本次文件版本标识"),
                    FILE_FORMAT = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, comment: "经校验的文件格式"),
                    ADDRESS_FAMILY = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false, comment: "实际覆盖地址族：ipv4、ipv6或dual"),
                    SHA256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, comment: "原始文件SHA-256十六进制摘要"),
                    SIZE_BYTES = table.Column<long>(type: "bigint", nullable: false, comment: "文件字节数"),
                    STORAGE_PATH = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false, comment: "仓库外私有存储定位，不对外展示"),
                    OFFICIAL_VERSION = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true, comment: "供应商提供的官方版本，未知为空"),
                    PUBLISHED_ON = table.Column<DateOnly>(type: "date", nullable: true, comment: "官方仅提供日期时保留原始日期精度"),
                    PUBLISHED_AT_UTC = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true, comment: "官方提供的精确发布时间，UTC，未知为空"),
                    OFFICIAL_RELEASE_INFO_JSON = table.Column<string>(type: "jsonb", nullable: false, comment: "官方发布信息及原始时间精度说明"),
                    LICENSE_EVIDENCE_JSON = table.Column<string>(type: "jsonb", nullable: false, comment: "许可依据及适用范围，不含密钥或Token"),
                    LICENSE_CONFIRMED = table.Column<bool>(type: "boolean", nullable: false, comment: "本项目用途的许可是否已核实"),
                    STATUS = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, comment: "接收、校验、就绪或拒绝等受控版本状态"),
                    CREATED_BY_ID = table.Column<long>(type: "bigint", nullable: false, comment: "上传该版本的管理员ID"),
                    CREATED_AT_UTC = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, comment: "版本登记时间，UTC"),
                    VALIDATED_AT_UTC = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true, comment: "最后完成校验时间，UTC")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IP_DATABASE_VERSION", x => x.ID);
                    table.UniqueConstraint("AK_IP_DB_VERSION_ID_PROVIDER", x => new { x.ID, x.PROVIDER_CODE });
                    table.CheckConstraint("CK_IP_DB_VERSION_FAMILY", "\"ADDRESS_FAMILY\" IN ('ipv4', 'ipv6', 'dual')");
                    table.CheckConstraint("CK_IP_DB_VERSION_ID_POSITIVE", "\"ID\" > 0");
                    table.CheckConstraint("CK_IP_DB_VERSION_REQUIRED_TEXT", "btrim(\"PROVIDER_CODE\") <> '' AND btrim(\"VERSION\") <> '' AND btrim(\"FILE_FORMAT\") <> '' AND btrim(\"STORAGE_PATH\") <> '' AND btrim(\"STATUS\") <> ''");
                    table.CheckConstraint("CK_IP_DB_VERSION_SHA256", "\"SHA256\" ~ '^[0-9a-fA-F]{64}$'");
                    table.CheckConstraint("CK_IP_DB_VERSION_SIZE", "\"SIZE_BYTES\" >= 0");
                    table.ForeignKey(
                        name: "FK_IP_DB_VERSION_CREATED_BY",
                        column: x => x.CREATED_BY_ID,
                        principalSchema: "iplocation",
                        principalTable: "ADMIN_ACCOUNT",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                },
                comment: "IP数据文件的不可变版本、校验与许可元数据；文件不存入本表");

            migrationBuilder.CreateTable(
                name: "SYSTEM_SETTING",
                schema: "iplocation",
                columns: table => new
                {
                    KEY = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false, comment: "服务端固定登记的唯一设置键"),
                    VALUE_JSON = table.Column<string>(type: "jsonb", nullable: false, comment: "经验证的非敏感JSON配置值"),
                    REVISION = table.Column<long>(type: "bigint", nullable: false, comment: "乐观并发版本，初始1，每次成功修改递增"),
                    UPDATED_BY_ID = table.Column<long>(type: "bigint", nullable: true, comment: "修改管理员ID；受控本地初始化可为空"),
                    UPDATED_AT_UTC = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, comment: "最后修改时间，UTC")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SYSTEM_SETTING", x => x.KEY);
                    table.CheckConstraint("CK_SYSTEM_SETTING_KEY", "btrim(\"KEY\") <> ''");
                    table.CheckConstraint("CK_SYSTEM_SETTING_REVISION", "\"REVISION\" > 0");
                    table.ForeignKey(
                        name: "FK_SYSTEM_SETTING_UPDATED_BY",
                        column: x => x.UPDATED_BY_ID,
                        principalSchema: "iplocation",
                        principalTable: "ADMIN_ACCOUNT",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                },
                comment: "非敏感系统设置；禁止保存明文密码或供应商Token");

            migrationBuilder.CreateTable(
                name: "DATABASE_UPLOAD_TASK",
                schema: "iplocation",
                columns: table => new
                {
                    ID = table.Column<long>(type: "bigint", nullable: false, comment: "上传任务ID，由统一YiteIdService生成"),
                    PROVIDER_CODE = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, comment: "目标来源代码"),
                    STATE = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, comment: "任务受控状态"),
                    RECEIVED_BYTES = table.Column<long>(type: "bigint", nullable: false, comment: "已接收字节数"),
                    SHA256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true, comment: "接收完成后的文件摘要，未完成可为空"),
                    VERSION_ID = table.Column<long>(type: "bigint", nullable: true, comment: "登记的数据版本ID，尚未登记可为空"),
                    PHASE = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, comment: "接收、校验、建索引等任务阶段"),
                    ERROR_CODE = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true, comment: "脱敏机器错误码，不保存原始异常"),
                    OPERATOR_ID = table.Column<long>(type: "bigint", nullable: false, comment: "操作管理员ID"),
                    STARTED_AT_UTC = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, comment: "开始时间，UTC"),
                    FINISHED_AT_UTC = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true, comment: "完成或失败时间，UTC")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DATABASE_UPLOAD_TASK", x => x.ID);
                    table.CheckConstraint("CK_DATABASE_UPLOAD_TASK_BYTES", "\"RECEIVED_BYTES\" >= 0");
                    table.CheckConstraint("CK_DATABASE_UPLOAD_TASK_ID_POSITIVE", "\"ID\" > 0");
                    table.CheckConstraint("CK_DATABASE_UPLOAD_TASK_SHA256", "\"SHA256\" IS NULL OR \"SHA256\" ~ '^[0-9a-fA-F]{64}$'");
                    table.CheckConstraint("CK_DATABASE_UPLOAD_TASK_TIME", "\"FINISHED_AT_UTC\" IS NULL OR \"FINISHED_AT_UTC\" >= \"STARTED_AT_UTC\"");
                    table.ForeignKey(
                        name: "FK_DATABASE_UPLOAD_TASK_OPERATOR",
                        column: x => x.OPERATOR_ID,
                        principalSchema: "iplocation",
                        principalTable: "ADMIN_ACCOUNT",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DATABASE_UPLOAD_TASK_VERSION_PROVIDER",
                        columns: x => new { x.VERSION_ID, x.PROVIDER_CODE },
                        principalSchema: "iplocation",
                        principalTable: "IP_DATABASE_VERSION",
                        principalColumns: new[] { "ID", "PROVIDER_CODE" },
                        onDelete: ReferentialAction.Restrict);
                },
                comment: "库文件上传和校验进度；任务成功不代表版本已经激活");

            migrationBuilder.CreateTable(
                name: "IP_DATABASE_ACTIVATION",
                schema: "iplocation",
                columns: table => new
                {
                    PROVIDER_CODE = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, comment: "来源代码，与引用版本保持一致"),
                    ADDRESS_FAMILY = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false, comment: "此活动指针适用的地址族"),
                    VERSION_ID = table.Column<long>(type: "bigint", nullable: false, comment: "当前活动数据版本ID"),
                    REVISION = table.Column<long>(type: "bigint", nullable: false, comment: "乐观并发版本，初始1，每次成功切换递增"),
                    UPDATED_AT_UTC = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, comment: "活动指针更新时间，UTC"),
                    UPDATED_BY_ID = table.Column<long>(type: "bigint", nullable: true, comment: "最近切换管理员ID")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IP_DATABASE_ACTIVATION", x => new { x.PROVIDER_CODE, x.ADDRESS_FAMILY });
                    table.CheckConstraint("CK_IP_DB_ACTIVATION_FAMILY", "\"ADDRESS_FAMILY\" IN ('ipv4', 'ipv6')");
                    table.CheckConstraint("CK_IP_DB_ACTIVATION_REVISION", "\"REVISION\" > 0");
                    table.ForeignKey(
                        name: "FK_IP_DB_ACTIVATION_UPDATED_BY",
                        column: x => x.UPDATED_BY_ID,
                        principalSchema: "iplocation",
                        principalTable: "ADMIN_ACCOUNT",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_IP_DB_ACTIVATION_VERSION_PROVIDER",
                        columns: x => new { x.VERSION_ID, x.PROVIDER_CODE },
                        principalSchema: "iplocation",
                        principalTable: "IP_DATABASE_VERSION",
                        principalColumns: new[] { "ID", "PROVIDER_CODE" },
                        onDelete: ReferentialAction.Restrict);
                },
                comment: "每来源每地址族的唯一活动版本；切换须核验许可和期望Revision");

            migrationBuilder.CreateIndex(
                name: "IX_ACCESS_LOG_DATABASE_OCCURRED",
                schema: "iplocation",
                table: "ACCESS_LOG",
                columns: new[] { "DATABASE", "OCCURRED_AT_UTC" });

            migrationBuilder.CreateIndex(
                name: "IX_ACCESS_LOG_OCCURRED",
                schema: "iplocation",
                table: "ACCESS_LOG",
                column: "OCCURRED_AT_UTC");

            migrationBuilder.CreateIndex(
                name: "IX_ACCESS_LOG_ROUTE_OCCURRED",
                schema: "iplocation",
                table: "ACCESS_LOG",
                columns: new[] { "ROUTE_CATEGORY", "OCCURRED_AT_UTC" });

            migrationBuilder.CreateIndex(
                name: "UX_ACCESS_LOG_EVENT_KEY",
                schema: "iplocation",
                table: "ACCESS_LOG",
                column: "EVENT_KEY",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_ADMIN_ACCOUNT_NORMALIZED_ACCOUNT",
                schema: "iplocation",
                table: "ADMIN_ACCOUNT",
                column: "NORMALIZED_ACCOUNT",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ADMIN_AUDIT_EVENT_ACTOR_OCCURRED",
                schema: "iplocation",
                table: "ADMIN_AUDIT_EVENT",
                columns: new[] { "ACTOR_ID", "OCCURRED_AT_UTC" });

            migrationBuilder.CreateIndex(
                name: "IX_ADMIN_AUDIT_EVENT_OCCURRED",
                schema: "iplocation",
                table: "ADMIN_AUDIT_EVENT",
                column: "OCCURRED_AT_UTC");

            migrationBuilder.CreateIndex(
                name: "IX_ADMIN_IP_ALLOWLIST_CREATED_BY_ID",
                schema: "iplocation",
                table: "ADMIN_IP_ALLOWLIST",
                column: "CREATED_BY_ID");

            migrationBuilder.CreateIndex(
                name: "IX_ADMIN_IP_ALLOWLIST_ENABLED",
                schema: "iplocation",
                table: "ADMIN_IP_ALLOWLIST",
                column: "ENABLED");

            migrationBuilder.CreateIndex(
                name: "IX_ADMIN_IP_ALLOWLIST_UPDATED_BY_ID",
                schema: "iplocation",
                table: "ADMIN_IP_ALLOWLIST",
                column: "UPDATED_BY_ID");

            migrationBuilder.CreateIndex(
                name: "IX_DATABASE_UPLOAD_TASK_OPERATOR_ID",
                schema: "iplocation",
                table: "DATABASE_UPLOAD_TASK",
                column: "OPERATOR_ID");

            migrationBuilder.CreateIndex(
                name: "IX_DATABASE_UPLOAD_TASK_STATE_STARTED",
                schema: "iplocation",
                table: "DATABASE_UPLOAD_TASK",
                columns: new[] { "STATE", "STARTED_AT_UTC" });

            migrationBuilder.CreateIndex(
                name: "IX_DATABASE_UPLOAD_TASK_VERSION_ID_PROVIDER_CODE",
                schema: "iplocation",
                table: "DATABASE_UPLOAD_TASK",
                columns: new[] { "VERSION_ID", "PROVIDER_CODE" });

            migrationBuilder.CreateIndex(
                name: "IX_IP_DATABASE_ACTIVATION_UPDATED_BY_ID",
                schema: "iplocation",
                table: "IP_DATABASE_ACTIVATION",
                column: "UPDATED_BY_ID");

            migrationBuilder.CreateIndex(
                name: "IX_IP_DATABASE_ACTIVATION_VERSION_ID_PROVIDER_CODE",
                schema: "iplocation",
                table: "IP_DATABASE_ACTIVATION",
                columns: new[] { "VERSION_ID", "PROVIDER_CODE" });

            migrationBuilder.CreateIndex(
                name: "IX_IP_DATABASE_VERSION_CREATED_BY_ID",
                schema: "iplocation",
                table: "IP_DATABASE_VERSION",
                column: "CREATED_BY_ID");

            migrationBuilder.CreateIndex(
                name: "IX_IP_DB_VERSION_PROVIDER_CREATED",
                schema: "iplocation",
                table: "IP_DATABASE_VERSION",
                columns: new[] { "PROVIDER_CODE", "CREATED_AT_UTC" });

            migrationBuilder.CreateIndex(
                name: "IX_IP_DB_VERSION_PROVIDER_SHA256",
                schema: "iplocation",
                table: "IP_DATABASE_VERSION",
                columns: new[] { "PROVIDER_CODE", "SHA256" });

            migrationBuilder.CreateIndex(
                name: "IX_SYSTEM_SETTING_UPDATED_BY_ID",
                schema: "iplocation",
                table: "SYSTEM_SETTING",
                column: "UPDATED_BY_ID");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ACCESS_LOG",
                schema: "iplocation");

            migrationBuilder.DropTable(
                name: "ADMIN_AUDIT_EVENT",
                schema: "iplocation");

            migrationBuilder.DropTable(
                name: "ADMIN_IP_ALLOWLIST",
                schema: "iplocation");

            migrationBuilder.DropTable(
                name: "DATABASE_UPLOAD_TASK",
                schema: "iplocation");

            migrationBuilder.DropTable(
                name: "IP_DATABASE_ACTIVATION",
                schema: "iplocation");

            migrationBuilder.DropTable(
                name: "SYSTEM_SETTING",
                schema: "iplocation");

            migrationBuilder.DropTable(
                name: "TRAFFIC_MINUTE_AGGREGATE",
                schema: "iplocation");

            migrationBuilder.DropTable(
                name: "IP_DATABASE_VERSION",
                schema: "iplocation");

            migrationBuilder.DropTable(
                name: "ADMIN_ACCOUNT",
                schema: "iplocation");
        }
    }
}
