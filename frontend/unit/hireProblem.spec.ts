import { expect, test } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import { parseHireProblem } from '../src/lib/hire/problem';

const dir = path.resolve(__dirname, 'fixtures/hire-errors');
const fx = (name: string) => {
  const f = JSON.parse(fs.readFileSync(path.join(dir, `${name}.json`), 'utf8'));
  return { response: { status: f.status, data: f.body, headers: f.headers } };
};

test.describe('problem+json parsing', () => {
  test('400 validation_failed becomes per-field codes (never a banner string)', () => {
    const p = parseHireProblem(fx('validation_failed'));
    expect(p.kind).toBe('validation');
    if (p.kind !== 'validation') return;
    expect(p.fields.englishName).toEqual(['value_too_long']);
    expect(p.fields['salaryBreakdown.housingAllowance']).toEqual(['negative_amount']);
  });
  test('409 warnings_require_acknowledgement lists the warnings', () => {
    const p = parseHireProblem(fx('warnings_require_acknowledgement'));
    expect(p.kind).toBe('warnings');
    if (p.kind !== 'warnings') return;
    expect(p.warnings.map(w => w.code)).toEqual(['joining_backdated_over_30d', 'grade_band_out_of_range']);
  });
  test('409 possible_duplicate keeps the match set', () => {
    const p = parseHireProblem(fx('possible_duplicate'));
    expect(p.kind).toBe('duplicate');
    if (p.kind !== 'duplicate') return;
    expect(p.matches[0].employeeId).toBe(44);
  });
  test('429 carries Retry-After seconds', () => {
    const p = parseHireProblem(fx('rate_limited'));
    expect(p).toMatchObject({ kind: 'rate_limited', retryAfterSeconds: 17 });
  });
  test('draft_changed_elsewhere, idempotency mismatch, override reason, invalid reference', () => {
    expect(parseHireProblem(fx('draft_changed_elsewhere')).kind).toBe('draft_conflict');
    expect(parseHireProblem(fx('idempotency_key_reuse_mismatch')).kind).toBe('idempotency_mismatch');
    expect(parseHireProblem(fx('override_reason_required')).kind).toBe('override_reason_required');
    const ref = parseHireProblem(fx('invalid_reference'));
    expect(ref).toMatchObject({ kind: 'invalid_reference', field: 'departmentId' });
  });
  test('anything else is "other"; a network failure is "network"', () => {
    expect(parseHireProblem({ response: { status: 503, data: { message: 'x' } } }).kind).toBe('other');
    expect(parseHireProblem(new Error('Network Error')).kind).toBe('network');
  });
});
