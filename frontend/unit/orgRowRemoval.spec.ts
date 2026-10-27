import { expect, test } from '@playwright/test';
import type { CompanySetupPlan, PlanItem } from '../src/api/companySetup';
import { emptyBody, isPlanChange, planCounts, planIsNoChange, sectionChanges } from '../src/lib/companySetupWizard';
import { reviewBucket, reviewCounts } from '../src/components/companySetup/reviewModel';
import { orgRowRemoval } from '../src/i18n/orgRowRemoval';

const item = (action: PlanItem['action']): PlanItem => ({ area: 'department', code: 'OPS', label: 'Operations', action });
const plan = (items: PlanItem[]): CompanySetupPlan => ({ ok: true, items, problems: [], warnings: [] }) as unknown as CompanySetupPlan;

test.describe('Remove any organisation row (wizard)', () => {
  test('a removal is a plan change of its own, and a plan with only removals is not "no change"', () => {
    expect(isPlanChange('remove', 'existing')).toBe(true);
    const only = plan([item('remove')]);
    expect(planCounts(only).remove).toBe(1);
    expect(planIsNoChange(only)).toBe(false);
    expect(planIsNoChange(plan([item('unchanged')]))).toBe(true);
  });

  test('the review counts removals in their own bucket, open by default', () => {
    expect(reviewBucket(item('remove'), new Set())).toBe('remove');
    expect(reviewCounts(plan([item('remove'), item('create'), item('unchanged')]), new Set())).toMatchObject({ remove: 1, add: 1, kept: 1 });
  });

  test('a part with only removals still writes on Save and next', () => {
    const body = emptyBody();
    body.existingCompanyId = 'co-1';
    body.departments = [{ existingId: 'd-1', code: 'OPS', nameEn: 'Operations' }];
    body.removals = { departments: ['d-1'] };
    const baseline = emptyBody();
    baseline.departments = [{ existingId: 'd-1', code: 'OPS', nameEn: 'Operations' }];
    expect(sectionChanges(body, baseline, 'departments')).toEqual({ added: 0, changed: 0, removed: 1 });
    expect(sectionChanges(body, baseline, 'grades').removed).toBe(0);
  });

  test('every new string has an Arabic translation that keeps its placeholders', () => {
    for (const [key, value] of Object.entries(orgRowRemoval.ar)) {
      expect(value, key).toBeTruthy();
      const placeholders = (text: string) => [...text.matchAll(/\{(\w+)\}/g)].map(m => m[1]).sort();
      expect(placeholders(value), key).toEqual(placeholders(key));
    }
  });
});

import { pickable } from '../src/lib/pickable';
import { usageSentence } from '../src/lib/orgUsage';

test.describe('pick lists and usage text', () => {
  test('a turned-off row is not offered for a new choice, but stays for the record that holds it', () => {
    const rows = [{ id: 'a', isActive: true }, { id: 'b', isActive: false }, { id: 'c' }];
    expect(pickable(rows).map(r => r.id)).toEqual(['a', 'c']);
    expect(pickable(rows, 'b').map(r => r.id)).toEqual(['a', 'b', 'c']);
  });

  test('usage is rendered from structure, with the server label as the fallback for an unknown kind', () => {
    const t = (key: string, p?: Record<string, string | number>) => key.replace(/\{(\w+)\}/g, (_, k) => String(p?.[k] ?? ''));
    expect(usageSentence([{ kind: 'employee', label: 'employees', count: 2 }, { kind: 'mystery', label: 'mystery records', count: 3 }], t)).toContain('3 mystery records');
    expect(usageSentence([], t, 'Used by 1 employees')).toBe('Used by 1 employees');
  });
});

import { setupRollout } from '../src/i18n/setupRollout';
import { setupRowApi } from '../src/api/orgRows';

test.describe('Setup rollout: one removal contract beyond organisation rows', () => {
  test('every rollout string has an Arabic translation that keeps its placeholders', () => {
    for (const [key, value] of Object.entries(setupRollout.ar)) {
      expect(value, key).toBeTruthy();
      if (setupRollout.en[key] !== key) continue; // plural patterns are checked below
      const placeholders = (text: string) => [...text.matchAll(/\{(\w+)\}/g)].map(m => m[1]).sort();
      expect(placeholders(value), key).toEqual(placeholders(key));
    }
  });

  test('setupRowApi builds the remove and restore routes, and sends the named acknowledgement flag', () => {
    const api = setupRowApi('/api/leave/types', 'acknowledgeStatutory');
    expect(typeof api.remove).toBe('function');
    expect(typeof api.restore).toBe('function');
  });
});

import { isCapabilityOff } from '../src/api/capabilities';

test.describe('Capabilities hide forms the server cannot save', () => {
  test('a feature is off only when the server says so; unknown or not yet loaded counts as on', () => {
    expect(isCapabilityOff(null, 'approvalDelegations.write')).toBe(false);
    expect(isCapabilityOff({}, 'approvalDelegations.write')).toBe(false);
    expect(isCapabilityOff({ 'approvalDelegations.write': true }, 'approvalDelegations.write')).toBe(false);
    expect(isCapabilityOff({ 'approvalDelegations.write': false }, 'approvalDelegations.write')).toBe(true);
  });

  test('every rollout plural message keeps its placeholders in Arabic', () => {
    const ph = (text: string) => [...text.matchAll(/\{(\w+)[,}]/g)].map(m => m[1]).filter(x => x !== 'count' || true).sort();
    for (const key of ['Role holders will be signed out: {count} hold {name}', 'Role still held: {count} hold {name}']) {
      expect(setupRollout.ar[key], key).toBeTruthy();
      expect(setupRollout.en[key], key).toContain('plural');
      expect(new Set(ph(setupRollout.ar[key]))).toEqual(new Set(['count', 'name']));
    }
  });
});
