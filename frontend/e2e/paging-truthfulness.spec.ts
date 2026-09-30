import { expect, test } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import { API_MAX_PAGE_SIZE, fetchAllPages, pageWindowText, type PageOf } from '../src/lib/paging';

/**
 * Silent truncation of paged lists. Most list endpoints clamp pageSize to 100 and answer a larger
 * request with 100 rows; payroll slips, payslips and overtime requests apply no cap, so a request
 * for 200 stopped at 200. Screens that took one page as "the list" (a payroll register, the
 * payslips KPI, an approval queue, a lookup select) lost every row past it without a word.
 */

const read = (relative: string) => fs.readFileSync(path.join(process.cwd(), relative), 'utf8');

/** A server that clamps pageSize like EmployeeManagementService and echoes page like PagedResult. */
function clampingServer(rowCount: number, cap = 100) {
  const rows = Array.from({ length: rowCount }, (_, i) => `row-${i + 1}`);
  const calls: Array<{ page: number; pageSize: number }> = [];
  const fetchPage = async (page: number, pageSize: number): Promise<PageOf<string>> => {
    calls.push({ page, pageSize });
    const size = Math.min(Math.max(pageSize, 1), cap);
    return { items: rows.slice((page - 1) * size, page * size), total: rows.length, page };
  };
  return { rows, calls, fetchPage };
}

test.describe('paged lists are read in full or shown as a window', () => {
  test('the bug this replaces: one big page from a clamping endpoint stops at 100 rows', async () => {
    const server = clampingServer(250);
    const firstPage = await server.fetchPage(1, 200);
    expect(firstPage.items).toHaveLength(100);
    expect(firstPage.total).toBe(250);
  });

  test('fetchAllPages returns every row of a 250-row list in 100-row pages', async () => {
    const server = clampingServer(250);
    const all = await fetchAllPages(server.fetchPage);
    expect(all).toEqual(server.rows);
    expect(server.calls).toEqual([
      { page: 1, pageSize: API_MAX_PAGE_SIZE },
      { page: 2, pageSize: API_MAX_PAGE_SIZE },
      { page: 3, pageSize: API_MAX_PAGE_SIZE },
    ]);
  });

  test('a server that caps below the requested size is still read to the end, by its total', async () => {
    const server = clampingServer(130, 50);
    expect(await fetchAllPages(server.fetchPage)).toEqual(server.rows);
    expect(server.calls.map((c) => c.page)).toEqual([1, 2, 3]);
  });

  test('an exact multiple of the page size and an empty list both stop without an extra request', async () => {
    const full = clampingServer(200);
    expect(await fetchAllPages(full.fetchPage)).toHaveLength(200);
    expect(full.calls).toHaveLength(2);
    const empty = clampingServer(0);
    expect(await fetchAllPages(empty.fetchPage)).toEqual([]);
    expect(empty.calls).toHaveLength(1);
  });

  test('without a total, a short page ends the list', async () => {
    const rows = Array.from({ length: 150 }, (_, i) => i);
    const all = await fetchAllPages(async (page, pageSize) => ({ items: rows.slice((page - 1) * pageSize, page * pageSize) }));
    expect(all).toEqual(rows);
  });

  test('an endpoint that ignores page fails loudly instead of repeating page 1', async () => {
    const rows = Array.from({ length: 100 }, (_, i) => i);
    await expect(fetchAllPages(async () => ({ items: rows, total: 250, page: 1 }))).rejects.toThrow(/page 1 when page 2/);
  });

  test('a list past the page limit fails instead of returning part of it', async () => {
    const server = clampingServer(500);
    await expect(fetchAllPages(server.fetchPage, { maxPages: 3 })).rejects.toThrow(/more than 300 rows/);
  });

  test('a page request that fails fails the whole list', async () => {
    let calls = 0;
    const failing = async (page: number): Promise<PageOf<number>> => {
      calls += 1;
      if (page === 2) throw new Error('HTTP 500');
      return { items: Array.from({ length: 100 }, (_, i) => i), total: 150, page };
    };
    await expect(fetchAllPages(failing)).rejects.toThrow('HTTP 500');
    expect(calls).toBe(2);
  });

  test('a window of a longer list says so, from the server total', () => {
    expect(pageWindowText(100, 1234, 'requests')).toBe('Showing 100 of 1,234 requests');
    expect(pageWindowText(100, 100, 'requests')).toBeNull();
    expect(pageWindowText(37, 37, 'timesheets')).toBeNull();
    expect(pageWindowText(0, null, 'timesheets')).toBeNull();
  });
});

test.describe('no screen reads one oversized page as the whole list', () => {
  const SOURCE_DIRS = ['src', 'app'];

  function sourceFiles(dir: string): string[] {
    const out: string[] = [];
    for (const entry of fs.readdirSync(path.join(process.cwd(), dir), { withFileTypes: true })) {
      const rel = path.join(dir, entry.name);
      if (entry.isDirectory()) out.push(...sourceFiles(rel));
      else if (/\.(ts|tsx)$/.test(entry.name)) out.push(rel);
    }
    return out;
  }

  test('no API call asks for more than 100 rows in one page', () => {
    const offenders: string[] = [];
    for (const file of SOURCE_DIRS.flatMap(sourceFiles)) {
      read(file).split('\n').forEach((line, i) => {
        const explicit = /pageSize:\s*(\d+)/.exec(line);
        const positional = /\.list\([^)]*\b1,\s*(\d+)\)/.exec(line);
        for (const match of [explicit, positional]) {
          if (match && Number(match[1]) > API_MAX_PAGE_SIZE) offenders.push(`${file}:${i + 1}: ${line.trim()}`);
        }
      });
    }
    expect(offenders).toEqual([]);
  });

  // ── Reading the server's default page as the list ───────────────────────────────────────
  // The quieter form of the same bug: a screen calls a paged endpoint without saying which page or
  // how many rows, gets the server's default page (20–50 rows), and shows it as the whole list.
  // Compliance, Leave, Performance, Recruitment and the assistant all did this until they were fixed
  // after #152's sweep found them.

  /** Paged API methods, found by reading src/api: a GET typed `{ items, total }` or PagedResult. */
  function pagedApiMethods(): Map<string, string> {
    const methods = new Map<string, string>();
    for (const file of sourceFiles('src/api')) {
      let obj: string | null = null;
      let method: string | null = null;
      let body = '';
      const flush = () => {
        if (obj && method && /client\.get</.test(body) && !/fetchAllPages/.test(body)
          && (/PagedResult</.test(body) || /\bitems:[^;>]*;\s*total\b|\btotal:\s*number;[^>]*\bitems:/.test(body))) {
          methods.set(`${obj}.${method}`, file);
        }
      };
      for (const line of read(file).split('\n')) {
        const opened = /^export const (\w+)\s*=\s*\{/.exec(line);
        const member = /^ {2}(?:async\s+)?(\w+)(?::\s*(?:async\s*)?\(|\s*\()/.exec(line);
        if (opened) { flush(); obj = opened[1]; method = null; body = ''; }
        else if (/^\};?/.test(line)) { flush(); obj = null; method = null; body = ''; }
        else if (obj && member) { flush(); method = member[1]; body = line; }
        else if (obj && method) body += `\n${line}`;
      }
      flush();
    }
    return methods;
  }

  /**
   * Calls to a paged method that pass neither `pageSize` nor a variable `page`, each with the reason
   * it is not the bug. Anything else is. An entry that no longer matches a call fails too, so this
   * list only shrinks.
   */
  const DEFAULT_PAGE_BY_DESIGN: Array<{ file: string; call: string; why: string }> = [
    { file: 'src/views/PerformancePage.tsx', call: 'pipApi.terminationQueue()',
      why: 'The endpoint returns the whole queue and takes no page (PIPController.TerminationQueue).' },
  ];

  test('the paged API methods are found (the check below is not vacuous)', () => {
    const methods = pagedApiMethods();
    for (const known of ['complianceVisaApi.list', 'leaveBalancesApi.list', 'goalsApi.list', 'interviewsApi.list', 'aiAssistantApi.queryHistory', 'companiesApi.list']) {
      expect(methods.has(known), known).toBe(true);
    }
  });

  test('no list call silently takes the server default page', () => {
    const methods = [...pagedApiMethods().keys()];
    const found: Array<{ file: string; call: string; line: number }> = [];
    for (const file of SOURCE_DIRS.flatMap(sourceFiles).map((f) => f.split(path.sep).join('/'))) {
      if (file.startsWith('src/api/')) continue;
      const text = read(file);
      for (const key of methods) {
        const re = new RegExp(`\\b${key.replace('.', '\\.')}\\(`, 'g');
        for (let m = re.exec(text); m; m = re.exec(text)) {
          let depth = 1;
          let j = m.index + m[0].length;
          for (; j < text.length && depth > 0; j++) depth += text[j] === '(' ? 1 : text[j] === ')' ? -1 : 0;
          const args = text.slice(m.index + m[0].length, j - 1);
          // `page: 1` alone still means "the default-sized first page".
          const explicit = /\bpageSize\b/.test(args) || /\bpage\b/.test(args.replace(/\bpage:\s*1\b/g, ''));
          if (!explicit) found.push({ file, call: `${key}(${args.replace(/\s+/g, ' ').trim()})`, line: text.slice(0, m.index).split('\n').length });
        }
      }
    }
    const allowed = (f: { file: string; call: string }) => DEFAULT_PAGE_BY_DESIGN.some((a) => a.file === f.file && a.call === f.call);
    expect(found.filter((f) => !allowed(f)).map((f) => `${f.file}:${f.line} ${f.call}`)).toEqual([]);
    expect(DEFAULT_PAGE_BY_DESIGN.filter((a) => !found.some((f) => f.file === a.file && f.call === a.call)).map((a) => a.call)).toEqual([]);
  });

  test('the payroll register, payslips, overtime queue and lookups read every page', () => {
    const payroll = read('src/views/PayrollPage.tsx');
    expect(payroll).toContain('payrollApi.allSlips(run.id)');
    expect(payroll).toContain('payrollApi.allPayslips(runId)');
    expect(payroll).not.toMatch(/listRuns\(\{ pageSize: 50 \}\)/);

    const overtime = read('src/views/OvertimePage.tsx');
    expect(overtime).toContain("overtimeApi.allRequests({ status: 'PendingManager' })");
    expect(overtime).toContain("overtimeApi.allRequests({ status: 'PendingHR' })");
    expect(overtime).toContain('setRequests(await overtimeApi.allRequests({ status }));');
    // History lists page with the server's total instead of calling the first page the list.
    expect(overtime.match(/<ListWindowFooter /g)).toHaveLength(2);
    expect(read('src/views/TimesheetsPage.tsx')).toContain('<ListWindowFooter shown={rows.length} total={list.total} noun="timesheets"');

    const people = read('src/views/EmployeesPage.tsx');
    for (const call of ['companiesApi.listAll()', 'departmentsApi.listAll()', 'designationsApi.listAll()', "employeesApi.listAll({ status: 'Active' })"]) {
      expect(people).toContain(call);
    }
    expect(read('src/components/DraftPlacementFix.tsx')).not.toContain('page <= 20');
    expect(read('src/views/LeavePage.tsx')).toContain("leaveRequestsApi.listAll({ status: 'PendingManagerApproval', ...groupFilter })");
    expect(read('src/views/HrLettersPage.tsx')).toContain('hrLettersApi.allRequests({ status })');
  });
});
