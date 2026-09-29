-- =============================================================================
-- 014_statutory.sql
-- Domain E — Statutory reference (2 tables).
-- TARGET_SCHEMA.md revision 6 §2.E; CONVENTIONS.md §1–§10.
--
-- CREATE TABLE only. FKs/CHECKs/EXCLUDEs live in 020_constraints_a_f.sql.
-- Not partitioned (§19.3).
--
-- Both tables are tier R: platform reference data, seeded by 070_seed_reference.sql,
-- read-only to tenants and protected by WITHHOLDING the write grant rather than by a
-- tenant filter (RLS policy shape (c), §19.2). There are NO tenant overrides of a
-- statutory row: contractual enhancements live in company_pay_policies and the engine
-- applies max(statutory, contractual) (§2.E, §11.6).
--
-- Reference tables take the row-stamp exemption (§Conventions): created_at plus their
-- own actor columns (verified_by / verified_at), not the created_by/updated_* quartet.
-- =============================================================================

-- -----------------------------------------------------------------------------
-- statutory_rules — tier R. ONE effective-dated table for GOSI, EOS, overtime,
-- leave pay, Nitaqat weights and WPS parameters.
-- -----------------------------------------------------------------------------
CREATE TABLE statutory_rules (
    id                      uuid            NOT NULL,
    country_code            char(2)         NOT NULL,
    family                  varchar(40)     NOT NULL,
    rule_key                varchar(64)     NOT NULL,
    nationality_class       varchar(40)     NOT NULL DEFAULT 'Any',
    cohort                  varchar(40)     NOT NULL DEFAULT 'Any',
    gosi_branch             varchar(40),
    payer                   varchar(40),
    effective_from          date            NOT NULL,
    effective_to            date,
    rate                    numeric(9,6),
    wage_floor              numeric(18,2),
    wage_cap                numeric(18,2),
    value_json              jsonb,
    rules_version           varchar(40)     NOT NULL,
    source_reference        text,
    verified_by             uuid,
    verified_at             timestamptz,
    created_at              timestamptz     NOT NULL DEFAULT now(),
    CONSTRAINT pk_statutory_rules PRIMARY KEY (id),
    -- §2.E: UNIQUE (country, family, rule_key, dims, effective_from).
    -- NULLS NOT DISTINCT because gosi_branch and payer are NULL on the families that
    -- have no branch or payer dimension (EOS, Overtime, Leave), and two such rows with
    -- the same key would otherwise both be accepted.
    CONSTRAINT uq_statutory_rules__key UNIQUE NULLS NOT DISTINCT
        (country_code, family, rule_key, nationality_class, cohort, gosi_branch, payer, effective_from)
);
COMMENT ON TABLE statutory_rules IS
  'The single effective-dated source of every statutory rate and bound the payroll engine may apply — GOSI by cohort, branch and payer, EOS, overtime, leave pay, Nitaqat weights and WPS parameters — with the circular it came from and who verified it. @tier:R @owner:Compliance @retention:Keep';
COMMENT ON COLUMN statutory_rules.cohort IS
  '''Legacy'' (Saudis first insured before 3 July 2024), ''Entrant2024'' (on or after), or ''Any''. Resolved from employees.gosi_first_registered_on; an unknown cohort BLOCKS the slip and is never defaulted (§2.E). [COUNSEL] confirms the ladder.';
COMMENT ON COLUMN statutory_rules.rate IS
  'numeric(9,6): the Entrant2024 annuities ladder steps by 0.5 percentage points, so two decimals on a percentage has no headroom (CONVENTIONS.md §4). Stored as 0.090000, not 9.';
COMMENT ON COLUMN statutory_rules.rules_version IS
  'e.g. SA-GOSI-2025.07. Frozen onto every payroll_slip_line so a slip can be recomputed years later.';
COMMENT ON COLUMN statutory_rules.gosi_branch IS
  'Closed set, §9 row 37. NULL on the families that have no branch. trg_gosi_filing_totals pivots the filing on this value and only WARNs, so an unroutable branch is a silently short filing — hence the CHECK.';
COMMENT ON COLUMN statutory_rules.payer IS
  'Closed set, §9 row 38: Employee | Employer. NULL on the families that have no payer.';

-- -----------------------------------------------------------------------------
-- statutory_rule_bands — tier R. Band-shaped rules a single rate cannot express.
--
-- Bands use the OTHER range convention, deliberately: inclusive lower / EXCLUSIVE
-- upper, numrange(lower, upper, '[)'), because a continuous quantity has no "last
-- value". Dates are '[]'; numeric bands are '[)'. That is the whole rule
-- (CONVENTIONS.md §5).
--
-- Carries: EOS Art. 84, Art. 85 resignation fractions, Art. 117 sick pay tiers and
-- any wage-banded GOSI step.
-- -----------------------------------------------------------------------------
CREATE TABLE statutory_rule_bands (
    id                      uuid            NOT NULL,
    statutory_rule_id       uuid            NOT NULL,
    band_key                varchar(64)     NOT NULL,
    unit                    varchar(40)     NOT NULL,
    band_order              integer         NOT NULL DEFAULT 0,
    lower_bound             numeric         NOT NULL,
    upper_bound             numeric,
    rate                    numeric(9,6),
    amount                  numeric(18,2),
    value_json              jsonb,
    created_at              timestamptz     NOT NULL DEFAULT now(),
    CONSTRAINT pk_statutory_rule_bands PRIMARY KEY (id),
    -- [DESIGN GAP] §2.E names band_key but states no uniqueness. Added: a band_key that
    -- repeats inside one rule cannot be resolved by key.
    CONSTRAINT uq_statutory_rule_bands__rule_band_key UNIQUE (statutory_rule_id, band_key)
);
COMMENT ON TABLE statutory_rule_bands IS
  'The banded form of a statutory rule — service-year, sick-day or contributory-wage ranges each carrying their own rate or amount — with the database guaranteeing the bands of one rule never overlap. @tier:R @owner:Compliance @retention:Keep';
COMMENT ON COLUMN statutory_rule_bands.upper_bound IS
  'NULL = open-ended. The exclusion constraint reads it as numrange(lower, COALESCE(upper, ''infinity''), ''[)'') — inclusive lower, EXCLUSIVE upper, unlike the inclusive-inclusive date convention (§5).';
COMMENT ON COLUMN statutory_rule_bands.unit IS
  'ServiceYears | SickDays | ContributoryWage — what lower_bound and upper_bound are measured in.';
-- Nitaqat band thresholds are deliberately NOT here: they are keyed by activity x size
-- tier, which the statutory_rules dimension set does not carry, so they stay in the
-- typed nitaqat_grid (domain M) under the same band discipline (§2.E).
