using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddKsaNitaqatModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "nitaqat_activities",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    code = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    name_en = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    name_ar = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    activity_group = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    source_note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    is_verified = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nitaqat_activities", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "nitaqat_band_thresholds",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    activity_code = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    size_tier_code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    band = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    band_rank = table.Column<int>(type: "integer", nullable: false),
                    min_saudization_percent = table.Column<decimal>(type: "numeric(6,3)", precision: 6, scale: 3, nullable: false),
                    effective_from = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    effective_to = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    source_note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    is_verified = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nitaqat_band_thresholds", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "nitaqat_employee_weight_overrides",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    category = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    justification = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    deleted_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    deleted_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nitaqat_employee_weight_overrides", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "nitaqat_establishment_profiles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    activity_code = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    mhrsd_establishment_number = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    labour_office_code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    qiwa_reported_band = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    qiwa_reported_on = table.Column<DateOnly>(type: "date", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    deleted_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    deleted_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nitaqat_establishment_profiles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "nitaqat_size_tiers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    name_en = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    name_ar = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    min_workforce = table.Column<int>(type: "integer", nullable: false),
                    max_workforce = table.Column<int>(type: "integer", nullable: true),
                    rank = table.Column<int>(type: "integer", nullable: false),
                    effective_from = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    effective_to = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    source_note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    is_verified = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nitaqat_size_tiers", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "nitaqat_standing_snapshots",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    as_of_date = table.Column<DateOnly>(type: "date", nullable: false),
                    activity_code = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    size_tier_code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    saudi_weighted = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: false),
                    total_weighted = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: false),
                    achieved_percent = table.Column<decimal>(type: "numeric(8,4)", precision: 8, scale: 4, nullable: false),
                    band = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    band_rank = table.Column<int>(type: "integer", nullable: false),
                    raw_saudi_headcount = table.Column<int>(type: "integer", nullable: false),
                    raw_total_headcount = table.Column<int>(type: "integer", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nitaqat_standing_snapshots", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "nitaqat_weight_rules",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    rule_code = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    classification = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    count_basis = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    category = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    numerator_weight = table.Column<decimal>(type: "numeric(8,4)", precision: 8, scale: 4, nullable: false),
                    denominator_weight = table.Column<decimal>(type: "numeric(8,4)", precision: 8, scale: 4, nullable: false),
                    precedence = table.Column<int>(type: "integer", nullable: false),
                    effective_from = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    effective_to = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    source_note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    is_verified = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nitaqat_weight_rules", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_nitaqat_activities_tenant_id_code",
                table: "nitaqat_activities",
                columns: new[] { "tenant_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_nitaqat_band_thresholds_activity_code_size_tier_code",
                table: "nitaqat_band_thresholds",
                columns: new[] { "activity_code", "size_tier_code" });

            migrationBuilder.CreateIndex(
                name: "IX_nitaqat_band_thresholds_tenant_id_activity_code_size_tier_c~",
                table: "nitaqat_band_thresholds",
                columns: new[] { "tenant_id", "activity_code", "size_tier_code", "band", "effective_from" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_nitaqat_employee_weight_overrides_tenant_id_company_id",
                table: "nitaqat_employee_weight_overrides",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_nitaqat_employee_weight_overrides_tenant_id_employee_id",
                table: "nitaqat_employee_weight_overrides",
                columns: new[] { "tenant_id", "employee_id" },
                unique: true,
                filter: "is_deleted = false");

            migrationBuilder.CreateIndex(
                name: "IX_nitaqat_establishment_profiles_tenant_id_company_id",
                table: "nitaqat_establishment_profiles",
                columns: new[] { "tenant_id", "company_id" },
                unique: true,
                filter: "is_deleted = false");

            migrationBuilder.CreateIndex(
                name: "IX_nitaqat_size_tiers_tenant_id_code_effective_from",
                table: "nitaqat_size_tiers",
                columns: new[] { "tenant_id", "code", "effective_from" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_nitaqat_standing_snapshots_tenant_id_company_id",
                table: "nitaqat_standing_snapshots",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_nitaqat_standing_snapshots_tenant_id_company_id_as_of_date",
                table: "nitaqat_standing_snapshots",
                columns: new[] { "tenant_id", "company_id", "as_of_date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_nitaqat_weight_rules_tenant_id_rule_code_effective_from",
                table: "nitaqat_weight_rules",
                columns: new[] { "tenant_id", "rule_code", "effective_from" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "nitaqat_activities");

            migrationBuilder.DropTable(
                name: "nitaqat_band_thresholds");

            migrationBuilder.DropTable(
                name: "nitaqat_employee_weight_overrides");

            migrationBuilder.DropTable(
                name: "nitaqat_establishment_profiles");

            migrationBuilder.DropTable(
                name: "nitaqat_size_tiers");

            migrationBuilder.DropTable(
                name: "nitaqat_standing_snapshots");

            migrationBuilder.DropTable(
                name: "nitaqat_weight_rules");
        }
    }
}
