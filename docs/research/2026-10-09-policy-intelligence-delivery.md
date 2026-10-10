# Policy to setup to Kody

Integration owner: main agent. Bounded specialist tracks: backend/security; HR/finance/audit acceptance; market research/frontend. No independent agent owns schema or production release.

## Product target

Use an organization's existing HR policies to prepare reviewable configuration, retain approved documents, and explain applicable policy through Kody. Match documented competitor capabilities before positioning the combined experience as a differentiator. See the market evidence and acceptance files in this directory.

## Implemented increment

- Reuse PDF/DOCX/TXT parsing and the existing policy corpus. Preserve normalized source text and its SHA-256 fingerprint. Reject empty/scanned/unsupported input rather than pretending OCR succeeded.
- Separate draft processing from company-wide employee publication, with effective dates, explicit permission checks, stale-content checks and optimistic concurrency.
- Use the existing configured AI provider and call recorder for sourced, typed setup proposals. Include Ollama without automatic cross-provider fallback. Reject unknown destinations, incomplete objects, unsupported runtime values and absent source excerpts.
- Present suggestions unchecked with before/after review. Preserve accepted field source references across sections. Source references identify input origins; they do not prove the final edited setting is semantically mandated by the quoted clause.
- Reuse the established setup Generate/Apply path, module authorization, transaction, validation and final-draft audit. Do not activate a second configuration writer.
- Add employee Kody policy mode using active employment, company, publication and effective-date filtering before retrieval. Show excerpts and version fingerprints. An uploaded draft is not available to employees.

## Remaining work before the full product target

1. Durable, resumable whole-handbook extraction with clause boundaries, chunk merge/conflict handling, progress and explicit cost limits. Current extraction fits one configured model context and rejects oversized input without truncation.
2. Original-file retention/version family and source page anchors; scan OCR quality review. Current fingerprint covers extracted text, and citations identify text offsets/chunks rather than original pages.
3. Fine-grained document/section audiences by grade, department, location and employment category. Current publication is explicitly whole-document, whole-company.
4. Arabic/English semantic retrieval and measured answer/citation quality. Current keyword retrieval is bounded but is not cross-language semantic search.
5. Approval separation, policy version supersession, acknowledgment, review reminders and documented retention/export workflows. Current publication is an authorized HR action, not an independent maker-checker approval chain.
6. Changes to approved policies should produce configuration differences and employee/finance impact scenarios, then an effective-dated change set. Do not retroactively rewrite closed payroll or leave ledgers.
7. Complete runtime feature gaps independently: anniversary/custom leave periods, automated accrual/rollover, nonmonthly salary calendars, executable approval routing and benefit contribution/enrollment rules. Until those engines exist, extraction must mark related requirements unsupported.
8. Connect authorized personal facts to policy explanations and HR escalation. Current employee policy Q&A does not calculate personal entitlements or trigger operations.

No production release or comprehensive competitor parity is claimed by this increment. Acceptance requires database migration, backend/frontend parity, real personas and actual model evaluation in the target environment.

## Verification evidence

- Frontend production build passed. Frozen-build browser suite: 49 passed, three intentional desktop-only skips in the mobile project, exit 0. Translation checks: eight passed, one intentional repin skip. Logs: `/tmp/policy-frontend-build-final.log`, `/tmp/policy-browser-all-final.log`, `/tmp/policy-i18n.log`. Browser APIs are intercepted fixtures; these tests establish UI behavior, not a live frontend/API integration.
- Desktop fit verified for company details and policy guide at 1728×900, 1440×900 and 1366×768; all five default setup steps at 1728×900 and 1440×900. Expanded extraction evidence and mobile retain natural scrolling. Root visually inspected readable benefit proposals and the employee Kody citation drawer. Screenshots: `/tmp/kynex-setup-experience-results/`. Less-common extracted enum labels still need copy polish.
- Backend focused suite: 83 passed, zero failed or skipped. Includes real HTTP authorization/lifecycle checks, native document parsing, PostgreSQL publication concurrency, migration upgrade/replay/guarded rollback, and source validation/audit on setup application. Local log: `/tmp/policy-governance-tests-full.log`.
- Migration discovery gate rebuilt the current assembly: all 95 migration files are visible to EF. Local log: `/tmp/policy-migration-visibility.log`.
- Actual local Ollama invocation with installed `qwen3-coder:30b`, synthetic policy text and an 8,192-token configured context: four supported proposals with matching source offsets in 64.075 seconds. An earlier probe exposed invented defaults from absent information; the prompt, evidence guard and regression tests were tightened before this successful rerun. Local log: `/tmp/policy-ollama-probe-final.log`. This is a bounded integration proof, not a broad model-quality or latency benchmark.
- Independent HR/finance/audit source rereview confirmed both identified P1s closed: mixed pasted/document sources and provenance loss across sequential section imports. No external consultant sign-off is implied.
- The repository-wide premium UI static audit remains non-green (207 findings); this increment does not claim that global audit has passed. Targeted browser checks are separate from that unresolved report.

The live preview has not been updated with this increment. Local backend tests, a local Ollama proof and frontend tests are distinct evidence; they do not establish deployed end-to-end acceptance. The frontend and API must be promoted as a matching pair after the migration and target model configuration are validated.

## Ollama deployment contract

The existing provider configuration is reused: `AI_PROVIDER=ollama`, `AI_MODEL` selects an installed model, `OLLAMA_BASE_URL` identifies the server, and `AI_MAX_CONTEXT_TOKENS` sets the extraction budget. The local proof used `http://127.0.0.1:11434` and `qwen3-coder:30b`; it did not change application or deployment configuration. A hosted API needs a privately reachable Ollama endpoint; its loopback address does not reach a developer's laptop. Ollama Cloud is a distinct deployment choice and is not equivalent to local processing. There is no automatic fallback from Ollama to an external provider.

Before release: apply the migration in the intended environment, validate that environment's model and endpoint using synthetic input, deploy matching API/frontend versions, and exercise upload → extraction → review → Apply and publication → employee answer → withdrawal using real authorized personas. Keep policy-source and publication audit evidence, and verify that company A employees cannot retrieve company B documents.
