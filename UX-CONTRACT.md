# Setup behavior contract

Visual context: [DESIGN.md](DESIGN.md). Existing domain and permission contracts remain authoritative.

- Company details → work and leave → people/pay → grades/benefits → review/apply. Moving between steps preserves authored values.
- Multiple attendance channels can coexist. Overtime exclusion is mutually exclusive with paid/time-off modes. Different leave entitlements use scoped policy rows.
- Custom numeric values and references are validated server-side before writes. Unknown or unsupported values produce actionable refusals, never silent substitution.
- Input changes invalidate generated drafts; failures retain user input. Preview is not apply. Apply requires canonical module permissions plus setup permission.
- The existing-policy action appears before company fields, with a three-stage, keyboard-accessible guide: add a document or paste text, choose AI assistance, then understand review/apply. Closing the guide preserves inputs. The mobile step rail remains compact so the policy action is visible on entry.
- Choosing a document selects it locally; the explicit upload action saves a private draft to the server. AI extraction is a separate opt-in action using the configured provider, including Ollama. Selecting a saved document clears active pasted context to prevent mixed sources. The paste path remains context-only.
- Extraction proposals start unselected. Show current and proposed values, exact source excerpts, missing/unsupported questions and coverage needing review. Copy only selected proposals into the form; Generate and Apply remain separate. Retain field-level document/hash/location references across successive policy sections. These record input origins; reviewed values may be edited and the final draft is separately audited.
- Document processing readiness does not mean employee publication. Publication requires authorized HR, a company, current content fingerprint and effective dates. Warn that the entire published document is visible to every employee of that company. No grade/department audience restriction is offered in this increment.
- Kody's employee policy mode uses the narrow employee endpoint, server-resolved active employment and published/effective company documents. Never grant general AI query permissions solely for employee policy answers. Show source excerpts/version fingerprints; policy explanation does not calculate a live balance or change records.
- Extraction must refuse oversized context without truncating the source. Whole-handbook background processing, OCR, semantic cross-language retrieval and policy-change impact calculations are not represented as delivered capabilities.
- AI policy intake is optional and bounded. A source excerpt is not a statutory authority or approval. No automatic employee enrollment, payroll payment or contribution amount is inferred from text.
- Repeatable fieldsets use visible labels; keyboard navigation and focus after step changes follow existing setup behavior. Mobile layouts must not overflow. Dynamic statuses and errors are accessible.
- Leave balance year, accrual frequency, partial-month proration, use eligibility and settlement are distinct. Unsupported accounting periods cannot be activated by storing an unused preference.
- Reviewed benefits create plans and eligibility, not enrollment or contributions. Effective dates, grade references, company scope, permission parity, replay behavior and audit evidence must be tested.

- Desktop company details use four columns from 1280px, a compact active-step description and a shared heading/navigation row. With the policy guide closed, all eight fields and Continue remain visible at 1728×900, 1440×900 and 1366×768 with the MFA reminder. Footer spacing keeps the assistant launcher away from the action. Smaller/zoomed layouts retain natural scrolling rather than clipping controls.

- Opening the policy guide temporarily hides the company-detail fields and main Continue footer, preserving their values. The guide uses two desktop columns for intake/AI review and three for the final explanation. Close/finish restores company details and focus; starting another setup resets guide visibility.
- Standard guided steps use available width for attendance/overtime, people/pay, grades/benefits, and review choices. Repeatable policy/grade/benefit rows and generated records retain natural scrolling. Import entry and all 17 settings entry screens must remain contained on desktop/mobile.
