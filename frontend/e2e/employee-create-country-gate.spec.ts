import { expect, test } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import {
  COMPANY_COUNTRY_FIX_HREF,
  COMPANY_COUNTRY_FIX_LOCATION,
  MISSING_COUNTRY_NOTICE_ID,
  isEmployeeCreateBlockedByCountry,
  missingCompanyCountryMessage,
} from '../src/lib/employeeCreateGate';

const read = (relative: string) => fs.readFileSync(path.join(process.cwd(), relative), 'utf8');
const employeesPage = () => read('src/views/EmployeesPage.tsx');

/**
 * THE DEFECT (P0, caught on the demo walkthrough): with a company that has no country, the Add Employee
 * modal showed the correct warning — "nothing to require and nothing to save here" — while its Create
 * Employee button stayed ENABLED (`disabled: false`). The presenter clicked it and got a failure instead
 * of the explanation the UI had already computed.
 *
 * Both directions are pinned below. A gate only ever seen to block is as untrustworthy as one only ever
 * seen to pass.
 */
test.describe('Add Employee — company country gate', () => {
  // ── The predicate, both directions ──────────────────────────────────────────────────────────────

  test('blocks the create when the selected company has no country', () => {
    expect(isEmployeeCreateBlockedByCountry(true, '')).toBe(true);
    expect(isEmployeeCreateBlockedByCountry(true, '   ')).toBe(true);
    expect(isEmployeeCreateBlockedByCountry(true, null)).toBe(true);
    expect(isEmployeeCreateBlockedByCountry(true, undefined)).toBe(true);
  });

  test('allows the create as soon as that company has a country', () => {
    expect(isEmployeeCreateBlockedByCountry(true, 'SA')).toBe(false);
    expect(isEmployeeCreateBlockedByCountry(true, 'AE')).toBe(false);
  });

  test('does not block before an employing company has been chosen', () => {
    // "No company chosen yet" has its own (correct) hint. Blocking it would stop a group
    // administrator before they have picked the legal entity at all.
    expect(isEmployeeCreateBlockedByCountry(false, '')).toBe(false);
  });

  test('the message names the company and where the fix lives', () => {
    expect(missingCompanyCountryMessage('Test for Claude'))
      .toBe('Test for Claude has no country set — set it in Setup → Companies before adding employees.');
    expect(missingCompanyCountryMessage('  ')).toContain('This company');
    expect(missingCompanyCountryMessage(null)).toContain(COMPANY_COUNTRY_FIX_LOCATION);
  });

  // ── The button honours the predicate, and says why ──────────────────────────────────────────────

  test('Create Employee is disabled on that same predicate — never on a recomputed one', () => {
    const source = employeesPage();

    // One predicate, read by both the warning and the button: they cannot disagree.
    expect(source).toContain('const formCompanyMissingCountry = isEmployeeCreateBlockedByCountry(');
    expect(source).toContain('disabled={saving || formCompanyMissingCountry}');
    // The duplicate warning's "Create anyway" is the same submit, so it carries the same block.
    expect(source.match(/disabled=\{saving \|\| formCompanyMissingCountry\}/g) ?? []).toHaveLength(2);
    // ...and the request is never fired from the keyboard/programmatic path either.
    expect(source).toContain('if (formCompanyMissingCountry) {\n      setFormError(formCompanyMissingCountryMessage);');
  });

  test('the disabled button explains itself — no bare disabled control', () => {
    const source = employeesPage();

    expect(source).toContain('title={formCompanyMissingCountry ? formCompanyMissingCountryMessage : undefined}');
    expect(source).toContain(`aria-describedby={formCompanyMissingCountry ? MISSING_COUNTRY_NOTICE_ID : undefined}`);
    // aria-describedby must point at something that actually renders in this state.
    expect(source).toContain('id={MISSING_COUNTRY_NOTICE_ID}');
    expect(source).toContain('{formCompanyMissingCountry && (');
    expect(MISSING_COUNTRY_NOTICE_ID).toBe('employee-create-missing-country');
  });

  test('the warning offers the one next action as a real link, not just a sentence', () => {
    const source = employeesPage();

    expect(COMPANY_COUNTRY_FIX_HREF).toBe('/setup?tab=companies');
    expect(source).toContain('href={COMPANY_COUNTRY_FIX_HREF}');
    expect(source).toContain('{COMPANY_COUNTRY_FIX_LOCATION}');
    // New tab, so the half-filled form keeps its state (same choice as EstablishmentBlockedModal).
    expect(source).toContain('rel="noopener noreferrer"');
    // The old dead-end copy — the words with nowhere to click — must be gone.
    expect(source).not.toContain('has no country set — set it in Setup → Companies before adding employees.</p>');
  });

  // ── The client gate and the server refusal speak one wording ───────────────────────────────────

  test('the wording mirrors the server helper both surfaces are built on', () => {
    const helper = read('../backend-dotnet/Zayra.Api/Application/Common/HomeJurisdiction.cs');

    expect(helper).toContain('public const string CompanyFixLocation = "Setup → Companies";');
    expect(helper).toContain('has no country set — set it in {CompanyFixLocation} before adding employees.');
    expect(missingCompanyCountryMessage('X'))
      .toBe(`X has no country set — set it in ${COMPANY_COUNTRY_FIX_LOCATION} before adding employees.`);

    // A disabled button is an affordance, not authorization: the API refuses on its own.
    const service = read('../backend-dotnet/Zayra.Api/Infrastructure/Employees/EmployeeManagementService.cs');
    expect(service).toContain('await EnsureCompanyHasCountryAsync(tenantId, request.CompanyId, cancellationToken);');
    expect(service).toContain('throw new CompanyCountryMissingException(');
    const controller = read('../backend-dotnet/Zayra.Api/Controllers/EmployeesController.cs');
    expect(controller).toContain('catch (CompanyCountryMissingException ex)');
    expect(controller).toContain('HomeJurisdiction.MissingCompanyCountryError');
  });
});
