# Independent approval and employee access

Employee activation, pay approval and login setup are separate decisions. A staged employee login
must not be made active to obtain the approval needed to activate that employee.

## Company setup

1. Establish the company's administrative logins in Setup → Users. These can be direct users without
   employee records. Grant a pay-entry operator and a different Finance Approver access to the company.
   New-company setup (step 6) can create both logins together: the company super admin and an optional independent pay
   reviewer (Finance Approver, this company only). The reviewer is a direct login with no employee record, so it is usable
   before anyone's own onboarding finishes, and it must be a different person from the administrator. Setup grants no
   approval: the Guided setup item "Independent pay reviewer" only reports whether a usable reviewer exists.
2. Each person completes their own password setup and required sign-in security. A pending, locked,
   disabled, out-of-scope or permission-denied reviewer does not count as ready.
3. Check **Independent pay approval** in Users and the independent reviewer item in Guided setup.
   These read current login permissions and company grants; they do not grant access.
4. Keep the reviewer independent of the packages they review. Creating or editing a package,
   being its employee, or handling that employee's disclosed credentials can exclude a reviewer.
   Being an administrator does not bypass these rules.

## Recover existing pending employees

1. Select the company in Setup → Users. Open **See who can review each pending package**.
   The panel lists eligible reviewers for each employee, without salary or bank details.
2. An eligible reviewer signs in using their own account, opens Pay approvals, checks the package
   against the actual evidence, and records their decision and reason. Do not script approvals.
3. If no eligible reviewer is listed, establish another independent direct user with the appropriate
   company grant and Finance Approver permission. Do not remove creator/editor attribution.
4. HR reloads employee readiness and completes any remaining blockers. Readiness and activation now
   use the same pay, task and stored-data checks. The activation endpoint rechecks at decision time.
5. HR activates each eligible employee, then issues their welcome code using the normal access flow.
   Each employee redeems their own code, chooses a password, and signs in. Used codes are refused;
   redemption itself never issues a session. A password reset never substitutes for pay approval.

The reported 46-account cohort must be reviewed individually: an available reviewer for one employee
may be barred for another. The Users diagnostic checks the first 100 pending packages and states when
more remain. Queue counters distinguish packages assigned to other independent reviewers from an
actually empty queue. Refresh after role, scope, security or account changes.

## Automatic notice when no reviewer is available

When a pending pay package has no reviewer who may decide it, administrators who can reach that company
receive one notification per company. It carries counts and a link to Setup → Users only, never names,
figures or account details. It refreshes while the gap remains and closes by itself, with an audit
entry, once an independent reviewer is available. The check only reads reviewer eligibility; it never
approves, rejects or changes access.

It is re-evaluated:

- right after a change that can alter who may review: role or permission changes, personal permission
  overrides, company grants and scope, switching a login on or off, the sign-in security policy,
  resetting a person's two-step setup, and an employee's employment status changing;
- right after pay is entered or changed for a person who is still pending: creating or editing an
  employee, an approved or scheduled pay change, a salary assignment or import, and employee imports;
- by a periodic backstop, so a change made without going through those paths is still picked up.

The backstop runs about every 6 hours (first run a few minutes after start-up), on one instance at a
time. Each run looks only at active workspaces that have a pending package or an open notice, and at
most 500 workspaces and 50 companies per workspace. A run that finds nothing changed writes nothing.
Settings (section `PayApprovalDeadEndSweep`): `Enabled`, `Interval`, `InitialDelay`,
`MaxTenantsPerRun`, `MaxCompaniesPerTenant`. Turning it off does not affect the checks above.

## Release and verification

No schema migration or bulk data repair is required. Deploy the API and frontend together.
Existing pay gaps, approval fingerprints, audit history, roles and passwords remain intact.

Automated coverage includes readiness/activation parity, company scope, unusable reviewer accounts,
creator, audited pay-writer, payroll-profile-editor, subject and credential-handler exclusions, an isolated 46-hire Saudi company through approval, activation,
welcome-code redemption and real password login, and responsive/Arabic approval UI checks.
Use disposable data for these tests; never exercise all production passwords or redeem employees'
real welcome codes to demonstrate a release.

After deployment, read the company diagnostic and each employee's readiness. Production completion
requires genuine independent pay decisions and the employees' own credential setup; a passing
automated test is not evidence that those business actions have occurred.
