# Backend test evidence in CI

`Backend Release build` restores and compiles the API and test project once. The unit lane and all four integration shards download that same Release output graph, verify it, restore their own NuGet dependencies, then run without compiling. Each runner retains a full checkout: tests read repository-relative source, SQL, documentation, fixtures and frontend contracts in addition to compiled output.

The archive contains both projects' Release `bin`/`obj` files and top-level restore assets, including testhost, runtime dependencies, copied fixtures, native libraries, executable modes and PDBs for coverage. It excludes `TestResults`, Debug outputs and unrelated repository files. The manifest binds the checked-out commit (including a synthetic PR merge commit), workflow run, run attempt, platform and architecture. Consumers verify the archive SHA-256, exact member inventory, safe paths and every file SHA-256 before writing any output. Missing or mismatched artifacts fail the lane.

**"Re-run failed jobs" works for any consumer.** Release and result artifacts are named per commit and run (`backend-release-<sha>-<run_id>`, `backend-results-<lane>-<sha>-<run_id>`), not per run attempt, because a failed-jobs re-run starts a new attempt without re-running the producer. A re-run producer or lane replaces its own artifact (`overwrite: true`). Staleness is prevented in layers: the build manifest binds commit, run id, platform and configuration and is rejected if it was produced by a *later* attempt than the consumer; every file is hash-verified; `Backend Tests (security gate)` requires every lane job's *current* result to be success before it reads the results, so an artifact left by an earlier attempt can only belong to a lane that passed there (a failing or re-run lane has its result and artifact replaced together, and an upload that fails fails the job). Artifact retention is seven days for compiled output (GitHub also limits re-runs to 30 days) and fourteen days for results/coverage; a re-run after the build artifact expired fails at download and needs "Re-run all jobs".

`Category!=Integration` retains all unit, architecture and other non-integration categories. `Category=Integration` discovery assigns whole classes greedily by test count with deterministic ties across four populated shards. Unrecognized discovery, invalid arguments, empty inventories/shards and class-substring ambiguity fail before a filter is emitted. No unmatched-filter fallback exists.

Every populated lane requires a well-formed TRX with at least one executed passing test and matching result entries/counters. VSTest reports its skipped summary counter as zero even with skipped entries; the validator accepts that placeholder and checks actual entries against total/executed counts. `Backend Tests (security gate)` also requires successful change detection, the build, unit and matrix jobs, then independently verifies this run's unit and all four shard result artifacts. A missing shard cannot pass through a reduced matrix. Only explicitly allowlisted top-level `README.md` and `LICENSE` changes may skip; documentation and fixtures elsewhere can be executable test contracts.

Schema/EF checks retain independent Debug builds, including the rebuild that detects stale migration assemblies. Chrome retains its own revision-marked Release build, and CodeQL retains its traced build. These lanes do not consume the test artifact.

Periodic build diagnostics record bounded memory/cgroup counters, numeric dotnet residency, executable basenames and disk usage without environment values, full process arguments or binary build logs. Successful same-head hosted builds have reached roughly 16.2 GB cgroup high water with zero OOM counters, but failed runner shutdowns lack termination-time samples. Reusing one Release compilation removes four duplicate compilations; it does not establish why the previous runners stopped or prove that shutdowns are fixed.

Run script regressions with:

```sh
python3 -m unittest discover -s tests/ci -v
```

No third-party Python dependency is required.
