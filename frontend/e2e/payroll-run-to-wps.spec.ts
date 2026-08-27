/**
 * PAYROLL: run → two-step approval → lock → payslips → WPS/SIF file.
 *
 * WHY THIS SPEC EXISTS
 * ────────────────────
 * Before it, the e2e suite contained not one tenant-side business mutation — every POST/PUT/DELETE
 * was a login, a platform fixture operation, or a negative authorization probe. The payroll module,
 * the revenue-critical path of a payroll product, had zero end-to-end coverage while its UI was
 * fully built. This walks the whole money path in a browser, as the three humans who actually walk
 * it, and checks the NUMBERS at each hand-off — because a workflow that advances states while
 * producing wrong figures is the failure that matters here.
 *
 * THREE PERSONAS, BECAUSE THE PRODUCT REQUIRES THREE
 * ──────────────────────────────────────────────────
 *   admin@intelliflow.com      Admin             creates + processes the run; the only role the
 *                                                seeded permission model grants `payroll.export`,
 *                                                so also the only one that can reach the Bank/WPS
 *                                                writes.
 *   hrmanager@intelliflow.com  HR Manager        the MAKER: payroll.read/write/approve, and
 *                                                deliberately NOT payroll.lock.
 *   finance@intelliflow.com    Finance Approver  the CHECKER: payroll.approve + payroll.lock, and
 *                                                deliberately NOT payroll.write.
 * PayrollController.Approve additionally refuses the user who PROCESSED the run
 * ("maker_checker_violation"), which is why the admin processes and the HR Manager approves.
 *
 * WHAT IT ASSERTS, IN ORDER
 * ─────────────────────────
 *   1. the run is visible to the payroll maker in `Processed`
 *   2. maker approves → `PendingFinanceReview`, and the maker CANNOT finish it alone — the final
 *      approval control is absent in the UI, AND the API refuses the maker's second approve while
 *      leaving the run's state untouched
 *   3. finance approves → `Approved`
 *   4. Lock Run → `Locked`
 *   5. Generate Payslips → payslip count == the run's employee count, one payslip NUMBER per
 *      distinct employee
 *   6. Create Payment Batch is DISABLED before the lock and ENABLED after — a real, untested
 *      precondition (`disabled={creating || !runIsLocked}`)
 *   7. WPS/SIF → batch reaches `FileGenerated`, and the file's total == the run's net-pay total
 *      == the sum of the batch's per-employee payment records
 *
 * HOUSE RULES OBSERVED
 * ────────────────────
 *   • no `if (x) { expect(...) }` — a precondition that might be absent is asserted PRESENT first
 *   • no mid-test `test.skip()` — a regression goes red, never yellow
 *   • every negative assertion is paired with a positive one proving real content was on screen
 *   • no `innerText().length > N` as a "page loaded" proxy — assertions name rendered values
 *   • no `.catch(() => '')`, no swallowed errors, no `expect([200, 4xx]).toContain(status)`
 *
 * DATA. Runs against the IntelliFlow demo tenant (IntelliFlowDemoSeeder, SEED_DEMO_DATA=true). It
 * does not depend on a seeded `Processed` run — no seeder produces one — it CREATES the run through
 * the real Runs tab, which is itself tenant-side mutation coverage that was missing. The period is
 * the first month with no existing run, so the spec is re-runnable and retry-safe against a
 * persistent database.
 */
import { test, expect, Browser, Locator, Page } from '@playwright/test';
import {
  INTELLIFLOW_SLUG,
  INTELLIFLOW_ADMIN,
  INTELLIFLOW_HR_MGR,
  INTELLIFLOW_FINANCE,
  tenantLogin,
  apiLogin,
} from './helpers';
import { mainContentLength } from './group-company/helpers';

const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

/** "194,596.87" → 194596.87. Throws on anything that is not a rendered money value. */
function parseAmount(raw: string): number {
  const cleaned = raw.replace(/,/g, '').trim();
  if (!/^-?\d+(\.\d+)?$/.test(cleaned)) throw new Error(`Not a rendered amount: ${JSON.stringify(raw)}`);
  return Number(cleaned);
}

/**
 * Everything is scoped to <main>. The sidebar carries its own "Approvals", "Payroll" and
 * "Finance & Talent" entries, so an unscoped getByRole/getByText would match the shell and either
 * click the wrong control or trip strict mode.
 */
const content = (page: Page): Locator => page.locator('main');

/** A persona's own browser context, authenticated with the session auth.setup.ts minted. */
async function openAs(
  browser: Browser,
  baseURL: string | undefined,
  persona: { email: string; password: string },
): Promise<Page> {
  const context = await browser.newContext({ baseURL });
  const page = await context.newPage();
  await tenantLogin(page, persona.email, persona.password, INTELLIFLOW_SLUG);
  return page;
}

/** Land on the payroll module and prove it rendered before touching anything. */
async function gotoPayroll(page: Page): Promise<void> {
  await page.goto('/payroll', { waitUntil: 'domcontentloaded' });
  await expect(content(page).getByRole('heading', { name: 'Payroll Management' })).toBeVisible({
    timeout: 30_000,
  });
}

/**
 * The payroll tab strip. Scoped structurally rather than by class: the Dashboard tab renders
 * quick-action tiles labelled "Approvals", "Validation" and "New Payroll Run" that collide with
 * the tab names, so `getByRole('button', { name: 'Approvals' })` is ambiguous while the dashboard
 * is mounted. "Bank / WPS Files" is a label only the tab strip uses; the innermost <div> that
 * contains it IS the strip.
 */
const tabStrip = (page: Page): Locator =>
  content(page)
    .locator('div')
    .filter({ has: page.getByRole('button', { name: 'Bank / WPS Files', exact: true }) })
    .last();

async function openTab(page: Page, label: string): Promise<void> {
  await tabStrip(page).getByRole('button', { name: label, exact: true }).click();
}

/**
 * Select a run in one of the payroll tabs' run pickers and return its id. Matches on the PERIOD
 * only ("Sep 2026"), never on the status, because the status is exactly what this journey changes
 * underneath the option label.
 */
async function selectRun(page: Page, ariaLabel: string, period: string): Promise<string> {
  const picker = content(page).locator(`select[aria-label="${ariaLabel}"]`);
  const option = picker.locator('option', { hasText: period });
  await expect(option, `run picker "${ariaLabel}" must offer exactly one ${period} run`).toHaveCount(1);
  const runId = await option.getAttribute('value');
  expect(runId, `the ${period} option must carry a run id`).toMatch(
    /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i,
  );
  await picker.selectOption(runId!);
  return runId!;
}

/** The Runs-tab card for a period (a div[role="button"] that opens the run's slip register). */
const runCard = (page: Page, period: string): Locator =>
  content(page).locator('[role="button"]').filter({ hasText: period }).first();

/** The Approvals-tab detail card for the selected run — where its status badge lives. */
const approvalCard = (page: Page): Locator =>
  content(page).locator('div.surface').filter({ hasText: /^Payroll Run —/ }).first();

/** A Bank/WPS-tab payment batch card. */
const batchCard = (page: Page, number: string): Locator =>
  content(page).locator('[role="button"]').filter({ hasText: number }).first();

test.describe('Payroll — run to WPS file', () => {
  test('run → maker approval → finance approval → lock → payslips → payment batch → WPS/SIF', async ({
    browser,
    baseURL,
    request,
  }) => {
    test.setTimeout(300_000);

    let adminPage: Page | undefined;
    let makerPage: Page | undefined;
    let financePage: Page | undefined;

    // Facts read off the UI in step 1 and used as the yardstick for every later number.
    let period = '';
    let runId = '';
    let employeeCount = 0;
    let netTotal = 0;
    let netRendered = '';
    let currency = '';
    let batchId = '';
    let batchNumber = '';

    try {
      adminPage = await openAs(browser, baseURL, INTELLIFLOW_ADMIN);
      makerPage = await openAs(browser, baseURL, INTELLIFLOW_HR_MGR);
      financePage = await openAs(browser, baseURL, INTELLIFLOW_FINANCE);
      const admin = adminPage;
      const maker = makerPage;
      const finance = financePage;

      // ── ARRANGE ───────────────────────────────────────────────────────────────────────────
      // No seeder produces a `Processed` run, so the admin creates and processes one through the
      // real Runs tab. That is a tenant-side business mutation in its own right: the Payroll Runs
      // create/process controls had no e2e coverage at all before this line.
      await test.step('arrange: admin creates and processes a payroll run', async () => {
        await gotoPayroll(admin);

        // The run list is read only once its fetch has actually landed. An empty list and a
        // freshly-mounted-but-unloaded list look identical ("0 payroll runs", no cards), and the
        // difference decides which period is free — reading too early concluded every month was
        // free and tried to create a run for a month that already had one. The Dashboard tab does
        // not touch /payroll/runs, so arming this before the tab click cannot catch a stray call.
        const runsFetch = admin.waitForResponse(
          (r) => /\/api\/payroll\/runs\?/.test(r.url()) && r.request().method() === 'GET',
          { timeout: 60_000 },
        );
        await openTab(admin, 'Payroll Runs');
        const existingTotal = ((await (await runsFetch).json()) as { total: number }).total;
        expect(
          existingTotal,
          'the runs list is fetched with pageSize=50 — reset the test database before it exceeds that',
        ).toBeLessThanOrEqual(50);

        // Assertion in its own right: the list renders every run the server returned.
        const existingCards = content(admin)
          .locator('[role="button"]')
          .filter({ hasText: /^[A-Z][a-z]{2} \d{4}/ });
        await expect(
          existingCards,
          'the Payroll Runs list must render every run the API returned',
        ).toHaveCount(existingTotal);
        await expect(
          content(admin).getByText(new RegExp(`^${existingTotal} payroll runs?$`)),
          'the run count above the list must match the runs it holds',
        ).toBeVisible();

        // Take the first period with no run yet, so the spec is re-runnable and retry-safe.
        const taken = new Set(
          (await existingCards.allInnerTexts())
            .map((label) => /^([A-Z][a-z]{2}) (\d{4})/.exec(label.trim()))
            .filter((m): m is RegExpExecArray => m !== null)
            .map((m) => `${m[1]} ${m[2]}`),
        );
        expect(
          taken.size,
          'every rendered run card must state its period — a card without one means the list changed shape',
        ).toBe(existingTotal);
        const now = new Date();
        const candidates: string[] = [];
        for (let i = 0; i < 18; i++) {
          const d = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth() + i, 1));
          if (d.getUTCFullYear() > now.getUTCFullYear() + 1) break; // CreateRun caps year at +1
          candidates.push(`${MONTHS[d.getUTCMonth()]} ${d.getUTCFullYear()}`);
        }
        const free = candidates.find((c) => !taken.has(c));
        expect(
          free,
          `every selectable payroll period already has a run (${[...taken].join(', ')}) — reset the test database`,
        ).toBeTruthy();
        period = free!;
        const [monthLabel, yearLabel] = period.split(' ');

        await content(admin).getByRole('button', { name: 'New Payroll Run' }).click();
        await content(admin).getByLabel('Year', { exact: true }).fill(yearLabel);
        await content(admin).getByLabel('Month', { exact: true }).selectOption({ label: monthLabel });
        await content(admin).getByRole('button', { name: 'Create Run' }).click();
        // The create modal stays open and shows its error when creation is rejected (a duplicate
        // period, for one), so its disappearance is the proof the run was actually created.
        await expect(
          content(admin).getByRole('button', { name: 'Create Run' }),
          `creating the ${period} run must close the New Payroll Run dialog`,
        ).toHaveCount(0);

        const card = runCard(admin, period);
        await expect(card, `the ${period} run must appear in the Payroll Runs list`).toBeVisible({
          timeout: 30_000,
        });
        await expect(card).toContainText('Draft');

        await card.getByRole('button', { name: 'Process' }).click();
        await expect(card, `processing the ${period} run must move it out of Draft`).toContainText(
          'Processed',
          { timeout: 90_000 },
        );
      });

      // ── 1. A run in `Processed` is visible to the payroll maker ───────────────────────────
      await test.step('1. the payroll maker sees the run in Processed', async () => {
        await gotoPayroll(maker);

        // Positive control for every role-shaped assertion below: the module resolved THIS user's
        // roles. Without it, "the final-approve control is absent" could pass on a blank page.
        await expect(
          content(maker).getByText('HR / Payroll', { exact: true }),
          'the payroll module must identify this session as HR / Payroll',
        ).toBeVisible();

        await openTab(maker, 'Payroll Runs');
        const card = runCard(maker, period);
        await expect(card, `the maker must see the ${period} run`).toBeVisible({ timeout: 30_000 });
        await expect(card).toContainText('Processed');

        // Open the run's salary register, so `employeeCount` below is corroborated by rendered
        // rows rather than trusted as a lone number on a card.
        await card.click();
        const registerRows = content(maker).locator('table tbody tr');
        await expect(registerRows.first()).toBeVisible({ timeout: 30_000 });
        const renderedSlips = await registerRows.count();
        expect(renderedSlips, 'a processed run must render one salary slip per employee').toBeGreaterThan(0);

        await openTab(maker, 'Approvals');
        runId = await selectRun(maker, 'Select payroll run to approve', period);

        const summary = approvalCard(maker).getByText(/\d+ employees\s*·\s*Gross/);
        await expect(summary, 'the approvals card must state the run size and its totals').toBeVisible();
        const summaryText = await summary.innerText();
        const parsed =
          /(\d+)\s+employees\s*·\s*Gross\s+([A-Z]{3})\s+([\d,]+\.\d{2})\s*·\s*Net\s+([A-Z]{3})\s+([\d,]+\.\d{2})/.exec(
            summaryText,
          );
        expect(parsed, `approvals summary did not render employees/gross/net: ${summaryText}`).toBeTruthy();

        employeeCount = Number(parsed![1]);
        currency = parsed![4];
        netRendered = parsed![5];
        netTotal = parseAmount(netRendered);

        expect(employeeCount, 'the run must cover at least one employee').toBeGreaterThan(0);
        expect(
          renderedSlips,
          'the salary register must hold exactly one slip per employee on the run',
        ).toBe(employeeCount);
        expect(netTotal, 'the run must carry a non-zero net-pay total').toBeGreaterThan(0);
        expect(parsed![2], 'gross and net must be denominated in the same currency').toBe(currency);

        await expect(
          approvalCard(maker).getByText('Processed', { exact: true }),
          'the maker must be acting on a Processed run',
        ).toBeVisible();
      });

      // ── 2. Maker approves → PendingFinanceReview, and cannot finish it alone ──────────────
      await test.step('2. maker approves to PendingFinanceReview and cannot finalise it alone', async () => {
        const makerControl = content(maker).getByRole('button', { name: 'Approve → Send to Finance' });
        await expect(
          makerControl,
          "the maker's control must send the run onward, not complete it",
        ).toBeVisible();
        await expect(
          content(maker).getByText(/Your approval will advance this run to/),
          'the maker must be told their approval is not the final one',
        ).toBeVisible();
        await expect(
          content(maker).getByRole('button', { name: 'Approve — Final' }),
          'a maker must never be offered the final approval on a Processed run',
        ).toHaveCount(0);

        await makerControl.click();

        // POSITIVE: the run advanced and the chain records the maker's level.
        await expect(
          approvalCard(maker).getByText('Pending Finance Review', { exact: true }),
          "the maker's approval must advance the run to Pending Finance Review",
        ).toBeVisible({ timeout: 30_000 });
        await expect(
          content(maker).getByText('Awaiting Finance Controller approval.'),
          'the run must now read as awaiting Finance',
        ).toBeVisible();
        await expect(
          content(maker).getByText('PayrollReview', { exact: true }),
          'the approval chain must record the maker step',
        ).toBeVisible();

        // NEGATIVE, paired with the three positives above. Scoped to the run's own card and named
        // exactly — a loose /^Approve/ would also match the "Approvals" TAB in the strip above,
        // and enumerating the three controls ApprovalsTab can render is what makes "no control
        // survives" mean something rather than "no control matched my selector".
        for (const name of ['Approve — Final', 'Approve → Send to Finance', 'Send Back to Payroll']) {
          await expect(
            approvalCard(maker).getByRole('button', { name, exact: true }),
            `the maker must have no "${name}" control on a run they already signed`,
          ).toHaveCount(0);
        }

        // Segregation of duties proved at the server too, not merely hidden in the DOM. The
        // maker's own session must be refused — and, the part that matters, the run must not move.
        const makerToken = await apiLogin(
          request,
          INTELLIFLOW_HR_MGR.email,
          INTELLIFLOW_HR_MGR.password,
          INTELLIFLOW_SLUG,
        );
        const retry = await request.post(`/api/payroll/runs/${runId}/approve`, {
          headers: { Authorization: `Bearer ${makerToken}` },
          data: { notes: 'maker attempting to complete the run alone' },
        });
        expect(retry.status(), 'the API must refuse the maker a second, completing approval').toBe(400);
        expect(await retry.text()).toContain('You cannot approve this run at its current stage.');

        const afterRefusal = await request.get('/api/payroll/runs?pageSize=100', {
          headers: { Authorization: `Bearer ${makerToken}` },
        });
        expect(afterRefusal.status()).toBe(200);
        const runsBody = (await afterRefusal.json()) as { items: { id: string; status: string }[] };
        const stillPending = runsBody.items.find((r) => r.id === runId);
        expect(stillPending, 'the run must still be readable after the refusal').toBeTruthy();
        expect(
          stillPending!.status,
          'a refused maker approval must leave the run exactly where it was',
        ).toBe('PendingFinanceReview');
      });

      // ── 3. Finance approves → Approved ────────────────────────────────────────────────────
      await test.step('3. the finance approver signs off and the run reaches Approved', async () => {
        await gotoPayroll(finance);
        await openTab(finance, 'Approvals');
        const financeRunId = await selectRun(finance, 'Select payroll run to approve', period);
        expect(financeRunId, 'both personas must be acting on the same run').toBe(runId);

        await expect(approvalCard(finance).getByText('Pending Finance Review', { exact: true })).toBeVisible();
        // Role-discriminating positive control: send-back is rendered only for Finance/Admin on a
        // PendingFinanceReview run, and was proved absent for the maker in step 2.
        await expect(
          content(finance).getByRole('button', { name: 'Send Back to Payroll' }),
          'the finance approver must hold the send-back control the maker does not',
        ).toBeVisible();

        const finalControl = content(finance).getByRole('button', { name: 'Approve — Final' });
        await expect(
          finalControl,
          'the finance approver must be offered the completing approval',
        ).toBeVisible();
        await finalControl.click();

        await expect(
          approvalCard(finance).getByText('Approved', { exact: true }),
          'the finance approval must move the run to Approved',
        ).toBeVisible({ timeout: 30_000 });
        await expect(
          content(finance).getByText('Payroll run has been approved and is ready to lock.'),
          'an approved run must read as ready to lock',
        ).toBeVisible();
        await expect(
          content(finance).getByText('FinanceReview', { exact: true }),
          'the approval chain must record the finance step alongside the maker step',
        ).toBeVisible();
      });

      // ── 6a. Create Payment Batch is DISABLED while the run is only Approved ───────────────
      // Taken BEFORE the lock, so the assertion proves the LOCK is the gate, not the approval.
      await test.step('6a. Create Payment Batch is disabled before the run is locked', async () => {
        await gotoPayroll(admin);
        await openTab(admin, 'Bank / WPS Files');
        expect(await selectRun(admin, 'Select payroll run', period)).toBe(runId);

        const createBatch = content(admin).getByRole('button', { name: 'Create Payment Batch' });
        await expect(createBatch, 'the Bank/WPS tab must offer the batch control').toBeVisible();
        await expect(
          createBatch,
          'a payment batch must not be creatable before the run is locked',
        ).toBeDisabled();
        await expect(
          content(admin).getByText(/A payment batch can only be created once the run is/),
          'the operator must be told why the control is disabled',
        ).toBeVisible();
        await expect(
          content(admin).getByText(/This run is\s*Approved\s*\./),
          "the notice must name the run's actual current status",
        ).toBeVisible();
      });

      // ── 4. Lock Run → Locked ──────────────────────────────────────────────────────────────
      await test.step('4. finance locks the approved run', async () => {
        await gotoPayroll(finance);
        await openTab(finance, 'Payroll Runs');
        const card = runCard(finance, period);
        await expect(card).toBeVisible({ timeout: 30_000 });
        await expect(card).toContainText('Approved');

        await card.getByRole('button', { name: 'Lock' }).click();

        await expect(card, 'locking must move the run to Locked').toContainText('Locked', {
          timeout: 90_000,
        });
        // NEGATIVE, paired with the Locked badge above: a locked run exposes no lifecycle actions.
        await expect(
          card.getByRole('button', { name: 'Lock' }),
          'a locked run must not still offer a Lock control',
        ).toHaveCount(0);
        await expect(
          card.getByRole('button', { name: 'Process' }),
          'a locked run must not still offer a Process control',
        ).toHaveCount(0);
      });

      // ── 5. Payslips: count == employee count, one number per employee ────────────────────
      await test.step('5. payslips are generated one per employee, each with a payslip number', async () => {
        await gotoPayroll(maker);
        await openTab(maker, 'Payslips');
        expect(await selectRun(maker, 'Select payroll run', period)).toBe(runId);

        await content(maker).getByRole('button', { name: 'Generate Payslips' }).click();

        const rows = content(maker).locator('table tbody tr');
        await expect(rows.first(), 'generating payslips must render a payslip table').toBeVisible({
          timeout: 90_000,
        });
        await expect(
          rows,
          `the run covers ${employeeCount} employees, so it must produce exactly ${employeeCount} payslips`,
        ).toHaveCount(employeeCount);

        // The KPI the operator actually reads must agree with the table beneath it.
        const kpi = content(maker).locator('div.surface').filter({ hasText: 'Total Payslips' }).first();
        await expect(kpi).toBeVisible();
        expect(
          (await kpi.innerText()).trim(),
          "the Total Payslips KPI must equal the run's employee count",
        ).toMatch(new RegExp(`^${employeeCount}\\b`));

        const numbers = (await rows.locator('td:nth-child(2)').allInnerTexts()).map((s) => s.trim());
        expect(numbers, 'one payslip-number cell per payslip row').toHaveLength(employeeCount);
        expect(
          numbers.filter((n) => !/^PS-\S+-\d{14}$/.test(n)),
          'every payslip row must render a real payslip number',
        ).toEqual([]);
        expect(
          new Set(numbers).size,
          'payslip numbers must be unique — a repeated number is two employees sharing one payslip',
        ).toBe(employeeCount);

        const employees = (await rows.locator('td:nth-child(1)').allInnerTexts()).map((s) => s.trim());
        expect(new Set(employees).size, 'each payslip must belong to a distinct employee').toBe(
          employeeCount,
        );
        expect(
          employees.filter((e) => !/^Emp #\d+$/.test(e)),
          'every payslip row must name the employee it belongs to',
        ).toEqual([]);

        // The run is Locked, so generation must also publish to ESS — otherwise the employee has
        // no visible payslip for a month that is already being paid.
        expect(
          (await rows.locator('td:nth-child(4)').allInnerTexts()).map((s) => s.trim()),
          'payslips generated on a Locked run must publish to ESS',
        ).toEqual(Array(employeeCount).fill('Published'));
      });

      // ── 6b. Create Payment Batch is ENABLED after the lock, and the batch adds up ────────
      await test.step('6b. Create Payment Batch is enabled after the lock and the batch totals the run', async () => {
        await gotoPayroll(admin);
        await openTab(admin, 'Bank / WPS Files');
        expect(await selectRun(admin, 'Select payroll run', period)).toBe(runId);

        const createBatch = content(admin).getByRole('button', { name: 'Create Payment Batch' });
        await expect(createBatch, 'locking the run must enable payment batch creation').toBeEnabled();
        // NEGATIVE, paired with the enabled control above: the blocking notice is gone.
        await expect(
          content(admin).getByText(/A payment batch can only be created once the run is/),
          'the lock warning must disappear once the run is locked',
        ).toHaveCount(0);

        const created = admin.waitForResponse(
          (r) =>
            r.url().includes(`/api/payroll/runs/${runId}/payment-batches`) &&
            r.request().method() === 'POST',
        );
        await createBatch.click();
        const batchResponse = await created;
        expect(batchResponse.status(), 'creating a payment batch on a Locked run must succeed').toBe(201);
        const batch = (await batchResponse.json()) as {
          id: string;
          batchNumber: string;
          totalAmount: number;
          currency: string;
        };
        batchId = batch.id;
        batchNumber = batch.batchNumber;

        expect(
          batch.totalAmount,
          "the payment batch total must equal the run's net-pay total",
        ).toBeCloseTo(netTotal, 2);
        expect(batch.currency, 'the batch must be denominated in the tenant currency').toBe(currency);

        const card = batchCard(admin, batchNumber);
        await expect(card, 'the new batch must render in the Payment Batches list').toBeVisible({
          timeout: 30_000,
        });
        await expect(card).toContainText('Draft');
        await expect(
          card,
          "the batch card must show the run's net-pay total as the amount to disburse",
        ).toContainText(`${currency} ${netRendered}`);

        // The per-employee payment records are what the bank is actually asked to pay. They must
        // reconcile to the same net total, one line per employee.
        const recordRows = content(admin).locator('table tbody tr');
        await expect(recordRows.first()).toBeVisible({ timeout: 30_000 });
        await expect(
          recordRows,
          'the batch must hold one payment record per employee on the run',
        ).toHaveCount(employeeCount);
        const amounts = (await recordRows.locator('td:nth-child(2)').allInnerTexts()).map(parseAmount);
        expect(
          amounts.reduce((sum, n) => sum + n, 0),
          "the batch's per-employee payment lines must add up to the run's net-pay total",
        ).toBeCloseTo(netTotal, 2);
      });

      // ── 7. WPS/SIF: FileGenerated, and the file's total == the run's net-pay total ───────
      await test.step('7. the WPS/SIF file is generated and its total equals the run net pay', async () => {
        // ─────────────────────────────────────────────────────────────────────────────────────
        // KNOWN PRODUCT GAP, asserted rather than skipped.
        //
        // `payrollApi.generateWpsFile()` posts to .../wps-file with NO query string, and the
        // Bank/WPS tab renders no control that could set `acknowledgeReadinessDrift`. The backend
        // refuses the export whenever an ACTIVE employee has drifted pay-blocked under the current
        // readiness policy (PayrollController.GenerateWps, §6.6). Every KSA seed in this repo ships
        // non-Saudi employees with an IqamaNumber and no IqamaExpiryDate, and GccReadinessFloor
        // makes IqamaExpiry a fail-closed PAY gate — so this button cannot succeed on the shipped
        // demo data, and no route on the API surface can set IqamaExpiryDate on an existing
        // employee (EmployeesController.ApplyChanges has no case for it; the service method that
        // would mirror it, IEmployeeManagementService.UpdateAsync, has no route at all).
        //
        // The button is pressed here anyway — it had no coverage whatsoever — and its real outcome
        // is pinned. When the UI gains the acknowledgement control (or the tenant's employee
        // records are completed), THIS assertion fails and must be replaced with a click. That is
        // deliberate: it is the tripwire that says the gap closed.
        // ─────────────────────────────────────────────────────────────────────────────────────
        const uiAttempt = admin.waitForResponse(
          (r) =>
            r.url().includes(`/api/payroll/payment-batches/${batchId}/wps-file`) &&
            r.request().method() === 'POST',
        );
        await batchCard(admin, batchNumber).getByRole('button', { name: 'Generate WPS/SIF' }).click();
        const blocked = await uiAttempt;
        expect(
          blocked.status(),
          "the UI's Generate WPS/SIF sends no readiness acknowledgement, so the export is refused",
        ).toBe(422);
        expect((await blocked.json()).error).toBe('readiness_drift_acknowledgement_required');
        await expect(
          content(admin).getByText(/no longer meet the current readiness policy/),
          'the refusal must be surfaced to the operator, not swallowed',
        ).toBeVisible();
        await expect(
          batchCard(admin, batchNumber),
          'a refused export must leave the batch in Draft',
        ).toContainText('Draft');

        // The acknowledged export: the same operator, the same session, the one flag the UI has no
        // control for. Everything after this point is asserted back in the browser.
        const adminToken = await apiLogin(
          request,
          INTELLIFLOW_ADMIN.email,
          INTELLIFLOW_ADMIN.password,
          INTELLIFLOW_SLUG,
        );
        const exported = await request.post(
          `/api/payroll/payment-batches/${batchId}/wps-file?acknowledgeReadinessDrift=true`,
          { headers: { Authorization: `Bearer ${adminToken}` } },
        );
        expect(exported.status(), 'the acknowledged WPS export must succeed').toBe(200);
        const file = (await exported.json()) as {
          sifFileName: string;
          employeeCount: number;
          totalSalaryAmount: number;
          fileHash: string;
        };

        expect(file.employeeCount, 'the WPS file must carry one record per employee on the run').toBe(
          employeeCount,
        );
        expect(
          Number(file.totalSalaryAmount),
          "THE ASSERTION THIS SPEC EXISTS FOR: the wage file total must equal the run's net pay",
        ).toBeCloseTo(netTotal, 2);
        expect(file.sifFileName, 'the export must name a real file').toMatch(/\.[a-z]{2,4}$/i);
        expect(file.fileHash, 'the export must record a content hash').toMatch(/^[0-9a-f]{64}$/);

        // Back to the browser: the operator's own view must now show the filed batch.
        await gotoPayroll(admin);
        await openTab(admin, 'Bank / WPS Files');
        expect(await selectRun(admin, 'Select payroll run', period)).toBe(runId);

        const filedCard = batchCard(admin, batchNumber);
        await expect(filedCard).toBeVisible({ timeout: 30_000 });
        await expect(
          filedCard,
          'a batch with a generated wage file must read as File Generated',
        ).toContainText('File Generated');
        await expect(
          filedCard,
          "the filed batch must still show the run's net-pay total",
        ).toContainText(`${currency} ${netRendered}`);
        // NEGATIVE, paired with the two positives above: a filed batch offers no re-generate.
        await expect(
          filedCard.getByRole('button', { name: 'Generate WPS/SIF' }),
          'a batch that already has a wage file must not offer to generate another',
        ).toHaveCount(0);

        // Guard that everything above ran against a populated module rather than a shell that
        // happened to contain the right words.
        expect(
          await mainContentLength(admin),
          'the Bank/WPS tab must have rendered real content',
        ).toBeGreaterThan(200);
      });
    } finally {
      await adminPage?.context().close();
      await makerPage?.context().close();
      await financePage?.context().close();
    }
  });
});
