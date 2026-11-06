import { expect, test } from '@playwright/test';
import { renderComponent } from './support/ssr';

/** A disabled control says why, in text that is always visible (not a hover popover). */

test('WriteBlockedNote: says why while the panel is not ready, and renders nothing once it is', () => {
  const blocked = renderComponent('src/components/ui/WriteBlockedNote.tsx', 'WriteBlockedNote', { id: 'gl-rates-blocked', ready: false });
  expect(blocked).toContain('id="gl-rates-blocked"');
  expect(blocked).toContain('Changes are turned off until this has loaded.');
  expect(blocked).toContain('data-write-blocked');
  expect(renderComponent('src/components/ui/WriteBlockedNote.tsx', 'WriteBlockedNote', { id: 'x', ready: true })).toBe('');
});

test('PlanLimitNote: the plan-limit reason is plain text with the counts, for employees and for users', () => {
  const employees = renderComponent('src/components/ui/PlanLimitNote.tsx', 'PlanLimitNote', { id: 'employee-limit-reason', kind: 'employees', used: 10, max: 10 });
  expect(employees).toContain('id="employee-limit-reason"');
  expect(employees).toContain('Employee limit reached (10/10). Upgrade your plan to add more employees.');
  const users = renderComponent('src/components/ui/PlanLimitNote.tsx', 'PlanLimitNote', { id: 'user-limit-reason', kind: 'users', used: 5, max: 5 });
  expect(users).toContain('User limit reached (5/5). Upgrade your plan to add more users.');
  expect(`${employees}${users}`).not.toContain('hidden group-hover');
});

test('the GL panels point their disabled write controls at the reason', async () => {
  const { readFileSync } = await import('node:fs');
  const { join } = await import('node:path');
  for (const [file, id] of [['CompanyRatesPanel', 'gl-rates-blocked'], ['DriverManagerPanel', 'gl-drivers-blocked'], ['AccountsMappingsPanel', 'gl-accounts-blocked']]) {
    const source = readFileSync(join(__dirname, '..', 'src', 'components', 'gl', `${file}.tsx`), 'utf8');
    expect(source, `${file} renders the note`).toContain(`<WriteBlockedNote id="${id}"`);
    expect(source, `${file} ties the buttons to it`).toContain(`aria-describedby={res.ready ? undefined : '${id}'}`);
  }
});

test('hover-only limit popovers are gone from Employees and Users', async () => {
  const { readFileSync } = await import('node:fs');
  const { join } = await import('node:path');
  for (const file of ['EmployeesPage', 'UserManagementPage']) {
    const source = readFileSync(join(__dirname, '..', 'src', 'views', `${file}.tsx`), 'utf8');
    expect(source, file).not.toMatch(/limit reached[\s\S]{0,80}Upgrade your plan/);
    expect(source, file).toContain('<PlanLimitNote');
  }
});
