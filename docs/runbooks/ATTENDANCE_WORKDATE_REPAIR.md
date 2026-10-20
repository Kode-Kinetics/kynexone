# Attendance work-date repair (split night shifts)

**Read-only investigation, then a manual repair.** Nothing here writes. Run it on a read replica, or in a
`BEGIN READ ONLY; ... ROLLBACK;` session. Do not reprocess anything until the fix on branch
`fix/attendance-work-date-correctness` is deployed (see section C).

## What went wrong

Before the fix, a night shift (for example 22:00–06:00) could be split across two daily records:

- **Day D** got the clock-in only. It was saved as In-only: `first_in_utc` set, `last_out_utc` null,
  `missing_punch = true`.
- **Day D+1** got the early-morning clock-out. This happened when device ingest dated the punch by the calendar
  day, when the legacy mobile punch dated it by the UTC day, when the punch was outside the exact shift span, or
  when HR's range run counted it again. The clock-out was then saved as an Out-only day:
  - **Before the fix**, the first-in fell back to the Out punch, so `first_in_utc = last_out_utc`,
    `missing_punch = false`, `status = 'Absent'`, and an `Absence deduction` was raised.
  - **After the fix**, `first_in_utc` is null, `last_out_utc` is set and `missing_punch = true`.
  - **In both cases** an `Early exit deduction` could be raised, often around 700–950 minutes.

`attendance_payroll_impacts.status` starts as `PendingPayroll`. It becomes `Processed` when a payroll run consumes
the impact (`PayrollController`, which also writes a `payroll_run_consumptions` row with
`artifact_type = 'AttendanceImpact'`).

Table and column names were checked against `Migrations/ZayraDbContextModelSnapshot.cs` and `InitialPostgres`.

## A. Find split night shifts that carry deductions, per tenant

This is a **candidate** list. It pairs an In-only day D with a day D+1 (same employee) that is Out-only in either
the pre-fix or post-fix shape, or is `Absent` while still having a punch. An `Absent` day with no punch at all is a
real absence, so it is excluded. The `overnight_shift_on_d` column tells you whether D had an overnight shift
assigned. Treat pairs without one as low-confidence and review them by hand.

```sql
WITH d AS (
    SELECT id, tenant_id, employee_id, work_date, first_in_utc, last_out_utc, missing_punch, status
    FROM attendance_daily_records
    WHERE NOT is_deleted
),
pairs AS (
    SELECT n.tenant_id, n.employee_id,
           p.work_date AS shift_date, n.work_date AS next_date,
           p.id AS shift_record_id, n.id AS next_record_id,
           n.status AS next_status, n.first_in_utc AS next_first_in, n.last_out_utc AS next_last_out,
           EXISTS (
               SELECT 1
               FROM shift_assignments sa
               JOIN shift_definitions sd ON sd.id = sa.shift_definition_id AND sd.tenant_id = sa.tenant_id
               WHERE sa.tenant_id = p.tenant_id AND sa.employee_id = p.employee_id
                 AND sa.assigned_date = p.work_date AND sd.end_time <= sd.start_time
           ) AS overnight_shift_on_d
    FROM d p
    JOIN d n ON n.tenant_id = p.tenant_id AND n.employee_id = p.employee_id AND n.work_date = p.work_date + 1
    WHERE p.first_in_utc IS NOT NULL AND p.last_out_utc IS NULL            -- D: In-only
      AND (   (n.first_in_utc IS NULL AND n.last_out_utc IS NOT NULL)      -- D+1 Out-only (post-fix shape)
           OR (n.first_in_utc IS NOT NULL AND n.first_in_utc = n.last_out_utc) -- D+1 Out-only (pre-fix shape)
           OR (n.status = 'Absent' AND n.last_out_utc IS NOT NULL))        -- D+1 Absent although it has a punch
)
SELECT pr.tenant_id,
       CASE WHEN i.status = 'Processed' THEN 'Processed (paid)' ELSE 'Pending (' || i.status || ')' END AS impact_state,
       i.impact_type,
       pr.overnight_shift_on_d,
       COUNT(*)                                        AS impacts,
       COUNT(DISTINCT (pr.employee_id, pr.next_date))  AS split_days,
       SUM(i.minutes)                                  AS minutes
FROM pairs pr
JOIN attendance_payroll_impacts i
  ON i.tenant_id = pr.tenant_id AND i.employee_id = pr.employee_id AND i.work_date = pr.next_date
WHERE i.impact_type IN ('Absence deduction', 'Early exit deduction')
GROUP BY pr.tenant_id, impact_state, i.impact_type, pr.overnight_shift_on_d
ORDER BY pr.tenant_id, impact_state, i.impact_type;
```

To see the individual rows for review, keep the `d` and `pairs` CTEs and replace the final `SELECT` with:

```sql
SELECT pr.tenant_id, pr.employee_id, pr.shift_date, pr.next_date, pr.overnight_shift_on_d,
       pr.next_status, pr.next_first_in, pr.next_last_out,
       i.id AS impact_id, i.impact_type, i.minutes, i.status AS impact_status,
       c.payroll_run_id                                   -- which run consumed it, if any
FROM pairs pr
JOIN attendance_payroll_impacts i
  ON i.tenant_id = pr.tenant_id AND i.employee_id = pr.employee_id AND i.work_date = pr.next_date
LEFT JOIN payroll_run_consumptions c
  ON c.tenant_id = i.tenant_id AND c.artifact_type = 'AttendanceImpact' AND c.artifact_id = i.id
WHERE i.impact_type IN ('Absence deduction', 'Early exit deduction')
ORDER BY pr.tenant_id, pr.employee_id, pr.next_date;
```

## B. Check the one-record-per-employee-day guarantee

```sql
-- Expect exactly one row, whose indexdef starts with CREATE UNIQUE INDEX, and indisvalid = true.
SELECT i.indexname, i.indexdef, x.indisvalid
FROM pg_indexes i
JOIN pg_class c ON c.relname = i.indexname
JOIN pg_index x ON x.indexrelid = c.oid
WHERE i.tablename = 'attendance_daily_records'
  AND i.indexname = 'IX_attendance_daily_records_tenant_id_employee_id_work_date';

-- Expect zero rows.
SELECT tenant_id, employee_id, work_date, COUNT(*) AS records
FROM attendance_daily_records
GROUP BY tenant_id, employee_id, work_date
HAVING COUNT(*) > 1;
```

If the index is missing or invalid, or duplicates exist, stop. Fix that before any repair.

## C. Safe repair procedure

1. **Deploy the fix first.** If you reprocess before it is live, the old code rebuilds the same split.
2. **The payroll lock does not protect you.** `attendance_lock_periods` is never written by the application, so
   `IsLocked` is always false, and `attendance_daily_records.is_payroll_locked` is never set either. Reprocessing
   would therefore happily rewrite a paid period. Worse, `AttendanceDerivedArtifacts.UpsertImpacts`
   (`AttendanceDerivedArtifacts.cs:92-93`) **deletes every impact for the employee-day, including `Processed`
   ones**, and recreates them as `PendingPayroll`. A paid deduction would be removed from its history and could be
   taken again by the next run, while the `payroll_run_consumptions` row would point at an impact that no longer
   exists.
3. **Paid periods** (impact status `Processed`, or the work date falls in a payroll run that is approved or paid):
   **do not reprocess.** Correct each employee's pay with a payroll adjustment in the next open run, refunding the
   wrongly deducted absence or early-exit minutes. Record the impact ids and `payroll_run_id` from section A as
   the evidence. Leave the attendance records as they are, or correct them only through an HR regularization
   once payroll has signed off.
4. **Unpaid periods** (every affected impact is still `PendingPayroll` and no payroll run covers the dates yet):
   once the fix is deployed, reprocess the affected employee-days. Use HR's attendance processing over the range
   from D to D+1, or approve a regularization for D.
   - **Check before you run it.** Under the fix, D's window takes the early-morning clock-out. Re-run section A for
     the tenant and confirm the pair is gone.
   - **Expected result.** D becomes a complete day. D+1 no longer counts the clock-out: it ends up as a no-punch
     day (Absent, Rest day or On leave, whichever applies) or as a genuine day if the employee also worked it.
5. **Where a payroll run covers the dates but is not yet approved:** treat the period as paid unless the run is
   recalculated after the reprocess.
6. **Afterwards**, re-run sections A and B and keep the before/after counts with the change record.
