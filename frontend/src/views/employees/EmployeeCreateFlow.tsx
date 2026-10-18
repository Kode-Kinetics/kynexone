'use client';

import { EmployeesPage } from '../EmployeesPage';

/**
 * Canonical owner for direct employee creation.
 *
 * The creation reducer and panels are being extracted from EmployeesPage without changing their
 * validated behavior. This route boundary makes `/people/new` canonical now and keeps New Hires'
 * separate EmployeeDraft workflow out of direct creation.
 */
export function EmployeeCreateFlow() {
  return <EmployeesPage createOnly />;
}
