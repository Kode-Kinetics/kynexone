# Setup behavior contract

Visual context: [DESIGN.md](DESIGN.md). Existing domain and permission contracts remain authoritative.

- Company details → work and leave → people/pay → grades/benefits → review/apply. Moving between steps preserves authored values.
- Multiple attendance channels can coexist. Overtime exclusion is mutually exclusive with paid/time-off modes. Different leave entitlements use scoped policy rows.
- Custom numeric values and references are validated server-side before writes. Unknown or unsupported values produce actionable refusals, never silent substitution.
- Input changes invalidate generated drafts; failures retain user input. Preview is not apply. Apply requires canonical module permissions plus setup permission.
- The existing-policy action appears before company fields, with a three-stage, keyboard-accessible guide: add text, choose AI assistance, then understand review/apply. Closing or navigating the guide preserves text and consent. The mobile step rail remains compact so the policy action is visible on entry.
- Choosing a file reads text locally. Generate submits the configuration to the setup service; only explicit opt-in shares excerpts with the configured AI provider. Text supplies context, not automatic configuration of every operational rule.
- AI policy intake is optional and bounded. A source excerpt is not a statutory authority or approval. No automatic employee enrollment, payroll payment or contribution amount is inferred from text.
- Repeatable fieldsets use visible labels; keyboard navigation and focus after step changes follow existing setup behavior. Mobile layouts must not overflow. Dynamic statuses and errors are accessible.
- Leave balance year, accrual frequency, partial-month proration, use eligibility and settlement are distinct. Unsupported accounting periods cannot be activated by storing an unused preference.
- Reviewed benefits create plans and eligibility, not enrollment or contributions. Effective dates, grade references, company scope, permission parity, replay behavior and audit evidence must be tested.
