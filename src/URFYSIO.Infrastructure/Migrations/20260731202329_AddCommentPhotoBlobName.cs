using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace URFYSIO.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCommentPhotoBlobName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PhotoBlobName",
                table: "TreatmentPlanEntryComments",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PhotoBlobName",
                table: "TreatmentPlanEntryComments");
        }
    }
}
