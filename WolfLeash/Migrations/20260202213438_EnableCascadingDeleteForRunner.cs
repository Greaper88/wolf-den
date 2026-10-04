using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WolfLeash.Migrations
{
    /// <inheritdoc />
    public partial class EnableCascadingDeleteForRunner : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Rebuild both tables together without disabling foreign keys or clearing app data.
            // Copy shared legacy runners once per app for the new one-to-one relationship.
            migrationBuilder.Sql("""
                CREATE TABLE __DenApps (
                    Id INTEGER NOT NULL CONSTRAINT PK_Apps PRIMARY KEY AUTOINCREMENT,
                    AppId TEXT NOT NULL, Title TEXT NOT NULL, IconPngPath TEXT NULL,
                    SupportHdr INTEGER NULL, StartVirtualCompositor INTEGER NULL,
                    StartAudioServer INTEGER NULL, RenderNode TEXT NULL,
                    H264_gst_pipeline TEXT NULL, Hevc_gst_pipeline TEXT NULL,
                    Av1_gst_pipeline TEXT NULL, Opus_gst_pipeline TEXT NULL);
                INSERT INTO __DenApps
                    (Id, AppId, Title, IconPngPath, SupportHdr, StartVirtualCompositor,
                     StartAudioServer, RenderNode, H264_gst_pipeline, Hevc_gst_pipeline,
                     Av1_gst_pipeline, Opus_gst_pipeline)
                SELECT Id, AppId, Title, IconPngPath, SupportHdr, StartVirtualCompositor,
                       StartAudioServer, RenderNode, H264_gst_pipeline, Hevc_gst_pipeline,
                       Av1_gst_pipeline, Opus_gst_pipeline FROM Apps;
                CREATE TABLE __DenRunners (
                    Id INTEGER NOT NULL CONSTRAINT PK_Runners PRIMARY KEY AUTOINCREMENT,
                    DbAppId INTEGER NOT NULL, Name TEXT NOT NULL, Image TEXT NOT NULL,
                    Type TEXT NOT NULL, Mounts TEXT NOT NULL, Env TEXT NOT NULL,
                    Devices TEXT NOT NULL, Ports TEXT NOT NULL, BaseCreateJson TEXT NOT NULL,
                    CONSTRAINT FK_Runners_Apps_DbAppId FOREIGN KEY (DbAppId) REFERENCES __DenApps(Id) ON DELETE CASCADE);
                INSERT INTO __DenRunners (DbAppId, Name, Image, Type, Mounts, Env, Devices, Ports, BaseCreateJson)
                SELECT a.Id, r.Name, r.Image, r.Type, r.Mounts, r.Env, r.Devices, r.Ports, r.BaseCreateJson
                FROM Apps a JOIN Runners r ON a.RunnerId = r.Id;
                DROP TABLE Apps;
                DROP TABLE Runners;
                ALTER TABLE __DenApps RENAME TO Apps;
                ALTER TABLE __DenRunners RENAME TO Runners;
                CREATE UNIQUE INDEX IX_Apps_AppId ON Apps(AppId);
                CREATE UNIQUE INDEX IX_Runners_DbAppId ON Runners(DbAppId);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Runners_Apps_DbAppId",
                table: "Runners");

            migrationBuilder.DropIndex(
                name: "IX_Runners_DbAppId",
                table: "Runners");

            migrationBuilder.DropColumn(
                name: "DbAppId",
                table: "Runners");

            migrationBuilder.AddColumn<int>(
                name: "RunnerId",
                table: "Apps",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_Apps_RunnerId",
                table: "Apps",
                column: "RunnerId");

            migrationBuilder.AddForeignKey(
                name: "FK_Apps_Runners_RunnerId",
                table: "Apps",
                column: "RunnerId",
                principalTable: "Runners",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
