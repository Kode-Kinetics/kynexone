# PR #282 backend CI correction evidence

Inspected and tested in the isolated `kynexone-backend-fixes` workspace on 2026-10-29. Source base: `cca1f559e07498f864762d9499444f95914af419`; main comparison: `74d931129548cae5059ea34941dddd2cda631411`. The resulting commit and fresh hosted checks belong in the PR ledger; this document records the local correction and verification.

## Confirmed defects

- Empty/unrecognized integration discovery previously returned an intentionally unmatched filter with exit zero. Invalid shard numbers also succeeded. Discovery now requires the recognized VSTest inventory, valid arguments, enough classes for every shard, and unambiguous class predicates. It emits no filter on error. Whole-class allocation remains deterministic and balanced by test-case count.
- The documentation shortcut excluded documentation and SQL read by real tests, including both cost-accounting schema documents and the rollback runbook. The shortcut now allows only top-level `README.md` and `LICENSE`; other documentation, source fixtures and executable inputs require the lanes.
- Successful test process exits were accepted without executed-test evidence. Every populated lane now validates its expected TRX, and the required aggregate independently requires the exact checkout/run/attempt's unit result plus all four shard results. Missing, malformed, zero-executed or unsuccessful evidence fails closed. Change detection and build/lane statuses also fail closed.

## Build investigation

The failed hosted retry on base `cca1f559` stopped during API compilation without termination-time resource samples. Successful same-head builds took about eleven minutes and reached approximately 16.2 GB cgroup high water with zero OOM counters. Those observations support removing duplicate builds but do not prove the failed runners were killed for memory exhaustion.

The local setup snapshot contained main's restore assets. An initial PR build therefore failed on missing Sentry references; refreshing the current PR's dependencies restored both projects successfully in 20 seconds. No source fix was needed for those stale setup outputs.

A fresh local Release rebuild with the existing CI settings reproduced a managed compiler `Out of memory` error after 333.4 seconds. Bounded 30-second samples recorded compiler RSS up to 12,889,892 KiB and cgroup high water of 13,787,918,336 bytes against a 17,179,869,184-byte limit; kernel OOM counters remained zero. A small runtime probe reported four processors, server GC and a 12,884,901,888-byte managed heap budget. This establishes a local compiler allocation failure; it does not retrospectively diagnose the hosted shutdowns.

A compiler-only workstation-GC experiment timed out at 600.9 seconds without a successful outcome; it is not a demonstrated mitigation. A clean compiler-only processor-count hint then failed explicitly with `Out of memory` after 569.8 seconds (MSBuild: 9m29.49s), with standalone child maximum RSS 12,839,360 KiB and maximum sampled cgroup current memory 13,705,170,944 bytes. Kernel OOM counters remained zero. Neither experimental setting is shipped: `Directory.Build.targets` and its existing policy test are unchanged from the base. Two short overlapping partial attempts caused by shutdown/timeout cleanup were stopped and discarded. The clean successor starts only after all previous compiler descendants have exited, refuses preexisting live dotnet processes and owns a dedicated process group with TERM/KILL deadline cleanup. No measurements from the overlapping partial attempts are used.

## Verification commands

The workspace uses .NET SDK 8.0.427/runtime 8.0.33. Prepend `/workspace/.setup-kynexone/dotnet` to `PATH` when reproducing native commands here.

```sh
python3 -m unittest discover -s tests/ci -v
/workspace/scratch/actionlint -shellcheck= -pyflakes= .github/workflows/ci.yml
git diff --check
dotnet restore backend-dotnet/Zayra.Api.Tests/Zayra.Api.Tests.csproj
GITHUB_ACTIONS=true dotnet build backend-dotnet/Zayra.Api.Tests/Zayra.Api.Tests.csproj --no-restore -c Release -t:Rebuild -m:2 -v:minimal
```

Regression tests were written first. The original shard/discovery/docs/status behavior produced 25 failing assertions across the first ten methods. Archive handoff, all-five-result aggregation, resource-wrapper exit preservation, cancellation/descendant cleanup and ambiguous class matching each had an observed red run before correction. Independent review also caught the VSTest logger's always-zero `notExecuted` placeholder; a pinned-real-shape mixed pass/skip regression failed before compatibility was corrected.

Final local verification:

| Check | Result |
| --- | --- |
| `python3 -m unittest discover -s tests/ci -v` | 27 methods passed; includes negative discovery/arguments, deterministic disjoint allocation, real Git docs classification, required-gate statuses/evidence, zero/failed/malformed TRX, genuine VSTest mixed-skip shape, archive provenance/hashes/unsafe entries and cancellation cleanup |
| Actionlint v1.7.7 on `ci.yml` | Passed; independent reviewer also checked all workflows |
| `git diff --check` | Passed |
| Current-PR dependency restore | Both projects restored successfully |
| Fresh existing-policy Release rebuild | Managed compiler OOM at 333.4s; no kernel OOM event |
| Workstation-GC diagnostic experiment | Bounded timeout at 600.9s; no successful compile; not shipped |
| Processor-count diagnostic experiment | Managed compiler OOM at 569.8s; not shipped |
| Authentic historical same-head unit TRX | New validator accepted 9,720 passed/zero skipped from run `40914887538`, artifact `12922394171`; schema compatibility evidence, not a new test execution |
| Managed Docker socket | Server 28.4.0 reachable |
| Retained prebuilt Testcontainers 3.10.0 infrastructure probe | Disposable PostgreSQL 16-alpine started; temporary-table transaction returned 42, rolled back, and container disposed; exit 0 |

The authentic historical TRX was checked with `python3 scripts/ci/verify-test-results.py /workspace/scratch/backend-fix/base-cca1f55-unit.trx`. It was extracted from the existing [same-head CI run](https://github.com/Kode-Kinetics/kynexone/actions/runs/40914887538); no old successful run is represented as final-commit verification.

The PostgreSQL probe executes a prebuilt setup helper outside the repository; it is current Docker/database infrastructure evidence, not application or artifact verification.

The cloud compiler allocation limit prevented a successful build of the current PR. Consequently **native application test discovery, actual four-shard predicate inventory comparison, compiled-output artifact package/restore, unit/integration coverage/TRX runs, application PostgreSQL fixtures, and EF/schema checks were not executed successfully for this patch locally**. Archive regressions use synthetic fixture files and real tar/hash operations; they do not substitute for a compiled testhost handoff. No local passing .NET integration claim is made.

A separate pristine-base monitor reported a successful all-source/analyzer Release rebuild on a 64 GiB/12-CPU Mac with SDK 8.0.421: 1m49s, zero errors, 81 warnings, maximum RSS 19,150,602,240 bytes; the three cost-accounting documentation tests passed. That is base `cca1f559` evidence on a different platform, SDK and memory accounting, not verification of the final commit or proof of the hosted shutdown cause. See [the independent monitor evidence](https://github.com/Kode-Kinetics/kynexone/pull/282#issuecomment-6508043979).

Both independent specialists reviewed the final scripts/workflow and reran the Python regressions. The xUnit/PostgreSQL/EF reviewer verified category/fixture/source and independent Debug contracts; the CI/security reviewer checked gate state combinations, archive attacks/provenance and workflow structure. Neither reported a remaining code blocker. The user explicitly authorized a static-reviewed, exact-base commit/patch fallback when the cloud compiler limit blocks native verification. Fresh exact-final-commit CI and/or the owner's 64 GiB Mac build remain required verification, reported separately in the PR ledger.

## Operational contract

Release compilation moves from five independent builds to one immutable, exact-checkout/run/attempt artifact. Complete Release API/test output, restore assets, PDBs, testhost and copied fixtures are preserved and hash-verified before extraction. Source-relative tests retain full checkouts. Debug EF freshness checks, independently revision-marked Chrome compilation, CodeQL extraction and all existing schema/release/security dependencies retain their own contracts.

**A failed-jobs-only retry cannot consume an earlier attempt's build or result artifacts. Rerun all jobs when the new attempt has no producer or complete evidence.** This is an intentional fail-closed constraint, not a missing-artifact fallback.

Changed files are `.github/workflows/ci.yml`; the existing `scripts/ci/shard-tests.sh` wrapper; new `scripts/ci/{shard-tests.py,verify-test-results.py,verify-backend-results.py,backend-build-artifact.py,run-with-resource-snapshots.py}`; new `tests/ci/{test_backend_test_gates.py,test_backend_artifact.py,test_resource_monitor.py}`; and `docs/ci/{BACKEND_TESTS.md,BACKEND_CI_FIX_EVIDENCE.md}`.

No application implementation, migrations/model snapshots, mobile files, production environments, deployment settings or optional encryption/observability were changed. No merge or deployment is authorized by these results. See [the backend test contract](BACKEND_TESTS.md) for the maintained handoff details.
