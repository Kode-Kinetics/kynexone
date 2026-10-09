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
KynexOne is an HR operations product for HR administrators and technical implementation teams. Preserve the established application shell. Setup should read as a guided policy workbook: one decision area at a time, visible progress, and editable evidence before apply. Its signature is the relationship between company policy, structured configuration and the final reviewed records.

## Colors
Runtime ownership remains in `frontend/tailwind.config.ts` and application CSS. Use sapphire for the primary action and active step, slate for hierarchy, and the existing dark theme tokens. This document records the existing palette; it does not introduce a rebrand.

## Typography
Use the existing Geist and IBM Plex Sans Arabic stack. Headings identify the task; labels identify the exact policy value. Keep financial values and machine codes readable in bidirectional text.

## Layout
Use the setup step rail on large screens and the existing responsive navigation on phones. Group repeatable policies in bordered fieldsets. Reveal detailed custom rules only on request. Show employee-group scope beside each policy instead of unexplained global multi-selects.

## Elevation & Depth
Retain existing button and shell elevation. Policy rows use borders rather than nested decorative cards.

## Shapes
Keep existing input/button shapes and 12px panels. Do not add a parallel token system.

## Components
Native labeled inputs, selects, checkboxes and date fields use the application input/select classes. `SetupPolicyEditor` owns repeatable setup policy authoring. `AiSetupAssistant` owns navigation, stale-draft invalidation, preview and apply. `useT` and `useFormat` remain locale owners. Backend domain services own validation, effective dates and permissions.

## Do's and Don'ts
Explain whether a choice creates an active policy, a catalog record or a planning preference. Never present a stored preference as an enforced calculation. Preserve input on validation failure. Company policy text is untrusted source material and reaches the configured AI provider only after explicit opt-in. New strings require Arabic translations and RTL checks.
