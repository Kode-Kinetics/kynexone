--
-- PostgreSQL database dump
--

\restrict GHL92Kg4kxYVBxoI3YtcMgguoHM81fCB1qKYSVFeGfb017XnGHOnSlNvik3abHg

-- Dumped from database version 17.10 (4f20678)
-- Dumped by pg_dump version 18.4

SET statement_timeout = 0;
SET lock_timeout = 0;
SET idle_in_transaction_session_timeout = 0;
SET transaction_timeout = 0;
SET client_encoding = 'UTF8';
SET standard_conforming_strings = on;
SELECT pg_catalog.set_config('search_path', '', false);
SET check_function_bodies = false;
SET xmloption = content;
SET client_min_messages = warning;
SET row_security = off;

SET default_tablespace = '';

SET default_table_access_method = heap;

--
-- Name: barcode_scan_events; Type: TABLE; Schema: public; Owner: neondb_owner
--

CREATE TABLE public.barcode_scan_events (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    asset_id uuid,
    shipment_id uuid,
    scanned_value character varying(120) NOT NULL,
    scanner_id character varying(80) NOT NULL,
    event_type character varying(60) NOT NULL,
    status character varying(40) NOT NULL,
    recorded_at_utc timestamp with time zone NOT NULL,
    notes character varying(500) NOT NULL
);


ALTER TABLE public.barcode_scan_events OWNER TO neondb_owner;

--
-- Name: booking_requests; Type: TABLE; Schema: public; Owner: neondb_owner
--

CREATE TABLE public.booking_requests (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    request_number text NOT NULL,
    customer_name text NOT NULL,
    origin text NOT NULL,
    destination text NOT NULL,
    status text NOT NULL,
    estimated_weight_kg numeric(12,2) NOT NULL,
    estimated_volume_cbm numeric(12,2) NOT NULL,
    requested_at_utc timestamp with time zone NOT NULL,
    notes text NOT NULL
);


ALTER TABLE public.booking_requests OWNER TO neondb_owner;

--
-- Name: carrier_contacts; Type: TABLE; Schema: public; Owner: neondb_owner
--

CREATE TABLE public.carrier_contacts (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    carrier_id uuid NOT NULL,
    name text NOT NULL,
    role text NOT NULL,
    email text NOT NULL,
    phone text NOT NULL,
    notes text NOT NULL,
    created_at_utc timestamp with time zone NOT NULL,
    updated_at_utc timestamp with time zone
);


ALTER TABLE public.carrier_contacts OWNER TO neondb_owner;

--
-- Name: carrier_performance_scores; Type: TABLE; Schema: public; Owner: neondb_owner
--

CREATE TABLE public.carrier_performance_scores (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    carrier_id uuid NOT NULL,
    on_time_pct numeric(5,2) NOT NULL,
    damage_pct numeric(5,2) NOT NULL,
    acceptance_pct numeric(5,2) NOT NULL,
    overall_score numeric(5,2) NOT NULL,
    scored_at_utc timestamp with time zone NOT NULL,
    notes text NOT NULL
);


ALTER TABLE public.carrier_performance_scores OWNER TO neondb_owner;

--
-- Name: carriers; Type: TABLE; Schema: public; Owner: neondb_owner
--

CREATE TABLE public.carriers (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    name text NOT NULL,
    code text NOT NULL,
    status text NOT NULL,
    region text NOT NULL,
    service_type text NOT NULL,
    v_a_t_number character varying(40) NOT NULL,
    commercial_registration_no character varying(80) NOT NULL,
    transport_document_no character varying(80) NOT NULL,
    permit_no character varying(80) NOT NULL,
    national_address_building_no character varying(20) NOT NULL,
    national_address_additional_no character varying(20) NOT NULL,
    district character varying(120) NOT NULL,
    city character varying(120) NOT NULL,
    postal_code character varying(40) NOT NULL,
    country character varying(80) NOT NULL,
    document_status text NOT NULL,
    expiry_status text NOT NULL,
    hijri_expiry_date date,
    gregorian_expiry_date date,
    on_time_score numeric(5,2) NOT NULL,
    damage_score numeric(5,2) NOT NULL,
    cost_score numeric(5,2) NOT NULL,
    notes text NOT NULL,
    created_at_utc timestamp with time zone NOT NULL,
    updated_at_utc timestamp with time zone
);


ALTER TABLE public.carriers OWNER TO neondb_owner;

--
-- Name: cold_chain_reports; Type: TABLE; Schema: public; Owner: neondb_owner
--

CREATE TABLE public.cold_chain_reports (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    shipment_id uuid NOT NULL,
    shipment_number character varying(80) NOT NULL,
    generated_at_utc timestamp with time zone NOT NULL,
    compliance_percent numeric(5,2) NOT NULL,
    min_temperature_celsius numeric(5,2) NOT NULL,
    max_temperature_celsius numeric(5,2) NOT NULL,
    total_readings integer NOT NULL,
    breach_count integer NOT NULL,
    summary_json json NOT NULL,
    notes character varying(500) NOT NULL
);


ALTER TABLE public.cold_chain_reports OWNER TO neondb_owner;

--
-- Name: customer_tracking_links; Type: TABLE; Schema: public; Owner: neondb_owner
--

CREATE TABLE public.customer_tracking_links (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    shipment_id uuid NOT NULL,
    token text NOT NULL,
    expires_at_utc timestamp with time zone NOT NULL,
    is_revoked boolean NOT NULL,
    shared_by text NOT NULL,
    created_at_utc timestamp with time zone NOT NULL,
    revoked_at_utc timestamp with time zone,
    updated_at_utc timestamp with time zone
);


ALTER TABLE public.customer_tracking_links OWNER TO neondb_owner;

--
-- Name: delivery_routes; Type: TABLE; Schema: public; Owner: neondb_owner
--

CREATE TABLE public.delivery_routes (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    route_code text NOT NULL,
    hub text NOT NULL,
    territory text NOT NULL,
    driver_name text NOT NULL,
    vehicle_number text NOT NULL,
    status text NOT NULL,
    planned_stops integer NOT NULL,
    completed_stops integer NOT NULL,
    distance_km numeric(10,2) NOT NULL,
    completion_percent numeric(5,2) NOT NULL,
    current_stop text NOT NULL,
    next_stop text NOT NULL,
    planned_for_date timestamp with time zone NOT NULL,
    departure_time_utc timestamp with time zone NOT NULL,
    eta_complete_utc timestamp with time zone,
    notes text NOT NULL
);


ALTER TABLE public.delivery_routes OWNER TO neondb_owner;

--
-- Name: dispatch_orders; Type: TABLE; Schema: public; Owner: neondb_owner
--

CREATE TABLE public.dispatch_orders (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    order_number text NOT NULL,
    customer_name text NOT NULL,
    customer_segment text NOT NULL,
    sales_channel text NOT NULL,
    city text NOT NULL,
    area text NOT NULL,
    status text NOT NULL,
    priority text NOT NULL,
    item_count integer NOT NULL,
    order_value numeric(12,2) NOT NULL,
    route_code text NOT NULL,
    driver_name text NOT NULL,
    vehicle_number text NOT NULL,
    dispatch_notes text NOT NULL,
    created_at_utc timestamp with time zone NOT NULL,
    promised_at_utc timestamp with time zone,
    dispatched_at_utc timestamp with time zone,
    delivered_at_utc timestamp with time zone,
    updated_at_utc timestamp with time zone
);


ALTER TABLE public.dispatch_orders OWNER TO neondb_owner;

--
-- Name: driver_tasks; Type: TABLE; Schema: public; Owner: neondb_owner
--

CREATE TABLE public.driver_tasks (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    shipment_id uuid NOT NULL,
    stop_id uuid,
    task_type text NOT NULL,
    title text NOT NULL,
    description text NOT NULL,
    status text NOT NULL,
    driver_name text NOT NULL,
    vehicle_number text NOT NULL,
    due_at_utc timestamp with time zone NOT NULL,
    completed_at_utc timestamp with time zone,
    notes text NOT NULL,
    created_at_utc timestamp with time zone NOT NULL,
    updated_at_utc timestamp with time zone
);


ALTER TABLE public.driver_tasks OWNER TO neondb_owner;

--
-- Name: fleet_fuel_events; Type: TABLE; Schema: public; Owner: neondb_owner
--

CREATE TABLE public.fleet_fuel_events (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    vehicle_number text NOT NULL,
    fuel_card_number text NOT NULL,
    station_name text NOT NULL,
    city text NOT NULL,
    event_type text NOT NULL,
    anomaly_flag boolean NOT NULL,
    liters numeric(12,2) NOT NULL,
    cost numeric(12,2) NOT NULL,
    odometer_km numeric(12,2) NOT NULL,
    notes text NOT NULL,
    recorded_at_utc timestamp with time zone NOT NULL,
    updated_at_utc timestamp with time zone
);


ALTER TABLE public.fleet_fuel_events OWNER TO neondb_owner;

--
-- Name: fleet_maintenance_tickets; Type: TABLE; Schema: public; Owner: neondb_owner
--

CREATE TABLE public.fleet_maintenance_tickets (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    work_order_number text NOT NULL,
    vehicle_number text NOT NULL,
    type text NOT NULL,
    status text NOT NULL,
    priority text NOT NULL,
    vendor_name text NOT NULL,
    description text NOT NULL,
    estimated_cost numeric(14,2) NOT NULL,
    actual_cost numeric(14,2) NOT NULL,
    downtime_hours numeric(8,2) NOT NULL,
    opened_at_utc timestamp with time zone NOT NULL,
    due_at_utc timestamp with time zone,
    closed_at_utc timestamp with time zone,
    notes text NOT NULL,
    updated_at_utc timestamp with time zone
);


ALTER TABLE public.fleet_maintenance_tickets OWNER TO neondb_owner;

--
-- Name: fleet_readiness_documents; Type: TABLE; Schema: public; Owner: neondb_owner
--

CREATE TABLE public.fleet_readiness_documents (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    kind character varying(40) NOT NULL,
    subject_type character varying(80) NOT NULL,
    subject_id character varying(120) NOT NULL,
    subject_name character varying(180) NOT NULL,
    document_type character varying(120) NOT NULL,
    document_number character varying(120) NOT NULL,
    transport_document_no character varying(120) NOT NULL,
    permit_no character varying(120) NOT NULL,
    v_a_t_number character varying(40) NOT NULL,
    commercial_registration_no character varying(80) NOT NULL,
    country_code character varying(10) NOT NULL,
    national_address_building_no character varying(20) NOT NULL,
    national_address_additional_no character varying(20) NOT NULL,
    district character varying(120) NOT NULL,
    city character varying(120) NOT NULL,
    region character varying(120) NOT NULL,
    postal_code character varying(40) NOT NULL,
    document_status character varying(40) NOT NULL,
    expiry_status character varying(40) NOT NULL,
    issue_date date,
    hijri_expiry_date date,
    gregorian_expiry_date date,
    notes text NOT NULL,
    created_at_utc timestamp with time zone NOT NULL,
    updated_at_utc timestamp with time zone
);


ALTER TABLE public.fleet_readiness_documents OWNER TO neondb_owner;

--
-- Name: fleet_shipments; Type: TABLE; Schema: public; Owner: neondb_owner
--

CREATE TABLE public.fleet_shipments (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    shipment_number text NOT NULL,
    customer_name text NOT NULL,
    customer_segment text NOT NULL,
    origin text NOT NULL,
    destination text NOT NULL,
    city text NOT NULL,
    status text NOT NULL,
    priority text NOT NULL,
    mode text NOT NULL,
    piece_count integer NOT NULL,
    weight_kg numeric(12,2) NOT NULL,
    volume_cbm numeric(12,2) NOT NULL,
    declared_value numeric(12,2) NOT NULL,
    carrier_name text NOT NULL,
    customer_v_a_t_number character varying(40) NOT NULL,
    customer_commercial_registration_no character varying(80) NOT NULL,
    customer_national_address_building_no character varying(20) NOT NULL,
    customer_national_address_additional_no character varying(20) NOT NULL,
    customer_national_address_district character varying(120) NOT NULL,
    customer_national_address_city character varying(120) NOT NULL,
    customer_national_address_region character varying(120) NOT NULL,
    customer_national_address_postal_code character varying(40) NOT NULL,
    customer_national_address_country character varying(80) NOT NULL,
    driver_name text NOT NULL,
    vehicle_number text NOT NULL,
    route_code text NOT NULL,
    pod_status text NOT NULL,
    temperature_range text NOT NULL,
    notes text NOT NULL,
    is_invoice_ready boolean NOT NULL,
    invoice_ready_at_utc timestamp with time zone,
    invoice_readiness_notes text NOT NULL,
    created_at_utc timestamp with time zone NOT NULL,
    pickup_scheduled_at_utc timestamp with time zone,
    picked_up_at_utc timestamp with time zone,
    delivered_at_utc timestamp with time zone,
    updated_at_utc timestamp with time zone
);


ALTER TABLE public.fleet_shipments OWNER TO neondb_owner;

--
-- Name: fleet_tracking_points; Type: TABLE; Schema: public; Owner: neondb_owner
--

CREATE TABLE public.fleet_tracking_points (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    shipment_number text NOT NULL,
    vehicle_number text NOT NULL,
    location_label text NOT NULL,
    status text NOT NULL,
    geofence_name text NOT NULL,
    alert_type text NOT NULL,
    latitude numeric(10,7) NOT NULL,
    longitude numeric(10,7) NOT NULL,
    speed_kph numeric(8,2) NOT NULL,
    recorded_at_utc timestamp with time zone NOT NULL,
    estimated_arrival_utc timestamp with time zone,
    notes text NOT NULL
);


ALTER TABLE public.fleet_tracking_points OWNER TO neondb_owner;

--
-- Name: fleet_vehicles; Type: TABLE; Schema: public; Owner: neondb_owner
--

CREATE TABLE public.fleet_vehicles (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    vehicle_number text NOT NULL,
    plate_number text NOT NULL,
    type text NOT NULL,
    status text NOT NULL,
    driver_name text NOT NULL,
    capacity_kg numeric(12,2) NOT NULL,
    capacity_cbm numeric(12,2) NOT NULL,
    current_load_kg numeric(12,2) NOT NULL,
    fuel_level_percent numeric(5,2) NOT NULL,
    odometer_km numeric(12,2) NOT NULL,
    health_status text NOT NULL,
    is_refrigerated boolean NOT NULL,
    temperature_celsius numeric(5,2),
    last_known_location text NOT NULL,
    last_ping_at_utc timestamp with time zone,
    last_service_at_utc timestamp with time zone,
    next_service_at_utc timestamp with time zone,
    notes text NOT NULL,
    created_at_utc timestamp with time zone NOT NULL,
    updated_at_utc timestamp with time zone
);


ALTER TABLE public.fleet_vehicles OWNER TO neondb_owner;

--
-- Name: last_mile_stops; Type: TABLE; Schema: public; Owner: neondb_owner
--

CREATE TABLE public.last_mile_stops (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    order_number text NOT NULL,
    route_code text NOT NULL,
    customer_name text NOT NULL,
    address_line text NOT NULL,
    city text NOT NULL,
    region character varying(120) NOT NULL,
    postal_code character varying(40) NOT NULL,
    country character varying(80) NOT NULL,
    saudi_national_address_building_no character varying(20) NOT NULL,
    saudi_national_address_additional_no character varying(20) NOT NULL,
    saudi_national_address_district character varying(120) NOT NULL,
    status text NOT NULL,
    proof_status text NOT NULL,
    recipient_name text NOT NULL,
    attempt_count integer NOT NULL,
    rider_name text NOT NULL,
    time_window text NOT NULL,
    eta_utc timestamp with time zone NOT NULL,
    delivered_at_utc timestamp with time zone,
    exception_reason text NOT NULL,
    created_at_utc timestamp with time zone NOT NULL,
    updated_at_utc timestamp with time zone
);


ALTER TABLE public.last_mile_stops OWNER TO neondb_owner;

--
-- Name: proofs_of_delivery; Type: TABLE; Schema: public; Owner: neondb_owner
--

CREATE TABLE public.proofs_of_delivery (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    shipment_id uuid NOT NULL,
    stop_id uuid NOT NULL,
    captured_by_user_id uuid,
    driver_id uuid,
    vehicle_id uuid,
    recipient_name text NOT NULL,
    recipient_phone text NOT NULL,
    signature_url text NOT NULL,
    photo_url text NOT NULL,
    document_url text NOT NULL,
    notes text NOT NULL,
    delivery_condition text NOT NULL,
    captured_latitude numeric(10,7),
    captured_longitude numeric(10,7),
    captured_at timestamp with time zone NOT NULL,
    verified_at timestamp with time zone,
    verified_by_user_id uuid,
    status text NOT NULL,
    created_at timestamp with time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL
);


ALTER TABLE public.proofs_of_delivery OWNER TO neondb_owner;

--
-- Name: quote_requests; Type: TABLE; Schema: public; Owner: neondb_owner
--

CREATE TABLE public.quote_requests (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    quote_number text NOT NULL,
    customer_name text NOT NULL,
    origin text NOT NULL,
    destination text NOT NULL,
    status text NOT NULL,
    estimated_amount numeric(14,2) NOT NULL,
    margin_pct numeric(5,2) NOT NULL,
    requested_at_utc timestamp with time zone NOT NULL,
    notes text NOT NULL
);


ALTER TABLE public.quote_requests OWNER TO neondb_owner;

--
-- Name: refrigeration_unit_health; Type: TABLE; Schema: public; Owner: neondb_owner
--

CREATE TABLE public.refrigeration_unit_health (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    vehicle_number character varying(40) NOT NULL,
    unit_serial character varying(80) NOT NULL,
    status character varying(40) NOT NULL,
    compressor_hours numeric(12,2) NOT NULL,
    last_service_at_utc timestamp with time zone,
    next_service_due_at_utc timestamp with time zone,
    temperature_deviation_count integer NOT NULL,
    notes character varying(500) NOT NULL,
    created_at_utc timestamp with time zone NOT NULL,
    updated_at_utc timestamp with time zone
);


ALTER TABLE public.refrigeration_unit_health OWNER TO neondb_owner;

--
-- Name: rfid_events; Type: TABLE; Schema: public; Owner: neondb_owner
--

CREATE TABLE public.rfid_events (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    asset_id uuid,
    shipment_id uuid,
    tag_id character varying(120) NOT NULL,
    reader_id character varying(80) NOT NULL,
    event_type character varying(60) NOT NULL,
    status character varying(40) NOT NULL,
    recorded_at_utc timestamp with time zone NOT NULL,
    notes character varying(500) NOT NULL
);


ALTER TABLE public.rfid_events OWNER TO neondb_owner;

--
-- Name: saudi_region_references; Type: TABLE; Schema: public; Owner: neondb_owner
--

CREATE TABLE public.saudi_region_references (
    id uuid NOT NULL,
    code text NOT NULL,
    name_en character varying(180) NOT NULL,
    name_ar character varying(180) NOT NULL,
    country_code character varying(10) NOT NULL,
    cities_json json NOT NULL,
    sort_order integer NOT NULL,
    is_gcc_ready boolean NOT NULL,
    created_at_utc timestamp with time zone NOT NULL
);


ALTER TABLE public.saudi_region_references OWNER TO neondb_owner;

--
-- Name: shipment_carrier_assignments; Type: TABLE; Schema: public; Owner: neondb_owner
--

CREATE TABLE public.shipment_carrier_assignments (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    shipment_id uuid NOT NULL,
    carrier_id uuid NOT NULL,
    status text NOT NULL,
    quoted_amount numeric(14,2) NOT NULL,
    agreed_amount numeric(14,2) NOT NULL,
    notes text NOT NULL,
    assigned_at_utc timestamp with time zone NOT NULL,
    updated_at_utc timestamp with time zone
);


ALTER TABLE public.shipment_carrier_assignments OWNER TO neondb_owner;

--
-- Name: shipment_events; Type: TABLE; Schema: public; Owner: neondb_owner
--

CREATE TABLE public.shipment_events (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    shipment_id uuid NOT NULL,
    event_type text NOT NULL,
    message text NOT NULL,
    actor_name text NOT NULL,
    occurred_at_utc timestamp with time zone NOT NULL,
    visibility text NOT NULL,
    created_at_utc timestamp with time zone NOT NULL,
    updated_at_utc timestamp with time zone
);


ALTER TABLE public.shipment_events OWNER TO neondb_owner;

--
-- Name: shipment_stops; Type: TABLE; Schema: public; Owner: neondb_owner
--

CREATE TABLE public.shipment_stops (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    shipment_id uuid NOT NULL,
    stop_type text NOT NULL,
    sequence_no integer NOT NULL,
    location_name text NOT NULL,
    contact_name text NOT NULL,
    contact_phone text NOT NULL,
    address_line1 text NOT NULL,
    address_line2 text NOT NULL,
    city text NOT NULL,
    region text NOT NULL,
    postal_code text NOT NULL,
    country text NOT NULL,
    saudi_national_address_building_no text NOT NULL,
    saudi_national_address_additional_no text NOT NULL,
    saudi_national_address_district text NOT NULL,
    latitude numeric(10,7),
    longitude numeric(10,7),
    planned_arrival_at timestamp with time zone NOT NULL,
    actual_arrival_at timestamp with time zone,
    completed_at timestamp with time zone,
    status text NOT NULL,
    notes text NOT NULL,
    created_at timestamp with time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL
);


ALTER TABLE public.shipment_stops OWNER TO neondb_owner;

--
-- Name: temperature_alerts; Type: TABLE; Schema: public; Owner: neondb_owner
--

CREATE TABLE public.temperature_alerts (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    device_id uuid NOT NULL,
    shipment_id uuid,
    reading_id uuid NOT NULL,
    alert_type character varying(60) NOT NULL,
    severity character varying(20) NOT NULL,
    status character varying(20) NOT NULL,
    threshold_min numeric(5,2) NOT NULL,
    threshold_max numeric(5,2) NOT NULL,
    measured_temperature numeric(5,2) NOT NULL,
    triggered_at_utc timestamp with time zone NOT NULL,
    resolved_at_utc timestamp with time zone,
    resolved_by character varying(120) NOT NULL,
    resolution_notes character varying(500) NOT NULL,
    notes character varying(500) NOT NULL
);


ALTER TABLE public.temperature_alerts OWNER TO neondb_owner;

--
-- Name: temperature_devices; Type: TABLE; Schema: public; Owner: neondb_owner
--

CREATE TABLE public.temperature_devices (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    device_code character varying(60) NOT NULL,
    name character varying(120) NOT NULL,
    zone_id uuid,
    shipment_id uuid,
    vehicle_number character varying(40) NOT NULL,
    status character varying(40) NOT NULL,
    last_reported_temperature_celsius numeric(5,2) NOT NULL,
    battery_percent numeric(5,2) NOT NULL,
    last_ping_at_utc timestamp with time zone,
    notes character varying(500) NOT NULL,
    created_at_utc timestamp with time zone NOT NULL,
    updated_at_utc timestamp with time zone
);


ALTER TABLE public.temperature_devices OWNER TO neondb_owner;

--
-- Name: temperature_readings; Type: TABLE; Schema: public; Owner: neondb_owner
--

CREATE TABLE public.temperature_readings (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    device_id uuid NOT NULL,
    shipment_id uuid,
    zone_id uuid,
    temperature_celsius numeric(5,2) NOT NULL,
    humidity_percent numeric(5,2),
    latitude numeric(10,7),
    longitude numeric(10,7),
    source character varying(40) NOT NULL,
    status character varying(40) NOT NULL,
    notes character varying(500) NOT NULL,
    recorded_at_utc timestamp with time zone NOT NULL,
    created_at_utc timestamp with time zone NOT NULL
);


ALTER TABLE public.temperature_readings OWNER TO neondb_owner;

--
-- Name: temperature_zones; Type: TABLE; Schema: public; Owner: neondb_owner
--

CREATE TABLE public.temperature_zones (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    code character varying(40) NOT NULL,
    name character varying(120) NOT NULL,
    min_celsius numeric(5,2) NOT NULL,
    max_celsius numeric(5,2) NOT NULL,
    color character varying(40) NOT NULL,
    is_active boolean NOT NULL,
    notes character varying(500) NOT NULL,
    created_at_utc timestamp with time zone NOT NULL,
    updated_at_utc timestamp with time zone
);


ALTER TABLE public.temperature_zones OWNER TO neondb_owner;

--
-- Data for Name: barcode_scan_events; Type: TABLE DATA; Schema: public; Owner: neondb_owner
--

COPY public.barcode_scan_events (id, tenant_id, asset_id, shipment_id, scanned_value, scanner_id, event_type, status, recorded_at_utc, notes) FROM stdin;
02aacbd8-c025-4199-9755-76baf3e934e6	15b0c4c2-bc3f-4428-a448-bc92e937038f	2d88c0ca-df2d-4397-994b-57088437f482	c08ddedf-9e57-4863-b53b-c8c2babc904e	AST-RASALMANAR-700	SCAN-RASALMANAR-12	Scan	Captured	2026-06-25 10:09:52.684931+00	Barcode scan captured in the warehouse.
756b4fc0-bc1a-4f4e-8342-515764c58df9	15b0c4c2-bc3f-4428-a448-bc92e937038f	611f64e2-552d-4718-b6e8-c5b664faea6b	dced6032-cde6-4113-93a5-06deb07bfd3e	AST-RASALMANAR-701	SCAN-RASALMANAR-22	Scan	Captured	2026-06-25 10:02:52.684931+00	Barcode scan captured in the warehouse.
76a299e7-1727-470c-b794-d9dbe7eff548	15b0c4c2-bc3f-4428-a448-bc92e937038f	32496dc1-051c-4ff5-80c4-ea14064ab5d8	924bfa7f-2fb5-4dc1-be16-4b9959807917	AST-RASALMANAR-702	SCAN-RASALMANAR-32	Scan	Captured	2026-06-25 09:55:52.684931+00	Barcode scan captured in the warehouse.
\.


--
-- Data for Name: booking_requests; Type: TABLE DATA; Schema: public; Owner: neondb_owner
--

COPY public.booking_requests (id, tenant_id, request_number, customer_name, origin, destination, status, estimated_weight_kg, estimated_volume_cbm, requested_at_utc, notes) FROM stdin;
93399a97-f420-44e6-8339-4b79deea04a5	15b0c4c2-bc3f-4428-a448-bc92e937038f	BKG-260625-302	Tamimi Markets	Dammam Hub	Eastern Province	Quoted	780.00	4.00	2026-06-21 10:44:52.055663+00	Seed booking request.
bd3eebd2-ee6b-46a8-a90a-665570a22f19	15b0c4c2-bc3f-4428-a448-bc92e937038f	BKG-260625-300	Almarai Distribution	Riyadh DC	Central Riyadh	Open	500.00	2.40	2026-06-23 10:44:52.055656+00	Seed booking request.
e959139b-b535-4d31-b304-b971c6e90dec	15b0c4c2-bc3f-4428-a448-bc92e937038f	BKG-260625-301	Nahdi Logistics	Jeddah Gateway	North Jeddah	Quoted	640.00	3.20	2026-06-22 10:44:52.05566+00	Seed booking request.
e15450c0-4c0f-47b1-856d-fc573e8172b3	15b0c4c2-bc3f-4428-a448-bc92e937038f	SMOKE-BKG-064857	Smoke Customer	Riyadh DC	Jeddah	Open	500.00	4.00	2026-06-25 10:49:04.330191+00	Smoke booking
\.


--
-- Data for Name: carrier_contacts; Type: TABLE DATA; Schema: public; Owner: neondb_owner
--

COPY public.carrier_contacts (id, tenant_id, carrier_id, name, role, email, phone, notes, created_at_utc, updated_at_utc) FROM stdin;
\.


--
-- Data for Name: carrier_performance_scores; Type: TABLE DATA; Schema: public; Owner: neondb_owner
--

COPY public.carrier_performance_scores (id, tenant_id, carrier_id, on_time_pct, damage_pct, acceptance_pct, overall_score, scored_at_utc, notes) FROM stdin;
\.


--
-- Data for Name: carriers; Type: TABLE DATA; Schema: public; Owner: neondb_owner
--

COPY public.carriers (id, tenant_id, name, code, status, region, service_type, v_a_t_number, commercial_registration_no, transport_document_no, permit_no, national_address_building_no, national_address_additional_no, district, city, postal_code, country, document_status, expiry_status, hijri_expiry_date, gregorian_expiry_date, on_time_score, damage_score, cost_score, notes, created_at_utc, updated_at_utc) FROM stdin;
2b38e2b8-af4d-4bbf-b93c-8e224cce9032	15b0c4c2-bc3f-4428-a448-bc92e937038f	Gulf Connect	CR-260625-32	Active	Eastern	Express	315b0c4c2bc3	715b0c4c2b	TD-260625-32	PERM-260625-32	402	62	Business District	Dammam	31411	Saudi Arabia	Active	ExpiringSoon	2027-01-25	2027-01-25	87.60	2.30	84.40	Seed carrier record for operational demo.	2026-06-11 10:44:52.055652+00	2026-06-25 10:44:52.056262+00
41b672e0-3124-456c-8fe0-6efd2fdfa03b	15b0c4c2-bc3f-4428-a448-bc92e937038f	Rapid Freight	CR-260625-12	Active	Central	Road	315b0c4c2bc3	715b0c4c2b	TD-260625-12	PERM-260625-12	400	60	Industrial City	Riyadh	12211	Saudi Arabia	Active	Healthy	2027-03-25	2027-03-25	92.00	1.50	88.00	Seed carrier record for operational demo.	2026-06-13 10:44:52.05564+00	2026-06-25 10:44:52.056262+00
de0d581c-49a2-44ba-a065-9d82580bfc61	15b0c4c2-bc3f-4428-a448-bc92e937038f	Desert Haul	CR-260625-22	Active	Western	Cold Chain	315b0c4c2bc3	715b0c4c2b	TD-260625-22	PERM-260625-22	401	61	Al Aziziyah	Jeddah	21411	Saudi Arabia	Active	Healthy	2027-02-25	2027-02-25	89.80	1.90	86.20	Seed carrier record for operational demo.	2026-06-12 10:44:52.055647+00	2026-06-25 10:44:52.056262+00
\.


--
-- Data for Name: cold_chain_reports; Type: TABLE DATA; Schema: public; Owner: neondb_owner
--

COPY public.cold_chain_reports (id, tenant_id, shipment_id, shipment_number, generated_at_utc, compliance_percent, min_temperature_celsius, max_temperature_celsius, total_readings, breach_count, summary_json, notes) FROM stdin;
5433725a-aae6-424d-bb9b-012f4a25f4e1	15b0c4c2-bc3f-4428-a448-bc92e937038f	c08ddedf-9e57-4863-b53b-c8c2babc904e	SHP-260625-200	2026-06-25 10:44:52.684931+00	100.00	4.00	4.40	6	0	{"shipment":"SHP-260625-200","deviceCount":1,"breachCount":0}	Generated cold-chain compliance view for shipment review.
6ac3cd1c-b398-482a-8837-31345d1399b7	15b0c4c2-bc3f-4428-a448-bc92e937038f	dced6032-cde6-4113-93a5-06deb07bfd3e	SHP-260625-201	2026-06-25 10:44:52.684931+00	83.30	-17.50	-7.50	6	1	{"shipment":"SHP-260625-201","deviceCount":1,"breachCount":1}	Generated cold-chain compliance view for shipment review.
\.


--
-- Data for Name: customer_tracking_links; Type: TABLE DATA; Schema: public; Owner: neondb_owner
--

COPY public.customer_tracking_links (id, tenant_id, shipment_id, token, expires_at_utc, is_revoked, shared_by, created_at_utc, revoked_at_utc, updated_at_utc) FROM stdin;
45dfe656-bbca-44ed-bfd5-f9cb1e787c7d	15b0c4c2-bc3f-4428-a448-bc92e937038f	c08ddedf-9e57-4863-b53b-c8c2babc904e	trk_68129c50eca147f892404a87d2c78230	2026-06-30 10:44:52.055681+00	f	Fleet Ops	2026-06-25 07:44:52.055681+00	\N	2026-06-25 10:44:52.056262+00
7985d9f9-939f-402b-b3d5-8b77bc7111d7	15b0c4c2-bc3f-4428-a448-bc92e937038f	924bfa7f-2fb5-4dc1-be16-4b9959807917	trk_d13907be0ff24b6eac1718a3792b0b6c	2026-07-02 10:44:52.055714+00	f	Fleet Ops	2026-06-25 07:44:52.055714+00	\N	2026-06-25 10:44:52.056262+00
a7d436ac-b4ee-4d0d-81e2-26273878df06	15b0c4c2-bc3f-4428-a448-bc92e937038f	dced6032-cde6-4113-93a5-06deb07bfd3e	trk_2c333e4a791b4decbe5d60e50304ea9e	2026-07-01 10:44:52.055699+00	f	Fleet Ops	2026-06-25 07:44:52.055699+00	\N	2026-06-25 10:44:52.056262+00
37b3a4b0-827b-444c-8f60-a766b67d6ebb	15b0c4c2-bc3f-4428-a448-bc92e937038f	b51217b1-ba3f-46d4-8816-1b08a6018cbe	f09df98ec04c4860a78946550da62fb6	2026-07-02 12:00:00+00	f	system	2026-06-25 10:49:03.902775+00	\N	2026-06-25 10:49:03.90381+00
\.


--
-- Data for Name: delivery_routes; Type: TABLE DATA; Schema: public; Owner: neondb_owner
--

COPY public.delivery_routes (id, tenant_id, route_code, hub, territory, driver_name, vehicle_number, status, planned_stops, completed_stops, distance_km, completion_percent, current_stop, next_stop, planned_for_date, departure_time_utc, eta_complete_utc, notes) FROM stdin;
6dc9ae14-4dcb-4b1f-b2d2-581bdda3c56b	15b0c4c2-bc3f-4428-a448-bc92e937038f	RT-260625-32	South Hub	Residential Loop	Fatimah Al-Shehri	VAN-70	Delayed	16	12	55.30	75.00	ByteWave Store · Dammam	Capital Coffee · Khobar	2026-06-25 00:00:00+00	2026-06-25 07:02:38.394281+00	2026-06-25 13:20:38.394281+00	Traffic expected after 5 PM on eastern belt
db75226d-c4a1-419b-97e2-38d4b5d2e2fd	15b0c4c2-bc3f-4428-a448-bc92e937038f	RT-260625-22	Central Hub	Business District	Mohammed Al-Otaibi	VAN-56	Active	15	10	46.90	66.70	Oasis Mart · Jeddah	ByteWave Store · Dammam	2026-06-25 00:00:00+00	2026-06-25 06:53:38.394278+00	2026-06-25 13:02:38.394278+00	All parcels scanned at hub departure
d0aa8557-5f59-47e0-aef3-30c64cbab7d8	15b0c4c2-bc3f-4428-a448-bc92e937038f	SMOKE-RT-064857	Central Hub	City Core	Smoke Driver	SMOKE-VAN-064857	Planned	8	0	42.00	0.00			2026-06-25 00:00:00+00	2026-06-25 10:49:00+00	\N	
11941362-6dc1-4d99-aef4-3baa0969aad1	15b0c4c2-bc3f-4428-a448-bc92e937038f	RT-260625-12	North Hub	City Core	Sara Al-Dossari	VAN-42	Active	12	9	38.50	75.00	Zenith Traders · Riyadh	Oasis Mart · Jeddah	2026-06-25 00:00:00+00	2026-06-25 06:44:38.394268+00	2026-06-25 12:44:38.394269+00	Smoke progress update
ab17e4ce-e1f2-4b93-9210-90975b30f0f3	15b0c4c2-bc3f-4428-a448-bc92e937038f	RT-260625-42	North Hub	Industrial Edge	Khalid Al-Harbi	VAN-84	Closed	19	14	63.70	73.70	Sama Clinic	Smoke Retry	2026-06-25 00:00:00+00	2026-06-25 07:11:38.394284+00	2026-06-25 13:38:38.394284+00	Smoke test attempt
\.


--
-- Data for Name: dispatch_orders; Type: TABLE DATA; Schema: public; Owner: neondb_owner
--

COPY public.dispatch_orders (id, tenant_id, order_number, customer_name, customer_segment, sales_channel, city, area, status, priority, item_count, order_value, route_code, driver_name, vehicle_number, dispatch_notes, created_at_utc, promised_at_utc, dispatched_at_utc, delivered_at_utc, updated_at_utc) FROM stdin;
0478d08e-391d-4825-b30d-04c3fd4283fe	15b0c4c2-bc3f-4428-a448-bc92e937038f	ORD-260625-106	Misk Home	Enterprise	Partner API	Dammam	Residential Loop	Picking	Critical	3	585.00	RT-260625-32	Fatimah Al-Shehri	VAN-70	Packed, scanned, and ready for route assignment	2026-06-25 08:44:38.541332+00	2026-06-25 13:44:38.54133+00	\N	\N	2026-06-25 10:44:38.541532+00
0718cf26-824c-43a1-851a-8e6fb481f98d	15b0c4c2-bc3f-4428-a448-bc92e937038f	ORD-260625-104	ByteWave Store	Retail	Portal	Riyadh	City Core	Exception	High	6	450.00	RT-260625-12	Sara Al-Dossari	VAN-42	Customer unreachable at the first attempt	2026-06-25 06:44:38.541323+00	2026-06-25 16:44:38.541321+00	\N	\N	2026-06-25 10:44:38.541532+00
36a201fe-c48b-459b-8a3d-ba49ed0f3219	15b0c4c2-bc3f-4428-a448-bc92e937038f	ORD-260625-100	Al Noor Pharmacy	Enterprise	Portal	Riyadh	City Core	Packed	High	2	180.00	RT-260625-12	Sara Al-Dossari	VAN-42	Packed, scanned, and ready for route assignment	2026-06-25 02:44:38.541295+00	2026-06-25 12:44:38.541264+00	\N	\N	2026-06-25 10:44:38.541532+00
3d7b18e6-23f5-4122-8b1b-e65c1af41aec	15b0c4c2-bc3f-4428-a448-bc92e937038f	ORD-260625-107	Nova Supplies	Retail	Sales Desk	Khobar	Industrial Edge	Packed	Normal	4	652.50	RT-260625-42	Khalid Al-Harbi	VAN-84	Packed, scanned, and ready for route assignment	2026-06-25 09:44:38.541338+00	2026-06-25 14:44:38.541335+00	\N	\N	2026-06-25 10:44:38.541532+00
41982fd5-a86f-4540-a3f4-4f9bbd24f197	15b0c4c2-bc3f-4428-a448-bc92e937038f	ORD-260625-110	Al Noor Pharmacy	Retail	Partner API	Dammam	Residential Loop	Delivered	Normal	2	855.00	RT-260625-32	Fatimah Al-Shehri	VAN-70	Packed, scanned, and ready for route assignment	2026-06-25 12:44:38.541362+00	2026-06-25 12:44:38.541361+00	2026-06-25 10:44:38.541363+00	2026-06-25 10:44:38.541363+00	2026-06-25 10:44:38.541532+00
684fe620-d5eb-4045-9932-72680fdac8b3	15b0c4c2-bc3f-4428-a448-bc92e937038f	ORD-260625-101	Sama Clinic	Retail	WhatsApp	Jeddah	Business District	Dispatched	Normal	3	247.50	RT-260625-22	Mohammed Al-Otaibi	VAN-56	Packed, scanned, and ready for route assignment	2026-06-25 03:44:38.541305+00	2026-06-25 13:44:38.541303+00	2026-06-25 08:56:38.541305+00	\N	2026-06-25 10:44:38.541532+00
a234a408-7d61-4c2e-8879-108b1362b6bd	15b0c4c2-bc3f-4428-a448-bc92e937038f	ORD-260625-103	Oasis Mart	Enterprise	Sales Desk	Khobar	Industrial Edge	Delivered	Normal	5	382.50	RT-260625-42	Khalid Al-Harbi	VAN-84	Packed, scanned, and ready for route assignment	2026-06-25 05:44:38.541315+00	2026-06-25 15:44:38.541313+00	2026-06-25 09:20:38.541315+00	2026-06-25 10:02:38.541315+00	2026-06-25 10:44:38.541532+00
aa1c3195-c3cc-4c76-96a4-ca263137fa72	15b0c4c2-bc3f-4428-a448-bc92e937038f	ORD-260625-109	Urban Fit	Enterprise	WhatsApp	Jeddah	Business District	InTransit	Normal	6	787.50	RT-260625-22	Mohammed Al-Otaibi	VAN-56	Packed, scanned, and ready for route assignment	2026-06-25 11:44:38.541359+00	2026-06-25 16:44:38.54135+00	2026-06-25 10:32:38.541359+00	\N	2026-06-25 10:44:38.541532+00
bd4fe1f2-c175-484f-b1d4-40126996a6bc	15b0c4c2-bc3f-4428-a448-bc92e937038f	ORD-260625-108	Royal Beauty	SME	Portal	Riyadh	City Core	Dispatched	High	5	720.00	RT-260625-12	Sara Al-Dossari	VAN-42	Packed, scanned, and ready for route assignment	2026-06-25 10:44:38.541348+00	2026-06-25 15:44:38.541345+00	2026-06-25 10:20:38.541348+00	\N	2026-06-25 10:44:38.541532+00
d9b6083f-489f-4db3-92f5-d820424bf929	15b0c4c2-bc3f-4428-a448-bc92e937038f	ORD-260625-105	Capital Coffee	SME	WhatsApp	Jeddah	Business District	Queued	Normal	2	517.50	RT-260625-22	Mohammed Al-Otaibi	VAN-56	Packed, scanned, and ready for route assignment	2026-06-25 07:44:38.541328+00	2026-06-25 12:44:38.541326+00	\N	\N	2026-06-25 10:44:38.541532+00
f2ad238f-82e5-4c27-8c93-da282ab68229	15b0c4c2-bc3f-4428-a448-bc92e937038f	ORD-260625-102	Zenith Traders	SME	Partner API	Dammam	Residential Loop	InTransit	Normal	4	315.00	RT-260625-32	Fatimah Al-Shehri	VAN-70	Packed, scanned, and ready for route assignment	2026-06-25 04:44:38.54131+00	2026-06-25 14:44:38.541308+00	2026-06-25 09:08:38.54131+00	\N	2026-06-25 10:44:38.541532+00
ce0d7324-c9d5-4dce-a46d-f635789e04ba	15b0c4c2-bc3f-4428-a448-bc92e937038f	SMOKE-ORD-064857	Smoke Customer	Retail	Portal	Riyadh	City Core	Queued	Normal	3	250.00	RT-260625-12				2026-06-25 10:49:00.454016+00	\N	\N	\N	2026-06-25 10:49:00.454182+00
5114bd0d-ce60-4a2d-9a5e-e7d2b6b2af47	15b0c4c2-bc3f-4428-a448-bc92e937038f	ORD-260625-111	Sama Clinic	SME	Sales Desk	Khobar	Industrial Edge	InTransit	Normal	3	922.50	RT-260625-42	Khalid Al-Harbi	VAN-84	Smoke test attempt	2026-06-25 13:44:38.541367+00	2026-06-25 13:44:38.541365+00	\N	\N	2026-06-25 10:49:01.734999+00
\.


--
-- Data for Name: driver_tasks; Type: TABLE DATA; Schema: public; Owner: neondb_owner
--

COPY public.driver_tasks (id, tenant_id, shipment_id, stop_id, task_type, title, description, status, driver_name, vehicle_number, due_at_utc, completed_at_utc, notes, created_at_utc, updated_at_utc) FROM stdin;
21e16adc-077a-44ca-a926-6dd624a1dfc3	15b0c4c2-bc3f-4428-a448-bc92e937038f	924bfa7f-2fb5-4dc1-be16-4b9959807917	1a0f4c66-a563-4d3f-bce7-699e19066fe3	Pickup	Pickup SHP-260625-202	Collect freight from Dammam Hub.	Completed	Fatimah Al-Shehri	FLEET-260625-32	2026-06-25 13:44:52.055719+00	\N	Driver task seeded.	2026-06-24 10:44:52.055719+00	2026-06-25 10:44:52.056262+00
3b07d09d-663c-4a20-9bd6-ad568297322e	15b0c4c2-bc3f-4428-a448-bc92e937038f	dced6032-cde6-4113-93a5-06deb07bfd3e	4c5087b9-72c0-456f-87b0-656266ec1dc1	Pickup	Pickup SHP-260625-201	Collect freight from Jeddah Gateway.	Completed	Mohammed Al-Otaibi	FLEET-260625-22	2026-06-25 13:14:52.055704+00	\N	Driver task seeded.	2026-06-24 10:44:52.055705+00	2026-06-25 10:44:52.056262+00
fd211b72-0d50-4182-b43b-eb78357f9fb5	15b0c4c2-bc3f-4428-a448-bc92e937038f	c08ddedf-9e57-4863-b53b-c8c2babc904e	54b40c9b-0df3-4687-92b1-12173c6e5eef	Pickup	Pickup SHP-260625-200	Collect freight from Riyadh DC.	Completed	Sara Al-Dossari	FLEET-260625-12	2026-06-25 12:44:52.055691+00	\N	Driver task seeded.	2026-06-24 10:44:52.055691+00	2026-06-25 10:44:52.056262+00
\.


--
-- Data for Name: fleet_fuel_events; Type: TABLE DATA; Schema: public; Owner: neondb_owner
--

COPY public.fleet_fuel_events (id, tenant_id, vehicle_number, fuel_card_number, station_name, city, event_type, anomaly_flag, liters, cost, odometer_km, notes, recorded_at_utc, updated_at_utc) FROM stdin;
1db5a80d-0b1a-4ab3-90a4-645b7882a7a7	15b0c4c2-bc3f-4428-a448-bc92e937038f	FLEET-260625-12	CARD-260625-706	Aramco Station	Dammam	Fuel	f	124.40	679.00	56900.00	Normal refuel event.	2026-06-25 02:44:51.655331+00	2026-06-25 10:44:52.056262+00
2bb1526f-e9b0-48c5-9603-54acd450bacf	15b0c4c2-bc3f-4428-a448-bc92e937038f	FLEET-260625-32	CARD-260625-702	Aramco Station	Dammam	Fuel	f	90.80	413.00	49300.00	Normal refuel event.	2026-06-24 18:44:51.65532+00	2026-06-25 10:44:52.056262+00
46581304-6d7f-4d16-b11a-229e3372d295	15b0c4c2-bc3f-4428-a448-bc92e937038f	FLEET-260625-42	CARD-260625-703	Shell Express	Khobar	Fuel	f	99.20	479.50	51200.00	Normal refuel event.	2026-06-24 20:44:51.655323+00	2026-06-25 10:44:52.056262+00
62a5edbe-9ec4-4ada-8504-da82468b726b	15b0c4c2-bc3f-4428-a448-bc92e937038f	FLEET-260625-52	CARD-260625-704	Petromin	Riyadh	Fuel	f	107.60	546.00	53100.00	Normal refuel event.	2026-06-24 22:44:51.655326+00	2026-06-25 10:44:52.056262+00
bbf36293-521a-4361-b0d6-05fa733c2aaa	15b0c4c2-bc3f-4428-a448-bc92e937038f	FLEET-260625-22	CARD-260625-701	Alyusr	Jeddah	Fuel	f	82.40	346.50	47400.00	Normal refuel event.	2026-06-24 16:44:51.655318+00	2026-06-25 10:44:52.056262+00
bd813b6f-007c-4b10-9de4-367e22e70afd	15b0c4c2-bc3f-4428-a448-bc92e937038f	FLEET-260625-12	CARD-260625-700	Petromin	Riyadh	Fuel	t	74.00	280.00	45500.00	High-volume fill flagged for review.	2026-06-24 14:44:51.655315+00	2026-06-25 10:44:52.056262+00
e3fd580e-8fbe-4d12-9ca9-20b9bd35d6e8	15b0c4c2-bc3f-4428-a448-bc92e937038f	FLEET-260625-62	CARD-260625-705	Alyusr	Jeddah	Fuel	t	116.00	612.50	55000.00	High-volume fill flagged for review.	2026-06-25 00:44:51.655329+00	2026-06-25 10:44:52.056262+00
2cf71cdb-d406-4a74-8363-f2e03a69b7da	15b0c4c2-bc3f-4428-a448-bc92e937038f	FLEET-260625-22	CARD-260625-707	Shell Express	Khobar	Fuel	t	132.80	745.50	58800.00	Smoke fuel review flag	2026-06-25 04:44:51.655334+00	2026-06-25 10:49:03.474846+00
\.


--
-- Data for Name: fleet_maintenance_tickets; Type: TABLE DATA; Schema: public; Owner: neondb_owner
--

COPY public.fleet_maintenance_tickets (id, tenant_id, work_order_number, vehicle_number, type, status, priority, vendor_name, description, estimated_cost, actual_cost, downtime_hours, opened_at_utc, due_at_utc, closed_at_utc, notes, updated_at_utc) FROM stdin;
422dd30b-0331-4c39-afe3-793b0ca31992	15b0c4c2-bc3f-4428-a448-bc92e937038f	WO-260625-502	FLEET-260625-32	Tyre Replacement	AwaitingParts	High	National Workshop	Preventive maintenance task logged from operational review.	780.00	0.00	6.00	2026-06-21 10:44:51.655308+00	2026-06-28 10:44:51.655308+00	\N	Service aligned with route demand.	2026-06-25 10:44:52.056262+00
5e7b64d1-ad7a-4193-9373-27e4e555ca78	15b0c4c2-bc3f-4428-a448-bc92e937038f	WO-260625-501	FLEET-260625-22	Brake Check	InProgress	Normal	FleetCare	Preventive maintenance task logged from operational review.	600.00	0.00	5.00	2026-06-22 10:44:51.655306+00	2026-06-27 10:44:51.655306+00	\N	Service aligned with route demand.	2026-06-25 10:44:52.056262+00
7f3354ab-eb56-47f3-b802-980e6d297527	15b0c4c2-bc3f-4428-a448-bc92e937038f	WO-260625-503	FLEET-260625-42	Cooling Unit	Open	Normal	FleetCare	Preventive maintenance task logged from operational review.	960.00	0.00	7.00	2026-06-20 10:44:51.655311+00	2026-06-29 10:44:51.655311+00	\N	Service aligned with route demand.	2026-06-25 10:44:52.056262+00
fc14c25b-128d-44f7-8bbc-d6754c1330c7	15b0c4c2-bc3f-4428-a448-bc92e937038f	WO-260625-500	FLEET-260625-12	Service	Closed	High	National Workshop	Preventive maintenance task logged from operational review.	420.00	420.00	4.00	2026-06-23 10:44:51.655303+00	2026-06-26 10:44:51.655303+00	2026-06-25 10:49:03.075878+00	Smoke close action	2026-06-25 10:49:03.075968+00
\.


--
-- Data for Name: fleet_readiness_documents; Type: TABLE DATA; Schema: public; Owner: neondb_owner
--

COPY public.fleet_readiness_documents (id, tenant_id, kind, subject_type, subject_id, subject_name, document_type, document_number, transport_document_no, permit_no, v_a_t_number, commercial_registration_no, country_code, national_address_building_no, national_address_additional_no, district, city, region, postal_code, document_status, expiry_status, issue_date, hijri_expiry_date, gregorian_expiry_date, notes, created_at_utc, updated_at_utc) FROM stdin;
255433ea-a2e7-47b3-885a-fae241bbef3f	15b0c4c2-bc3f-4428-a448-bc92e937038f	Compliance	Warehouse	rasalmanar-warehouse-east	RASALMANAR Eastern Warehouse	Warehouse Permit	WH-RASALMANAR-044			315b0c4c2bc3	715b0c4c2b	SA	12	7	Al Khalidiyah	Dammam	Eastern Province	31411	Active	ExpiringSoon	2025-06-25	\N	2026-07-12	Warehouse permit used for GCC-ready logistics staging.	2026-06-25 10:44:52.512675+00	2026-06-25 10:44:52.512854+00
3fcac59d-6cde-4115-976f-68a567478900	15b0c4c2-bc3f-4428-a448-bc92e937038f	Driver	Driver	67	Sara Al-Dossari	Work Permit	WP-RASALMANAR-EMP002		MOL-EMP002							Jeddah	Makkah	21411	Active	Healthy	2025-10-25	\N	2026-12-25	Driver work permit readiness example.	2026-06-25 10:44:52.512686+00	2026-06-25 10:44:52.512854+00
633fe038-1b88-4d5b-b73c-98357e4ad64a	15b0c4c2-bc3f-4428-a448-bc92e937038f	Transport	Carrier	carrier-rapid-freight	Rapid Freight	Transport Permit	TRN-RASALMANAR-118	TD-RASALMANAR-9001	PERM-RASALMANAR-522			SA	205	19	Industrial City	Jeddah	Makkah	21432	Active	Healthy	2026-02-25	\N	2027-02-25	Transport document ready for cross-border and domestic movement.	2026-06-25 10:44:52.512672+00	2026-06-25 10:44:52.512854+00
731a9b5f-05db-4640-9688-25d480432cf9	15b0c4c2-bc3f-4428-a448-bc92e937038f	Driver	Driver	66	Abdulrahman Al-Qahtani	Driver License	DL-RASALMANAR-EMP001		IQAMA-EMP001							Riyadh	Riyadh	12211	Active	Healthy	2024-06-25	\N	2027-08-25	Driver permit and licence readiness record.	2026-06-25 10:44:52.512679+00	2026-06-25 10:44:52.512854+00
c9676103-8d39-4fd5-9d8a-a5f99d23dc3d	15b0c4c2-bc3f-4428-a448-bc92e937038f	Compliance	Branch	rasalmanar-riyadh-hq	RASALMANAR Riyadh HQ	Commercial Registration	CR-RASALMANAR-001			315b0c4c2bc3	715b0c4c2b	SA	7882	1345	Olaya	Riyadh	Riyadh	12211	Active	Healthy	2025-06-25	\N	2027-04-25	Saudi/GCC readiness foundation record for head office compliance.	2026-06-25 10:44:52.51266+00	2026-06-25 10:44:52.512854+00
f6aef72d-792d-4e84-bd1d-2cdf089c90c3	15b0c4c2-bc3f-4428-a448-bc92e937038f	Driver	Driver	68	Mohammed Al-Otaibi	GCC Permit	GCC-RASALMANAR-EMP003		GCCP-EMP003							Dammam	Eastern Province	31411	Active	ExpiringSoon	2025-08-25	\N	2026-07-19	GCC movement permit example.	2026-06-25 10:44:52.512689+00	2026-06-25 10:44:52.512854+00
\.


--
-- Data for Name: fleet_shipments; Type: TABLE DATA; Schema: public; Owner: neondb_owner
--

COPY public.fleet_shipments (id, tenant_id, shipment_number, customer_name, customer_segment, origin, destination, city, status, priority, mode, piece_count, weight_kg, volume_cbm, declared_value, carrier_name, customer_v_a_t_number, customer_commercial_registration_no, customer_national_address_building_no, customer_national_address_additional_no, customer_national_address_district, customer_national_address_city, customer_national_address_region, customer_national_address_postal_code, customer_national_address_country, driver_name, vehicle_number, route_code, pod_status, temperature_range, notes, is_invoice_ready, invoice_ready_at_utc, invoice_readiness_notes, created_at_utc, pickup_scheduled_at_utc, picked_up_at_utc, delivered_at_utc, updated_at_utc) FROM stdin;
1014ac00-4c4f-4a92-ad82-0c60be902ffb	15b0c4c2-bc3f-4428-a448-bc92e937038f	SHP-260625-210	Almarai Distribution	Retail	Dammam Hub	Eastern Province	Dammam	Delivered	Normal	Road	4	605.00	4.35	7920.00	Kynex Logistics	315b0c4c2bc3	715b0c4c2b	310	510	Olaya	Dammam	Eastern Province	12211	Saudi Arabia	Noura Al-Mutairi	FLEET-260625-52	RT-260625-32	Signature		Ready for route assignment	f	\N		2026-06-25 08:44:51.65529+00	2026-06-25 11:44:51.65529+00	2026-06-25 09:44:51.65529+00	2026-06-25 10:44:51.65529+00	2026-06-25 10:44:51.655585+00
3749b6ba-9245-4c2c-b63f-35949beb4ff9	15b0c4c2-bc3f-4428-a448-bc92e937038f	SHP-260625-208	Gulf Pharma	Pharma	Riyadh DC	Central Riyadh	Riyadh	PickedUp	High	Refrigerated	7	508.00	3.65	6696.00	Kynex Logistics	315b0c4c2bc3	715b0c4c2b	308	58	Olaya	Riyadh	Riyadh	12211	Saudi Arabia	Fatimah Al-Shehri	FLEET-260625-32	RT-260625-12	Pending	2-8C	Ready for route assignment	f	\N		2026-06-25 06:44:51.655275+00	2026-06-25 14:44:51.655275+00	2026-06-25 09:20:51.655275+00	\N	2026-06-25 10:44:51.655585+00
3a706db4-b44f-4be7-a221-cfc37b486f2f	15b0c4c2-bc3f-4428-a448-bc92e937038f	SHP-260625-207	Al Rajhi Supply	Retail	Khobar Crossdock	Western Retail Loop	Khobar	Loaded	Normal	Road	6	459.50	3.30	6084.00	Kynex Logistics	315b0c4c2bc3	715b0c4c2b	307	57	Al Hamra	Khobar	Eastern Province	21411	Saudi Arabia	Mohammed Al-Otaibi	FLEET-260625-22	RT-260625-42	Pending		Ready for route assignment	f	\N		2026-06-25 05:44:51.655268+00	2026-06-25 13:44:51.655269+00	\N	\N	2026-06-25 10:44:51.655585+00
924bfa7f-2fb5-4dc1-be16-4b9959807917	15b0c4c2-bc3f-4428-a448-bc92e937038f	SHP-260625-202	Tamimi Markets	Pharma	Dammam Hub	Eastern Province	Dammam	InTransit	Normal	Road	6	217.00	1.55	3024.00	Kynex Logistics	315b0c4c2bc3	715b0c4c2b	302	52	Olaya	Dammam	Eastern Province	12211	Saudi Arabia	Fatimah Al-Shehri	FLEET-260625-32	RT-260625-32	Pending		Ready for route assignment	f	\N		2026-06-25 00:44:51.65523+00	2026-06-25 13:44:51.65523+00	2026-06-25 08:08:51.655231+00	\N	2026-06-25 10:44:51.655585+00
ac8a9e59-0af9-4042-9fe3-515ae997bfa8	15b0c4c2-bc3f-4428-a448-bc92e937038f	SHP-260625-205	Noon Fulfilment	Pharma	Jeddah Gateway	North Jeddah	Jeddah	Booked	Normal	Road	4	362.50	2.60	4860.00	Kynex Logistics	315b0c4c2bc3	715b0c4c2b	305	55	Al Hamra	Jeddah	Makkah	21411	Saudi Arabia	Abdullah Al-Ghamdi	FLEET-260625-62	RT-260625-22	Pending		Ready for route assignment	f	\N		2026-06-25 03:44:51.655255+00	2026-06-25 11:44:51.655255+00	\N	\N	2026-06-25 10:44:51.655585+00
cab3578e-644d-4a79-ad26-e08141973158	15b0c4c2-bc3f-4428-a448-bc92e937038f	SHP-260625-204	Sultan Foods	Retail	Riyadh DC	Central Riyadh	Riyadh	Exception	High	Refrigerated	8	314.00	2.25	4248.00	Kynex Logistics	315b0c4c2bc3	715b0c4c2b	304	54	Olaya	Riyadh	Riyadh	12211	Saudi Arabia	Noura Al-Mutairi	FLEET-260625-52	RT-260625-12	Pending	2-8C	Rescheduled due to customer unavailable	f	\N		2026-06-25 02:44:51.655246+00	2026-06-25 15:44:51.655246+00	\N	\N	2026-06-25 10:44:51.655585+00
e1d87a11-f67f-4ed4-8ce8-581116ccfd68	15b0c4c2-bc3f-4428-a448-bc92e937038f	SHP-260625-209	Misk Essentials	Enterprise	Jeddah Gateway	North Jeddah	Jeddah	InTransit	Normal	Road	8	556.50	4.00	7308.00	Kynex Logistics	315b0c4c2bc3	715b0c4c2b	309	59	Al Hamra	Jeddah	Makkah	21411	Saudi Arabia	Khalid Al-Harbi	FLEET-260625-42	RT-260625-22	Pending		Ready for route assignment	f	\N		2026-06-25 07:44:51.655283+00	2026-06-25 15:44:51.655283+00	2026-06-25 09:32:51.655283+00	\N	2026-06-25 10:44:51.655585+00
ea15de64-9b9f-4bc6-b71d-73e5359dc1c8	15b0c4c2-bc3f-4428-a448-bc92e937038f	SHP-260625-203	Jarir Trade	Enterprise	Khobar Crossdock	Western Retail Loop	Khobar	Delivered	Normal	Road	7	265.50	1.90	3636.00	Kynex Logistics	315b0c4c2bc3	715b0c4c2b	303	53	Al Hamra	Khobar	Eastern Province	21411	Saudi Arabia	Khalid Al-Harbi	FLEET-260625-42	RT-260625-42	Signature		Ready for route assignment	f	\N		2026-06-25 01:44:51.655239+00	2026-06-25 14:44:51.655239+00	2026-06-25 08:20:51.655239+00	2026-06-25 10:02:51.655239+00	2026-06-25 10:44:51.655585+00
fa2623c1-f506-448d-a41b-549944b275d3	15b0c4c2-bc3f-4428-a448-bc92e937038f	SHP-260625-206	BinDawood Retail	Enterprise	Dammam Hub	Eastern Province	Dammam	Planned	Critical	Road	5	411.00	2.95	5472.00	Kynex Logistics	315b0c4c2bc3	715b0c4c2b	306	56	Olaya	Dammam	Eastern Province	12211	Saudi Arabia	Sara Al-Dossari	FLEET-260625-12	RT-260625-32	Pending		Ready for route assignment	f	\N		2026-06-25 04:44:51.655262+00	2026-06-25 12:44:51.655262+00	\N	\N	2026-06-25 10:44:51.655585+00
c08ddedf-9e57-4863-b53b-c8c2babc904e	15b0c4c2-bc3f-4428-a448-bc92e937038f	SHP-260625-200	Almarai Distribution	Enterprise	Riyadh DC	Central Riyadh	Riyadh	Loaded	High	Refrigerated	4	120.00	0.85	1800.00	Kynex Logistics	315b0c4c2bc3	715b0c4c2b	500	80	Business District	Riyadh	Riyadh	12211	Saudi Arabia	Sara Al-Dossari	FLEET-260625-12	RT-260625-12	Pending	2-8C	Ready for route assignment	t	2026-06-25 08:44:52.51283+00	VAT and CR details are populated for invoice export.	2026-06-24 22:44:51.655203+00	2026-06-25 11:44:51.655205+00	\N	\N	2026-06-25 10:44:52.512854+00
b51217b1-ba3f-46d4-8816-1b08a6018cbe	15b0c4c2-bc3f-4428-a448-bc92e937038f	SHP-260625-211	Nahdi Logistics	Pharma	Khobar Crossdock	Western Retail Loop	Khobar	InTransit	Normal	Road	5	653.50	4.70	8532.00	Desert Haul	315b0c4c2bc3	715b0c4c2b	311	511	Al Hamra	Khobar	Eastern Province	21411	Saudi Arabia	Abdullah Al-Ghamdi	FLEET-260625-62	RT-260625-42	Pending		Smoke dispatch action	f	\N		2026-06-25 09:44:51.655296+00	2026-06-25 12:44:51.655296+00	2026-06-25 10:49:02.148859+00	\N	2026-06-25 10:49:05.07392+00
dced6032-cde6-4113-93a5-06deb07bfd3e	15b0c4c2-bc3f-4428-a448-bc92e937038f	SHP-260625-201	Nahdi Logistics	Retail	Jeddah Gateway	North Jeddah	Jeddah	PickedUp	Normal	Road	5	168.50	1.20	2412.00	Kynex Logistics	315b0c4c2bc3	715b0c4c2b	501	81	Business District	Jeddah	Makkah	21411	Saudi Arabia	Mohammed Al-Otaibi	FLEET-260625-22	RT-260625-22	Pending		Ready for route assignment	t	2026-06-25 07:44:52.512834+00	VAT and CR details are populated for invoice export.	2026-06-24 23:44:51.655223+00	2026-06-25 12:44:51.655224+00	2026-06-25 07:56:51.655224+00	\N	2026-06-25 10:44:52.512854+00
\.


--
-- Data for Name: fleet_tracking_points; Type: TABLE DATA; Schema: public; Owner: neondb_owner
--

COPY public.fleet_tracking_points (id, tenant_id, shipment_number, vehicle_number, location_label, status, geofence_name, alert_type, latitude, longitude, speed_kph, recorded_at_utc, estimated_arrival_utc, notes) FROM stdin;
0c6e23f8-57a4-4873-8b58-99184b5d1545	15b0c4c2-bc3f-4428-a448-bc92e937038f	SHP-260625-207	FLEET-260625-22	Khobar Corridor	InTransit	Khobar Service Area		24.9201716	46.4655966	38.60	2026-06-25 10:20:51.655271+00	2026-06-25 14:14:51.655271+00	Live GPS ping captured from delivery unit.
4752e62f-59c5-42ea-88fa-7b5ccd3bf3e8	15b0c4c2-bc3f-4428-a448-bc92e937038f	SHP-260625-200	FLEET-260625-12	Riyadh Corridor	InTransit	Riyadh Service Area		25.2811267	46.4507065	46.80	2026-06-25 09:59:51.655217+00	2026-06-25 12:44:51.655217+00	Live GPS ping captured from delivery unit.
56712a59-bd3d-4ebd-9e9e-d31fefa069b0	15b0c4c2-bc3f-4428-a448-bc92e937038f	SHP-260625-202	FLEET-260625-32	Dammam Corridor	InTransit	Dammam Service Area		24.8113816	47.3855300	53.20	2026-06-25 10:05:51.655233+00	2026-06-25 13:44:51.655233+00	Live GPS ping captured from delivery unit.
61288945-e410-45b4-beff-7f33eaaffd43	15b0c4c2-bc3f-4428-a448-bc92e937038f	SHP-260625-205	FLEET-260625-62	Jeddah Corridor	InTransit	Jeddah Service Area		25.0881559	47.1022863	44.10	2026-06-25 10:14:51.655258+00	2026-06-25 13:14:51.655258+00	Live GPS ping captured from delivery unit.
68859cae-0b19-4a2d-98fc-3a34ca0272d8	15b0c4c2-bc3f-4428-a448-bc92e937038f	SHP-260625-201	FLEET-260625-22	Jeddah Corridor	InTransit	Jeddah Service Area		25.2942625	47.6837476	46.80	2026-06-25 10:02:51.655226+00	2026-06-25 13:14:51.655226+00	Live GPS ping captured from delivery unit.
6c047e90-fdc2-4030-a11f-4970f71deeda	15b0c4c2-bc3f-4428-a448-bc92e937038f	SHP-260625-206	FLEET-260625-12	Dammam Corridor	InTransit	Dammam Service Area		25.4385951	47.5719793	58.10	2026-06-25 10:17:51.655264+00	2026-06-25 13:44:51.655264+00	Live GPS ping captured from delivery unit.
71f145f1-06c8-4fb0-a704-0c59687799ca	15b0c4c2-bc3f-4428-a448-bc92e937038f	SHP-260625-203	FLEET-260625-42	Khobar Corridor	Delivered	Khobar Service Area		24.9203345	47.6506738	0.00	2026-06-25 10:08:51.655242+00	2026-06-25 14:14:51.655242+00	Live GPS ping captured from delivery unit.
803d900c-cdae-4edd-8438-0f70c2ecde63	15b0c4c2-bc3f-4428-a448-bc92e937038f	SHP-260625-204	FLEET-260625-52	Riyadh Corridor	Stopped	Riyadh Service Area	DelayRisk	24.5602214	46.5779622	58.80	2026-06-25 10:11:51.65525+00	2026-06-25 12:44:51.65525+00	Live GPS ping captured from delivery unit.
902333c6-f979-4a58-a6ad-9a4e89414379	15b0c4c2-bc3f-4428-a448-bc92e937038f	SHP-260625-211	FLEET-260625-62	Khobar Corridor	Stopped	Khobar Service Area	DelayRisk	25.4910460	47.5150396	52.00	2026-06-25 10:32:51.655299+00	2026-06-25 14:14:51.655299+00	Live GPS ping captured from delivery unit.
ad39d931-7531-41b4-a7a8-19c15a93a49b	15b0c4c2-bc3f-4428-a448-bc92e937038f	SHP-260625-208	FLEET-260625-32	Riyadh Corridor	InTransit	Riyadh Service Area		24.5772120	47.1926366	53.20	2026-06-25 10:23:51.655277+00	2026-06-25 12:44:51.655278+00	Live GPS ping captured from delivery unit.
bfee9592-99b9-4dd2-ac29-ef2fce295ddf	15b0c4c2-bc3f-4428-a448-bc92e937038f	SHP-260625-210	FLEET-260625-52	Dammam Corridor	Delivered	Dammam Service Area		25.0550547	47.4997066	0.00	2026-06-25 10:29:51.655292+00	2026-06-25 13:44:51.655292+00	Live GPS ping captured from delivery unit.
d717fc05-3514-45b2-bd11-01047802c36c	15b0c4c2-bc3f-4428-a448-bc92e937038f	SHP-260625-209	FLEET-260625-42	Jeddah Corridor	InTransit	Jeddah Service Area		24.7063526	46.7254526	45.10	2026-06-25 10:26:51.655285+00	2026-06-25 13:14:51.655285+00	Live GPS ping captured from delivery unit.
e33ff8f4-b3d9-4c37-9af2-a62d3846fe5e	15b0c4c2-bc3f-4428-a448-bc92e937038f	SHP-260625-211	FLEET-260625-62	Khobar Crossdock	PickedUp	Origin Hub		24.7136000	46.6753000	0.00	2026-06-25 10:49:02.232542+00	2026-06-25 13:49:02.232542+00	Shipment dispatched from the command center.
\.


--
-- Data for Name: fleet_vehicles; Type: TABLE DATA; Schema: public; Owner: neondb_owner
--

COPY public.fleet_vehicles (id, tenant_id, vehicle_number, plate_number, type, status, driver_name, capacity_kg, capacity_cbm, current_load_kg, fuel_level_percent, odometer_km, health_status, is_refrigerated, temperature_celsius, last_known_location, last_ping_at_utc, last_service_at_utc, next_service_at_utc, notes, created_at_utc, updated_at_utc) FROM stdin;
3aedbd4c-c1c8-4258-83fe-1829cc594e67	15b0c4c2-bc3f-4428-a448-bc92e937038f	FLEET-260625-12	KSA-6000	Van	Maintenance	Sara Al-Dossari	1200.00	6.50	280.00	34.00	45210.00	NeedsService	t	4.00	Riyadh DC Yard	2026-06-25 10:24:51.354025+00	2026-06-07 10:44:51.354026+00	2026-07-05 10:44:51.354027+00	Cold-chain capable	2026-06-25 00:44:51.354027+00	2026-06-25 10:44:51.354164+00
833d79bb-8fbc-4605-bb86-4cca60e976c2	15b0c4c2-bc3f-4428-a448-bc92e937038f	FLEET-260625-52	KSA-6068	Van	Maintenance	Noura Al-Mutairi	2480.00	12.10	880.00	66.00	54666.00	NeedsService	t	6.00	Riyadh DC Yard	2026-06-25 09:56:51.354045+00	2026-05-26 10:44:51.354045+00	2026-07-29 10:44:51.354045+00	Cold-chain capable	2026-06-25 00:44:51.354045+00	2026-06-25 10:44:51.354164+00
8674c171-5c0f-43fd-82b5-3445a362f026	15b0c4c2-bc3f-4428-a448-bc92e937038f	FLEET-260625-32	KSA-6034	Refrigerated Truck	Available	Fatimah Al-Shehri	1840.00	9.30	580.00	50.00	49938.00	Healthy	t	5.00	Dammam Hub Yard	2026-06-25 10:10:51.354037+00	2026-06-01 10:44:51.354038+00	2026-07-17 10:44:51.354038+00	Cold-chain capable	2026-06-25 00:44:51.354038+00	2026-06-25 10:44:51.354164+00
f7058eb8-5528-4cd3-82c5-6b623f1e7974	15b0c4c2-bc3f-4428-a448-bc92e937038f	FLEET-260625-42	KSA-6051	Trailer	OnTrip	Khalid Al-Harbi	2160.00	10.70	730.00	58.00	52302.00	Healthy	f	\N	Khobar Crossdock Yard	2026-06-25 10:03:51.354041+00	2026-05-29 10:44:51.354042+00	2026-07-23 10:44:51.354042+00	General freight unit	2026-06-25 00:44:51.354042+00	2026-06-25 10:44:51.354164+00
c019697d-05c6-4123-8a92-2e37f61de78c	15b0c4c2-bc3f-4428-a448-bc92e937038f	FLEET-260625-62	KSA-6085	Box Truck	OnTrip	Abdullah Al-Ghamdi	2800.00	13.50	1683.50	74.00	57030.00	Healthy	f	\N	Khobar Crossdock	2026-06-25 10:49:02.232494+00	2026-05-23 10:44:51.354051+00	2026-08-04 10:44:51.354051+00	General freight unit	2026-06-25 00:44:51.354051+00	2026-06-25 10:49:02.232667+00
085dcb6f-bc00-49b6-9153-7b6058838db3	15b0c4c2-bc3f-4428-a448-bc92e937038f	FLEET-260625-22	KSA-6017	Box Truck	Maintenance	Mohammed Al-Otaibi	1520.00	7.90	430.00	42.00	47574.00	Reviewing	f	\N	Jeddah Gateway Yard	2026-06-25 10:17:51.354033+00	2026-06-25 10:49:02.655595+00	2026-07-11 10:44:51.354033+00	Smoke service action	2026-06-25 00:44:51.354033+00	2026-06-25 10:49:02.65567+00
\.


--
-- Data for Name: last_mile_stops; Type: TABLE DATA; Schema: public; Owner: neondb_owner
--

COPY public.last_mile_stops (id, tenant_id, order_number, route_code, customer_name, address_line, city, region, postal_code, country, saudi_national_address_building_no, saudi_national_address_additional_no, saudi_national_address_district, status, proof_status, recipient_name, attempt_count, rider_name, time_window, eta_utc, delivered_at_utc, exception_reason, created_at_utc, updated_at_utc) FROM stdin;
1a63d743-40c4-4a5f-bd0d-290eaf331de7	15b0c4c2-bc3f-4428-a448-bc92e937038f	ORD-260625-104	RT-260625-12	ByteWave Store	104 Riyadh Business Road	Riyadh	Riyadh	12211	Saudi Arabia	104	24	Business District	Attempted	None		2	Sara Al-Dossari	10:00-13:00	2026-06-25 12:44:38.541326+00	\N	Customer requested reschedule	2026-06-25 06:44:38.541326+00	2026-06-25 10:44:38.541532+00
3c1da0d1-874e-41aa-9be3-19cd62986324	15b0c4c2-bc3f-4428-a448-bc92e937038f	ORD-260625-105	RT-260625-22	Capital Coffee	105 Jeddah Business Road	Jeddah	Makkah	21411	Saudi Arabia	105	25	Logistics Zone	OutForDelivery	None		0	Mohammed Al-Otaibi	14:00-18:00	2026-06-25 12:59:38.54133+00	\N		2026-06-25 07:14:38.54133+00	2026-06-25 10:44:38.541532+00
465939bd-9e5b-4c45-9a9b-5543425742ee	15b0c4c2-bc3f-4428-a448-bc92e937038f	ORD-260625-107	RT-260625-42	Nova Supplies	107 Khobar Business Road	Khobar	Eastern Province	31411	Saudi Arabia	107	27	Logistics Zone	OutForDelivery	None		0	Khalid Al-Harbi	14:00-18:00	2026-06-25 13:29:38.541345+00	\N		2026-06-25 08:14:38.541345+00	2026-06-25 10:44:38.541532+00
4a2117fb-f55b-48a3-8a38-1ea3628e05f1	15b0c4c2-bc3f-4428-a448-bc92e937038f	ORD-260625-109	RT-260625-22	Urban Fit	109 Jeddah Business Road	Jeddah	Makkah	21411	Saudi Arabia	109	29	Logistics Zone	OutForDelivery	None		0	Mohammed Al-Otaibi	14:00-18:00	2026-06-25 13:59:38.541361+00	\N		2026-06-25 09:14:38.541361+00	2026-06-25 10:44:38.541532+00
4a26d6bc-7d72-4bf0-a112-c470a385c5ba	15b0c4c2-bc3f-4428-a448-bc92e937038f	ORD-260625-106	RT-260625-32	Misk Home	106 Dammam Business Road	Dammam	Eastern Province	31411	Saudi Arabia	106	26	Business District	OutForDelivery	None		0	Fatimah Al-Shehri	10:00-13:00	2026-06-25 13:14:38.541335+00	\N		2026-06-25 07:44:38.541335+00	2026-06-25 10:44:38.541532+00
91d7c938-a672-4453-ada0-e848ac19ee47	15b0c4c2-bc3f-4428-a448-bc92e937038f	ORD-260625-102	RT-260625-32	Zenith Traders	102 Dammam Business Road	Dammam	Eastern Province	31411	Saudi Arabia	102	22	Business District	OutForDelivery	None		0	Fatimah Al-Shehri	10:00-13:00	2026-06-25 12:14:38.541312+00	\N		2026-06-25 05:44:38.541312+00	2026-06-25 10:44:38.541532+00
ce67d475-807f-4473-94bb-65be8f71ad84	15b0c4c2-bc3f-4428-a448-bc92e937038f	ORD-260625-100	RT-260625-12	Al Noor Pharmacy	100 Riyadh Business Road	Riyadh	Riyadh	12211	Saudi Arabia	100	20	Business District	OutForDelivery	None		0	Sara Al-Dossari	10:00-13:00	2026-06-25 11:44:38.541302+00	\N		2026-06-25 04:44:38.541303+00	2026-06-25 10:44:38.541532+00
f3c08f9a-d542-4c10-a365-18ddeb284cbe	15b0c4c2-bc3f-4428-a448-bc92e937038f	ORD-260625-103	RT-260625-42	Oasis Mart	103 Khobar Business Road	Khobar	Eastern Province	31411	Saudi Arabia	103	23	Logistics Zone	Delivered	None	Oasis Receiver	1	Khalid Al-Harbi	14:00-18:00	2026-06-25 12:29:38.54132+00	2026-06-25 09:59:38.54132+00		2026-06-25 06:14:38.541321+00	2026-06-25 10:44:38.541532+00
fb46688d-ef8e-46f0-b60f-c11bcea55bce	15b0c4c2-bc3f-4428-a448-bc92e937038f	ORD-260625-101	RT-260625-22	Sama Clinic	101 Jeddah Business Road	Jeddah	Makkah	21411	Saudi Arabia	101	21	Logistics Zone	OutForDelivery	None		0	Mohammed Al-Otaibi	14:00-18:00	2026-06-25 11:59:38.541308+00	\N		2026-06-25 05:14:38.541308+00	2026-06-25 10:44:38.541532+00
fdd6f741-03b6-40ca-9b0b-ab8c0455823e	15b0c4c2-bc3f-4428-a448-bc92e937038f	ORD-260625-110	RT-260625-32	Al Noor Pharmacy	110 Dammam Business Road	Dammam	Eastern Province	31411	Saudi Arabia	110	210	Business District	Delivered	POD	Al Receiver	1	Fatimah Al-Shehri	10:00-13:00	2026-06-25 14:14:38.541365+00	2026-06-25 09:24:38.541365+00		2026-06-25 09:44:38.541365+00	2026-06-25 10:44:38.541532+00
fe6844f3-1348-4604-b8cd-fcc8b0828fe7	15b0c4c2-bc3f-4428-a448-bc92e937038f	ORD-260625-108	RT-260625-12	Royal Beauty	108 Riyadh Business Road	Riyadh	Riyadh	12211	Saudi Arabia	108	28	Business District	OutForDelivery	None		0	Sara Al-Dossari	10:00-13:00	2026-06-25 13:44:38.54135+00	\N		2026-06-25 08:44:38.54135+00	2026-06-25 10:44:38.541532+00
5e043460-93a0-4c52-99fb-6bfbe88d12a4	15b0c4c2-bc3f-4428-a448-bc92e937038f	ORD-260625-111	RT-260625-42	Sama Clinic	111 Khobar Business Road	Khobar	Eastern Province	31411	Saudi Arabia	111	211	Logistics Zone	Attempted	None		3	Khalid Al-Harbi	14:00-18:00	2026-06-26 12:00:00+00	\N	Smoke test attempt	2026-06-25 10:14:38.541368+00	2026-06-25 10:49:01.734999+00
\.


--
-- Data for Name: proofs_of_delivery; Type: TABLE DATA; Schema: public; Owner: neondb_owner
--

COPY public.proofs_of_delivery (id, tenant_id, shipment_id, stop_id, captured_by_user_id, driver_id, vehicle_id, recipient_name, recipient_phone, signature_url, photo_url, document_url, notes, delivery_condition, captured_latitude, captured_longitude, captured_at, verified_at, verified_by_user_id, status, created_at, updated_at) FROM stdin;
2059474a-5fb9-45aa-a86d-555cf6edb7ba	15b0c4c2-bc3f-4428-a448-bc92e937038f	ea15de64-9b9f-4bc6-b71d-73e5359dc1c8	6e8739c2-4b7d-4a71-a637-d64e03f1acad	\N	\N	\N	Jarir Receiver	+966500000002	https://example.com/signature.png	https://example.com/pod-photo.png	https://example.com/pod.pdf	Seed POD verified through customer handoff.	Good	24.7400000	46.7000000	2026-06-25 09:44:52.05573+00	2026-06-25 10:24:52.05573+00	\N	Verified	2026-06-25 09:44:52.055731+00	2026-06-25 10:44:52.055731+00
dee2d449-1cb6-45cf-b3dd-fd39a201d2bb	15b0c4c2-bc3f-4428-a448-bc92e937038f	1014ac00-4c4f-4a92-ad82-0c60be902ffb	812f07e8-921f-45c3-9dcd-d14891bf4de0	\N	\N	\N	Almarai Receiver	+966500000002	https://example.com/signature.png	https://example.com/pod-photo.png	https://example.com/pod.pdf	Seed POD verified through customer handoff.	Good	24.8100000	46.7700000	2026-06-25 09:44:52.055819+00	2026-06-25 10:24:52.055819+00	\N	Verified	2026-06-25 09:44:52.05582+00	2026-06-25 10:44:52.05582+00
\.


--
-- Data for Name: quote_requests; Type: TABLE DATA; Schema: public; Owner: neondb_owner
--

COPY public.quote_requests (id, tenant_id, quote_number, customer_name, origin, destination, status, estimated_amount, margin_pct, requested_at_utc, notes) FROM stdin;
021a4ce7-d79b-40dc-aaf4-04458ce6aa0e	15b0c4c2-bc3f-4428-a448-bc92e937038f	QTE-260625-402	Tamimi Markets	Dammam Hub	Eastern Province	Sent	3360.00	16.00	2026-06-22 10:44:52.055666+00	Seed quote request.
82ec90fd-fd12-416d-b46e-decaaafb22ec	15b0c4c2-bc3f-4428-a448-bc92e937038f	QTE-260625-400	Almarai Distribution	Riyadh DC	Central Riyadh	Draft	2400.00	12.00	2026-06-24 10:44:52.055658+00	Seed quote request.
9665ba8c-6230-4f8c-bc56-ce0b653bec7a	15b0c4c2-bc3f-4428-a448-bc92e937038f	QTE-260625-401	Nahdi Logistics	Jeddah Gateway	North Jeddah	Sent	2880.00	14.00	2026-06-23 10:44:52.055662+00	Seed quote request.
780d9d9a-a2d9-4bb2-a603-66164c305763	15b0c4c2-bc3f-4428-a448-bc92e937038f	SMOKE-QTE-064857	Smoke Customer	Riyadh DC	Dammam	Open	1200.00	18.00	2026-06-25 10:49:04.609327+00	Smoke quote
\.


--
-- Data for Name: refrigeration_unit_health; Type: TABLE DATA; Schema: public; Owner: neondb_owner
--

COPY public.refrigeration_unit_health (id, tenant_id, vehicle_number, unit_serial, status, compressor_hours, last_service_at_utc, next_service_due_at_utc, temperature_deviation_count, notes, created_at_utc, updated_at_utc) FROM stdin;
1f6250eb-cc33-4867-b36d-ec3cfb4051bd	15b0c4c2-bc3f-4428-a448-bc92e937038f	FLEET-260625-12	REF-RASALMANAR-12	ServiceDue	120.00	2026-06-09 10:44:52.684931+00	2026-07-07 10:44:52.684931+00	1	Refrigeration unit seeded for preventive monitoring.	2026-06-25 10:44:52.684931+00	2026-06-25 10:44:53.588973+00
49860bf5-5bc3-4966-9b34-7bbf6605fd4c	15b0c4c2-bc3f-4428-a448-bc92e937038f	FLEET-260625-22	REF-RASALMANAR-22	Healthy	162.00	2026-06-05 10:44:52.684931+00	2026-07-12 10:44:52.684931+00	2	Refrigeration unit seeded for preventive monitoring.	2026-06-25 10:44:52.684931+00	2026-06-25 10:44:53.588973+00
cc88cdb6-7d36-4721-9e88-c3c3ceff1ef0	15b0c4c2-bc3f-4428-a448-bc92e937038f	FLEET-260625-32	REF-RASALMANAR-32	Monitor	204.00	2026-06-01 10:44:52.684931+00	2026-07-17 10:44:52.684931+00	3	Refrigeration unit seeded for preventive monitoring.	2026-06-25 10:44:52.684931+00	2026-06-25 10:44:53.588973+00
\.


--
-- Data for Name: rfid_events; Type: TABLE DATA; Schema: public; Owner: neondb_owner
--

COPY public.rfid_events (id, tenant_id, asset_id, shipment_id, tag_id, reader_id, event_type, status, recorded_at_utc, notes) FROM stdin;
5078bb8a-1aad-4871-b938-4219bc26bec9	15b0c4c2-bc3f-4428-a448-bc92e937038f	611f64e2-552d-4718-b6e8-c5b664faea6b	dced6032-cde6-4113-93a5-06deb07bfd3e	RFID-RASALMANAR-AST-RASALMANAR-701	RDR-RASALMANAR-22	Exit	Captured	2026-06-25 10:13:52.684931+00	RFID gate event seeded for live operations.
c1cd551a-389f-405a-bfae-cbb91c77efde	15b0c4c2-bc3f-4428-a448-bc92e937038f	32496dc1-051c-4ff5-80c4-ea14064ab5d8	924bfa7f-2fb5-4dc1-be16-4b9959807917	RFID-RASALMANAR-AST-RASALMANAR-702	RDR-RASALMANAR-32	Read	Captured	2026-06-25 10:02:52.684931+00	RFID gate event seeded for live operations.
d71be604-f269-4aa3-8f7b-7c06be9fcb50	15b0c4c2-bc3f-4428-a448-bc92e937038f	2d88c0ca-df2d-4397-994b-57088437f482	c08ddedf-9e57-4863-b53b-c8c2babc904e	RFID-RASALMANAR-AST-RASALMANAR-700	RDR-RASALMANAR-12	Read	Captured	2026-06-25 10:24:52.684931+00	RFID gate event seeded for live operations.
\.


--
-- Data for Name: saudi_region_references; Type: TABLE DATA; Schema: public; Owner: neondb_owner
--

COPY public.saudi_region_references (id, code, name_en, name_ar, country_code, cities_json, sort_order, is_gcc_ready, created_at_utc) FROM stdin;
22315525-3ecf-41e2-9953-48ff01b16a77	Madinah	Madinah	المدينة المنورة	SA	["Madinah","Yanbu","AlUla"]	4	t	2026-06-25 10:44:42.098622+00
3a0138d0-6b02-457a-a195-11240098323c	Eastern	Eastern Province	المنطقة الشرقية	SA	["Dammam","Khobar","Dhahran","Jubail"]	3	t	2026-06-25 10:44:42.09862+00
6c5fd8e5-4028-4ea3-ba4a-43ea1d556a44	Riyadh	Riyadh	الرياض	SA	["Riyadh","Diriyah","Al Kharj","Dhurma"]	1	t	2026-06-25 10:44:42.098429+00
a3b73ada-d06d-46a2-89a6-aa500c475f50	Makkah	Makkah	مكة المكرمة	SA	["Jeddah","Makkah","Taif","Rabigh"]	2	t	2026-06-25 10:44:42.098619+00
c2c265f2-13b0-48e4-8510-44cfcf0236b4	Asir	Asir	عسير	SA	["Abha","Khamis Mushait","Bisha"]	5	t	2026-06-25 10:44:42.098623+00
\.


--
-- Data for Name: shipment_carrier_assignments; Type: TABLE DATA; Schema: public; Owner: neondb_owner
--

COPY public.shipment_carrier_assignments (id, tenant_id, shipment_id, carrier_id, status, quoted_amount, agreed_amount, notes, assigned_at_utc, updated_at_utc) FROM stdin;
5b0c545b-72cc-4a1b-b28c-9593e69bffe5	15b0c4c2-bc3f-4428-a448-bc92e937038f	c08ddedf-9e57-4863-b53b-c8c2babc904e	41b672e0-3124-456c-8fe0-6efd2fdfa03b	Assigned	1200.00	1150.00	Assigned from carrier management demo.	2026-06-25 04:44:52.055644+00	2026-06-25 10:44:52.056262+00
da3d3fe0-fb21-4169-a379-c124b8c106c1	15b0c4c2-bc3f-4428-a448-bc92e937038f	dced6032-cde6-4113-93a5-06deb07bfd3e	de0d581c-49a2-44ba-a065-9d82580bfc61	Assigned	1380.00	1320.00	Assigned from carrier management demo.	2026-06-25 03:44:52.055649+00	2026-06-25 10:44:52.056262+00
ecefd967-3534-4778-b969-19743bd2d788	15b0c4c2-bc3f-4428-a448-bc92e937038f	924bfa7f-2fb5-4dc1-be16-4b9959807917	2b38e2b8-af4d-4bbf-b93c-8e224cce9032	Assigned	1560.00	1490.00	Assigned from carrier management demo.	2026-06-25 02:44:52.055653+00	2026-06-25 10:44:52.056262+00
a61c1d7c-357b-4f17-a183-38664832b62f	15b0c4c2-bc3f-4428-a448-bc92e937038f	b51217b1-ba3f-46d4-8816-1b08a6018cbe	de0d581c-49a2-44ba-a065-9d82580bfc61	Assigned	900.00	850.00	Smoke carrier assignment	2026-06-25 10:49:05.073599+00	2026-06-25 10:49:05.07392+00
\.


--
-- Data for Name: shipment_events; Type: TABLE DATA; Schema: public; Owner: neondb_owner
--

COPY public.shipment_events (id, tenant_id, shipment_id, event_type, message, actor_name, occurred_at_utc, visibility, created_at_utc, updated_at_utc) FROM stdin;
0074142c-774a-4df5-9bf7-d8bde81e889b	15b0c4c2-bc3f-4428-a448-bc92e937038f	ea15de64-9b9f-4bc6-b71d-73e5359dc1c8	ShipmentCreated	Shipment created and queued for planning.	Fleet Ops	2026-06-23 10:44:52.055736+00	Public	2026-06-23 10:44:52.055736+00	2026-06-25 10:44:52.056262+00
042065de-e384-49eb-911a-1d484115472e	15b0c4c2-bc3f-4428-a448-bc92e937038f	cab3578e-644d-4a79-ad26-e08141973158	ShipmentPlanned	Shipment planned onto operational route.	Fleet Ops	2026-06-24 10:44:52.055755+00	Public	2026-06-24 10:44:52.055755+00	2026-06-25 10:44:52.056262+00
091451d6-6a52-4468-9424-de6a0eb8d4ee	15b0c4c2-bc3f-4428-a448-bc92e937038f	e1d87a11-f67f-4ed4-8ce8-581116ccfd68	ShipmentCreated	Shipment created and queued for planning.	Fleet Ops	2026-06-23 10:44:52.055811+00	Public	2026-06-23 10:44:52.055811+00	2026-06-25 10:44:52.056262+00
22494756-a2a0-4051-857e-456e42a4320f	15b0c4c2-bc3f-4428-a448-bc92e937038f	924bfa7f-2fb5-4dc1-be16-4b9959807917	ShipmentPlanned	Shipment planned onto operational route.	Fleet Ops	2026-06-24 10:44:52.055717+00	Public	2026-06-24 10:44:52.055717+00	2026-06-25 10:44:52.056262+00
26ccfc76-76aa-49dc-b397-3d89e2221527	15b0c4c2-bc3f-4428-a448-bc92e937038f	b51217b1-ba3f-46d4-8816-1b08a6018cbe	ShipmentPlanned	Shipment planned onto operational route.	Fleet Ops	2026-06-24 10:44:52.055834+00	Public	2026-06-24 10:44:52.055834+00	2026-06-25 10:44:52.056262+00
2c2b4271-cbe2-42a9-b34c-25fe84b2ab17	15b0c4c2-bc3f-4428-a448-bc92e937038f	c08ddedf-9e57-4863-b53b-c8c2babc904e	ShipmentCreated	Shipment created and queued for planning.	Fleet Ops	2026-06-23 10:44:52.055684+00	Public	2026-06-23 10:44:52.055684+00	2026-06-25 10:44:52.056262+00
36349edc-f5ed-459d-9262-b386ce8861e4	15b0c4c2-bc3f-4428-a448-bc92e937038f	1014ac00-4c4f-4a92-ad82-0c60be902ffb	ShipmentPlanned	Shipment planned onto operational route.	Fleet Ops	2026-06-24 10:44:52.055824+00	Public	2026-06-24 10:44:52.055824+00	2026-06-25 10:44:52.056262+00
3df6773a-c956-4284-940d-95ea0de9c702	15b0c4c2-bc3f-4428-a448-bc92e937038f	cab3578e-644d-4a79-ad26-e08141973158	ShipmentCreated	Shipment created and queued for planning.	Fleet Ops	2026-06-23 10:44:52.055753+00	Public	2026-06-23 10:44:52.055753+00	2026-06-25 10:44:52.056262+00
5294b500-4338-4c49-9e24-fa47cbd4ef21	15b0c4c2-bc3f-4428-a448-bc92e937038f	3a706db4-b44f-4be7-a221-cfc37b486f2f	ShipmentPlanned	Shipment planned onto operational route.	Fleet Ops	2026-06-24 10:44:52.055793+00	Public	2026-06-24 10:44:52.055793+00	2026-06-25 10:44:52.056262+00
5588f14c-d2c8-4fb6-94e1-2048e9d75e95	15b0c4c2-bc3f-4428-a448-bc92e937038f	924bfa7f-2fb5-4dc1-be16-4b9959807917	ShipmentCreated	Shipment created and queued for planning.	Fleet Ops	2026-06-23 10:44:52.055716+00	Public	2026-06-23 10:44:52.055716+00	2026-06-25 10:44:52.056262+00
5efd6762-7994-4ed9-a57b-1ce230b961a8	15b0c4c2-bc3f-4428-a448-bc92e937038f	fa2623c1-f506-448d-a41b-549944b275d3	ShipmentPlanned	Shipment planned onto operational route.	Fleet Ops	2026-06-24 10:44:52.05578+00	Public	2026-06-24 10:44:52.05578+00	2026-06-25 10:44:52.056262+00
6168f4e4-6a74-4337-a101-6320e102f8ae	15b0c4c2-bc3f-4428-a448-bc92e937038f	c08ddedf-9e57-4863-b53b-c8c2babc904e	ShipmentPlanned	Shipment planned onto operational route.	Fleet Ops	2026-06-24 10:44:52.055686+00	Public	2026-06-24 10:44:52.055686+00	2026-06-25 10:44:52.056262+00
6a02bbd8-c35e-481b-ba32-0d976468767b	15b0c4c2-bc3f-4428-a448-bc92e937038f	ac8a9e59-0af9-4042-9fe3-515ae997bfa8	ShipmentCreated	Shipment created and queued for planning.	Fleet Ops	2026-06-23 10:44:52.055767+00	Public	2026-06-23 10:44:52.055767+00	2026-06-25 10:44:52.056262+00
75204085-9a97-451e-9076-b51dfe7a2891	15b0c4c2-bc3f-4428-a448-bc92e937038f	fa2623c1-f506-448d-a41b-549944b275d3	ShipmentCreated	Shipment created and queued for planning.	Fleet Ops	2026-06-23 10:44:52.055779+00	Public	2026-06-23 10:44:52.055779+00	2026-06-25 10:44:52.056262+00
838cc811-5271-4fb1-b16f-c06341afdbfe	15b0c4c2-bc3f-4428-a448-bc92e937038f	e1d87a11-f67f-4ed4-8ce8-581116ccfd68	ShipmentPlanned	Shipment planned onto operational route.	Fleet Ops	2026-06-24 10:44:52.055812+00	Public	2026-06-24 10:44:52.055812+00	2026-06-25 10:44:52.056262+00
a36f0c9b-c750-4d7f-b8c2-583c25079684	15b0c4c2-bc3f-4428-a448-bc92e937038f	1014ac00-4c4f-4a92-ad82-0c60be902ffb	ShipmentCreated	Shipment created and queued for planning.	Fleet Ops	2026-06-23 10:44:52.055823+00	Public	2026-06-23 10:44:52.055823+00	2026-06-25 10:44:52.056262+00
a4cf54e6-6b4f-455e-8705-eb43210132e6	15b0c4c2-bc3f-4428-a448-bc92e937038f	ac8a9e59-0af9-4042-9fe3-515ae997bfa8	ShipmentPlanned	Shipment planned onto operational route.	Fleet Ops	2026-06-24 10:44:52.055768+00	Public	2026-06-24 10:44:52.055768+00	2026-06-25 10:44:52.056262+00
bb43615b-07f0-4e58-94c6-c5ce554838d4	15b0c4c2-bc3f-4428-a448-bc92e937038f	ea15de64-9b9f-4bc6-b71d-73e5359dc1c8	ShipmentPlanned	Shipment planned onto operational route.	Fleet Ops	2026-06-24 10:44:52.055739+00	Public	2026-06-24 10:44:52.055739+00	2026-06-25 10:44:52.056262+00
be866222-067e-4e1d-960d-acf55855c7fe	15b0c4c2-bc3f-4428-a448-bc92e937038f	3a706db4-b44f-4be7-a221-cfc37b486f2f	ShipmentCreated	Shipment created and queued for planning.	Fleet Ops	2026-06-23 10:44:52.055792+00	Public	2026-06-23 10:44:52.055792+00	2026-06-25 10:44:52.056262+00
c3ead050-9a2e-4521-8a48-c077cbf3d3e8	15b0c4c2-bc3f-4428-a448-bc92e937038f	3749b6ba-9245-4c2c-b63f-35949beb4ff9	ShipmentCreated	Shipment created and queued for planning.	Fleet Ops	2026-06-23 10:44:52.055802+00	Public	2026-06-23 10:44:52.055802+00	2026-06-25 10:44:52.056262+00
d0262fdc-d4ea-4ff8-bc71-d79fe45bf07f	15b0c4c2-bc3f-4428-a448-bc92e937038f	3749b6ba-9245-4c2c-b63f-35949beb4ff9	ShipmentPlanned	Shipment planned onto operational route.	Fleet Ops	2026-06-24 10:44:52.055803+00	Public	2026-06-24 10:44:52.055803+00	2026-06-25 10:44:52.056262+00
d9252a2a-a100-474e-9605-fab9d15a75f3	15b0c4c2-bc3f-4428-a448-bc92e937038f	b51217b1-ba3f-46d4-8816-1b08a6018cbe	ShipmentCreated	Shipment created and queued for planning.	Fleet Ops	2026-06-23 10:44:52.055833+00	Public	2026-06-23 10:44:52.055833+00	2026-06-25 10:44:52.056262+00
db0ebf4d-4a93-4985-9162-36b27c6bb31c	15b0c4c2-bc3f-4428-a448-bc92e937038f	dced6032-cde6-4113-93a5-06deb07bfd3e	ShipmentPlanned	Shipment planned onto operational route.	Fleet Ops	2026-06-24 10:44:52.055703+00	Public	2026-06-24 10:44:52.055703+00	2026-06-25 10:44:52.056262+00
ee28c29c-2c48-40b1-bf94-1312a55b5d95	15b0c4c2-bc3f-4428-a448-bc92e937038f	dced6032-cde6-4113-93a5-06deb07bfd3e	ShipmentCreated	Shipment created and queued for planning.	Fleet Ops	2026-06-23 10:44:52.055701+00	Public	2026-06-23 10:44:52.055701+00	2026-06-25 10:44:52.056262+00
31ff2805-a0b7-4087-8d37-393a99f0a5bb	15b0c4c2-bc3f-4428-a448-bc92e937038f	b51217b1-ba3f-46d4-8816-1b08a6018cbe	TrackingLinkCreated	Customer tracking link generated.	system	2026-06-25 10:49:03.903751+00	Public	2026-06-25 10:49:03.903751+00	2026-06-25 10:49:03.90381+00
047213d8-40e8-4261-8298-adf35ac5f802	15b0c4c2-bc3f-4428-a448-bc92e937038f	b51217b1-ba3f-46d4-8816-1b08a6018cbe	CarrierAssigned	Carrier Desert Haul assigned to shipment.	system	2026-06-25 10:49:05.073902+00	Private	2026-06-25 10:49:05.073902+00	2026-06-25 10:49:05.07392+00
\.


--
-- Data for Name: shipment_stops; Type: TABLE DATA; Schema: public; Owner: neondb_owner
--

COPY public.shipment_stops (id, tenant_id, shipment_id, stop_type, sequence_no, location_name, contact_name, contact_phone, address_line1, address_line2, city, region, postal_code, country, saudi_national_address_building_no, saudi_national_address_additional_no, saudi_national_address_district, latitude, longitude, planned_arrival_at, actual_arrival_at, completed_at, status, notes, created_at, updated_at) FROM stdin;
055a9b20-3bf4-4bf8-b26b-285c31446b5b	15b0c4c2-bc3f-4428-a448-bc92e937038f	ea15de64-9b9f-4bc6-b71d-73e5359dc1c8	Pickup	1	Khobar Crossdock	Jarir Trade	+966500000000	103 Khobar Crossdock Street		Khobar	Saudi Region	11411	Saudi Arabia	103	23	Business District	\N	\N	2026-06-25 13:14:52.055721+00	\N	\N	Completed	Pickup stop seeded.	2026-06-24 10:44:52.055721+00	2026-06-25 10:44:52.055721+00
1a0f4c66-a563-4d3f-bce7-699e19066fe3	15b0c4c2-bc3f-4428-a448-bc92e937038f	924bfa7f-2fb5-4dc1-be16-4b9959807917	Pickup	1	Dammam Hub	Tamimi Markets	+966500000000	102 Dammam Hub Street		Dammam	Saudi Region	11411	Saudi Arabia	102	22	Business District	\N	\N	2026-06-25 12:44:52.055707+00	\N	\N	Planned	Pickup stop seeded.	2026-06-24 10:44:52.055707+00	2026-06-25 10:44:52.055707+00
2893cdbc-8827-4ed8-a0a8-3706f6f4c619	15b0c4c2-bc3f-4428-a448-bc92e937038f	c08ddedf-9e57-4863-b53b-c8c2babc904e	Delivery	2	Central Riyadh	Almarai Distribution Receiver	+966500000001	200 Central Riyadh Road		Riyadh	Saudi Region	11511	Saudi Arabia	200	40	Retail Zone	\N	\N	2026-06-25 14:44:52.055675+00	\N	\N	Planned	Delivery stop seeded.	2026-06-24 10:44:52.055676+00	2026-06-25 10:44:52.055676+00
34290612-bb9d-4ef3-8e34-64ff231d2b26	15b0c4c2-bc3f-4428-a448-bc92e937038f	b51217b1-ba3f-46d4-8816-1b08a6018cbe	Pickup	1	Khobar Crossdock	Nahdi Logistics	+966500000000	111 Khobar Crossdock Street		Khobar	Saudi Region	11411	Saudi Arabia	111	211	Business District	\N	\N	2026-06-25 17:14:52.055827+00	\N	\N	Planned	Pickup stop seeded.	2026-06-24 10:44:52.055827+00	2026-06-25 10:44:52.055827+00
359b09ea-f190-419f-a1d6-e941d4e3d820	15b0c4c2-bc3f-4428-a448-bc92e937038f	3749b6ba-9245-4c2c-b63f-35949beb4ff9	Pickup	1	Riyadh DC	Gulf Pharma	+966500000000	108 Riyadh DC Street		Riyadh	Saudi Region	11411	Saudi Arabia	108	28	Business District	\N	\N	2026-06-25 15:44:52.055796+00	\N	\N	Planned	Pickup stop seeded.	2026-06-24 10:44:52.055796+00	2026-06-25 10:44:52.055796+00
4259211e-0d22-460a-abaa-0116ba5e5258	15b0c4c2-bc3f-4428-a448-bc92e937038f	1014ac00-4c4f-4a92-ad82-0c60be902ffb	Pickup	1	Dammam Hub	Almarai Distribution	+966500000000	110 Dammam Hub Street		Dammam	Saudi Region	11411	Saudi Arabia	110	210	Business District	\N	\N	2026-06-25 16:44:52.055815+00	\N	\N	Completed	Pickup stop seeded.	2026-06-24 10:44:52.055815+00	2026-06-25 10:44:52.055815+00
42a418d7-0f93-48ac-99b8-038438b9bfbb	15b0c4c2-bc3f-4428-a448-bc92e937038f	fa2623c1-f506-448d-a41b-549944b275d3	Pickup	1	Dammam Hub	BinDawood Retail	+966500000000	106 Dammam Hub Street		Dammam	Saudi Region	11411	Saudi Arabia	106	26	Business District	\N	\N	2026-06-25 14:44:52.055772+00	\N	\N	Planned	Pickup stop seeded.	2026-06-24 10:44:52.055772+00	2026-06-25 10:44:52.055772+00
4c5087b9-72c0-456f-87b0-656266ec1dc1	15b0c4c2-bc3f-4428-a448-bc92e937038f	dced6032-cde6-4113-93a5-06deb07bfd3e	Pickup	1	Jeddah Gateway	Nahdi Logistics	+966500000000	101 Jeddah Gateway Street		Jeddah	Saudi Region	11411	Saudi Arabia	101	21	Business District	\N	\N	2026-06-25 12:14:52.055694+00	\N	\N	Planned	Pickup stop seeded.	2026-06-24 10:44:52.055694+00	2026-06-25 10:44:52.055694+00
54b40c9b-0df3-4687-92b1-12173c6e5eef	15b0c4c2-bc3f-4428-a448-bc92e937038f	c08ddedf-9e57-4863-b53b-c8c2babc904e	Pickup	1	Riyadh DC	Almarai Distribution	+966500000000	100 Riyadh DC Street		Riyadh	Saudi Region	11411	Saudi Arabia	100	20	Business District	\N	\N	2026-06-25 11:44:52.055671+00	\N	\N	Planned	Pickup stop seeded.	2026-06-24 10:44:52.055672+00	2026-06-25 10:44:52.055672+00
5cce56c0-5bfb-4be0-8b53-79928b736d8a	15b0c4c2-bc3f-4428-a448-bc92e937038f	cab3578e-644d-4a79-ad26-e08141973158	Pickup	1	Riyadh DC	Sultan Foods	+966500000000	104 Riyadh DC Street		Riyadh	Saudi Region	11411	Saudi Arabia	104	24	Business District	\N	\N	2026-06-25 13:44:52.055743+00	\N	\N	Planned	Pickup stop seeded.	2026-06-24 10:44:52.055743+00	2026-06-25 10:44:52.055743+00
6721f89a-0caa-4db8-98de-6443f865534e	15b0c4c2-bc3f-4428-a448-bc92e937038f	fa2623c1-f506-448d-a41b-549944b275d3	Delivery	2	Eastern Province	BinDawood Retail Receiver	+966500000001	206 Eastern Province Road		Dammam	Saudi Region	11511	Saudi Arabia	206	46	Retail Zone	\N	\N	2026-06-25 17:44:52.055774+00	\N	\N	Planned	Delivery stop seeded.	2026-06-24 10:44:52.055774+00	2026-06-25 10:44:52.055774+00
6e8739c2-4b7d-4a71-a637-d64e03f1acad	15b0c4c2-bc3f-4428-a448-bc92e937038f	ea15de64-9b9f-4bc6-b71d-73e5359dc1c8	Delivery	2	Western Retail Loop	Jarir Trade Receiver	+966500000001	203 Western Retail Loop Road		Khobar	Saudi Region	11511	Saudi Arabia	203	43	Retail Zone	\N	\N	2026-06-25 16:14:52.055723+00	2026-06-25 09:44:52.055723+00	2026-06-25 09:59:52.055723+00	Completed	Delivery stop seeded.	2026-06-24 10:44:52.055724+00	2026-06-25 10:44:52.055724+00
77b36cf1-e399-4315-96a4-a7a41b030609	15b0c4c2-bc3f-4428-a448-bc92e937038f	924bfa7f-2fb5-4dc1-be16-4b9959807917	Delivery	2	Eastern Province	Tamimi Markets Receiver	+966500000001	202 Eastern Province Road		Dammam	Saudi Region	11511	Saudi Arabia	202	42	Retail Zone	\N	\N	2026-06-25 15:44:52.055709+00	\N	\N	Planned	Delivery stop seeded.	2026-06-24 10:44:52.05571+00	2026-06-25 10:44:52.05571+00
812f07e8-921f-45c3-9dcd-d14891bf4de0	15b0c4c2-bc3f-4428-a448-bc92e937038f	1014ac00-4c4f-4a92-ad82-0c60be902ffb	Delivery	2	Eastern Province	Almarai Distribution Receiver	+966500000001	210 Eastern Province Road		Dammam	Saudi Region	11511	Saudi Arabia	210	410	Retail Zone	\N	\N	2026-06-25 19:44:52.055817+00	2026-06-25 09:44:52.055817+00	2026-06-25 09:59:52.055817+00	Completed	Delivery stop seeded.	2026-06-24 10:44:52.055817+00	2026-06-25 10:44:52.055817+00
9728c630-18d7-4077-9520-9d371e00ad42	15b0c4c2-bc3f-4428-a448-bc92e937038f	ac8a9e59-0af9-4042-9fe3-515ae997bfa8	Pickup	1	Jeddah Gateway	Noon Fulfilment	+966500000000	105 Jeddah Gateway Street		Jeddah	Saudi Region	11411	Saudi Arabia	105	25	Business District	\N	\N	2026-06-25 14:14:52.05576+00	\N	\N	Planned	Pickup stop seeded.	2026-06-24 10:44:52.05576+00	2026-06-25 10:44:52.05576+00
987f322f-fe56-465b-93d5-b313def4f0fb	15b0c4c2-bc3f-4428-a448-bc92e937038f	dced6032-cde6-4113-93a5-06deb07bfd3e	Delivery	2	North Jeddah	Nahdi Logistics Receiver	+966500000001	201 North Jeddah Road		Jeddah	Saudi Region	11511	Saudi Arabia	201	41	Retail Zone	\N	\N	2026-06-25 15:14:52.055696+00	\N	\N	Planned	Delivery stop seeded.	2026-06-24 10:44:52.055696+00	2026-06-25 10:44:52.055696+00
a13f337d-9427-410b-821f-73c494dce9bf	15b0c4c2-bc3f-4428-a448-bc92e937038f	3a706db4-b44f-4be7-a221-cfc37b486f2f	Delivery	2	Western Retail Loop	Al Rajhi Supply Receiver	+966500000001	207 Western Retail Loop Road		Khobar	Saudi Region	11511	Saudi Arabia	207	47	Retail Zone	\N	\N	2026-06-25 18:14:52.055787+00	\N	\N	Planned	Delivery stop seeded.	2026-06-24 10:44:52.055787+00	2026-06-25 10:44:52.055787+00
ba60957a-450b-4915-ba65-9bf8006b5b52	15b0c4c2-bc3f-4428-a448-bc92e937038f	b51217b1-ba3f-46d4-8816-1b08a6018cbe	Delivery	2	Western Retail Loop	Nahdi Logistics Receiver	+966500000001	211 Western Retail Loop Road		Khobar	Saudi Region	11511	Saudi Arabia	211	411	Retail Zone	\N	\N	2026-06-25 20:14:52.05583+00	\N	\N	Planned	Delivery stop seeded.	2026-06-24 10:44:52.05583+00	2026-06-25 10:44:52.05583+00
cde2f115-846f-4d7b-ac64-c1f363afead1	15b0c4c2-bc3f-4428-a448-bc92e937038f	e1d87a11-f67f-4ed4-8ce8-581116ccfd68	Pickup	1	Jeddah Gateway	Misk Essentials	+966500000000	109 Jeddah Gateway Street		Jeddah	Saudi Region	11411	Saudi Arabia	109	29	Business District	\N	\N	2026-06-25 16:14:52.055806+00	\N	\N	Planned	Pickup stop seeded.	2026-06-24 10:44:52.055806+00	2026-06-25 10:44:52.055806+00
d70fc53f-8b21-4df3-b9a9-e05167e7cb72	15b0c4c2-bc3f-4428-a448-bc92e937038f	3a706db4-b44f-4be7-a221-cfc37b486f2f	Pickup	1	Khobar Crossdock	Al Rajhi Supply	+966500000000	107 Khobar Crossdock Street		Khobar	Saudi Region	11411	Saudi Arabia	107	27	Business District	\N	\N	2026-06-25 15:14:52.055785+00	\N	\N	Planned	Pickup stop seeded.	2026-06-24 10:44:52.055785+00	2026-06-25 10:44:52.055785+00
e71f1c93-b3d4-4dd5-8373-5401cb76ec6d	15b0c4c2-bc3f-4428-a448-bc92e937038f	ac8a9e59-0af9-4042-9fe3-515ae997bfa8	Delivery	2	North Jeddah	Noon Fulfilment Receiver	+966500000001	205 North Jeddah Road		Jeddah	Saudi Region	11511	Saudi Arabia	205	45	Retail Zone	\N	\N	2026-06-25 17:14:52.055762+00	\N	\N	Planned	Delivery stop seeded.	2026-06-24 10:44:52.055762+00	2026-06-25 10:44:52.055762+00
fc2b01d3-d46f-4248-976c-8d44f0ca323d	15b0c4c2-bc3f-4428-a448-bc92e937038f	3749b6ba-9245-4c2c-b63f-35949beb4ff9	Delivery	2	Central Riyadh	Gulf Pharma Receiver	+966500000001	208 Central Riyadh Road		Riyadh	Saudi Region	11511	Saudi Arabia	208	48	Retail Zone	\N	\N	2026-06-25 18:44:52.055798+00	\N	\N	Planned	Delivery stop seeded.	2026-06-24 10:44:52.055798+00	2026-06-25 10:44:52.055798+00
fc91db56-832f-4b1a-a2b4-feab21b104af	15b0c4c2-bc3f-4428-a448-bc92e937038f	e1d87a11-f67f-4ed4-8ce8-581116ccfd68	Delivery	2	North Jeddah	Misk Essentials Receiver	+966500000001	209 North Jeddah Road		Jeddah	Saudi Region	11511	Saudi Arabia	209	49	Retail Zone	\N	\N	2026-06-25 19:14:52.055807+00	\N	\N	Planned	Delivery stop seeded.	2026-06-24 10:44:52.055807+00	2026-06-25 10:44:52.055807+00
fd0db050-ca3b-44e1-8bbb-eb55ffbc3515	15b0c4c2-bc3f-4428-a448-bc92e937038f	cab3578e-644d-4a79-ad26-e08141973158	Delivery	2	Central Riyadh	Sultan Foods Receiver	+966500000001	204 Central Riyadh Road		Riyadh	Saudi Region	11511	Saudi Arabia	204	44	Retail Zone	\N	\N	2026-06-25 16:44:52.055746+00	\N	\N	Planned	Delivery stop seeded.	2026-06-24 10:44:52.055748+00	2026-06-25 10:44:52.055748+00
\.


--
-- Data for Name: temperature_alerts; Type: TABLE DATA; Schema: public; Owner: neondb_owner
--

COPY public.temperature_alerts (id, tenant_id, device_id, shipment_id, reading_id, alert_type, severity, status, threshold_min, threshold_max, measured_temperature, triggered_at_utc, resolved_at_utc, resolved_by, resolution_notes, notes) FROM stdin;
40d3885c-965d-45df-af68-5d17e3f25cda	15b0c4c2-bc3f-4428-a448-bc92e937038f	67290376-cb86-4487-8e30-43b4bec5a916	dced6032-cde6-4113-93a5-06deb07bfd3e	cc06e90b-65ef-435d-9794-6f26ca7ac5c3	TemperatureBreach	Critical	Resolved	-25.00	-10.00	-7.50	2026-06-25 09:56:52.684931+00	2026-06-25 10:26:52.684931+00	Operations Desk	Route was re-iced and device recalibrated.	Seed cold-chain alert generated from the demo telemetry stream.
\.


--
-- Data for Name: temperature_devices; Type: TABLE DATA; Schema: public; Owner: neondb_owner
--

COPY public.temperature_devices (id, tenant_id, device_code, name, zone_id, shipment_id, vehicle_number, status, last_reported_temperature_celsius, battery_percent, last_ping_at_utc, notes, created_at_utc, updated_at_utc) FROM stdin;
35d69c48-9f8e-44af-9c64-c210f18c87ab	15b0c4c2-bc3f-4428-a448-bc92e937038f	TDEV-RASALMANAR-01	North Line Sensor	243a077d-37b8-4d7c-9ccc-54f5ac761ac4	c08ddedf-9e57-4863-b53b-c8c2babc904e	FLEET-260625-12	Active	4.20	87.00	2026-06-25 10:32:52.684931+00	Cabin probe for chilled movement.	2026-06-25 10:44:52.684931+00	2026-06-25 10:44:53.588973+00
67290376-cb86-4487-8e30-43b4bec5a916	15b0c4c2-bc3f-4428-a448-bc92e937038f	TDEV-RASALMANAR-02	Frozen Bay Sensor	d98b878e-9a07-411b-8a30-1e4b8a67bca9	dced6032-cde6-4113-93a5-06deb07bfd3e	FLEET-260625-22	Active	-16.80	72.00	2026-06-25 10:35:52.684931+00	Pallet bay probe for frozen freight.	2026-06-25 10:44:52.684931+00	2026-06-25 10:44:53.588973+00
\.


--
-- Data for Name: temperature_readings; Type: TABLE DATA; Schema: public; Owner: neondb_owner
--

COPY public.temperature_readings (id, tenant_id, device_id, shipment_id, zone_id, temperature_celsius, humidity_percent, latitude, longitude, source, status, notes, recorded_at_utc, created_at_utc) FROM stdin;
0a276b12-82d7-4161-9d45-01cfa7f7ba93	15b0c4c2-bc3f-4428-a448-bc92e937038f	67290376-cb86-4487-8e30-43b4bec5a916	dced6032-cde6-4113-93a5-06deb07bfd3e	d98b878e-9a07-411b-8a30-1e4b8a67bca9	-17.10	55.00	24.6340000	46.7550000	Gateway	Normal	In-range telemetry sample.	2026-06-25 10:10:52.684931+00	2026-06-25 10:10:52.684931+00
1c3cec6f-7ffc-4585-9d8b-88595aea75a6	15b0c4c2-bc3f-4428-a448-bc92e937038f	67290376-cb86-4487-8e30-43b4bec5a916	dced6032-cde6-4113-93a5-06deb07bfd3e	d98b878e-9a07-411b-8a30-1e4b8a67bca9	-17.50	49.00	24.6580000	46.7850000	Gateway	Normal	In-range telemetry sample.	2026-06-25 10:26:52.684931+00	2026-06-25 10:26:52.684931+00
214eb8f6-2db4-4239-9854-954c65f347f3	15b0c4c2-bc3f-4428-a448-bc92e937038f	35d69c48-9f8e-44af-9c64-c210f18c87ab	c08ddedf-9e57-4863-b53b-c8c2babc904e	243a077d-37b8-4d7c-9ccc-54f5ac761ac4	4.40	52.00	24.5740000	46.6800000	Sensor	Normal	In-range telemetry sample.	2026-06-25 09:30:52.684931+00	2026-06-25 09:30:52.684931+00
3e7cde5f-c5a7-4649-95cb-9c85f785a263	15b0c4c2-bc3f-4428-a448-bc92e937038f	67290376-cb86-4487-8e30-43b4bec5a916	dced6032-cde6-4113-93a5-06deb07bfd3e	d98b878e-9a07-411b-8a30-1e4b8a67bca9	-17.50	55.00	24.5860000	46.6950000	Gateway	Normal	In-range telemetry sample.	2026-06-25 09:38:52.684931+00	2026-06-25 09:38:52.684931+00
47c801c4-54e3-49d0-b7a4-4a1164f111ec	15b0c4c2-bc3f-4428-a448-bc92e937038f	35d69c48-9f8e-44af-9c64-c210f18c87ab	c08ddedf-9e57-4863-b53b-c8c2babc904e	243a077d-37b8-4d7c-9ccc-54f5ac761ac4	4.20	46.00	24.5980000	46.7100000	Sensor	Normal	In-range telemetry sample.	2026-06-25 09:46:52.684931+00	2026-06-25 09:46:52.684931+00
48192633-75d0-4e81-8dc7-ad97e9612394	15b0c4c2-bc3f-4428-a448-bc92e937038f	35d69c48-9f8e-44af-9c64-c210f18c87ab	c08ddedf-9e57-4863-b53b-c8c2babc904e	243a077d-37b8-4d7c-9ccc-54f5ac761ac4	4.20	52.00	24.6700000	46.8000000	Sensor	Normal	In-range telemetry sample.	2026-06-25 10:34:52.684931+00	2026-06-25 10:34:52.684931+00
4b73e893-4673-440f-8f10-79452f7b022a	15b0c4c2-bc3f-4428-a448-bc92e937038f	35d69c48-9f8e-44af-9c64-c210f18c87ab	c08ddedf-9e57-4863-b53b-c8c2babc904e	243a077d-37b8-4d7c-9ccc-54f5ac761ac4	4.00	46.00	24.5500000	46.6500000	Sensor	Normal	In-range telemetry sample.	2026-06-25 09:14:52.684931+00	2026-06-25 09:14:52.684931+00
6e576d10-ba1d-407b-b6f3-9c0da87c981b	15b0c4c2-bc3f-4428-a448-bc92e937038f	35d69c48-9f8e-44af-9c64-c210f18c87ab	c08ddedf-9e57-4863-b53b-c8c2babc904e	243a077d-37b8-4d7c-9ccc-54f5ac761ac4	4.40	46.00	24.6460000	46.7700000	Sensor	Normal	In-range telemetry sample.	2026-06-25 10:18:52.684931+00	2026-06-25 10:18:52.684931+00
a15b0ddd-3dbc-4714-b4d6-a266c18cb41e	15b0c4c2-bc3f-4428-a448-bc92e937038f	35d69c48-9f8e-44af-9c64-c210f18c87ab	c08ddedf-9e57-4863-b53b-c8c2babc904e	243a077d-37b8-4d7c-9ccc-54f5ac761ac4	4.00	52.00	24.6220000	46.7400000	Sensor	Normal	In-range telemetry sample.	2026-06-25 10:02:52.684931+00	2026-06-25 10:02:52.684931+00
cc06e90b-65ef-435d-9794-6f26ca7ac5c3	15b0c4c2-bc3f-4428-a448-bc92e937038f	67290376-cb86-4487-8e30-43b4bec5a916	dced6032-cde6-4113-93a5-06deb07bfd3e	d98b878e-9a07-411b-8a30-1e4b8a67bca9	-7.50	49.00	24.6100000	46.7250000	Gateway	Breach	Temperature moved outside the allowed band.	2026-06-25 09:54:52.684931+00	2026-06-25 09:54:52.684931+00
d3af99c4-377d-48ea-8809-512731a35c9f	15b0c4c2-bc3f-4428-a448-bc92e937038f	67290376-cb86-4487-8e30-43b4bec5a916	dced6032-cde6-4113-93a5-06deb07bfd3e	d98b878e-9a07-411b-8a30-1e4b8a67bca9	-16.70	55.00	24.6820000	46.8150000	Gateway	Normal	In-range telemetry sample.	2026-06-25 10:42:52.684931+00	2026-06-25 10:42:52.684931+00
f11cd6d2-5324-445d-a864-c90feb763574	15b0c4c2-bc3f-4428-a448-bc92e937038f	67290376-cb86-4487-8e30-43b4bec5a916	dced6032-cde6-4113-93a5-06deb07bfd3e	d98b878e-9a07-411b-8a30-1e4b8a67bca9	-17.10	49.00	24.5620000	46.6650000	Gateway	Normal	In-range telemetry sample.	2026-06-25 09:22:52.684931+00	2026-06-25 09:22:52.684931+00
\.


--
-- Data for Name: temperature_zones; Type: TABLE DATA; Schema: public; Owner: neondb_owner
--

COPY public.temperature_zones (id, tenant_id, code, name, min_celsius, max_celsius, color, is_active, notes, created_at_utc, updated_at_utc) FROM stdin;
243a077d-37b8-4d7c-9ccc-54f5ac761ac4	15b0c4c2-bc3f-4428-a448-bc92e937038f	CHILLED	Chilled Goods	2.00	8.00	#38bdf8	t	Milk, produce, pharmaceuticals, and other chilled goods.	2026-06-25 10:44:52.684931+00	2026-06-25 10:44:53.588973+00
ceac991f-4d30-4eb4-b49a-edde139d43e7	15b0c4c2-bc3f-4428-a448-bc92e937038f	CONTROLLED	Controlled Ambient	15.00	25.00	#14b8a6	t	Controlled ambient handling for sensitive freight.	2026-06-25 10:44:52.684931+00	2026-06-25 10:44:53.588973+00
d98b878e-9a07-411b-8a30-1e4b8a67bca9	15b0c4c2-bc3f-4428-a448-bc92e937038f	FROZEN	Frozen Goods	-25.00	-10.00	#8b5cf6	t	Frozen inventory with strict temperature control.	2026-06-25 10:44:52.684931+00	2026-06-25 10:44:53.588973+00
\.


--
-- Name: barcode_scan_events PK_barcode_scan_events; Type: CONSTRAINT; Schema: public; Owner: neondb_owner
--

ALTER TABLE ONLY public.barcode_scan_events
    ADD CONSTRAINT "PK_barcode_scan_events" PRIMARY KEY (id);


--
-- Name: booking_requests PK_booking_requests; Type: CONSTRAINT; Schema: public; Owner: neondb_owner
--

ALTER TABLE ONLY public.booking_requests
    ADD CONSTRAINT "PK_booking_requests" PRIMARY KEY (id);


--
-- Name: carrier_contacts PK_carrier_contacts; Type: CONSTRAINT; Schema: public; Owner: neondb_owner
--

ALTER TABLE ONLY public.carrier_contacts
    ADD CONSTRAINT "PK_carrier_contacts" PRIMARY KEY (id);


--
-- Name: carrier_performance_scores PK_carrier_performance_scores; Type: CONSTRAINT; Schema: public; Owner: neondb_owner
--

ALTER TABLE ONLY public.carrier_performance_scores
    ADD CONSTRAINT "PK_carrier_performance_scores" PRIMARY KEY (id);


--
-- Name: carriers PK_carriers; Type: CONSTRAINT; Schema: public; Owner: neondb_owner
--

ALTER TABLE ONLY public.carriers
    ADD CONSTRAINT "PK_carriers" PRIMARY KEY (id);


--
-- Name: cold_chain_reports PK_cold_chain_reports; Type: CONSTRAINT; Schema: public; Owner: neondb_owner
--

ALTER TABLE ONLY public.cold_chain_reports
    ADD CONSTRAINT "PK_cold_chain_reports" PRIMARY KEY (id);


--
-- Name: customer_tracking_links PK_customer_tracking_links; Type: CONSTRAINT; Schema: public; Owner: neondb_owner
--

ALTER TABLE ONLY public.customer_tracking_links
    ADD CONSTRAINT "PK_customer_tracking_links" PRIMARY KEY (id);


--
-- Name: delivery_routes PK_delivery_routes; Type: CONSTRAINT; Schema: public; Owner: neondb_owner
--

ALTER TABLE ONLY public.delivery_routes
    ADD CONSTRAINT "PK_delivery_routes" PRIMARY KEY (id);


--
-- Name: dispatch_orders PK_dispatch_orders; Type: CONSTRAINT; Schema: public; Owner: neondb_owner
--

ALTER TABLE ONLY public.dispatch_orders
    ADD CONSTRAINT "PK_dispatch_orders" PRIMARY KEY (id);


--
-- Name: driver_tasks PK_driver_tasks; Type: CONSTRAINT; Schema: public; Owner: neondb_owner
--

ALTER TABLE ONLY public.driver_tasks
    ADD CONSTRAINT "PK_driver_tasks" PRIMARY KEY (id);


--
-- Name: fleet_fuel_events PK_fleet_fuel_events; Type: CONSTRAINT; Schema: public; Owner: neondb_owner
--

ALTER TABLE ONLY public.fleet_fuel_events
    ADD CONSTRAINT "PK_fleet_fuel_events" PRIMARY KEY (id);


--
-- Name: fleet_maintenance_tickets PK_fleet_maintenance_tickets; Type: CONSTRAINT; Schema: public; Owner: neondb_owner
--

ALTER TABLE ONLY public.fleet_maintenance_tickets
    ADD CONSTRAINT "PK_fleet_maintenance_tickets" PRIMARY KEY (id);


--
-- Name: fleet_readiness_documents PK_fleet_readiness_documents; Type: CONSTRAINT; Schema: public; Owner: neondb_owner
--

ALTER TABLE ONLY public.fleet_readiness_documents
    ADD CONSTRAINT "PK_fleet_readiness_documents" PRIMARY KEY (id);


--
-- Name: fleet_shipments PK_fleet_shipments; Type: CONSTRAINT; Schema: public; Owner: neondb_owner
--

ALTER TABLE ONLY public.fleet_shipments
    ADD CONSTRAINT "PK_fleet_shipments" PRIMARY KEY (id);


--
-- Name: fleet_tracking_points PK_fleet_tracking_points; Type: CONSTRAINT; Schema: public; Owner: neondb_owner
--

ALTER TABLE ONLY public.fleet_tracking_points
    ADD CONSTRAINT "PK_fleet_tracking_points" PRIMARY KEY (id);


--
-- Name: fleet_vehicles PK_fleet_vehicles; Type: CONSTRAINT; Schema: public; Owner: neondb_owner
--

ALTER TABLE ONLY public.fleet_vehicles
    ADD CONSTRAINT "PK_fleet_vehicles" PRIMARY KEY (id);


--
-- Name: last_mile_stops PK_last_mile_stops; Type: CONSTRAINT; Schema: public; Owner: neondb_owner
--

ALTER TABLE ONLY public.last_mile_stops
    ADD CONSTRAINT "PK_last_mile_stops" PRIMARY KEY (id);


--
-- Name: proofs_of_delivery PK_proofs_of_delivery; Type: CONSTRAINT; Schema: public; Owner: neondb_owner
--

ALTER TABLE ONLY public.proofs_of_delivery
    ADD CONSTRAINT "PK_proofs_of_delivery" PRIMARY KEY (id);


--
-- Name: quote_requests PK_quote_requests; Type: CONSTRAINT; Schema: public; Owner: neondb_owner
--

ALTER TABLE ONLY public.quote_requests
    ADD CONSTRAINT "PK_quote_requests" PRIMARY KEY (id);


--
-- Name: refrigeration_unit_health PK_refrigeration_unit_health; Type: CONSTRAINT; Schema: public; Owner: neondb_owner
--

ALTER TABLE ONLY public.refrigeration_unit_health
    ADD CONSTRAINT "PK_refrigeration_unit_health" PRIMARY KEY (id);


--
-- Name: rfid_events PK_rfid_events; Type: CONSTRAINT; Schema: public; Owner: neondb_owner
--

ALTER TABLE ONLY public.rfid_events
    ADD CONSTRAINT "PK_rfid_events" PRIMARY KEY (id);


--
-- Name: saudi_region_references PK_saudi_region_references; Type: CONSTRAINT; Schema: public; Owner: neondb_owner
--

ALTER TABLE ONLY public.saudi_region_references
    ADD CONSTRAINT "PK_saudi_region_references" PRIMARY KEY (id);


--
-- Name: shipment_carrier_assignments PK_shipment_carrier_assignments; Type: CONSTRAINT; Schema: public; Owner: neondb_owner
--

ALTER TABLE ONLY public.shipment_carrier_assignments
    ADD CONSTRAINT "PK_shipment_carrier_assignments" PRIMARY KEY (id);


--
-- Name: shipment_events PK_shipment_events; Type: CONSTRAINT; Schema: public; Owner: neondb_owner
--

ALTER TABLE ONLY public.shipment_events
    ADD CONSTRAINT "PK_shipment_events" PRIMARY KEY (id);


--
-- Name: shipment_stops PK_shipment_stops; Type: CONSTRAINT; Schema: public; Owner: neondb_owner
--

ALTER TABLE ONLY public.shipment_stops
    ADD CONSTRAINT "PK_shipment_stops" PRIMARY KEY (id);


--
-- Name: temperature_alerts PK_temperature_alerts; Type: CONSTRAINT; Schema: public; Owner: neondb_owner
--

ALTER TABLE ONLY public.temperature_alerts
    ADD CONSTRAINT "PK_temperature_alerts" PRIMARY KEY (id);


--
-- Name: temperature_devices PK_temperature_devices; Type: CONSTRAINT; Schema: public; Owner: neondb_owner
--

ALTER TABLE ONLY public.temperature_devices
    ADD CONSTRAINT "PK_temperature_devices" PRIMARY KEY (id);


--
-- Name: temperature_readings PK_temperature_readings; Type: CONSTRAINT; Schema: public; Owner: neondb_owner
--

ALTER TABLE ONLY public.temperature_readings
    ADD CONSTRAINT "PK_temperature_readings" PRIMARY KEY (id);


--
-- Name: temperature_zones PK_temperature_zones; Type: CONSTRAINT; Schema: public; Owner: neondb_owner
--

ALTER TABLE ONLY public.temperature_zones
    ADD CONSTRAINT "PK_temperature_zones" PRIMARY KEY (id);


--
-- Name: IX_barcode_scan_events_tenant_id_asset_id_recorded_at_utc; Type: INDEX; Schema: public; Owner: neondb_owner
--

CREATE INDEX "IX_barcode_scan_events_tenant_id_asset_id_recorded_at_utc" ON public.barcode_scan_events USING btree (tenant_id, asset_id, recorded_at_utc);


--
-- Name: IX_barcode_scan_events_tenant_id_shipment_id_recorded_at_utc; Type: INDEX; Schema: public; Owner: neondb_owner
--

CREATE INDEX "IX_barcode_scan_events_tenant_id_shipment_id_recorded_at_utc" ON public.barcode_scan_events USING btree (tenant_id, shipment_id, recorded_at_utc);


--
-- Name: IX_booking_requests_tenant_id_request_number; Type: INDEX; Schema: public; Owner: neondb_owner
--

CREATE UNIQUE INDEX "IX_booking_requests_tenant_id_request_number" ON public.booking_requests USING btree (tenant_id, request_number);


--
-- Name: IX_booking_requests_tenant_id_status; Type: INDEX; Schema: public; Owner: neondb_owner
--

CREATE INDEX "IX_booking_requests_tenant_id_status" ON public.booking_requests USING btree (tenant_id, status);


--
-- Name: IX_carrier_contacts_tenant_id_carrier_id; Type: INDEX; Schema: public; Owner: neondb_owner
--

CREATE INDEX "IX_carrier_contacts_tenant_id_carrier_id" ON public.carrier_contacts USING btree (tenant_id, carrier_id);


--
-- Name: IX_carrier_performance_scores_tenant_id_carrier_id_scored_at_u~; Type: INDEX; Schema: public; Owner: neondb_owner
--

CREATE INDEX "IX_carrier_performance_scores_tenant_id_carrier_id_scored_at_u~" ON public.carrier_performance_scores USING btree (tenant_id, carrier_id, scored_at_utc);


--
-- Name: IX_carriers_tenant_id_code; Type: INDEX; Schema: public; Owner: neondb_owner
--

CREATE UNIQUE INDEX "IX_carriers_tenant_id_code" ON public.carriers USING btree (tenant_id, code);


--
-- Name: IX_carriers_tenant_id_status; Type: INDEX; Schema: public; Owner: neondb_owner
--

CREATE INDEX "IX_carriers_tenant_id_status" ON public.carriers USING btree (tenant_id, status);


--
-- Name: IX_cold_chain_reports_tenant_id_generated_at_utc; Type: INDEX; Schema: public; Owner: neondb_owner
--

CREATE INDEX "IX_cold_chain_reports_tenant_id_generated_at_utc" ON public.cold_chain_reports USING btree (tenant_id, generated_at_utc);


--
-- Name: IX_cold_chain_reports_tenant_id_shipment_id; Type: INDEX; Schema: public; Owner: neondb_owner
--

CREATE UNIQUE INDEX "IX_cold_chain_reports_tenant_id_shipment_id" ON public.cold_chain_reports USING btree (tenant_id, shipment_id);


--
-- Name: IX_customer_tracking_links_tenant_id_is_revoked_expires_at_utc; Type: INDEX; Schema: public; Owner: neondb_owner
--

CREATE INDEX "IX_customer_tracking_links_tenant_id_is_revoked_expires_at_utc" ON public.customer_tracking_links USING btree (tenant_id, is_revoked, expires_at_utc);


--
-- Name: IX_customer_tracking_links_tenant_id_shipment_id; Type: INDEX; Schema: public; Owner: neondb_owner
--

CREATE INDEX "IX_customer_tracking_links_tenant_id_shipment_id" ON public.customer_tracking_links USING btree (tenant_id, shipment_id);


--
-- Name: IX_customer_tracking_links_tenant_id_token; Type: INDEX; Schema: public; Owner: neondb_owner
--

CREATE UNIQUE INDEX "IX_customer_tracking_links_tenant_id_token" ON public.customer_tracking_links USING btree (tenant_id, token);


--
-- Name: IX_delivery_routes_tenant_id_route_code; Type: INDEX; Schema: public; Owner: neondb_owner
--

CREATE UNIQUE INDEX "IX_delivery_routes_tenant_id_route_code" ON public.delivery_routes USING btree (tenant_id, route_code);


--
-- Name: IX_delivery_routes_tenant_id_status; Type: INDEX; Schema: public; Owner: neondb_owner
--

CREATE INDEX "IX_delivery_routes_tenant_id_status" ON public.delivery_routes USING btree (tenant_id, status);


--
-- Name: IX_dispatch_orders_tenant_id_order_number; Type: INDEX; Schema: public; Owner: neondb_owner
--

CREATE UNIQUE INDEX "IX_dispatch_orders_tenant_id_order_number" ON public.dispatch_orders USING btree (tenant_id, order_number);


--
-- Name: IX_dispatch_orders_tenant_id_route_code; Type: INDEX; Schema: public; Owner: neondb_owner
--

CREATE INDEX "IX_dispatch_orders_tenant_id_route_code" ON public.dispatch_orders USING btree (tenant_id, route_code);


--
-- Name: IX_dispatch_orders_tenant_id_status; Type: INDEX; Schema: public; Owner: neondb_owner
--

CREATE INDEX "IX_dispatch_orders_tenant_id_status" ON public.dispatch_orders USING btree (tenant_id, status);


--
-- Name: IX_driver_tasks_tenant_id_driver_name_status; Type: INDEX; Schema: public; Owner: neondb_owner
--

CREATE INDEX "IX_driver_tasks_tenant_id_driver_name_status" ON public.driver_tasks USING btree (tenant_id, driver_name, status);


--
-- Name: IX_driver_tasks_tenant_id_due_at_utc; Type: INDEX; Schema: public; Owner: neondb_owner
--

CREATE INDEX "IX_driver_tasks_tenant_id_due_at_utc" ON public.driver_tasks USING btree (tenant_id, due_at_utc);


--
-- Name: IX_driver_tasks_tenant_id_shipment_id; Type: INDEX; Schema: public; Owner: neondb_owner
--

CREATE INDEX "IX_driver_tasks_tenant_id_shipment_id" ON public.driver_tasks USING btree (tenant_id, shipment_id);


--
-- Name: IX_fleet_fuel_events_tenant_id_anomaly_flag; Type: INDEX; Schema: public; Owner: neondb_owner
--

CREATE INDEX "IX_fleet_fuel_events_tenant_id_anomaly_flag" ON public.fleet_fuel_events USING btree (tenant_id, anomaly_flag);


--
-- Name: IX_fleet_fuel_events_tenant_id_recorded_at_utc; Type: INDEX; Schema: public; Owner: neondb_owner
--

CREATE INDEX "IX_fleet_fuel_events_tenant_id_recorded_at_utc" ON public.fleet_fuel_events USING btree (tenant_id, recorded_at_utc);


--
-- Name: IX_fleet_fuel_events_tenant_id_vehicle_number; Type: INDEX; Schema: public; Owner: neondb_owner
--

CREATE INDEX "IX_fleet_fuel_events_tenant_id_vehicle_number" ON public.fleet_fuel_events USING btree (tenant_id, vehicle_number);


--
-- Name: IX_fleet_maintenance_tickets_tenant_id_status; Type: INDEX; Schema: public; Owner: neondb_owner
--

CREATE INDEX "IX_fleet_maintenance_tickets_tenant_id_status" ON public.fleet_maintenance_tickets USING btree (tenant_id, status);


--
-- Name: IX_fleet_maintenance_tickets_tenant_id_vehicle_number; Type: INDEX; Schema: public; Owner: neondb_owner
--

CREATE INDEX "IX_fleet_maintenance_tickets_tenant_id_vehicle_number" ON public.fleet_maintenance_tickets USING btree (tenant_id, vehicle_number);


--
-- Name: IX_fleet_maintenance_tickets_tenant_id_work_order_number; Type: INDEX; Schema: public; Owner: neondb_owner
--

CREATE UNIQUE INDEX "IX_fleet_maintenance_tickets_tenant_id_work_order_number" ON public.fleet_maintenance_tickets USING btree (tenant_id, work_order_number);


--
-- Name: IX_fleet_readiness_documents_tenant_id_document_status_expiry_~; Type: INDEX; Schema: public; Owner: neondb_owner
--

CREATE INDEX "IX_fleet_readiness_documents_tenant_id_document_status_expiry_~" ON public.fleet_readiness_documents USING btree (tenant_id, document_status, expiry_status);


--
-- Name: IX_fleet_readiness_documents_tenant_id_gregorian_expiry_date; Type: INDEX; Schema: public; Owner: neondb_owner
--

CREATE INDEX "IX_fleet_readiness_documents_tenant_id_gregorian_expiry_date" ON public.fleet_readiness_documents USING btree (tenant_id, gregorian_expiry_date);


--
-- Name: IX_fleet_readiness_documents_tenant_id_kind_subject_type_docum~; Type: INDEX; Schema: public; Owner: neondb_owner
--

CREATE INDEX "IX_fleet_readiness_documents_tenant_id_kind_subject_type_docum~" ON public.fleet_readiness_documents USING btree (tenant_id, kind, subject_type, document_type);


--
-- Name: IX_fleet_shipments_tenant_id_route_code; Type: INDEX; Schema: public; Owner: neondb_owner
--

CREATE INDEX "IX_fleet_shipments_tenant_id_route_code" ON public.fleet_shipments USING btree (tenant_id, route_code);


--
-- Name: IX_fleet_shipments_tenant_id_shipment_number; Type: INDEX; Schema: public; Owner: neondb_owner
--

CREATE UNIQUE INDEX "IX_fleet_shipments_tenant_id_shipment_number" ON public.fleet_shipments USING btree (tenant_id, shipment_number);


--
-- Name: IX_fleet_shipments_tenant_id_status; Type: INDEX; Schema: public; Owner: neondb_owner
--

CREATE INDEX "IX_fleet_shipments_tenant_id_status" ON public.fleet_shipments USING btree (tenant_id, status);


--
-- Name: IX_fleet_tracking_points_tenant_id_recorded_at_utc; Type: INDEX; Schema: public; Owner: neondb_owner
--

CREATE INDEX "IX_fleet_tracking_points_tenant_id_recorded_at_utc" ON public.fleet_tracking_points USING btree (tenant_id, recorded_at_utc);


--
-- Name: IX_fleet_tracking_points_tenant_id_shipment_number; Type: INDEX; Schema: public; Owner: neondb_owner
--

CREATE INDEX "IX_fleet_tracking_points_tenant_id_shipment_number" ON public.fleet_tracking_points USING btree (tenant_id, shipment_number);


--
-- Name: IX_fleet_tracking_points_tenant_id_vehicle_number; Type: INDEX; Schema: public; Owner: neondb_owner
--

CREATE INDEX "IX_fleet_tracking_points_tenant_id_vehicle_number" ON public.fleet_tracking_points USING btree (tenant_id, vehicle_number);


--
-- Name: IX_fleet_vehicles_tenant_id_driver_name; Type: INDEX; Schema: public; Owner: neondb_owner
--

CREATE INDEX "IX_fleet_vehicles_tenant_id_driver_name" ON public.fleet_vehicles USING btree (tenant_id, driver_name);


--
-- Name: IX_fleet_vehicles_tenant_id_status; Type: INDEX; Schema: public; Owner: neondb_owner
--

CREATE INDEX "IX_fleet_vehicles_tenant_id_status" ON public.fleet_vehicles USING btree (tenant_id, status);


--
-- Name: IX_fleet_vehicles_tenant_id_vehicle_number; Type: INDEX; Schema: public; Owner: neondb_owner
--

CREATE UNIQUE INDEX "IX_fleet_vehicles_tenant_id_vehicle_number" ON public.fleet_vehicles USING btree (tenant_id, vehicle_number);


--
-- Name: IX_last_mile_stops_tenant_id_eta_utc; Type: INDEX; Schema: public; Owner: neondb_owner
--

CREATE INDEX "IX_last_mile_stops_tenant_id_eta_utc" ON public.last_mile_stops USING btree (tenant_id, eta_utc);


--
-- Name: IX_last_mile_stops_tenant_id_order_number; Type: INDEX; Schema: public; Owner: neondb_owner
--

CREATE UNIQUE INDEX "IX_last_mile_stops_tenant_id_order_number" ON public.last_mile_stops USING btree (tenant_id, order_number);


--
-- Name: IX_last_mile_stops_tenant_id_route_code; Type: INDEX; Schema: public; Owner: neondb_owner
--

CREATE INDEX "IX_last_mile_stops_tenant_id_route_code" ON public.last_mile_stops USING btree (tenant_id, route_code);


--
-- Name: IX_last_mile_stops_tenant_id_status; Type: INDEX; Schema: public; Owner: neondb_owner
--

CREATE INDEX "IX_last_mile_stops_tenant_id_status" ON public.last_mile_stops USING btree (tenant_id, status);


--
-- Name: IX_proofs_of_delivery_tenant_id_shipment_id; Type: INDEX; Schema: public; Owner: neondb_owner
--

CREATE INDEX "IX_proofs_of_delivery_tenant_id_shipment_id" ON public.proofs_of_delivery USING btree (tenant_id, shipment_id);


--
-- Name: IX_proofs_of_delivery_tenant_id_status; Type: INDEX; Schema: public; Owner: neondb_owner
--

CREATE INDEX "IX_proofs_of_delivery_tenant_id_status" ON public.proofs_of_delivery USING btree (tenant_id, status);


--
-- Name: IX_proofs_of_delivery_tenant_id_stop_id; Type: INDEX; Schema: public; Owner: neondb_owner
--

CREATE INDEX "IX_proofs_of_delivery_tenant_id_stop_id" ON public.proofs_of_delivery USING btree (tenant_id, stop_id);


--
-- Name: IX_quote_requests_tenant_id_quote_number; Type: INDEX; Schema: public; Owner: neondb_owner
--

CREATE UNIQUE INDEX "IX_quote_requests_tenant_id_quote_number" ON public.quote_requests USING btree (tenant_id, quote_number);


--
-- Name: IX_quote_requests_tenant_id_status; Type: INDEX; Schema: public; Owner: neondb_owner
--

CREATE INDEX "IX_quote_requests_tenant_id_status" ON public.quote_requests USING btree (tenant_id, status);


--
-- Name: IX_refrigeration_unit_health_tenant_id_status; Type: INDEX; Schema: public; Owner: neondb_owner
--

CREATE INDEX "IX_refrigeration_unit_health_tenant_id_status" ON public.refrigeration_unit_health USING btree (tenant_id, status);


--
-- Name: IX_refrigeration_unit_health_tenant_id_vehicle_number; Type: INDEX; Schema: public; Owner: neondb_owner
--

CREATE UNIQUE INDEX "IX_refrigeration_unit_health_tenant_id_vehicle_number" ON public.refrigeration_unit_health USING btree (tenant_id, vehicle_number);


--
-- Name: IX_rfid_events_tenant_id_asset_id_recorded_at_utc; Type: INDEX; Schema: public; Owner: neondb_owner
--

CREATE INDEX "IX_rfid_events_tenant_id_asset_id_recorded_at_utc" ON public.rfid_events USING btree (tenant_id, asset_id, recorded_at_utc);


--
-- Name: IX_rfid_events_tenant_id_shipment_id_recorded_at_utc; Type: INDEX; Schema: public; Owner: neondb_owner
--

CREATE INDEX "IX_rfid_events_tenant_id_shipment_id_recorded_at_utc" ON public.rfid_events USING btree (tenant_id, shipment_id, recorded_at_utc);


--
-- Name: IX_saudi_region_references_code; Type: INDEX; Schema: public; Owner: neondb_owner
--

CREATE UNIQUE INDEX "IX_saudi_region_references_code" ON public.saudi_region_references USING btree (code);


--
-- Name: IX_shipment_carrier_assignments_tenant_id_carrier_id; Type: INDEX; Schema: public; Owner: neondb_owner
--

CREATE INDEX "IX_shipment_carrier_assignments_tenant_id_carrier_id" ON public.shipment_carrier_assignments USING btree (tenant_id, carrier_id);


--
-- Name: IX_shipment_carrier_assignments_tenant_id_shipment_id; Type: INDEX; Schema: public; Owner: neondb_owner
--

CREATE UNIQUE INDEX "IX_shipment_carrier_assignments_tenant_id_shipment_id" ON public.shipment_carrier_assignments USING btree (tenant_id, shipment_id);


--
-- Name: IX_shipment_events_tenant_id_shipment_id_occurred_at_utc; Type: INDEX; Schema: public; Owner: neondb_owner
--

CREATE INDEX "IX_shipment_events_tenant_id_shipment_id_occurred_at_utc" ON public.shipment_events USING btree (tenant_id, shipment_id, occurred_at_utc);


--
-- Name: IX_shipment_events_tenant_id_visibility; Type: INDEX; Schema: public; Owner: neondb_owner
--

CREATE INDEX "IX_shipment_events_tenant_id_visibility" ON public.shipment_events USING btree (tenant_id, visibility);


--
-- Name: IX_shipment_stops_tenant_id_shipment_id_sequence_no; Type: INDEX; Schema: public; Owner: neondb_owner
--

CREATE UNIQUE INDEX "IX_shipment_stops_tenant_id_shipment_id_sequence_no" ON public.shipment_stops USING btree (tenant_id, shipment_id, sequence_no);


--
-- Name: IX_shipment_stops_tenant_id_shipment_id_status; Type: INDEX; Schema: public; Owner: neondb_owner
--

CREATE INDEX "IX_shipment_stops_tenant_id_shipment_id_status" ON public.shipment_stops USING btree (tenant_id, shipment_id, status);


--
-- Name: IX_temperature_alerts_tenant_id_device_id_triggered_at_utc; Type: INDEX; Schema: public; Owner: neondb_owner
--

CREATE INDEX "IX_temperature_alerts_tenant_id_device_id_triggered_at_utc" ON public.temperature_alerts USING btree (tenant_id, device_id, triggered_at_utc);


--
-- Name: IX_temperature_alerts_tenant_id_shipment_id_status; Type: INDEX; Schema: public; Owner: neondb_owner
--

CREATE INDEX "IX_temperature_alerts_tenant_id_shipment_id_status" ON public.temperature_alerts USING btree (tenant_id, shipment_id, status);


--
-- Name: IX_temperature_alerts_tenant_id_status; Type: INDEX; Schema: public; Owner: neondb_owner
--

CREATE INDEX "IX_temperature_alerts_tenant_id_status" ON public.temperature_alerts USING btree (tenant_id, status);


--
-- Name: IX_temperature_devices_tenant_id_device_code; Type: INDEX; Schema: public; Owner: neondb_owner
--

CREATE UNIQUE INDEX "IX_temperature_devices_tenant_id_device_code" ON public.temperature_devices USING btree (tenant_id, device_code);


--
-- Name: IX_temperature_devices_tenant_id_shipment_id; Type: INDEX; Schema: public; Owner: neondb_owner
--

CREATE INDEX "IX_temperature_devices_tenant_id_shipment_id" ON public.temperature_devices USING btree (tenant_id, shipment_id);


--
-- Name: IX_temperature_devices_tenant_id_status; Type: INDEX; Schema: public; Owner: neondb_owner
--

CREATE INDEX "IX_temperature_devices_tenant_id_status" ON public.temperature_devices USING btree (tenant_id, status);


--
-- Name: IX_temperature_devices_tenant_id_zone_id; Type: INDEX; Schema: public; Owner: neondb_owner
--

CREATE INDEX "IX_temperature_devices_tenant_id_zone_id" ON public.temperature_devices USING btree (tenant_id, zone_id);


--
-- Name: IX_temperature_readings_tenant_id_device_id_recorded_at_utc; Type: INDEX; Schema: public; Owner: neondb_owner
--

CREATE INDEX "IX_temperature_readings_tenant_id_device_id_recorded_at_utc" ON public.temperature_readings USING btree (tenant_id, device_id, recorded_at_utc);


--
-- Name: IX_temperature_readings_tenant_id_shipment_id_recorded_at_utc; Type: INDEX; Schema: public; Owner: neondb_owner
--

CREATE INDEX "IX_temperature_readings_tenant_id_shipment_id_recorded_at_utc" ON public.temperature_readings USING btree (tenant_id, shipment_id, recorded_at_utc);


--
-- Name: IX_temperature_readings_tenant_id_zone_id_recorded_at_utc; Type: INDEX; Schema: public; Owner: neondb_owner
--

CREATE INDEX "IX_temperature_readings_tenant_id_zone_id_recorded_at_utc" ON public.temperature_readings USING btree (tenant_id, zone_id, recorded_at_utc);


--
-- Name: IX_temperature_zones_tenant_id_code; Type: INDEX; Schema: public; Owner: neondb_owner
--

CREATE UNIQUE INDEX "IX_temperature_zones_tenant_id_code" ON public.temperature_zones USING btree (tenant_id, code);


--
-- Name: IX_temperature_zones_tenant_id_is_active; Type: INDEX; Schema: public; Owner: neondb_owner
--

CREATE INDEX "IX_temperature_zones_tenant_id_is_active" ON public.temperature_zones USING btree (tenant_id, is_active);


--
-- PostgreSQL database dump complete
--

\unrestrict GHL92Kg4kxYVBxoI3YtcMgguoHM81fCB1qKYSVFeGfb017XnGHOnSlNvik3abHg

