import fs from 'node:fs';
import path from 'node:path';
import { test, expect } from '@playwright/test';
import {
  CATALOGUE_LOAN_FACILITIES, GENERIC_LOAN_VALUE_TYPES, GRADE_LOAN_LIMITS, LOAN_TYPES, facilityCodeFor, loanCatalogueProblems,
} from '../e2e/release-a/demo-seed.data';

/**
 * The Masar demo seed's loan types against the Release A entitlement catalogue. The seed refuses to start when
 * loanCatalogueProblems() is not empty; these tests pin its inputs to the C# sources so the mirror cannot drift.
 */
const backend = (rel: string) => fs.readFileSync(path.join(__dirname, '../../backend-dotnet/Zayra.Api', rel), 'utf8');

test('the seed data has no loan catalogue problems', () => {
  expect(loanCatalogueProblems()).toEqual([]);
});

test('the housing advance lands on the catalogue facility, not the generic LOAN_HOUSING', () => {
  const advance = LOAN_TYPES.find((t) => t.nameEn === 'Housing Advance')!;
  expect(advance.code).toBe('HOUSING_ADVANCE');
  expect(facilityCodeFor(advance.code)).toBe('LOAN_HOUSING_ADVANCE');
  expect(advance.catalogued).toBe(true);
  // The storyline figures: 3x monthly housing as a multiple of basic (25% → 0.75, G5 30% → 0.9).
  const grid = GRADE_LOAN_LIMITS[advance.code];
  expect(grid.G1.eligible).toBe(false);
  expect([grid.G2, grid.G3, grid.G4].map((c) => [c.valueType, c.rate])).toEqual(Array(3).fill(['MultipleOfBasic', 0.75]));
  expect([grid.G5.valueType, grid.G5.rate]).toEqual(['MultipleOfBasic', 0.9]);
});

test('the old HOUSING code is refused before the seed writes anything', () => {
  const types = LOAN_TYPES.map((t) => (t.catalogued ? { ...t, code: 'HOUSING' } : t));
  const limits = { ...GRADE_LOAN_LIMITS, HOUSING: GRADE_LOAN_LIMITS.HOUSING_ADVANCE };
  delete (limits as Record<string, unknown>).HOUSING_ADVANCE;
  expect(loanCatalogueProblems(types, limits)).toEqual(['loan type HOUSING derives facility LOAN_HOUSING, not the declared LOAN_HOUSING_ADVANCE']);
});

test('a generic loan facility may not use a catalogue-only value type', () => {
  const limits = { ...GRADE_LOAN_LIMITS, PERSONAL: { ...GRADE_LOAN_LIMITS.PERSONAL, G3: { ...GRADE_LOAN_LIMITS.PERSONAL.G3, valueType: 'MultipleOfHousing' } } };
  expect(loanCatalogueProblems(LOAN_TYPES, limits as never)).toEqual(['loan type PERSONAL G3: MultipleOfHousing is not allowed for LOAN_PERSONAL']);
});

test('facilityCodeFor is a faithful port of GradeLoanLimitResolver.FacilityCodeFor', () => {
  const source = backend('Infrastructure/Finance/GradeLoanLimitResolver.cs');
  expect(source).toContain('public static string FacilityCodeFor(string loanTypeCode)');
  expect(source).toContain(`var code = "LOAN_" + (cleaned.Length == 0 ? "TYPE" : cleaned);`);
  expect(source).toContain('return code.Length > 64 ? code[..64] : code;');
  expect(facilityCodeFor('HOUSING')).toBe('LOAN_HOUSING');
  expect(facilityCodeFor(' housing-advance ')).toBe('LOAN_HOUSING_ADVANCE');
  expect(facilityCodeFor('per sonal')).toBe('LOAN_PER_SONAL');
  expect(facilityCodeFor('--')).toBe('LOAN_TYPE');
  expect(facilityCodeFor('X'.repeat(80))).toHaveLength(64);
});

test('the pinned catalogue matches EntitlementComponentRules.Catalogue', () => {
  const source = backend('Infrastructure/Entitlements/EntitlementComponentRules.cs');
  // Constants declared as LOAN_* codes (LoanPrefix itself is "LOAN_", not a facility).
  const constants = Object.fromEntries([...source.matchAll(/public const string (\w+) = "(LOAN_\w+)";/g)].map((m) => [m[1], m[2]]));
  // Catalogue entries whose code is one of those constants are the catalogue's own loan facilities.
  const catalogue = source.slice(source.indexOf('public static readonly IReadOnlyList<EntitlementComponentRule> Catalogue'), source.indexOf('private static readonly Dictionary'));
  const facilities = Object.entries(constants).filter(([name]) => new RegExp(`new\\(${name},`).test(catalogue)).map(([, code]) => code);
  expect(facilities.sort()).toEqual(Object.keys(CATALOGUE_LOAN_FACILITIES).sort());
  for (const code of facilities) {
    const name = Object.entries(constants).find(([, c]) => c === code)![0];
    const entry = catalogue.slice(catalogue.indexOf(`new(${name},`));
    const types = entry.slice(0, entry.indexOf(']')).match(/GradeEntitlementValueTypes\.(\w+)/g)!.map((x) => x.split('.')[1]);
    expect(types.sort()).toEqual([...CATALOGUE_LOAN_FACILITIES[code]].sort());
  }
  // The generic fallback in For(): every other LOAN_* code.
  const fallback = source.slice(source.indexOf('public static EntitlementComponentRule? For(string code)'));
  const generic = fallback.slice(0, fallback.indexOf(']')).match(/GradeEntitlementValueTypes\.(\w+)/g)!.map((x) => x.split('.')[1]);
  expect(generic.sort()).toEqual([...GENERIC_LOAN_VALUE_TYPES].sort());
});
