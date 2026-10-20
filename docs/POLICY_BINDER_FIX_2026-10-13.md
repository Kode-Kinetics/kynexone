# Unified policy binder and setup draft repair

Status: implemented locally on `fix/policy-binder-draft`, based on main `945b9dc4626988f8b6cb2c9dfc1492ed64a80257`. Not merged or deployed. No live Evostel writes, database tables or migrations. The original dirty checkout was preserved; work is isolated in `KynexOne-wt/policy-binder-fix`.

## Delivered

- One Setup Studio draft accepts multiple PDF/DOCX/TXT chapters, a complete document, pasted text saved as a private document, or existing Policy Library documents.
- Add, replace and detach sources. Detaching does not delete the library document or previously reviewed form values.
- Source extraction traverses bounded sections with original offsets, consent, progress, explicit cancellation and retry checkpoints. Sections are not silently discarded to fit one AI request.
- Suggestions from documents are merged for review. Conflicting values require a choice; known partial values are prefilled and missing fields require answers. Manual completion is marked in source provenance.
- Review pagination limits the rendered settings/questions. Company identity is protected. All existing permission, source-hash, company and Apply guards remain.
- Tenant/user/company-scoped browser drafts persist inputs and edited generated previews. Restore requires explicit preview review before Apply; changed inputs invalidate the saved preview. Cancel/discard is confirmed. Policy Library and Setup Studio have return navigation.
- ISO3 company country values such as SAU normalize to the setup select's ISO2 value SA.

## Explicit limits

| Layer | Current limit |
| --- | --- |
| Upload file | 20 MiB per file |
| Extracted text | 5,000,000 UTF-16 characters per document |
| PDF | 2,000 pages per document |
| Binder | 50 distinct documents |
| Reviewed source evidence | 500 excerpt references per submission |
| Apply audit payload | 128 KiB serialized reviewed configuration, not the uploaded source text |
| DOCX expansion guard | Existing 40 MiB expanded archive / 2,000 archive-entry guard retained |

Both Setup Studio and Policy Library use the shared upload parser limits. There is no separate source component named unified policy draft upload. These are safety caps, not proof of maximum-size AI throughput. Per-request source sections also depend on the configured AI input budget; a large binder can require many sequential requests.

## Verification

- Backend: 65 targeted extraction/parser/configuration tests passed, including disposable PostgreSQL Apply tests. Final root rerun used `dotnet test ... --no-build --no-restore --filter 'FullyQualifiedName~PolicyExtractionTests|FullyQualifiedName~PolicyDocumentGovernanceTests|FullyQualifiedName~SetupConfiguration'` after the implementation agent's final rebuild.
- Frontend: all 274 unit tests passed using `npm run test:unit`.
- Binder browser suite: 6 passed. Covers >200,000-character intake, multiple chapters, partial completion, conflicts, provenance, retry, cancel/resume, replacement/library reuse, bounded pagination and overtime multiplier correction.
- Draft lifecycle browser suite: 10 desktop/mobile tests passed, including edited-preview restoration and company isolation.
- Existing setup-experience suite: initial full run 54 passed, 3 skipped and 3 failed. The two real density defects were fixed and the HMR-time mobile failure was rerun. All three failed cases passed focused reruns; no clean full-suite rerun is claimed.
- English/Arabic coverage: 8 passed, 1 intentional repin skip. TypeScript and diff whitespace checks passed.
- Production frontend build: `npm run build` completed successfully, including type validation and all 86 static pages.
- Independent review findings concerning source provenance, integer validation, multiplier completion and case-insensitive leave identity were fixed and re-reviewed as closed.
- Browser tests use intercepted API fixtures; they are not authenticated production acceptance or live AI-provider evidence.

## Remaining release boundaries

- No merge/deployment/live provider acceptance is implied by local tests or the successful build.
- No OCR added: scanned image-only documents require readable text/OCR first.
- Unsupported operational rules remain explicit review findings. This does not implement arbitrary policy engines, legal interpretation, or overwrite existing changed operational policies.
- Browser-local draft/checkpoint storage is quota-limited and not cross-device durable; storage failures are shown. Uploaded documents remain server-side under existing access rules.
- Large-document live provider performance, throttling, cost and end-to-end cancellation have not been certified.
- Dependency installation reported existing vulnerabilities (3 moderate, 5 high); unrelated dependency upgrades were not made in this patch.

## Policy 3 structured review follow-up (13 October)

- Supported settings retain explicit selection checkboxes and optional review comments. Unsupported operational clauses instead become source-backed review cards: disposition, comment, follow-up owner and (for an approved answer) approval reference. Recording a decision does not configure or approve an executable workflow.
- Repetitive ungrounded missing-field warnings and unconditional payroll/approval warnings were removed. Policy, recommendation, assumption, proposed configuration and clarification passages are distinguished; proposed/draft passages cannot silently become approved setup values.
- Review evidence includes document version, exact source excerpt and character location. Server writes check tenant/company access, organization permission, current source hash and excerpt offsets. Decisions append to the existing central audit log, using its existing transaction and chain sealer; no table or migration was added.
- Review/checkpoint identities include company, document version and proposed value. Company changes invalidate stale cards. Source replacement requires fresh extraction, preserving prior decisions as history. SingleCompany drafts resolve their existing company before allowing review saves.
- Final frontend verification: 283 unit tests; 8 translation checks passed with 1 intentional existing skip; 8 structured-review and 6 binder browser tests passed against the optimized local build. Browser regressions use isolated fixtures. Production-mode frontend build completed successfully.
- Final focused backend verification: 55 passed, 0 failed/skipped, covering extraction, review authorization/version/evidence validation, the permission and raw-SQL guards, and real disposable PostgreSQL concurrency. Eight review saves plus an ordinary audit writer formed one valid nine-row chain; another tenant's marker and operational tables remained unchanged. No security allow-list was broadened.
- An earlier full backend run exposed the two new endpoint security-inventory issues corrected above. It was stopped after more than 13 minutes on the superseded build; a clean full-backend-suite pass is not claimed. The final focused run explicitly reran both guards successfully.
- Separate real Chrome/provider verification used the actual uploaded Policy 3 DOCX in the local shadow tenant. The first run yielded 21 source-backed review items, with 0 executable setup settings. One clearly labelled local-test clarification was saved to the real shadow PostgreSQL database and restored after reload. No Apply, Publish or operational-policy writes were performed.
- Important capability boundary: existing onboarding templates are tenant-wide, and probation endpoints are employee-specific. This patch does not invent company-scoped workflow configuration, activate Policy 3 automatically, resolve its contradictory confirmation dates, or implement a general Group/company inheritance engine. These remain explicit product/policy decisions.
- A final fresh company-bound live extraction yielded 19 review items and 0 executable suggestions. Counts/wording can vary between AI runs; exact evidence and human review remain required, and previous decisions are retained in history rather than silently approving differently worded items.
- Final API was restarted locally and returned readiness HTTP 200 with zero pending migrations. A second explicitly labelled local-test clarification saved successfully through Chrome on that build; review history now contains two test decisions. Screenshot evidence: `/tmp/kynex-policy-review-final.png`. Production was not changed.

## Existing-form correction — 2026-10-13

The owner narrowed scope to prefill existing forms and reuse unchanged Save APIs. Broader save-envelope, concurrency, server provenance/audit and schema work in the earlier design/plan is deferred. This increment changes extraction output and the frontend handoff only; `OnboardingController`, recruitment API contracts, models and migrations are unchanged.

- Extraction now returns bounded, evidence-checked `onboardingTaskDrafts`. REC/ASM/TBC/PROP sources are excluded, unknown deadlines/owners/mandatory flags remain null, and conditions are retained for human review. Titles/descriptions/categories are AI draft wording, not independently certified semantic interpretations.
- Setup's primary next action opens the existing `/recruitment/onboarding` Checklist Templates controls. Generic review cards are collapsed. The tenant/user/company-scoped browser draft preserves edits and makes no onboarding write on extraction, open or reload.
- Each task uses the existing title, description, category, owner, deadline, order and mandatory controls. Checklist selection is explicit. Unknown deadline blocks Save instead of becoming zero. Conditional applicability and matching-title updates require explicit review. Group/company-specific drafts remain unsaved because existing templates are tenant-wide.
- Existing Save is followed by ID/value readback. Provenance remains in the browser draft; this is not server policy audit, concurrent-write protection, automatic eligibility enforcement, employee generation or policy publication.
- TDD reproduced the missing handoff and extraction/unknown-field failures before implementation. Independent QA caught browser-quota edit loss; the corrected in-memory retention has regression coverage. Final frontend unit run: 292 passed. Translation checks: 8 passed, 1 existing intentional skip. Optimized build and TypeScript passed. Existing binder/review/lifecycle browser suites: 6 + 8 + 10 passed. Root reran the final new prefill suite: 12 passed, including mobile, exact-title update, custom permissions, tenant/user isolation, read-only access and Group company-switch clearing without a stale write.
- Root independently reran extraction/document-governance/review tests: 85 passed, 0 failed/skipped. The broader 7,307-test backend suite was stopped after approximately 8 minutes without a completion result; no full-suite pass is claimed. Full backend regression remains a release gate, separate from this focused local proof.
- Real provider probe: one malformed JSON response saved nothing; a retry processed all 3 source sections and returned 13 drafts. A separate real Chrome run processed the same stored Policy 3 DOCX and returned 9 drafts, then visibly filled the existing form. AI output varies; this does not certify complete Policy 3 coverage. Probation calculations, appraisal scheduling, conditional eligibility and source-named document forms remain outside this slice.
- Actual shadow-only Save proof: checklist `POL3-LOCAL-QA` / `29214299-ed95-4048-8c2e-ba0eebb8cfd5`, task `Contract signing` / `31d36a67-b44b-40a2-820c-6c05d58db9c2`. Description explicitly labels QA-only; deadline 1 and optional status were manually entered test inputs, not approved policy. API readback matched, reload retained edits and saved marker, employee task count stayed 0. No production mutations.
- Actual Chrome evidence: `/tmp/kynex-policy-existing-form-actual.png`. Page identity and populated controls verified; no framework overlay or console warning/error was observed. The tab is left at an unsaved Bank account support draft with missing deadline blank for owner review.

Owner test: reload `http://127.0.0.1:5197/setup`, open the saved policy, Extract settings (old extraction formats require this once), choose **Open existing onboarding form**, select a task and **Fill existing task form**. Review the source, select the intended checklist and supply only approved missing values before using **Save Template Task**. Local shadow only; production is unchanged.

## Repeat-extraction correction — 2026-10-13

This supersedes the collapsed-questionnaire UI described above. Setup no longer renders generic disposition/comment/follow-up questionnaires. Source evidence and genuine gaps remain compact, read-only supporting information. Zero usable mapped values show **No form values prepared** and an explicit retry action, including after resuming an older checkpoint.

Real-provider diagnosis reproduced the owner's zero-value outcome, despite passing synthetic tests: the provider returned complete JSON in Markdown fences, changed hyphens in quotations, abbreviated evidence with ellipses, and classified task text using a neighboring REC control cell. Exact-evidence validation correctly refused those altered quotations, but there was no reliable route back to original task evidence.

The bounded repair accepts a single whole JSON fence (not malformed/truncated output), and uses one focused, audited projection into existing task fields. Its source IDs are server-owned and bound to the stored document/hash/original offsets. Original source approval context is rechecked; unknown IDs and contradictory task/non-task outcomes are rejected. Compound and multi-sentence evidence cannot assign an ambiguous owner, deadline or mandatory flag. An explicit non-task outcome permits objectives/triggers to stay context instead of forcing invented tasks. This does not prove semantic completeness or eliminate synonymous drafts; wording and duplicates still require human review. No new operational Save endpoint or schema was added.

Fresh frontend verification on the correctly targeted optimized build: 36 browser cases passed (8 review/outcome, 12 existing-form prefill, 6 binder, 10 lifecycle). The real signed-in shadow screen also showed the questionnaire removed and the honest zero-value retry state before re-extraction. Frontend unit suite: 292 passed; build and TypeScript passed.

Build target caution: Next.js captures API rewrites at build time. Build this shadow with `NEXT_PUBLIC_API_BASE_URL=http://127.0.0.1:5127 npm run build`; verify `.next/routes-manifest.json` targets port 5127 before starting port 5197. A default-target rebuild was caught and replaced during this work; that is not established as the cause of the owner's original screenshot.

Intermediate real Policy 3 replay after the source-ID repair returned 26 task drafts; all 26 original evidence ranges matched the stored text. Contract, bank support, workspace, stationery, medical, IT equipment, ID card, system access, welcome, personal file and mentor tasks were present. Unknown owner/deadline/mandatory values stayed null. This is intermediate positive evidence, not final-build certification.

The following real-browser check caught a separate transport failure: Next's local rewrite proxy defaulted to 30 seconds, less than the backend's 90-second section budget. A regression using the installed Next proxy reproduced a 31-second upstream POST failing at 30 seconds with ECONNRESET. `experimental.proxyTimeout: 120_000` now allows that response, and caller cancellation still closes upstream without retrying the POST. Deployment ingress limits remain unverified; this is local/self-hosted transport proof. After this change: 294 frontend unit tests, optimized build/typechecking, and all 36 browser cases passed again. Built manifest confirms port 5127 and timeout 120000.

A subsequent actual model response also incorrectly routed appraisal clauses from a known probation section into onboarding drafts. Source-topic validation now excludes that wrong-form mapping; excluded source-backed rules remain read-only requirements, deduplicated by evidence span. Final backend and live Chrome evidence follow below.

- Backend owner final focused suite: 139 passed, 0 failed/skipped. Root independently reran extraction/document-governance/review selection on that DLL: 138 passed, 0 failed/skipped. The broader backend suite remains uncompleted, not certified.
- Final real Chrome run on the rebuilt frontend and topic-validated API processed all three sections of the stored Policy 3 DOCX and returned **23 task drafts**. The actual existing `/recruitment/onboarding?policyDraft=1` form was populated with **Arrange workspace**, General category, and a source-backed description. Owner and deadline remained blank, Mandatory remained **Not specified — choose**, and Save was disabled until explicit required review/input. The list did not route appraisal rules into onboarding tasks. No operational Save or employee generation was performed.
- Browser console showed no warnings/errors; screenshot: `/tmp/kynex-policy-form-fixed.png`. The populated unsaved form is left open for owner inspection. Local operational counts remained 1 prior QA checklist, 1 prior QA template task, 0 employee tasks.
- A separate same-build API replay returned an unreadable focused AI response. The audit recorded **Task projection was unreadable**, returned a retryable current-section outcome, and did not claim completion. One explicit retry then returned 19 first-section drafts in 21.4 seconds, advanced to section 2, preserved exact evidence for all 19, and returned zero appraisal task drafts. This is a retained provider reliability limitation, not evidence of flawless extraction. AI wording, objective/task distinctions, duplicate meanings, and complete binder coverage remain subject to human review. Production deployment and owner acceptance remain separate gates.

Owner retest in the original browser: refresh `http://127.0.0.1:5197/setup`, reopen the saved policy, run **Extract settings / Retry extraction**, then **Open existing onboarding form** → select a task → **Fill existing task form**. No re-upload is needed. Review missing values and applicability before choosing a checklist and explicitly saving. Production Evostel has not been changed.
