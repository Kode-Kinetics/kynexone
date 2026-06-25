using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddFleetAndLogisticsSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "commercial_registration_no",
                table: "branches",
                type: "character varying(80)",
                maxLength: 80,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "national_address_additional_no",
                table: "branches",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "national_address_building_no",
                table: "branches",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "national_address_country",
                table: "branches",
                type: "character varying(80)",
                maxLength: 80,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "national_address_district",
                table: "branches",
                type: "character varying(120)",
                maxLength: 120,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "national_address_postal_code",
                table: "branches",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "national_address_region",
                table: "branches",
                type: "character varying(120)",
                maxLength: 120,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "v_a_t_number",
                table: "branches",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "asset_assignments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    shipment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    carrier_id = table.Column<Guid>(type: "uuid", nullable: true),
                    assignee_type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    assignee_name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    assigned_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    released_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_asset_assignments", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "asset_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    location = table.Column<string>(type: "character varying(180)", maxLength: 180, nullable: false),
                    actor_name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    occurred_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_asset_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "asset_types",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    is_returnable = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_asset_types", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "assets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    asset_type_id = table.Column<Guid>(type: "uuid", nullable: false),
                    asset_tag = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    current_location = table.Column<string>(type: "character varying(180)", maxLength: 180, nullable: false),
                    condition = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    is_returnable = table.Column<bool>(type: "boolean", nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    unit_of_measure = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    last_seen_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_assets", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "barcode_scan_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: true),
                    shipment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    scanned_value = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    scanner_id = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    event_type = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    recorded_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_barcode_scan_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "booking_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_number = table.Column<string>(type: "text", nullable: false),
                    customer_name = table.Column<string>(type: "text", nullable: false),
                    origin = table.Column<string>(type: "text", nullable: false),
                    destination = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    estimated_weight_kg = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    estimated_volume_cbm = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    requested_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    notes = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_booking_requests", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "carrier_contacts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    carrier_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    role = table.Column<string>(type: "text", nullable: false),
                    email = table.Column<string>(type: "text", nullable: false),
                    phone = table.Column<string>(type: "text", nullable: false),
                    notes = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_carrier_contacts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "carrier_performance_scores",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    carrier_id = table.Column<Guid>(type: "uuid", nullable: false),
                    on_time_pct = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    damage_pct = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    acceptance_pct = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    overall_score = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    scored_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    notes = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_carrier_performance_scores", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "carriers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    region = table.Column<string>(type: "text", nullable: false),
                    service_type = table.Column<string>(type: "text", nullable: false),
                    v_a_t_number = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    commercial_registration_no = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    transport_document_no = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    permit_no = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    national_address_building_no = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    national_address_additional_no = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    district = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    city = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    postal_code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    country = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    document_status = table.Column<string>(type: "text", nullable: false),
                    expiry_status = table.Column<string>(type: "text", nullable: false),
                    hijri_expiry_date = table.Column<DateOnly>(type: "date", nullable: true),
                    gregorian_expiry_date = table.Column<DateOnly>(type: "date", nullable: true),
                    on_time_score = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    damage_score = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    cost_score = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    notes = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_carriers", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "cold_chain_reports",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    shipment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    shipment_number = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    generated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    compliance_percent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    min_temperature_celsius = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    max_temperature_celsius = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    total_readings = table.Column<int>(type: "integer", nullable: false),
                    breach_count = table.Column<int>(type: "integer", nullable: false),
                    summary_json = table.Column<string>(type: "json", nullable: false),
                    notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cold_chain_reports", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "customer_tracking_links",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    shipment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token = table.Column<string>(type: "text", nullable: false),
                    expires_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    is_revoked = table.Column<bool>(type: "boolean", nullable: false),
                    shared_by = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    revoked_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_customer_tracking_links", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "delivery_routes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    route_code = table.Column<string>(type: "text", nullable: false),
                    hub = table.Column<string>(type: "text", nullable: false),
                    territory = table.Column<string>(type: "text", nullable: false),
                    driver_name = table.Column<string>(type: "text", nullable: false),
                    vehicle_number = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    planned_stops = table.Column<int>(type: "integer", nullable: false),
                    completed_stops = table.Column<int>(type: "integer", nullable: false),
                    distance_km = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    completion_percent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    current_stop = table.Column<string>(type: "text", nullable: false),
                    next_stop = table.Column<string>(type: "text", nullable: false),
                    planned_for_date = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    departure_time_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    eta_complete_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    notes = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_delivery_routes", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "dispatch_orders",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_number = table.Column<string>(type: "text", nullable: false),
                    customer_name = table.Column<string>(type: "text", nullable: false),
                    customer_segment = table.Column<string>(type: "text", nullable: false),
                    sales_channel = table.Column<string>(type: "text", nullable: false),
                    city = table.Column<string>(type: "text", nullable: false),
                    area = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    priority = table.Column<string>(type: "text", nullable: false),
                    item_count = table.Column<int>(type: "integer", nullable: false),
                    order_value = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    route_code = table.Column<string>(type: "text", nullable: false),
                    driver_name = table.Column<string>(type: "text", nullable: false),
                    vehicle_number = table.Column<string>(type: "text", nullable: false),
                    dispatch_notes = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    promised_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    dispatched_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    delivered_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dispatch_orders", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "driver_tasks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    shipment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stop_id = table.Column<Guid>(type: "uuid", nullable: true),
                    task_type = table.Column<string>(type: "text", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    driver_name = table.Column<string>(type: "text", nullable: false),
                    vehicle_number = table.Column<string>(type: "text", nullable: false),
                    due_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    completed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    notes = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_driver_tasks", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "fleet_fuel_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    vehicle_number = table.Column<string>(type: "text", nullable: false),
                    fuel_card_number = table.Column<string>(type: "text", nullable: false),
                    station_name = table.Column<string>(type: "text", nullable: false),
                    city = table.Column<string>(type: "text", nullable: false),
                    event_type = table.Column<string>(type: "text", nullable: false),
                    anomaly_flag = table.Column<bool>(type: "boolean", nullable: false),
                    liters = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    cost = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    odometer_km = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    notes = table.Column<string>(type: "text", nullable: false),
                    recorded_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fleet_fuel_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "fleet_maintenance_tickets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    work_order_number = table.Column<string>(type: "text", nullable: false),
                    vehicle_number = table.Column<string>(type: "text", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    priority = table.Column<string>(type: "text", nullable: false),
                    vendor_name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    estimated_cost = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    actual_cost = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    downtime_hours = table.Column<decimal>(type: "numeric(8,2)", precision: 8, scale: 2, nullable: false),
                    opened_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    due_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    closed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    notes = table.Column<string>(type: "text", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fleet_maintenance_tickets", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "fleet_readiness_documents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    subject_type = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    subject_id = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    subject_name = table.Column<string>(type: "character varying(180)", maxLength: 180, nullable: false),
                    document_type = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    document_number = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    transport_document_no = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    permit_no = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    v_a_t_number = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    commercial_registration_no = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    country_code = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    national_address_building_no = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    national_address_additional_no = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    district = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    city = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    region = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    postal_code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    document_status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    expiry_status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    issue_date = table.Column<DateOnly>(type: "date", nullable: true),
                    hijri_expiry_date = table.Column<DateOnly>(type: "date", nullable: true),
                    gregorian_expiry_date = table.Column<DateOnly>(type: "date", nullable: true),
                    notes = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fleet_readiness_documents", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "fleet_shipments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    shipment_number = table.Column<string>(type: "text", nullable: false),
                    customer_name = table.Column<string>(type: "text", nullable: false),
                    customer_segment = table.Column<string>(type: "text", nullable: false),
                    origin = table.Column<string>(type: "text", nullable: false),
                    destination = table.Column<string>(type: "text", nullable: false),
                    city = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    priority = table.Column<string>(type: "text", nullable: false),
                    mode = table.Column<string>(type: "text", nullable: false),
                    piece_count = table.Column<int>(type: "integer", nullable: false),
                    weight_kg = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    volume_cbm = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    declared_value = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    carrier_name = table.Column<string>(type: "text", nullable: false),
                    customer_v_a_t_number = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    customer_commercial_registration_no = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    customer_national_address_building_no = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    customer_national_address_additional_no = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    customer_national_address_district = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    customer_national_address_city = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    customer_national_address_region = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    customer_national_address_postal_code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    customer_national_address_country = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    driver_name = table.Column<string>(type: "text", nullable: false),
                    vehicle_number = table.Column<string>(type: "text", nullable: false),
                    route_code = table.Column<string>(type: "text", nullable: false),
                    pod_status = table.Column<string>(type: "text", nullable: false),
                    temperature_range = table.Column<string>(type: "text", nullable: false),
                    notes = table.Column<string>(type: "text", nullable: false),
                    is_invoice_ready = table.Column<bool>(type: "boolean", nullable: false),
                    invoice_ready_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    invoice_readiness_notes = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    pickup_scheduled_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    picked_up_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    delivered_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fleet_shipments", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "fleet_tracking_points",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    shipment_number = table.Column<string>(type: "text", nullable: false),
                    vehicle_number = table.Column<string>(type: "text", nullable: false),
                    location_label = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    geofence_name = table.Column<string>(type: "text", nullable: false),
                    alert_type = table.Column<string>(type: "text", nullable: false),
                    latitude = table.Column<decimal>(type: "numeric(10,7)", precision: 10, scale: 7, nullable: false),
                    longitude = table.Column<decimal>(type: "numeric(10,7)", precision: 10, scale: 7, nullable: false),
                    speed_kph = table.Column<decimal>(type: "numeric(8,2)", precision: 8, scale: 2, nullable: false),
                    recorded_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    estimated_arrival_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    notes = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fleet_tracking_points", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "fleet_vehicles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    vehicle_number = table.Column<string>(type: "text", nullable: false),
                    plate_number = table.Column<string>(type: "text", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    driver_name = table.Column<string>(type: "text", nullable: false),
                    capacity_kg = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    capacity_cbm = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    current_load_kg = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    fuel_level_percent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    odometer_km = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    health_status = table.Column<string>(type: "text", nullable: false),
                    is_refrigerated = table.Column<bool>(type: "boolean", nullable: false),
                    temperature_celsius = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: true),
                    last_known_location = table.Column<string>(type: "text", nullable: false),
                    last_ping_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_service_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    next_service_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    notes = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fleet_vehicles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "last_mile_stops",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_number = table.Column<string>(type: "text", nullable: false),
                    route_code = table.Column<string>(type: "text", nullable: false),
                    customer_name = table.Column<string>(type: "text", nullable: false),
                    address_line = table.Column<string>(type: "text", nullable: false),
                    city = table.Column<string>(type: "text", nullable: false),
                    region = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    postal_code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    country = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    saudi_national_address_building_no = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    saudi_national_address_additional_no = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    saudi_national_address_district = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    proof_status = table.Column<string>(type: "text", nullable: false),
                    recipient_name = table.Column<string>(type: "text", nullable: false),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    rider_name = table.Column<string>(type: "text", nullable: false),
                    time_window = table.Column<string>(type: "text", nullable: false),
                    eta_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    delivered_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    exception_reason = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_last_mile_stops", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "proofs_of_delivery",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    shipment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stop_id = table.Column<Guid>(type: "uuid", nullable: false),
                    captured_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    driver_id = table.Column<Guid>(type: "uuid", nullable: true),
                    vehicle_id = table.Column<Guid>(type: "uuid", nullable: true),
                    recipient_name = table.Column<string>(type: "text", nullable: false),
                    recipient_phone = table.Column<string>(type: "text", nullable: false),
                    signature_url = table.Column<string>(type: "text", nullable: false),
                    photo_url = table.Column<string>(type: "text", nullable: false),
                    document_url = table.Column<string>(type: "text", nullable: false),
                    notes = table.Column<string>(type: "text", nullable: false),
                    delivery_condition = table.Column<string>(type: "text", nullable: false),
                    captured_latitude = table.Column<decimal>(type: "numeric(10,7)", precision: 10, scale: 7, nullable: true),
                    captured_longitude = table.Column<decimal>(type: "numeric(10,7)", precision: 10, scale: 7, nullable: true),
                    captured_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    verified_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    verified_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_proofs_of_delivery", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "quote_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quote_number = table.Column<string>(type: "text", nullable: false),
                    customer_name = table.Column<string>(type: "text", nullable: false),
                    origin = table.Column<string>(type: "text", nullable: false),
                    destination = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    estimated_amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    margin_pct = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    requested_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    notes = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_quote_requests", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "refrigeration_unit_health",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    vehicle_number = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    unit_serial = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    compressor_hours = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    last_service_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    next_service_due_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    temperature_deviation_count = table.Column<int>(type: "integer", nullable: false),
                    notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_refrigeration_unit_health", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "rfid_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: true),
                    shipment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    tag_id = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    reader_id = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    event_type = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    recorded_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rfid_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "saudi_region_references",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    name_en = table.Column<string>(type: "character varying(180)", maxLength: 180, nullable: false),
                    name_ar = table.Column<string>(type: "character varying(180)", maxLength: 180, nullable: false),
                    country_code = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    cities_json = table.Column<string>(type: "json", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    is_gcc_ready = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_saudi_region_references", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "shipment_carrier_assignments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    shipment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    carrier_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    quoted_amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    agreed_amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    notes = table.Column<string>(type: "text", nullable: false),
                    assigned_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shipment_carrier_assignments", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "shipment_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    shipment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "text", nullable: false),
                    message = table.Column<string>(type: "text", nullable: false),
                    actor_name = table.Column<string>(type: "text", nullable: false),
                    occurred_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    visibility = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shipment_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "shipment_stops",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    shipment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stop_type = table.Column<string>(type: "text", nullable: false),
                    sequence_no = table.Column<int>(type: "integer", nullable: false),
                    location_name = table.Column<string>(type: "text", nullable: false),
                    contact_name = table.Column<string>(type: "text", nullable: false),
                    contact_phone = table.Column<string>(type: "text", nullable: false),
                    address_line1 = table.Column<string>(type: "text", nullable: false),
                    address_line2 = table.Column<string>(type: "text", nullable: false),
                    city = table.Column<string>(type: "text", nullable: false),
                    region = table.Column<string>(type: "text", nullable: false),
                    postal_code = table.Column<string>(type: "text", nullable: false),
                    country = table.Column<string>(type: "text", nullable: false),
                    saudi_national_address_building_no = table.Column<string>(type: "text", nullable: false),
                    saudi_national_address_additional_no = table.Column<string>(type: "text", nullable: false),
                    saudi_national_address_district = table.Column<string>(type: "text", nullable: false),
                    latitude = table.Column<decimal>(type: "numeric(10,7)", precision: 10, scale: 7, nullable: true),
                    longitude = table.Column<decimal>(type: "numeric(10,7)", precision: 10, scale: 7, nullable: true),
                    planned_arrival_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    actual_arrival_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    notes = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shipment_stops", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "temperature_alerts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    shipment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reading_id = table.Column<Guid>(type: "uuid", nullable: false),
                    alert_type = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    severity = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    threshold_min = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    threshold_max = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    measured_temperature = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    triggered_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    resolved_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    resolved_by = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    resolution_notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_temperature_alerts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "temperature_devices",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_code = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    zone_id = table.Column<Guid>(type: "uuid", nullable: true),
                    shipment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    vehicle_number = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    last_reported_temperature_celsius = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    battery_percent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    last_ping_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_temperature_devices", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "temperature_readings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    shipment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    zone_id = table.Column<Guid>(type: "uuid", nullable: true),
                    temperature_celsius = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    humidity_percent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: true),
                    latitude = table.Column<decimal>(type: "numeric(10,7)", precision: 10, scale: 7, nullable: true),
                    longitude = table.Column<decimal>(type: "numeric(10,7)", precision: 10, scale: 7, nullable: true),
                    source = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    recorded_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_temperature_readings", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "temperature_zones",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    min_celsius = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    max_celsius = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    color = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_temperature_zones", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_asset_assignments_tenant_id_asset_id_assigned_at_utc",
                table: "asset_assignments",
                columns: new[] { "tenant_id", "asset_id", "assigned_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_asset_assignments_tenant_id_carrier_id",
                table: "asset_assignments",
                columns: new[] { "tenant_id", "carrier_id" });

            migrationBuilder.CreateIndex(
                name: "IX_asset_assignments_tenant_id_shipment_id",
                table: "asset_assignments",
                columns: new[] { "tenant_id", "shipment_id" });

            migrationBuilder.CreateIndex(
                name: "IX_asset_events_tenant_id_asset_id_occurred_at_utc",
                table: "asset_events",
                columns: new[] { "tenant_id", "asset_id", "occurred_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_asset_types_tenant_id_code",
                table: "asset_types",
                columns: new[] { "tenant_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_assets_tenant_id_asset_tag",
                table: "assets",
                columns: new[] { "tenant_id", "asset_tag" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_assets_tenant_id_asset_type_id",
                table: "assets",
                columns: new[] { "tenant_id", "asset_type_id" });

            migrationBuilder.CreateIndex(
                name: "IX_assets_tenant_id_status",
                table: "assets",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_barcode_scan_events_tenant_id_asset_id_recorded_at_utc",
                table: "barcode_scan_events",
                columns: new[] { "tenant_id", "asset_id", "recorded_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_barcode_scan_events_tenant_id_shipment_id_recorded_at_utc",
                table: "barcode_scan_events",
                columns: new[] { "tenant_id", "shipment_id", "recorded_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_booking_requests_tenant_id_request_number",
                table: "booking_requests",
                columns: new[] { "tenant_id", "request_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_booking_requests_tenant_id_status",
                table: "booking_requests",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_carrier_contacts_tenant_id_carrier_id",
                table: "carrier_contacts",
                columns: new[] { "tenant_id", "carrier_id" });

            migrationBuilder.CreateIndex(
                name: "IX_carrier_performance_scores_tenant_id_carrier_id_scored_at_u~",
                table: "carrier_performance_scores",
                columns: new[] { "tenant_id", "carrier_id", "scored_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_carriers_tenant_id_code",
                table: "carriers",
                columns: new[] { "tenant_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_carriers_tenant_id_status",
                table: "carriers",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_cold_chain_reports_tenant_id_generated_at_utc",
                table: "cold_chain_reports",
                columns: new[] { "tenant_id", "generated_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_cold_chain_reports_tenant_id_shipment_id",
                table: "cold_chain_reports",
                columns: new[] { "tenant_id", "shipment_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_customer_tracking_links_tenant_id_is_revoked_expires_at_utc",
                table: "customer_tracking_links",
                columns: new[] { "tenant_id", "is_revoked", "expires_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_customer_tracking_links_tenant_id_shipment_id",
                table: "customer_tracking_links",
                columns: new[] { "tenant_id", "shipment_id" });

            migrationBuilder.CreateIndex(
                name: "IX_customer_tracking_links_tenant_id_token",
                table: "customer_tracking_links",
                columns: new[] { "tenant_id", "token" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_delivery_routes_tenant_id_route_code",
                table: "delivery_routes",
                columns: new[] { "tenant_id", "route_code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_delivery_routes_tenant_id_status",
                table: "delivery_routes",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_dispatch_orders_tenant_id_order_number",
                table: "dispatch_orders",
                columns: new[] { "tenant_id", "order_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_dispatch_orders_tenant_id_route_code",
                table: "dispatch_orders",
                columns: new[] { "tenant_id", "route_code" });

            migrationBuilder.CreateIndex(
                name: "IX_dispatch_orders_tenant_id_status",
                table: "dispatch_orders",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_driver_tasks_tenant_id_driver_name_status",
                table: "driver_tasks",
                columns: new[] { "tenant_id", "driver_name", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_driver_tasks_tenant_id_due_at_utc",
                table: "driver_tasks",
                columns: new[] { "tenant_id", "due_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_driver_tasks_tenant_id_shipment_id",
                table: "driver_tasks",
                columns: new[] { "tenant_id", "shipment_id" });

            migrationBuilder.CreateIndex(
                name: "IX_fleet_fuel_events_tenant_id_anomaly_flag",
                table: "fleet_fuel_events",
                columns: new[] { "tenant_id", "anomaly_flag" });

            migrationBuilder.CreateIndex(
                name: "IX_fleet_fuel_events_tenant_id_recorded_at_utc",
                table: "fleet_fuel_events",
                columns: new[] { "tenant_id", "recorded_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_fleet_fuel_events_tenant_id_vehicle_number",
                table: "fleet_fuel_events",
                columns: new[] { "tenant_id", "vehicle_number" });

            migrationBuilder.CreateIndex(
                name: "IX_fleet_maintenance_tickets_tenant_id_status",
                table: "fleet_maintenance_tickets",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_fleet_maintenance_tickets_tenant_id_vehicle_number",
                table: "fleet_maintenance_tickets",
                columns: new[] { "tenant_id", "vehicle_number" });

            migrationBuilder.CreateIndex(
                name: "IX_fleet_maintenance_tickets_tenant_id_work_order_number",
                table: "fleet_maintenance_tickets",
                columns: new[] { "tenant_id", "work_order_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_fleet_readiness_documents_tenant_id_document_status_expiry_~",
                table: "fleet_readiness_documents",
                columns: new[] { "tenant_id", "document_status", "expiry_status" });

            migrationBuilder.CreateIndex(
                name: "IX_fleet_readiness_documents_tenant_id_gregorian_expiry_date",
                table: "fleet_readiness_documents",
                columns: new[] { "tenant_id", "gregorian_expiry_date" });

            migrationBuilder.CreateIndex(
                name: "IX_fleet_readiness_documents_tenant_id_kind_subject_type_docum~",
                table: "fleet_readiness_documents",
                columns: new[] { "tenant_id", "kind", "subject_type", "document_type" });

            migrationBuilder.CreateIndex(
                name: "IX_fleet_shipments_tenant_id_route_code",
                table: "fleet_shipments",
                columns: new[] { "tenant_id", "route_code" });

            migrationBuilder.CreateIndex(
                name: "IX_fleet_shipments_tenant_id_shipment_number",
                table: "fleet_shipments",
                columns: new[] { "tenant_id", "shipment_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_fleet_shipments_tenant_id_status",
                table: "fleet_shipments",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_fleet_tracking_points_tenant_id_recorded_at_utc",
                table: "fleet_tracking_points",
                columns: new[] { "tenant_id", "recorded_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_fleet_tracking_points_tenant_id_shipment_number",
                table: "fleet_tracking_points",
                columns: new[] { "tenant_id", "shipment_number" });

            migrationBuilder.CreateIndex(
                name: "IX_fleet_tracking_points_tenant_id_vehicle_number",
                table: "fleet_tracking_points",
                columns: new[] { "tenant_id", "vehicle_number" });

            migrationBuilder.CreateIndex(
                name: "IX_fleet_vehicles_tenant_id_driver_name",
                table: "fleet_vehicles",
                columns: new[] { "tenant_id", "driver_name" });

            migrationBuilder.CreateIndex(
                name: "IX_fleet_vehicles_tenant_id_status",
                table: "fleet_vehicles",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_fleet_vehicles_tenant_id_vehicle_number",
                table: "fleet_vehicles",
                columns: new[] { "tenant_id", "vehicle_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_last_mile_stops_tenant_id_eta_utc",
                table: "last_mile_stops",
                columns: new[] { "tenant_id", "eta_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_last_mile_stops_tenant_id_order_number",
                table: "last_mile_stops",
                columns: new[] { "tenant_id", "order_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_last_mile_stops_tenant_id_route_code",
                table: "last_mile_stops",
                columns: new[] { "tenant_id", "route_code" });

            migrationBuilder.CreateIndex(
                name: "IX_last_mile_stops_tenant_id_status",
                table: "last_mile_stops",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_proofs_of_delivery_tenant_id_shipment_id",
                table: "proofs_of_delivery",
                columns: new[] { "tenant_id", "shipment_id" });

            migrationBuilder.CreateIndex(
                name: "IX_proofs_of_delivery_tenant_id_status",
                table: "proofs_of_delivery",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_proofs_of_delivery_tenant_id_stop_id",
                table: "proofs_of_delivery",
                columns: new[] { "tenant_id", "stop_id" });

            migrationBuilder.CreateIndex(
                name: "IX_quote_requests_tenant_id_quote_number",
                table: "quote_requests",
                columns: new[] { "tenant_id", "quote_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_quote_requests_tenant_id_status",
                table: "quote_requests",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_refrigeration_unit_health_tenant_id_status",
                table: "refrigeration_unit_health",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_refrigeration_unit_health_tenant_id_vehicle_number",
                table: "refrigeration_unit_health",
                columns: new[] { "tenant_id", "vehicle_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_rfid_events_tenant_id_asset_id_recorded_at_utc",
                table: "rfid_events",
                columns: new[] { "tenant_id", "asset_id", "recorded_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_rfid_events_tenant_id_shipment_id_recorded_at_utc",
                table: "rfid_events",
                columns: new[] { "tenant_id", "shipment_id", "recorded_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_saudi_region_references_code",
                table: "saudi_region_references",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_shipment_carrier_assignments_tenant_id_carrier_id",
                table: "shipment_carrier_assignments",
                columns: new[] { "tenant_id", "carrier_id" });

            migrationBuilder.CreateIndex(
                name: "IX_shipment_carrier_assignments_tenant_id_shipment_id",
                table: "shipment_carrier_assignments",
                columns: new[] { "tenant_id", "shipment_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_shipment_events_tenant_id_shipment_id_occurred_at_utc",
                table: "shipment_events",
                columns: new[] { "tenant_id", "shipment_id", "occurred_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_shipment_events_tenant_id_visibility",
                table: "shipment_events",
                columns: new[] { "tenant_id", "visibility" });

            migrationBuilder.CreateIndex(
                name: "IX_shipment_stops_tenant_id_shipment_id_sequence_no",
                table: "shipment_stops",
                columns: new[] { "tenant_id", "shipment_id", "sequence_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_shipment_stops_tenant_id_shipment_id_status",
                table: "shipment_stops",
                columns: new[] { "tenant_id", "shipment_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_temperature_alerts_tenant_id_device_id_triggered_at_utc",
                table: "temperature_alerts",
                columns: new[] { "tenant_id", "device_id", "triggered_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_temperature_alerts_tenant_id_shipment_id_status",
                table: "temperature_alerts",
                columns: new[] { "tenant_id", "shipment_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_temperature_alerts_tenant_id_status",
                table: "temperature_alerts",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_temperature_devices_tenant_id_device_code",
                table: "temperature_devices",
                columns: new[] { "tenant_id", "device_code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_temperature_devices_tenant_id_shipment_id",
                table: "temperature_devices",
                columns: new[] { "tenant_id", "shipment_id" });

            migrationBuilder.CreateIndex(
                name: "IX_temperature_devices_tenant_id_status",
                table: "temperature_devices",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_temperature_devices_tenant_id_zone_id",
                table: "temperature_devices",
                columns: new[] { "tenant_id", "zone_id" });

            migrationBuilder.CreateIndex(
                name: "IX_temperature_readings_tenant_id_device_id_recorded_at_utc",
                table: "temperature_readings",
                columns: new[] { "tenant_id", "device_id", "recorded_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_temperature_readings_tenant_id_shipment_id_recorded_at_utc",
                table: "temperature_readings",
                columns: new[] { "tenant_id", "shipment_id", "recorded_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_temperature_readings_tenant_id_zone_id_recorded_at_utc",
                table: "temperature_readings",
                columns: new[] { "tenant_id", "zone_id", "recorded_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_temperature_zones_tenant_id_code",
                table: "temperature_zones",
                columns: new[] { "tenant_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_temperature_zones_tenant_id_is_active",
                table: "temperature_zones",
                columns: new[] { "tenant_id", "is_active" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "asset_assignments");

            migrationBuilder.DropTable(
                name: "asset_events");

            migrationBuilder.DropTable(
                name: "asset_types");

            migrationBuilder.DropTable(
                name: "assets");

            migrationBuilder.DropTable(
                name: "barcode_scan_events");

            migrationBuilder.DropTable(
                name: "booking_requests");

            migrationBuilder.DropTable(
                name: "carrier_contacts");

            migrationBuilder.DropTable(
                name: "carrier_performance_scores");

            migrationBuilder.DropTable(
                name: "carriers");

            migrationBuilder.DropTable(
                name: "cold_chain_reports");

            migrationBuilder.DropTable(
                name: "customer_tracking_links");

            migrationBuilder.DropTable(
                name: "delivery_routes");

            migrationBuilder.DropTable(
                name: "dispatch_orders");

            migrationBuilder.DropTable(
                name: "driver_tasks");

            migrationBuilder.DropTable(
                name: "fleet_fuel_events");

            migrationBuilder.DropTable(
                name: "fleet_maintenance_tickets");

            migrationBuilder.DropTable(
                name: "fleet_readiness_documents");

            migrationBuilder.DropTable(
                name: "fleet_shipments");

            migrationBuilder.DropTable(
                name: "fleet_tracking_points");

            migrationBuilder.DropTable(
                name: "fleet_vehicles");

            migrationBuilder.DropTable(
                name: "last_mile_stops");

            migrationBuilder.DropTable(
                name: "proofs_of_delivery");

            migrationBuilder.DropTable(
                name: "quote_requests");

            migrationBuilder.DropTable(
                name: "refrigeration_unit_health");

            migrationBuilder.DropTable(
                name: "rfid_events");

            migrationBuilder.DropTable(
                name: "saudi_region_references");

            migrationBuilder.DropTable(
                name: "shipment_carrier_assignments");

            migrationBuilder.DropTable(
                name: "shipment_events");

            migrationBuilder.DropTable(
                name: "shipment_stops");

            migrationBuilder.DropTable(
                name: "temperature_alerts");

            migrationBuilder.DropTable(
                name: "temperature_devices");

            migrationBuilder.DropTable(
                name: "temperature_readings");

            migrationBuilder.DropTable(
                name: "temperature_zones");

            migrationBuilder.DropColumn(
                name: "commercial_registration_no",
                table: "branches");

            migrationBuilder.DropColumn(
                name: "national_address_additional_no",
                table: "branches");

            migrationBuilder.DropColumn(
                name: "national_address_building_no",
                table: "branches");

            migrationBuilder.DropColumn(
                name: "national_address_country",
                table: "branches");

            migrationBuilder.DropColumn(
                name: "national_address_district",
                table: "branches");

            migrationBuilder.DropColumn(
                name: "national_address_postal_code",
                table: "branches");

            migrationBuilder.DropColumn(
                name: "national_address_region",
                table: "branches");

            migrationBuilder.DropColumn(
                name: "v_a_t_number",
                table: "branches");
        }
    }
}
