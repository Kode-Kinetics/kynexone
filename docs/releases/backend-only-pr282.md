# Isolated backend release from PR #282

The source PR is `ad20a9ee584bd5c9c9640240189f000c939b78af`; its original main base is `74d931129548cae5059ea34941dddd2cda631411`. The candidate branch is `release/backend-only-pr282`, rebased on publication main `5d7d4dc666543fa7e2272e48b3adefa29c285ef3` after maintenance PR #288 normally merged with all eight required checks passing. Its four dependency-security files are inherited from that base, not backend candidate changes. The source inventory still compares the original main/source SHAs above; it remains exactly 931 paths.

## Included behavior

- Existing Qiwa token acquisition, employee push and status calls propagate caller cancellation instead of recording a false network failure. Independent HTTP timeouts remain handled failures. The existing named HttpClient has a 30-second timeout; the live-adapter activation/partner-agreement policy is unchanged and no live integration is enabled.
- Backend CI separates unit and four populated integration shards while compiling the Release graph once. Exact-checkout/run/attempt provenance and complete archive/file hashes are validated before reuse. Empty/malformed discovery, unsafe archives, zero-executed/missing/failed results and incomplete aggregate evidence fail closed.
- Every non-backend main job and protected deployment dependency stays intact. Chrome retains the same revision-marked independent build with periodic diagnostics. Schema/EF keeps its fresh Debug builds. CodeQL traces an independent manual C# application build without path exclusions or artifact reuse. Main compiler policy is unchanged.

Main originally compiled its combined backend lane once. Reuse avoids five compilations when splitting that lane; it does not claim a five-to-one reduction compared with main. Historical source-PR runner shutdowns have no established cause, and no compiler lifetime/GC/runner change is introduced here.

## Complete groups reserved in the original PR

The exact included/adapted/deferred source-path inventory is `backend-only-pr282-scope.json`. No omitted source implementation is deleted or partially deployed.

| Group | Why it remains in #282 |
| --- | --- |
| Payroll background processing and salary/first-package staging | Source changes synchronous main contracts to 202 job/proposal responses; existing clients cannot consume the complete new workflow. Main's #283 independent Grade choice, audited exceptions and activation checks remain intact. |
| Large leave-adjustment proposals, payroll rules, setup/removal/code reuse, below-floor acknowledgements | Shared business, client, model and migration dependencies require complete coordinated releases. |
| Field encryption/session cache/audit guards/device ingest/bridge/custom bank templates/optional observability | These are separate feature graphs, some sharing DbContext/model/designer snapshots and operational configuration. No graph is partially copied. |
| Complete CSV group | Source whitespace unescape changes legitimate raw main input `'  =note`. An attempted reversible apostrophe encoding also changes raw `''=note`. Actual-source native probes reproduced both differences; main reference preserves both inputs. No new CSV protocol is introduced. All main CSV helpers, controllers, tests and bank/WPS/ERP formats are unchanged. |
| Complete compression group | The source auth-prefix policy does not cover all credential replies, including CMI5 authentication and ERP/attendance keys or invitation/reset links. Global HTTPS compression needs a separate complete protected-response policy. Main HTTP middleware/security behavior stays unchanged. |
| Frontend/mobile product work and source-only operational/analysis exclusions | Out of this candidate's implementation scope; main client sources and existing scan/browser/deployment checks are preserved. Separate maintenance #288 changes only its reviewed dependency-security files. |

## Local verification and limits

- 29 Python regression methods passed, including corruption/provenance/shard/TRX/cancellation cases and scope-verifier failure fixtures. The source-specific compiler-policy assertion was not imported because main has no such target; resource/privacy coverage is retained.
- All-workflow actionlint and `git diff --check` passed.
- A bounded native probe linking actual unchanged production dependencies reproduced swallowed Qiwa caller cancellation before extraction, then passed after extraction.
- The exact new `QiwaTransportBoundaryTests.cs`, linked with its actual production source dependencies in a separate focused test project, passed all six xUnit cases: token/push/status caller cancellation and independent timeout behavior. VSTest 17.11.1 generated a real TRX, and the new evidence validator accepted its six executed passing results. This focused project does not replace a full application build or test run.
- All tracked publication-main Controllers/Application/Models/Domain/Data/Migrations/frontend/mobile/existing .NET tests were byte-compared unchanged after rebase; the inherited maintenance dependency fix is not a backend delta. Program differs only in the named Qiwa timeout. No application package or schema change is present.

Commands used (workspace-local SDK is `/workspace/.setup-kynexone/dotnet`):

```sh
python3 -m unittest discover -s tests/ci -v
/workspace/scratch/actionlint -shellcheck= -pyflakes= .github/workflows/*.yml
git diff --check
python3 scripts/release/verify-backend-only-scope.py
/workspace/.setup-kynexone/dotnet/dotnet test /workspace/scratch/backend-release/transport-tests/tests.csproj --no-restore -c Release --results-directory /workspace/scratch/backend-release/transport-results --logger 'trx;LogFileName=qiwa.trx'
python3 scripts/ci/verify-test-results.py /workspace/scratch/backend-release/transport-results/qiwa.trx
```

No massive local application build or repeated compiler-knob experiment was performed for this candidate. Full application build/unit/integration coverage, real compiled-output handoff, fresh/upgrade/drift schema, Chrome, CodeQL, dependency and all other required checks must pass on the exact published candidate. Old source-PR or maintenance-baseline checks are not candidate verification.

The Release artifact is attempt-scoped: a failed-jobs-only retry without a same-attempt producer or all five result artifacts fails closed; rerun all jobs. No fallback to old assemblies/evidence is permitted.

## Publication and deployment

Maintenance PR #288 has normally merged at publication base `5d7d4dc666543fa7e2272e48b3adefa29c285ef3` after its eight required checks passed. The unpublished backend candidate was rebased without runtime changes; ledger/static/scope checks are refreshed against that actual main. Obtain final delta review and publish the backend candidate. The automatic main production workflow still enforces its existing full deployment gates. Only exact-head passing required checks and normal protected migration/release gates authorize its merge and deployment. Source #282/#287 are not merged wholesale. Existing deployment parity can rebuild unchanged frontend source on a main push; backend-only here describes source scope, not a changed deployment policy. No mobile store/OTA release, protection bypass or production configuration change is part of this work.
