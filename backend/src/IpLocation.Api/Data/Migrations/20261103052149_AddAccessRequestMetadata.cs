using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IpLocation.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAccessRequestMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "REQUEST_HEADERS_JSON",
                schema: "iplocation",
                table: "ACCESS_LOG",
                type: "jsonb",
                nullable: true,
                comment: "经允许名单清洗及限额的请求头快照；历史未采集为空");

            migrationBuilder.AddColumn<string>(
                name: "SOURCE_LOCATION_JSON",
                schema: "iplocation",
                table: "ACCESS_LOG",
                type: "jsonb",
                nullable: true,
                comment: "后台解析日志来源IP的位置快照；历史未采集为空");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "REQUEST_HEADERS_JSON",
                schema: "iplocation",
                table: "ACCESS_LOG");

            migrationBuilder.DropColumn(
                name: "SOURCE_LOCATION_JSON",
                schema: "iplocation",
                table: "ACCESS_LOG");
        }
    }
}
