/** Validation findings of a Locked run, as the API sends them (English text with the employee, months and a raw setting key baked in). */
const finding = (id: string, code: string, severity: string, message: string) => ({ id, employeeId: 8, severity, code, message, isResolved: false });

export const LOCKED_RUN_FINDINGS = [
  finding('f1', 'GOSI_COHORT_NOT_RECORDED', 'Warning', 'Employee EVT2-0008 (Maha Almutairi): GOSI cohort not recorded, contribution basis unverified. There is no GOSI first-registration date on the employee record.'),
  finding('f2', 'IMPORTED_BANK_DETAILS_UNVERIFIED', 'Warning', "Employee EVT2-0008's bank details came from an employee import and have not been confirmed by a second person. Verify them with the employee, then confirm them on the employee's record (People → the employee → Confirm bank details) before this run is locked."),
  finding('f3', 'WARN_ARREARS_LOOKBACK_TRUNCATED', 'Warning', 'Maha Almutairi (EVT2-0008) has a salary change effective 2022-01-07, which is older than the 24-month arrears lookback. These finalised period(s) were NOT settled by this run: 2022-04, 2022-05, 2022-06, 2022-07. Raise a supplementary run, or widen payparameter.arrears_max_lookback_months.'),
];
