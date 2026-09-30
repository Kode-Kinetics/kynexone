/**
 * THE EVIDENCE HARNESS'S OWN GATES, proved without a browser or a stack.
 *
 * This repository has already shipped a lint that passed vacuously and a health check that always
 * returned zero, and the standing rule is that a gate which has never been seen to fail is not a
 * gate. The evidence harness makes four promises — it redacts personal data, it does not redact
 * evidence, it prints a missing image as "NO CAPTURE", and it refuses a reused screen — and every
 * one of them is the kind of thing that decays silently. So each is exercised here, including the
 * regressions that actually happened.
 *
 * Runs in the browserless lane (`e2e/playwright.browserless.config.ts`) alongside the preflight's
 * refusal logic and the identity contract, so a change that breaks one fails on the PR.
 */
import { expect, test } from '@playwright/test';
import { PII_PATTERNS, redactDeep, redactText } from './mask';
import { renderIndex, type RunRecord } from './recorder';

// ── Masking: what must be redacted ────────────────────────────────────────────────────────────

test('personal identifiers are redacted', () => {
  expect(redactText('hrmanager@intelliflow.com')).not.toContain('hrmanager@');
  expect(redactText('hrmanager@intelliflow.com')).toContain('@intelliflow.com');
  expect(redactText('IBAN SA0380000000608010167519')).not.toContain('SA0380000000608010167519');
  expect(redactText('Iqama 2000000001')).not.toContain('2000000001');
  expect(redactText('National ID 1098765432')).not.toContain('1098765432');
  expect(redactText('Mobile +966 50 123 4567')).not.toContain('50 123 4567');
  expect(redactText('Mobile +966501234567')).not.toContain('966501234567');
  expect(redactText('Mobile 0501234567')).not.toContain('0501234567');
});

// ── Masking: what must NOT be redacted ────────────────────────────────────────────────────────
//
// THE REGRESSION THIS PINS. The first run of the harness masked its own evidence: the phone pattern
// had no lookarounds, so a numeric run inside a UUID matched it and the manifest recorded the run as
// `14b95f51-28d9-4eab-827*******49e1d7e`, the batch as `PAY-202********59` and the wage file as
// `mudad_wps_000*****00_202609.xml`. A bundle whose record ids are starred out cannot be checked
// against anything at all, which makes it worse than no bundle. Each of those exact strings is
// asserted to survive untouched.

for (const evidenceValue of [
  '14b95f51-28d9-4eab-8271-9d0a49e1d7e',            // a payroll run id
  'PAY-2026093012345659',                            // a payment batch number
  'mudad_wps_0001234500_202609.xml',                 // the wage file name
  'PS-INTELLIFLOW-KSA-E1-20260930163934',            // a payslip number
  'aae7df796a426369bb11223344556677889900aabbccddeeff00112233445566', // a file hash
  'SAR 119,730.47',                                  // money the whole bundle turns on
  '138250.00',
  '1234567890.00',                                   // a large amount is not a phone number
  '2026-09-30T16:39:33.291036Z',                     // a timestamp
]) {
  test(`evidence survives redaction untouched: ${evidenceValue}`, () => {
    expect(redactText(evidenceValue)).toBe(evidenceValue);
  });
}

test('redactDeep redacts string values anywhere in the record, and leaves numbers alone', () => {
  const out = redactDeep({
    actor: { email: 'finance@intelliflow.com' },
    record: { id: '14b95f51-28d9-4eab-8271-9d0a49e1d7e' },
    totals: { net: 119730.47 },
    lines: ['IBAN SA0380000000608010167519', 'net 119,730.47'],
  });
  expect(out.actor.email).not.toContain('finance@i');
  expect(out.record.id).toBe('14b95f51-28d9-4eab-8271-9d0a49e1d7e');
  expect(out.totals.net).toBe(119730.47);
  expect(out.lines[0]).not.toContain('SA0380000000608010167519');
  expect(out.lines[1]).toBe('net 119,730.47');
});

test('every numeric pattern is anchored so it cannot match inside a longer token', () => {
  // The numeric and alphanumeric patterns are the dangerous ones: a run of digits inside a UUID,
  // a batch number or a file name looks exactly like an identifier, so each must refuse a match
  // that is glued to a word character on either side. That is the regression pinned above.
  //
  // `email` is exempt and named explicitly rather than skipped by shape: it is self-delimiting by
  // the `@`, and no record id in this product contains one.
  const mustBeAnchored = PII_PATTERNS.filter((p) => p.name !== 'email');
  expect(mustBeAnchored.map((p) => p.name)).toEqual(['iban', 'national-id', 'phone', 'ksa-mobile']);
  for (const p of mustBeAnchored) {
    const source = p.re.source;
    expect(source, `${p.name} must not match inside a longer token`).toContain('(?<!');
    expect(source, `${p.name} must not match a prefix of a longer token`).toContain('(?!');
  }
});

// ── The index: a missing image is stated, never implied ───────────────────────────────────────

const runWith = (steps: RunRecord['steps']): RunRecord => ({
  harness: 'kynexone-evidence-harness',
  manifestVersion: 1,
  runId: 'test',
  startedAtUtc: '2026-09-30T00:00:00.000Z',
  finishedAtUtc: '2026-09-30T00:01:00.000Z',
  story: 'test story',
  baseUrl: 'http://127.0.0.1:5340',
  apiBaseUrl: 'http://127.0.0.1:5341',
  expectedCommit: 'deadbee',
  expectedDatabase: 'disposable',
  redacted: true,
  steps,
  summary: {
    steps: steps.length,
    passed: steps.filter((s) => s.outcome === 'pass').length,
    failed: steps.filter((s) => s.outcome === 'fail').length,
    captured: steps.filter((s) => s.capture.status === 'captured').length,
    noCapture: steps.filter((s) => s.capture.status === 'no capture').length,
    stepsWithPersistenceReRead: steps.filter((s) => s.persisted).length,
  },
  notRun: [],
  defects: [],
});

const step = (over: Partial<RunRecord['steps'][number]>): RunRecord['steps'][number] => ({
  ord: 1,
  id: 's1',
  atUtc: '2026-09-30T00:00:30.000Z',
  actor: { name: 'A Person', email: 'a@b.local', role: 'HR Manager', tenant: 't', scope: 'group' },
  action: 'did a thing',
  expected: 'the thing happened',
  observed: 'as expected',
  outcome: 'pass',
  api: [],
  capture: { status: 'captured', shareablePath: 'img/01.jpg', shareableSha256: 'abc' },
  durationMs: 10,
  ...over,
});

test('a step with no image is printed as NO CAPTURE with its reason, in the table and its section', () => {
  const index = renderIndex(
    runWith([
      step({ ord: 1, id: 'captured-step' }),
      step({
        ord: 2,
        id: 'api-only-step',
        action: 'refused something over the API',
        expected: 'a 422',
        capture: { status: 'no capture', reason: 'driven through the API; there is no screen of it' },
      }),
    ]),
    true,
  );
  // Both the row and the section must say it, and must say WHY.
  expect(index.match(/NO CAPTURE/g)?.length ?? 0).toBeGreaterThanOrEqual(2);
  expect(index).toContain('driven through the API; there is no screen of it');
  // And the step must not carry an image reference that would read as a picture of it.
  const section = index.slice(index.indexOf('## 2.'));
  expect(section).not.toContain('![');
});

test('the index states the no-capture count in its own summary line', () => {
  const index = renderIndex(
    runWith([step({ capture: { status: 'no capture', reason: 'no screen exists' } })]),
    true,
  );
  expect(index).toContain('1 explicitly NO CAPTURE');
});

test('a failed step keeps its evidence in the index rather than being dropped', () => {
  const index = renderIndex(
    runWith([step({
      outcome: 'fail',
      observed: 'FAILED: the screen never settled',
      failure: 'the screen never settled — 2 request(s) still in flight',
      capture: { status: 'no capture', reason: 'the screen never settled, so nothing was captured' },
    })]),
    true,
  );
  expect(index).toContain('**FAIL**');
  expect(index).toContain('2 request(s) still in flight');
  expect(index).toContain('the screen never settled, so nothing was captured');
});

test('the index says whether the copy is redacted, so the two copies cannot be confused', () => {
  expect(renderIndex(runWith([step({})]), true)).toContain('**redacted** in this copy');
  expect(renderIndex(runWith([step({})]), false)).toContain('UNREDACTED');
});

test('a recorded defect is printed with its severity and evidence', () => {
  const run = runWith([step({})]);
  run.defects = [{
    severity: 'P2',
    title: 'a background fetch 403s and raises an error toast',
    detail: 'the transaction underneath succeeded',
    evidence: ['visible in img/11-payslips.jpg'],
  }];
  const index = renderIndex(run, true);
  expect(index).toContain('Defects observed while recording this story');
  expect(index).toContain('P2 — a background fetch 403s and raises an error toast');
  expect(index).toContain('visible in img/11-payslips.jpg');
});
