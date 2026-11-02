---
version: alpha
colors:
  primary: '#2F6BFF'
  background: '#F8FAFC'
  darkBackground: '#0B1020'
  success: '#00C896'
  darkAccent: '#5EEBFF'
typography:
  sans:
    fontFamily: 'Geist, IBM Plex Sans Arabic, sans-serif'
  mono:
    fontFamily: 'Geist Mono, monospace'
rounded:
  panel: '12px'
omitted:
  - section: spacing
    reason: 'Existing Tailwind spacing utilities remain canonical.'
  - section: components
    reason: 'Runtime shared primitives and global form classes remain canonical.'
---

## Overview
KynexOne is an HR operations product for HR administrators and technical implementation teams. Preserve the established application shell. Setup Studio opens a non-linear twelve-area workspace with visible company context and permission-aware access to existing settings. Its signature is the relationship between company policy, structured configuration and the final reviewed records. The starter configuration draft retains its guided review-before-apply flow as a separate workspace action.

## Colors
Runtime ownership remains in `frontend/tailwind.config.ts` and application CSS. Use sapphire for the primary action and active step, slate for hierarchy, and the existing dark theme tokens. This document records the existing palette; it does not introduce a rebrand.

## Typography
Use the existing Geist and IBM Plex Sans Arabic stack. Headings identify the task; labels identify the exact policy value. Keep financial values and machine codes readable in bidirectional text.

## Layout
Use an area navigation rail on large screens and a named area disclosure on phones. Areas are destinations, not mandatory completion steps; the overview does not assess configuration completion. Keep settings/templates distinct from related operations, and show company, tenant-wide or record-dependent scope. Company details use four columns on desktop and two or one on smaller screens; align setup paths beside the page heading when width permits. Group repeatable policies in bordered fieldsets. Reveal detailed custom rules only on request. Show the starter policy guide as a focused stage instead of stacking it over the company form. Place independent authoring groups beside one another on desktop while keeping nested fields wide enough to read. Show employee-group scope beside each policy instead of unexplained global multi-selects.

## Elevation & Depth
Buttons are flat solid fills; elevation follows the e1/e2/e3 levels. Policy rows use borders rather than nested decorative cards.

## Shapes
Radii follow the token scale above (8 controls, 12 panels, 16 dialogs). Do not add a parallel token system.

## Components
`SetupStudioWorkspace` and `SetupAreaNavigator` own area discovery and URL navigation. Embedded settings reuse canonical module editors and save APIs; source-module routes remain available. Dialogs use the shared modal for focus management and scrolling. Preserve dirty-editor navigation guards and browser/company-scoped starter drafts. Document-to-form adapters and policy-library/Kody convergence remain required programme packages, not implied by twelve discoverable areas.

Native labeled inputs, selects, checkboxes and date fields use the application input/select classes. `SetupPolicySource` owns the prominent policy entry and three-stage intake guide. `SetupPolicyEditor` owns repeatable setup policy authoring. `AiSetupAssistant` owns navigation, stale-draft invalidation, preview and apply. `useT` and `useFormat` remain locale owners. Backend domain services own validation, effective dates and permissions.

`SetupPolicyExtraction` owns before/after proposal review with source passages and unchecked acceptance. `PolicyDocumentManager` owns draft intake, source review and explicit company-wide publication. `PolicyAnswer` is the shared evidence display for HR and employee answers; `EmployeePolicyAssistant` is the narrow employee entry inside Kody. Uploaded drafts and employee-visible documents must never share an ambiguous "Ready" publication label.

## Design tokens
Source of truth: `frontend/src/styles/tokens.css` (colour roles, light and dark), mapped to utilities in `frontend/tailwind.config.ts`. Use roles, not palette names or hex: `bg-surface`, `text-fg` / `text-fg-muted` / `text-fg-subtle`, `border-line` / `border-line-strong`, `bg-primary` + `text-on-primary`, `text-primary-fg`, `bg-success-soft text-success-fg` (same for warning, danger, info).
- Type: `text-caption` 11, `text-meta` 12, `text-body-sm` 13, `text-body` 14, `text-h4` 16, `text-h3` 18, `text-h2` 22, `text-h1` 28, `text-kpi` 28. Nothing under 11px. Weights are 400 / 500 / 600 only (`font-bold` and heavier alias to 600).
- Space is the 4px Tailwind scale. Radii: 4 (`rounded`), 8 (`rounded-lg`), 12 (`rounded-xl`, cards), 16 (`rounded-2xl`, dialogs), `rounded-full` for pills.
- Elevation: `shadow-e1` cards, `shadow-e2` menus and toasts, `shadow-e3` dialogs. Neutral ink only; never a coloured shadow. Flat surfaces use a 1px `border-line`.
- Control height: `h-control-sm` 28, `h-control-md` 36 (default), `h-control-lg` 40; coarse pointers get 44. Buttons and inputs share the scale.
- Glass (`wg-glass`) is for floating chrome only: top bar, sidebar, bottom nav, assistant drawer. Buttons, inputs, cards, menus and dialogs are solid.
- Contrast is asserted in `frontend/unit/designTokens.spec.ts` (text 4.5:1, control boundary and focus ring 3:1, both themes). Add a role only with its pair in that test.
- Primitives: `components/ui/{Button,Field,Card,Badge,Table,EmptyState,Skeleton,AppToast,ConfirmDialog}` and `components/Modal` (`placement="end"` for a drawer). The `.btn-*`, `.input`, `.select`, `.textarea`, `.badge-*`, `.surface` classes are the same styles for markup that cannot use the components.
- Ratchet: `npm run lint:tokens` fails if off-system counts (arbitrary font sizes, raw hex in classes, `gray-*`, off-scale radii, `shadow-2xl`, coloured shadows, heavy weights) rise above `frontend/scripts/design-token-baseline.json`. After lowering counts run `node scripts/design-token-ratchet.mjs --update`. `scripts/design-token-codemod.py <files>` migrates a file mechanically.

## Do's and Don'ts
Explain whether a choice creates an active policy, a catalog record or a planning preference. Never present a stored preference as an enforced calculation. Preserve input on validation failure. Company policy text is untrusted source material and reaches the configured AI provider only after explicit opt-in. New strings require Arabic translations and RTL checks.
