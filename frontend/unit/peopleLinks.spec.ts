import { expect, test } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import {
  PEOPLE_DIRECTORY_QUERY_KEYS,
  buildPeopleCreateHref,
  buildPeopleReturnTo,
  employeeWorkspaceHref,
  legacyEmployeeTarget,
  parsePeopleDirectoryState,
  serializePeopleDirectoryState,
  validatePeopleReturnTo,
  type EmployeeSection,
  type PeopleDirectoryState,
} from '../src/lib/peopleRoutes';
import { readinessFixHref, readinessFixTarget } from '../src/lib/readinessFixTarget';
import { resolveComplianceRoute } from '../src/lib/complianceRoutes';
import { navigationGroups, navigationHints } from '../src/routes/navigation';
import { SETUP_SECTION_ORDER } from '../src/lib/setupAreas';

/**
 * People module link check ("find broken links and dead ends").
 *
 * Every href / router.push / Link / returnTo / legacy redirect this module can emit is checked
 * against the real app route table (the `app/` directory), the sidebar, the page permission gates
 * and the query keys the pages actually read. Tests named "DEFECT:" are test.fixme: each one states
 * the correct behaviour and fails on current code, so they stay out of CI until the product is
 * fixed (then drop the `.fixme`). The reasoning for each is in the test body.
 *
 * Source-scan tests read the .tsx as text, the same approach as costAccountingLinks.spec.ts.
 */
const FRONTEND = path.resolve(__dirname, '..');
const src = (...p: string[]) => fs.readFileSync(path.join(FRONTEND, ...p), 'utf8');

function listFiles(dir: string, match: RegExp, out: string[] = []): string[] {
  const abs = path.join(FRONTEND, dir);
  for (const name of fs.readdirSync(abs)) {
    const rel = path.join(dir, name);
    if (fs.statSync(path.join(FRONTEND, rel)).isDirectory()) listFiles(rel, match, out);
    else if (match.test(name)) out.push(rel);
  }
  return out;
}

function stripComments(text: string): string {
  return text.replace(/\/\*[\s\S]*?\*\//g, '').replace(/^\s*\/\/.*$/gm, '');
}

// ---- the app route table -------------------------------------------------------------------

/** `app/(dashboard)/people/[employeeId]/edit/page.tsx` -> `/people/[employeeId]/edit`. */
const APP_ROUTES: string[] = listFiles('app', /^(page|route)\.tsx?$/).map(rel => {
  const dir = path.dirname(rel).split(path.sep).slice(1).filter(seg => !(seg.startsWith('(') && seg.endsWith(')')));
  return '/' + dir.join('/');
});
const APP_TOP_SEGMENTS = new Set(APP_ROUTES.map(r => r.split('/')[1]).filter(Boolean));

function routeResolves(href: string): boolean {
  const pathname = href.split(/[?#]/)[0].replace(/\/+$/, '') || '/';
  const segments = pathname.split('/');
  return APP_ROUTES.some(route => {
    const parts = route.replace(/\/$/, '').split('/');
    return parts.length === segments.length && parts.every((part, i) => /^\[.+\]$/.test(part) ? segments[i].length > 0 : part === segments[i]);
  });
}

// ---- module sources ------------------------------------------------------------------------

const MODULE_SOURCES: string[] = [
  'src/views/EmployeesPage.tsx',
  'src/views/ExEmployeesTable.tsx',
  'src/views/NewHiresPage.tsx',
  ...listFiles('src/views/employees', /\.tsx?$/),
  ...listFiles('src/views/people', /\.tsx?$/),
  ...listFiles('src/components/employeeAccess', /\.tsx?$/),
  'src/components/EmployeeCompletionHandoff.tsx',
  'src/components/ReadinessBadge.tsx',
  'src/hooks/usePeopleDirectoryQuery.ts',
  'src/lib/peopleRoutes.ts',
  'src/lib/readinessFixTarget.ts',
  'src/lib/employeeAccess.ts',
  ...listFiles('app/(dashboard)/people', /\.tsx?$/),
];

const ORIGIN = 'https://app.test';

interface Literal { file: string; line: number; href: string }

/** Every string/template literal that starts with `/<known app segment>` in the module sources. */
function internalLiterals(): Literal[] {
  const found: Literal[] = [];
  for (const file of MODULE_SOURCES) {
    const text = stripComments(src(file));
    const re = /(['"`])(\/[A-Za-z][A-Za-z0-9_\-/.]*(?:\$\{[^}]*\}[A-Za-z0-9_\-/.]*)*)(?:[?#][^'"`\n]*)?\1/g;
    for (const m of text.matchAll(re)) {
      const full = m[0].slice(1, -1).replace(/\$\{[^}]*\}/g, '1');
      const first = full.split(/[/?#]/)[1];
      if (!APP_TOP_SEGMENTS.has(first)) continue;
      found.push({ file, line: text.slice(0, m.index).split('\n').length, href: full });
    }
  }
  return found;
}

const SECTIONS: EmployeeSection[] = ['overview', 'personal', 'employment', 'pay-benefits', 'compliance-documents', 'access-onboarding', 'activity'];

// =============================================================================================
// 1. Static targets resolve to a real app route
// =============================================================================================

test('the route reader sees the People app routes (guards the parser itself)', () => {
  for (const route of ['/people', '/people/[employeeId]', '/people/[employeeId]/edit', '/people/new', '/people/new-hires', '/people/onboarding-review', '/people/onboarding-tasks']) {
    expect(APP_ROUTES, route).toContain(route);
  }
  expect(routeResolves('/people/901/edit?x=1')).toBe(true);
  expect(routeResolves('/people/901/nope')).toBe(false);
  expect(routeResolves('/people/')).toBe(true);
  expect(internalLiterals().length).toBeGreaterThan(20);
});

test('every literal internal href/push target in the People module resolves to an existing app route', () => {
  const dead = internalLiterals()
    .filter(l => !routeResolves(l.href))
    .map(l => `${l.href}  (${l.file}:${l.line})`);
  expect(dead, 'literal link targets with no matching app/**/page.tsx').toEqual([]);
});

test('every /people? link in the module uses only query keys the People list parses', () => {
  const allowed = new Set([...PEOPLE_DIRECTORY_QUERY_KEYS, 'employeeId', 'tab', 'returnTo', 'section', 'panel', 'mode', 'draft', 'saved']);
  const bad: string[] = [];
  for (const l of internalLiterals().filter(x => x.href.startsWith('/people?'))) {
    for (const key of l.href.slice('/people?'.length).matchAll(/(?:^|&)([A-Za-z]+)=/g)) {
      if (!allowed.has(key[1])) bad.push(`${key[1]} in ${l.href} (${l.file}:${l.line})`);
    }
  }
  expect(bad).toEqual([]);
});

test('the /people/new, /people/new-hires and /people/<id> paths never collide (static segments win over [employeeId])', () => {
  // [employeeId] only accepts digits (isPositiveEmployeeId), so the static siblings must be separate pages.
  for (const staticPath of ['/people/new', '/people/new-hires', '/people/onboarding-review', '/people/onboarding-tasks']) {
    expect(APP_ROUTES, staticPath).toContain(staticPath);
  }
  const workspace = src('src/views/employees/EmployeeWorkspacePage.tsx');
  expect(workspace).toContain('/^[1-9]\\d*$/.test(value)');
  for (const bad of ['new', 'new-hires', '0', '-1', '01', '1.5', 'abc']) {
    expect(/^[1-9]\d*$/.test(bad), bad).toBe(false);
  }
});

test('every Setup deep link the People module emits names a real Setup section', () => {
  const known = new Set<string>(SETUP_SECTION_ORDER);
  const bad: string[] = [];
  for (const l of internalLiterals().filter(x => x.href.startsWith('/setup?'))) {
    // The template may end in a nested `${...}` expression, so read the tab id lexically rather than via URLSearchParams.
    const tab = l.href.match(/[?&]tab=([A-Za-z]+)/)?.[1];
    if (tab && !known.has(tab)) bad.push(`tab=${tab} (${l.file}:${l.line})`);
  }
  expect(bad).toEqual([]);
});

// =============================================================================================
// 2. Sidebar: HR & TIME entries -> pages, hints and permission gates
// =============================================================================================

const PEOPLE_NAV = [
  { label: 'People', path: '/people', page: 'app/(dashboard)/people/page.tsx' },
  { label: 'New Hires', path: '/people/new-hires', page: 'app/(dashboard)/people/new-hires/page.tsx' },
  { label: 'Profile review', path: '/people/onboarding-review', page: 'app/(dashboard)/people/onboarding-review/page.tsx' },
  { label: 'Onboarding Tasks', path: '/people/onboarding-tasks', page: 'app/(dashboard)/people/onboarding-tasks/page.tsx' },
] as const;

function gatePermissions(pageFile: string): string[] {
  const m = src(pageFile).match(/permissions=\{\[([^\]]*)\]\}/);
  return m ? [...m[1].matchAll(/'([^']+)'/g)].map(x => x[1]) : [];
}

for (const entry of PEOPLE_NAV) {
  test(`sidebar "${entry.label}" points at an existing page, has a hint, and shows to exactly the roles the page admits`, () => {
    const group = navigationGroups.find(g => g.label === 'HR & Time');
    expect(group, 'HR & Time group').toBeTruthy();
    const item = group!.items.find(i => i.label === entry.label);
    expect(item, `nav item ${entry.label}`).toBeTruthy();
    expect(item!.path).toBe(entry.path);
    expect(routeResolves(item!.path!)).toBe(true);
    expect(fs.existsSync(path.join(FRONTEND, entry.page))).toBe(true);
    expect(navigationHints[entry.path], 'hint for the page header').toBeTruthy();
    // PermissionGate and the Sidebar both use "any of": a mismatch is a link shown that bounces (or a page with no link).
    expect([...item!.requiredPermissions ?? []].sort()).toEqual(gatePermissions(entry.page).sort());
  });
}

test('sidebar People entries appear once each and in workflow order', () => {
  const items = navigationGroups.find(g => g.label === 'HR & Time')!.items;
  const order = items.map(i => i.path).filter(p => p?.startsWith('/people'));
  expect(order).toEqual(['/people', '/people/new-hires', '/people/onboarding-review', '/people/onboarding-tasks']);
  expect(new Set(order).size).toBe(order.length);
});

test('the mobile bottom nav People tab and the other People entry points resolve', () => {
  const mobile = src('src/layouts/MobileBottomNav.tsx');
  expect(mobile).toContain("path: '/people'");
  expect(mobile).toContain("perms: ['employees.read']");
  expect(routeResolves('/people')).toBe(true);
});

test('the new-hire/edit/create pages gate on the permission their primary action needs', () => {
  expect(gatePermissions('app/(dashboard)/people/page.tsx')).toEqual(['employees.read']);
  expect(gatePermissions('app/(dashboard)/people/[employeeId]/page.tsx')).toEqual(['employees.read']);
  // Edit and create are write-only: a read-only user is never offered the link (canWrite) and a typed URL is gated.
  expect(gatePermissions('app/(dashboard)/people/[employeeId]/edit/page.tsx')).toEqual(['employees.write']);
  expect(gatePermissions('app/(dashboard)/people/new/page.tsx')).toEqual(['employees.write']);
});

// =============================================================================================
// 3. Employee workspace URLs: section/panel/returnTo survive; destinations understand them
// =============================================================================================

const workspaceSource = src('src/views/employees/EmployeeWorkspacePage.tsx');
const knownSectionsInPage = (): Set<string> => {
  const block = workspaceSource.match(/const employeeSections = new Set<EmployeeSection>\(\[([\s\S]*?)\]\)/);
  return new Set([...(block?.[1] ?? '').matchAll(/'([^']+)'/g)].map(m => m[1]));
};

test('the workspace page accepts exactly the sections peopleRoutes can emit (no section falls back to Overview)', () => {
  expect([...knownSectionsInPage()].sort()).toEqual([...SECTIONS].sort());
  // and every section has a renderer in the shell
  const shell = src('src/views/employees/EmployeeWorkspaceShell.tsx');
  for (const section of SECTIONS) expect(shell, section).toContain(`section === '${section}'`);
});

test('every employeeWorkspaceHref combination resolves to a real route and keeps employeeId, section, panel and returnTo', () => {
  const returnTo = '/people?search=Alex&page=2&anchor=employee-901';
  const failures: string[] = [];
  for (const section of SECTIONS) {
    for (const edit of [false, true]) {
      for (const panel of [undefined, 'payroll']) {
        const href = employeeWorkspaceHref(901, { section, panel, edit, returnTo });
        const url = new URL(href, ORIGIN);
        if (!routeResolves(href)) failures.push(`unresolved ${href}`);
        if (url.pathname !== (edit ? '/people/901/edit' : '/people/901')) failures.push(`path ${href}`);
        if (url.searchParams.get('section') !== section) failures.push(`section lost ${href}`);
        if ((url.searchParams.get('panel') ?? undefined) !== panel) failures.push(`panel lost ${href}`);
        if (url.searchParams.get('returnTo') !== returnTo) failures.push(`returnTo mangled ${href}`);
      }
    }
  }
  expect(failures).toEqual([]);
});

test('"Edit full record" and the edit-then-save redirect keep the page they were opened from', () => {
  const returnTo = '/people?access=not_started%2Ccode_given&view=ex&page=4&anchor=employee-77';
  const edit = employeeWorkspaceHref(77, { edit: true, mode: 'guided', section: 'overview', returnTo });
  const url = new URL(edit, ORIGIN);
  expect(url.pathname).toBe('/people/77/edit');
  // What EmployeeWorkspacePage.onSaved does: view href for the same section/panel/returnTo, then `&saved=`.
  const savedBase = employeeWorkspaceHref(77, { section: 'overview', returnTo: url.searchParams.get('returnTo') ?? undefined });
  expect(savedBase).toContain('?');
  const after = new URL(`${savedBase}&saved=updated`, ORIGIN);
  expect(after.pathname).toBe('/people/77');
  expect(after.searchParams.get('saved')).toBe('updated');
  expect(after.searchParams.get('returnTo')).toBe(returnTo);
});

test('the edit-save redirect appends "&saved=" to a URL that always has a query (section is always passed)', () => {
  const onSaved = workspaceSource.slice(workspaceSource.indexOf('onSaved='));
  expect(onSaved).toMatch(/employeeWorkspaceHref\(record\.detail\.data!\.id, \{ section, panel, returnTo \}\)/);
  expect(onSaved).toContain('&saved=');
  // Invariant the string concat depends on: any href carrying a section has "?".
  for (const section of SECTIONS) expect(employeeWorkspaceHref(1, { section })).toContain('?section=');
});

test('the workspace page reads every query key the People module links emit, except mode', () => {
  const read = new Set([...workspaceSource.matchAll(/searchParams\.get\('([A-Za-z]+)'\)/g)].map(m => m[1]));
  expect(read).toEqual(new Set(['returnTo', 'section', 'panel', 'saved']));
});

test('"Edit full record" does not emit a mode param that nothing reads', () => {
  // The shell used to link with `mode=guided`; EmployeeWorkspacePage reads only returnTo/section/panel/saved and the editor
  // has no guided variant, so the param was dead. The button opens the editor (edit: true) with the same section and returnTo.
  const shell = stripComments(src('src/views/employees/EmployeeWorkspaceShell.tsx'));
  expect(shell).not.toMatch(/mode:\s*'guided'/);
  expect(shell).toContain("employeeWorkspaceHref(employee.id, { edit: true, section, returnTo })");
  const read = new Set([...workspaceSource.matchAll(/searchParams\.get\('([A-Za-z]+)'\)/g)].map(m => m[1]));
  const href = employeeWorkspaceHref(1, { edit: true, section: 'overview', returnTo: '/people?page=2' });
  for (const key of new URL(href, ORIGIN).searchParams.keys()) expect(read.has(key), key).toBe(true);
});

// ---- readiness "Open section" fix links ------------------------------------------------------

const CATEGORIES = ['personal', 'org', 'contract', 'identity', 'payroll', 'document', 'compliance', 'pay', 'unknown-category'];
const FIXES = [undefined, { kind: 'field', target: 'salary' }, { kind: 'field', target: 'dateOfBirth' }, { kind: 'document', documentType: 'Contract' }, { kind: 'policy' }] as const;

test('every readiness "Open section" target resolves, names a known section, and keeps returnTo', () => {
  const returnTo = '/people?readiness=Blocked&page=3&anchor=employee-12';
  const bad: string[] = [];
  for (const category of CATEGORIES) {
    for (const fix of FIXES) {
      const item = { category, fix } as Parameters<typeof readinessFixHref>[1];
      const href = readinessFixHref(12, item, returnTo);
      const url = new URL(href, ORIGIN);
      if (!routeResolves(href)) bad.push(`unresolved ${category}/${JSON.stringify(fix)} -> ${href}`);
      if (!knownSectionsInPage().has(url.searchParams.get('section') ?? '')) bad.push(`unknown section ${href}`);
      if (url.searchParams.get('returnTo') !== returnTo) bad.push(`returnTo lost ${href}`);
      // a field fix opens the editor (needs employees.write); everything else stays on the read-only route
      const target = readinessFixTarget(item);
      if (target.edit !== url.pathname.endsWith('/edit')) bad.push(`edit flag/path disagree ${href}`);
    }
  }
  expect(bad).toEqual([]);
});

/** The panels EmployeePayBenefitsSection renders; any other panel shows "This panel is not available". */
function payBenefitsKnownPanels(): Set<string> {
  const m = src('src/views/employees/EmployeePayBenefitsSection.tsx').match(/const knownPanels = new Set\(\[([^\]]*)\]\)/);
  return new Set([...(m?.[1] ?? '').matchAll(/'([^']+)'/g)].map(x => x[1]));
}

test('the pay-benefits panel reader sees payroll, benefits, package, deductions and salary (guards the parser itself)', () => {
  expect([...payBenefitsKnownPanels()].sort()).toEqual(['benefits', 'deductions', 'package', 'payroll', 'salary']);
});

test('the Edit pay details button opens a panel the view route understands', () => {
  // EmployeePayBenefitsSection: edit link uses panel || 'payroll'.
  const href = employeeWorkspaceHref(5, { edit: true, section: 'pay-benefits', panel: 'payroll', returnTo: '/people' });
  expect(payBenefitsKnownPanels().has(new URL(href, ORIGIN).searchParams.get('panel')!)).toBe(true);
});

test('a missing-salary readiness fix lands, after the save, on a panel the view route renders', () => {
  // readinessFixTarget('payroll', field salary) -> { section: 'pay-benefits', panel: 'salary', edit: true }: the editor opens on
  // the salary step. After Save (and from the editor's cancel link) the VIEW route gets the same panel, so the pay-benefits
  // section must render `salary` (the pay overview with the salary lines) rather than "This panel is not available".
  // The Payroll "cash allowance issues" link (CashAllowanceIssues.tsx) opens the same view panel.
  const section = src('src/views/employees/EmployeePayBenefitsSection.tsx');
  expect(section).toMatch(/if \(panel && panel !== 'payroll' && panel !== 'salary'\) return/); // salary is not the "not available for your access" fallthrough
  const target = readinessFixTarget({ category: 'payroll', fix: { kind: 'field', target: 'salary' } } as Parameters<typeof readinessFixTarget>[0]);
  const viewAfterSave = employeeWorkspaceHref(5, { section: target.section, panel: target.panel, returnTo: '/people' });
  expect(payBenefitsKnownPanels().has(new URL(viewAfterSave, ORIGIN).searchParams.get('panel')!)).toBe(true);
});

// ---- legacy ?employeeId= redirects -----------------------------------------------------------

const LEGACY_TABS = ['personal', 'employment', 'payroll', 'benefits', 'package', 'deductions', 'compliance', 'documents', 'history', 'transfers', 'development'];

test('every legacy ?employeeId=&tab= redirect lands on an existing route, a known section, and a panel the destination handles', () => {
  const bad: string[] = [];
  for (const tab of LEGACY_TABS) {
    const href = legacyEmployeeTarget(new URLSearchParams(`employeeId=901&tab=${tab}`));
    if (!href) { bad.push(`${tab}: null`); continue; }
    const url = new URL(href, ORIGIN);
    if (!routeResolves(href)) bad.push(`${tab}: unresolved ${href}`);
    const section = url.searchParams.get('section') ?? '';
    if (!knownSectionsInPage().has(section)) bad.push(`${tab}: unknown section ${section}`);
    // pay-benefits is the one destination that rejects unknown panels with a dead-end message
    if (section === 'pay-benefits' && !payBenefitsKnownPanels().has(url.searchParams.get('panel') ?? '')) bad.push(`${tab}: panel not rendered by pay-benefits`);
  }
  expect(bad).toEqual([]);
});

test('the legacy redirect carries the whole directory state to the destination as returnTo, and Back restores it exactly', () => {
  const legacy = new URLSearchParams('employeeId=901&tab=benefits&search=Al%20Qahtani&status=Active&readiness=NotReady&access=code_given&view=ex&page=7&anchor=employee-901');
  const href = legacyEmployeeTarget(legacy)!;
  const back = validatePeopleReturnTo(new URL(href, ORIGIN).searchParams.get('returnTo'), ORIGIN);
  expect(parsePeopleDirectoryState(new URL(back, ORIGIN).searchParams)).toEqual({
    search: 'Al Qahtani', status: 'Active', readiness: 'NotReady', access: 'code_given', view: 'ex', page: 7, anchor: 'employee-901',
  });
});

test('legacy params that cannot name an employee never redirect (they would loop or 404)', () => {
  for (const q of ['employeeId=', 'employeeId=abc', 'employeeId=-3', 'employeeId=0', 'employeeId=007', 'employeeId=1e3', 'employeeId=9007199254740993', 'employeeId=new', 'tab=personal', 'employeeId=%20']) {
    expect(legacyEmployeeTarget(new URLSearchParams(q)), q).toBeNull();
  }
});

test('the redirect target never points back at /people?employeeId= (no redirect loop)', () => {
  for (const tab of [...LEGACY_TABS, undefined, 'bogus']) {
    const href = legacyEmployeeTarget(new URLSearchParams(`employeeId=5${tab ? `&tab=${tab}` : ''}&page=2`))!;
    expect(href.startsWith('/people/5')).toBe(true);
    expect(new URL(href, ORIGIN).searchParams.has('employeeId')).toBe(false);
  }
});

test('/people (the page that owns legacy redirects) replaces, not pushes, so Back does not bounce through the legacy URL', () => {
  const page = src('app/(dashboard)/people/page.tsx');
  expect(page).toContain('router.replace(legacyTarget)');
  expect(page).not.toContain('router.push(legacyTarget)');
});

// =============================================================================================
// 4. returnTo round trip: directory row -> workspace -> Back
// =============================================================================================

const ROUND_TRIP_STATES: Array<[string, PeopleDirectoryState]> = [
  ['empty', {}],
  ['search with ampersand, hash, plus and percent', { search: 'R&D #1 + 100%' }],
  ['Arabic search', { search: 'محمد العتيبي' }],
  ['comma separated access filter', { access: 'not_started,code_given' }],
  ['every filter, ex view, deep page', { search: 'Alex', status: 'On Leave', readiness: 'NotReady', access: 'blocked', importBatchId: '8c740ad8-e69d-4af3-b09f-36cbe15eb995', gapType: 'link:manager', view: 'ex', page: 12 }],
  ['page 1 explicit', { page: 1 }],
  ['value with a slash and a question mark', { search: 'a/b?c=d' }],
];

for (const [name, state] of ROUND_TRIP_STATES) {
  test(`row link -> workspace -> Back restores the directory exactly: ${name}`, () => {
    const id = 4021;
    // EmployeesPage row: returnTo = buildPeopleReturnTo({ ...state, anchor: `employee-${id}` })
    const rowHref = employeeWorkspaceHref(id, { returnTo: buildPeopleReturnTo({ ...state, anchor: `employee-${id}` }) });
    expect(routeResolves(rowHref)).toBe(true);
    const url = new URL(rowHref, ORIGIN);
    expect(url.pathname).toBe(`/people/${id}`);
    // EmployeeWorkspacePage: validatePeopleReturnTo(searchParams.get('returnTo')) is the Back href.
    const back = validatePeopleReturnTo(url.searchParams.get('returnTo'), ORIGIN);
    expect(routeResolves(back)).toBe(true);
    expect(parsePeopleDirectoryState(new URL(back, ORIGIN).searchParams)).toEqual({ ...state, anchor: `employee-${id}` });
    // every in-workspace link (section tabs, edit, activity) keeps the same returnTo
    for (const section of SECTIONS) {
      const inner = new URL(employeeWorkspaceHref(id, { section, returnTo: back }), ORIGIN);
      expect(inner.searchParams.get('returnTo'), section).toBe(back);
    }
  });
}

test('the anchor a row link puts in returnTo is the id the directory focuses on return', () => {
  const page = src('src/views/EmployeesPage.tsx');
  expect(page).toContain('id={`employee-${employee.id}`}');
  expect(page).toContain('anchor: `employee-${employee.id}`');
  expect(page).toContain('document.getElementById(anchor)?.focus(');
});

test('serialize(parse(x)) is stable for every filter/page combination (no drift across Back/forward)', () => {
  for (const [, state] of ROUND_TRIP_STATES) {
    const once = serializePeopleDirectoryState(parsePeopleDirectoryState(serializePeopleDirectoryState(state)));
    expect(once.toString()).toBe(serializePeopleDirectoryState(state).toString());
  }
});

test('hostile returnTo values fall back to /people from every entry point', () => {
  for (const raw of ['javascript:alert(1)', 'https://evil.test/people', '//evil.test', '/people#frag', '/people?search=%E0%A4%A', '/payroll', '/people/901', ' /people', '/people\\evil', null, undefined, '']) {
    expect(validatePeopleReturnTo(raw as string | null | undefined, ORIGIN), String(raw)).toBe('/people');
  }
});

test('filter and pager setters reset the page and the stale row anchor (a filter change never leaves you on page 9 of 2)', () => {
  const page = src('src/views/EmployeesPage.tsx');
  for (const setter of ['setSearch', 'setStatus', 'setReadinessFilter', 'setGapTypeFilter', 'setImportBatchFilter', 'setAccessFilter', 'setView']) {
    const body = page.slice(page.indexOf(`const ${setter} = useCallback`), page.indexOf('}, [directoryQuery]);', page.indexOf(`const ${setter} = useCallback`)));
    expect(body, setter).toContain('page: undefined');
    expect(body, setter).toContain('anchor: undefined');
  }
  const pager = page.slice(page.indexOf('const setPage = useCallback'), page.indexOf('const surfaceAdvisoryWarning'));
  expect(pager).toContain('anchor: undefined');
  expect(pager).toContain("{ history: 'push' }"); // Back steps through pages
});

const WORKLIST_ROUTES = ['/people/new-hires', '/people/onboarding-review', '/people/onboarding-tasks'];

test('a workspace opened from New Hires / Profile review / Onboarding Tasks can go Back to that list', () => {
  // The three worklist pages keep their filters in component state (not the URL), so the return target is the bare route.
  for (const route of WORKLIST_ROUTES) {
    expect(validatePeopleReturnTo(route, ORIGIN), route).toBe(route);
    expect(routeResolves(route), route).toBe(true);
    const href = employeeWorkspaceHref(7, { section: 'overview', returnTo: route });
    expect(new URL(href, ORIGIN).searchParams.get('returnTo'), href).toBe(route);
  }
  // Any query on a worklist route is dropped (the pages read none), so nothing attacker-chosen rides along.
  expect(validatePeopleReturnTo('/people/onboarding-tasks?status=overdue&x=1', ORIGIN)).toBe('/people/onboarding-tasks');
  expect(employeeWorkspaceHref(7, { section: 'overview', returnTo: '/people/new-hires?page=2' })).toBe('/people/7?section=overview&returnTo=%2Fpeople%2Fnew-hires');
});

test('the returnTo allow-list is exactly /people plus the three worklist routes (no open redirect)', () => {
  const hostile = [
    '//evil.test', '//evil.test/people/new-hires', '///evil.test', '\\\\evil.test', '/\\evil.test', '/people/new-hires\\..\\..\\evil',
    'javascript:alert(1)', ' javascript:alert(1)', 'JaVaScRiPt:alert(1)', 'data:text/html,x', 'https://evil.test/people/new-hires', 'http://localhost/people/new-hires@evil.test',
    '/people/new-hires/', '/people/new-hires/extra', '/people/new-hires/../../evil', '/people/onboarding-tasks/..%2F..%2Fevil', '/people/new-hires%2F..%2Fevil',
    '/people%2Fnew-hires', '%2Fpeople%2Fnew-hires', '%2F%2Fevil.test', '/people/new-hires#frag', '/people/onboarding-review#x', ' /people/new-hires', '/people/new-hires ',
    '/people/new-hires\n//evil', '/people/new-hires\t', '/people/onboarding-tasks%', '/people/onboarding-tasks?q=%E0%A4%A', '/people/new-hire', '/People/new-hires',
    '/people/123', '/people/new', '/people/new-hires.evil.test', '/payroll', '/dashboard',
  ];
  for (const raw of hostile) expect(validatePeopleReturnTo(raw, ORIGIN), JSON.stringify(raw)).toBe('/people');
  // A different host never passes, even with an allow-listed path; the same host yields the path only.
  expect(validatePeopleReturnTo('https://evil.test/people/new-hires', 'https://app.test')).toBe('/people');
  expect(validatePeopleReturnTo('https://app.test/people/new-hires', 'https://app.test')).toBe('/people/new-hires');
  // employeeWorkspaceHref uses the same allow-list: a hostile returnTo is omitted, not emitted.
  for (const raw of hostile) expect(employeeWorkspaceHref(7, { section: 'overview', returnTo: raw }), JSON.stringify(raw)).toBe('/people/7?section=overview');
});

test('New Hires, Profile review and Onboarding Tasks pass their own route as returnTo when opening an employee', () => {
  const cases: Array<[string, string, number]> = [
    ['src/views/NewHiresPage.tsx', "'/people/new-hires'", 2],
    ['src/views/people/CompletionReviewPage.tsx', "'/people/onboarding-review'", 1],
    ['src/views/people/OnboardingWorklistPage.tsx', "'/people/onboarding-tasks'", 2],
  ];
  for (const [file, route, links] of cases) {
    const text = stripComments(src(file));
    expect(text, file).toContain(route);
    const linkCalls = [...text.matchAll(/employeeWorkspaceHref\([^;\n]*\)/g)].map(m => m[0]);
    expect(linkCalls.length, file).toBeGreaterThanOrEqual(links);
    for (const call of linkCalls) expect(call, `${file}: ${call}`).toContain('returnTo');
  }
});

// =============================================================================================
// 5. Dead ends: handlers, anchors, authenticated downloads
// =============================================================================================

test('every <button> in the People module has a handler, is a submit, or is intentionally disabled', () => {
  const offenders: string[] = [];
  for (const file of MODULE_SOURCES.filter(f => f.endsWith('.tsx'))) {
    const text = stripComments(src(file));
    for (const m of text.matchAll(/<button\b/g)) {
      let i = m.index! + 7; let depth = 0; let quote: string | null = null;
      for (; i < text.length; i++) {
        const c = text[i];
        if (quote) { if (c === quote && text[i - 1] !== '\\') quote = null; continue; }
        if (depth > 0 && (c === '"' || c === "'" || c === '`')) { quote = c; continue; }
        if (c === '{') depth++; else if (c === '}') depth--; else if (c === '>' && depth === 0) break;
      }
      const tag = text.slice(m.index, i + 1);
      const ok = /onClick\s*=|type\s*=\s*["']submit["']|type=\{|\{\.\.\.|\sdisabled(\s|>|=\s*["']?true)/.test(tag);
      if (!ok) offenders.push(`${file}:${text.slice(0, m.index).split('\n').length} ${tag.replace(/\s+/g, ' ').slice(0, 120)}`);
    }
  }
  expect(offenders).toEqual([]);
});

test('no link in the People module is an empty, "#" or javascript: href', () => {
  const offenders: string[] = [];
  for (const file of MODULE_SOURCES.filter(f => f.endsWith('.tsx'))) {
    const text = stripComments(src(file));
    for (const m of text.matchAll(/href=(?:""|"#"|"javascript:[^"]*"|\{undefined\}|\{null\}|\{''\}|\{'#'\})/g)) {
      offenders.push(`${file}:${text.slice(0, m.index).split('\n').length} ${m[0]}`);
    }
  }
  expect(offenders).toEqual([]);
});

test('in-page "#" anchors point at an id the page renders', () => {
  const create = src('src/views/EmployeesPage.tsx');
  const gate = src('src/lib/employeeCreateGate.ts');
  const id = gate.match(/MISSING_COUNTRY_NOTICE_ID = '([^']+)'/)![1];
  expect(create).toContain('href={`#${MISSING_COUNTRY_NOTICE_ID}`}');
  expect(create).toContain('id={MISSING_COUNTRY_NOTICE_ID}');
  expect(id).toBeTruthy();
});

test('Compliance centre "Fix" links from the backend never carry a People filter key the list ignores', () => {
  const svc = fs.readFileSync(path.resolve(FRONTEND, '..', 'backend-dotnet', 'Zayra.Api', 'Infrastructure', 'Compliance', 'SaudiComplianceDashboardService.cs'), 'utf8');
  const routes = [...svc.matchAll(/"(\/people[^"]*)"/g)].map(m => m[1]);
  expect(routes.length).toBeGreaterThanOrEqual(3);
  for (const route of routes) {
    const resolved = resolveComplianceRoute(route);
    expect(routeResolves(resolved), resolved).toBe(true);
    for (const key of new URL(resolved, ORIGIN).searchParams.keys()) expect(PEOPLE_DIRECTORY_QUERY_KEYS, `${route} -> ${key}`).toContain(key);
  }
});

test('precondition: the API authenticates by Authorization header only, so a plain <a href="/api/..."> cannot download', () => {
  const client = src('src/api/client.ts');
  expect(client).toContain("localStorage.getItem('zayra_access_token')");
  expect(client).toContain('config.headers.Authorization = `Bearer ${token}`');
  const program = fs.readFileSync(path.resolve(FRONTEND, '..', 'backend-dotnet', 'Zayra.Api', 'Program.cs'), 'utf8');
  // No cookie or ?access_token= fallback: JwtBearerEvents only has OnTokenValidated.
  expect(program).not.toContain('OnMessageReceived');
  expect(program).not.toMatch(/access_token/);
});

test('Profile review "Download" fetches the file through the authenticated client instead of a plain anchor to a bearer-protected /api URL', () => {
  // CompletionReviewPage renders <a href={hrCompletionApi.documentUrl(id)}> = /api/hr/profile-completion/documents/<id>/download.
  // A browser navigation sends no Authorization header (token lives in localStorage; see the precondition test above),
  // and the API has no cookie/query-token fallback, so HR gets a 401 JSON page instead of the file.
  // Correct behaviour: fetch the file through the api client as a blob (as api/esign.ts download does) and save it.
  const page = src('src/views/people/CompletionReviewPage.tsx');
  expect(page).not.toMatch(/<a\s[^>]*href=\{hrCompletionApi\.documentUrl/);
  expect(page).not.toMatch(/<a\s[^>]*href=\{?["'`]?\/api\//);
  expect(page).toContain('downloadAuthenticated(');
  expect(page).toContain('hrCompletionApi.downloadDocument(');
});

test('the employee workspace offers a keyboard-reachable Download for each document, through the authenticated client', () => {
  // EmployeeComplianceDocumentsSection renders each document as name + approval + scan label only. The backend serves
  // GET /api/employees/documents/{id}/download (esign.ts already uses it as a blob), but no People screen calls it.
  // Correct behaviour: each document with a downloadable scan state has an Open/Download action.
  // \bDownload\b does not match the identifier `canDownloadScanned` that is already imported there.
  const section = stripComments(src('src/views/employees/EmployeeComplianceDocumentsSection.tsx'));
  expect(section).toMatch(/\b(Download|Open file|View file)\b/);
  expect(section).toContain('downloadAuthenticated(');
  expect(section).toContain('employeesApi.downloadDocument(');
  expect(section).toContain('documentDownloadState(');
  expect(section).not.toMatch(/<a\s[^>]*href=\{?["'`]?\/api\//);
  expect(section).toMatch(/<button\s[^>]*type="button"[^>]*data-testid="doc-download"/);
});

test('internal People navigation uses next/link, not plain <a href> (no full page reload)', () => {
  // The directory row link used to be <a href>, so every row open was a hard navigation (SPA state, auth bootstrap and the
  // unsaved-change guard thrown away). It is now a <Link> that keeps the id (return anchor), classes and href.
  const offenders: string[] = [];
  for (const file of ['src/views/EmployeesPage.tsx', 'src/views/employees/hire/HireWizard.tsx', 'src/views/employees/hire/steps/SuccessStep.tsx', 'src/views/NewHiresPage.tsx', 'src/views/people/CompletionReviewPage.tsx', 'src/views/people/OnboardingWorklistPage.tsx']) {
    const text = stripComments(src(file));
    for (const m of text.matchAll(/<a\b[^>]*?href=(?:\{employeeWorkspaceHref|"\/people|\{`\/people)/g)) {
      // The one allowed exception: "Add another" / "Start a new employee" to /people/new. HireWizard keeps all its state in one
      // mounted component and reads ?draft= once on mount, so a client-side <Link> to the same route would keep the finished /
      // failed wizard on screen; the hard navigation is what resets it.
      if (/^href="\/people\/new"/.test(text.slice(m.index! + m[0].length - 'href="/people'.length, m.index! + m[0].length + 6)) && file.includes('/hire/')) continue;
      offenders.push(`${file}:${text.slice(0, m.index).split('\n').length}`);
    }
  }
  expect(offenders).toEqual([]);
  const page = stripComments(src('src/views/EmployeesPage.tsx'));
  expect(page).toMatch(/<Link\s+id=\{`employee-\$\{employee\.id\}`\}\s+href=\{employeeWorkspaceHref\(employee\.id,/);
});

test('the legacy create path carries the directory state (filters, page, anchor) through to Cancel, draft save, merge and view-existing', () => {
  // Add Employee opens /people/new with the directory state in the query; every exit from the create route builds its
  // target from that state with buildPeopleReturnTo(directoryQuery.state) instead of a hard-coded /people.
  const page = stripComments(src('src/views/EmployeesPage.tsx'));
  expect(page).not.toContain('returnTo=%2Fpeople');
  expect(page).not.toMatch(/navigation\.push\('\/people'\)/);
  expect(page).not.toMatch(/navigation\.push\('\/people\/new'\)/);
  expect(page).not.toMatch(/navigation\.push\(`\/people\/\$\{[^}]+\}\/edit\?/);
  expect(page).toContain('buildPeopleCreateHref(directoryQuery.state)');
  const createExits = [...page.matchAll(/navigation\.push\(([^;\n]*employeeWorkspaceHref\([^;\n]*)\);/g)].map(m => m[1]);
  expect(createExits.length).toBeGreaterThanOrEqual(5);
  for (const call of createExits) {
    if (call.includes('returnTo: buildPeopleReturnTo({ ...directoryQuery.state, anchor')) continue; // the row's own open
    expect(call, call).toContain('returnTo: buildPeopleReturnTo(directoryQuery.state)');
  }
  expect(page).toMatch(/next: \{ label: t\('Back to the People list'\), href: buildPeopleReturnTo\(directoryQuery\.state\) \}/);
});

test('buildPeopleCreateHref puts the directory state on /people/new and Cancel round-trips it', () => {
  expect(buildPeopleCreateHref({})).toBe('/people/new');
  const state: PeopleDirectoryState = { search: 'Al Qahtani & Sons', status: 'Active', page: 3, view: 'ex', anchor: 'employee-9' };
  const href = buildPeopleCreateHref(state);
  const url = new URL(href, ORIGIN);
  expect(url.pathname).toBe('/people/new');
  // On /people/new the page reads the same query keys, and the exit target it builds is the list the user came from.
  expect(buildPeopleReturnTo(parsePeopleDirectoryState(url.searchParams))).toBe(buildPeopleReturnTo(state));
  // The created record's workspace links back to that list too.
  const edit = employeeWorkspaceHref(9, { edit: true, section: 'personal', returnTo: buildPeopleReturnTo(parsePeopleDirectoryState(url.searchParams)) });
  expect(new URL(edit, ORIGIN).searchParams.get('returnTo')).toBe(buildPeopleReturnTo(state));
});
