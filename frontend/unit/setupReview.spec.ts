import { expect, test } from '@playwright/test';
import type { CompanySetupDraftBody, CompanySetupPlan, PlanItem } from '../src/api/companySetup';
import { buildTree, emptyBody } from '../src/lib/companySetupWizard';
import { conflictNamesRow, draftValueLabel, isOpenBucket, reviewBucket, reviewCounts, savedInStep4 } from '../src/components/companySetup/reviewModel';

/** An existing company as the wizard opened it: one cost centre and one department it already had. */
function opened(): CompanySetupDraftBody {
  const body = emptyBody();
  body.existingCompanyId = 'co-1';
  body.costCentres = [{ existingId: 'cc-1', code: 'ADM', nameEn: 'Administration' }];
  body.departments = [{ existingId: 'd-1', code: 'HR', nameEn: 'Human Resources' }];
  return body;
}
const item = (area: PlanItem['area'], code: string, action: PlanItem['action'], reason?: string): PlanItem => ({ area, code, label: code, action, reason });

test.describe('Step 7 review model', () => {
  test('a Step 4 row saved live reads as saved; what the company already had stays "kept"', () => {
    const baseline = opened();
    const body = opened();
    // B2 saved the Finance department live: it now carries a company id it did not have when the wizard opened.
    body.departments.push({ existingId: 'd-2', code: 'fin ', nameEn: 'Finance' });
    const saved = savedInStep4(body, baseline);
    expect([...saved]).toEqual(['department:FIN']);
    expect(reviewBucket(item('department', 'FIN', 'unchanged'), saved)).toBe('saved');
    expect(reviewBucket(item('department', 'HR', 'unchanged'), saved)).toBe('kept');
    expect(reviewBucket(item('costCentre', 'ADM', 'unchanged'), saved)).toBe('kept');
  });

  test('the wizard can name whole sections as saved; a row with no company id never is', () => {
    const body = opened();
    body.costCentres.push({ code: 'NEW', nameEn: 'Not saved yet' });
    const saved = savedInStep4(body, opened(), ['costCentres']);
    expect(saved.has('costCentre:ADM')).toBe(true);
    expect(saved.has('costCentre:NEW')).toBe(false);
    expect(saved.has('department:HR')).toBe(false);
  });

  test('only unchanged Organisation rows can be "saved"; every action lands in one bucket', () => {
    const saved = new Set(['branch:HQ', 'department:FIN']);
    expect(reviewBucket(item('branch', 'HQ', 'unchanged'), saved)).toBe('kept');
    expect(reviewBucket(item('department', 'FIN', 'update'), saved)).toBe('update');
    expect(reviewBucket(item('department', 'X', 'create'), saved)).toBe('add');
    expect(reviewBucket(item('grade', 'G1', 'reuse'), saved)).toBe('kept');
    expect(reviewBucket(item('costCentre', 'ADM', 'conflict'), saved)).toBe('fix');
    expect(isOpenBucket('kept')).toBe(false);
    expect(['add', 'update', 'saved', 'fix'].every(b => isOpenBucket(b as never))).toBe(true);
  });

  test('counts trace to rows: they add up to the plan, and "to fix" includes problems', () => {
    const plan: CompanySetupPlan = {
      ok: false,
      items: [item('branch', 'HQ', 'create'), item('branch', 'JED', 'update'), item('department', 'FIN', 'unchanged'), item('grade', 'G1', 'reuse'), item('costCentre', 'ADM', 'conflict')],
      problems: [{ step: 2, field: 'company.legalNameEn', code: 'x', message: 'x' }],
      warnings: [],
    };
    const counts = reviewCounts(plan, new Set(['department:FIN']));
    expect(counts).toEqual({ add: 1, update: 1, saved: 1, kept: 1, fix: 2 });
    expect(counts.add + counts.update + counts.saved + counts.kept + (counts.fix - plan.problems.length)).toBe(plan.items.length);
  });

  test('"Keep my value" names the draft value; empty and object paths name nothing', () => {
    const body = opened();
    body.branches = [{ code: 'HQ', nameEn: 'Head Office', city: 'Riyadh', timeZoneId: 'Asia/Riyadh', isHeadOffice: true, locations: [] } as CompanySetupDraftBody['branches'][number]];
    expect(draftValueLabel(body, 'branches.0.nameEn')).toBe('Head Office');
    expect(draftValueLabel(body, 'branches[0].isHeadOffice')).toBe('Yes');
    expect(draftValueLabel(body, 'branches.0')).toBeNull();
    expect(draftValueLabel(body, 'company.tradeName')).toBeNull();
  });

  test('the structure shows no shared group catalog: its rows sit under the company', () => {
    const body = opened();
    const tree = buildTree(body, null);
    expect(tree.children.map(n => n.kind)).toEqual(['company']);
    expect(tree.children[0].children.map(n => n.id)).toEqual(expect.arrayContaining(['branches', 'costCentres', 'departments', 'designations', 'grades']));
    expect(JSON.stringify(tree)).not.toContain('"reused"');
  });

  test('a conflict whose reason already names the row is not said twice', () => {
    expect(conflictNamesRow({ code: 'ADM', reason: 'Cost centre ADM is already used by Sister. Use another code.' })).toBe(true);
    expect(conflictNamesRow({ code: 'ADM', reason: 'Already exists.' })).toBe(false);
    expect(conflictNamesRow({ code: 'ADM', reason: null })).toBe(false);
  });
});
