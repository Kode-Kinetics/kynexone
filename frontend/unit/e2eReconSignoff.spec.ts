import { test, expect } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import type { ReconDto } from '../src/api/payrollRecon';
import { MIN_EXPLANATION_LENGTH } from '../src/lib/payrollRecon';
import { buildReconSignoffRequest, reconNeedsSignoff, signOffReconciliation, type ReconTransport } from '../e2e/bootstrap/reconSignoff';

/**
 * The stack-backed specs approve payroll through the real API, which refuses an Approve without a current reconciliation
 * sign-off (409 reconciliation_not_reviewed). e2e/bootstrap/reconSignoff.ts gives them one shared way to sign it off.
 * These tests pin that request to the shape the server and the screen agree on, and that it asks the API for what it needs.
 */
const fixture = (name: string): ReconDto => JSON.parse(fs.readFileSync(path.join(__dirname, '../e2e/fixtures', name), 'utf8')) as ReconDto;

test.describe('the sign-off request the e2e helper sends', () => {
  test('is bound to the version stamp, restates the flagged count, and explains each flagged item then each employee', () => {
    const recon = fixture('recon-flagged.json');
    const body = buildReconSignoffRequest(recon, 4, [200, 201]);
    expect(body.expectedProcessVersion).toBe(recon.processVersion);
    expect(body.expectedSnapshotHash).toBe(recon.snapshotHash);
    expect(body.acknowledgedFlaggedEmployeeCount).toBe(4);
    expect(body.explanations.map(e => `${e.scope}:${e.key}`)).toEqual(['Item:Earning:OVERTIME', 'Item:Earning:BONUS', 'Employee:200', 'Employee:201']);
    for (const e of body.explanations) expect(e.text.length, e.key).toBeGreaterThanOrEqual(MIN_EXPLANATION_LENGTH);
    expect(body.priorRunId).toBeUndefined();
  });

  test('a first run with no prior month needs no explanations', () => {
    const body = buildReconSignoffRequest(fixture('recon-noprior.json'), 0, []);
    expect(body.explanations).toEqual([]);
    expect(body.acknowledgedFlaggedEmployeeCount).toBe(0);
  });

  test('a reconciliation with no version stamp cannot be signed off', () => {
    const recon = { ...fixture('recon-full.json'), processVersion: undefined };
    expect(() => buildReconSignoffRequest(recon, 0, [])).toThrow(/stamp/i);
  });

  test('an off-cycle run (not applicable) and an already signed one need no sign-off', () => {
    expect(reconNeedsSignoff(fixture('recon-full.json'))).toBe(true);
    expect(reconNeedsSignoff({ ...fixture('recon-full.json'), mode: 'NotApplicable' })).toBe(false);
    expect(reconNeedsSignoff(fixture('recon-signed.json'))).toBe(false);
  });
});

test.describe('signOffReconciliation', () => {
  const RUN = '33333333-3333-4333-8333-333333333333';
  const respond = (status: number, body: unknown) => ({ status, body, text: JSON.stringify(body) });

  function transport(recon: ReconDto, opts: { flaggedTotal?: number; notPaid?: number[]; unexplained?: number[]; signoffStatus?: number } = {}) {
    const calls: Array<{ method: string; path: string; body?: unknown }> = [];
    const call: ReconTransport = async (method, p, body) => {
      calls.push({ method, path: p, body });
      const url = new URL(p, 'http://x');
      if (method === 'GET' && url.pathname.endsWith('/recon')) return respond(200, recon);
      if (method === 'GET' && url.pathname.endsWith('/recon/employees')) {
        const bucket = url.searchParams.get('bucket');
        const category = url.searchParams.get('category');
        const ids = category === 'NOT_PAID' ? opts.notPaid ?? [] : category === 'UNEXPLAINED' ? opts.unexplained ?? [] : [];
        if (bucket === 'flagged') return respond(200, { items: [], total: opts.flaggedTotal ?? 0, page: 1, pageSize: 1 });
        return respond(200, { items: ids.map(employeeId => ({ employeeId })), total: ids.length, page: 1, pageSize: 200 });
      }
      if (method === 'POST' && url.pathname.endsWith('/recon/signoff')) return respond(opts.signoffStatus ?? 201, { id: 's1', status: 'Active' });
      return respond(404, null);
    };
    return { call, calls };
  }

  test('reads the reconciliation, counts the flagged employees, lists NOT_PAID and UNEXPLAINED employees, then posts the sign-off', async () => {
    const { call, calls } = transport(fixture('recon-flagged.json'), { flaggedTotal: 5, notPaid: [200], unexplained: [310] });
    const result = await signOffReconciliation(call, RUN);
    expect(result).toBe('signed');
    const post = calls.find(c => c.method === 'POST')!;
    expect(post.path).toBe(`/api/payroll/runs/${RUN}/recon/signoff`);
    expect(post.body).toMatchObject({ acknowledgedFlaggedEmployeeCount: 5 });
    expect((post.body as { explanations: Array<{ scope: string; key: string }> }).explanations.map(e => `${e.scope}:${e.key}`))
      .toEqual(['Item:Earning:OVERTIME', 'Item:Earning:BONUS', 'Employee:200', 'Employee:310']);
    const flaggedQuery = calls.find(c => c.path.includes('bucket=flagged'))!;
    expect(flaggedQuery.path).toContain('section=value');
    expect(calls.filter(c => c.path.includes('category=NOT_PAID') || c.path.includes('category=UNEXPLAINED'))).toHaveLength(2);
  });

  test('does nothing for a run that is not reconciled or is already signed off', async () => {
    const off = transport({ ...fixture('recon-full.json'), mode: 'NotApplicable' });
    expect(await signOffReconciliation(off.call, RUN)).toBe('not_needed');
    expect(off.calls.filter(c => c.method === 'POST')).toHaveLength(0);
    const signed = transport(fixture('recon-signed.json'));
    expect(await signOffReconciliation(signed.call, RUN)).toBe('already_signed');
    expect(signed.calls.filter(c => c.method === 'POST')).toHaveLength(0);
  });

  test('a refusal throws with the status and the server body, never silently', async () => {
    const { call } = transport(fixture('recon-full.json'), { signoffStatus: 403 });
    await expect(signOffReconciliation(call, RUN)).rejects.toThrow(/HTTP 403/);
  });
});
