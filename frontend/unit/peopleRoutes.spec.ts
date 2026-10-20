import { expect, test } from '@playwright/test';
import {
  buildPeopleReturnTo,
  employeeWorkspaceHref,
  legacyEmployeeTarget,
  parsePeopleDirectoryState,
  serializePeopleDirectoryState,
  validatePeopleReturnTo,
  type PeopleDirectoryState,
} from '../src/lib/peopleRoutes';

const completeDirectoryState: PeopleDirectoryState = {
  search: 'Alex Morgan',
  status: 'Active',
  readiness: 'NeedsAttention',
  access: 'not_started,code_given',
  importBatchId: '8c740ad8-e69d-4af3-b09f-36cbe15eb995',
  gapType: 'link:manager',
  view: 'ex',
  page: 3,
  anchor: 'employee-link-901',
};

test('round-trips every supported directory field without serializing transient selection or scroll state', () => {
  const serialized = serializePeopleDirectoryState({
    ...completeDirectoryState,
    selected: ['901'],
    scrollY: 720,
  } as PeopleDirectoryState & { selected: string[]; scrollY: number });

  expect(serialized.toString()).toBe(
    'search=Alex+Morgan&status=Active&readiness=NeedsAttention&access=not_started%2Ccode_given&importBatchId=8c740ad8-e69d-4af3-b09f-36cbe15eb995&gapType=link%3Amanager&view=ex&page=3&anchor=employee-link-901',
  );
  expect(parsePeopleDirectoryState(serialized)).toEqual(completeDirectoryState);
  expect(serialized.has('selected')).toBe(false);
  expect(serialized.has('scrollY')).toBe(false);
});

test('parsing ignores unsupported and invalid directory values instead of creating hidden state', () => {
  const parsed = parsePeopleDirectoryState(new URLSearchParams(
    'search=&status=Draft&view=former&page=0&anchor=row-7&selected=7&scrollY=400',
  ));

  expect(parsed).toEqual({ status: 'Draft', anchor: 'row-7' });
});

test('builds a complete directory return path including the focus anchor', () => {
  expect(buildPeopleReturnTo(completeDirectoryState)).toBe(
    '/people?search=Alex+Morgan&status=Active&readiness=NeedsAttention&access=not_started%2Ccode_given&importBatchId=8c740ad8-e69d-4af3-b09f-36cbe15eb995&gapType=link%3Amanager&view=ex&page=3&anchor=employee-link-901',
  );
  expect(buildPeopleReturnTo({})).toBe('/people');
});

for (const [raw, expected] of [
  ['/people?search=Alex&view=current&page=2&anchor=employee-link-17', '/people?search=Alex&view=current&page=2&anchor=employee-link-17'],
  ['https://app.test/people?status=Active', '/people?status=Active'],
] as const) {
  test(`accepts an exact same-origin People directory target: ${raw}`, () => {
    expect(validatePeopleReturnTo(raw, 'https://app.test')).toBe(expected);
  });
}

for (const raw of [
  'https://evil.test/people',
  '//evil.test/people',
  '/payroll',
  '/people/901',
  '/people/',
  'https://u:p@app.test/people',
  '/people?search=%',
  '%',
  'not a URL',
] as const) {
  test(`rejects an unsafe or malformed return target: ${raw}`, () => {
    expect(validatePeopleReturnTo(raw, 'https://app.test')).toBe('/people');
  });
}

test('sanitizes accepted return targets to the supported directory schema', () => {
  expect(validatePeopleReturnTo(
    '/people?search=Alex&view=ex&page=2&anchor=employee-link-17&selected=17&scrollY=900&unknown=yes',
    'https://app.test',
  )).toBe('/people?search=Alex&view=ex&page=2&anchor=employee-link-17');
});

test('builds view, panel, destination edit, and guided full-record URLs with one encoded return target', () => {
  const returnTo = '/people?search=Alex+Morgan&view=current&page=2&anchor=employee-link-901';

  expect(employeeWorkspaceHref(901)).toBe('/people/901');
  expect(employeeWorkspaceHref(901, { section: 'pay-benefits', panel: 'benefits' }))
    .toBe('/people/901?section=pay-benefits&panel=benefits');
  expect(employeeWorkspaceHref(901, { edit: true, section: 'employment' }))
    .toBe('/people/901/edit?section=employment');
  expect(employeeWorkspaceHref(901, { edit: true, mode: 'guided', section: 'employment', returnTo }))
    .toBe('/people/901/edit?mode=guided&section=employment&returnTo=%2Fpeople%3Fsearch%3DAlex%2BMorgan%26view%3Dcurrent%26page%3D2%26anchor%3Demployee-link-901');
});

test('never serializes an external or non-directory return target into an employee link', () => {
  expect(employeeWorkspaceHref(901, { returnTo: 'https://evil.test/people' })).toBe('/people/901');
  expect(employeeWorkspaceHref(901, { returnTo: '/people/901' })).toBe('/people/901');
  expect(employeeWorkspaceHref(901, { returnTo: '//app.test/people' })).toBe('/people/901');
});

for (const employeeId of [0, -1, 1.2, Number.NaN, Number.POSITIVE_INFINITY]) {
  test(`refuses a non-positive-integer employee identifier: ${employeeId}`, () => {
    expect(() => employeeWorkspaceHref(employeeId)).toThrow('positive integer');
  });
}

test('keeps static new and new-hires paths separate from numeric employee workspaces', () => {
  expect(legacyEmployeeTarget(new URLSearchParams('employeeId=new&tab=personal'))).toBeNull();
  expect(legacyEmployeeTarget(new URLSearchParams('employeeId=new-hires&tab=personal'))).toBeNull();
  expect(legacyEmployeeTarget(new URLSearchParams('employeeId=0&tab=personal'))).toBeNull();
  expect(legacyEmployeeTarget(new URLSearchParams('employeeId=901&tab=personal'))).toBe('/people/901?section=personal');
});

for (const [tab, expectedQuery] of [
  ['personal', 'section=personal'],
  ['employment', 'section=employment&panel=details'],
  ['payroll', 'section=pay-benefits&panel=payroll'],
  ['benefits', 'section=pay-benefits&panel=benefits'],
  ['package', 'section=pay-benefits&panel=package'],
  ['deductions', 'section=pay-benefits&panel=deductions'],
  ['compliance', 'section=compliance-documents&panel=compliance'],
  ['documents', 'section=compliance-documents&panel=documents'],
  ['history', 'section=activity&panel=history'],
  ['transfers', 'section=employment&panel=transfers'],
  ['development', 'section=activity&panel=development'],
] as const) {
  test(`maps legacy tab ${tab} to its canonical workspace target`, () => {
    expect(legacyEmployeeTarget(new URLSearchParams(`employeeId=901&tab=${tab}`)))
      .toBe(`/people/901?${expectedQuery}`);
  });
}

test('legacy redirects retain directory state as an encoded return target and never retain bulk selection', () => {
  const target = legacyEmployeeTarget(new URLSearchParams(
    'employeeId=901&tab=package&search=Alex&view=current&page=2&anchor=employee-link-901&selected=901',
  ));

  expect(target).toBe(
    '/people/901?section=pay-benefits&panel=package&returnTo=%2Fpeople%3Fsearch%3DAlex%26view%3Dcurrent%26page%3D2%26anchor%3Demployee-link-901',
  );
});

test('unknown or absent legacy tabs use the overview destination', () => {
  expect(legacyEmployeeTarget(new URLSearchParams('employeeId=901'))).toBe('/people/901?section=overview');
  expect(legacyEmployeeTarget(new URLSearchParams('employeeId=901&tab=unknown'))).toBe('/people/901?section=overview');
});
