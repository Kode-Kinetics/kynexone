import { expect, test } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import { offerCreationFailure } from '../src/lib/recruitmentJourney';

const read = (relative: string) => fs.readFileSync(path.join(process.cwd(), relative), 'utf8');
const recruitmentPage = () => read('src/views/RecruitmentPage.tsx');

/**
 * F08: an offer's department and job title were typed as free text. Nothing checked them until HR
 * approved the accepted offer's employee record, and activation then refused the hire because the
 * names matched no department or designation record. Both offer forms now pick from the
 * organisation's records, and a refusal reaches the recruiter in the API's own words.
 */
test.describe('Recruitment — an offer names the organisation\'s own department and designation', () => {
  test('neither offer form accepts a typed department or job title any more', () => {
    const source = recruitmentPage();
    expect(source).not.toContain('placeholder="Department" value={offerForm.department}');
    expect(source).not.toContain('offeredJobTitle: e.target.value');
    expect(source).not.toContain('offeredDepartment: e.target.value');
    // Both forms use the one picker.
    expect(source.match(/<OfferPlacementFields/g)?.length).toBe(2);
  });

  test('the drawer sends record ids and otherwise lets the API use the opening\'s placement', () => {
    const source = recruitmentPage();
    expect(source).toContain('departmentId: offerForm.departmentId || undefined,');
    expect(source).toContain('designationId: offerForm.designationId || undefined,');
    expect(source).toMatch(/<OfferPlacementFields\s+idPrefix={`drawer-offer-\$\{id\}`}\s+openingDefaults\s/);
    expect(source).toContain("openingDefaults={false}");
  });

  test('the Offers tab requires a designation and shows why the API refused an offer', () => {
    const source = recruitmentPage();
    expect(source).toContain("'Application, designation and start date are required.'");
    expect(source).toContain('setOfferError(offerCreationFailure(e))');
    expect(source).not.toContain("setOfferError('Failed to create offer.')");
  });

  test('a refusal is reported in the API\'s words, with a fallback only when it gave none', () => {
    const refusal = {
      response: { status: 422, data: { error: 'offer_placement_unresolved', message: "'Growth' is not one of your organisation's departments." } },
    };
    expect(offerCreationFailure(refusal)).toContain("'Growth' is not one of your organisation's departments.");
    expect(offerCreationFailure({ response: { status: 500 } })).toBe('The offer could not be created. Please try again.');
    expect(offerCreationFailure(null)).toBe('The offer could not be created. Please try again.');
  });

  test('the picker lists the records activation resolves against, and says so when it cannot load them', () => {
    const picker = read('src/components/OfferPlacementFields.tsx');
    expect(picker).toContain('offersApi.placementOptions()');
    expect(picker).toContain('role="alert"');
    expect(read('src/api/recruitment.ts')).toContain("'/api/recruitment/offers/placement-options'");
  });
});
