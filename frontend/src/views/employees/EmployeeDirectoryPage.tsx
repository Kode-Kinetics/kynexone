'use client';

import { EmployeesPage } from '../EmployeesPage';

/** Route-owned People directory. The legacy monolith remains the implementation source while its
 * record, edit, and create surfaces are moved into their dedicated workspace routes. */
export function EmployeeDirectoryPage() {
  return <EmployeesPage />;
}
