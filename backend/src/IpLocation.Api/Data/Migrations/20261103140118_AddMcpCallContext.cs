using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IpLocation.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMcpCallContext : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MCP_CONTEXT_JSON",
                schema: "iplocation",
                table: "ACCESS_LOG",
                type: "jsonb",
                nullable: true,
                comment: "经白名单清洗及限额的MCP调用上下文；非MCP和历史记录为空");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MCP_CONTEXT_JSON",
                schema: "iplocation",
                table: "ACCESS_LOG");
        }
    }
}
