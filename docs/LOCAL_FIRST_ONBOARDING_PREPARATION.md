# Local-first onboarding policy preparation

Scope: the existing Onboarding and confirmation workspace in Setup Studio. This does not replace other setup areas or publish policies.

## User flow

1. Enter tasks manually, or upload a text-based PDF, DOCX or TXT document.
2. The existing native parser reads document text on the application server. The onboarding workspace automatically prepares supported task drafts using local rules.
3. Review source-backed values in the existing onboarding task form. Unstated deadlines and mandatory status require the user's decision; conditional tasks retain their applicability review.
4. Save explicitly through the existing checklist-template endpoint. Preparation itself never creates employee tasks, saves settings or publishes policy content.

“Improve with AI (optional)” is separate. It requires consent, uses the existing AI pipeline, and cannot remove the locally prepared results when a provider fails.

## Boundaries

- Local means application-server processing without an AI provider, not browser-only storage of the original document.
- Local mapping supports conservative English-language onboarding actions and checklists, not arbitrary semantic interpretation of every HR policy.
- Native text parsing and successful field mapping are different outcomes. Parsed but unmapped documents remain available for manual review or optional AI.
- Scanned/image-only PDFs require OCR; legacy `.doc` files require conversion to DOCX or text-based PDF. No OCR engine is added here.
- Proposed, unapproved, negated and unrelated workflow clauses are excluded. Ambiguous comma-separated conditional checklists are left for manual review.
- Mapping is bounded to 1,000 task drafts with an explicit warning; overlong clauses are also reported for manual review. A completed local pass is not a claim of complete policy coverage.
- Exact source offsets and document hashes accompany task drafts. Source changes invalidate the local checkpoint; draft storage remains tenant/user/company scoped.

## Implementation

The existing `POST /api/setup-assistant/policy/extract` endpoint accepts `useAi: false` for complete-document local preparation. Document authorization is enforced before the local branch; provider calls and AI quota checks are exclusive to the `useAi: true` branch. Nonzero local section indexes are rejected.

No database schema changes, additional tables, migrations or production deployment are included.

## Verification

Focused mapper/controller tests cover source fidelity, unknown values, negative/proposed policy sections, conditional lists, tenant/user access and absence of provider or operational writes. Browser tests cover automatic local preparation, AI failure retention, reload recovery, retry/manual fallback and desktop/mobile form access. Existing policy-binder and form-prefill regression coverage is retained.

Local verification on 2026-10-16 (not production or owner acceptance):

- Optimized frontend build passed; 312 frontend unit tests passed.
- 22 policy-form-prefill browser tests and 6 policy-binder browser tests passed; desktop/mobile local-preparation screenshots inspected.
- 179 targeted backend regression tests passed, including native document governance, policy extraction/review, setup configuration and the new local mapper/controller cases.
- Authenticated local-shadow API request against the existing Policy 3 document returned HTTP 200, provider `local-rules`, 13 drafts in 165 ms. All source offsets/hash matched; four conditions were retained, with owners/deadlines/mandatory status left unknown.
- Before/after database counts were unchanged: AI logs 58, checklists 1, template tasks 1, employee tasks 0, policy documents 5. No operational save or publication was performed by the probe.
- Frontend remains on `127.0.0.1:5197`, API on `127.0.0.1:5127`; production was not deployed.
