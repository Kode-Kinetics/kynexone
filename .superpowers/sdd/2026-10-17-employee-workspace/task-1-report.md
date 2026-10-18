# Task 1 report: server-authoritative Employee workspace permissions

## Status

COMPLETE for Task 1. The focused authorization suite and affected adjacent suites are green with no skipped cases. The full backend suite was stopped cleanly at the requested 20-minute cap, so it remains incomplete evidence for the final branch gate.

## Commits

- Implementation: `07e0c0c9` (`fix(employees): enforce workspace permissions`)

## TDD evidence

### RED: permission matrix

Command:

```bash
dotnet test backend-dotnet/Zayra.Api.Tests/Zayra.Api.Tests.csproj --filter "FullyQualifiedName~EmployeeWorkspaceAuthorizationTests" --no-restore
```

Result: FAILED as expected — 18 failed, 31 passed, 0 skipped, 49 total. Failures proved missing explicit read/write/import-template gates, missing conjunctive raw-download authorization, and exposed sensitive document metadata.

### RED: direct document-metadata masking

After adding the metadata assertion and temporarily retaining the old controller projection, command:

```bash
dotnet test backend-dotnet/Zayra.Api.Tests/Zayra.Api.Tests.csproj --filter "FullyQualifiedName~EmployeeWorkspaceAuthorizationTests.SensitiveRead_WithoutSensitivePermission_IsMasked" --no-restore
```

Result: FAILED as expected — the sensitive passport filename was returned instead of an empty masked value.

### GREEN: focused Task 1 suite

Command:

```bash
dotnet test backend-dotnet/Zayra.Api.Tests/Zayra.Api.Tests.csproj --filter "FullyQualifiedName~EmployeeWorkspaceAuthorizationTests" --no-restore
```

Result: PASSED — 50 passed, 0 failed, 0 skipped.

### GREEN: required adjacent suites

Command:

```bash
dotnet test backend-dotnet/Zayra.Api.Tests/Zayra.Api.Tests.csproj --no-restore --filter "FullyQualifiedName~EmployeeWorkspaceAuthorizationTests|FullyQualifiedName~EmployeeAuthzRegressionTests|FullyQualifiedName~SensitiveFieldMaskingTests|FullyQualifiedName~EmployeeBulkActionTests|FullyQualifiedName~EmployeeAccessTests|FullyQualifiedName~EmployeeCompletionHttpTests"
```

Result: PASSED — 180 passed, 0 failed, 0 skipped in 18 seconds.

### Full backend suite and reconciliation

Command:

```bash
dotnet test backend-dotnet/Zayra.Api.Tests/Zayra.Api.Tests.csproj --no-build --no-restore
```

Result: INCOMPLETE — cancelled cleanly after 20 minutes while the test host remained CPU-active. Before cancellation it exposed two failures:

1. `DocumentStorageSecurityTests.EmployeeDownload_UsesProviderNeutralBytes_WhenResolvePathIsUnsupported` lacked the newly required `employees.read` and `employees.documents` fixture claims.
2. `LegacyRoleGateBypassSweepTests.EveryRoleGateBypass_IsReviewed_AndNoNewOneAppears` showed that the `HR Director` import-template allow-list entry must remain because that role legitimately has `employees.bulk_import`.

Both were corrected. Exact reconciliation command:

```bash
dotnet test backend-dotnet/Zayra.Api.Tests/Zayra.Api.Tests.csproj --no-restore --filter "FullyQualifiedName~DocumentStorageSecurityTests.EmployeeDownload_UsesProviderNeutralBytes_WhenResolvePathIsUnsupported|FullyQualifiedName~LegacyRoleGateBypassSweepTests.EveryRoleGateBypass_IsReviewed_AndNoNewOneAppears"
```

Result: PASSED — 2 passed, 0 failed, 0 skipped.

Final combined affected gate:

```bash
dotnet test backend-dotnet/Zayra.Api.Tests/Zayra.Api.Tests.csproj --no-build --no-restore --filter "FullyQualifiedName~EmployeeWorkspaceAuthorizationTests|FullyQualifiedName~EmployeeAuthzRegressionTests|FullyQualifiedName~SensitiveFieldMaskingTests|FullyQualifiedName~EmployeeBulkActionTests|FullyQualifiedName~EmployeeAccessTests|FullyQualifiedName~EmployeeCompletionHttpTests|FullyQualifiedName~DocumentStorageSecurityTests|FullyQualifiedName~LegacyRoleGateBypassSweepTests"
```

Result: PASSED — 190 passed, 0 failed, 0 skipped in 22 seconds.

## Changed files and hunks

- `backend-dotnet/Zayra.Api/Controllers/EmployeesController.cs`
  - Added explicit `employees.read` gates to list/archive/detail/catalog/org-chart/reporting-line/hierarchy/workflow-approver/document-metadata/history/readiness reads.
  - Added `employees.bulk_import` to import-template and `employees.write` to update.
  - Enforced raw download imperatively as `employees.read` AND `employees.documents`, with `employees.sensitive` also required for Identity or Payroll categories.
  - Passed the effective sensitive permission into every document DTO projection used by employee/draft document responses.
- `backend-dotnet/Zayra.Api/Application/Employees/EmployeeManagementDtos.cs`
  - Made sensitive Identity/Payroll document projection permission-aware and masked protected metadata without removing workflow/status/category identity.
  - Propagated employee-detail `includeSensitive` into document projection.
- `backend-dotnet/Zayra.Api.Tests/Security/EmployeeWorkspaceAuthorizationTests.cs`
  - Added the required named, table-driven endpoint/action tests, raw-download AND matrix, sensitive-category cases, production authorization-pipeline checks, data-scope checks, and body-dependent bulk-action checks.
- `backend-dotnet/Zayra.Api.Tests/Security/EmployeeAuthzRegressionTests.cs`
  - Made the legacy role assertion select the role-bearing `AuthorizeAttribute` when permission-derived authorization attributes are also present.
- `backend-dotnet/Zayra.Api.Tests/Security/LegacyRoleGateBypassAllowList.cs`
  - Removed only import-template bypasses invalidated by the new local permission gate; retained the legitimate HR Director permission case.
- `backend-dotnet/Zayra.Api.Tests/DocumentStorageSecurityTests.cs`
  - Reconciled the provider-neutral download fixture with the new conjunctive read/document requirements.

## Dirty-worktree preservation

The initial branch and dirty state were inspected before edits. `EmployeesController.cs` already contained unrelated user work, especially work-email validation/derivation changes. Only Task 1 controller hunks were applied to the Git index through a generated zero-interaction filtered patch (`git diff ... | awk ... | git apply --cached`). All other Task 1 files were staged explicitly by exact path. The cached diff was reviewed with `git diff --cached`, checked with `git diff --cached --check`, and the resulting commit was inspected with `git show`. The unrelated controller changes and every other pre-existing modification/untracked file remain unstaged and were not reset, stashed, switched, or absorbed.

## Concerns

- The full backend suite did not produce a final result within 20 minutes. The final branch gate must rerun it; only the two failures emitted before cancellation are known, and both now have green targeted coverage.
- The worktree remains intentionally dirty with extensive pre-existing user changes. Any later integration must continue to avoid broad staging.

## Fix round 1: independent-review findings

### Status and commit

COMPLETE for the three review findings. Implementation commit: `a11ec768` (`fix(employees): close workspace authorization gaps`).

### RED evidence

Sensitive classification and employee/company/team scope tests were added first and run with:

```bash
dotnet test backend-dotnet/Zayra.Api.Tests/Zayra.Api.Tests.csproj --filter "FullyQualifiedName~EmployeeWorkspaceAuthorizationTests" --no-restore
```

Result: FAILED — 23 failed, 59 passed, 0 skipped, 82 total. The failures demonstrated category-free Passport metadata/raw-byte exposure, missing employee/company scope checks across lifecycle/document/transfer actions, and tenant-wide org-chart output. Two first-pass null-category fixtures also exposed an EF InMemory setup limitation; those fixtures were corrected to model the legacy null row without changing the intended authorization assertion.

The existing bulk action implementation already used the correct permission map, so the strengthened exact-positive/cross-permission test was mutation-validated. `activate` was temporarily mapped to `employees.delete`, then this exact test was run:

```bash
dotnet test backend-dotnet/Zayra.Api.Tests/Zayra.Api.Tests.csproj --filter "FullyQualifiedName~EmployeeWorkspaceAuthorizationTests.BulkAction_UsesItsOwnPermission" --no-restore
```

Result with the deliberate mutation: FAILED — 1 failed, 3 passed, 0 skipped, 4 total. The exact-positive `employees.approve` case caught the wrong permission. The production mapping was restored immediately.

### GREEN evidence

Final focused authorization command:

```bash
dotnet test backend-dotnet/Zayra.Api.Tests/Zayra.Api.Tests.csproj --no-build --no-restore --filter "FullyQualifiedName~EmployeeWorkspaceAuthorizationTests"
```

Result: PASSED — 86 passed, 0 failed, 0 skipped in 2 seconds.

Final bulk matrix command after restoring the correct implementation:

```bash
dotnet test backend-dotnet/Zayra.Api.Tests/Zayra.Api.Tests.csproj --filter "FullyQualifiedName~EmployeeWorkspaceAuthorizationTests.BulkAction_UsesItsOwnPermission" --no-restore
```

Result: PASSED — 4 passed, 0 failed, 0 skipped.

Final affected security/regression command:

```bash
dotnet test backend-dotnet/Zayra.Api.Tests/Zayra.Api.Tests.csproj --no-restore --filter "FullyQualifiedName~EmployeeWorkspaceAuthorizationTests|FullyQualifiedName~EmployeeAuthzRegressionTests|FullyQualifiedName~SensitiveFieldMaskingTests|FullyQualifiedName~EmployeeBulkActionTests|FullyQualifiedName~EmployeeAccessTests|FullyQualifiedName~EmployeeCompletionHttpTests|FullyQualifiedName~DocumentStorageSecurityTests|FullyQualifiedName~LegacyRoleGateBypassSweepTests|FullyQualifiedName~HrmHierarchyTests"
```

Result: PASSED — 250 passed, 0 failed, 0 skipped in 18 seconds. The first run of this set correctly exposed one stale masking fixture whose `Manager` principal had no linked employee and therefore no resolved data scope. The fixture now uses an organization-scoped `HR Officer` without `employees.sensitive`, preserving the masking assertion while satisfying the new authorization boundary.

### Fix-round changed files and hunks

- `backend-dotnet/Zayra.Api/Application/Employees/EmployeeManagementDtos.cs`: added a normalized, server-owned identity/payroll classifier and used document type plus category for fail-closed metadata projection.
- `backend-dotnet/Zayra.Api/Infrastructure/Employees/EmployeeManagementService.cs`: canonicalized upload and metadata-update categories; known sensitive document types override blank or misleading client categories.
- `backend-dotnet/Zayra.Api/Controllers/EmployeesController.cs`: applied the shared employee data/company-scope check across audited reads and mutations, used the classifier for raw downloads and draft documents, and passed a resolved visible employee set to org-chart projection.
- `backend-dotnet/Zayra.Api/Application/Organization/IHrmHierarchyService.cs` and `backend-dotnet/Zayra.Api/Infrastructure/Organization/HrmHierarchyService.cs`: added an optional trailing visible-employee-ID contract and filtered the employee source before tree construction. Existing callers remain source-compatible.
- `backend-dotnet/Zayra.Api.Tests/Security/EmployeeWorkspaceAuthorizationTests.cs`: added alias/null/casing classification tests, restricted-scope action-family tests, company- and team-scoped org-chart tests, and exact positive plus cross-permission bulk matrices with seeded in-scope employees.
- `backend-dotnet/Zayra.Api.Tests/Security/SensitiveFieldMaskingTests.cs`: reconciled the HR-transfer masking fixture with the newly enforced data-scope boundary.

### Fix-round preservation method and concerns

`EmployeesController.cs` and `EmployeeManagementService.cs` both contained unrelated pre-existing work. Only fix-round hunks were selected into the index with generated noninteractive patches (`git diff ... | awk ... | git apply --cached`); the remaining fix files were staged by exact path. `git diff --cached --check` passed, and commit `a11ec768` contains only the seven files listed above. Unrelated tracked and untracked work remains in place and unstaged.

The full backend suite was not rerun in this fix round because the earlier run exceeded the explicit 20-minute cap; the final branch gate still owns that run. The optional `IHrmHierarchyService.GetOrgChartAsync` visibility parameter is the only domain-interface change and was reported before commit.

## Fix round 2: production field-catalog document types

### Status and commit

COMPLETE for the remaining re-review finding. Implementation commit: `3ba8b40e` (`fix(employees): classify catalog document types`).

### RED evidence

Tests first added the exact current `documentTypesForCountry` remote/fallback statutory labels and keys, sensitive payroll catalog labels/keys, the normal blank-category `Passport Number` upload payload, metadata masking, and raw-download denial. Command:

```bash
dotnet test backend-dotnet/Zayra.Api.Tests/Zayra.Api.Tests.csproj --no-restore --filter "FullyQualifiedName~ProductionCatalogSensitiveDocumentType_WithBlankCategory|FullyQualifiedName~ProductionPassportNumberUpload_WithoutClientCategory"
```

Result before the registry change: FAILED — 59 failed, 7 passed, 0 skipped, 66 total. `Passport Number` canonicalized to an empty category, the upload persisted it as non-sensitive, and the other unregistered exact catalog values failed the same behavior assertions.

An additional server-catalog drift guard was then added and run with:

```bash
dotnet test backend-dotnet/Zayra.Api.Tests/Zayra.Api.Tests.csproj --no-restore --filter "FullyQualifiedName~ServerCatalog_SensitiveIdentityAndPayrollLabelsAndKeys_AreProtectedDocumentTypes"
```

Result: FAILED — 1 failed, 0 passed, 0 skipped. It identified `PassportExpiryDate` as the first sensitive server-catalog key still absent from the exact registry; inspection identified and added its label/key and the corresponding `VisaExpiryDate` label/key.

### GREEN evidence

Exact production-label/key, upload, metadata, and raw-download cases:

```bash
dotnet test backend-dotnet/Zayra.Api.Tests/Zayra.Api.Tests.csproj --no-restore --filter "FullyQualifiedName~ProductionCatalogSensitiveDocumentType_WithBlankCategory|FullyQualifiedName~ProductionPassportNumberUpload_WithoutClientCategory"
```

Result: PASSED — 66 passed, 0 failed, 0 skipped in 1 second.

Server-catalog drift guard plus explicit non-substring cases:

```bash
dotnet test backend-dotnet/Zayra.Api.Tests/Zayra.Api.Tests.csproj --no-restore --filter "FullyQualifiedName~ServerCatalog_SensitiveIdentityAndPayrollLabelsAndKeys_AreProtectedDocumentTypes|FullyQualifiedName~SensitiveDocumentTypeRegistry_DoesNotUseSubstringMatching"
```

Result: PASSED — 3 passed, 0 failed, 0 skipped.

Final focused authorization suite:

```bash
dotnet test backend-dotnet/Zayra.Api.Tests/Zayra.Api.Tests.csproj --no-build --no-restore --filter "FullyQualifiedName~EmployeeWorkspaceAuthorizationTests"
```

Result: PASSED — 154 passed, 0 failed, 0 skipped in 2 seconds.

Final affected security/catalog regression suite:

```bash
dotnet test backend-dotnet/Zayra.Api.Tests/Zayra.Api.Tests.csproj --no-build --no-restore --filter "FullyQualifiedName~EmployeeWorkspaceAuthorizationTests|FullyQualifiedName~EmployeeAuthzRegressionTests|FullyQualifiedName~SensitiveFieldMaskingTests|FullyQualifiedName~EmployeeBulkActionTests|FullyQualifiedName~EmployeeAccessTests|FullyQualifiedName~EmployeeCompletionHttpTests|FullyQualifiedName~DocumentStorageSecurityTests|FullyQualifiedName~LegacyRoleGateBypassSweepTests|FullyQualifiedName~HrmHierarchyTests|FullyQualifiedName~EmployeeFieldWiringTests"
```

Result: PASSED — 345 passed, 0 failed, 0 skipped in 19 seconds.

### Fix-round changed files, preservation, and concerns

- `backend-dotnet/Zayra.Api/Application/Employees/EmployeeManagementDtos.cs`: replaced hand-normalized short tokens with an explicit server-owned registry of exact current remote/fallback catalog labels, stable keys, and supported legacy aliases. Matching remains punctuation/case-normalized equality and never uses substring or free-form `Contains` logic.
- `backend-dotnet/Zayra.Api.Tests/Security/EmployeeWorkspaceAuthorizationTests.cs`: added exact production label/key metadata and raw-byte tests, blank-category `Passport Number` upload coverage, a server-catalog drift guard, and non-substring negative cases.

Both changed files were clean at fix-round start and were staged by exact path. `git diff --cached --check` passed; commit `3ba8b40e` contains only these two files. All unrelated tracked and untracked changes remain unstaged and untouched.

Per the round instruction, the full backend suite was not run. Builds emitted pre-existing compiler/analyzer warnings, but every requested focused and affected scoped test completed with zero failures and zero skips.
