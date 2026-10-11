using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Octopus.Deployments.Migrations
{
    /// <inheritdoc />
    public partial class AddDockerfilePathAndCustomDomain : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CustomDomain",
                table: "Apps",
                type: "character varying(253)",
                maxLength: 253,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Apps_CustomDomain",
                table: "Apps",
                column: "CustomDomain",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Apps_CustomDomain",
                table: "Apps");

            migrationBuilder.DropColumn(
                name: "CustomDomain",
                table: "Apps");
        }
    }
}
