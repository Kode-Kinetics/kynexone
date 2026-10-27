using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <summary>
    /// Builds the partial unique index on attendance_raw_events(tenant_id, device_id, dedup_key) CONCURRENTLY, in a migration of
    /// its own with the transaction suppressed (CREATE INDEX CONCURRENTLY cannot run inside one). A plain CREATE UNIQUE INDEX takes
    /// a SHARE lock that blocks device pushes for the duration of the scan. Every existing dedup_key is NULL, so the build cannot
    /// hit a duplicate. IF NOT EXISTS makes a retry after a cancelled build safe (an INVALID leftover is dropped first).
    /// </summary>
    public partial class DeviceDedupIndexConcurrent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS ux_attendance_raw_events_device_dedup;", suppressTransaction: true);
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX CONCURRENTLY IF NOT EXISTS ux_attendance_raw_events_device_dedup " +
                "ON attendance_raw_events (tenant_id, device_id, dedup_key) WHERE dedup_key IS NOT NULL;",
                suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS ux_attendance_raw_events_device_dedup;", suppressTransaction: true);
        }
    }
}
