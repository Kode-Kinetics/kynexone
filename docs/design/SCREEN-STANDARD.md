# KynexOne Screen Standard

Status: proposed. Verified against `origin/main` 1d0f932e, 2026-11-06. Companion: [SCREEN-STANDARD-EXAMPLES.md](SCREEN-STANDARD-EXAMPLES.md). For every frontend builder and reviewer. Direction: restrained light "Workforce Glass" (layering and elevation, no 3D). It codifies and tightens the system that exists (`frontend/src/styles/tokens.css`, `tailwind.config.ts`, `DESIGN.md`, `src/i18n/GLOSSARY.md`); it adds no palette, font or radius.

**Why it exists.** The owner's three complaints are measurable today. (1) Space: at 1366x768 the Attendance daily table gets about 300 px, because a 360 px punch card and a 360 px rail sit beside it. (2) Guidance: about 43 `info=` props exist across 8 files against roughly 890 `<label>`s; 24 of the 43 are bare English literals that never reach Arabic; `InfoTip` prints a developer `key:` line to every user. (3) Next step: `ui/EmptyState` exists and is used by 0 screens (35 hand-rolled `border-dashed` boxes instead), there are 15 separate local `Field` definitions (`ui/Field.tsx` exports only inputs), and every wizard builds its own step strip.

**Build six primitives first; everything else is migration.** `ui/Page` (header, purpose line, primary slot, rail grid), `ui/ActionBar` (sticky footer), `ui/StepStrip` (extract `EmployeeCreateProgress`), `ui/Field` (label, `info`, `hint`, `error`, `badge`), `ui/ResultBanner` (post-save), `ui/CountUp`; and extend `ui/EmptyState` (`kind`). They emit the test hooks used in section 8 (`data-purpose`, `data-primary-action`, `data-step-strip`, `data-result`, `data-empty-state`), so a screen built from them passes the contracts by construction.

---

## 1. Screen types and their grids

**Frame.** Content width = viewport - 240 sidebar - 64 padding: 976 at 1280, 1062 at 1366, 1136 at 1440, 1616 at 1920. Container `mx-auto w-full max-w-[1680px]`; page padding 16 / 24 / 32 (`px-4 sm:px-6 lg:px-8`, existing). 12 columns; gutter 16 below 1024, 24 from 1024. Breakpoints: Tailwind defaults plus two added to `tailwind.config.ts`: `rail` 1360 px and `wide` 1680 px. Every screen's header block, in order: optional back link, `h1` (`text-h1`), one purpose line (`text-body text-fg-muted max-w-[70ch]`), and the primary action at the inline-end of that row. In RTL, inline-end is the left edge; write logical properties only.

| Type | Grid and width | Primary action (one `btn-primary`) | Secondary actions | Right-hand space on wide screens | Phone (< 640) |
|---|---|---|---|---|---|
| **List** | Full width to 1680. Toolbar row (search 320, at most 3 filters, then "More filters"), table, pager (25 / 50 / 100). | Header, inline-end: "Add {thing}". The empty state repeats it. | Row click opens the Record. Export / Import in a "..." menu. Row kebab at inline-end. | Rail 320-480 only from `wide` (the table must keep 960 px). Below `wide`, a strip of at most 4 stat chips sits above the table. Rail: summary counts, "Needs attention", selected-row preview with its own action. | Card list (3 key fields + status chip); toolbar becomes search + "Filters" sheet; primary becomes a sticky bottom bar. |
| **Record (view)** | Header band (avatar/name, status chip, 4-6 key facts). Tabs, at most 6 (`RovingTabs`). Main 8/12 + rail from `rail`. | Header, inline-end: "Edit". | Other verbs in a "..." menu; destructive verbs only there. | Rail 320-480: "At a glance" (at most 6 facts), "Needs attention" (count + first action), "Recent activity" (5). Below `rail` it stacks above the tabs, never below the fold. | One column; tabs become a select; sticky bottom action bar. |
| **Form / Wizard** | Step strip spans the full width under the header. Form column 704 (800 from `wide`); inside it 1 column below `md`, 2 columns from `md` for short fields (dates, numbers, selects); text areas, names, addresses span 2. Never 3 columns. | Sticky footer (`ActionBar`), inline-end: "Save & next: {Step}"; last step "Add employee" / "Save changes" / "Publish". | Footer inline-start: Back (secondary), "Save draft" (ghost). Cancel is a text link at the far inline-start. | Rail 320 from `rail`, flexing to 560 from `wide`: live "What will be created / changed" summary, "Needs your attention" (blocking items), step help. At 1920 the occupied share of main is at least 80%. | Strip becomes "Step 2 of 5: Employment" + select; one column; sticky footer. |
| **Dashboard** | KPI row 2-up (phone), 4-up from `md`, 6-up from `wide`. Cards on the 12-col grid, at least 3 per row from `rail`. "My day / Company" is a 2-option segmented control at the header inline-end. | Header inline-end holds the one verb the page exists for ("Record punch"). A dashboard with no single verb has none, and its first card carries the one primary ("Review 4 approvals"). | Card-level text links and `btn-ghost` only. | Rail at `wide` for secondary cards (devices, exceptions). | Stack: attention, KPIs, work. Swipe views keep a visible tab control (`DashboardViews`). |
| **Setup / config** | One grouped left menu 240 (owner-approved) + content 704-960 + rail from `rail`. No second rail. | Sticky footer, inline-end: "Save & next" or "Save". | Back; "Skip for now" (ghost). | Rail 320 = "In this area" (`SetupNextActionsRail`): the next unfinished step is its one primary button, then at most 3 shortcuts. | Menu becomes a named disclosure; sticky footer. |
| **Dialog / Drawer** | Dialog sm 384 (confirm), md 512 (short form), lg 672, wizard 896. Drawer (`placement="end"`) 480, or 640 if it holds a table; enters from the inline-end edge. Drawer when the page behind must stay visible; dialog when the page must pause. | Footer, inline-end. | Cancel (secondary) directly inline-start of it. | n/a | Full-height sheet; sticky actions. |

Rules for every type:
1. One primary per view. A disabled primary says why in adjacent text (`aria-describedby`): "Choose a grade to continue".
2. The primary lives in one of two zones: **page header inline-end** (List, Record, Dashboard) or **sticky footer inline-end** (Form, Setup, Dialog, Drawer). Never mid-card, never both.
3. A centred narrow column is allowed only for Dialog, auth, and forms of 6 or fewer fields. Every other screen fills the width or pairs the form column with a rail. "Never empty" means no blank band wider than 25% of content width at 1920x1080.
4. A rail appears only if the main column keeps at least 700 px (forms, records) or 960 px (lists, tables). Otherwise its content moves above the main content as a compact strip, so the next action is never below the fold.

## 2. Density and spacing

Scale: Tailwind 4 px steps; allowed in layout code are 1, 2, 3, 4, 5, 6, 8, 10, 12, 16 (4-64 px). No arbitrary `[13px]` values. Control height 28 / 36 (default) / 40, and 44 on coarse pointers (`h-control-*`, existing).

| Intent | Value | Class |
|---|---|---|
| icon to label, inline items | 8 | `gap-2` |
| label to control | 6 | `mt-1.5` |
| field to field (vertical / horizontal) | 16 / 24 | `gap-y-4 gap-x-6` |
| group to group inside a card or step | 24 | `space-y-6` |
| card to card | 24 (16 below 1024) | `gap-6 max-lg:gap-4` |
| page band to page band | 32 | `space-y-8` |
| card padding | 20 default, 16 compact (KPI, list cards) | `<Card>` / `<Card compact>` |
| dialog and drawer padding | 24 | |
| table cell and row | 16 x 10, row 40; compact density 36 (lists over 50 rows) | `px-4 py-2.5` |

Type in regular use is five roles: `text-h1` 28 (page), `text-h2` 22 (Setup and dialog titles), `text-h4` 16 semibold (section), `text-body` 14, `text-meta` 12 (helper, timestamps). `text-caption` 11 is for chips and units only. Weights 400 / 500 / 600. Money and counts use `tabular-nums`. Reading text caps at 70ch (purpose lines), helper text and empty-state bodies at 65ch; tables and forms are exempt.

Per type:
- **List:** at least 12 data rows visible at 1366x768 without scrolling (topbar 56 + header 88 + toolbar 48 + thead 40 leaves about 536 px, 13 rows at 40). At most 8 columns; the first (name) column is sticky.
- **Record:** at most 6 facts per group, at most 5 groups before the fold; the rest collapse under a named disclosure (chunk, do not count).
- **Form step:** at most 12 fields in at most 3 groups of at most 6, each group with an `h4`.
- **Dashboard:** at most 6 KPIs in the first row; at most 8 widgets above the fold.
- **Choice load:** a toolbar shows at most 3 filters; a header has 1 primary + at most 2 secondary controls; the rest go in a menu.

Table or cards? Table when rows share 4 or more comparable attributes or the user sorts or compares (employees, runs, punches). Cards when each item is a thing with a status and 1-2 actions and fewer than 4 comparable attributes (devices, policies, templates), and for every list under 640 px.

## 3. Guidance

**(a) Purpose line.** Every screen, wizard step, drawer and dialog opens with one sentence under its title: verb first, what the user does here and what it affects, 20-140 characters in English, written natively in Arabic (allow +35%). It is `data-purpose`, the `ui/Page` slot. Good: "Set what each grade receives. Changes apply from the date you choose." Bad: "Manage benefits information."

**(b) The `i` (a rule, not a list).** A field gets an `i` when any one is true:
1. its value changes money, leave, eligibility, tax, GOSI, end-of-service or a legal document;
2. its label is jargon or an abbreviation (GOSI, WPS, Iqama, grade, cost centre, accrual);
3. it is a choice of 3 or more options whose names do not state the consequence;
4. a default was applied for the user (the `i` says why);
5. a new HR assistant would plausibly ask "what does this do?".

No `i` on name, phone, email, address lines, notes, or a plain date with one obvious meaning. An exempt field says so in code, `<Field noInfo="plain text">`, so the omission is a visible decision.
*Text:* at most 2 sentences and 160 characters (English). Sentence 1 says what the value does ("Sets the benefits and loan limits this employee is entitled to."). Sentence 2 gives the consequence, default, or where else to change it ("Changing it applies from the next payroll run."). Never begin "This field", never restate the label. Format and limits belong in the visible `hint` under the field, not in the `i` (required information must not hide behind hover). English and Arabic both go through `t()`; Arabic is written natively using `GLOSSARY.md` terms, not translated word for word.
*Behaviour:* `InfoTip` opens on hover, keyboard focus and tap; Esc dismisses; the popover stays open while hovered (WCAG 1.4.13); max width 280; anchors to the inline-start edge of the icon and flips at the viewport edge; icon hit area 24 px (44 on coarse pointers, by padding). The `key:` line shows only to holders of the Setup permission; the aria-label is `t('About {field}')`; per-company overrides (`HelpTextManager`) keep working.

**(c) Empty states** use `ui/EmptyState` and nothing else. Three kinds, three causes:

| `kind` | Title (what it is) | Body (why it is empty) | Action (exactly one) |
|---|---|---|---|
| `first-use` | "No grades yet" | "Grades set each employee's benefits and loan limits." | "Add the first grade" (same verb as the header) |
| `filtered` | "No employees match" | "Active filters: Department Finance, Status Active." | "Clear filters" |
| `restricted` | "You can't see payroll runs" | "Payroll access is given by your HR administrator." | none; it names who can grant it |

A failed load is not empty: it shows `LoadFailedRow` with Retry. A hand-rolled `border-dashed` block is a defect.

**(d) What next.**
- Every flow of 2 or more steps has `StepStrip`: an `<ol>` of 3-5 named phases (never "step 7 of 12"), `aria-current="step"`, each phase done / current / to do / has-problem as icon plus text, reachable steps clickable, the step in the URL (`?step=`). The footer primary names its destination: "Save & next: Employment". Drafts autosave and show "Draft saved 10:42"; reopening resumes on the exact step.
- Every list has its create action in the header and again in its empty state.
- After every save the user lands on a `ResultBanner` (`role="status"`, focus moved to it, kept until dismissed) with exactly three parts. **What changed:** "3 changes saved: Grade, Housing, Probation end". **Where you are:** the page you stayed on or reached. **One next action:** a button, "Review who is affected". A pending state is stated: "The salary change goes to Finance and applies on 1 Dec after approval." Toasts are only for inline row edits that do not navigate: 5 s, with Undo where reversible. Never end a flow on a bare "Saved".

**(e) Refusals** follow *what happened, why, what to do*, in one or two sentences, with the fix as a button or link where possible: "Salary wasn't saved: the structure is no longer valid for this employee. Choose another structure." Never "Failed to save", "Invalid", "Error 422", or "Something went wrong" alone. Field errors sit under the field; two or more add a summary at the top that receives focus, each entry linking to its field. A 403 says what is blocked and who can grant it. Server messages map through `lib/apiError.ts` to `t()` keys; never print them raw.

## 4. Interaction and motion

Motion acknowledges a state change or preserves continuity; it never decorates. Gate by frequency: monthly actions may be expressive, daily ones subtle and fast; hundreds-a-day and keyboard-initiated actions are not animated.

| Token (`styles/index.css`) | Value | Use |
|---|---|---|
| `--wg-dur-fast` | 120 ms | hover, press, focus ring, checkbox, tooltip, row tint |
| `--wg-dur-base` | 200 ms | value or status crossfade, disclosure, row insert/remove, step panel, toast, route enter, drawer and dialog exit |
| `--wg-dur-slow` | 280 ms | drawer and dialog enter; no UI motion exceeds 300 ms |
| `--wg-ease` | cubic-bezier(0.2, 0, 0, 1) | everything on screen |
| `--wg-ease-drawer` | cubic-bezier(0.32, 0.72, 0, 1) | drawer and dialog enter only |

Today there are six durations (110 / 140 / 200 / 220 / 280 / 380 ms). Press and hover fold into `fast`, update and disclosure into `base`, drawer into `slow`, and the 380 ms pulse is retired. `linear` is for the spinner and skeleton shimmer only. Tailwind gets `transitionDuration: { fast, base, slow }` and `transitionTimingFunction: { standard, drawer }` mapped to these variables, so a raw `duration-150` becomes the lint failure.
*Reduced motion* is handled once, at the root: under `prefers-reduced-motion: reduce` the three tokens become 0.01 ms, animations run once, scroll is `auto`. 120 ms opacity crossfades stay. Essential state motion (spinner, determinate progress) is marked `data-essential-motion` and kept.

Required response states for every interactive control:

| State | Treatment | Timing |
|---|---|---|
| Hover (pointer only, `@media (hover:hover)`) | tint to `surface-sunken` / `border-line-strong`; no lift, no scale | `fast` |
| Press | colour step down; tiles may scale to 0.985 | `fast` |
| Focus | 2 px `focus` ring, 2 px offset, on `:focus-visible`; never removed | 0 ms |
| Working | after 100 ms: label becomes a gerund ("Saving..."), 16 px spinner, `aria-busy`, width locked, control disabled | spinner linear |
| Done | inline check + "Saved" text in the live region for 3 s; the changed value crossfades once (`wg-settle`); never colour alone | `base` |
| Error | message under the control, danger border and icon; no shake | none |

*Skeleton or spinner.* Skeleton when the layout is known and the wait exceeds 300 ms: it appears after a 150 ms delay, shows for at least 300 ms, matches the real shape (at most 8 rows), and is `.kx-skeleton` only. Spinner only inside a button, or inline for an unknown wait under 10 s. Over 10 s: determinate progress, or text with a cancel or "keep working in background" choice (payroll runs). Never both on one region, never a blank screen, never `animate-pulse` on content.
*Mirrored transitions.* A drawer enters from the inline-end edge (`rtl:animate-slide-in-left`, existing). A step panel moves 16 px in the direction of progress, so forward is toward inline-start; step-strip progress fills from inline-start; directional icons use `rtl:rotate-180`. Keyboard-initiated step changes are instant. Route enter is a 120 ms opacity fade with no translate (today `AppLayout` re-keys `main` with a 350 ms `animate-fade-in-up` on every navigation).
*Count-up* is allowed only for a hero KPI that is the result of the user's action (a run total after Run; a Company KPI on first load): once per mount, at most 600 ms, ease-out; never on refresh, polling or filter change; never on a number the user reads or compares (tables, balances, IBANs, payslip lines, amounts over 7 digits). The final value is in the DOM at once (`aria-label`, `data-value`), `tabular-nums` prevents width shift, and reduced motion jumps to the end. Only `ui/CountUp` may do it.
*Banned:* looping decoration (`animate-pulse` on dots or badges, `animate-ping`, glow, breathe; 64 `animate-pulse` uses exist today); parallax or scroll-linked effects on any authenticated working screen; hover scale on cards or rows; blur-in entrances on tables and forms; list stagger over 8 items or on data reload; 3D tilt (`wg-tile3d`, 1 use); charts that redraw on refresh (first mount only); auto-advancing carousels; toasts that shift layout; any animation on data the user is currently reading. Only the login surface and platform console are exempt.

## 5. Wayfinding and words

- **One home per thing.** Each noun (grades, benefits, devices) has one canonical route. Everywhere else is a link labelled "Open in {Home}" (as `ModuleSettingsLink` does), never a second implementation. The left menu has one entry per area; an area's configuration lives in Setup under that area and is reached from the operational screen by one contextual link. No same-label entries; no top tabs that duplicate menu entries. Tabs are for views of one record or area (at most 6; more means group). Depth 2 or more shows "Back to {parent}"; tab and step live in the URL; browser Back works.
- **Button verbs** (one verb per action, sentence case, verb + object). **Add** creates any record or row ("Add grade"). **Save** persists edits ("Save changes", "Save & next", "Save draft"). **Publish** makes configuration live for others, with an effective date. **Submit for approval** sends it (always says "for approval"). **Approve / Reject** decide. **Remove** takes a row out of this list and is recoverable. **Delete** destroys data. **Withdraw** retracts your own request. **Cancel** only abandons the open dialog or edit. **Edit, Open, Export, Import, Run** keep their plain meaning. Banned alone as a label: Submit, OK, Yes, Confirm, Done, Click here. These verbs and their Arabic equivalents are added to `src/i18n/GLOSSARY.md`.
- **Bilingual.** One concept, one Arabic term, from `GLOSSARY.md` (24 entries today; extend to the 40 most-used nouns plus the verbs above), checked by a glossary ratchet shaped like `unit/costAccountingTerminology.spec.ts`. Whole sentences with `{placeholders}`, never concatenation. *Numerals:* Latin digits 0-9 in both languages (already `-u-nu-latn` in `lib/format.ts`), always through `useFormat`; money as "SAR 12,500.00" with `tabular-nums`. *`<bdi>`* around every person name, code (employee, ID, IBAN, cost centre), email, phone and free-text value, in tables and inside sentences (`BdiMessage`). Containers use `min-w`, never fixed widths; Arabic runs about +30%, so labels wrap and never clip (test at 130%). Logical properties only (the RTL ratchet exists); directional icons mirror, others do not; chart time axes stay left to right (`dir="ltr"` on the chart), labels follow the page. User-entered data keeps its own language; only labels switch.
- **Destructive confirm, three tiers.** *Tier 1, reversible or low impact* (remove a draft row, discard an edit): no dialog; act and show Undo for 8 s. *Tier 2, delete or revoke* (delete a record, reset a password, withdraw a request): `useConfirm` danger dialog; the title is verb + named object ("Delete grade G5?"); the body states the consequence with counts ("12 employees are on this grade; they keep their package until you assign another."); the confirm button repeats the verb ("Delete grade"); focus starts on Cancel; Enter does not confirm; busy and error states stay inside the dialog. *Tier 3, irreversible, money or legal* (finalise a payroll run, purge tenant data): Tier 2 plus an impact preview, a typed confirmation of the name or number, a required reason written to the audit log, and step-up MFA where `StepUpHost` applies. `window.confirm` is banned (9 uses today).

## 6. Accessibility floor

- **Contrast:** tokens only. Text 4.5:1, control boundary and focus ring 3:1, both themes (`unit/designTokens.spec.ts` asserts it). No colour-only meaning: a status is chip text plus icon, and "outlined in amber" is not a valid cue.
- **Focus order** = visual order = DOM order; RTL mirrors visually with the same DOM. No positive `tabindex`. A step change moves focus to the step heading; dialogs and drawers trap focus and restore it to the opener (`Modal` does). Sticky footers must not hide a focused field (`scroll-padding-bottom: 96px`). A failed submit focuses the error summary.
- **Targets (Fitts):** pointer at least 28 px (`h-control-sm`), default 36, touch 44; at least 8 px between adjacent targets; the primary is at least 36 x 96; a destructive control is at least 16 px from the primary.
- **Live regions:** one persistent polite `role="status"` region per screen, present before content changes. Async outcomes post text there (saved, "24 results", import finished, run complete). A blocking error uses `role="alert"` once, not both. Loading sets `aria-busy` on the region plus hidden text "Loading {thing}".
- **320 px reflow** (WCAG 1.4.10): no horizontal scroll at 320 CSS px; tables become card lists under 640 px; the only scroll container allowed is a genuine wide matrix, with `tabindex="0"` and a label; rails stack; the step strip collapses to a select.
- **Keyboard:** every action reachable with Tab and Enter/Space; Esc closes dialogs, drawers and the `i`; arrows move within tabs, the step strip and menus (roving); Enter in a single-line input triggers the footer primary (not in textareas); no trap. Honour reduced motion, forced colours and 200% zoom.

## 7. Definition of done for a screen

A reviewer scores each item 0 (fails), 1 (partial), 2 (meets). A new screen merges at 22 of 24 or more with no 0; a changed screen may not score lower than before. **R** = a ratchet or unit test enforces it (today, or once section 8 lands); **P** = a Playwright screen contract; **S** = needs screenshot review (EN and AR; 1366, 1920, 320).

| # | Item | Enforced by |
|---|---|---|
| 1 | One-sentence purpose line, 20-140 characters, EN + AR | P (`[data-purpose]`); wording S |
| 2 | Exactly one primary action, in its zone, verb from section 5 | P (`[data-primary-action]` count and position); verb R (glossary) |
| 3 | Correct screen type, widths and rail; at least 80% of main used at 1920; at least 12 rows at 1366x768 | P (bounding boxes) + S |
| 4 | Spacing, type, radius, elevation and colour from tokens only | R (`lint:tokens` today; add an `arbitrarySpacing` metric) |
| 5 | `i` on every eligible field, 2 sentences, EN + AR, no raw literals | R (info-coverage ratchet); text quality S |
| 6 | Empty (`first-use` / `filtered` / `restricted`), loading (skeleton) and error (Retry) designed, one next action each | P + R (empty-state contract) |
| 7 | Multi-step flow has `StepStrip`, named phases, "Save & next: {Step}", resumable draft | P |
| 8 | Save shows `ResultBanner`: what changed, where you are, next action, in the live region | P |
| 9 | Every refusal says what to do; errors under fields; summary at 2 or more | S + unit test on message templates; R for banned literals ("Failed to") |
| 10 | Motion: tokens only, nothing banned, reduced motion honoured | R (motion ratchet); feel S |
| 11 | Bilingual: keys in en and ar, glossary terms, logical properties, `<bdi>`, Latin digits, RTL checked | R (`i18n-coverage`, `rtl-logical-properties` today); layout S |
| 12 | Accessibility: axe has no serious or critical, keyboard path, 320 reflow, targets, live region | P (axe + overflow) + S |

## 8. Tests that enforce this standard

Rule for the whole program: **every fix starts from a failing test.** The first commit of a UX change adds or tightens one of the checks below and is red on CI; the fix commit turns it green; the reviewer checks the commit order. A ratchet baseline may only be lowered (same shape as `design-token-baseline.json` and `eslint-baseline.json`). A screen is not done until (a), (b) and (c) are green for the files it touches, so no integration is left half-wired. New browserless specs join the `testMatch` regex in `frontend/e2e/playwright.browserless.config.ts` (CI already runs `npx playwright test -c e2e/playwright.browserless.config.ts`, `npm run lint` and `npm run test:unit`). A new Playwright config must be listed in `e2e/config-manifest.json` or a workflow, or `check-playwright-configs.mjs` fails it as an orphan.

| | Check | Tool and file | What it does; baseline today |
|---|---|---|---|
| a | **Route wiring** | Browserless spec `frontend/e2e/route-wiring.spec.ts` + `route-wiring-baseline.json` | Builds the route set from `app/**/page.tsx` (106 pages; `(group)` stripped, `[param]` a wildcard). Parses TS with the `typescript` API (as `i18n-coverage.spec.ts` does) for every internal `href="/..."` (93), `` href={`/...`} `` (38), `router.push/replace` (17), `redirect(`, and the route tables (`setupAreas`, nav items, command palette). Strips query and hash; fails on a literal that resolves to no page. Existing misses are pinned and may only shrink. |
| b | **API wiring, no 501** | xUnit `backend-dotnet/Zayra.Api.Tests/Security/FrontendApiWiringRatchetTests.cs` (repo-root walk, as `AiAdvisoryServiceTests` does) | Extracts every `client.get/post/put/patch/delete('/api/...')` literal in `frontend/src/api/*.ts` (`${x}` becomes `{*}`) and matches method + path against controller routes by reflecting over `ControllerBase` types (107 controller files), not by regex. Fails on no match. Also fails if the matched action is in the existing `status501` set of `Architecture/architecture-baseline.json` (Qiwa stubs today) unless the caller sits behind a capability flag. Baseline list pinned, shrink only. |
| c | **Dead controls** | Browserless spec `frontend/e2e/dead-controls.spec.ts` | AST scan for `onClick={() => {}}`, `onClick={noop}`, `href="#"`, `href=""`, `javascript:`, a `<button>` with no handler, type or form, and "coming soon" / "not available yet" / TODO / lorem in JSX text or `t()` keys. Hard zero (today: 0, 0, and one comment in `CapabilityNote.tsx`, allow-listed because that component states its reason). |
| d | **`i` coverage** | New `frontend/scripts/info-coverage-ratchet.mjs`, added to `npm run lint`; `info-coverage-baseline.json` | Step 0: replace the 15 local `Field`s with `ui/Field` (otherwise the metric cannot be measured). Then per file count `<Field>` with `info`/`infoKey`, with `noInfo="reason"`, and with neither; `withoutInfo` may only go down; a new file starts at 0. `infoNotTranslated` (24 today) must reach 0: add `info`, `hint`, `noInfo` to the attributes `i18n-coverage.spec.ts` counts as hard-coded. |
| e | **Empty-state contract** | Browserless `frontend/unit/emptyStateContract.spec.ts` (`npm run test:unit`) + a contract in (g) | Source scan: every `<EmptyState>` has `action` or `kind="restricted"`; `border-dashed` blocks outside `EmptyState` ratchet down from 35. Per screen, (g) mocks an empty and a filtered response and asserts `[data-empty-state]` holds exactly one button or link (or the restricted text). |
| f | **Motion tokens** | Extend `frontend/scripts/design-token-ratchet.mjs` (in `npm run lint`) + `unit/motionTokens.spec.ts` | New metrics: `rawDurationClass` (`duration-\d+`, 39 today), `rawTransitionDuration` (3), `rawCssTime` (ms or s literals in `animation:`/`transition:` outside the token block), `ambientLoop` (`animate-pulse` 64, `animate-ping` 3, `animate-bounce` 1, `infinite`), `hoverScale` (0), `tile3d` (1). `duration-fast/base/slow` are allowed. The unit test parses `index.css`, asserts the three tokens exist and are 0 under reduced motion. |
| g | **Per-screen contracts** | `frontend/e2e/screen-contract/*.spec.ts` + helper `contract.ts`, run by a new `playwright.screen-contract.config.ts` (route-mocked like `setup-studio-workspace`) | `expectScreenContract(page, { type, multiStep?, saveFlow? })` runs the matrix **{en, ar} x {1366x768, 1920x1080, 320x640}**. Asserts: `[data-purpose]` 20-140 characters; exactly one `[data-primary-action]` in its zone (mirrored for `ar`); `[data-step-strip] [aria-current=step]` on multi-step flows; after save a `[role=status]` shows `[data-result]` with a next-action button; `dir="rtl"` and `lang="ar"` for `ar`; no horizontal overflow at 320 (`scrollWidth <= clientWidth`); at 1920 the occupied share of main is at least 0.8; axe has no serious or critical (`@axe-core/playwright`, already a dependency). |

Order of work: primitives; then (a), (b), (c) (cheap, and they catch real breakage); then (d)-(f) baselines; then one (g) contract per screen as that screen is migrated.
