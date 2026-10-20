---
name: KynexOne offer letter designer
description: Implemented recruitment template authoring surface within the existing enterprise application.
---

# Offer letter designer — implemented surface

## Overview

This is an operate-mode extension of Recruitment → Offers, also reachable at `/recruitment/offer-templates`. It retains the existing KynexOne shell, company context, sapphire/slate identity and enterprise form language. Root `PRODUCT.md` and `DESIGN.md` remain authoritative; this record describes only the offer-letter surface and introduces no global tokens, visual world or composition standard.

The workspace supports reusable, versioned letter designs with editable wording, branding, typography, page layout, signatures and merge fields. The route uses the existing recruitment permission gate. The template selector in offer creation displays saved versions and explains that the chosen design is copied into the offer.

## Colors

The application uses the incumbent sapphire primary save action, blue active-tab treatment, slate text and boundaries, white/light panels and existing dark variants. Errors use rose, confirmations emerald, and scope/archive notices amber. These are application states, separate from the customer's document colors.

Document controls expose accent, body-text and paper colors, plus optional block text/background overrides. Customer-selected letter colors do not replace the application palette. Native color inputs provide direct selection; server validation requires six-digit hex values.

## Typography

Application chrome inherits Geist and IBM Plex Sans Arabic from the existing system. The designer title is a compact semibold task heading (24px); controls and field labels use the established 14px form scale, with 12px help/status text. No new display font is introduced.

Letter body and heading families are independently selectable from Lato, Noto Sans Arabic and one uploaded custom font family. A custom upload occupies one shared font slot and initially selects Custom for both roles; this is not a multi-family font library. Body size supports 8–24pt, heading size 12–48pt and line spacing 1–2.5. Supported blocks can override font size (8–48pt), color, bold, italic and underline or restore template typography.

**Independent language and document direction.** Application locale translates interface copy and determines UI direction. Document reading direction is an explicit Layout choice; switching the UI to Arabic does not translate authored wording or change the document direction. User text, names, merge keys and font names remain authored content. The Arabic screenshots intentionally show an English sample letter inside an Arabic interface.

## Layout

The first controls identify the task and next action, followed by template library, template name, copy/archive actions, version/dirty state and company/shared scope. Both the designer action header and PDF preview remain in normal document flow. Their former sticky treatment was removed after overlap review; do not reintroduce overlapping sticky layers within the application shell.

On large screens, controls and PDF preview sit beside one another in a grid: the control column is 320–420px and the preview takes the remaining available width. Below the large-screen breakpoint, explicit **Edit design** / **PDF preview** buttons select a single visible pane. Each pane can shrink within its parent, and action rows wrap. The controls remain a three-column, two-row tab group on phones.

The preview is a server-generated PDF in an iframe, with a sample download fallback. It uses fictional candidate/salary values and the same renderer used for offer PDFs. A 650ms debounce updates the preview after design changes; updating/error states dim the last preview, announce status and prevent saving/downloading a stale result. An error explicitly explains that the last valid preview may remain visible. This is a paginated document preview, not an editable canvas.

## Elevation & Depth

The surface reuses the existing application panels and restrained elevation. Borders and pale surfaces separate library, controls, preview and disclosures. The paper is framed by the PDF viewer; it does not introduce a separate application depth system.

## Shapes

Retain existing rounded panels and controls. Inputs/buttons use gently rounded corners (8px), while the preview enclosure follows the incumbent panel shape (12px). The document's optional page border is a customer-controlled letter feature, independent of application container borders.

## Components

The six control tabs use tab/tabpanel semantics, a single tab stop, Home/End, and direction-aware arrow navigation. The selected tab is highlighted without changing the underlying data model.

| Tab | Implemented controls |
| --- | --- |
| Content | Ordered structured blocks: heading, paragraph/clause, details/compensation table, image, signatures/acceptance, divider, spacing and page break. Select, move up/down, duplicate, delete, include/hide and add blocks; edit wording/table rows; insert merge fields at the text cursor; disclose block styling. |
| Branding | Company logo upload/removal, width and alignment; accent/body/paper colors; repeating letterhead and footer text; optional page border. |
| Typography | Independent body/heading font choices and sizes, line spacing, one embedded custom TTF/OTF font upload/removal. |
| Layout | A4/Letter/Legal, portrait/landscape, margins, explicit LTR/RTL reading direction and page numbers. Long letters paginate; page-break blocks provide explicit breaks. |
| Signatures | Signatory name/title, signature image, company stamp and candidate acceptance wording, rendered where the signature block is placed. |
| Merge fields | Built-in offer fields, custom field values, add/remove custom keys. Built-in values are protected from template overrides. |

**Template lifecycle and scope.** New, save, save-as-copy and archive operate on templates. Updates and archive submit the current version for concurrency checks. The library and status show version and shared/company scope. Admin, HR Manager or Recruiter roles can save; a shared template is read-only from company scope, with Save as copy available to create a company customization. Shared editing requires the group scope without a selected company. These UI affordances accompany server authorization, not replace it.

Template selection at offer creation freezes the design, resolved values, template identifier and version in the offer's snapshot. The saved snapshot drives subsequent PDF rendering; editing or archiving the source template does not rewrite issued letters. This designer does not directly edit an existing offer's saved design.

**Recoverable authoring.** A dirty-state navigation guard protects unsaved changes. Archive requires inline confirmation. Errors and completion notices use alert/status semantics. Import/export is a secondary disclosure: JSON export includes uploaded assets; import validates schema/renderability, loads locally and requires an explicit save. Standard letter selection remains available when the template library fails to load.

**Asset and structure limits.** Images are PNG/JPEG, at most 512,000 bytes (displayed as 500 KB), 4000px per dimension and 8 megapixels; SVG and external image URLs are not accepted. The single custom TTF/OTF is at most 1,500,000 bytes and must be licensed for embedding. Template JSON import is limited to 6,000,000 bytes; design assets are limited to 6 MB. Designs contain 1–50 blocks, tables at most 30 rows, and at most 30 custom merge fields. Logo width is 24–240pt, section image width 24–300pt, margins 18–90pt and section spacing 0–60pt. API validation remains authoritative.

## Do's and Don'ts

- **Do** preserve the distinction between application branding and editable document branding, application locale and document direction, and reusable templates and frozen offer snapshots.
- **Do** retain the normal-flow header/preview, wrapping actions, mobile pane switch, keyboard tabs, visible scope/version state and unsaved-change protection.
- **Do** describe the implementation as native structured block editing with PDF preview and JSON interchange.
- **Don't** imply arbitrary drag-and-drop canvas composition, DOCX import/export, a multi-family uploaded font library or direct editing of existing offer designs; these are not implemented here.
- **Don't** describe uploaded signature/stamp images as an electronic-signature integration or verified signature service.
- **Don't** turn the sample letter's wording, color choices or composition into global KynexOne design rules.

Evidence for this record: `frontend/src/components/OfferLetterDesigner.tsx`, `OfferTemplateSelect.tsx`, `frontend/src/i18n/offerLetter.ts`, `frontend/app/(dashboard)/recruitment/offer-templates/page.tsx`, and the snapshot/image-validation paths in `backend-dotnet/Zayra.Api/Infrastructure/Documents/Letters/OfferLetterDesign.cs` and `Controllers/Recruitment/OffersController.cs`. Visual inspection used `.impeccable/review/offer-designer/desktop.png`, `mobile.png`, `mobile-preview.png`, `desktop-rtl.png` and `mobile-rtl.png`.

Verification handoff reported 64 backend tests, four translation unit tests, TypeScript checking, and desktop/mobile RTL browser checks including no horizontal overflow and keyboard navigation. This documentation pass did not rerun those checks or launch a browser. Header overlap and localization findings were corrected in the inspected implementation; the finish reviewer returned a ship verdict for those scored fixes and reported no regressions from the fix batch. Detailed evidence and limits are recorded in `docs/OFFER_LETTER_DESIGNER_VERIFICATION.md`. This record is not a deployment or formal acceptance claim.
