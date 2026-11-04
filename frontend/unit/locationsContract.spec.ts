import { expect, test } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import { ORG_STRUCTURE_WRITE_ROLES } from '../src/components/setup/OrgRowActions';

/**
 * Setup → Locations against the real backend source: the routes the client calls, the body it sends, the row it reads,
 * and the role / permission each call needs. The e2e suite mocks the API, so it cannot notice either side drifting.
 */
const root = process.cwd();
const backend = (...p: string[]) => fs.readFileSync(path.resolve(root, '..', 'backend-dotnet', 'Zayra.Api', ...p), 'utf8');
const front = (...p: string[]) => fs.readFileSync(path.resolve(root, 'src', ...p), 'utf8');

const camel = (s: string) => s.charAt(0).toLowerCase() + s.slice(1);
const setupController = backend('Controllers', 'Admin', 'SetupSettingsController.cs');
const locationsController = backend('Controllers', 'LocationsController.cs');
const setupApi = front('api', 'setup.ts');
const setupPage = front('views', 'SetupPage.tsx');

/** The members of `public record <name>(...)`, camel-cased as the JSON serializer writes them. */
const recordFields = (source: string, name: string) => {
  const m = source.match(new RegExp(`public record ${name}\\(([^)]*)\\)`));
  expect(m, `${name} not found`).not.toBeNull();
  return m![1].split(',').map((p) => camel(p.trim().split(/\s+/)[1].replace(/=.*/, '')));
};
const classFields = (source: string, name: string) => {
  const body = source.match(new RegExp(`public class ${name}\\b[^{]*\\{([\\s\\S]*?)\\n\\}`));
  expect(body, `${name} not found`).not.toBeNull();
  return [...body![1].matchAll(/public [\w?<>]+ (\w+) \{ get; set; \}/g)].map((m) => camel(m[1]));
};
const tsKeys = (block: string) => [...block.matchAll(/(\w+)\??:/g)].map((m) => m[1]);

const request = recordFields(setupController, 'LocationRequest');
const entity = classFields(backend('Models', 'SetupAdmin.cs'), 'Location');

test('the routes the Locations client calls exist, with the verb the server expects', () => {
  const route = (verb: string, p: string) => expect(setupController, `${verb} ${p}`).toMatch(new RegExp(`\\[Http${verb}\\("${p.replace(/[{}]/g, '\\$&')}"\\)\\]`));
  route('Get', 'api/admin/locations');
  route('Post', 'api/admin/locations');
  route('Put', 'api/admin/locations/{id:guid}');
  route('Delete', 'api/admin/locations/{id:guid}');
  route('Post', 'api/admin/locations/{id:guid}/restore');
  expect(setupApi).toContain("client.get<Location[]>('/api/admin/locations'");
  expect(setupApi).toContain("client.post<Location>('/api/admin/locations'");
  expect(setupApi).toContain('`/api/admin/locations/${id}`');
  expect(front('api', 'orgRows.ts')).toContain("location: '/api/admin/locations'");

  expect(locationsController).toContain('[Route("api/locations")]');
  for (const [verb, p] of [['Get', 'export'], ['Get', 'import-template'], ['Post', 'import']]) expect(locationsController).toContain(`[Http${verb}("${p}")]`);
  for (const p of ['export', 'import-template', 'import']) expect(setupApi).toContain(`'/api/locations/${p}'`);
});

test('the body the client sends uses only fields the server reads', () => {
  const create = setupApi.match(/locationsApi = \{[\s\S]*?create: \(body: \{([^}]*)\}/);
  expect(create).not.toBeNull();
  const sent = tsKeys(create![1]);
  expect(sent.length).toBeGreaterThan(5);
  for (const key of sent) expect(request, `create body field ${key}`).toContain(key);
  // The form's own fields, which the save posts as they are.
  const form = setupPage.match(/const emptyForm = \(\) => \(\{([^}]*)\}\)/);
  expect(form).not.toBeNull();
  for (const key of tsKeys(form![1])) expect(request, `form field ${key}`).toContain(key);
});

test('the row the client reads has only fields the server returns', () => {
  const iface = setupApi.match(/export interface Location \{([\s\S]*?)\n\}/);
  expect(iface).not.toBeNull();
  for (const key of tsKeys(iface![1])) expect(entity, `Location.${key}`).toContain(key);
});

test('each call needs the role or permission the screen asks for', () => {
  const after = (verb: string, route: string) => setupController.match(new RegExp(`\\[Http${verb}\\("${route.replace(/[{}]/g, '\\$&')}"\\)\\]\\s*\\[HasPermission\\("([\\w.]+)"\\)\\]`))?.[1];
  expect(after('Post', 'api/admin/locations')).toBe('organization.write');
  expect(after('Put', 'api/admin/locations/{id:guid}')).toBe('organization.write');
  expect(after('Delete', 'api/admin/locations/{id:guid}')).toBe('organization.delete');
  expect(after('Post', 'api/admin/locations/{id:guid}/restore')).toBe('organization.delete');
  // Import and export: the controller's roles are the ones the toolbar checks.
  const roles = locationsController.match(/\[Authorize\(Roles = "([^"]+)"\)\]\s*public class LocationsController/)?.[1].split(',');
  expect(roles).toEqual([...ORG_STRUCTURE_WRITE_ROLES]);
});
