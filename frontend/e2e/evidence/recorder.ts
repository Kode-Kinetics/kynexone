/**
 * THE EVIDENCE RECORDER — register item F12.
 *
 * ── What this is for ──────────────────────────────────────────────────────────────────────────
 * A green test says a journey passed. An evidence bundle is supposed to say WHAT HAPPENED, well
 * enough that someone who did not run it can check the claim. F12 records four ways bundles in this
 * repository have failed to do that, and each one is closed here by construction rather than by
 * care:
 *
 *   a loading-spinner frame filed as a completed transaction
 *       → nothing is captured on demand. `settle.ts` waits for zero in-flight requests, no visible
 *         loading indicator and a region whose text has stopped changing; a screen that never gets
 *         there fails the step instead of being photographed.
 *
 *   a reused, unchanged screen
 *       → every capture is hashed, and a hash that repeats an earlier step's is recorded as
 *         `identicalToStep` and FAILS the step, unless the step declared `allowIdentical` and said
 *         why. Two steps cannot quietly share one picture.
 *
 *   an expected outcome recorded where no capture exists
 *       → `expected` and the capture are one record. A step with no page must state
 *         `noCaptureReason`, and the manifest and the index both print "NO CAPTURE — <reason>"
 *         rather than leaving a gap that reads as an image.
 *
 *   a screenshot of an error page that did not show the recorded HTTP failure
 *       → `mustShowOnScreen` is asserted against the settled region's own text BEFORE the capture,
 *         and the pattern and its match are recorded. A step that records an HTTP status and a
 *         picture must also prove the picture is of that status.
 *
 * ── And the part that makes "saved" mean saved ────────────────────────────────────────────────
 * Every step that writes re-reads the record afterwards through a FRESH GET (or a reload) and
 * records the persisted values. The UI showing "Approved" is a claim about the UI; the API
 * answering `status: Approved` to a new request is a claim about the database.
 *
 * ── Output ────────────────────────────────────────────────────────────────────────────────────
 *   <bundle>/manifest.json   machine-readable, personal data redacted
 *   <bundle>/index.md        human-readable, personal data redacted, every step listed including
 *                            the ones with no image
 *   <bundle>/img/*.jpg       the redacted captures the manifest hashes
 *   <originals>/…            the same three, unredacted, written OUTSIDE the repository
 */
import { createHash } from 'node:crypto';
import { mkdir, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { test, type APIRequestContext, type Page } from '@playwright/test';
import type { Persona } from '../world';
import { piiLocator, redactDeep, redactText, tagPii, untagPii } from './mask';
import { attach, settle, type SettleOptions } from './settle';

// ── The record shapes ─────────────────────────────────────────────────────────────────────────

export interface EvidenceActor {
  /** The persona's own name, e.g. "IntelliFlow Finance Approver". */
  name: string;
  email: string;
  /** The tenant role the account holds, e.g. "Finance Approver". */
  role: string;
  tenant: string | null;
  /** 'group', 'platform', or the company codes a company-scoped persona is confined to. */
  scope: string;
}

export interface ApiCallRecord {
  method: string;
  /** Path + query only; the origin is on the run record. */
  url: string;
  status: number;
  durationMs: number;
  /** The business record id this call created or acted on, when one could be read from it. */
  recordId?: string | null;
  /** A short excerpt of a non-2xx body, so a refusal is legible without the originals. */
  detail?: string;
}

export interface CaptureRecord {
  status: 'captured' | 'no capture';
  /** Why there is no image. Always present when status is 'no capture'. */
  reason?: string;
  /** Bundle-relative path of the redacted image. */
  shareablePath?: string;
  shareableSha256?: string;
  /** sha256 of the UNREDACTED capture, so the original can be matched to this record. */
  originalSha256?: string;
  bytes?: number;
  settledAfterMs?: number;
  /** Requests and indicators the harness waited out before capturing. */
  waitedOnRequests?: string[];
  waitedOnIndicators?: string[];
  /** How many DOM nodes were painted over in the shareable copy. */
  maskedNodes?: number;
  /** Set when this image is byte-identical to an earlier step's. */
  identicalToStep?: string;
}

export interface StepRecord {
  ord: number;
  id: string;
  atUtc: string;
  actor: EvidenceActor;
  action: string;
  /** The business record the step is about. */
  record?: { type: string; id: string | null };
  expected: string;
  observed: string;
  outcome: 'pass' | 'fail';
  /** Present when outcome is 'fail'. */
  failure?: string;
  api: ApiCallRecord[];
  /** The screen assertion that ties the picture to the recorded HTTP result. */
  screenAssertion?: { pattern: string; matched: boolean };
  /** The re-read after the write. Absent for steps that only look. */
  persisted?: { via: string; values: Record<string, unknown> };
  capture: CaptureRecord;
  durationMs: number;
}

export interface RunRecord {
  harness: string;
  manifestVersion: number;
  runId: string;
  startedAtUtc: string;
  finishedAtUtc: string | null;
  story: string;
  baseUrl: string;
  apiBaseUrl: string;
  expectedCommit: string | null;
  expectedDatabase: string | null;
  /** Personal data is redacted in this copy; the originals directory holds the unredacted one. */
  redacted: boolean;
  steps: StepRecord[];
  summary: {
    steps: number;
    passed: number;
    failed: number;
    captured: number;
    noCapture: number;
    stepsWithPersistenceReRead: number;
  };
  /** Anything the story could not reach, recorded rather than implied. */
  notRun: Array<{ area: string; reason: string }>;
  /**
   * Defects OBSERVED while the story ran, whether or not they failed a step.
   *
   * An evidence run sees more than it asserts — a background request that 403s, an error toast on
   * a screen whose transaction succeeded. Recording those in the bundle rather than only in a
   * covering note is the difference between evidence and a highlight reel.
   */
  defects: Array<{ severity: 'P0' | 'P1' | 'P2'; title: string; detail: string; evidence: string[] }>;
}

// ── Step declaration ──────────────────────────────────────────────────────────────────────────

export interface StepContext {
  /**
   * Record an API call explicitly — for calls made outside the browser (a fresh GET, a refusal
   * probe). Calls the browser makes are recorded automatically while the step runs.
   */
  api: (method: string, path: string, init?: { token?: string; data?: unknown; recordId?: string }) =>
    Promise<{ status: number; json: any; text: string }>;
  /** Note the business record id once the step learns it. */
  setRecordId: (id: string) => void;
  /** Add a sentence to the observed outcome. */
  note: (text: string) => void;
}

export interface StepSpec {
  /** Stable slug, used for the file name and for cross-references. */
  id: string;
  actor: Persona;
  action: string;
  expected: string;
  /** The page whose SETTLED final state is captured. Omit only with `noCaptureReason`. */
  page?: Page;
  /** Required when `page` is omitted: why this step has no image. Printed verbatim in the index. */
  noCaptureReason?: string;
  record?: { type: string; id?: string | null };
  /**
   * Text that must be on the settled screen before it is captured. This is what stops a picture of
   * one screen being filed as evidence of another — most importantly an error page that does not
   * actually show the HTTP failure the step recorded.
   */
  mustShowOnScreen?: string | RegExp;
  /** Perform the action. */
  act: (ctx: StepContext) => Promise<void>;
  /**
   * Re-read the record after the write and return the values that are actually persisted. Runs
   * AFTER `act` and BEFORE the capture, so the capture shows the state the re-read confirmed.
   */
  persisted?: (ctx: StepContext) => Promise<Record<string, unknown>>;
  /** How the re-read was done, for the manifest. Defaults to 'fresh GET'. */
  persistedVia?: string;
  /**
   * Extra regions to paint over in the SHAREABLE image, for personal data no pattern can find.
   *
   * The pattern masker catches identifiers — emails, IBANs, national ids, phone numbers — because
   * those have a shape. A person's NAME does not: "Aisha Al-Harbi" is indistinguishable from a
   * company name or a column heading. So a spec that knowingly photographs a column of employee
   * names names that column here, and the manifest records how many nodes were painted. A screen
   * of people with `maskedNodes: 0` is a signal, not a reassurance.
   */
  maskExtra?: (page: Page) => Array<import('@playwright/test').Locator>;
  /** Accept an image identical to an earlier step's, and say why. */
  allowIdentical?: string;
  /** The region whose text must stop changing, and whose text `mustShowOnScreen` is checked against. */
  region?: string;
  settle?: SettleOptions;
}

// ── The recorder ──────────────────────────────────────────────────────────────────────────────

const sha256 = (buf: Buffer | string): string => createHash('sha256').update(buf).digest('hex');
const nowUtc = (): string => new Date().toISOString();

export interface RecorderOptions {
  story: string;
  /** Where the SHAREABLE bundle is written. Redacted; this is the copy that is committed. */
  bundleDir: string;
  /** Where the UNREDACTED copies go. Must be outside the repository. */
  originalsDir: string;
  baseUrl: string;
  apiBaseUrl: string;
  expectedCommit?: string | null;
  expectedDatabase?: string | null;
  /** Used for `ctx.api` and for every persistence re-read. */
  request: APIRequestContext;
}

export class EvidenceRecorder {
  private readonly steps: StepRecord[] = [];
  private readonly notRun: Array<{ area: string; reason: string }> = [];
  private readonly hashes = new Map<string, string>(); // sha256 → step id
  private readonly startedAtUtc = nowUtc();
  private readonly runId: string;

  constructor(private readonly options: RecorderOptions) {
    this.runId = this.startedAtUtc.replace(/[:.]/g, '-');
  }

  private readonly defects: RunRecord['defects'] = [];

  /** Record an area the story could not reach. Printed in the index as Not run, never as a pass. */
  notRunArea(area: string, reason: string): void {
    this.notRun.push({ area, reason });
  }

  /** Record a defect observed while the story ran. It appears in the manifest and the index. */
  defect(severity: 'P0' | 'P1' | 'P2', title: string, detail: string, evidence: string[] = []): void {
    this.defects.push({ severity, title, detail, evidence });
  }

  /** Every API call recorded so far, across every step — for after-the-fact observations. */
  recordedCalls(): Array<ApiCallRecord & { stepId: string }> {
    return this.steps.flatMap((s) => s.api.map((c) => ({ ...c, stepId: s.id })));
  }

  private actorOf(persona: Persona): EvidenceActor {
    return {
      name: persona.label.replace(/\s*\([^)]*\)\s*$/, ''),
      email: persona.email,
      role: persona.role ?? 'platform owner',
      tenant: persona.tenantSlug,
      scope: persona.scope === 'companies' ? persona.companyCodes.join('+') : persona.scope,
    };
  }

  /**
   * Run one significant step and record it as ONE linked record: who, when, what they did, what the
   * API answered, which record it was about, what was expected, what was observed, what the
   * database says afterwards, and a hashed picture of the settled screen.
   *
   * Throws on failure — a failed step is a finding, and the manifest keeps it, but the test goes red.
   */
  async step(spec: StepSpec): Promise<StepRecord> {
    const ord = this.steps.length + 1;
    const startedAt = Date.now();
    const atUtc = nowUtc();
    const apiCalls: ApiCallRecord[] = [];
    const notes: string[] = [];
    let recordId: string | null = spec.record?.id ?? null;

    if (!spec.page && !spec.noCaptureReason) {
      throw new Error(
        `[evidence] step "${spec.id}" declares neither a page to capture nor a noCaptureReason. `
        + 'A recorded expectation with an unexplained missing image is one of the four failures this '
        + 'harness exists to prevent; say why there is no picture.',
      );
    }
    if (spec.page) attach(spec.page);

    // Every /api call the BROWSER makes during this step, recorded without the spec asking.
    const browserListener = spec.page
      ? (response: import('@playwright/test').Response) => {
        const url = response.url();
        if (!url.includes('/api/')) return;
        const request = response.request();
        const timing = request.timing();
        apiCalls.push({
          method: request.method(),
          url: pathOf(url),
          status: response.status(),
          durationMs: Math.max(0, Math.round(timing.responseEnd - timing.startTime)),
        });
      }
      : null;
    if (spec.page && browserListener) spec.page.on('response', browserListener);

    const ctx: StepContext = {
      api: async (method, path, init = {}) => {
        const t0 = Date.now();
        const response = await this.options.request.fetch(`${this.options.apiBaseUrl}${path}`, {
          method,
          headers: init.token ? { Authorization: `Bearer ${init.token}` } : undefined,
          data: init.data as never,
        });
        const text = await response.text();
        let json: any = null;
        try { json = text ? JSON.parse(text) : null; } catch { /* not JSON */ }
        apiCalls.push({
          method,
          url: path,
          status: response.status(),
          durationMs: Date.now() - t0,
          recordId: init.recordId ?? null,
          detail: response.ok() ? undefined : text.replace(/\s+/g, ' ').slice(0, 220),
        });
        return { status: response.status(), json, text };
      },
      setRecordId: (id) => { recordId = id; },
      note: (text) => { notes.push(text); },
    };

    let failure: string | undefined;
    let persisted: StepRecord['persisted'];
    let capture: CaptureRecord = { status: 'no capture', reason: spec.noCaptureReason };
    let screenAssertion: StepRecord['screenAssertion'];

    try {
      await test.step(`${ord}. ${spec.action} — as ${this.actorOf(spec.actor).name}`, async () => {
        await spec.act(ctx);
        // SETTLE FIRST, THEN RE-READ. The order is load-bearing and was learned the hard way: the
        // first version re-read the record immediately after `act`, and `act` can legitimately
        // finish on a DOM assertion while the write that produced it is still in flight. The
        // persistence re-read then raced the write and recorded the PREVIOUS value — a manifest
        // saying the database still held "Processed" under a screenshot showing "Pending Finance
        // Review". Settling first means every re-read happens after the page has no outstanding
        // request, so "saved" is measured against a finished write.
        let report: Awaited<ReturnType<typeof settle>> | null = null;
        if (spec.page) report = await settle(spec.page, { ...spec.settle, region: spec.region });
        if (spec.persisted) {
          const values = await spec.persisted(ctx);
          persisted = { via: spec.persistedVia ?? 'fresh GET', values };
        }
        if (spec.page && report) {
          if (spec.mustShowOnScreen) {
            const text = await regionTextOf(spec.page, spec.region ?? 'main');
            const matched = typeof spec.mustShowOnScreen === 'string'
              ? text.includes(spec.mustShowOnScreen)
              : spec.mustShowOnScreen.test(text);
            screenAssertion = { pattern: String(spec.mustShowOnScreen), matched };
            if (!matched) {
              throw new Error(
                `[evidence] step "${spec.id}": the settled screen does not show ${spec.mustShowOnScreen}. `
                + 'REFUSING TO CAPTURE — a picture filed here would be evidence of a different screen '
                + `than the one this step claims.\nSettled ${spec.region ?? 'main'} text (first 600 chars):\n`
                + text.slice(0, 600),
              );
            }
          }
          // A persistence re-read can itself leave the page fetching (a spec that reloads to prove
          // the value). Re-settle so the capture is still of a settled screen.
          report = await settle(spec.page, { ...spec.settle, region: spec.region, quietMs: 200 });
          capture = await this.capture(spec, report);
        }
      });
    } catch (error) {
      failure = error instanceof Error ? error.message : String(error);
    }

    if (spec.page && browserListener) spec.page.off('response', browserListener);

    const record: StepRecord = {
      ord,
      id: spec.id,
      atUtc,
      actor: this.actorOf(spec.actor),
      action: spec.action,
      record: spec.record ? { type: spec.record.type, id: recordId } : undefined,
      expected: spec.expected,
      observed: failure
        ? `FAILED: ${firstLine(failure)}`
        : notes.length ? notes.join(' ') : 'as expected',
      outcome: failure ? 'fail' : 'pass',
      failure,
      api: apiCalls,
      screenAssertion,
      persisted,
      capture,
      durationMs: Date.now() - startedAt,
    };
    this.steps.push(record);

    if (failure) throw new Error(`[evidence] step ${ord} "${spec.id}" failed and was recorded as such.\n${failure}`);
    return record;
  }

  /** Photograph the settled page twice: unredacted to the originals, redacted to the bundle. */
  private async capture(spec: StepSpec, report: Awaited<ReturnType<typeof settle>>): Promise<CaptureRecord> {
    const page = spec.page!;
    const name = `${String(this.steps.length + 1).padStart(2, '0')}-${spec.id}.jpg`;
    const shareRel = join('img', name);
    const sharePath = join(this.options.bundleDir, shareRel);
    const originalPath = join(this.options.originalsDir, 'img', name);
    await mkdir(join(this.options.bundleDir, 'img'), { recursive: true });
    await mkdir(join(this.options.originalsDir, 'img'), { recursive: true });

    const patternNodes = await tagPii(page);
    const extra = spec.maskExtra?.(page) ?? [];
    let extraNodes = 0;
    for (const locator of extra) extraNodes += await locator.count();
    // JPEG, not PNG: the bundle is committed and has to stay small. Quality is high enough that
    // every figure the manifest quotes is legible in the image.
    const original = await page.screenshot({ type: 'jpeg', quality: 72, path: originalPath });
    const shareable = await page.screenshot({
      type: 'jpeg',
      quality: 72,
      path: sharePath,
      mask: [piiLocator(page), ...extra],
      maskColor: '#475569',
    });
    await untagPii(page);
    const maskedNodes = patternNodes + extraNodes;

    const shareableSha256 = sha256(shareable);
    const previous = this.hashes.get(shareableSha256);
    if (previous && !spec.allowIdentical) {
      throw new Error(
        `[evidence] step "${spec.id}" produced an image byte-identical to step "${previous}".\n`
        + 'That is a reused, unchanged screen presented as a new piece of evidence. Either the action '
        + 'did not change anything visible, or the wrong page was captured. If the two screens really '
        + 'are meant to be identical, declare allowIdentical with the reason.',
      );
    }
    if (!previous) this.hashes.set(shareableSha256, spec.id);

    return {
      status: 'captured',
      shareablePath: shareRel,
      shareableSha256,
      originalSha256: sha256(original),
      bytes: shareable.byteLength,
      settledAfterMs: report.settledAfterMs,
      waitedOnRequests: report.waitedOnRequests.map(pathOf).slice(0, 8),
      waitedOnIndicators: report.waitedOnIndicators.slice(0, 8),
      maskedNodes,
      identicalToStep: previous,
    };
  }

  /** Write both copies of the manifest and the index. Safe to call from a fixture teardown. */
  async finish(): Promise<RunRecord> {
    const run: RunRecord = {
      harness: 'kynexone-evidence-harness',
      manifestVersion: 1,
      runId: this.runId,
      startedAtUtc: this.startedAtUtc,
      finishedAtUtc: nowUtc(),
      story: this.options.story,
      baseUrl: this.options.baseUrl,
      apiBaseUrl: this.options.apiBaseUrl,
      expectedCommit: this.options.expectedCommit ?? null,
      expectedDatabase: this.options.expectedDatabase ?? null,
      redacted: true,
      steps: this.steps,
      summary: {
        steps: this.steps.length,
        passed: this.steps.filter((s) => s.outcome === 'pass').length,
        failed: this.steps.filter((s) => s.outcome === 'fail').length,
        captured: this.steps.filter((s) => s.capture.status === 'captured').length,
        noCapture: this.steps.filter((s) => s.capture.status === 'no capture').length,
        stepsWithPersistenceReRead: this.steps.filter((s) => s.persisted).length,
      },
      notRun: this.notRun,
      defects: this.defects,
    };

    await mkdir(this.options.bundleDir, { recursive: true });
    await mkdir(this.options.originalsDir, { recursive: true });
    await writeFile(join(this.options.originalsDir, 'manifest.json'), `${JSON.stringify(run, null, 2)}\n`);
    await writeFile(join(this.options.originalsDir, 'index.md'), renderIndex(run, false));

    const redacted = redactDeep({ ...run, redacted: true });
    await writeFile(join(this.options.bundleDir, 'manifest.json'), `${JSON.stringify(redacted, null, 2)}\n`);
    await writeFile(join(this.options.bundleDir, 'index.md'), renderIndex(redacted, true));
    return run;
  }
}

// ── Helpers ───────────────────────────────────────────────────────────────────────────────────

const firstLine = (s: string): string => s.split('\n')[0].slice(0, 300);

function pathOf(url: string): string {
  try { const u = new URL(url); return `${u.pathname}${u.search}`; } catch { return url; }
}

async function regionTextOf(page: Page, region: string): Promise<string> {
  return page.evaluate((sel) => {
    const el = document.querySelector(sel) ?? document.body;
    return (el as HTMLElement).innerText ?? '';
  }, region);
}

/**
 * The human-readable index. Its one hard rule: a step with no image says so, in its own row and in
 * its own section, as "NO CAPTURE". A blank cell would read as an oversight; an omitted row would
 * read as an image.
 */
export function renderIndex(run: RunRecord, redacted: boolean): string {
  const L: string[] = [];
  L.push(`# ${run.story}`);
  L.push('');
  L.push(
    `Recorded by the KynexOne evidence harness (\`frontend/e2e/evidence/\`). Every step below is one `
    + 'linked record: who acted, when, what they did, what the API answered, what the database said '
    + 'when the record was read back, and a picture of the settled screen. Steps with no picture are '
    + 'listed as **NO CAPTURE** with the reason — never left blank.',
  );
  L.push('');
  L.push(`- Run: \`${run.runId}\` · started ${run.startedAtUtc} · finished ${run.finishedAtUtc ?? '(incomplete)'}`);
  L.push(`- Frontend: ${run.baseUrl} · API: ${run.apiBaseUrl}`);
  L.push(`- Build under test: ${run.expectedCommit ?? '(not declared)'} · database: ${run.expectedDatabase ?? '(not declared)'}`);
  L.push(`- Personal data: ${redacted ? '**redacted** in this copy; the unredacted originals are kept outside the repository and are matched to it by `originalSha256`' : 'UNREDACTED — this is the originals copy, do not share'}`);
  L.push('');
  L.push(
    `**${run.summary.steps} steps — ${run.summary.passed} passed, ${run.summary.failed} failed. `
    + `${run.summary.captured} with a verified settled-state image, ${run.summary.noCapture} explicitly NO CAPTURE. `
    + `${run.summary.stepsWithPersistenceReRead} re-read the record after the write.**`,
  );
  L.push('');
  if (run.defects.length) {
    L.push('## Defects observed while recording this story');
    L.push('');
    L.push(
      'These were seen during the run. They did not necessarily fail a step — an evidence run sees '
      + 'more than it asserts — and they are recorded here so the bundle is not a highlight reel.',
    );
    L.push('');
    for (const d of run.defects) {
      L.push(`- **${d.severity} — ${d.title}**  `);
      L.push(`  ${d.detail}`);
      for (const e of d.evidence) L.push(`  - ${e}`);
    }
    L.push('');
  }
  L.push('| # | Step | Actor | Role | Record | Outcome | Image |');
  L.push('|---|------|-------|------|--------|---------|-------|');
  for (const s of run.steps) {
    const img = s.capture.status === 'captured'
      ? `[\`${s.capture.shareablePath}\`](${s.capture.shareablePath})`
      : `**NO CAPTURE** — ${s.capture.reason ?? 'no reason recorded'}`;
    const rec = s.record ? `${s.record.type} \`${s.record.id ?? '—'}\`` : '—';
    L.push(`| ${s.ord} | ${s.action} | ${s.actor.name} | ${s.actor.role} | ${rec} | ${s.outcome === 'pass' ? 'pass' : '**FAIL**'} | ${img} |`);
  }
  L.push('');
  for (const s of run.steps) {
    L.push(`## ${s.ord}. ${s.action}`);
    L.push('');
    L.push(`- **Actor** — ${s.actor.name} (${s.actor.role}), \`${s.actor.email}\`, tenant \`${s.actor.tenant ?? 'platform'}\`, scope \`${s.actor.scope}\``);
    L.push(`- **When (UTC)** — ${s.atUtc} (${s.durationMs} ms)`);
    if (s.record) L.push(`- **Business record** — ${s.record.type} \`${s.record.id ?? 'none recorded'}\``);
    L.push(`- **Expected** — ${s.expected}`);
    L.push(`- **Observed** — ${s.observed}`);
    if (s.api.length) {
      L.push('- **API**');
      for (const c of s.api.slice(0, 12)) {
        L.push(`  - \`${c.method} ${c.url}\` → **${c.status}** (${c.durationMs} ms)${c.detail ? ` — ${c.detail}` : ''}`);
      }
      if (s.api.length > 12) L.push(`  - …and ${s.api.length - 12} more, in \`manifest.json\``);
    }
    if (s.persisted) {
      L.push(`- **Persisted (${s.persisted.via})** — ${Object.entries(s.persisted.values).map(([k, v]) => `${k}=\`${JSON.stringify(v)}\``).join(', ')}`);
    }
    if (s.screenAssertion) {
      L.push(`- **Screen assertion** — the settled screen ${s.screenAssertion.matched ? 'showed' : 'DID NOT show'} ${s.screenAssertion.pattern}`);
    }
    if (s.capture.status === 'captured') {
      L.push(`- **Image** — \`${s.capture.shareablePath}\`, sha256 \`${s.capture.shareableSha256}\` (original sha256 \`${s.capture.originalSha256}\`, ${s.capture.bytes} bytes)`);
      L.push(`- **Settled** — after ${s.capture.settledAfterMs} ms; waited out ${s.capture.waitedOnRequests?.length ?? 0} request(s) and ${s.capture.waitedOnIndicators?.length ?? 0} loading indicator(s); ${s.capture.maskedNodes ?? 0} node(s) redacted`);
      L.push('');
      L.push(`![${s.action}](${s.capture.shareablePath})`);
    } else {
      L.push(`- **Image** — **NO CAPTURE.** ${s.capture.reason ?? 'no reason recorded'}`);
    }
    if (s.failure) {
      L.push('');
      L.push('```');
      L.push(s.failure.slice(0, 2000));
      L.push('```');
    }
    L.push('');
  }
  if (run.notRun.length) {
    L.push('## Not run');
    L.push('');
    L.push('Areas this story did not execute. Recorded so that no one reads their absence as a pass.');
    L.push('');
    for (const n of run.notRun) L.push(`- **${n.area}** — ${n.reason}`);
    L.push('');
  }
  return `${L.join('\n')}\n`;
}

/** Where the bundle goes. Overridable so CI can write somewhere else. */
export function bundlePaths(): { bundleDir: string; originalsDir: string } {
  // cwd is `frontend/` for every Playwright lane in this repo.
  const bundleDir = process.env.E2E_EVIDENCE_DIR?.trim() || join('..', 'docs', 'acceptance', 'payroll-journey');
  const originalsDir = process.env.E2E_EVIDENCE_ORIGINALS_DIR?.trim()
    || join('test-results', 'evidence-originals');
  return { bundleDir, originalsDir };
}
