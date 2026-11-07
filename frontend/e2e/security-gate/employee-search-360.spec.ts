/**
 * "360" EMPLOYEE SEARCH — the sensitive-field gate, against the real API with each role's real token
 * (GET /api/employees?search=).
 *
 *   Group HR Director (employees.sensitive): finds a person by lower-case name, upper-case job title, personal email, the exact
 *     ID/Iqama number and (when the seed gave them one) their phone in local and international form; a partial ID finds nobody.
 *   Auditor and HR Officer (employees.read, NO employees.sensitive): find the same person by name, but never by personal email,
 *     phone or ID number, so search cannot confirm a value the caller may not see.
 */
import { test, expect, request as pwRequest, type APIRequestContext } from '@playwright/test';
import fs from 'node:fs';
import { BASE_URL, roleByKey, tokenPath } from './roles';
import { stampActor } from '../identity/actor';

interface Detail { id: number; employeeCode: string; fullName: string; designation?: string; phone?: string; idNumber?: string; personalEmail?: string; workEmail?: string }

const tokenFor = (key: string): string => JSON.parse(fs.readFileSync(tokenPath(key), 'utf8')).token;

async function apiAs(key: string): Promise<APIRequestContext> {
  const role = roleByKey(key);
  stampActor({ email: role.email, tenantSlug: role.tenantSlug, via: `security-gate role '${key}'` });
  return pwRequest.newContext({ baseURL: BASE_URL, timeout: 30_000, extraHTTPHeaders: { Authorization: `Bearer ${tokenFor(key)}` } });
}

const found = async (api: APIRequestContext, term: string): Promise<number[]> => {
  const r = await api.get(`/api/employees?search=${encodeURIComponent(term)}&pageSize=100`);
  expect(r.status(), `search "${term}"`).toBe(200);
  return ((await r.json()) as { items: Array<{ id: number }> }).items.map((i) => i.id);
};

/** A stored phone in its local ("05…") and international ("+966 5…") forms. */
function phoneForms(stored: string): { local: string; international: string } {
  let digits = stored.replace(/\D/g, '');
  if (digits.startsWith('00')) digits = digits.slice(2);
  if (digits.startsWith('966') && digits.length >= 11) digits = digits.slice(3);
  digits = digits.replace(/^0+/, '');
  return { local: `0${digits}`, international: `+966 ${digits}` };
}

test.describe('Employee 360 search — sensitive fields are searchable only with employees.sensitive', () => {
  let hr: APIRequestContext;
  let person: Detail;

  test.beforeAll(async () => {
    hr = await apiAs('group-hr');
    const list = await (await hr.get('/api/employees?status=Active&pageSize=100')).json() as { items: Array<{ id: number }> };
    for (const { id } of list.items) {
      const d = await (await hr.get(`/api/employees/${id}`)).json() as Detail;
      const ownEmail = d.personalEmail && d.personalEmail.toLowerCase() !== (d.workEmail ?? '').toLowerCase();
      if (d.idNumber && d.idNumber.length >= 8 && d.designation && ownEmail) { person = d; break; }
    }
    expect(person, 'a seeded employee with an ID number, a job title and a personal email of their own').toBeTruthy();
  });

  test.afterAll(async () => { await hr?.dispose(); });

  /** The values only employees.sensitive may search by: personal email, exact ID, and the phone when there is one. */
  const sensitiveTerms = (): string[] => {
    const terms = [person.personalEmail!, person.idNumber!];
    if (person.phone && person.phone.replace(/\D/g, '').length >= 8) { const p = phoneForms(person.phone); terms.push(p.local, p.international); }
    return terms;
  };

  test('HR with employees.sensitive finds the person by name, job title, personal email, phone and the exact ID number', async () => {
    for (const term of [person.fullName.toLowerCase(), person.designation!.toUpperCase(), ...sensitiveTerms()]) {
      expect(await found(hr, term), `"${term}" should find ${person.employeeCode}`).toContain(person.id);
    }
    expect(await found(hr, person.idNumber!.slice(0, -1)), 'a partial ID number is not a match').not.toContain(person.id);
  });

  for (const key of ['auditor', 'hr-officer']) {
    test(`${key} (no employees.sensitive) finds the person by name, never by personal email, phone or ID number`, async () => {
      const api = await apiAs(key);
      try {
        expect(await found(api, person.fullName.toLowerCase())).toContain(person.id);
        for (const term of sensitiveTerms()) {
          expect(await found(api, term), `"${term}" must not reveal ${person.employeeCode} to ${key}`).not.toContain(person.id);
        }
      } finally {
        await api.dispose();
      }
    });
  }
});
