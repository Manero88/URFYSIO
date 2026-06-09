using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace URFYSIO.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SyncModelChanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "CompletedAt",
                table: "TreatmentPlans",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsCompleted",
                table: "TreatmentPlans",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "TreatmentPlanEntryComments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TreatmentPlanEntryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Text = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TreatmentPlanEntryComments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TreatmentPlanEntryComments_TreatmentPlanEntries_TreatmentPlanEntryId",
                        column: x => x.TreatmentPlanEntryId,
                        principalTable: "TreatmentPlanEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TreatmentPlanEntryComments_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TreatmentPlanEntryComments_TreatmentPlanEntryId",
                table: "TreatmentPlanEntryComments",
                column: "TreatmentPlanEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_TreatmentPlanEntryComments_UserId",
                table: "TreatmentPlanEntryComments",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TreatmentPlanEntryComments");

            migrationBuilder.DropColumn(
                name: "CompletedAt",
                table: "TreatmentPlans");

            migrationBuilder.DropColumn(
                name: "IsCompleted",
                table: "TreatmentPlans");
        }
    }
}
