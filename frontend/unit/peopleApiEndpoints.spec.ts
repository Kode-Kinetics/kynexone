import { expect, test } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';

/**
 * Link check for the API side of the People module: every `client.<verb>('/api/...')` the module's
 * api files and screens issue must match a route that a backend controller actually declares, for
 * the same HTTP verb. A path or verb that drifts is a button that silently 404s/405s.
 *
 * Reads the controllers' [Route]/[HttpX] attributes as text (same approach as hireReadinessKeys.spec.ts).
 */
const FRONTEND = path.resolve(__dirname, '..');
const CONTROLLERS = path.resolve(FRONTEND, '..', 'backend-dotnet', 'Zayra.Api', 'Controllers');

function walk(dir: string, out: string[] = []): string[] {
  for (const name of fs.readdirSync(dir)) {
    const full = path.join(dir, name);
    if (fs.statSync(full).isDirectory()) walk(full, out);
    else if (full.endsWith('.cs')) out.push(full);
  }
  return out;
}

/** `{id:int}`, `{docId:guid}`, `{x?}` -> `{}`; leading/trailing slashes trimmed; lower-cased. */
function normalise(route: string): string {
  return route
    .replace(/\$\{[^}]*\}/g, '{}')
    .replace(/\{[^}]*\}/g, '{}')
    .replace(/\?.*$/, '')
    .replace(/^\/+|\/+$/g, '')
    .toLowerCase();
}

interface BackendRoute { verb: string; route: string; file: string }

function backendRoutes(): BackendRoute[] {
  const routes: BackendRoute[] = [];
  for (const file of walk(CONTROLLERS)) {
    const text = fs.readFileSync(file, 'utf8');
    // A file may hold several controllers; walk attribute lines in order and track the active class route.
    let classRoute = '';
    for (const line of text.split('\n')) {
      const cls = line.match(/^\s*\[Route\("([^"]*)"\)\]\s*$/);
      if (cls) { classRoute = cls[1]; continue; }
      const m = line.match(/^\s*\[Http(Get|Post|Put|Patch|Delete)(?:\("([^"]*)"\))?[,\]]/);
      if (!m) continue;
      const sub = m[2] ?? '';
      // A method route that starts with "/" or "api/" is absolute (ASP.NET ignores the class route).
      const full = sub.startsWith('/') || sub.startsWith('api/') ? sub : [classRoute, sub].filter(Boolean).join('/');
      routes.push({ verb: m[1].toLowerCase(), route: normalise(full), file: path.relative(FRONTEND, file) });
    }
  }
  return routes;
}

interface FrontendCall { verb: string; route: string; file: string; line: number }

function frontendCalls(files: string[]): FrontendCall[] {
  const calls: FrontendCall[] = [];
  for (const rel of files) {
    const text = fs.readFileSync(path.join(FRONTEND, rel), 'utf8');
    const re = /client\s*\.\s*(get|post|put|patch|delete)\b[\s\S]{0,400}?\(\s*([`'"])(\/api\/[^`'"]+)\2/g;
    for (const m of text.matchAll(re)) {
      // The lazy window could have skipped past a call without a literal path; make sure no other
      // `client.<verb>` starts between the match start and the path literal.
      const head = m[0].slice(0, m[0].lastIndexOf(m[3]));
      if ((head.match(/client\s*\.\s*(get|post|put|patch|delete)\b/g) ?? []).length > 1) continue;
      // `${decision}` is the only literal-segment placeholder: it is 'approve' | 'reject', two real routes.
      const variants = m[3].includes('${decision}') ? ['approve', 'reject'].map(d => m[3].replace('${decision}', d)) : [m[3]];
      for (const variant of variants) {
        calls.push({ verb: m[1], route: normalise(variant), file: rel, line: text.slice(0, m.index).split('\n').length });
      }
    }
  }
  return calls;
}

// Every api module and screen that the People directory, workspace, hire wizard, New Hires,
// Profile review and Onboarding Tasks screens call through.
const MODULE_API_FILES = [
  'src/api/employees.ts',
  'src/api/employeeAccess.ts',
  'src/api/employeeCompletion.ts',
  'src/api/employeeDrafts.ts',
  'src/api/employeeHire.ts',
  'src/api/employeeFieldCatalog.ts',
  'src/api/onboardingTemplates.ts',
  'src/api/essCompletion.ts',
  'src/views/EmployeesPage.tsx',
];

test('the backend route reader sees the employee controllers (guards the parser itself)', () => {
  const routes = backendRoutes();
  expect(routes.length).toBeGreaterThan(300);
  expect(routes).toContainEqual(expect.objectContaining({ verb: 'get', route: 'api/employees/{}/readiness' }));
  expect(routes).toContainEqual(expect.objectContaining({ verb: 'post', route: 'api/employees/bulk' }));
  // Controllers that declare an absolute "api/..." method route are joined correctly.
  expect(routes).toContainEqual(expect.objectContaining({ verb: 'get', route: 'api/reference/nationalities' }));
});

test('the frontend call reader finds the People api surface (guards the parser itself)', () => {
  const calls = frontendCalls(MODULE_API_FILES);
  expect(calls.length).toBeGreaterThan(60);
  expect(calls).toContainEqual(expect.objectContaining({ verb: 'get', route: 'api/employees/export' }));
  expect(calls).toContainEqual(expect.objectContaining({ verb: 'get', route: 'api/employees/import-template' }));
  expect(calls).toContainEqual(expect.objectContaining({ verb: 'post', route: 'api/employees/bulk-activate' }));
});

test('every API call the People module makes matches a backend route for the same verb', () => {
  const backend = backendRoutes();
  const missing = frontendCalls(MODULE_API_FILES)
    .filter(call => !backend.some(route => route.verb === call.verb && route.route === call.route))
    .map(call => `${call.verb.toUpperCase()} /${call.route}  (${call.file}:${call.line})`);
  expect(missing, 'People module calls an endpoint no controller declares').toEqual([]);
});

test('directory toolbar actions (export, template, import, preview, bulk) have live endpoints', () => {
  const backend = backendRoutes();
  const has = (verb: string, route: string) => backend.some(r => r.verb === verb && r.route === route);
  expect(has('get', 'api/employees/export')).toBe(true);
  expect(has('get', 'api/employees/import-template')).toBe(true);
  expect(has('post', 'api/employees/import')).toBe(true);
  expect(has('post', 'api/employees/import-preview')).toBe(true);
  expect(has('post', 'api/employees/bulk')).toBe(true);
  expect(has('post', 'api/employees/bulk-activate')).toBe(true);
  expect(has('get', 'api/employees/ex-employees')).toBe(true);
  expect(has('get', 'api/employee-access/summary')).toBe(true);
  expect(has('post', 'api/employee-access/codes')).toBe(true);
});
