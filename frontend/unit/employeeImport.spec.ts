import { expect, test } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import {
  LOCAL_FIELD_CATALOG, complianceEditFieldsForCountry, complianceProfileForCountry, resolveFieldCatalog, unwrapFieldCatalog,
} from '../src/api/employeeFieldCatalog';
import type { EmployeeImportPreview } from '../src/api/employees';

/**
 * Specs fed from the backend's REAL responses. The fixture is written and verified by the backend test
 * EmployeeImportPilotSafetyPostgresTests.ImportAndFieldCatalogResponses_MatchTheFixtureTheFrontendSpecsUse,
 * so if the API changes shape that test fails until the fixture (and these specs) are refreshed together.
 */
const fixtures = JSON.parse(fs.readFileSync(path.join(__dirname, 'fixtures', 'employeeImportResponses.json'), 'utf8')) as Record<string, unknown>;

// ── F05: the field catalogue the server returns actually reaches the screen ─────────────────────────

test('the real field-catalog envelope unwraps to its fields (it used to be discarded as "not an array")', () => {
  const fields = unwrapFieldCatalog(fixtures.FieldCatalogSaIndian);
  expect(fields).not.toBeNull();
  expect(fields!.length).toBeGreaterThan(10);
  expect(Array.isArray(fixtures.FieldCatalogSaIndian)).toBe(false);
});

test('a Saudi national in KSA gets the server-resolved identity rows: Hawiyya, no Iqama, no work permit', () => {
  const catalog = resolveFieldCatalog(unwrapFieldCatalog(fixtures.FieldCatalogSaSaudi));
  expect(catalog).not.toBe(LOCAL_FIELD_CATALOG);
  const keys = complianceProfileForCountry(catalog, 'SA').map((f) => f.fieldKey);
  expect(keys).toContain('id_number');
  expect(keys).not.toContain('iqama_number');
  expect(keys).not.toContain('work_permit');
  // The offline fallback offers the superset — the proof the server's nationality axis is what was applied.
  const fallback = complianceProfileForCountry(LOCAL_FIELD_CATALOG, 'SA').map((f) => f.fieldKey);
  expect(fallback).toContain('iqama_number');
});

test('an expatriate in KSA gets Iqama (with its expiry) and a work permit, and no Hawiyya', () => {
  const catalog = resolveFieldCatalog(unwrapFieldCatalog(fixtures.FieldCatalogSaIndian));
  const profile = complianceProfileForCountry(catalog, 'SA');
  const iqama = profile.find((f) => f.fieldKey === 'iqama_number');
  expect(iqama?.expiryEntityKey).toBe('iqamaExpiryDate');
  expect(profile.map((f) => f.fieldKey)).toContain('work_permit');
  expect(profile.map((f) => f.fieldKey)).not.toContain('id_number');
});

// ── F04: an expiry input is never bound to an issue-date column ─────────────────────────────────────

test('no expiry input, server-resolved or offline, is bound to an issue-date column', () => {
  const catalogs = [
    LOCAL_FIELD_CATALOG,
    resolveFieldCatalog(unwrapFieldCatalog(fixtures.FieldCatalogSaSaudi)),
    resolveFieldCatalog(unwrapFieldCatalog(fixtures.FieldCatalogSaIndian)),
  ];
  for (const catalog of catalogs) {
    for (const country of ['SA', 'AE', 'QA', 'KW', 'OM', 'BH']) {
      for (const f of complianceProfileForCountry(catalog, country)) {
        if (f.expiryEntityKey) expect(f.expiryEntityKey, `${country} ${f.fieldKey}`).toMatch(/ExpiryDate$/);
      }
      for (const input of complianceEditFieldsForCountry(catalog, country)) {
        if (/expiry/i.test(input.label)) expect(input.key, `${country} "${input.label}"`).not.toMatch(/IssueDate$/i);
      }
    }
  }
});

// ── F01: the preview carries the commit's own verdict ───────────────────────────────────────────────

test('a refused file previews with the commit refusal, naming every bad row', () => {
  const preview = fixtures.PreviewRefused as EmployeeImportPreview;
  expect(preview.commitCheck?.outcome).toBe('would_refuse');
  expect(preview.commitCheck?.failedRows?.map((r) => r.row)).toEqual([3, 4]);
  expect(preview.rows.filter((r) => r.status === 'WillFail').map((r) => r.row)).toEqual([3, 4]);
  // The commit refuses with exactly the message the preview showed.
  expect((fixtures.ImportRefused as { message: string }).message).toBe(preview.commitCheck?.message);
});

test('a good file previews with the commit count it will actually produce', () => {
  const preview = fixtures.PreviewGood as EmployeeImportPreview;
  expect(preview.commitCheck).toEqual({ outcome: 'would_import', created: 2, repaired: 0, skipped: 0 });
  expect(preview.wouldCreate).toBe(preview.commitCheck?.created);
});

test('an unquoted thousands separator is refused naming the row and both cell counts', () => {
  const body = fixtures.ImportShapeRefused as { error: string; failedRows: Array<{ row: number; cells: number; expected: number }> };
  expect(body.error).toBe('csv_row_shape');
  expect(body.failedRows).toEqual([expect.objectContaining({ row: 2, cells: 9, expected: 8 })]);
});
