-- STUB ONLY. Minimal stand-ins for the domain A-F tables the second engineer
-- owns, carrying just the keys the G-R foreign keys target. Never applied to a
-- real database; it exists so 021_constraints_g_r.sql can be validated before
-- 010-015 land.
CREATE EXTENSION IF NOT EXISTS btree_gist;

CREATE TABLE tenants (
    id uuid PRIMARY KEY
);

CREATE TABLE users (
    id uuid PRIMARY KEY,
    tenant_id uuid NOT NULL REFERENCES tenants (id),
    UNIQUE (tenant_id, id)
);

CREATE TABLE companies (
    id uuid PRIMARY KEY,
    tenant_id uuid NOT NULL REFERENCES tenants (id),
    gosi_registration_no varchar(20),
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, gosi_registration_no)
);

CREATE TABLE employees (
    id uuid PRIMARY KEY,
    tenant_id uuid NOT NULL REFERENCES tenants (id),
    UNIQUE (tenant_id, id)
);

CREATE TABLE files (
    id uuid PRIMARY KEY,
    tenant_id uuid NOT NULL REFERENCES tenants (id),
    UNIQUE (tenant_id, id)
);

CREATE TABLE branches (
    id uuid PRIMARY KEY,
    tenant_id uuid NOT NULL REFERENCES tenants (id),
    UNIQUE (tenant_id, id)
);

CREATE TABLE cost_centers (
    id uuid PRIMARY KEY,
    tenant_id uuid NOT NULL REFERENCES tenants (id),
    UNIQUE (tenant_id, id)
);

CREATE TABLE payroll_runs (
    id uuid PRIMARY KEY,
    tenant_id uuid NOT NULL REFERENCES tenants (id),
    UNIQUE (tenant_id, id)
);

CREATE TABLE payroll_slips (
    id uuid PRIMARY KEY,
    tenant_id uuid NOT NULL REFERENCES tenants (id),
    UNIQUE (tenant_id, id)
);

CREATE TABLE statutory_rules (
    id uuid PRIMARY KEY
);
