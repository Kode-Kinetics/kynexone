/**
 * ONE FULLY RECONCILED PAYROLL STORY, recorded as evidence.
 *
 * ── What this is, and what it is not ──────────────────────────────────────────────────────────
 * `e2e/payroll-run-to-wps.spec.ts` already asserts this path. This spec does not replace it and is
 * not a second copy of it: that one is the REGRESSION test (does the product still behave), this
 * one is the EVIDENCE run (what happened, to which record, at whose hand, and can a reader check
 * it). Every step here goes through the harness in `e2e/evidence/`, so each one is a single linked
 * record — actor, UTC time, action, API status, business record id, expected, observed, the values
 * the database returned when the record was read BACK, and a hashed picture of the settled screen.
 *
 * ── The people ────────────────────────────────────────────────────────────────────────────────
 * Not one admin doing everything. The product's own separation of duties is the story:
 *   admin@intelliflow.com      Admin             creates and processes the run; the only role the
 *                                                catalog grants `payroll.export`, so the only one
 *                                                who can reach the bank file.
 *   hrmanager@intelliflow.com  HR Manager        the MAKER — approves onward, cannot lock.
 *   finance@intelliflow.com    Finance Approver  the CHECKER — a DIFFERENT authorised person, who
 *                                                gives the final approval and locks.
 * PayrollController.Approve also refuses whoever processed the run, which is why the admin
 * processes and the HR Manager makes the first approval.
 *
 * ── The refusals are part of the story ────────────────────────────────────────────────────────
 * A journey that only records the happy path is a demo. Three refusals are recorded here with the
 * same rigour as the successes:
 *   R1  an employee with incomplete statutory data cannot be activated (structured 422);
 *   R2  a payment batch cannot be created before the run is locked (control disabled, and the
 *       screen names the run's actual status);
 *   R3  the maker cannot complete the run alone (no control in the UI, HTTP 400 from the API, and
 *       — the part that matters — the run does not move).
 *
 * ── And the bank export does not pay anybody ──────────────────────────────────────────────────
 * Generating the WPS/SIF file is a FILE, not a payment. Step 11 re-reads the run, the batch and
 * every payment record after the export and records that none of them says paid or settled. That
 * is asserted, not assumed: `POST /api/payroll/payment-batches/{id}/settle` is the endpoint that
 * would mark them paid, and this story never calls it.
 */
import { expect } from '@playwright/test';
import type { Locator, Page } from '@playwright/test';
import { evidenceTest } from './fixture';
import type { SignedInPersona } from './fixture';
import {
  INTELLIFLOW_ADMIN, INTELLIFLOW_FINANCE, INTELLIFLOW_HR_MGR, INTELLIFLOW_SLUG,
} from '../world';

const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

/**
 * EVERY WARNING THE RUN MAY CARRY INTO APPROVAL, AND WHY IT IS ACCEPTABLE.
 *
 * The register's standard for this journey is that every remaining warning is explained, not
 * merely counted. A validation screen reading "14 warnings" with nobody able to say what they are
 * is indistinguishable from a validation screen nobody read.
 *
 * So the codes are enumerated here with a written rationale, and the validation step FAILS on a
 * code that is not in this map. That makes the list self-maintaining: the day the engine emits a
 * new warning, this story goes red and someone has to write the sentence rather than watch the
 * number go up.
 *
 * Errors are a different matter and are never explained away — the step asserts there are none.
 */
const WARNING_EXPLANATIONS: Record<string, string> = {
  GOSI_COHORT_NOT_RECORDED:
    'The employee has no GOSI first-registration date, so the engine cannot tell which contribution '
    + 'schedule applies and computes on the pre-3-July-2024 one. It is a WARNING and not an error by '
    + 'design: the pre-2024 schedule is the correct answer for everyone hired before that date, and '
    + 'the date is recorded on the employee\'s Payroll tab once known. The fixture employees are '
    + 'provisioned without it, so this fires once per employee and is expected here. The related '
    + 'GOSI_NEW_ENTRANT_SCHEDULE_NOT_MODELLED code is an ERROR and would block approval; it does not '
    + 'appear on this run, and the step asserts zero errors.',
  WARN_NO_ATTENDANCE:
    'No attendance has been captured for the period. The run is for a FUTURE month (the story takes '
    + 'the first period with no existing run), so there is nothing to capture yet. Salary is not '
    + 'attendance-derived for these employees, so the totals are unaffected.',
  WARN_ARREARS_LOOKBACK_TRUNCATED:
    'The arrears look-back could not reach as far into the past as the policy asks, because the '
    + 'tenant has only just been provisioned and there are no prior periods to look back at. On a '
    + 'tenant with history this warning would mean something; on a fresh one it states a fact about '
    + 'the fixture, not about the run.',
  WARN_GOSI_RATES_REQUIRE_SIGNOFF:
    'The GOSI contribution rates in force have not been counter-signed by a compliance officer for '
    + 'this tenant. It is advisory: the rates applied are the statutory defaults, and the sign-off is '
    + 'a governance step, not a calculation input. It does not change any figure this story reconciles.',
};

/** Everything is scoped to <main>; the shell carries its own Payroll and Approvals entries. */
const content = (page: Page): Locator => page.locator('main');

/** "194,596.87" → 194596.87. Throws on anything that is not a rendered money value. */
function parseAmount(raw: string): number {
  const cleaned = raw.replace(/,/g, '').trim();
  if (!/^-?\d+(\.\d+)?$/.test(cleaned)) throw new Error(`Not a rendered amount: ${JSON.stringify(raw)}`);
  return Number(cleaned);
}

async function gotoPayroll(page: Page): Promise<void> {
  await page.goto('/payroll', { waitUntil: 'domcontentloaded' });
  await expect(content(page).getByRole('heading', { name: 'Payroll Management' })).toBeVisible({ timeout: 60_000 });
}

async function openTab(page: Page, label: string): Promise<void> {
  const tab = content(page).getByRole('tab', { name: label, exact: true });
  await tab.click();
  await expect(tab).toHaveAttribute('aria-selected', 'true');
}

async function selectRun(page: Page, ariaLabel: string, period: string): Promise<string> {
  const picker = content(page).locator(`select[aria-label="${ariaLabel}"]`);
  const option = picker.locator('option', { hasText: period });
  await expect(option, `run picker "${ariaLabel}" must offer exactly one ${period} run`).toHaveCount(1);
  const runId = await option.getAttribute('value');
  await picker.selectOption(runId!);
  return runId!;
}

const runCard = (page: Page, period: string): Locator =>
  content(page).locator('[role="button"]').filter({ hasText: period }).first();
const approvalCard = (page: Page): Locator =>
  content(page).locator('div.surface').filter({ hasText: /^Payroll Run —/ }).first();
const batchCard = (page: Page, number: string): Locator =>
  content(page).locator('[role="button"]').filter({ hasText: number }).first();

interface RunRow {
  id: string; status: string; employeeCount: number;
  totalGrossSalary: number; totalNetSalary: number; lockedAtUtc: string | null;
}

/** The slice of the recorder's step context the helpers below need. */
interface StepApi {
  api: (method: string, path: string, init?: { token?: string; data?: unknown; recordId?: string })
    => Promise<{ status: number; json: any; text: string }>;
}

evidenceTest.describe('Evidence — one fully reconciled payroll story', () => {
  evidenceTest(
    'prerequisites → run → validation → maker approval → finance approval → lock → payslips → bank file → reconciliation',
    async ({ evidence, openAs, api }) => {
      let admin!: SignedInPersona;
      let maker!: SignedInPersona;
      let finance!: SignedInPersona;

      // Facts read from the product and used as the yardstick for everything after them.
      let period = '';
      let runId = '';
      let employeeCount = 0;
      let netTotal = 0;
      let grossTotal = 0;
      let netRendered = '';
      let currency = '';
      let batchId = '';
      let batchNumber = '';

      /**
       * The run as the API reports it right now — the persistence re-read every write uses.
       * Deliberately goes through the step's own `ctx.api`, so the re-read appears in the manifest
       * as a recorded call: "saved" has to be checkable, not merely claimed.
       */
      const readRun = async (ctx: StepApi, token: string, id: string): Promise<RunRow> => {
        const res = await ctx.api('GET', '/api/payroll/runs?pageSize=100', { token, recordId: id });
        expect(res.status, 'the run list must be readable for the persistence re-read').toBe(200);
        const row = ((res.json?.items ?? []) as RunRow[]).find((r) => r.id === id);
        if (!row) throw new Error(`[evidence] run ${id} is not in the run list on a fresh read.`);
        return row;
      };

      admin = await openAs(INTELLIFLOW_ADMIN, INTELLIFLOW_SLUG);
      maker = await openAs(INTELLIFLOW_HR_MGR, INTELLIFLOW_SLUG);
      finance = await openAs(INTELLIFLOW_FINANCE, INTELLIFLOW_SLUG);

      // ── 1. Prerequisites ────────────────────────────────────────────────────────────────────
      await evidence.step({
        id: 'prerequisites',
        actor: maker.persona,
        page: maker.page,
        action: 'Read the payroll prerequisites for the tenant before opening a run',
        expected:
          'The payroll dashboard states how many ACTIVE employees were checked against salary '
          + 'assignments, payroll profiles, IBANs and statutory readiness, and names every remaining gap.',
        record: { type: 'Tenant payroll readiness', id: INTELLIFLOW_SLUG },
        mustShowOnScreen: /Checked for \d+ active employees/,
        act: async (ctx) => {
          await gotoPayroll(maker.page);
          await openTab(maker.page, 'Dashboard');
          const panel = content(maker.page).getByText(/Checked for \d+ active employees/);
          await expect(panel, 'the prerequisites panel must state the population it checked').toBeVisible({
            timeout: 60_000,
          });
          const text = await panel.innerText();
          const checked = Number(/Checked for ([\d,]+) active employees/.exec(text)![1].replace(/,/g, ''));
          expect(checked, 'prerequisites must be evaluated against a real population').toBeGreaterThan(0);
          ctx.note(`The prerequisites panel reports ${checked} active employees checked.`);
        },
        persisted: async (ctx) => {
          const now = new Date();
          const r = await ctx.api(
            'GET',
            `/api/payroll/readiness?year=${now.getUTCFullYear()}&month=${now.getUTCMonth() + 1}`,
            { token: maker.token },
          );
          expect(r.status, 'the readiness endpoint must answer the maker').toBe(200);
          return {
            totalActiveEmployees: r.json.totalActiveEmployees,
            employeesWithSalary: r.json.employeesWithSalary,
            salaryCoveragePercent: r.json.salaryCoveragePercent,
            isReadyForProcessing: r.json.isReadyForProcessing,
          };
        },
        // The prerequisites panel can expand to a per-employee table. Names have no detectable
        // shape, so the column is named here rather than left to the pattern masker.
        maskExtra: (page) => [page.locator('main table tbody tr td:nth-child(1)')],
      });

      // ── R1. Incomplete data is still refused ────────────────────────────────────────────────
      // A KSA expat with no GOSI reference and no Iqama number cannot lawfully be paid, so the
      // product must not let them into a payroll population. Employees are BORN Draft (the create
      // request has no status field), and activation is the gate: POST {id}/activate raises
      // EmployeeActivationBlockedException, which the controller turns into a structured 422.
      let refusedEmployeeId: number | null = null;
      let bypassedEmployeeId: number | null = null;
      await evidence.step({
        id: 'refusal-incomplete-employee',
        actor: admin.persona,
        action: 'REFUSAL — try to activate a KSA expat who has no GOSI reference and no Iqama number',
        expected:
          'The activation is refused with a structured 422 naming the missing statutory data, and the '
          + 'employee stays Draft. An employee who cannot lawfully be paid must never join a payroll '
          + 'population.',
        record: { type: 'Employee (Draft, refused activation)', id: null },
        noCaptureReason:
          'Deliberately so: this refusal was driven through the API, so there is no '
          + 'screen of it to photograph; the 422 body recorded below is the whole of the evidence. A '
          + 'picture of the employee list here would show a screen that has nothing to do with the '
          + 'refusal, which is exactly the substitution this harness exists to prevent. The UI path '
          + 'for the same refusal is listed under Not run.',
        act: async (ctx) => {
          const company = await ksaCompany(ctx, admin.token);
          // `countryCode` is carried on a COMPLIANCE RECORD, not as a field of its own: it is the
          // only thing on the create request that sets it. It is stated here because the statutory
          // floor is jurisdiction-driven — GccReadinessFloor.Resolve(countryCode) returns an EMPTY
          // requirement list for a blank country, so an employee with no country has no gate at all.
          // See the tripwire immediately below, which is about exactly that.
          const created = await ctx.api('POST', '/api/employees', {
            token: admin.token,
            data: {
              employeeCode: `EV-REFUSE-${Date.now().toString().slice(-8)}`,
              manualEmployeeCode: true,
              englishName: 'Evidence Refusal Case',
              gender: 'Male',
              nationality: 'Indian',
              companyId: company.id,
              joiningDate: new Date().toISOString(),
              complianceRecords: [{
                countryCode: 'SA',
                fieldKey: 'Nationality',
                fieldLabel: 'Nationality',
                fieldValue: 'Indian',
                isSensitive: false,
                isRequired: true,
              }],
            },
          });
          expect(created.status, 'the Draft employee must be created so activation has something to refuse')
            .toBe(201);
          refusedEmployeeId = Number(created.json.id);
          ctx.setRecordId(String(refusedEmployeeId));
          expect(String(created.json.status ?? ''), 'an employee is born Draft').toBe('Draft');
          expect(String(created.json.countryCode ?? ''), 'the employee must be in the KSA jurisdiction').toBe('SA');

          const activate = await ctx.api('POST', `/api/employees/${refusedEmployeeId}/activate`, {
            token: admin.token,
            data: {
              status: 'Active',
              effectiveDate: new Date().toISOString().slice(0, 10),
              reason: 'evidence harness: activation with incomplete statutory data',
            },
            recordId: String(refusedEmployeeId),
          });
          expect(
            activate.status,
            'activating a KSA expat with no GOSI reference and no Iqama number must be REFUSED, '
            + 'not saved. A 2xx here would mean the pay gate can be walked through.',
          ).toBe(422);
          expect(activate.json?.error, 'the refusal must be the structured activation gate').toBe(
            'employee_not_activatable',
          );
          const blocking: Array<{ key: string }> = activate.json?.blocking ?? [];
          const payBlocking: Array<{ key: string }> = activate.json?.payBlocking ?? [];
          expect(
            blocking.map((b) => b.key),
            'the Iqama number is the activate-gated statutory field for a non-GCC expat in KSA',
          ).toContain('IqamaNumber');
          expect(
            payBlocking.map((b) => b.key),
            'the GOSI reference is pay-gated: it is obtained after hire, so it blocks PAY, not activation',
          ).toContain('GosiReference');
          ctx.note(
            `Draft employee #${refusedEmployeeId} (SA, Indian national) created; activation refused with `
            + `HTTP ${activate.status} employee_not_activatable. Activate-blocking: `
            + `${blocking.map((b) => b.key).join(', ')}. Pay-blocking: `
            + `${payBlocking.map((b) => b.key).join(', ')}.`,
          );
        },
        persisted: async (ctx) => {
          const row = await ctx.api('GET', `/api/employees/${refusedEmployeeId}`, {
            token: admin.token, recordId: String(refusedEmployeeId),
          });
          expect(row.status, 'the refused employee must still be readable').toBe(200);
          expect(
            String(row.json.status ?? ''),
            'a refused activation must leave the employee exactly where it was',
          ).toBe('Draft');
          return { employeeId: refusedEmployeeId, status: row.json.status, activated: false };
        },
        persistedVia: 'fresh GET of the employee — a refused activation must leave it Draft',
      });

      // ── 2. Create the run ───────────────────────────────────────────────────────────────────
      await evidence.step({
        id: 'run-created',
        actor: admin.persona,
        page: admin.page,
        action: 'Create the payroll run for the first period that does not have one',
        expected: 'A new run exists in Draft for that period, and the list renders every run the API returned.',
        record: { type: 'PayrollRun' },
        mustShowOnScreen: /Draft/,
        act: async (ctx) => {
          await gotoPayroll(admin.page);
          const runsFetch = admin.page.waitForResponse(
            (r) => /\/api\/payroll\/runs\?/.test(r.url()) && r.request().method() === 'GET',
            { timeout: 120_000 },
          );
          await openTab(admin.page, 'Payroll Runs');
          const existingTotal = ((await (await runsFetch).json()) as { total: number }).total;

          const existingCards = content(admin.page)
            .locator('[role="button"]').filter({ hasText: /^[A-Z][a-z]{2} \d{4}/ });
          await expect(
            existingCards, 'the Payroll Runs list must render every run the API returned',
          ).toHaveCount(existingTotal);

          const taken = new Set(
            (await existingCards.allInnerTexts())
              .map((l) => /^([A-Z][a-z]{2}) (\d{4})/.exec(l.trim()))
              .filter((m): m is RegExpExecArray => m !== null)
              .map((m) => `${m[1]} ${m[2]}`),
          );
          const now = new Date();
          const candidates: string[] = [];
          for (let i = 0; i < 18; i++) {
            const d = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth() + i, 1));
            if (d.getUTCFullYear() > now.getUTCFullYear() + 1) break; // CreateRun caps year at +1
            candidates.push(`${MONTHS[d.getUTCMonth()]} ${d.getUTCFullYear()}`);
          }
          const free = candidates.find((c) => !taken.has(c));
          expect(free, `every selectable period already has a run (${[...taken].join(', ')})`).toBeTruthy();
          period = free!;
          const [monthLabel, yearLabel] = period.split(' ');

          await content(admin.page).getByRole('button', { name: 'New Payroll Run' }).click();
          await content(admin.page).getByLabel('Year', { exact: true }).fill(yearLabel);
          await content(admin.page).getByLabel('Month', { exact: true }).selectOption({ label: monthLabel });
          await content(admin.page).getByRole('button', { name: 'Create Run' }).click();
          await expect(
            content(admin.page).getByRole('button', { name: 'Create Run' }),
            'creating the run must close the dialog — the dialog stays open on a rejected create',
          ).toHaveCount(0);

          const card = runCard(admin.page, period);
          await expect(card, `the ${period} run must appear in the list`).toBeVisible({ timeout: 60_000 });
          await expect(card).toContainText('Draft');

          runId = await selectRunIdFromApi(ctx, admin.token, period);
          ctx.setRecordId(runId);
          ctx.note(`Run for ${period} created in Draft as ${runId}.`);
        },
        persisted: async (ctx) => {
          const row = await readRun(ctx, admin.token, runId);
          return { runId: row.id, status: row.status, employeeCount: row.employeeCount };
        },
      });

      // ── 3. Process the run ──────────────────────────────────────────────────────────────────
      await evidence.step({
        id: 'run-processed',
        actor: admin.persona,
        page: admin.page,
        action: 'Process the run so every employee on it gets a calculated salary slip',
        expected:
          'The run leaves Draft for Processed and carries a non-zero employee count and net-pay total.',
        record: { type: 'PayrollRun', id: runId },
        mustShowOnScreen: /Processed/,
        act: async (ctx) => {
          const card = runCard(admin.page, period);
          await card.getByRole('button', { name: 'Process' }).click();
          await expect(card, 'processing must move the run out of Draft').toContainText('Processed', {
            timeout: 180_000,
          });
          ctx.setRecordId(runId);
        },
        persisted: async (ctx) => {
          const row = await readRun(ctx, admin.token, runId);
          employeeCount = row.employeeCount;
          grossTotal = Number(row.totalGrossSalary);
          netTotal = Number(row.totalNetSalary);
          expect(employeeCount, 'a processed run must cover at least one employee').toBeGreaterThan(0);
          expect(netTotal, 'a processed run must carry a non-zero net-pay total').toBeGreaterThan(0);
          ctx.note(
            `${employeeCount} employees, gross ${grossTotal.toFixed(2)}, net ${netTotal.toFixed(2)}.`,
          );
          return {
            status: row.status, employeeCount, totalGrossSalary: grossTotal, totalNetSalary: netTotal,
          };
        },
      });

      // ── 4. Validation ───────────────────────────────────────────────────────────────────────
      await evidence.step({
        id: 'validation',
        actor: maker.persona,
        page: maker.page,
        action: 'Run payroll validation on the processed run and read every finding',
        expected:
          'Validation completes with ZERO errors, and every warning it does return is one this story '
          + 'can explain by code — an unexplained warning fails the step rather than being counted '
          + 'and carried into approval.',
        record: { type: 'PayrollRun', id: runId },
        act: async (ctx) => {
          await gotoPayroll(maker.page);
          await openTab(maker.page, 'Validation');
          expect(await selectRun(maker.page, 'Select payroll run', period)).toBe(runId);
          await content(maker.page).getByRole('button', { name: 'Run Validation' }).click();
          // The tab shows either the findings list or its "no saved validation issues" state; both
          // are settled outcomes, and which one appeared is recorded from the API below.
          const results = await ctx.api('GET', `/api/payroll/runs/${runId}/validate`, { token: maker.token });
          expect(results.status, 'saved validation results must be readable').toBe(200);
          const rows: Array<{ severity: string; code: string; message: string }> = results.json ?? [];
          const errors = rows.filter((r) => r.severity === 'Error');
          const warnings = rows.filter((r) => r.severity === 'Warning');
          const codes = [...new Set(rows.map((r) => r.code))];
          ctx.note(
            `Validation returned ${rows.length} finding(s): ${errors.length} error(s), `
            + `${warnings.length} warning(s)${codes.length ? `; codes: ${codes.join(', ')}` : ''}.`,
          );
          expect(
            errors.length,
            'this story does not approve a run with unresolved validation ERRORS. If this fails, the '
            + `codes are: ${errors.map((e) => `${e.code} — ${e.message}`).join(' | ')}`,
          ).toBe(0);

          // The register's standard: every remaining warning is EXPLAINED, not counted. A code with
          // no written rationale fails here, so the explanations cannot silently fall behind the
          // engine.
          const unexplained = [...new Set(warnings.map((w) => w.code))]
            .filter((code) => !WARNING_EXPLANATIONS[code]);
          expect(
            unexplained,
            'every warning carried into approval must have a written explanation in '
            + 'WARNING_EXPLANATIONS at the top of this file. These do not: '
            + unexplained.map((c) => `${c} — "${warnings.find((w) => w.code === c)?.message}"`).join(' | '),
          ).toEqual([]);
        },
        persisted: async (ctx) => {
          const r = await ctx.api('GET', `/api/payroll/runs/${runId}/validate`, { token: maker.token });
          const rows: Array<{ severity: string; code: string; message: string; isResolved: boolean }> = r.json ?? [];
          const warningCodes = [...new Set(rows.filter((x) => x.severity === 'Warning').map((x) => x.code))];
          return {
            findings: rows.length,
            errors: rows.filter((x) => x.severity === 'Error').length,
            warnings: rows.filter((x) => x.severity === 'Warning').length,
            warningCodes,
            // The explanation for each code travels WITH the evidence, so a reader of the bundle
            // does not have to find this file to learn why the run was approved over them.
            warningsExplained: Object.fromEntries(
              warningCodes.map((code) => [
                code,
                `${rows.filter((x) => x.code === code).length}× — ${WARNING_EXPLANATIONS[code]}`,
              ]),
            ),
          };
        },
        persistedVia: 'fresh GET of the saved validation results',
      });

      // ── 5. The maker approves, onward ───────────────────────────────────────────────────────
      await evidence.step({
        id: 'maker-approval',
        actor: maker.persona,
        page: maker.page,
        action: 'The HR Manager (maker) approves the run and sends it to Finance',
        expected:
          'The run moves to Pending Finance Review, and the approval chain records the maker step. '
          + "The maker is told, on screen, that their approval is not the final one.",
        record: { type: 'PayrollRun', id: runId },
        mustShowOnScreen: 'Awaiting Finance Controller approval.',
        act: async (ctx) => {
          await openTab(maker.page, 'Approvals');
          expect(await selectRun(maker.page, 'Select payroll run to approve', period)).toBe(runId);

          const summary = approvalCard(maker.page).getByText(/\d+ employees\s*·\s*Gross/);
          await expect(summary, 'the approvals card must state the run size and its totals').toBeVisible();
          const parsed = /(\d+)\s+employees\s*·\s*Gross\s+([A-Z]{3})\s+([\d,]+\.\d{2})\s*·\s*Net\s+([A-Z]{3})\s+([\d,]+\.\d{2})/
            .exec(await summary.innerText());
          expect(parsed, 'the approvals summary must render employees, gross and net').toBeTruthy();
          expect(Number(parsed![1]), 'the screen must show the same headcount the API reports').toBe(employeeCount);
          currency = parsed![4];
          netRendered = parsed![5];
          expect(parseAmount(netRendered), "the screen's net total must equal the run's").toBeCloseTo(netTotal, 2);
          expect(parseAmount(parsed![3]), "the screen's gross total must equal the run's").toBeCloseTo(grossTotal, 2);

          await expect(
            content(maker.page).getByRole('button', { name: 'Approve — Final' }),
            'a maker must never be offered the final approval on a Processed run',
          ).toHaveCount(0);
          await content(maker.page).getByRole('button', { name: 'Approve → Send to Finance' }).click();
          await expect(
            approvalCard(maker.page).getByText('Pending Finance Review', { exact: true }),
          ).toBeVisible({ timeout: 60_000 });
          ctx.note(
            `Screen and API agree: ${employeeCount} employees, gross ${currency} ${parsed![3]}, `
            + `net ${currency} ${netRendered}. The run is now Pending Finance Review.`,
          );
        },
        persisted: async (ctx) => {
          const row = await readRun(ctx, maker.token, runId);
          return { status: row.status };
        },
      });

      // ── R3. The maker cannot complete it alone ──────────────────────────────────────────────
      await evidence.step({
        id: 'refusal-maker-cannot-finalise',
        actor: maker.persona,
        page: maker.page,
        action: 'REFUSAL — the maker tries to approve a second time and complete the run alone',
        expected:
          'No approval control survives on the run the maker already signed, the API refuses a second '
          + 'approval with HTTP 400, and — the part that matters — the run does not move.',
        record: { type: 'PayrollRun', id: runId },
        mustShowOnScreen: 'Awaiting Finance Controller approval.',
        allowIdentical:
          'not expected to be identical to any earlier capture; declared only because this step '
          + 'deliberately photographs the same card as the previous one after a refusal that must '
          + 'have changed nothing on it.',
        act: async (ctx) => {
          for (const name of ['Approve — Final', 'Approve → Send to Finance', 'Send Back to Payroll']) {
            await expect(
              approvalCard(maker.page).getByRole('button', { name, exact: true }),
              `the maker must have no "${name}" control on a run they already signed`,
            ).toHaveCount(0);
          }
          const retry = await ctx.api('POST', `/api/payroll/runs/${runId}/approve`, {
            token: maker.token,
            data: { notes: 'maker attempting to complete the run alone' },
            recordId: runId,
          });
          expect(retry.status, 'the API must refuse the maker a second, completing approval').toBe(400);
          ctx.note(
            `Three approval controls absent from the maker's card; the API answered HTTP ${retry.status}: `
            + `${retry.text.replace(/\s+/g, ' ').slice(0, 200)}`,
          );
        },
        persisted: async (ctx) => {
          const row = await readRun(ctx, maker.token, runId);
          expect(row.status, 'a refused maker approval must leave the run exactly where it was')
            .toBe('PendingFinanceReview');
          return { status: row.status, unchanged: true };
        },
      });

      // ── 6. The finance approval — a DIFFERENT authorised person ─────────────────────────────
      await evidence.step({
        id: 'finance-approval',
        actor: finance.persona,
        page: finance.page,
        action: 'The Finance Approver — a different authorised person — gives the final approval',
        expected:
          'The run reaches Approved, reads as ready to lock, and the approval chain holds BOTH the '
          + 'maker step and the finance step, signed by two different people.',
        record: { type: 'PayrollRun', id: runId },
        mustShowOnScreen: 'Payroll run has been approved and is ready to lock.',
        act: async (ctx) => {
          await gotoPayroll(finance.page);
          await openTab(finance.page, 'Approvals');
          expect(await selectRun(finance.page, 'Select payroll run to approve', period)).toBe(runId);
          await expect(
            approvalCard(finance.page).getByText('Pending Finance Review', { exact: true }),
            'the finance approver must pick the run up exactly where the maker left it',
          ).toBeVisible();
          await expect(
            content(finance.page).getByRole('button', { name: 'Send Back to Payroll' }),
            'the finance approver must hold the send-back control the maker does not',
          ).toBeVisible();

          await content(finance.page).getByRole('button', { name: 'Approve — Final' }).click();
          await expect(
            approvalCard(finance.page).getByText('Approved', { exact: true }),
            'the finance approval must move the run to Approved',
          ).toBeVisible({ timeout: 60_000 });
          await expect(
            content(finance.page).getByText('FinanceReview', { exact: true }),
            'the approval chain must record the finance step',
          ).toBeVisible();
          await expect(
            content(finance.page).getByText('PayrollReview', { exact: true }),
            'the approval chain must still record the maker step alongside it',
          ).toBeVisible();
          const chain = await ctx.api('GET', `/api/payroll/runs/${runId}/approvals`, { token: finance.token });
          expect(chain.status).toBe(200);
          const rows: Array<{ approvalLevel: string; decidedByUserId: string | null }> = chain.json ?? [];
          const approvers = new Set(rows.map((r) => r.decidedByUserId).filter(Boolean));
          expect(
            approvers.size,
            'the two approvals must have been given by two DIFFERENT people — that is the control',
          ).toBeGreaterThanOrEqual(2);
          ctx.note(
            `Approval chain: ${rows.map((r) => r.approvalLevel).join(' → ')}, signed by `
            + `${approvers.size} distinct users.`,
          );
        },
        persisted: async (ctx) => {
          const row = await readRun(ctx, finance.token, runId);
          const chain = await ctx.api('GET', `/api/payroll/runs/${runId}/approvals`, { token: finance.token });
          const rows: Array<{ approvalLevel: string; decision: string; decidedByUserId: string | null }> = chain.json ?? [];
          return {
            status: row.status,
            approvalLevels: rows.map((r) => `${r.approvalLevel}:${r.decision}`),
            distinctApprovers: new Set(rows.map((r) => r.decidedByUserId).filter(Boolean)).size,
          };
        },
      });

      // ── R2. A payment batch is refused before the lock ──────────────────────────────────────
      // Taken while the run is Approved but NOT yet Locked, so it proves the LOCK is the gate
      // rather than the approval. Doing it after the lock would prove nothing.
      await evidence.step({
        id: 'refusal-batch-before-lock',
        actor: admin.persona,
        page: admin.page,
        action: 'REFUSAL — open the bank export while the run is Approved but not yet Locked',
        expected:
          'Create Payment Batch is disabled, and the screen says why, naming the run\'s actual '
          + 'current status rather than a generic message. No batch exists.',
        record: { type: 'PayrollRun', id: runId },
        mustShowOnScreen: /A payment batch can only be created once the run is/,
        act: async (ctx) => {
          await gotoPayroll(admin.page);
          await openTab(admin.page, 'Bank / WPS Files');
          expect(await selectRun(admin.page, 'Select payroll run', period)).toBe(runId);
          const createBatch = content(admin.page).getByRole('button', { name: 'Create Payment Batch' });
          await expect(createBatch, 'the Bank/WPS tab must offer the batch control').toBeVisible();
          await expect(createBatch, 'a batch must not be creatable before the run is locked').toBeDisabled();
          await expect(
            content(admin.page).getByText(/This run is\s*Approved\s*\./),
            "the notice must name the run's actual current status",
          ).toBeVisible();
          ctx.note(
            'Create Payment Batch is disabled and the screen states "This run is Approved." — the '
            + 'lock, not the approval, is the gate.',
          );
        },
        persisted: async (ctx) => {
          const batches = await ctx.api('GET', `/api/payroll/payment-batches?runId=${runId}`, {
            token: admin.token,
          });
          const rows: unknown[] = batches.json ?? [];
          expect(rows.length, 'no payment batch may exist for a run that is not locked').toBe(0);
          return {
            paymentBatchesForThisRun: rows.length,
            runStatusAtRefusal: (await readRun(ctx, admin.token, runId)).status,
          };
        },
        persistedVia: "fresh GET of the run's payment batches — none may exist yet",
      });

      // ── 7. Lock ─────────────────────────────────────────────────────────────────────────────
      await evidence.step({
        id: 'run-locked',
        actor: finance.persona,
        page: finance.page,
        action: 'The Finance Approver locks the approved run',
        expected:
          'The run reaches Locked, carries a lock timestamp, and offers no further lifecycle control.',
        record: { type: 'PayrollRun', id: runId },
        mustShowOnScreen: /Locked/,
        act: async (ctx) => {
          await openTab(finance.page, 'Payroll Runs');
          const card = runCard(finance.page, period);
          await expect(card).toBeVisible({ timeout: 60_000 });
          await card.getByRole('button', { name: 'Lock' }).click();
          await expect(card, 'locking must move the run to Locked').toContainText('Locked', { timeout: 180_000 });
          for (const name of ['Lock', 'Process']) {
            await expect(
              card.getByRole('button', { name }),
              `a locked run must not still offer a ${name} control`,
            ).toHaveCount(0);
          }
          ctx.note('The run is Locked and exposes no Lock or Process control.');
        },
        persisted: async (ctx) => {
          const row = await readRun(ctx, finance.token, runId);
          expect(row.lockedAtUtc, 'a locked run must carry a lock timestamp').toBeTruthy();
          return { status: row.status, lockedAtUtc: row.lockedAtUtc };
        },
      });

      // ── 8. Payslips ─────────────────────────────────────────────────────────────────────────
      await evidence.step({
        id: 'payslips',
        actor: maker.persona,
        page: maker.page,
        action: 'Generate the payslips for the locked run',
        expected:
          `Exactly ${employeeCount} payslips, one per distinct employee, each with a real payslip `
          + 'number, and each published to employee self-service (the run is locked, so the month is '
          + 'already being paid).',
        record: { type: 'PayrollRun', id: runId },
        mustShowOnScreen: 'Total Payslips',
        act: async (ctx) => {
          await gotoPayroll(maker.page);
          await openTab(maker.page, 'Payslips');
          expect(await selectRun(maker.page, 'Select payroll run', period)).toBe(runId);
          await content(maker.page).getByRole('button', { name: 'Generate Payslips' }).click();

          const rows = content(maker.page).locator('table tbody tr');
          await expect(rows.first(), 'generating payslips must render a payslip table').toBeVisible({
            timeout: 180_000,
          });
          await expect(
            rows, `the run covers ${employeeCount} employees, so it must produce exactly that many payslips`,
          ).toHaveCount(employeeCount);

          const kpi = content(maker.page).locator('div.surface').filter({ hasText: 'Total Payslips' }).first();
          expect(
            (await kpi.innerText()).trim(),
            "the Total Payslips KPI must equal the run's employee count",
          ).toMatch(new RegExp(`^${employeeCount}\\b`));

          const numbers = (await rows.locator('td:nth-child(2)').allInnerTexts()).map((s) => s.trim());
          expect(numbers.filter((n) => !/^PS-\S+-\d{14}$/.test(n)), 'every row must carry a real payslip number').toEqual([]);
          expect(new Set(numbers).size, 'payslip numbers must be unique').toBe(employeeCount);
          const employees = (await rows.locator('td:nth-child(1)').allInnerTexts()).map((s) => s.trim());
          expect(new Set(employees).size, 'each payslip must belong to a distinct employee').toBe(employeeCount);
          expect(
            employees.filter((e) => e.length === 0 || /^Emp #\d+$/.test(e)),
            'every payslip row must name its employee, not a placeholder code',
          ).toEqual([]);
          ctx.note(`${employeeCount} payslips, ${new Set(numbers).size} distinct numbers, ${new Set(employees).size} distinct employees.`);
        },
        persisted: async (ctx) => {
          const r = await ctx.api('GET', `/api/payroll/runs/${runId}/payslips?page=1&pageSize=200`, {
            token: maker.token,
          });
          expect(r.status).toBe(200);
          // PayslipListItemDto is header-only: id, employee id/code/name, payslip number and
          // isPublishedToEss. There is no `status` field, so publication is read from the flag the
          // API actually returns rather than from a field name guessed off the UI column.
          const items: Array<{ payslipNumber?: string; isPublishedToEss?: boolean; employeeId?: number }> =
            r.json?.items ?? [];
          expect(items.length, 'the persisted payslip count must equal the run headcount').toBe(employeeCount);
          expect(
            items.filter((p) => p.isPublishedToEss !== true).length,
            'payslips generated on a LOCKED run must be published to employee self-service — the month '
            + 'is already being paid, so an unpublished payslip is a payslip the employee cannot see',
          ).toBe(0);
          return {
            payslips: items.length,
            distinctEmployees: new Set(items.map((p) => p.employeeId)).size,
            distinctPayslipNumbers: new Set(items.map((p) => p.payslipNumber)).size,
            publishedToEss: items.filter((p) => p.isPublishedToEss === true).length,
          };
        },
        maskExtra: (page) => [page.locator('main table tbody tr td:nth-child(1)')],
      });

      // ── 9. The payment batch, once the run is locked ────────────────────────────────────────
      await evidence.step({
        id: 'payment-batch',
        actor: admin.persona,
        page: admin.page,
        action: 'Create the payment batch now that the run is locked',
        expected:
          "The control is enabled, the blocking notice is gone, and the batch's total equals the "
          + "run's net pay with one payment line per employee.",
        record: { type: 'PayrollPaymentBatch' },
        mustShowOnScreen: /Draft/,
        act: async (ctx) => {
          await gotoPayroll(admin.page);
          await openTab(admin.page, 'Bank / WPS Files');
          expect(await selectRun(admin.page, 'Select payroll run', period)).toBe(runId);
          const createBatch = content(admin.page).getByRole('button', { name: 'Create Payment Batch' });
          await expect(createBatch, 'locking the run must enable payment batch creation').toBeEnabled();
          await expect(
            content(admin.page).getByText(/A payment batch can only be created once the run is/),
            'the lock warning must disappear once the run is locked',
          ).toHaveCount(0);

          const created = admin.page.waitForResponse(
            (r) => r.url().includes(`/api/payroll/runs/${runId}/payment-batches`) && r.request().method() === 'POST',
          );
          await createBatch.click();
          const response = await created;
          expect(response.status(), 'creating a payment batch on a Locked run must succeed').toBe(201);
          const batch = (await response.json()) as { id: string; batchNumber: string; totalAmount: number; currency: string };
          batchId = batch.id;
          batchNumber = batch.batchNumber;
          ctx.setRecordId(batchId);

          expect(batch.totalAmount, "the batch total must equal the run's net-pay total").toBeCloseTo(netTotal, 2);
          expect(batch.currency, 'the batch must be denominated in the tenant currency').toBe(currency);
          await expect(batchCard(admin.page, batchNumber)).toBeVisible({ timeout: 60_000 });
          await expect(batchCard(admin.page, batchNumber)).toContainText(`${currency} ${netRendered}`);
          ctx.note(`Batch ${batchNumber} created in Draft for ${currency} ${netRendered}.`);
        },
        persisted: async (ctx) => {
          const records = await ctx.api('GET', `/api/payroll/payment-batches/${batchId}/records`, {
            token: admin.token, recordId: batchId,
          });
          expect(records.status).toBe(200);
          const rows: Array<{ amount: number; status: string }> = records.json ?? [];
          expect(rows.length, 'one payment line per employee on the run').toBe(employeeCount);
          const sum = rows.reduce((a, r) => a + Number(r.amount), 0);
          expect(sum, "the payment lines must add up to the run's net-pay total").toBeCloseTo(netTotal, 2);
          return {
            batchId, batchNumber, paymentRecords: rows.length,
            sumOfPaymentLines: Number(sum.toFixed(2)),
            recordStatuses: [...new Set(rows.map((r) => r.status))],
          };
        },
      });

      // ── 10. The bank export — a FILE, and nobody is paid ────────────────────────────────────
      await evidence.step({
        id: 'bank-export-generation-only',
        actor: admin.persona,
        page: admin.page,
        action: 'Generate the WPS/SIF bank file (generation only — this must not pay anybody)',
        expected:
          "The batch reaches File Generated with a named file, a content hash, one record per "
          + "employee and a total equal to the run's net pay. The run stays Locked, the batch is not "
          + 'settled, and no payment record says paid.',
        record: { type: 'PayrollPaymentBatch', id: batchId },
        mustShowOnScreen: 'File Generated',
        act: async (ctx) => {
          const attempt = admin.page.waitForResponse(
            (r) => r.url().includes(`/api/payroll/payment-batches/${batchId}/wps-file`)
              && r.request().method() === 'POST',
          );
          await batchCard(admin.page, batchNumber).getByRole('button', { name: 'Generate WPS/SIF' }).click();
          const exported = await attempt;
          expect(
            exported.status(),
            "the operator's own Generate WPS/SIF must complete; a 422 here means an ACTIVE employee has "
            + 'drifted pay-blocked',
          ).toBe(200);
          const file = (await exported.json()) as {
            sifFileName: string; employeeCount: number; totalSalaryAmount: number; fileHash: string;
          };
          expect(file.employeeCount, 'one record per employee on the run').toBe(employeeCount);
          expect(
            Number(file.totalSalaryAmount),
            "the wage file total must equal the run's net pay",
          ).toBeCloseTo(netTotal, 2);
          expect(file.fileHash, 'the export must record a content hash').toMatch(/^[0-9a-f]{64}$/);

          await gotoPayroll(admin.page);
          await openTab(admin.page, 'Bank / WPS Files');
          expect(await selectRun(admin.page, 'Select payroll run', period)).toBe(runId);
          const filed = batchCard(admin.page, batchNumber);
          await expect(filed, 'a batch with a wage file must read as File Generated').toContainText('File Generated');
          await expect(
            filed.getByRole('button', { name: 'Generate WPS/SIF' }),
            'a batch that already has a wage file must not offer to generate another',
          ).toHaveCount(0);
          ctx.note(
            `${file.sifFileName}: ${file.employeeCount} records, total ${file.totalSalaryAmount}, `
            + `sha256 ${file.fileHash.slice(0, 16)}…`,
          );
        },
        persisted: async (ctx) => {
          const run = await readRun(ctx, admin.token, runId);
          const batches = await ctx.api('GET', `/api/payroll/payment-batches?runId=${runId}`, { token: admin.token });
          const batch = ((batches.json ?? []) as Array<{ id: string; status: string; wpsStatus: string }>)
            .find((b) => b.id === batchId)!;
          const records = await ctx.api('GET', `/api/payroll/payment-batches/${batchId}/records`, { token: admin.token });
          const rows: Array<{ status: string }> = records.json ?? [];
          const paidLike = /paid|settled|disbursed/i;

          expect(run.status, 'generating a bank file must not move the run past Locked').toBe('Locked');
          expect(
            paidLike.test(batch.status),
            `THE ASSERTION THIS STEP EXISTS FOR: the batch must not be marked paid by generating a file `
            + `(status was "${batch.status}")`,
          ).toBe(false);
          expect(
            rows.filter((r) => paidLike.test(r.status)).length,
            'no payment record may be marked paid by generating a file',
          ).toBe(0);
          return {
            runStatus: run.status,
            batchStatus: batch.status,
            batchWpsStatus: batch.wpsStatus,
            paymentRecordStatuses: [...new Set(rows.map((r) => r.status))],
            anyRecordMarkedPaid: false,
            settleEndpointCalled: false,
          };
        },
        persistedVia:
          'fresh GET of the run, the batch and every payment record — the "nobody was paid" check',
      });

      // ── 11. Reconciliation ──────────────────────────────────────────────────────────────────
      await evidence.step({
        id: 'reconciliation',
        actor: finance.persona,
        page: finance.page,
        action: 'Reconcile the displayed totals against the source records',
        expected:
          "The reconciliation report's current-period gross and net equal the run's own totals, which "
          + 'equal the sum of the per-employee salary slips, which equal the sum of the payment lines, '
          + 'which equal the wage file total.',
        record: { type: 'PayrollRun', id: runId },
        mustShowOnScreen: /Reconciliation|Variance|Headcount/i,
        act: async (ctx) => {
          await gotoPayroll(finance.page);
          await openTab(finance.page, 'Reconciliation');
          const picker = content(finance.page).locator('select').first();
          await expect(picker).toBeVisible({ timeout: 60_000 });
          const option = picker.locator('option', { hasText: period });
          if (await option.count()) await picker.selectOption(await option.first().getAttribute('value') ?? '');

          const report = await ctx.api(
            'GET', `/api/payroll/reports/reconciliation?runId=${runId}`, { token: finance.token, recordId: runId },
          );
          expect(report.status, 'the reconciliation report must be readable').toBe(200);
          expect(
            Number(report.json.currentTotalNet),
            "the reconciliation report's net must equal the run's net",
          ).toBeCloseTo(netTotal, 2);
          expect(
            Number(report.json.currentTotalGross),
            "the reconciliation report's gross must equal the run's gross",
          ).toBeCloseTo(grossTotal, 2);
          expect(
            Number(report.json.currentHeadcount),
            "the reconciliation report's headcount must equal the run's",
          ).toBe(employeeCount);
          ctx.note(
            `Reconciliation: headcount ${report.json.currentHeadcount}, gross `
            + `${Number(report.json.currentTotalGross).toFixed(2)}, net `
            + `${Number(report.json.currentTotalNet).toFixed(2)}, `
            + `${report.json.flaggedVariances} flagged variance(s).`,
          );
        },
        persisted: async (ctx) => {
          // Five independent sources for the same money, read fresh and compared.
          const run = await readRun(ctx, finance.token, runId);
          const slips = await ctx.api('GET', `/api/payroll/runs/${runId}/slips?page=1&pageSize=500`, { token: finance.token });
          const slipRows: Array<{ netSalary: number; grossSalary: number }> = slips.json?.items ?? [];
          const slipNet = slipRows.reduce((a, s) => a + Number(s.netSalary), 0);
          const records = await ctx.api('GET', `/api/payroll/payment-batches/${batchId}/records`, { token: admin.token });
          const lineSum = ((records.json ?? []) as Array<{ amount: number }>)
            .reduce((a, r) => a + Number(r.amount), 0);
          const recon = await ctx.api('GET', `/api/payroll/reports/reconciliation?runId=${runId}`, { token: finance.token });

          expect(slipRows.length, 'one salary slip per employee on the run').toBe(employeeCount);
          expect(slipNet, "the salary slips must sum to the run's net total").toBeCloseTo(netTotal, 2);
          expect(lineSum, "the bank payment lines must sum to the run's net total").toBeCloseTo(netTotal, 2);
          return {
            runTotalNet: Number(run.totalNetSalary),
            sumOfSalarySlipNet: Number(slipNet.toFixed(2)),
            sumOfBankPaymentLines: Number(lineSum.toFixed(2)),
            reconciliationReportNet: Number(Number(recon.json.currentTotalNet).toFixed(2)),
            netShownOnApprovalScreen: parseAmount(netRendered),
            allFiveAgree: true,
          };
        },
        persistedVia:
          'fresh GETs of the run, its salary slips, the bank payment lines and the reconciliation report',
      });

      // ── R1b. DEFECT TRIPWIRE — the same refusal, bypassed ───────────────────────────────────
      // Placed AFTER the payroll story, not beside R1, for a boring but necessary reason: this step
      // succeeds in ACTIVATING an employee, and an active employee joins the tenant's payroll
      // population. Run before the payroll run is created, it changed the headcount the whole story
      // reconciles against — and an employee with no salary structure and no IBAN would have taken
      // the bank export down with it. The defect is about activation, not about payroll, so it is
      // recorded where it cannot contaminate the money.
      //
      // Found by this run, not looked for. R1 above only refuses because the create request carried
      // a compliance record naming SA. Send the IDENTICAL employee — same KSA legal entity, same
      // Indian nationality, same absent Iqama and GOSI — WITHOUT that record, and `CountryCode`
      // stays empty (EmployeeManagementService line ~1340 sets it only from a compliance record and
      // never falls back to the company's country). GccReadinessFloor.Resolve("") then returns an
      // EMPTY requirement list, so the activation floor does not exist and the employee activates.
      //
      // EmployeeActivationGateTests constructs its fixtures with CountryCode = "SA" set directly on
      // the entity, so it exercises the gate but never the derivation — which is why this has held
      // green. GET /api/employees/field-catalog documents the intended rule in as many words:
      // "Explicit countryCode wins, else the company's country."
      //
      // This step asserts the CURRENT, WRONG behaviour deliberately, in the house tripwire style: it
      // is written to go RED the day the gap closes, so nobody has to remember to come back. When it
      // fails with 422, the defect is fixed — delete this step and fold the case into R1.
      await evidence.step({
        id: 'defect-activation-floor-bypassed-without-country',
        actor: admin.persona,
        action:
          'DEFECT TRIPWIRE — the same incomplete KSA expat, created without a country, activates anyway',
        expected:
          'SHOULD be the same 422 as R1: the employee is attached to a KSA legal entity, so the KSA '
          + 'floor should apply. It does NOT. This step records the product as it is today — an HTTP '
          + '200 activation — and is written to FAIL the day the gap is closed.',
        record: { type: 'Employee (Draft, wrongly activatable)', id: null },
        noCaptureReason:
          'Driven through the API, like R1, so there is no screen of it. The two API '
          + 'calls below, differing only in the absent complianceRecords, are the whole of the evidence.',
        act: async (ctx) => {
          const company = await ksaCompany(ctx, admin.token);
          const created = await ctx.api('POST', '/api/employees', {
            token: admin.token,
            data: {
              employeeCode: `EV-BYPASS-${Date.now().toString().slice(-8)}`,
              manualEmployeeCode: true,
              englishName: 'Evidence Bypass Case',
              gender: 'Male',
              nationality: 'Indian',
              companyId: company.id,
              joiningDate: new Date().toISOString(),
              // complianceRecords deliberately omitted — that is the whole difference from R1.
            },
          });
          expect(created.status).toBe(201);
          const id = Number(created.json.id);
          ctx.setRecordId(String(id));
          expect(
            String(created.json.countryCode ?? ''),
            'THE DEFECT: an employee attached to a KSA legal entity is stored with NO country, so no '
            + 'jurisdiction floor can resolve for them',
          ).toBe('');

          const activate = await ctx.api('POST', `/api/employees/${id}/activate`, {
            token: admin.token,
            data: {
              status: 'Active',
              effectiveDate: new Date().toISOString().slice(0, 10),
              reason: 'evidence harness: tripwire for the bypassed activation floor',
            },
            recordId: String(id),
          });
          expect(
            activate.status,
            'TRIPWIRE: this records the CURRENT behaviour. When this line starts failing with 422, the '
            + 'activation floor has been fixed to fall back to the company country — delete this step '
            + 'and fold the case into R1.',
          ).toBe(200);
          ctx.note(
            `Employee #${id} — same KSA legal entity, same Indian nationality, same absent Iqama and `
            + `GOSI reference as R1 — was stored with an EMPTY countryCode and ACTIVATED with HTTP `
            + `${activate.status}. R1's 422 differs only by a complianceRecords entry naming SA.`,
          );
          bypassedEmployeeId = id;
        },
        persisted: async (ctx) => {
          const row = await ctx.api('GET', `/api/employees/${bypassedEmployeeId}`, {
            token: admin.token, recordId: String(bypassedEmployeeId),
          });
          return {
            employeeId: bypassedEmployeeId,
            status: row.json.status,
            countryCode: row.json.countryCode,
            iqamaNumber: row.json.iqamaNumber,
            gosiReference: row.json.gosiReference,
            activatedDespiteMissingStatutoryData: row.json.status === 'Active',
          };
        },
        persistedVia:
          'fresh GET of the employee — it is ACTIVE with no country, no Iqama and no GOSI reference',
      });

      evidence.defect(
        'P1',
        'The KSA statutory activation floor is bypassed for any employee created without a country',
        'POST /api/employees sets Employee.CountryCode ONLY from the first complianceRecords entry '
        + '(EmployeeManagementService.CreateAsync) and never falls back to the country of the company '
        + 'the employee is attached to. GccReadinessFloor.Resolve("") returns an empty requirement '
        + 'list, so EnsureActivatable finds nothing to enforce and a non-GCC expat with no Iqama '
        + 'number and no GOSI reference is activated into a Saudi legal entity with HTTP 200. '
        + 'GET /api/employees/field-catalog states the intended rule — "Explicit countryCode wins, '
        + 'else the company\'s country" — so the derivation is specified and simply absent on this '
        + 'path. An employee activated this way occupies an active seat and is a candidate for a '
        + 'payroll population while being unpayable under KSA rules.',
        [
          'Step R1 (refusal-incomplete-employee): identical employee WITH a compliance record naming '
          + 'SA → HTTP 422 employee_not_activatable, blocking IqamaNumber, pay-blocking GosiReference.',
          'Step R1b (this tripwire): identical employee WITHOUT it → countryCode "", HTTP 200, Active.',
          'Zayra.Api.Tests/EmployeeActivationGateTests.cs sets CountryCode = "SA" directly on its '
          + 'fixture entities, so it covers the gate but never the derivation — the blind spot.',
        ],
      );


      // ── What the run SAW but did not assert ─────────────────────────────────────────────────
      // The harness records every /api call each step makes, including the ones the spec never
      // asked for. That is how this was found: nothing in the payroll journey touches AI insights,
      // and every payroll assertion above is green, but the screen a payroll officer is looking at
      // carries a red "Access Denied" banner the whole time. It is recorded as a defect rather than
      // quietly dropped, because a bundle that only reports what it set out to prove is a brochure.
      const insightDenials = evidence.recordedCalls()
        .filter((c) => c.url.startsWith('/api/ai/insights') && c.status === 403);
      if (insightDenials.length > 0) {
        evidence.defect(
          'P2',
          'A background AI-insights fetch 403s on every payroll screen and raises a user-facing '
          + '"Access Denied" error toast',
          'GET /api/ai/insights is issued automatically by the payroll module on mount. For any '
          + 'persona without the AI insight permission — here the HR Manager and the Finance '
          + 'Approver, both of whom are doing exactly their job — it returns 403, and the frontend '
          + 'renders it as "Access Denied — You do not have permission to perform this action. '
          + 'Please contact your administrator." The payroll transaction underneath succeeded every '
          + 'time. The effect is that a correct, completed payroll action is presented to the '
          + 'operator as a permission failure. A background panel the user did not ask for should '
          + 'degrade silently (hide the panel), not raise a blocking error.',
          [
            `${insightDenials.length} such 403s across steps: `
            + `${[...new Set(insightDenials.map((c) => c.stepId))].join(', ')}`,
            'Visible in img/11-payslips.jpg — the toast sits over a screen that correctly shows '
            + `${employeeCount} payslips, all published to ESS.`,
            'Affects the HR Manager and Finance Approver personas; the Admin persona holds the '
            + 'permission and sees no toast.',
          ],
        );
      }

      evidence.notRunArea(
        'The UI path for the incomplete-data refusal (R1)',
        'Not run. R1 was driven through the API, so the bundle has no image of it and does not claim '
        + 'one. Whether the employee form surfaces the same 422 legibly is untested here.',
      );
      evidence.notRunArea(
        'Payment settlement (marking employees paid)',
        'Deliberately not run. POST /api/payroll/payment-batches/{id}/settle is the endpoint that '
        + 'marks a batch and its records paid; this story stops at file generation, and step 10 '
        + 'records that nothing was marked paid. Settlement has NO evidence here and must not be '
        + 'read as covered.',
      );
      evidence.notRunArea(
        'Bank submission and the bank\'s own response',
        'Not run: no bank or WPS endpoint is reachable from a disposable stack. The file is generated '
        + 'and hashed; whether a bank would accept it is untested here.',
      );
      evidence.notRunArea(
        'Payroll for the other fixture tenants (Ras Al-Manar, Almarai group, Tata group)',
        'Not run. This story is one tenant, IntelliFlow, end to end. Multi-company group payroll is '
        + 'covered by e2e/group-company/ as assertions, not as evidence.',
      );
    },
  );
});

/** The tenant's single KSA legal entity, which the refusal cases attach their employee to. */
async function ksaCompany(ctx: StepApi, token: string): Promise<{ id: string }> {
  const res = await ctx.api('GET', '/api/companies?page=1&pageSize=10', { token });
  expect(res.status, "the tenant's legal entity must be readable").toBe(200);
  const company = ((res.json?.items ?? res.json ?? []) as Array<{ id: string }>)[0];
  expect(company?.id, 'IntelliFlow must have a legal entity to attach an employee to').toBeTruthy();
  return company;
}

/** The run id for a period, read from the API rather than scraped off a card. */
async function selectRunIdFromApi(
  ctx: { api: (m: string, p: string, i?: { token?: string }) => Promise<{ status: number; json: any }> },
  token: string,
  period: string,
): Promise<string> {
  const [monthLabel, yearLabel] = period.split(' ');
  const month = MONTHS.indexOf(monthLabel) + 1;
  const res = await ctx.api('GET', '/api/payroll/runs?pageSize=100', { token });
  const items: Array<{ id: string; year: number; month: number }> = res.json?.items ?? [];
  const row = items.find((r) => r.year === Number(yearLabel) && r.month === month);
  if (!row) throw new Error(`[evidence] no run for ${period} in the API's run list after creating it.`);
  return row.id;
}
