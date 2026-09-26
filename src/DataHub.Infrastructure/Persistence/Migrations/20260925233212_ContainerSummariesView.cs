using DataHub.Infrastructure.Persistence.Sql;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ContainerSummariesView : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql(SqlScripts.Read("ContainerSummaries.sql"));

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql("DROP VIEW IF EXISTS \"ContainerSummaries\";");
    }
}
