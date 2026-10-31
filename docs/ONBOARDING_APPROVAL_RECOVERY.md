# Independent approval and employee access

Employee activation, pay approval and login setup are separate decisions. A staged employee login
must not be made active to obtain the approval needed to activate that employee.

## Company setup

1. Establish the company's administrative logins in Setup → Users. These can be direct users without
   employee records. Grant a pay-entry operator and a different Finance Approver access to the company.
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

## Release and verification

No schema migration or bulk data repair is required. Deploy the API and frontend together.
Existing pay gaps, approval fingerprints, audit history, roles and passwords remain intact.

Automated coverage includes readiness/activation parity, company scope, unusable reviewer accounts,
creator/last-editor exclusions, an isolated 46-hire Saudi company through approval, activation,
welcome-code redemption and real password login, and responsive/Arabic approval UI checks.
Use disposable data for these tests; never exercise all production passwords or redeem employees'
real welcome codes to demonstrate a release.

After deployment, read the company diagnostic and each employee's readiness. Production completion
requires genuine independent pay decisions and the employees' own credential setup; a passing
automated test is not evidence that those business actions have occurred.
