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
  - **In both cases** an `Early exit deduction` could be raised, often around 700–950 minutes, and sometimes a
    `Late deduction` too.
- **A reversed pair** (an Out stamped before the In) was also saved as a complete, zero-length `Absent` day with an
  absence deduction.

`attendance_payroll_impacts.status` starts as `PendingPayroll`. It becomes `Processed` when a payroll run consumes
the impact (`PayrollController`, which also writes a `payroll_run_consumptions` row with
`artifact_type = 'AttendanceImpact'`).

Table and column names were checked against `Migrations/ZayraDbContextModelSnapshot.cs` and `InitialPostgres`.

## A. Find suspect days that carry deductions, per tenant

A **suspect day** is any daily record that has a punch but no worked span. That covers three shapes:

- **Out-only, post-fix:** no first-in.
- **Out-only, pre-fix:** first-in equal to last-out.
- **Reversed pair:** last-out before first-in.

It also covers a day marked `Absent` that still has a punch. Every suspect day carrying a Late, Early exit or
Absence deduction is listed, **whatever state the previous day is in**. A day whose previous day was repaired on its
own (for example by a regularization) therefore still shows up.

Two columns help you judge each row:

- `previous_day_in_only` marks the classic split night shift.
- `overnight_shift_on_previous_day` says whether the previous day had an overnight shift assigned.

Treat rows where both are false as low-confidence and review them by hand.

```sql
WITH d AS (
    SELECT id, tenant_id, employee_id, work_date, first_in_utc, last_out_utc, missing_punch, status
    FROM attendance_daily_records
    WHERE NOT is_deleted
),
suspect AS (
    SELECT n.tenant_id, n.employee_id, n.work_date AS next_date, n.id AS next_record_id,
           n.status AS next_status, n.first_in_utc AS next_first_in, n.last_out_utc AS next_last_out,
           (p.id IS NOT NULL AND p.first_in_utc IS NOT NULL AND p.last_out_utc IS NULL) AS previous_day_in_only,
           EXISTS (
               SELECT 1
               FROM shift_assignments sa
               JOIN shift_definitions sd ON sd.id = sa.shift_definition_id AND sd.tenant_id = sa.tenant_id
               WHERE sa.tenant_id = n.tenant_id AND sa.employee_id = n.employee_id
                 AND sa.assigned_date = n.work_date - 1 AND sd.end_time <= sd.start_time
           ) AS overnight_shift_on_previous_day
    FROM d n
    LEFT JOIN d p ON p.tenant_id = n.tenant_id AND p.employee_id = n.employee_id AND p.work_date = n.work_date - 1
    WHERE n.last_out_utc IS NOT NULL
      AND (   n.first_in_utc IS NULL                    -- Out-only (post-fix shape)
           OR n.first_in_utc >= n.last_out_utc          -- Out-only (pre-fix: first-in = last-out) or reversed pair
           OR n.status = 'Absent')                      -- Absent although it has a punch
)
SELECT s.tenant_id,
       CASE WHEN i.status = 'Processed' THEN 'Processed (paid)' ELSE 'Pending (' || i.status || ')' END AS impact_state,
       i.impact_type,
       s.previous_day_in_only,
       s.overnight_shift_on_previous_day,
       COUNT(*)                                        AS impacts,
       COUNT(DISTINCT (s.employee_id, s.next_date))    AS suspect_days,
       SUM(i.minutes)                                  AS minutes
FROM suspect s
JOIN attendance_payroll_impacts i
  ON i.tenant_id = s.tenant_id AND i.employee_id = s.employee_id AND i.work_date = s.next_date
WHERE i.impact_type IN ('Late deduction', 'Early exit deduction', 'Absence deduction')
GROUP BY s.tenant_id, impact_state, i.impact_type, s.previous_day_in_only, s.overnight_shift_on_previous_day
ORDER BY s.tenant_id, impact_state, i.impact_type;
```

For the per-row review list, which is also the **repair list** that section C works through, keep the CTEs and
replace the final `SELECT` with:

```sql
SELECT s.tenant_id, s.employee_id, s.next_date - 1 AS shift_date, s.next_date,
       s.previous_day_in_only, s.overnight_shift_on_previous_day,
       s.next_status, s.next_first_in, s.next_last_out,
       i.id AS impact_id, i.impact_type, i.minutes, i.status AS impact_status,
       c.payroll_run_id                                   -- which run consumed it, if any
FROM suspect s
JOIN attendance_payroll_impacts i
  ON i.tenant_id = s.tenant_id AND i.employee_id = s.employee_id AND i.work_date = s.next_date
LEFT JOIN payroll_run_consumptions c
  ON c.tenant_id = i.tenant_id AND c.artifact_type = 'AttendanceImpact' AND c.artifact_id = i.id
WHERE i.impact_type IN ('Late deduction', 'Early exit deduction', 'Absence deduction')
ORDER BY s.tenant_id, s.employee_id, s.next_date;
```

**Save this output before any repair.** The check in section C step 5 compares against it.

## B. Check the one-record-per-employee-day guarantee

```sql
-- Expect exactly one row, whose indexdef starts with CREATE UNIQUE INDEX, and indisvalid = true.
SELECT i.schemaname, i.indexname, i.indexdef, x.indisvalid
FROM pg_indexes i
JOIN pg_namespace ns ON ns.nspname = i.schemaname
JOIN pg_class c ON c.relname = i.indexname AND c.relnamespace = ns.oid
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
   will therefore recompute a paid period's days.
   - **Before this fix**, `AttendanceDerivedArtifacts.UpsertImpacts` **deleted every impact for the employee-day,
     including `Processed` ones**, and recreated them as `PendingPayroll`. A paid deduction was removed from its
     history and could be taken again by an off-cycle run, and the `payroll_run_consumptions` row pointed at an
     impact that no longer existed.
   - **With the fix**, `UpsertImpactsAsync` leaves `Processed` impacts exactly as paid. It raises no new deduction
     on a paid day, and records any difference as a High `PaidAttendanceChanged` attendance exception.
   - **What counts as a paid day** is payroll **coverage**, not impacts. The rule is: a non-voided payroll run for
     that month, at status `Processed`, `PendingFinanceReview`, `Approved`, `Locked` or `Paid`, that pays recurring
     salary (`IncludesRecurringPay`) and holds a non-voided slip for the employee. A day with any `Processed` impact
     also counts. So a clean day (paid in full, with no deductions) is protected too: a later recompute that finds
     an absence raises the exception, not a new deduction.
   - Draft and Voided runs, and bonus-only runs (which consume no attendance), do not make a day paid.
   - **Never run this repair before the fix is deployed.**
3. **Paid periods** (impact status `Processed`, or the work date falls in a payroll run that is approved or paid):
   **do not reprocess.** Correct each employee's pay with a payroll adjustment in the next open run, refunding the
   wrongly deducted minutes. Record the impact ids and `payroll_run_id` from section A as the evidence.
   - **If a paid day was reprocessed anyway**, open the unresolved `PaidAttendanceChanged` exceptions for those
     days. Each lists every impact type with what was paid and what the day now says. Make each listed difference a
     payroll adjustment, then resolve the exception.
   - **Payroll shows these as a warning.** Processing a run, and `/validate`, raise the non-blocking warning
     `WARN_PAID_ATTENDANCE_CHANGED`, once per unresolved exception for the run's employees in its period.
   - **How the exception behaves over time:**
     - There is one open exception per employee-day. Its text is rewritten when the difference changes.
     - It **closes automatically** when a later recompute matches what was paid again.
     - Once HR has resolved a difference, **the same difference is not raised again**, so nobody makes a second
       adjustment for it. A *different* difference opens a new exception. The signature is stored as a `[sig:…]`
       tag at the end of `details`, and automatic closes are marked `[auto-resolved: …]`.
     - **Voiding or reopening** the run closes the open exceptions for the employees and month it covered.
   - **Known gap:** the application has no screen or endpoint to resolve an attendance exception yet. Until one
     exists, HR's "resolve" is a manual, audited data correction.
   - **If the run is later voided or reopened**, the restore hands the paid impacts back as `PendingPayroll` with the
     amounts that were **paid**, not the recomputed ones. Reprocess those days before the replacement run.
4. **Unpaid periods** (every affected impact is still `PendingPayroll` and no payroll run covers the dates yet):
   repair with a **range run over D..D+1 for the one employee**. Use `POST /api/attendance/process` (or
   `process/async`) with body `{ "fromDate": "<D>", "toDate": "<D+1>", "employeeId": <id> }`.
   - **Do not use a regularization for this.** Approving a regularization recomputes D only, so D+1 keeps its
     Out-only record and its early-exit impact.
   - Under the fix, D's window takes the early-morning clock-out and D+1 no longer counts it.
   - **Processing covers Active employees only**, whether through `ProcessAsync` or the processing job. For an
     employee who has since been offboarded (Inactive, Suspended or otherwise not Active), the range run does
     nothing. Correct their final pay with a payroll adjustment instead.
5. **Check for absences the repair itself created. Do this after every repair.** Rest days come from the tenant's
   calendar work week, not from the roster. If D+1 was a night worker's rostered day off but the work week treats it
   as a working day, the reprocess leaves D+1 with no punch and **raises a new 480-minute `Absence deduction`**.
   Run this check after each batch of range runs:

   ```sql
   -- Absence deductions now on a day that follows an overnight shift and has no shift of its own. These are the
   -- rostered day off the work week does not know about. Compare against the repair list saved from section A:
   -- any (employee_id, work_date) here that did not carry an Absence deduction before is new.
   SELECT i.tenant_id, i.employee_id, i.work_date, i.minutes, i.status, i.created_at_utc, r.status AS day_status
   FROM attendance_payroll_impacts i
   JOIN attendance_daily_records r
     ON r.tenant_id = i.tenant_id AND r.employee_id = i.employee_id AND r.work_date = i.work_date AND NOT r.is_deleted
   WHERE i.impact_type = 'Absence deduction'
     AND r.first_in_utc IS NULL AND r.last_out_utc IS NULL
     AND EXISTS (SELECT 1 FROM shift_assignments sa JOIN shift_definitions sd
                   ON sd.id = sa.shift_definition_id AND sd.tenant_id = sa.tenant_id
                 WHERE sa.tenant_id = i.tenant_id AND sa.employee_id = i.employee_id
                   AND sa.assigned_date = i.work_date - 1 AND sd.end_time <= sd.start_time)
     AND NOT EXISTS (SELECT 1 FROM shift_assignments sa
                     WHERE sa.tenant_id = i.tenant_id AND sa.employee_id = i.employee_id AND sa.assigned_date = i.work_date)
   ORDER BY i.tenant_id, i.employee_id, i.work_date;
   ```

   For each new row, get HR to regularize the day (or record it as the employee's day off) **before any payroll run
   consumes it**. **On a tenant that rosters shifts, do not start the repair unless this check, and the
   regularization of what it finds, is part of the change plan.**
6. **Where a payroll run covers the dates but is not yet approved:** treat the period as paid unless the run is
   recalculated after the repair and the step 5 check.
7. **Afterwards**, re-run sections A and B, plus the step 5 check, and keep the before/after counts with the change
   record.

## Known behaviours

These are intended results of the one-boundary model. They are not defects, but HR should recognise them.

- **Touching shifts.** Take a 22:00–06:00 night followed by a 06:00–14:00 day. The boundary between the two days is
  06:01, one minute after the night ends. A clock-in for the day shift before 06:01 (for example at 05:50) goes to
  the night's day. The day shift's own record then lacks its first-in, so it is raised as a missing punch, and HR
  corrects it.
- **An unrostered early day after a night shift.** Take a 22:00–06:00 night with nothing rostered the next day. The
  night keeps its full 2-hour tolerance, so the boundary is 08:00. If the employee does work an unrostered early day:
  - a clock-in at 07:30 counts on the night's day (as a second In; the night's first-in is unchanged);
  - that day's later clock-out lands on the next day as an Out-only day, with a High missing-punch exception for
    review.

  Roster the early day, and the boundary moves to 2 hours before its start.

## Known limitations

- **The roster is read at processing time, not stored with the punch.** A punch's work date depends on the shifts
  assigned around it *when the punch is recomputed*. If a roster is published or edited after punches were
  recorded, the punches already processed keep their old dates until a range run over the affected days re-dates
  them. Any later recompute uses the new roster: another punch that day, ingest, a regularization, or the
  processing job. After a roster change for past days, run the D..D+1 range for the affected employees, then repeat
  the step 5 check.
- **The 120-minute punch tolerance is a constant** (`AttendanceWorkDate.ShiftPunchToleranceMinutes`). It is not a
  tenant, policy or shift setting.
