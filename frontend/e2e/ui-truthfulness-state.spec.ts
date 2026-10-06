import { expect, test } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import { createLatestRequestGate, runLatest } from '../src/lib/latestRequest';
import { commonPayrollCurrency, resolvePayrollRunCurrency, resolvePayrollRunsCurrency, totalsByCurrency } from '../src/lib/payrollCurrency';
import { filterPayrollInsightsForReadiness, paymentReadinessHeadline, type PaymentPrerequisites } from '../src/lib/payrollPrerequisites';
import { buildAttention, fmtMoney } from '../src/components/dashboard/dashboardModel';
import type { DashboardFull } from '../src/api/dashboard';
import type { AIInsight } from '../src/api/intelligence';
import {
  ATTENDANCE_DOMAINS,
  attendanceErrorSummary,
  regularizationQueueUnavailableMessage,
  unavailableMessage,
} from '../src/lib/attendanceLoadState';
import {
  QIWA_SIMULATED_LABEL,
  deliveryAttentionSummary,
  emailModeLabel,
  qiwaConnectionLabel,
  qiwaModeLabel,
  reportRunStatusLabel,
  reportRunStatusTone,
} from '../src/lib/integrationDeliveryState';
import { LOGIN_CAPABILITIES, LOGIN_PREVIEW_DISCLOSURE } from '../src/lib/loginCapabilities';
import { payrollInsightEmptyCopy, payrollInsightState, payrollPeriodState } from '../src/lib/payrollInsightState';
import { requestFailureReason } from '../src/lib/requestFailure';
import { createUrlSeed } from '../src/lib/urlSeed';
import { spendUtilisation, totalSpend } from '../src/lib/establishmentSpend';

const read = (relative: string) => fs.readFileSync(path.join(process.cwd(), relative), 'utf8');

test.describe('browserless UI truthfulness contracts', () => {
  test('attendance models every independent data source and names partial failure', () => {
    expect(ATTENDANCE_DOMAINS).toHaveLength(10);
    const errors = { daily: 'failed', deviceSync: 'failed' } as const;
    expect(attendanceErrorSummary(errors)).toContain('2 attendance data sources unavailable');
    expect(attendanceErrorSummary(errors)).toContain('daily attendance');
    expect(unavailableMessage('deviceSync', errors)).toContain('device health unavailable');
    expect(regularizationQueueUnavailableMessage({ pendingRegularizations: 'failed' }, 0)).toContain('pending correction approvals unavailable');
    expect(regularizationQueueUnavailableMessage({ regularizations: 'failed' }, 0)).toContain('your correction requests unavailable');
    expect(regularizationQueueUnavailableMessage({ regularizations: 'failed' }, 2)).toBeNull();
  });

  test('attendance failure rendering never falls through to zero or empty-success states', () => {
    const attendance = read('src/views/AttendancePage.tsx');

    expect(attendance).toContain("dashboardUnavailable ? <DomainUnavailable");
    expect(attendance).toContain("deviceSyncUnavailable ? <DomainUnavailable");
    expect(attendance).toContain("devicesUnavailable ? <DomainUnavailable");
    expect(attendance).toContain("dailyUnavailable ? 'Unavailable' : minutes(totalWorked)");
    expect(attendance).toContain("dashboardUnavailable ? 'Unavailable' : (summary?.overtimeEmployees ?? 0)");
    expect(attendance).toContain("action={insightsUnavailable ? 'Unavailable' : `${insights.length} open`}");
    expect(attendance).toContain("action={rawUnavailable ? 'Unavailable' : `${rawEvents.length} latest`}");
    expect(attendance).toContain("action={payrollSummaryUnavailable ? 'Unavailable' : `${payrollSummary.length} employees`}");
    expect(attendance).toContain("action={insightsUnavailable ? 'Unavailable' : `${insights.length} signals`}");
    expect(attendance).toContain("correctionQueueUnavailable");
  });

  test('payroll never equates empty or failed insight data with healthy payroll', () => {
    expect(payrollInsightState(false, true, 0)).toBe('unavailable');
    expect(payrollInsightEmptyCopy('unavailable')).toContain('does not prove payroll is clear');
    expect(payrollInsightEmptyCopy('empty')).toContain('Complete payroll validation');
    expect(payrollPeriodState(false, true)).toBe('no-run');
    expect(payrollPeriodState(true, true)).toBe('has-run');
    expect(payrollPeriodState(false, false)).toBe('unavailable');
    const payrollPage = read('src/views/PayrollPage.tsx');
    expect(payrollPage).not.toContain('all payroll and HR signals look normal');
    expect(payrollPage).toContain('Empty totals do not indicate a completed or healthy payroll');
  });

  // An evidence run recorded this on 5 of 15 payroll steps: the advisory insights panel fetches
  // /api/ai/insights on mount, that endpoint requires `ai.insights_view`, and the HR Manager and
  // Finance Approver bundles do not hold it — so the shared client's global interceptor raised a red
  // "Access Denied" toast over screens where the payroll action had SUCCEEDED. One sat directly over a
  // screen correctly showing 14 published payslips. A completed action must never read as a refusal.
  test('an advisory panel nobody asked for degrades silently, and refusals users cause still speak', () => {
    const payrollPage = read('src/views/PayrollPage.tsx');
    expect(payrollPage, 'the panel must check the permission the endpoint enforces')
      .toContain("hasPermission('ai.insights_view')");
    expect(payrollPage, 'and must not issue the request it knows will be refused')
      .toContain('if (!enabled) { setLoading(false); return; }');
    expect(payrollPage, "rendering nothing — NOT the amber 'unavailable' alert, which would trade a "
      + 'transient false alarm for a permanent one')
      .toContain('if (!enabled) return null;');

    // The fix must be the call site, never the toast: a user-initiated action that is refused has to
    // keep saying why. If this disappears, access denials have gone silent product-wide.
    const client = read('src/api/client.ts');
    expect(client).toContain("window.dispatchEvent(new CustomEvent('zayra:access-denied'");
  });

  test('pre-auth preview and capability claims are qualified', () => {
    expect(LOGIN_PREVIEW_DISCLOSURE).toContain('Illustrative sample data');
    // WS3: no unbacked Saudi claim before sign-in. Mudad takes only the bank-signed file and there is no
    // verified Qiwa API, so the login says what the product does: a bank file with WPS fields, a Qiwa
    // data check, and evidence tracking for Mudad.
    expect(LOGIN_CAPABILITIES.find((item) => item.includes('Qiwa'))).toBe('Qiwa data checks & Mudad evidence tracking');
    expect(LOGIN_CAPABILITIES.find((item) => item.includes('WPS'))).toBe('Bank payroll files with WPS fields (ANB)');
    const login = read('src/components/LoginMarketing.tsx');
    for (const claim of ["'Mudad file'", "'ملف مدد'", "'Mudad'", "'مدد'", "'Qiwa'", 'Mudad WPS XML exporter', 'the Mudad \'', 'live sync']) {
      expect(login, `login marketing must not claim ${claim}`).not.toContain(claim);
    }
    expect(login).toContain("'ANB payroll file'");
    expect(login).toContain("'Qiwa data check'");
    expect(LOGIN_CAPABILITIES.find((item) => item.includes('Hijri'))).toContain('aware');
    /* These three used to pin Tailwind utilities on the old panel-grid
       sign-in page: overflow-x-hidden, an lg: two-column grid, and a
       safe-centred column. The V3 rebuild moved that layout out of utility
       classes and into src/styles/login-aurora.css, so the assertions were
       checking for strings in a file that no longer has any business
       containing them — and the suite has been red ever since.
       Same three guarantees, asserted where they now live. */
    const shellCss = read('src/styles/login-aurora.css');
    // 1. no horizontal scroll on the sign-in shell
    expect(shellCss).toContain('overflow-x: hidden');
    // 2. the stage is still two columns: brief + the card's own track
    // No `s` flag: [^}] already spans newlines, and the flag needs an es2018 target.
    expect(shellCss).toMatch(/\.lx-stage\b[^}]*grid-template-columns:\s*minmax\(0, 1fr\)/);
    // 3. and it collapses to one column rather than overflowing when narrow
    expect(shellCss).toContain('grid-template-columns: minmax(0, 1fr);');
  });

  test('public privacy and security claims stay evidence-bound', () => {
    const privacy = read('app/privacy/page.tsx');
    const security = read('app/security/page.tsx');

    expect(privacy).not.toContain('AES-256 encryption at rest for all sensitive fields');
    expect(privacy).not.toContain('Customers can request that their data be stored');
    expect(privacy).not.toContain('Regular penetration testing and vulnerability assessments');
    expect(privacy).toContain('must be confirmed in the applicable customer agreement');
    expect(privacy).toContain('does not claim ISO 27001 or SOC 2 Type II certification');

    /* 2026-09-21 rewrite (scratchpad/privacy-policy.md). Each of these was a published promise the
       product did not keep: the retention sweep that would anonymise is switched off and the delete
       path stamps seven years, not 90 days; no legal-hold mechanism exists; no "Privacy & Cookies"
       screen exists and the app sets no cookies; bank details are permission-gated, not masked;
       access tokens last 30 minutes, not 24 hours. They stay out until the code makes them true. */
    expect(privacy).not.toContain('Anonymised within 90 days');
    expect(privacy.toLowerCase()).not.toContain('legal hold');
    expect(privacy).not.toContain('Privacy &amp; Cookies');
    expect(privacy).not.toContain('masked in user-facing responses');
    expect(privacy).not.toContain('Session tokens expire within 24 hours');
    expect(privacy).not.toContain('explicit consent is collected');
    // The disclosures a Saudi customer's counsel will look for, pinned so they cannot be softened away.
    expect(privacy).toContain('does not automatically delete or anonymise personal data');
    expect(privacy).toContain('None of the platform&apos;s data is stored in the Kingdom of Saudi Arabia');
    expect(privacy).toContain('Backblaze B2 in a United States region');
    /* This pin is a tripwire, not trivia: the page names the model that receives employee data,
       so it must track Render's AI_MODEL. It fired on 2026-09-23 when AI_MODEL was confirmed as
       deepseek-v4-pro:cloud while the page still said gpt-oss:120b -- the page had been wrong
       since the model was last switched. Update BOTH, or the disclosure goes stale again. */
    expect(privacy).toContain('deepseek-v4-pro:cloud');
    expect(privacy).toContain('does not remove or disguise personal data before sending it to the model');

    expect(security).not.toContain('Every table that holds tenant-owned data');
    expect(security).not.toContain('TLS 1.2 minimum enforced');
    expect(security).not.toContain('Every push to main triggers a Docker build and deploy');
    expect(security).toContain('Selected PostgreSQL integration suites use Testcontainers when Docker is available');
    expect(security).toContain('Render auto-deploy is disabled');
  });

  test('critical surfaces carry semantic and server-truth guards', () => {
    const login = read('src/views/LoginPage.tsx');
    const people = read('src/views/EmployeesPage.tsx');
    const approvals = read('src/views/ApprovalsPage.tsx');
    const notifications = read('src/layouts/TopBar.tsx');
    const tabs = read('src/components/ui/RovingTabs.tsx');

    /* aria-busy={busy}, not aria-busy={loading}: the rebuild pulled the
       submit button out into a <Submit> part that takes the flag as `busy`
       (LoginPage still assigns `const busy = loading`). The guarantee — the
       submit control reports its pending state to assistive tech — is
       unchanged, so the assertion follows the rename rather than the page
       being reverted to satisfy a string match. */
    expect(login).toContain('const busy = loading');
    expect(login).toContain('aria-busy={busy}');
    expect(login).toContain('aria-pressed={showPw}');
    expect(people).toContain('aria-label={`Open profile for ${employee.fullName}`}');
    expect(people).toContain('Clear filters');
    expect(approvals).toContain('aria-pressed={queueFilter === key}');
    expect(approvals).not.toContain("alert('Please add a clear rejection reason");
    expect(notifications).toContain('No local status was changed');
    expect(notifications).toContain('group-focus-within:opacity-100');
    expect(approvals).toContain('<div role="alert" aria-live="assertive"');
    expect(tabs).toContain('role="tablist"');
    expect(tabs).toContain("event.key === 'Home'");
    expect(tabs).toContain('role="tabpanel"');
  });

  test('request failures name their cause instead of reading as empty data', () => {
    expect(requestFailureReason({ isAxiosError: true, response: { status: 403, data: { message: 'Forbidden' } } })).toContain('do not have permission');
    expect(requestFailureReason({ isAxiosError: true })).toContain('could not be reached');
    expect(requestFailureReason({ isAxiosError: true, response: { status: 500, data: { message: 'Report store offline' } } })).toBe('Report store offline');
    expect(requestFailureReason({ isAxiosError: true, response: { status: 502 } })).toContain('HTTP 502');
    expect(requestFailureReason(new SyntaxError('Unexpected token'))).not.toContain('could not be reached');
  });

  test('offboarding and reports never swallow a load or action failure', () => {
    // F04/F05: each of these rendered an outage as "No offboardings yet", "0 reports",
    // "No saved reports", "No schedules configured" or "No executions yet", or did nothing.
    for (const file of ['src/views/OffboardingPage.tsx', 'src/views/ReportsPage.tsx']) {
      const source = read(file);
      expect(source, file).not.toMatch(/catch\s*\{\s*\/\*\*\/\s*\}/);
      expect(source, file).not.toMatch(/\.catch\(\(\)\s*=>\s*\{\s*\}\)/);
    }
    const offboarding = read('src/views/OffboardingPage.tsx');
    expect(offboarding).toContain(') : listUnavailable ? (');
    expect(offboarding).toContain('This does not mean there are no separations in progress.');
    const reports = read('src/views/ReportsPage.tsx');
    for (const what of ['The report catalog', 'Saved reports', 'Scheduled reports', 'Execution history']) {
      expect(reports).toContain(`<LoadFailure what="${what}"`);
    }
  });
});

// R02 / R03 — kept in their own block (appended) so parallel additions to the block above merge cleanly.
test.describe('browserless employee search race and payroll currency contracts', () => {
  const deferred = <T,>() => {
    let resolve!: (value: T) => void;
    let reject!: (reason?: unknown) => void;
    const promise = new Promise<T>((res, rej) => { resolve = res; reject = rej; });
    return { promise, resolve, reject };
  };

  test('R02: a slow earlier search that resolves after a newer one never replaces it', async () => {
    // Control: the pre-fix shape (write whatever resolves) ends on the STALE result.
    const naive = { rows: [] as string[] };
    const slowOld = deferred<string[]>();
    const fastNew = deferred<string[]>();
    const n1 = slowOld.promise.then((rows) => { naive.rows = rows; });
    const n2 = fastNew.promise.then((rows) => { naive.rows = rows; });
    fastNew.resolve(['EVO0004']); await n2;
    slowOld.resolve(['EVO0003']); await n1;
    expect(naive.rows).toEqual(['EVO0003']);

    // Gated: the newest request wins regardless of arrival order, and the stale one is reported as such.
    const gate = createLatestRequestGate();
    const state = { rows: [] as string[], loading: true, error: '' };
    const handlers = {
      onResult: (rows: string[]) => { state.rows = rows; },
      onError: () => { state.error = 'failed'; },
      onSettled: () => { state.loading = false; },
    };
    const older = deferred<string[]>();
    const newer = deferred<string[]>();
    const first = runLatest(gate, () => older.promise, handlers);
    const second = runLatest(gate, () => newer.promise, handlers);
    newer.resolve(['EVO0004']);
    expect(await second).toBe('applied');
    expect(state.rows).toEqual(['EVO0004']);
    older.resolve(['EVO0003']);
    expect(await first).toBe('stale');
    expect(state.rows).toEqual(['EVO0004']);
  });

  test('R02: a pick retires the in-flight search, so a late response cannot reopen the list or change the selection', async () => {
    const gate = createLatestRequestGate();
    const picker = { open: false, results: [] as string[], selected: 'EVO0004' };
    const late = deferred<string[]>();
    const pending = runLatest(gate, () => late.promise, {
      onResult: (rows) => { picker.results = rows; picker.open = true; },
    });
    gate.invalidate(); // select(emp) / clear() / the query being emptied
    late.resolve(['EVO0003']);
    expect(await pending).toBe('stale');
    expect(picker.open).toBe(false);
    expect(picker.results).toEqual([]);
    expect(picker.selected).toBe('EVO0004');
  });

  test('R02: a stale failure neither shows an error nor ends the newer request\'s loading state', async () => {
    const gate = createLatestRequestGate();
    const state = { loading: true, error: '', rows: [] as string[] };
    const handlers = {
      onResult: (rows: string[]) => { state.rows = rows; },
      onError: () => { state.error = 'Could not load employees from the API.'; },
      onSettled: () => { state.loading = false; },
    };
    const older = deferred<string[]>();
    const newer = deferred<string[]>();
    const first = runLatest(gate, () => older.promise, handlers);
    const second = runLatest(gate, () => newer.promise, handlers);
    older.reject(new Error('timeout'));
    expect(await first).toBe('stale');
    expect(state.error).toBe('');
    expect(state.loading).toBe(true);
    newer.reject(new Error('HTTP 500'));
    expect(await second).toBe('failed');
    expect(state.error).toBe('Could not load employees from the API.');
    expect(state.loading).toBe(false);
  });

  test('R02: every employee search surface goes through the latest-request gate', () => {
    const people = read('src/views/EmployeesPage.tsx');
    expect(people).toContain('const employeeLoadGate = useMemo(() => createLatestRequestGate(), []);');
    expect(people).toContain('await runLatest(employeeLoadGate, () => employeesApi.list({');
    // Handlers holding an older `load` must refresh the CURRENT query, not re-run the old one.
    expect(people).toContain('const query = employeeQueryRef.current;');
    expect(people).toContain('useEffect(() => { load(); }, [load, employeeQuery]);');
    for (const file of ['src/components/EmployeePicker.tsx', 'src/components/EmployeeSearchSelect.tsx']) {
      const source = read(file);
      expect(source, file).toContain('const searchGate = useMemo(() => createLatestRequestGate(), []);');
      expect(source, file).toContain('runLatest(searchGate, () => employeesApi.list(');
      expect(source, file).toMatch(/const select = \(emp: EmployeeListItem\) => \{\n\s+searchGate\.invalidate\(\);/);
      expect(source, file).toContain('const clear = () => { searchGate.invalidate();');
      expect(source, file).not.toMatch(/employeesApi\.list\([^)]*\)\s*\n?\s*\.then/);
    }
    expect(read('src/components/EmployeePicker.tsx')).toContain('{!value && open && results.length > 0 && (');
  });

  test('People ?search= seeds the box; typing afterwards is never overwritten by the URL', () => {
    // Control: the pre-fix effect re-applied the URL whenever the box differed from it.
    const naive = { box: 'ali' };
    const naiveEffect = (url: string | null) => { if (url !== null && url !== naive.box) naive.box = url; };
    naive.box = 'alic'; // the user types one more letter
    naiveEffect('ali'); // `search` changed, so the effect ran again
    expect(naive.box).toBe('ali');

    const seed = createUrlSeed();
    const people = { box: 'ali' }; // the box starts from the URL
    const effect = (url: string | null) => { const v = seed.take(url); if (v !== undefined) people.box = v; };
    effect('ali');
    expect(people.box).toBe('ali');
    people.box = 'alice';
    effect('ali'); // re-render, or the page adding ?employeeId= beside the same search
    effect('ali');
    expect(people.box).toBe('alice');
    people.box = '';
    effect('ali');
    expect(people.box).toBe(''); // a cleared box stays cleared
    effect('bob'); // navigation to a different search
    expect(people.box).toBe('bob');
    effect(null); // navigation without the parameter leaves what is there
    expect(people.box).toBe('bob');
    people.box = 'x';
    effect('bob'); // following a search link again seeds it again
    expect(people.box).toBe('bob');
  });

  test('the People page seeds search from the URL without keying that effect on the typed value', () => {
    const people = read('src/views/EmployeesPage.tsx');
    expect(people).toContain("const [search, setSearch] = useState(() => searchParams?.get('search') ?? '');");
    expect(people).toMatch(/const seeded = searchSeed\.take\(searchParams\?\.get\('search'\) \?\? null\);\n\s+if \(seeded !== undefined\) setSearch\(seeded\);\n\s+\}, \[searchParams, searchSeed\]\);/);
    expect(people).not.toContain('searchFromUrl !== search');
    // #128's gate still decides which employee load may write the table.
    expect(people).toContain('await runLatest(employeeLoadGate, () => employeesApi.list({');
  });

  test('R03: a run is shown in its employing company\'s currency, never the tenant default', () => {
    const companies = [
      { id: 'ksa', name: 'Evostel Certification KSA', tradeName: '', defaultCurrency: 'sar' },
      { id: 'uae', name: 'Evostel UAE', tradeName: 'Evostel Dubai', defaultCurrency: 'AED' },
    ];
    expect(resolvePayrollRunCurrency({ companyId: 'ksa' }, companies, 'loaded'))
      .toEqual({ status: 'resolved', currency: 'SAR', companyName: 'Evostel Certification KSA' });
    expect(resolvePayrollRunCurrency({ companyId: 'uae' }, companies, 'loaded'))
      .toEqual({ status: 'resolved', currency: 'AED', companyName: 'Evostel Dubai' });
    expect(resolvePayrollRunCurrency({ companyId: 'ksa' }, [], 'loading')).toEqual({ status: 'loading' });
  });

  test('R03: an unconfirmed currency is unavailable with a reason, never guessed', () => {
    const failed = resolvePayrollRunCurrency({ companyId: 'ksa' }, [], 'failed');
    expect(failed.status).toBe('unavailable');
    expect(failed.status === 'unavailable' && failed.reason).toContain('could not be loaded');
    const unknown = resolvePayrollRunCurrency({ companyId: 'gone' }, [{ id: 'ksa', defaultCurrency: 'SAR' }], 'loaded');
    expect(unknown.status).toBe('unavailable');
    const blank = resolvePayrollRunCurrency({ companyId: 'ksa' }, [{ id: 'ksa', name: 'KSA Co', defaultCurrency: ' ' }], 'loaded');
    expect(blank.status === 'unavailable' && blank.reason).toContain('KSA Co has no default currency');
    expect(resolvePayrollRunCurrency(null, [], 'loaded').status).toBe('unavailable');
  });

  test('R03: a run without a legal entity is labelled only when every company shares one currency', () => {
    expect(resolvePayrollRunCurrency({ companyId: null }, [{ id: 'a', defaultCurrency: 'SAR' }, { id: 'b', defaultCurrency: 'sar' }], 'loaded'))
      .toEqual({ status: 'resolved', currency: 'SAR' });
    const mixed = resolvePayrollRunCurrency({ companyId: null }, [{ id: 'a', defaultCurrency: 'SAR' }, { id: 'b', defaultCurrency: 'AED' }], 'loaded');
    expect(mixed.status).toBe('unavailable');
    expect(mixed.status === 'unavailable' && mixed.reason).toContain('AED, SAR');
  });

  test('R03: group totals never add different currencies into one labelled figure', () => {
    expect(commonPayrollCurrency([{ currency: 'sar', hasPayrollRun: true }, { currency: 'SAR', hasPayrollRun: true }])).toBe('SAR');
    expect(commonPayrollCurrency([{ currency: 'SAR', hasPayrollRun: true }, { currency: 'AED', hasPayrollRun: true }])).toBeNull();
    expect(commonPayrollCurrency([{ currency: 'SAR', hasPayrollRun: true }, { currency: 'AED', hasPayrollRun: false }])).toBe('SAR');
    expect(commonPayrollCurrency([])).toBeNull();
  });

  test('R03: the approval gate requires a confirmed run currency and no tenant-currency totals remain', () => {
    const payroll = read('src/views/PayrollPage.tsx');
    expect(payroll).toContain('const resolution = resolvePayrollRunCurrency(run, companies, companiesState);');
    expect(payroll).toContain('= useRunCurrency(selectedRun);');
    expect(payroll).toMatch(/const gateSatisfied =\n.*&& currencyConfirmed;/);
    expect(payroll).toContain('<RunCurrencyNotice resolution={runCurrency} companiesState={companiesState} onRetry={retryRunCurrency} blocksApproval />');
    expect(payroll).toContain('Approval is unavailable until the currency is confirmed.');
    // No single run's amounts — approval, register or reconciliation — are labelled with the tenant default.
    expect(payroll).not.toContain('fmtAmt(selectedRun.totalGrossSalary, currencyCode)');
    expect(payroll).not.toContain('fmtAmt(overview.totalGrossPayroll, currencyCode)');
    expect(payroll).not.toContain('fmtAmt(m.current, currencyCode)');
    expect(payroll).not.toContain('fmtAmt(slips.reduce((s, x) => s + x.grossSalary, 0), currencyCode)');
  });

  test('R03: a set of runs has one currency only when every run resolves to it', () => {
    const companies = [{ id: 'ksa', defaultCurrency: 'SAR' }, { id: 'ksa2', defaultCurrency: 'sar' }, { id: 'uae', defaultCurrency: 'AED' }];
    expect(resolvePayrollRunsCurrency([{ companyId: 'ksa' }, { companyId: 'ksa2' }], companies, 'loaded'))
      .toEqual({ status: 'resolved', currency: 'SAR' });
    const mixed = resolvePayrollRunsCurrency([{ companyId: 'ksa' }, { companyId: 'uae' }], companies, 'loaded');
    expect(mixed.status).toBe('unavailable');
    expect(mixed.status === 'unavailable' && mixed.reason).toContain('AED, SAR');
    expect(resolvePayrollRunsCurrency([{ companyId: 'ksa' }], [], 'loading')).toEqual({ status: 'loading' });
    expect(resolvePayrollRunsCurrency([{ companyId: 'gone' }, { companyId: 'ksa' }], companies, 'loaded').status).toBe('unavailable');
    expect(resolvePayrollRunsCurrency([], companies, 'loaded').status).toBe('unavailable');
  });

  test('R03: totals are kept per currency, never added across currencies', () => {
    const batches = [
      { currency: 'SAR', totalAmount: 100 }, { currency: 'sar', totalAmount: 50 },
      { currency: 'AED', totalAmount: 30 }, { currency: '', totalAmount: 5 },
    ];
    expect(totalsByCurrency(batches, (b) => b.currency, (b) => b.totalAmount)).toEqual([
      { currency: 'SAR', total: 150 }, { currency: 'AED', total: 30 }, { currency: null, total: 5 },
    ]);
  });

  test('R03: payment tracking, reports YTD, EOSB history and the home dashboard carry no tenant-default or hard-coded currency', () => {
    const payroll = read('src/views/PayrollPage.tsx');
    expect(payroll).not.toContain('fmtAmt(total, currencyCode)');
    expect(payroll).not.toContain('fmtAmt(summary.totalGrossYtd, currencyCode)');
    expect(payroll).not.toContain('fmtAmt(summary.totalNetYtd, currencyCode)');
    expect(payroll).not.toContain('{currencyCode} {(h.eligibleSalary');
    expect(payroll).not.toContain('{currencyCode} {(h.calculatedAmount');
    expect(payroll).toContain('const totals = totalsByCurrency(batches, b => b.currency, b => b.totalAmount);');
    expect(payroll).toContain('const totals = totalsByCurrency(summary.ytdByCompany, row => {');

    // The dashboard's money helper has no currency default, and no dashboard surface spells SAR.
    expect(fmtMoney(1_500)).toBe('1.5K');
    expect(fmtMoney(2_400_000, 'AED')).toBe('AED 2.40M');
    for (const file of ['src/components/dashboard/dashboardModel.ts', 'src/components/dashboard/PayrollHero.tsx',
      'src/components/dashboard/PayrollByDepartment.tsx', 'src/components/dashboard/charts/Visuals.tsx']) {
      const code = read(file).split('\n').filter((line) => !/^\s*(\*|\/\/|\/\*)/.test(line)).join('\n');
      expect(code, file).not.toMatch(/\bSAR\b/);
    }
    const dashboard = read('src/views/DashboardPage.tsx');
    expect(dashboard).toContain('resolvePayrollRunCurrency({ companyId: latestRun.companyId ?? null }, payrollCompanies.companies, payrollCompanies.state)');
    expect(dashboard).toContain('const trendCurrency = resolvePayrollRunsCurrency(');
  });
});

// F10 — maker/checker on final settlements, and whose figures the home dashboard shows.
test.describe('browserless F10 contracts', () => {
  test('the final-settlement approval has no maker/checker escape hatch', () => {
    const payroll = read('src/views/PayrollPage.tsx');
    const api = read('src/api/payroll.ts');
    expect(payroll).not.toContain('acknowledgeSelfApproval');
    expect(payroll).not.toContain('Acknowledge maker/checker exception');
    expect(api).not.toContain('acknowledgeSelfApproval');
    expect(payroll).toContain('whoever calculated, recalculated or submitted a settlement cannot approve it.');
  });

  test('payroll figures and the reports link are only offered to someone who can read them', () => {
    const dashboard = read('src/views/DashboardPage.tsx');
    expect(dashboard).toContain("const canReadPayroll = hasPermission('payroll.read');");
    expect(dashboard).toContain("const canReadReports = hasPermission('reports.read');");
    expect(dashboard).toMatch(/const heroEl = data && canReadPayroll\n/);
    expect(dashboard).toContain('const payDeptEl = data && canReadPayroll ? <PayrollByDepartment');
    expect(dashboard).toMatch(/\{canReadReports && \(\n\s+<Link href="\/reports"/);
    expect(dashboard).toContain('usePayrollCompanies(canReadPayroll)');
  });

  test('payroll readiness withheld by the API (null) is unknown, never "no gaps"', () => {
    const data = {
      overview: { complianceCriticalTotal: 0, alerts: [], approvalQueue: [], pendingApprovals: 0 },
      kpis: {
        pendingLeaveRequests: 0, pendingAttendanceCorrections: 0, attendanceExceptions: 0, expiringDocuments: 0,
        expiredDocuments: 0, missingDocuments: 0, qiwaEnabled: false, missingSalaryAssignments: null, missingBankDetails: null,
      },
    } as unknown as DashboardFull;
    expect(buildAttention(data, [], Date.now(), { payroll: false })).toEqual([]);
  });
});

// F06 / I01 — payment readiness is not salary coverage.
test.describe('browserless payroll payment-readiness contracts', () => {
  const prerequisites = (over: Partial<PaymentPrerequisites> = {}): PaymentPrerequisites => ({
    evaluatedEmployees: 250,
    blockedEmployees: 0,
    employeesWithRecommendations: 0,
    attendanceChecked: true,
    companyBlocking: [],
    companyRecommended: [],
    blocking: [],
    recommended: [],
    employees: [],
    employeesTruncated: false,
    ...over,
  });

  test('100% salary coverage with no bank details reads as blocked, not ready', () => {
    const headline = paymentReadinessHeadline(prerequisites({
      blockedEmployees: 250,
      blocking: [{ code: 'MISSING_PAYROLL_PROFILE', label: 'No payroll profile — no bank IBAN or MOL ID on file', nextAction: 'Open the employee and add their payroll and bank details', count: 250 }],
    }));
    expect(headline.tone).toBe('blocked');
    expect(headline.tone === 'blocked' && headline.title).toBe('250 of 250 active employees cannot be paid yet');
    expect(headline.tone === 'blocked' && headline.detail).toContain('Salary coverage alone does not make payroll ready to pay');
  });

  test('recommendations never make payroll read as blocked, and a company blocker always does', () => {
    const recommendedOnly = paymentReadinessHeadline(prerequisites({
      employeesWithRecommendations: 250,
      recommended: [{ code: 'WARN_NO_ATTENDANCE', label: 'No attendance recorded in this period', nextAction: 'Process attendance', count: 250 }],
    }));
    expect(recommendedOnly.tone).toBe('attention');
    expect(recommendedOnly.tone === 'attention' && recommendedOnly.detail).toContain('do not block approval');

    const companyBlocked = paymentReadinessHeadline(prerequisites({
      companyBlocking: [{ companyId: 'ksa', companyName: 'KSA Co', code: 'COMPANY_CURRENCY_MISSING', label: 'The company has no currency', nextAction: 'Set it' }],
    }));
    expect(companyBlocked.tone).toBe('blocked');

    expect(paymentReadinessHeadline(prerequisites()).tone).toBe('clear');
    expect(paymentReadinessHeadline(prerequisites({ evaluatedEmployees: 0 })).tone).toBe('empty');
    // An API that predates the field makes no claim either way.
    expect(paymentReadinessHeadline(undefined).tone).toBe('unknown');
  });

  test('a stale salary insight is retired only by tenant-wide live coverage', () => {
    const insights = [{ insightType: 'MissingSalarySetup' }, { insightType: 'PayrollVariance' }];
    const full = { companyId: null, totalActiveEmployees: 250, employeesWithSalary: 250, salaryCoveragePercent: 100 };
    expect(filterPayrollInsightsForReadiness(insights, full)).toEqual([{ insightType: 'PayrollVariance' }]);
    // One fully covered company says nothing about the tenant's other companies.
    expect(filterPayrollInsightsForReadiness(insights, { ...full, companyId: 'ksa' })).toHaveLength(2);
    expect(filterPayrollInsightsForReadiness(insights, { ...full, employeesWithSalary: 249, salaryCoveragePercent: 99.6 })).toHaveLength(2);
    expect(filterPayrollInsightsForReadiness(insights, null)).toHaveLength(2);
  });

  test('the dashboard raises live payroll gaps and drops the stale salary insight they replace', () => {
    const data = {
      summary: { totalEmployees: 250, activeEmployees: 250, presentToday: 0, onLeave: 0, absent: 0, overtimeHours: 0, churnRisk: 0 },
      trends: [], payrollTrends: [], activityFeed: [],
      overview: { pendingApprovals: 0, approvalQueue: [], payrollSummary: null, payrollByEntity: [], workforceMix: [], headcountByDepartment: [], alerts: [], openLeaveRequests: 0, newJoinersThisMonth: 0 },
      kpis: { pendingLeaveRequests: 0, pendingAttendanceCorrections: 0, attendanceExceptions: 0, expiringDocuments: 0, expiredDocuments: 0, missingDocuments: 0, qiwaEnabled: true, missingSalaryAssignments: 0, missingBankDetails: 250 },
    } as unknown as DashboardFull;
    const staleInsight = {
      id: 'i1', tenantId: 't', module: 'Payroll', insightType: 'MissingSalarySetup', severity: 'Critical', employeeId: null, employeeName: '',
      title: '250 employee(s) without salary assignment', summary: '250 of 250 active employees have no salary', dataJson: '{}', generatedBy: 'rules',
      isAcknowledged: false, createdAtUtc: '2026-09-26T01:25:56Z',
    } as unknown as AIInsight;

    const items = buildAttention(data, [staleInsight]);
    expect(items.map((i) => i.id)).toEqual(['payroll-bank-missing']);
    expect(items[0].title).toBe('250 employees without bank details');
    expect(items[0].to).toBe('/payroll');

    // Someone who cannot open payroll is not sent there.
    expect(buildAttention(data, [], Date.now(), { payroll: false })).toEqual([]);

    // An API without the live fields keeps the insight — nothing is claimed that was not measured.
    const legacy = { ...data, kpis: { ...data.kpis, missingSalaryAssignments: undefined, missingBankDetails: undefined } } as DashboardFull;
    expect(buildAttention(legacy, [staleInsight]).map((i) => i.id)).toEqual(['insight-MissingSalarySetup-i1']);
  });

  test('the payroll dashboard shows payment readiness ahead of the totals, with blocking and recommended apart', () => {
    const payroll = read('src/views/PayrollPage.tsx');
    expect(payroll).toContain('<PaymentPrerequisitesPanel prerequisites={readiness.paymentPrerequisites} onNavigate={onNavigate} />');
    expect(payroll.indexOf('<PaymentPrerequisitesPanel')).toBeLessThan(payroll.indexOf('<KpiCard label="Gross Payroll"'));
    expect(payroll).toContain('>Blocks payment</p>');
    expect(payroll).toContain('>Recommended — does not block approval</p>');
    expect(payroll).toContain('href={`/people?employeeId=${e.employeeId}`}');
    expect(payroll).toContain('const visibleInsights = filterPayrollInsightsForReadiness(insights, readiness);');
  });
});

test.describe('browserless withheld establishment spend contracts', () => {
  test('withheld spend (null) is restricted, never 0 or 0%', () => {
    // Control: the pre-fix rollup added null as 0 and divided it by the budget.
    const withheldRows = [{ spend: null as number | null, budget: 50000 }, { spend: null as number | null, budget: 20000 }];
    const naiveSpend = withheldRows.reduce((s, r) => s + (r.spend as unknown as number), 0);
    expect(naiveSpend).toBe(0);
    expect(Math.round((naiveSpend / 70000) * 100)).toBe(0);

    expect(totalSpend(withheldRows.map((r) => r.spend))).toBeNull();
    expect(spendUtilisation(null, 50000)).toEqual({ state: 'restricted' });
    expect(spendUtilisation(undefined, 50000)).toEqual({ state: 'restricted' });
    expect(spendUtilisation(null, 0)).toEqual({ state: 'restricted' });
  });

  test('a real zero is still zero, and one withheld row withholds the group total', () => {
    expect(totalSpend([0, 0])).toBe(0);
    expect(spendUtilisation(0, 50000)).toEqual({ state: 'known', percent: 0 });
    expect(totalSpend([12000, 30000])).toBe(42000);
    expect(spendUtilisation(42000, 40000)).toEqual({ state: 'known', percent: 105 });
    expect(spendUtilisation(42000, 0)).toEqual({ state: 'no-budget' });
    expect(totalSpend([12000, null])).toBeNull();
  });

  test('the establishment panel renders spend and utilisation through the withheld-aware helpers', () => {
    const panel = read('src/components/EstablishmentPanel.tsx');
    expect(panel).toContain('spend: totalSpend(gr.map(r => r.currentMonthlySpend)),');
    expect(panel).toContain('const u = spendUtilisation(tot.spend, tot.budget);');
    expect(panel).toContain('const u = spendUtilisation(r.currentMonthlySpend, r.monthlyBudgetAmount);');
    expect(panel).toContain("r.currentMonthlySpend === null ? SPEND_RESTRICTED_LABEL");
    expect(panel).not.toMatch(/s \+ r\.currentMonthlySpend/);
    expect(read('src/api/planning.ts')).toContain('currentMonthlySpend: number | null;');
  });
});

test.describe('browserless outbound-integration truthfulness contracts', () => {
  test('outbound integrations never read as delivered or filed when they were not (F09)', () => {
    // Qiwa: a data check, never "Live". Only a positive partner flag from the server changes the label,
    // and even then it never says Live, Connected, Synced or Filed.
    expect(qiwaModeLabel(true)).toBe('Qiwa partner integration (agreement on file)');
    expect(qiwaModeLabel(false)).toBe(QIWA_SIMULATED_LABEL);
    expect(qiwaModeLabel(undefined)).toBe('Qiwa data check only (nothing sent to Qiwa)');
    expect(qiwaConnectionLabel('Simulated')).toBe(QIWA_SIMULATED_LABEL);
    // A stored "Connected" row is not believed unless the running server is a partner integration.
    expect(qiwaConnectionLabel('Connected')).toBe(QIWA_SIMULATED_LABEL);
    expect(qiwaConnectionLabel('Connected', false)).toBe(QIWA_SIMULATED_LABEL);
    for (const status of ['Simulated', 'Connected', 'Disconnected', 'NotConfigured', 'ConfigurationError', 'ApiError', 'Synced', 'whatever']) {
      for (const live of [undefined, false]) {
        const label = qiwaConnectionLabel(status, live);
        expect(label, `${status}/${live}`).not.toMatch(/synced|connected|\bLive\b|filed with qiwa/i);
      }
    }
    for (const live of [true, false, undefined]) {
      expect(qiwaModeLabel(live)).not.toMatch(/synced|connected|\bLive\b|filed with qiwa/i);
    }

    // Scheduled reports: SMTP can confirm a relay accepted it, nothing more; not-set-up and captured
    // are neither success nor failure.
    expect(reportRunStatusLabel('Success')).toBe('Accepted by mail server');
    expect(reportRunStatusLabel('NotConfigured')).toContain('not set up');
    expect(reportRunStatusLabel('Captured')).toContain('not sent');
    expect(reportRunStatusTone('NotConfigured')).toBe('warn');
    expect(reportRunStatusTone('Captured')).toBe('warn');
    expect(emailModeLabel('capture')).toContain('nothing is sent');
    expect(emailModeLabel('not_configured')).toContain('nothing is sent');
    expect(emailModeLabel(undefined)).toBe('Not reported');

    // Zero problems is a count, never "all delivered"; a missing payload is "not reported".
    expect(deliveryAttentionSummary({ deadLetter: 0, failed: 0 })).toBe('No undelivered messages recorded.');
    expect(deliveryAttentionSummary({ deadLetter: 2, notConfigured: 1 })).toContain('2 gave up after retries');
    expect(deliveryAttentionSummary(undefined)).toContain('not reported');

    const dashboard = read('src/views/SaudiComplianceDashboard.tsx');
    expect(dashboard).toContain('qiwa-simulation-notice');
    expect(dashboard).not.toContain('Last filed with Qiwa');
    expect(dashboard).not.toContain('<dt className="text-slate-400">Last sync</dt>');
    expect(dashboard).toContain('{qiwaConnectionLabel(status, live)}');
    expect(dashboard).toContain("{qiwa.isLiveIntegration === true && (");

    // WS3: the Qiwa screens never print a sync/connection claim for the sandbox, and the OAuth form
    // (with its "Qiwa Developer Portal → My Apps" hunt for secrets) is behind the operator flag.
    const config = read('src/views/SaudiComplianceConfig.tsx');
    expect(config).not.toContain('My Apps');
    expect(config).not.toContain('Developer Portal');
    expect(config).not.toContain('never <strong>Synced</strong>');
    expect(config).toContain('{credentialFormEnabled && (');
    expect(config).toContain('{qiwaConnectionLabel(status, live)}');
    for (const source of [config, dashboard]) {
      expect(source).not.toMatch(/>\s*(Synced|Connected|Live)\s*</);
      expect(source).not.toContain('Ready for Sync');
      expect(source).not.toContain('Filed with Qiwa');
    }

    // WS3: the KSA XML export is an internal register, never a "WPS/Mudad" file, and Accepted needs evidence.
    const payroll = read('src/views/PayrollPage.tsx');
    expect(payroll).toContain('Generate payroll register (internal — not a bank/WPS file)');
    expect(payroll).toContain("form.status === 'Accepted' && !evidenceId");
    expect(payroll).toContain('payrollApi.uploadWpsEvidence');
    // The server owns the WPS transition table; the screen keeps no copy of it.
    expect(payroll).toContain('b.allowedNextStatuses');
    expect(payroll).not.toContain('allowedWpsNext');
    expect(payroll).toContain('Accepted — evidence attached (not verified by Mudad)');

    const health = read('app/platform/system-health/page.tsx');
    expect(health).toContain('delivery-health-unavailable');
    expect(health).not.toContain("{ key: 'smtp',    label: 'Email (SMTP)' }");

    const billing = read('app/platform/tenants/[id]/billing/page.tsx');
    expect(billing).not.toContain('`Sent to ${r.billingEmail} ✓`');
  });
});
