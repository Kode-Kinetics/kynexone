import { expect, test } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import { formFromEmployee, normaliseDate } from '../src/lib/hire/draftHydrate';
import { isRealDate } from '../src/lib/hire/dates';
import { validateHire } from '../src/lib/hire/hireValidation';
import { buildCreateRequest } from '../src/lib/hire/payload';
import { createAutosave, type AutosaveState } from '../src/lib/hire/autosave';

// CD67: the draft is the REAL GET /api/employees/{id} body, captured by the backend test
// HireDraftResumeContractTests (REGEN_HIRE_FIXTURE=1 rewrites it). Nothing here is hand-typed API shape.
const real = JSON.parse(fs.readFileSync(path.resolve(__dirname, 'fixtures/hire-real/employee-draft.json'), 'utf8'));
const ctx = { emailDomain: 'example.com', companyCountry: 'SA' };

test.describe('resume from the real draft shape', () => {
  test('the nationality survives: restored from nationalityCode', () => {
    expect(formFromEmployee(real, ctx).nationalityCode).toBe('EG');
  });
  test('without a code (older record) the country name is resolved through the catalog, never blanked', () => {
    const f = formFromEmployee({ ...real, nationalityCode: null, nationality: 'Egypt' }, ctx);
    expect(f.nationalityCode).toBe('EG');
    expect(formFromEmployee({ ...real, nationalityCode: undefined, nationality: 'Pakistan' }, ctx).nationalityCode).toBe('PK');
    expect(formFromEmployee({ ...real, nationalityCode: undefined, nationality: 'Martian' }, ctx).nationalityCode).toBe('');
  });
  test('the start date is a YYYY-MM-DD the input and the validator accept', () => {
    expect(real.joiningDate).toMatch(/T.*Z$/);
    const f = formFromEmployee(real, ctx);
    expect(f.startDate).toBe('2026-11-01');
    expect(isRealDate(f.startDate)).toBe(true);
    expect(validateHire(f, { today: '2026-10-27', companyCountry: 'SA', emailDomain: 'example.com' } as never, 'draft').map(i => i.field)).not.toContain('startDate');
  });
  test('date-time values are cut in the company time zone, date-only values pass through', () => {
    expect(normaliseDate('2026-11-01T00:00:00Z', 'Asia/Riyadh')).toBe('2026-11-01');
    expect(normaliseDate('2026-10-31T22:30:00Z', 'Asia/Riyadh')).toBe('2026-11-01');   // 01:30 in Riyadh
    expect(normaliseDate('2026-10-31T22:30:00Z', 'UTC')).toBe('2026-10-31');
    expect(normaliseDate('2026-11-01T00:00:00', 'Asia/Riyadh')).toBe('2026-11-01');     // no offset: already a local calendar value
    expect(normaliseDate('2026-11-01', 'Asia/Riyadh')).toBe('2026-11-01');
    expect(normaliseDate(undefined, 'Asia/Riyadh')).toBe('');
    expect(normaliseDate('not a date', 'Asia/Riyadh')).toBe('');
  });
  test('the other date fields of the record are normalised too', () => {
    const f = formFromEmployee({ ...real, contractEndDate: '2027-11-01T00:00:00Z', probationEndDate: '2027-01-30T00:00:00Z', dateOfBirth: '1990-06-01T00:00:00Z' }, ctx);
    expect([f.contractEndDate, f.probationEndDate, f.dob]).toEqual(['2027-11-01', '2027-01-30', '1990-06-01']);
  });
  test('an autosave of the resumed form still carries the nationality', () => {
    const body = buildCreateRequest(formFromEmployee(real, ctx), { companyCountry: 'SA', currency: 'SAR', emailDomain: 'example.com' });
    expect(body.nationalityCode).toBe('EG');
    expect(body.joiningDate).toBe('2026-11-01');
  });
});

test.describe('different person', () => {
  test('confirming sends acknowledgeDuplicate together with the reason', () => {
    const body = buildCreateRequest({ ...formFromEmployee(real, ctx) }, { companyCountry: 'SA', currency: 'SAR' }, { duplicateOverrideReason: 'Different person, different date of birth' });
    expect(body.acknowledgeDuplicate).toBe(true);
    expect(body.duplicateOverrideReason).toBe('Different person, different date of birth');
  });
  test('without a reason nothing is acknowledged', () => {
    const body = buildCreateRequest(formFromEmployee(real, ctx), { companyCountry: 'SA', currency: 'SAR' });
    expect(body.acknowledgeDuplicate).toBeUndefined();
    expect(body.duplicateOverrideReason).toBeUndefined();
  });
});

const err = (status: number, code?: string, extra: object = {}) => ({ response: { status, data: { code, ...extra } } });

function harness(failures: unknown[]) {
  const timers: Array<{ id: number; due: number; fn: () => void }> = [];
  let clock = 0; let nextId = 1;
  const saves: string[] = []; const states: AutosaveState[] = []; const problems: string[] = [];
  const auto = createAutosave<string>({
    delayMs: 100,
    schedule: (fn, ms) => { const id = nextId++; timers.push({ id, due: clock + ms, fn }); return id; },
    cancel: id => { const i = timers.findIndex(t => t.id === id); if (i >= 0) timers.splice(i, 1); },
    save: async payload => { saves.push(payload); const f = failures.shift(); if (f) throw f; return { updatedAtUtc: `v${saves.length}` }; },
    onState: s => states.push(s),
    onProblem: p => problems.push(p.kind),
  });
  const advance = async (ms: number) => { clock += ms; for (const t of timers.filter(x => x.due <= clock)) { timers.splice(timers.indexOf(t), 1); t.fn(); } await new Promise(r => setTimeout(r, 0)); };
  return { auto, saves, states, problems, advance };
}

test.describe('autosave 409 by problem code', () => {
  test('draft_changed_elsewhere is the terminal conflict', async () => {
    const h = harness([err(409, 'draft_changed_elsewhere')]);
    h.auto.adopt('v0'); h.auto.markDirty('a'); await h.advance(100);
    expect(h.states.at(-1)).toBe('conflict');
    expect(h.auto.isConflict()).toBe(true);
    h.auto.markDirty('b'); await h.advance(200);
    expect(h.saves).toEqual(['a']);
  });
  test('warnings_require_acknowledgement is not a conflict: the warning is surfaced and autosave keeps working', async () => {
    const h = harness([err(409, 'warnings_require_acknowledgement', { warnings: [{ code: 'joining_backdated', field: 'joiningDate' }] })]);
    h.auto.adopt('v0'); h.auto.markDirty('a'); await h.advance(100);
    expect(h.auto.isConflict()).toBe(false);
    expect(h.problems).toEqual(['warnings']);
    expect(h.states.at(-1)).not.toBe('conflict');
    h.auto.markDirty('b'); await h.advance(100);
    expect(h.saves).toEqual(['a', 'b']);
    expect(h.states.at(-1)).toBe('saved');
  });
  test('any other 409 is a generic error, and autosave works again after the field is fixed', async () => {
    const h = harness([err(409, 'not_a_draft'), err(409)]);
    h.auto.adopt('v0'); h.auto.markDirty('a'); await h.advance(100);
    expect(h.auto.isConflict()).toBe(false);
    expect(h.states.at(-1)).toBe('error');
    expect(h.problems).toEqual(['other']);
    h.auto.markDirty('b'); await h.advance(100);
    expect(h.states.at(-1)).toBe('error');
    h.auto.markDirty('c'); await h.advance(100);
    expect(h.saves).toEqual(['a', 'b', 'c']);
    expect(h.states.at(-1)).toBe('saved');
  });
});
