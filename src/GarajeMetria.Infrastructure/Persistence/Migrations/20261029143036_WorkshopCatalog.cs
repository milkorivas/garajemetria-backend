using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GarajeMetria.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class WorkshopCatalog : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // RF-001 had no corporate codes. Existing identities require an explicit mapping,
        // never a fabricated code or a silent replacement of historical references.
        migrationBuilder.Sql("IF EXISTS (SELECT 1 FROM [WorkshopAccess]) THROW 51001, 'Existing workshop identities require an explicit corporate-code migration plan before RF-009.', 1;");
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "LastObservedAt",
            table: "WorkshopCatalogState",
            type: "datetimeoffset(7)",
            precision: 7,
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "NextObservationVersion",
            table: "WorkshopCatalogState",
            type: "bigint",
            nullable: false,
            defaultValue: 0L);

        migrationBuilder.AddColumn<long>(
            name: "PublishedVersion",
            table: "WorkshopCatalogState",
            type: "bigint",
            nullable: false,
            defaultValue: 0L);

        migrationBuilder.AddColumn<string>(
            name: "Code",
            table: "WorkshopAccess",
            type: "nvarchar(256)",
            maxLength: 256,
            nullable: false,
            defaultValue: "",
            collation: "Latin1_General_100_BIN2");

        migrationBuilder.AddColumn<string>(
            name: "Name",
            table: "WorkshopAccess",
            type: "nvarchar(2048)",
            maxLength: 2048,
            nullable: false,
            defaultValue: "");

        migrationBuilder.CreateIndex(
            name: "IX_WorkshopAccess_Code",
            table: "WorkshopAccess",
            column: "Code",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_WorkshopAccess_Code",
            table: "WorkshopAccess");

        migrationBuilder.DropColumn(
            name: "LastObservedAt",
            table: "WorkshopCatalogState");

        migrationBuilder.DropColumn(
            name: "NextObservationVersion",
            table: "WorkshopCatalogState");

        migrationBuilder.DropColumn(
            name: "PublishedVersion",
            table: "WorkshopCatalogState");

        migrationBuilder.DropColumn(
            name: "Code",
            table: "WorkshopAccess");

        migrationBuilder.DropColumn(
            name: "Name",
            table: "WorkshopAccess");
    }
}
