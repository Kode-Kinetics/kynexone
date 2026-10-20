# Employee Workspace Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the People page's persistent employee drawer and separate create/edit modals with one secure, route-backed, guided Employee Workspace while preserving all existing employee-domain behavior.

**Architecture:** Secure the existing APIs first, then introduce pure route-state and persistent navigation-guard foundations. Extract the current directory, record panels, edit flow, and create flow from `EmployeesPage.tsx` into focused route-owned components without changing database schema or request payloads. Keep legacy links behind one redirect adapter until all internal producers use canonical workspace URLs.

**Tech Stack:** Next.js 15 App Router, React 19, TypeScript 6, Tailwind CSS, Playwright, ASP.NET Core, EF Core, xUnit, FluentAssertions.

**Spec:** `docs/superpowers/specs/2026-10-17-employee-workspace-design.md`

## Global Constraints

- Preserve the existing PostgreSQL schema, employee entities, migrations, API payloads, tenant/company/employee scope, approvals, and application shell.
- Direct Add Employee remains `POST /api/employees`; it does not enter the `EmployeeDraft` New Hires queue.
- Every edit reaches the existing before/after Review before any mutation.
- Effective permission claims plus tenant/company/employee scope are authoritative; frontend role-name allowlists are not security gates.
- HR/IT enters the complete IT-issued work email; collision suggestions are advisory and never auto-applied.
- Full setup cannot reach Review until Payroll, Salary, and Identity have been visited.
- Arabic suggestions remain explicit Use/Dismiss actions with a safe manual fallback.
- The workspace has one primary vertical content scroller and no persistent right-hand employee drawer.
- Every new or moved string must be translated in English and Arabic and behave correctly under RTL.
- Motion is functional, interruptible, approximately 120–200 ms for micro-interactions and 180–240 ms for destination changes, with equivalent instant reduced-motion behavior.
- Do not overwrite, reset, stash, or absorb unrelated dirty work in `fix/policy-binder-draft`.

## Review Focus

1. A delayed employee-A response after navigating to employee B must never replace employee B or its error state — covered in Task 4's deferred-response browser tests.
2. A hostile or malformed `returnTo` must never leave the same-origin `/people` route family — covered in Task 2's table-driven unit tests.
3. A dirty draft must survive pathname remount while an asynchronous leave decision is open, then run the queued action exactly once — covered in Task 3's unit and browser tests.
4. Sensitive values must never enter the DOM, mutation payload, review delta, or client cache for a caller lacking `employees.sensitive` — covered in Tasks 1, 6, and 7.
5. Mobile sticky actions, the bottom navigation, assistant launcher, safe area, focused field, and virtual keyboard must never overlap — covered in Task 9's geometry assertions.

---

### Task 1: Make the Employee permission matrix server-authoritative

**Files:**
- Create: `backend-dotnet/Zayra.Api.Tests/Security/EmployeeWorkspaceAuthorizationTests.cs`
- Modify: `backend-dotnet/Zayra.Api/Controllers/EmployeesController.cs:109-5205`
- Modify: `backend-dotnet/Zayra.Api.Tests/Security/LegacyRoleGateBypassAllowList.cs:300-325`
- Modify: `backend-dotnet/Zayra.Api.Tests/Security/EmployeeAuthzRegressionTests.cs`
- Modify: `backend-dotnet/Zayra.Api.Tests/Security/SensitiveFieldMaskingTests.cs`

**Interfaces:**
- Consumes: existing `HasPermissionAttribute`, production authorization pipeline, `IDataScopeService`, sensitive-field masking, and body-dependent `POST /api/employees/bulk` authorization.
- Produces: explicit per-endpoint permission attributes and pipeline-level 403 coverage for every directory/workspace API.

- [ ] **Step 1: Write the failing authorization tests**

Add table-driven HTTP tests named `ReadEndpoint_WithoutEmployeesRead_ReturnsForbidden`, `ImportTemplate_WithoutBulkImport_ReturnsForbidden`, `MutationEndpoint_WithoutActionPermission_ReturnsForbidden`, `BulkAction_UsesItsOwnPermission`, `SensitiveRead_WithoutSensitivePermission_IsMasked`, and `ScopedReader_CannotReadAnotherCompanyEmployee`.

```csharp
[Theory]
[MemberData(nameof(EmployeeReadCases))]
public async Task ReadEndpoint_WithoutEmployeesRead_ReturnsForbidden(HttpRequestMessage request)
{
    var response = await SendAsync(request, permissions: []);
    response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
}
```

Cases must include list, Ex-Employees, detail, field catalog, document metadata, history, readiness, reporting lines, hierarchy, workflow approvers, and raw document download. Mutation cases include create/update/status/activate/terminate/delete, duplicate resolution, bank confirmation, document actions, transfers, completion, access, import, and each bulk action. Raw document download must prove both `employees.read` and `employees.documents` are present; sensitive identity/payroll document categories must additionally prove `employees.sensitive`.

- [ ] **Step 2: Run the focused tests and verify RED**

Run from repository root:

```bash
dotnet test backend-dotnet/Zayra.Api.Tests/Zayra.Api.Tests.csproj --filter "FullyQualifiedName~EmployeeWorkspaceAuthorizationTests"
```

Expected: the new tests fail on missing or inferred-only permission gates, including detail/documents/history/readiness and import-template.

- [ ] **Step 3: Apply explicit endpoint permissions**

Add `HasPermission("employees.read")` to read endpoints, `HasPermission("employees.write")` to update, and `HasPermission("employees.bulk_import")` to import template. Preserve any-of semantics, imperative sensitive checks, all data-scope checks, and body-dependent bulk authorization. Because repeated `HasPermission` attributes retain the framework's any-of behavior, enforce raw document download imperatively as `employees.read` **and** `employees.documents`; require `employees.sensitive` as a third permission for sensitive identity/payroll document categories. Keep document metadata read-only and masked for callers without the sensitive permission.

- [ ] **Step 4: Reconcile legacy authorization expectations**

Update only tests/allow-list entries invalidated by explicit local gates. Do not weaken data scope, sensitive masking, or action-specific permissions.

- [ ] **Step 5: Run focused and adjacent authorization suites**

```bash
dotnet test backend-dotnet/Zayra.Api.Tests/Zayra.Api.Tests.csproj --filter "FullyQualifiedName~EmployeeWorkspaceAuthorizationTests|FullyQualifiedName~EmployeeAuthzRegressionTests|FullyQualifiedName~SensitiveFieldMaskingTests|FullyQualifiedName~EmployeeBulkActionTests|FullyQualifiedName~EmployeeAccessTests|FullyQualifiedName~EmployeeCompletionHttpTests"
```

Expected: PASS with no skipped authorization cases.

- [ ] **Step 6: Commit**

```bash
git add backend-dotnet/Zayra.Api/Controllers/EmployeesController.cs backend-dotnet/Zayra.Api.Tests/Security/EmployeeWorkspaceAuthorizationTests.cs backend-dotnet/Zayra.Api.Tests/Security/LegacyRoleGateBypassAllowList.cs backend-dotnet/Zayra.Api.Tests/Security/EmployeeAuthzRegressionTests.cs backend-dotnet/Zayra.Api.Tests/Security/SensitiveFieldMaskingTests.cs
git commit -m "fix(employees): enforce workspace permissions"
```

### Task 2: Freeze canonical People and employee workspace URLs

**Files:**
- Create: `frontend/src/lib/peopleRoutes.ts`
- Create: `frontend/unit/peopleRoutes.spec.ts`

**Interfaces:**
- Consumes: `URLSearchParams` and browser origin only.
- Produces: `EmployeeSection`, `PeopleDirectoryState`, `parsePeopleDirectoryState`, `serializePeopleDirectoryState`, `buildPeopleReturnTo`, `validatePeopleReturnTo`, `employeeWorkspaceHref`, and `legacyEmployeeTarget`.

- [ ] **Step 1: Write the failing route-contract tests**

```ts
test('round-trips the complete visible directory state without bulk selection', () => {
  const state = fixturePeopleState();
  expect(parsePeopleDirectoryState(serializePeopleDirectoryState(state))).toEqual(state);
  expect(serializePeopleDirectoryState(state).has('selected')).toBe(false);
});

test.each(['https://evil.test/people', '//evil.test/people', '/payroll', 'https://u:p@app.test/people', '%'])
  ('rejects an unsafe return target: %s', (raw) => {
    expect(validatePeopleReturnTo(raw, 'https://app.test')).toBe('/people');
  });
```

Also assert positive numeric IDs, static `new`/`new-hires` separation, all legacy tab mappings, view/edit/guided URLs, `panel`, encoded `returnTo`, and `anchor` behavior.

- [ ] **Step 2: Run the unit test and verify RED**

```bash
cd frontend && npx playwright test -c playwright.unit.config.ts peopleRoutes.spec.ts
```

Expected: FAIL because `peopleRoutes.ts` does not exist.

- [ ] **Step 3: Implement the route helpers**

Use the exact interfaces from the Interfaces block. The supported directory keys are `search`, `status`, `readiness`, `access`, `importBatchId`, `gapType`, `view`, `page`, and `anchor`. Accept only a same-origin `/people` pathname for `returnTo`.

- [ ] **Step 4: Run the route-contract tests and verify GREEN**

Run the Step 2 command. Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add frontend/src/lib/peopleRoutes.ts frontend/unit/peopleRoutes.spec.ts
git commit -m "feat(people): define workspace routes"
```

### Task 3: Install one persistent asynchronous navigation guard

**Files:**
- Create: `frontend/src/lib/navigationGuardState.ts`
- Create: `frontend/unit/navigationGuardState.spec.ts`
- Create: `frontend/src/contexts/NavigationGuardContext.tsx`
- Create: `frontend/src/hooks/useNavigationBlocker.ts`
- Modify: `frontend/src/components/ui/ConfirmDialog.tsx`
- Modify: `frontend/app/(dashboard)/layout.tsx`
- Modify: `frontend/src/layouts/AppLayout.tsx`
- Modify: `frontend/src/layouts/Sidebar.tsx`
- Modify: `frontend/src/layouts/TopBar.tsx`
- Modify: `frontend/src/contexts/CompanyContext.tsx`
- Modify: `frontend/src/hooks/useSetupNavigationGuard.ts`
- Modify: `frontend/src/lib/confirmAppNavigation.ts`
- Test: `frontend/e2e/employee-workspace.spec.ts`

**Interfaces:**
- Consumes: existing `ConfirmDialog`, Next router, company-change event, link clicks, History API, and `beforeunload`.
- Produces: `NavigationGuardProvider`, `useNavigationGuard()`, and `useNavigationBlocker({ id, when, message, onDiscard })`.

```ts
interface NavigationIntent {
  destination: string;
  run: () => void | Promise<void>;
  restoreFocus?: HTMLElement | null;
}
interface NavigationGuardValue {
  request(intent: NavigationIntent): void;
  push(href: string): void;
  replace(href: string): void;
  run(destination: string, action: () => void | Promise<void>): void;
}
```

- [ ] **Step 1: Write failing pure state-machine tests**

Assert `queueIntent`, `cancelIntent`, and `confirmIntent` preserve one latest intent, restore focus on cancel, call `onDiscard` before navigation, and run the confirmed action once even if history/router interception re-enters.

- [ ] **Step 2: Verify the unit tests fail**

```bash
cd frontend && npx playwright test -c playwright.unit.config.ts navigationGuardState.spec.ts
```

- [ ] **Step 3: Implement the pure guard state and provider**

Mount `NavigationGuardProvider` above `AppLayout`'s pathname-keyed `<main>`. The provider owns blockers, queued intent, shared accessible confirmation, one-shot bypass, link/Back/Forward interception, company switch, sign-out, and native `beforeunload`. Extend `ConfirmDialog` so an async confirm action remains open and disabled while `onDiscard` and the queued navigation finish; errors keep the dialog open and announce the failure.

- [ ] **Step 4: Adapt existing shell and Setup callers**

Keep `confirmAppNavigation` as a temporary adapter into the provider. Replace Setup's independent `window.confirm`/history system with `useNavigationBlocker`; no second global navigation guard remains.

- [ ] **Step 5: Add and run browser regressions**

In `employee-workspace.spec.ts`, assert dirty link, command-palette, sidebar, company-switch, sign-out, Back/Forward, and clean navigation; cancel restores URL/focus; confirm performs the intended action once; refresh registers `beforeunload`.

```bash
cd frontend && npx playwright test -c e2e/playwright.employee-workspace.config.ts -g "navigation guard"
```

Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add frontend/src/lib/navigationGuardState.ts frontend/unit/navigationGuardState.spec.ts frontend/src/contexts/NavigationGuardContext.tsx frontend/src/hooks/useNavigationBlocker.ts frontend/src/components/ui/ConfirmDialog.tsx frontend/app/'(dashboard)'/layout.tsx frontend/src/layouts/AppLayout.tsx frontend/src/layouts/Sidebar.tsx frontend/src/layouts/TopBar.tsx frontend/src/contexts/CompanyContext.tsx frontend/src/hooks/useSetupNavigationGuard.ts frontend/src/lib/confirmAppNavigation.ts frontend/e2e/employee-workspace.spec.ts
git commit -m "feat(navigation): add persistent leave guard"
```

### Task 4: Add route owners and race-safe employee record loading

**Files:**
- Create: `frontend/app/(dashboard)/people/new/page.tsx`
- Create: `frontend/app/(dashboard)/people/[employeeId]/page.tsx`
- Create: `frontend/app/(dashboard)/people/[employeeId]/edit/page.tsx`
- Create: `frontend/src/views/employees/useEmployeeRecord.ts`
- Create: `frontend/src/views/employees/EmployeeWorkspacePage.tsx`
- Create: `frontend/e2e/employee-workspace.fixture.ts`
- Create: `frontend/e2e/playwright.employee-workspace.config.ts`
- Modify: `frontend/app/(dashboard)/people/page.tsx`
- Modify: `frontend/e2e/employee-workspace.spec.ts`

**Interfaces:**
- Consumes: Task 2 route helpers, `employeesApi.get`, `employeesApi.readiness`, and `PermissionGate`.
- Produces: `useEmployeeRecord(employeeId: number): EmployeeRecordState` with independent detail/readiness states and `refreshRecord()`.

```ts
interface EmployeeRecordState {
  detail: { data: EmployeeDetail | null; loading: boolean; error: EmployeeLoadError | null };
  readiness: { data: EmployeeReadiness | null; loading: boolean; error: EmployeeLoadError | null };
  refreshRecord(): Promise<void>;
  refreshReadiness(): Promise<void>;
}
```

- [ ] **Step 1: Write failing route/loading browser tests**

Cover direct and legacy URLs, invalid/nonpositive IDs, 404/403/network recovery, detail-before-readiness, readiness-before-detail, readiness Retry, and deferred A→B responses where A must never render after B.

- [ ] **Step 2: Verify RED**

```bash
cd frontend && npx playwright test -c e2e/playwright.employee-workspace.config.ts -g "route|loading|stale"
```

- [ ] **Step 3: Implement route pages and legacy adapter**

Use `employees.read` for view, `employees.write` for new/edit, and preserve the existing New Hires any-of gate. Validate positive integer IDs before calling the API. `/people` redirects legacy `employeeId/tab` before loading the directory.

- [ ] **Step 4: Implement `useEmployeeRecord`**

Start detail/readiness concurrently, settle independently, and guard both success and error results with an abort controller or monotonically increasing request generation.

- [ ] **Step 5: Run focused browser tests and TypeScript**

```bash
cd frontend && npx playwright test -c e2e/playwright.employee-workspace.config.ts -g "route|loading|stale" && npx tsc --noEmit
```

Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add frontend/app/'(dashboard)'/people/page.tsx frontend/app/'(dashboard)'/people/new/page.tsx frontend/app/'(dashboard)'/people/'[employeeId]'/page.tsx frontend/app/'(dashboard)'/people/'[employeeId]'/edit/page.tsx frontend/src/views/employees/useEmployeeRecord.ts frontend/src/views/employees/EmployeeWorkspacePage.tsx frontend/e2e/employee-workspace.fixture.ts frontend/e2e/employee-workspace.spec.ts frontend/e2e/playwright.employee-workspace.config.ts
git commit -m "feat(employees): add workspace routes"
```

### Task 5: Extract the full-width People directory and canonical return state

**Files:**
- Create: `frontend/src/hooks/usePeopleDirectoryQuery.ts`
- Create: `frontend/src/views/employees/EmployeeDirectoryPage.tsx`
- Modify: `frontend/src/views/EmployeesPage.tsx:330-1075,1930-2275`
- Modify: `frontend/src/views/ExEmployeesTable.tsx`
- Modify: `frontend/app/(dashboard)/people/page.tsx`
- Modify: `frontend/e2e/employee-workspace.spec.ts`
- Modify: `frontend/e2e/employee-import-preview.spec.ts`

**Interfaces:**
- Consumes: Task 2 route helpers and existing list/import/bulk/access APIs.
- Produces: `usePeopleDirectoryQuery()` and `EmployeeDirectoryPage`; employee names are real links built by `employeeWorkspaceHref`.

- [ ] **Step 1: Write failing directory browser tests**

Assert full-width table/no employee aside, canonical URL changes for all filters/view/page, exact return restoration, transient selection clearing with announcement, keyboard/new-tab links, Ex-Employees URL state/read-only behavior, import preview, and anchor/focus restoration.

- [ ] **Step 2: Verify RED**

```bash
cd frontend && npx playwright test -c e2e/playwright.employee-workspace.config.ts -g "directory|Ex-Employees|return state"
```

- [ ] **Step 3: Extract directory state and UI**

Move list, filters, pagination, import, bulk, access issuance, notices, and Current/Ex selection into `EmployeeDirectoryPage`. Keep selection in memory only and clear it on departure. Move raw import-template/export calls into `employeesApi` if still outside it.

- [ ] **Step 4: Convert Ex-Employees and row navigation**

Drive Ex-Employees search/page from the canonical URL. Keep it read-only. Replace active employee row selection with canonical links containing validated `returnTo`.

- [ ] **Step 5: Run directory/import tests**

```bash
cd frontend && npx playwright test -c e2e/playwright.employee-workspace.config.ts -g "directory|Ex-Employees|return state" && npx playwright test -c e2e/playwright.employee-import.config.ts
```

Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add frontend/src/hooks/usePeopleDirectoryQuery.ts frontend/src/views/employees/EmployeeDirectoryPage.tsx frontend/src/views/EmployeesPage.tsx frontend/src/views/ExEmployeesTable.tsx frontend/app/'(dashboard)'/people/page.tsx frontend/src/api/employees.ts frontend/e2e/employee-workspace.spec.ts frontend/e2e/employee-import-preview.spec.ts
git commit -m "feat(people): extract full-width directory"
```

### Task 6: Build the single Employee Workspace record surface

**Files:**
- Create: `frontend/src/views/employees/EmployeeWorkspaceShell.tsx`
- Create: `frontend/src/views/employees/EmployeeOverview.tsx`
- Create: `frontend/src/views/employees/EmployeePersonalSection.tsx`
- Create: `frontend/src/views/employees/EmployeeEmploymentSection.tsx`
- Create: `frontend/src/views/employees/EmployeePayBenefitsSection.tsx`
- Create: `frontend/src/views/employees/EmployeeComplianceDocumentsSection.tsx`
- Create: `frontend/src/views/employees/EmployeeAccessOnboardingSection.tsx`
- Create: `frontend/src/views/employees/EmployeeActivitySection.tsx`
- Modify: `frontend/src/views/employees/EmployeeWorkspacePage.tsx`
- Modify: `frontend/src/views/EmployeesPage.tsx:2276-2595`
- Modify: `frontend/src/i18n/translations.ts`
- Modify: `frontend/src/styles/index.css`
- Modify: `frontend/e2e/employee-workspace.spec.ts`

**Interfaces:**
- Consumes: Task 4 `EmployeeRecordState`, existing Benefits/Package/Deductions/Development/Completion/Access/Readiness/GOSI panels, auth permissions, feature flags, and Task 2 section/panel routes.
- Produces: one header/navigation/content shell with `data-employee-workspace-scroll` and seven permission-aware destinations.

- [ ] **Step 1: Write failing workspace destination and persona tests**

Assert lossless tab mapping, addressable panels, one primary scroller, informative Overview next action, blocker vs recommendation copy, panel-local failure, absent unauthorized controls with zero mutation requests, sensitive-value absence, nested-dialog Escape/focus, and unavailable feature/permission fallbacks.

- [ ] **Step 2: Verify RED**

```bash
cd frontend && npx playwright test -c e2e/playwright.employee-workspace.config.ts -g "workspace|destination|permission|sensitive"
```

- [ ] **Step 3: Implement the shell and move existing panels**

Move—not fork—the current record panels. Keep lifecycle actions in Overview/More actions, readiness fixes at their canonical destination, access/completion in Access & Onboarding, and approvals/history/development in Activity. Gate document mutation with `employees.documents`, transfers with `manager.approve`, activation with `employees.approve`, deletion with `employees.delete`, and bank confirmation with `employees.write` plus `employees.sensitive`.

- [ ] **Step 4: Add English/Arabic strings and responsive styles**

Use logical properties and directional icons. Do not add a second app sidebar or nested workspace-height scroller.

- [ ] **Step 5: Run workspace, unit, and type checks**

```bash
cd frontend && npx playwright test -c e2e/playwright.employee-workspace.config.ts -g "workspace|destination|permission|sensitive" && npm run test:unit && npx tsc --noEmit
```

Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add frontend/src/views/employees/EmployeeWorkspaceShell.tsx frontend/src/views/employees/EmployeeOverview.tsx frontend/src/views/employees/EmployeePersonalSection.tsx frontend/src/views/employees/EmployeeEmploymentSection.tsx frontend/src/views/employees/EmployeePayBenefitsSection.tsx frontend/src/views/employees/EmployeeComplianceDocumentsSection.tsx frontend/src/views/employees/EmployeeAccessOnboardingSection.tsx frontend/src/views/employees/EmployeeActivitySection.tsx frontend/src/views/employees/EmployeeWorkspacePage.tsx frontend/src/views/EmployeesPage.tsx frontend/src/i18n/translations.ts frontend/src/styles/index.css frontend/e2e/employee-workspace.spec.ts
git commit -m "feat(employees): add unified record workspace"
```

### Task 7: Move existing employee editing into the workspace

**Files:**
- Create: `frontend/src/views/employees/useEmployeeEditDraft.ts`
- Create: `frontend/src/views/employees/EmployeeEditFlow.tsx`
- Modify: `frontend/src/views/EmployeesPage.tsx:1413-1732,2598-2705`
- Modify: `frontend/src/components/EmployeeCreateWizard.tsx`
- Modify: `frontend/e2e/employee-edit-wizard.spec.ts`
- Modify: `frontend/e2e/employee-edit-draft.spec.ts`
- Modify: `frontend/e2e/employee-workspace.spec.ts`

**Interfaces:**
- Consumes: Task 3 `useNavigationBlocker`, Task 4 record refresh, `employeeEditDraft.ts`, field catalog, `employeesApi.update`, and existing wizard/review primitives.
- Produces: destination-scoped and `mode=guided` edit entries; both reach Review before PUT.

- [ ] **Step 1: Rewrite tests first for route-owned editing**

Assert no PUT before Review for both entry modes, changed-fields-only payload, effective date, 202 approval response, custom role with `employees.write`, no-sensitive field exclusion, delayed catalog merge without draft loss, field validation, failed-save retry with the identical payload, internal step changes without leave prompt, and external dirty navigation through Task 3.

- [ ] **Step 2: Verify RED**

```bash
cd frontend && npx playwright test -c e2e/playwright.employee-wizard.config.ts employee-edit-wizard.spec.ts && npx playwright test -c e2e/playwright.browserless.config.ts employee-edit-draft.spec.ts
```

- [ ] **Step 3: Extract edit state and route UI**

Reuse `buildEmployeeEditSnapshot`, dirty-key calculation, changed-field payload, salary breakdown/effective-date validation, approval response handling, and retry state. Remove the frontend named-role conjunction. Register dirty state with the persistent guard.

- [ ] **Step 4: Remove obsolete modal/drawer transitions**

After replacements pass, delete `editOpen`, `autoEditId`, `openEmployeeWindow`, `openFullProfile`, and modal-only close behavior. Update `EmployeeCreatePanel` scrolling to prefer `[data-employee-workspace-scroll]` while retaining dialog compatibility during migration.

- [ ] **Step 5: Run edit and authorization regressions**

```bash
cd frontend && npx playwright test -c e2e/playwright.employee-wizard.config.ts employee-edit-wizard.spec.ts && npx playwright test -c e2e/playwright.browserless.config.ts employee-edit-draft.spec.ts && npx tsc --noEmit
```

Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add frontend/src/views/employees/useEmployeeEditDraft.ts frontend/src/views/employees/EmployeeEditFlow.tsx frontend/src/views/EmployeesPage.tsx frontend/src/components/EmployeeCreateWizard.tsx frontend/e2e/employee-edit-wizard.spec.ts frontend/e2e/employee-edit-draft.spec.ts frontend/e2e/employee-workspace.spec.ts
git commit -m "feat(employees): move editing into workspace"
```

### Task 8: Move direct employee creation into the workspace

**Files:**
- Create: `frontend/src/lib/employeeCreateDraft.ts`
- Create: `frontend/src/views/employees/useEmployeeCreateDraft.ts`
- Create: `frontend/src/views/employees/EmployeeCreateFlow.tsx`
- Modify: `frontend/src/views/EmployeesPage.tsx:1076-1411,2707-3507`
- Modify: `frontend/app/(dashboard)/people/new/page.tsx`
- Modify: `frontend/e2e/employee-create-wizard.spec.ts`
- Modify: `frontend/e2e/employee-create-country-gate.spec.ts`
- Modify: `frontend/e2e/employee-onboarding-completion.spec.ts`
- Modify: `frontend/e2e/employee-workspace.spec.ts`

**Interfaces:**
- Consumes: Task 2 return routes, Task 3 navigation guard, existing `EmployeeCreateWizard` primitives, organization lookups, employee field catalog, duplicate/work-email APIs, and `POST /api/employees`.
- Produces: `/people/new` guided create flow, draft/resume state, and post-create `replace('/people/[newId]')`.

- [ ] **Step 1: Rewrite create tests before production extraction**

Assert Person→Employment→optional Payroll→Salary→Identity→Review traversal, country gate, manual complete work email, advisory-only conflict suggestion, explicit Arabic Use/Dismiss, duplicate view/merge/create-anyway decision, department→cost center, department-scoped managers, failed draft retry, Save draft and exit return, resume at the last section, and successful URL replacement.

- [ ] **Step 2: Verify RED**

```bash
cd frontend && npx playwright test -c e2e/playwright.employee-wizard.config.ts employee-create-wizard.spec.ts employee-onboarding-completion.spec.ts && npx playwright test -c e2e/playwright.browserless.config.ts employee-create-country-gate.spec.ts
```

- [ ] **Step 3: Extract create-only state and UI**

Move, do not duplicate, payload cleaning, duplicate probing, quick/full setup, visited-step rules, country resolution, salary/identity assembly, error mapping, and post-create completion handoff. Keep direct creation separate from `EmployeeDraft` New Hires.

- [ ] **Step 4: Implement draft/resume and route replacement**

Persist the existing Draft employee on Save draft and exit, return through validated `returnTo`, resume with `/people/[id]/edit?section=<last-section>`, and replace `/people/new` with the saved record after final Create.

- [ ] **Step 5: Run create/onboarding/type regressions**

Run the Step 2 commands, then `cd frontend && npx tsc --noEmit`. Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add frontend/src/lib/employeeCreateDraft.ts frontend/src/views/employees/useEmployeeCreateDraft.ts frontend/src/views/employees/EmployeeCreateFlow.tsx frontend/src/views/EmployeesPage.tsx frontend/app/'(dashboard)'/people/new/page.tsx frontend/e2e/employee-create-wizard.spec.ts frontend/e2e/employee-create-country-gate.spec.ts frontend/e2e/employee-onboarding-completion.spec.ts frontend/e2e/employee-workspace.spec.ts
git commit -m "feat(employees): move creation into workspace"
```

### Task 9: Migrate employee links and certify guided responsive interaction

**Files:**
- Modify: `frontend/src/layouts/AppLayout.tsx`
- Modify: `frontend/src/views/NewHiresPage.tsx`
- Modify: `frontend/src/views/PayrollPage.tsx`
- Modify: `frontend/src/views/CompliancePage.tsx`
- Modify: `frontend/src/components/succession/KpiTiles.tsx`
- Modify: `frontend/src/views/EmployeesPage.tsx`
- Modify: `frontend/e2e/employee-workspace.spec.ts`
- Modify: `frontend/e2e/new-hire-review.spec.ts`
- Modify: `frontend/e2e/benefits-additional.spec.ts`
- Modify: `frontend/e2e/release-a-package.spec.ts`
- Modify: `frontend/e2e/release-a-deductions.spec.ts`
- Modify: `frontend/e2e/succession-journey.spec.ts`
- Modify: `frontend/e2e/ui-truthfulness-state.spec.ts`
- Modify: `.github/workflows/ci.yml`

**Interfaces:**
- Consumes: Tasks 2–8 canonical routes and completed workspace.
- Produces: canonical internal links, retained legacy adapter, purposeful motion/guidance, responsive/RTL/a11y evidence, and CI coverage.

- [ ] **Step 1: Add failing link and certification tests**

Assert all known producers use canonical URLs; New Hires preserves write-only/approve-only maker-checker behavior; Overview shows accurate recommended next action; approval/effective-date help is present before Review; motion is interruptible and focus-stable; reduced motion removes translation/shimmer; Arabic sets `dir="rtl"` with logical icons/focus; axe reports no serious/critical violations.

Add geometry assertions at 1440×1000, 390×844, and 320 CSS px/200% zoom: one workspace-height scroller, no document-level horizontal overflow, sticky actions clear bottom nav/assistant/safe area/focused input, and header/navigation/actions remain reachable over long Documents/History.

- [ ] **Step 2: Verify RED**

```bash
cd frontend && npx playwright test -c e2e/playwright.employee-workspace.config.ts
```

- [ ] **Step 3: Migrate link producers and remove the legacy drawer**

Use `employeeWorkspaceHref` everywhere listed. Keep one `/people?employeeId=...` redirect test. Remove the obsolete aside, “Open full profile,” and “Close employee profile” only after replacement tests pass; reduce `EmployeesPage.tsx` to a compatibility export or remove it if unused.

- [ ] **Step 4: Implement guidance and motion from the spec**

Use shared motion primitives and semantic live status. Guidance comes only from authoritative readiness/status/permission data and never claims legal compliance.

- [ ] **Step 5: Add the dedicated workspace suite to CI**

Run it against the same frontend provenance as the other browser gates and retain desktop, mobile reduced-motion, and 320px reflow projects.

- [ ] **Step 6: Run affected suites**

```bash
cd frontend && npx playwright test -c e2e/playwright.employee-workspace.config.ts
cd frontend && npx playwright test -c e2e/playwright.fixture.config.ts employee-access-hr.spec.ts release-a-package.spec.ts release-a-deductions.spec.ts
cd frontend && npx playwright test -c e2e/playwright.benefit-defaults.config.ts benefits-additional.spec.ts
cd frontend && npx playwright test -c e2e/playwright.succession.config.ts succession-journey.spec.ts
cd frontend && npx playwright test -c e2e/playwright.browserless.config.ts
```

Expected: PASS.

- [ ] **Step 7: Run complete project gates**

```bash
dotnet test backend-dotnet/Zayra.Api.Tests/Zayra.Api.Tests.csproj
cd frontend && npm run test:unit && npm run test:unit:tz && npx tsc --noEmit && npm run build && npx playwright test --project=chromium
```

Record every failure by name; do not hide pre-existing failures or claim full completion while any required gate is red.

- [ ] **Step 8: Commit**

```bash
git add frontend/src/layouts/AppLayout.tsx frontend/src/views/NewHiresPage.tsx frontend/src/views/PayrollPage.tsx frontend/src/views/CompliancePage.tsx frontend/src/components/succession/KpiTiles.tsx frontend/src/views/EmployeesPage.tsx frontend/e2e/employee-workspace.spec.ts frontend/e2e/new-hire-review.spec.ts frontend/e2e/benefits-additional.spec.ts frontend/e2e/release-a-package.spec.ts frontend/e2e/release-a-deductions.spec.ts frontend/e2e/succession-journey.spec.ts frontend/e2e/ui-truthfulness-state.spec.ts frontend/e2e/playwright.employee-workspace.config.ts .github/workflows/ci.yml
git commit -m "feat(employees): complete workspace migration"
```

## Self-review record

- Spec coverage: all fourteen spec sections map to Tasks 1–9; permission audit precedes UI; routes/guard precede drafts; directory, view, edit, and create remain separately testable.
- Step scan: every production step follows a named failing test and explicit RED command; no task authorizes implementation before its test failure is observed.
- Type consistency: Tasks 4, 6, and 7 consume the exact Task 2 route types; Tasks 3, 7, and 8 consume the same navigation-guard API; Tasks 6–8 consume Task 4's `EmployeeRecordState` refresh contract.
- Review Focus: each of the five risks is pinned to a named task and assertion.
- Proportion: the plan specifies interfaces, behavior, tests, and commands without transcribing component implementations.
