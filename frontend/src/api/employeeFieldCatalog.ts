'use client';

import client from './client';
import type { EmployeeCreateRequest } from './employees';

/**
 * ── Employee field catalog — the SINGLE frontend source of truth ────────────────────────────────
 *
 * Employee Management has three surfaces that must stay in field-parity:
 *   (a) the create/edit modal, (b) the CSV import template + importer, (c) the DB entity.
 * ALL THREE derive from ONE backend source of truth — the EmployeeFieldRegistry, resolved on TWO
 * axes: COUNTRY (from the employing company / legal entity) × NATIONALITY (national vs expat, from
 * the person). The backend resolver decides which document/identity fields are VISIBLE and with what
 * correct local English label (Emirates ID, Iqama, Qatar ID/QID, Bahrain CPR, Kuwait Civil ID, Oman
 * Resident Card, …) for that (country, nationality) pair. The frontend fetches that resolved catalog
 * via GET /api/employees/field-catalog?companyId=…&countryCode=…&nationality=… and renders strictly
 * the fields it says are visible — so a Saudi national never sees an Iqama field, a UAE hire never
 * sees an Iqama, etc. The fetch is re-run whenever the company country or the employee nationality
 * changes.
 *
 * Requiredness for activation is NOT a client gate — it is resolved server-side per tenant/company
 * policy (the readiness floor) and surfaced through GET /{id}/readiness. `required`/`gate` here drive
 * labels/asterisks/hints only; Save→Activate→Pay stays server-authoritative.
 *
 * OFFLINE FALLBACK ONLY: `LOCAL_FIELD_CATALOG` / `COUNTRY_COMPLIANCE_PROFILES` below are a dumb
 * degraded fallback used ONLY when the backend endpoint 404s or fails (resolveFieldCatalog(null)).
 * They are NOT the source of truth and carry no nationality axis — a network failure degrades UX
 * (may show a superset of fields) but never changes the server gate. When the backend responds, its
 * nationality-resolved, visible field set REPLACES the local per-country list for the active country.
 */

export const GCC_COUNTRY_CODES = ['SA', 'AE', 'QA', 'KW', 'OM', 'BH'] as const;

/** Normalise a free-text / ISO-3 / long-form country into the GCC ISO-2 code the profiles key on. */
export function normalizeCountryCode(value?: string): string {
  const code = (value ?? '').trim().toUpperCase();
  const compact = code.replace(/[^A-Z]/g, '');
  if (['SAU', 'KSA', 'SAUDIARABIA', 'KINGDOMOFSAUDIARABIA'].includes(compact)) return 'SA';
  if (['ARE', 'UAE', 'UNITEDARABEMIRATES', 'UNITEDARABEMIRATE'].includes(compact)) return 'AE';
  if (['QAT', 'QATAR'].includes(compact)) return 'QA';
  if (['KWT', 'KUWAIT'].includes(compact)) return 'KW';
  if (['OMN', 'OMAN'].includes(compact)) return 'OM';
  if (['BHR', 'BAHRAIN'].includes(compact)) return 'BH';
  return code.length === 2 ? code : '';
}

export type FieldInputType = 'text' | 'email' | 'date' | 'number' | 'select' | 'toggle';

/** One editable field for the Edit-Details modal. Every key here has a working server PUT path. */
export interface EmployeeEditField {
  section: string;
  key: string;
  label: string;
  type?: FieldInputType;
  options?: string[];
  sensitive?: boolean;
}

/** One statutory/compliance field. Rendered as a value(+expiry) pair on create and as edit inputs. */
export interface EmployeeComplianceField {
  /** snake_case key the backend compliance-mirror recognises (e.g. "iqama_number"). */
  fieldKey: string;
  fieldLabel: string;
  /** Employee scalar / edit key the value mirrors to (e.g. "iqamaNumber"). */
  entityKey?: string;
  /** Employee scalar / edit key the expiry mirrors to, when a scalar column exists. */
  expiryEntityKey?: string;
  /** Advisory only (server gate is authoritative): true ⇒ show an asterisk / checklist hint. */
  required?: boolean;
  /** Advisory gate tier the field sits in: "activate" | "pay" | "recommended" (from the resolver). */
  gate?: string;
  /** Local identity-document format hint from the country pack (advisory validation only). */
  pattern?: string;
  patternHint?: string;
}

// ── Shared option constants — ONE source for both the create and edit selects ───────────────────
export const GENDER_OPTIONS = ['Male', 'Female', 'Other'];
export const MARITAL_STATUS_OPTIONS = ['Single', 'Married', 'Divorced', 'Widowed'];
export const EMPLOYMENT_TYPE_OPTIONS = ['Full-Time', 'Part-Time', 'Contractor', 'Intern'];
export const CONTRACT_TYPE_OPTIONS = ['Unlimited', 'Fixed-Term', 'Temporary'];
export const PAYMENT_METHOD_OPTIONS = ['BankTransfer', 'Cash', 'Cheque', 'WPS'];
export const SALARY_CURRENCY_OPTIONS = ['USD', 'GBP', 'EUR', 'AED', 'SAR', 'QAR', 'KWD', 'BHD', 'OMR'];

/**
 * Base (non-statutory) editable fields. Every key maps to a server ApplyChanges case, so no dead
 * inputs are rendered. Sensitive fields route to the Approval Center (202) server-side.
 */
export const BASE_EDIT_FIELDS: EmployeeEditField[] = [
  { section: 'Personal', key: 'englishName', label: 'English full name' },
  { section: 'Personal', key: 'arabicName', label: 'Arabic name' },
  { section: 'Personal', key: 'preferredName', label: 'Preferred name' },
  { section: 'Personal', key: 'gender', label: 'Gender', type: 'select', options: GENDER_OPTIONS },
  { section: 'Personal', key: 'nationality', label: 'Nationality' },
  { section: 'Personal', key: 'maritalStatus', label: 'Marital status', type: 'select', options: MARITAL_STATUS_OPTIONS },
  { section: 'Personal', key: 'dateOfBirth', label: 'Date of birth', type: 'date', sensitive: true },
  { section: 'Personal', key: 'personalEmail', label: 'Personal email', type: 'email' },
  { section: 'Personal', key: 'workEmail', label: 'Work email', type: 'email' },
  { section: 'Personal', key: 'phone', label: 'Mobile number' },
  { section: 'Personal', key: 'emergencyContactName', label: 'Emergency contact name' },
  { section: 'Personal', key: 'emergencyContactPhone', label: 'Emergency contact phone' },
  { section: 'Employment', key: 'department', label: 'Department' },
  { section: 'Employment', key: 'designation', label: 'Designation' },
  { section: 'Employment', key: 'jobTitle', label: 'Job title' },
  { section: 'Employment', key: 'employmentType', label: 'Employment type', type: 'select', options: EMPLOYMENT_TYPE_OPTIONS },
  { section: 'Employment', key: 'contractType', label: 'Contract type', type: 'select', options: CONTRACT_TYPE_OPTIONS },
  { section: 'Employment', key: 'workLocation', label: 'Work location' },
  { section: 'Employment', key: 'grade', label: 'Grade' },
  { section: 'Employment', key: 'costCenter', label: 'Cost center' },
  { section: 'Employment', key: 'joiningDate', label: 'Joining date', type: 'date' },
  { section: 'Employment', key: 'managerEmployeeId', label: 'Manager employee ID', type: 'number' },
  { section: 'Payroll & Banking', key: 'salary', label: 'Salary', type: 'number', sensitive: true },
  { section: 'Payroll & Banking', key: 'bankName', label: 'Bank name', sensitive: true },
  { section: 'Payroll & Banking', key: 'bankIban', label: 'IBAN', sensitive: true },
];

// Passport is common to every GCC jurisdiction.
const COMMON_COMPLIANCE_FIELDS: EmployeeComplianceField[] = [
  { fieldKey: 'passport_number', fieldLabel: 'Passport Number', entityKey: 'passportNumber', expiryEntityKey: 'passportExpiryDate', required: true },
];

/**
 * Per-country statutory profile — OFFLINE FALLBACK ONLY (see the module header). This has no
 * nationality axis: it deliberately offers the SUPERSET of a country's identity fields (both the
 * national-ID and the expat residence-permit rows) so that, when the backend resolver is
 * unreachable, no field a user might legitimately need is missing. NOTE the SA split: `iqama_number`
 * is the expat residence permit, `id_number` is the Saudi-national ID (Hawiyya). Local ID names are
 * used per §D (Emirates ID / QID / Kuwait Civil ID / Oman Resident Card / Bahrain CPR). Card-expiry
 * rows carry `expiryEntityKey` so the *Expiry scalar columns are capturable even in fallback mode.
 * The correct nationality-aware hiding is done server-side and REPLACES this for the active country
 * whenever the endpoint responds.
 *
 * INVARIANT — `expiryEntityKey` must name a real `*ExpiryDate` scalar and must equal the registry's
 * `ExpiryKey` (camelCased) for the same statutory row. It previously named `workPermitIssueDate` in
 * all six profiles and `residencyIssueDate` in KW/OM: ISSUE-date columns.
 * `complianceEditFieldsForCountry` below labels that second input `"<field> expiry"` and
 * `ApplyChanges` stores whatever key it is handed, so a permit expiring 2027-03-01 was persisted as
 * *issued* 2027-03-01, the expiry column stayed empty, and no renewal alert could ever fire.
 * `EmployeeFieldRegistry` declares no `ExpiryKey` for `WorkPermitNumber`/`ResidencyNumber` (no such
 * column exists on `Employee`), so the server already sent `expiryEntityKey: null` for both — the
 * fallback invented the binding. Guarded by `EmployeeFieldWiringTests`.
 */
const COUNTRY_COMPLIANCE_PROFILES: Record<string, EmployeeComplianceField[]> = {
  SA: [
    ...COMMON_COMPLIANCE_FIELDS,
    { fieldKey: 'id_number', fieldLabel: 'National ID (Hawiyya)', entityKey: 'idNumber' },
    { fieldKey: 'iqama_number', fieldLabel: 'Iqama (residence permit)', entityKey: 'iqamaNumber', expiryEntityKey: 'iqamaExpiryDate' },
    { fieldKey: 'muqeem_reference', fieldLabel: 'Muqeem Reference', entityKey: 'muqeemNumber' },
    { fieldKey: 'gosi_reference', fieldLabel: 'GOSI Reference', entityKey: 'gosiReference' },
    { fieldKey: 'qiwa_contract_reference', fieldLabel: 'Qiwa Contract Number', entityKey: 'qiwaContractNumber' },
    { fieldKey: 'work_permit', fieldLabel: 'Work Permit Number', entityKey: 'workPermitNumber' },
  ],
  AE: [
    ...COMMON_COMPLIANCE_FIELDS,
    { fieldKey: 'emirates_id', fieldLabel: 'Emirates ID', entityKey: 'emiratesId', expiryEntityKey: 'emiratesIdExpiryDate', required: true },
    { fieldKey: 'work_permit', fieldLabel: 'MOHRE work permit / labour card no.', entityKey: 'workPermitNumber' },
    { fieldKey: 'visa_number', fieldLabel: 'Residence visa number', entityKey: 'visaNumber', expiryEntityKey: 'visaExpiryDate' },
    { fieldKey: 'visa_file_number', fieldLabel: 'Visa File Number', entityKey: 'visaFileNumber' },
    { fieldKey: 'labor_card_number', fieldLabel: 'Labour Card Number', entityKey: 'laborCardNumber' },
  ],
  QA: [
    ...COMMON_COMPLIANCE_FIELDS,
    { fieldKey: 'qid', fieldLabel: 'Qatar ID (QID)', entityKey: 'qid', expiryEntityKey: 'qidExpiryDate', required: true },
    { fieldKey: 'visa_number', fieldLabel: 'Residence permit number', entityKey: 'visaNumber', expiryEntityKey: 'visaExpiryDate' },
    { fieldKey: 'work_permit', fieldLabel: 'Work Permit Number', entityKey: 'workPermitNumber' },
  ],
  KW: [
    ...COMMON_COMPLIANCE_FIELDS,
    { fieldKey: 'civil_id', fieldLabel: 'Kuwait Civil ID', entityKey: 'civilId', expiryEntityKey: 'civilIdExpiryDate', required: true },
    { fieldKey: 'residency_number', fieldLabel: 'Residency (Article) number', entityKey: 'residencyNumber' },
    { fieldKey: 'work_permit', fieldLabel: 'Work Permit Number', entityKey: 'workPermitNumber' },
  ],
  OM: [
    ...COMMON_COMPLIANCE_FIELDS,
    { fieldKey: 'civil_id', fieldLabel: 'Oman Resident Card / Civil ID', entityKey: 'civilId', expiryEntityKey: 'civilIdExpiryDate', required: true },
    { fieldKey: 'residency_number', fieldLabel: 'Residency Number', entityKey: 'residencyNumber' },
    { fieldKey: 'work_permit', fieldLabel: 'Work Permit Number', entityKey: 'workPermitNumber' },
  ],
  BH: [
    ...COMMON_COMPLIANCE_FIELDS,
    { fieldKey: 'civil_id', fieldLabel: 'Bahrain CPR (Personal no.)', entityKey: 'civilId', expiryEntityKey: 'civilIdExpiryDate', required: true },
    { fieldKey: 'work_permit', fieldLabel: 'LMRA work permit number', entityKey: 'workPermitNumber' },
  ],
};

/** The resolved catalog the UI reads from (local defaults, optionally overlaid by the backend registry). */
export interface ResolvedFieldCatalog {
  editFields: EmployeeEditField[];
  commonCompliance: EmployeeComplianceField[];
  countryCompliance: Record<string, EmployeeComplianceField[]>;
}

export const LOCAL_FIELD_CATALOG: ResolvedFieldCatalog = {
  editFields: BASE_EDIT_FIELDS,
  commonCompliance: COMMON_COMPLIANCE_FIELDS,
  countryCompliance: COUNTRY_COMPLIANCE_PROFILES,
};

// ── Country-scoped selectors (all take the resolved catalog, so backend overlays flow through) ───

export function complianceProfileForCountry(catalog: ResolvedFieldCatalog, countryCode?: string): EmployeeComplianceField[] {
  const country = normalizeCountryCode(countryCode);
  return catalog.countryCompliance[country] ?? catalog.commonCompliance;
}

/** Build the compliance-record seeds for the create form, preserving any values already entered. */
export function complianceRecordsForCountry(
  catalog: ResolvedFieldCatalog,
  countryCode: string | undefined,
  existing: EmployeeCreateRequest['complianceRecords'] = [],
): NonNullable<EmployeeCreateRequest['complianceRecords']> {
  const country = normalizeCountryCode(countryCode);
  if (!country) return [];
  const existingByKey = new Map((existing ?? []).map((record) => [record.fieldKey, record]));
  return complianceProfileForCountry(catalog, country).map((field) => {
    const current = existingByKey.get(field.fieldKey);
    return {
      countryCode: country,
      fieldKey: field.fieldKey,
      fieldLabel: field.fieldLabel,
      fieldValue: current?.fieldValue ?? '',
      issueDate: current?.issueDate,
      expiryDate: current?.expiryDate,
      isSensitive: true,
      isRequired: Boolean(field.required),
    };
  });
}

/** Edit-modal inputs for the statutory fields (value, plus expiry where a scalar column exists). */
export function complianceEditFieldsForCountry(catalog: ResolvedFieldCatalog, countryCode?: string): EmployeeEditField[] {
  return complianceProfileForCountry(catalog, countryCode).flatMap((field) => {
    const fields: EmployeeEditField[] = [];
    if (field.entityKey) fields.push({ section: 'Compliance Documents', key: field.entityKey, label: field.fieldLabel, sensitive: true });
    if (field.expiryEntityKey) fields.push({ section: 'Compliance Documents', key: field.expiryEntityKey, label: `${field.fieldLabel} expiry`, type: 'date', sensitive: true });
    return fields;
  });
}

/** The complete edit-modal field list for a country (base fields + statutory fields). */
export function editFieldsForCountry(catalog: ResolvedFieldCatalog, countryCode?: string): EmployeeEditField[] {
  return [...catalog.editFields, ...complianceEditFieldsForCountry(catalog, countryCode)];
}

/**
 * Non-statutory document types every employee file carries, offered after the country's own
 * statutory identity documents.
 *
 * The three certificates are not decoration. In every GCC state the residence-permit and work-permit
 * files are assembled from them: a pre-employment MEDICAL fitness certificate, an attested
 * EDUCATIONAL certificate, and an EXPERIENCE certificate from the previous employer. They were
 * missing from this list, so the only way to file one was to pick a type that was not what the
 * document was — which then reads back wrong on the compliance and expiry screens that group by
 * document type. `DocType.Category` already names `Certificate` as a first-class category and this
 * product's own HR-letter module issues an Experience Certificate; the upload list simply never
 * caught up. The backend stores DocumentType as free text (max 80), so nothing here is a new
 * contract — it is the vocabulary a user can actually reach.
 */
const GENERAL_DOCUMENT_TYPES = [
  'Contract',
  'Offer letter',
  'NDA',
  'Policy acknowledgment',
  'Medical certificate',
  'Educational certificate',
  'Experience certificate',
];

export function documentTypesForCountry(catalog: ResolvedFieldCatalog, countryCode?: string): string[] {
  const statutory = complianceProfileForCountry(catalog, countryCode).map((field) => field.fieldLabel);
  return [...new Set([...statutory, ...GENERAL_DOCUMENT_TYPES])];
}

// ── Backend registry hydration (progressive enhancement) ─────────────────────────────────────────

/**
 * Wire contract for GET /api/employees/field-catalog — the server-side EmployeeFieldRegistry resolved
 * for one (country, nationality) pair. The resolver joins catalog SHAPE × readiness floor
 * REQUIREDNESS × country-pack FORMAT and returns, per field, whether it is `visible` for that pair
 * (the nationality axis), its country-resolved local `label`, the advisory `required`/`gate`, and the
 * identity-document `pattern`/`patternHint`. Every property except `key` is optional so the endpoint
 * can grow without breaking the client.
 */
export interface RemoteFieldDescriptor {
  key: string;
  label?: string;
  /** identity | personal | employment | organization | payroll | salary | qiwa | compliance */
  section?: string;
  inputType?: FieldInputType;
  options?: string[];
  sensitive?: boolean;
  csvHeader?: string | null;
  /** Set for statutory rows: the snake_case compliance-mirror key. */
  complianceFieldKey?: string | null;
  entityKey?: string | null;
  expiryEntityKey?: string | null;
  /** GCC ISO-2 country applicability; null/empty = all countries. */
  countries?: string[] | null;
  /**
   * Nationality axis: normalized nationalities (ISO-2) the field applies to; null/empty = all.
   * Informational — the resolved `visible` flag already encodes the nationality decision.
   */
  nationalities?: string[] | null;
  editable?: boolean;
  required?: boolean;
  /**
   * The nationality-resolved visibility for the requested pair. When the resolver runs the two-axis
   * join it sets this explicitly; treated as visible when omitted (a thinner endpoint that only lists
   * applicable rows). Rows with visible===false are soft-hidden from the modal.
   */
  visible?: boolean;
  /** Advisory readiness tier: "activate" | "pay" | "recommended". Never a client gate. */
  gate?: string | null;
  /** Identity-document format from the country pack (advisory validation / input hint only). */
  pattern?: string | null;
  patternHint?: string | null;
}

export interface FieldCatalogQuery {
  companyId?: string;
  /** GCC ISO-2 (or free-text; the server normalizes). Wins over the company's country when set. */
  countryCode?: string;
  /** The person's nationality — the second resolution axis (national vs expat). */
  nationality?: string;
}

export const employeeFieldCatalogApi = {
  /**
   * Registry-driven, two-axis (country × nationality) field metadata. Resolves to null (not an error)
   * when the endpoint is unavailable, so callers transparently fall back to LOCAL_FIELD_CATALOG.
   */
  get: (params?: FieldCatalogQuery) =>
    client
      .get<FieldCatalogResponse | RemoteFieldDescriptor[]>('/api/employees/field-catalog', {
        params: params
          ? {
              companyId: params.companyId || undefined,
              countryCode: params.countryCode || undefined,
              nationality: params.nationality || undefined,
            }
          : undefined,
      })
      .then((r) => unwrapFieldCatalog(r.data))
      .catch(() => null),
};

/**
 * The endpoint's response envelope. `GET /api/employees/field-catalog` returns an OBJECT carrying the
 * resolved axes plus the descriptor list under `fields` — never a bare array.
 *
 * This client used to array-check the raw response body and discard anything that was not an array —
 * which is every object the endpoint has ever returned — so it always resolved to `null` and
 * `resolveFieldCatalog(null)` returned `LOCAL_FIELD_CATALOG` 100% of the time: the entire
 * server-side country x nationality resolver, the per-country labels, the format regexes and the
 * nationality hiding never reached a screen, and the hard-coded fallback (with the issue-date expiry
 * bug above) was what every user got. `EmployeeFieldWiringTests.FieldCatalog_Envelope_*` pins the
 * property name on the server side so the two cannot diverge again silently.
 */
export interface FieldCatalogResponse {
  countryCode?: string | null;
  nationality?: string | null;
  tier?: string | null;
  disclaimer?: string | null;
  fields?: RemoteFieldDescriptor[] | null;
}

/** The property on the response envelope that carries the descriptor list. Pinned by a contract test. */
export const FIELD_CATALOG_ENVELOPE_KEY = 'fields' as const;

/**
 * Read the descriptor list out of whatever the endpoint returned. Accepts the envelope (current
 * contract) and a bare array (older/never-shipped shape) so a redeploy ordering can never blank the
 * catalogue; anything else degrades to `null` → `LOCAL_FIELD_CATALOG`, exactly as a network failure does.
 */
export function unwrapFieldCatalog(data: unknown): RemoteFieldDescriptor[] | null {
  if (Array.isArray(data)) return data as RemoteFieldDescriptor[];
  if (data && typeof data === 'object') {
    const fields = (data as Record<string, unknown>)[FIELD_CATALOG_ENVELOPE_KEY];
    if (Array.isArray(fields)) return fields as RemoteFieldDescriptor[];
  }
  return null;
}

/** True when a remote descriptor is a statutory/identity (compliance) row. */
function isComplianceRemote(d: RemoteFieldDescriptor): boolean {
  return !!d.complianceFieldKey;
}

/** Map a remote statutory descriptor to the frontend compliance-field shape. */
function toComplianceField(d: RemoteFieldDescriptor): EmployeeComplianceField {
  return {
    fieldKey: d.complianceFieldKey!,
    fieldLabel: d.label ?? d.key,
    entityKey: d.entityKey ?? undefined,
    expiryEntityKey: d.expiryEntityKey ?? undefined,
    required: d.required ?? false,
    gate: d.gate ?? undefined,
    pattern: d.pattern ?? undefined,
    patternHint: d.patternHint ?? undefined,
  };
}

/**
 * Resolve the field catalog the UI renders from. The backend response is the authoritative source of
 * truth; the local defaults are only a degraded fallback.
 *
 *  - Passing null/empty (endpoint down) returns LOCAL_FIELD_CATALOG unchanged.
 *  - Edit-field metadata (labels/types/sensitivity) is overlaid from the non-compliance remote rows.
 *  - Compliance is REMOTE-AUTHORITATIVE per country: for any country the response resolves (i.e. it
 *    carries a country-specific statutory row for it — which the active-axis fetch always does), the
 *    country's compliance list becomes EXACTLY the remote rows that are `visible`, in remote order.
 *    This is what makes the nationality axis real — the server's decision to hide a field (e.g. Iqama
 *    for a Saudi national) is honoured verbatim; the local superset can never leak a hidden field
 *    back in. Countries the response does not resolve keep the local fallback (they are not the
 *    active jurisdiction and the modal never reads them).
 */
/**
 * The registry's `InputType` vocabulary is wider than the modal's: it includes `lookup`, which names a
 * picker the edit modal does not render. The modal does `<input type={f.type ?? 'text'} />`, so an
 * unfiltered overlay would put `type="lookup"` on the Department, Designation and Grade inputs — an
 * invalid HTML input type. Browsers do fall back to a text box, so it is not a data defect, but it is
 * invalid markup that only appeared once the catalogue started reaching the UI at all. Anything the modal
 * cannot render keeps the local type.
 */
function renderableInputType(inputType?: string | null): FieldInputType | undefined {
  const renderable: FieldInputType[] = ['text', 'email', 'date', 'number', 'select', 'toggle'];
  return renderable.includes(inputType as FieldInputType) ? (inputType as FieldInputType) : undefined;
}

/**
 * The overlaid input type, refusing an OPTIONLESS SELECT.
 *
 * `EmployeeFieldRegistry` declares `Nationality`, `CountryCode`, `Status`, `Currency`, `PaymentMethod`,
 * `SaudiOrNonSaudi` and `IdType` as `"select"`, but `EmployeeFieldDescriptor` carries no option list and
 * the endpoint therefore sends none — the local `BASE_EDIT_FIELDS` entry is the only source of options
 * there has ever been. Overlaying the remote type blindly turned `nationality` (a local TEXT field with
 * no options) into a `<select>` whose option list was `undefined`, and the modal's `f.options.map(...)`
 * threw during EmployeesPage's render. Because modal children are built eagerly by the parent, that took
 * the ENTIRE People page down behind the error boundary — for every role, not just inside the modal.
 * The Chrome security gate caught it; nothing else did.
 *
 * It was invisible until the envelope fix above let the catalogue reach a screen for the first time. A
 * select whose options nobody can supply is not renderable, so the local type wins.
 */
function optionsAwareType(
  remoteInputType: string | null | undefined,
  localType: FieldInputType | undefined,
  options: string[] | undefined,
): FieldInputType | undefined {
  const remote = renderableInputType(remoteInputType);
  if (remote === 'select' && !(options && options.length > 0)) return localType;
  return remote ?? localType;
}

export function resolveFieldCatalog(remote: RemoteFieldDescriptor[] | null | undefined): ResolvedFieldCatalog {
  if (!remote || remote.length === 0) return LOCAL_FIELD_CATALOG;

  const byEditKey = new Map(remote.filter((d) => !isComplianceRemote(d)).map((d) => [d.key, d]));
  const editFields = LOCAL_FIELD_CATALOG.editFields.map((field) => {
    const r = byEditKey.get(field.key);
    if (!r) return field;
    const options = r.options ?? field.options;
    return {
      ...field,
      label: r.label ?? field.label,
      type: optionsAwareType(r.inputType, field.type, options),
      options,
      sensitive: r.sensitive ?? field.sensitive,
    };
  });

  const complianceRemote = remote.filter(isComplianceRemote);
  const countryCompliance: Record<string, EmployeeComplianceField[]> = {};
  for (const country of GCC_COUNTRY_CODES) {
    // Rows applicable to this country: country-specific ones plus universal (no `countries`) rows.
    const specific = complianceRemote.filter(
      (d) => d.countries && d.countries.length > 0 && d.countries.map(normalizeCountryCode).includes(country),
    );
    if (specific.length === 0) {
      // Response did not resolve this country — keep the offline fallback for it.
      countryCompliance[country] = LOCAL_FIELD_CATALOG.countryCompliance[country] ?? LOCAL_FIELD_CATALOG.commonCompliance;
      continue;
    }
    const universal = complianceRemote.filter((d) => !d.countries || d.countries.length === 0);
    // Remote-authoritative: exactly the visible rows for this (country, nationality) pair, in order.
    countryCompliance[country] = [...universal, ...specific]
      .filter((d) => d.visible !== false)
      .map(toComplianceField);
  }

  return { editFields, commonCompliance: LOCAL_FIELD_CATALOG.commonCompliance, countryCompliance };
}
