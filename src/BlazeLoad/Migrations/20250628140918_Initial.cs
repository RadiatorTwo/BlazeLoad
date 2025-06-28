﻿using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BlazeLoad.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Downloads",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    BackendId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Url = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    TargetDirectory = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    Connections = table.Column<int>(type: "INTEGER", nullable: false),
                    PausedDueToDisconnect = table.Column<bool>(type: "INTEGER", nullable: false),
                    TotalBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    DownloadedBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    AddedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    FinishedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Downloads", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Downloads");
        }
    }
}
