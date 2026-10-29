using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <summary>
    /// F2 - device ingest hardening. EXPAND only. attendance_raw_events.dedup_key is NULL on every existing row; the partial unique index
    /// on it is built CONCURRENTLY by the next migration (DeviceDedupIndexConcurrent), so no write lock is taken on the table.
    /// attendance_devices gains the signing/heartbeat columns with safe defaults (no secret, signature not required), so every
    /// device keeps authenticating on its static key until an operator opts in. Down drops the columns.
    /// </summary>
    public partial class DeviceIngestHardening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "dedup_key", table: "attendance_raw_events", type: "character varying(64)", maxLength: 64, nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "bridge_version", table: "attendance_devices", type: "character varying(40)", maxLength: 40, nullable: true);
            migrationBuilder.AddColumn<int>(
                name: "last_clock_skew_seconds", table: "attendance_devices", type: "integer", nullable: true);
            migrationBuilder.AddColumn<DateTime>(
                name: "last_heartbeat_at_utc", table: "attendance_devices", type: "timestamp with time zone", nullable: true);
            migrationBuilder.AddColumn<bool>(
                name: "require_signature", table: "attendance_devices", type: "boolean", nullable: false, defaultValue: false);
            migrationBuilder.AddColumn<int>(
                name: "signing_key_version", table: "attendance_devices", type: "integer", nullable: false, defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "dedup_key", table: "attendance_raw_events");
            migrationBuilder.DropColumn(name: "bridge_version", table: "attendance_devices");
            migrationBuilder.DropColumn(name: "last_clock_skew_seconds", table: "attendance_devices");
            migrationBuilder.DropColumn(name: "last_heartbeat_at_utc", table: "attendance_devices");
            migrationBuilder.DropColumn(name: "require_signature", table: "attendance_devices");
            migrationBuilder.DropColumn(name: "signing_key_version", table: "attendance_devices");
        }
    }
}
