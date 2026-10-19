# Offer letter designer — local implementation and verification

Date: 2026-10-19. Worktree: `policy-binder-fix`, base HEAD `b25988353766c46a782b872e749680669d8b2ebb`. Changes are uncommitted alongside preserved, unrelated work. This is local verification, not a production release or whole-recruitment certification.

## Open and use

- Isolated local preview: http://127.0.0.1:5198/recruitment/offer-templates
- Normal navigation: Recruitment → Offers → Letter designer.
- Choose a template or start a new one. Customize, inspect the actual sample PDF, then save.
- Both Create Offer and the application pipeline's Generate Offer select a saved template. The offer stores a complete snapshot, including assets and merged values. Later template edits or archival do not alter that offer's PDF.
- `Save as copy` supports company-specific variants and one-off designs without changing a shared template.
- The original frontend on 5197 and original databases were not updated by this feature rollout.

## Implemented

- Content: ordered headings, clauses, tables, images, signature/acceptance sections, dividers, spacing and explicit page breaks; enable, reorder, duplicate and remove sections.
- Branding: uploaded logo, dimensions/alignment, accent/text/paper colors, section backgrounds, letterhead, footer and page border.
- Typography: included Lato and Noto Sans Arabic; one uploaded TTF/OTF family; body/heading fonts and sizes, line spacing, per-section size/color/bold/italic/underline overrides.
- Layout: A4/Letter/Legal, portrait/landscape, margins, page numbers and explicit LTR/RTL document direction.
- Signatures: signatory name/title, signature and stamp images, acceptance wording. Uploaded signatures are images, not a verified e-signature service.
- Merge fields: real candidate/company/role/department/dates/compensation/currency values; protected built-in keys and editable custom fields.
- Versioned shared/company templates, optimistic conflict handling, archive and audit events. Tenant/company access is enforced server-side.
- Real PDF preview and final PDF use the same renderer. Invalid layouts/assets/tokens show errors; stale previews are dimmed and labelled.
- Template JSON import/export includes assets; imports are validated before applying and never autosave.
- Unsaved-navigation protection, loading locks, mobile Edit/Preview switching, translated English/Arabic interface and direction-aware keyboard tabs.

## Verification evidence

| Check | Result |
| --- | --- |
| Focused backend test suite | 64 passed, 0 failed, 0 skipped |
| Localization unit tests | 4 passed |
| TypeScript | `npx tsc --noEmit --pretty false` passed after corrections |
| Whitespace | `git diff --check` passed |
| Live API | Login, template persistence, direct offer creation, PDF download, snapshot stability after template change, pipeline generation, unsafe remote image rejection passed |
| Final read-only smoke | Login 200, saved template available, preview 200 with valid 50,701-byte PDF |
| Browser | Save/reopen template, selection in creation form, dirty-navigation Cancel, real PDF, 390px mobile Edit/Preview, desktop and RTL controls checked |
| RTL checks | Arabic UI; ArrowLeft advances tabs, ArrowRight reverses; back icon mirrored; document wording unchanged by interface language |
| Width checks | Mobile document width 386 at viewport 390; desktop RTL width 1724 at viewport 1728 |
| Independent finish review | Two findings (sticky overlap and localization) corrected; reviewer scored both resolved. Verdict applies to those fixes, not whole-product certification. |

Backend suite includes `OfferLetterDesignTests`, `OfferTemplatePersistenceTests`, `RecruitmentHireJourneyTests`, `RecruitmentOfferMakerCheckerTests`, `RecruitmentCompanyScopeTests`, `OfferAcceptanceExactlyOnceTests` and `OfferPlacementTests`.

TRX: `/tmp/kynex-offer-designer-tests/offer-designer-final.trx`.

Frontend command: `npx playwright test -c playwright.unit.config.ts unit/offerLetterI18n.spec.ts`. This is a browserless unit suite; actual browser checks used the connected browser.

Screenshots: `.impeccable/review/offer-designer/desktop.png`, `mobile.png`, `mobile-preview.png`, `desktop-rtl.png`, `desktop-rtl-scrolled.png`, `mobile-rtl.png`, `mobile-preview-rtl.png`. These are viewport captures: Chrome's native PDF viewer does not reliably paint offscreen content in full-page captures.

## Persistence and isolation

- Additive migration `20261019043220_OfferLetterDesigner`: nullable `offer_letters.design_snapshot_json` plus `offer_letter_templates` and indexes. Existing offer records retain the legacy fallback.
- Applied only to `kynex_offer_shadow_20261019`, cloned from the local policy-shadow database on port 55447. Its local QA admin login was changed only inside that clone.
- A separate initial clone, `kynex_offer_designer_20261019` on port 55433, was abandoned after an existing migration/schema mismatch (`benefit_plans.classification` already present). It is unused; original data was not repaired or modified.
- Synthetic recruitment records and draft offers were created only in the isolated shadow clone. No offers were sent externally.
- QA API on 5128 is started through `/tmp/kynex-offer-designer-runtime.mjs`; macOS sandbox policy denies non-loopback outbound connections. Jobs/integrations are disabled for this instance.
- QA frontend uses `NEXT_DIST_DIR=.next-offer-designer NEXT_PUBLIC_API_BASE_URL=http://127.0.0.1:5128 npm run dev -- --hostname 127.0.0.1 --port 5198`. This preserves the existing `.next` runtime. Restart depends on local containers and the temporary API launcher remaining available.

## Explicit limits and release work

This is a structured, template-based PDF designer—not an unrestricted desktop-publishing canvas. It does not implement arbitrary positioned elements, multiple uploaded font families, DOCX import/export, electronic-signature-provider integration or an editor for an already-created offer's saved design. For a one-off new offer, save a template copy and select it when creating the offer.

Limits are surfaced in the interface and validated server-side: up to 50 sections, 30 rows per table, 30 custom fields, 250 active templates; PNG/JPEG assets up to 500 KB and 4000 px/8 MP; uploaded font up to 1.5 MB; complete design up to 6 MB. User-owned font embedding rights remain the user's responsibility.

The customized output is the PDF download path. The existing legacy HTML endpoint remains legacy HTML and is not a WYSIWYG export of the new designer. Existing outer Recruitment UI has pre-existing untranslated strings outside this feature's localization scope.

Not performed: production deployment/migration, full production Next build, complete application regression suite, formal accessibility certification, or exhaustive native browser/font compatibility. Source review, production build/CI and an approved deployment remain release steps.
