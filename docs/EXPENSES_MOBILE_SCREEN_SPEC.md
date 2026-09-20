# Expenses — mobile screen spec (W2-B)

Audience: the KynexOne mobile app team. This document specifies the employee-facing "My expenses"
flow against the `api/ess/expenses` endpoints that ship with W2-B. It changes nothing in the mobile
repository; it is the contract the screens are built to.

## 1. Scope

Employees, on their own behalf only:

- see their expense claims and where each one is (draft, awaiting approval, approved, in a payroll
  run, paid, rejected with the reason);
- create a claim with one or more lines (date, category, amount, description);
- attach one receipt per line (camera or file);
- submit a draft for approval; cancel a draft.

Out of scope on mobile: approving claims, adding claims to a payroll run, editing category policy.
Those stay on the web (`/expenses`).

## 2. Contract

Base path `api/ess/expenses`. Bearer token as for every other ESS call. The server resolves the
employee from the token; the app never sends an employee id.

| Method | Path | Purpose |
| --- | --- | --- |
| GET | `/config` | Currency, receipt limits, categories with policy. Load once per session. |
| GET | `/?status=&page=&pageSize=` | The caller's claims, newest first (paged: `items`, `total`, `page`, `pageSize`). |
| GET | `/{id}` | One claim with lines and approval progress. |
| POST | `/` | Create a draft. Body: `{ title?, lines: [{ expenseDate, categoryCode, amount, description }] }`. |
| PUT | `/{id}` | Replace a draft's lines. Lines carrying an `id` keep their receipt; lines without one are new; omitted lines are deleted. |
| POST | `/{id}/lines/{lineId}/receipt` | Multipart, field name `file`. Replaces any previous receipt on that line. |
| GET | `/{id}/lines/{lineId}/receipt` | Download the receipt (binary, `Content-Disposition` set). |
| POST | `/{id}/submit` | Draft → Submitted (routes into approval). |
| POST | `/{id}/cancel` | Draft → Cancelled. |

Errors are one shape everywhere: `{ code, message, violations?: [{ lineNumber, code, message }] }`.

- `422` — policy or input problem. Render `violations[]` as a list; each has a `lineNumber` (may
  be null for claim-level problems such as a category cap). `code` values the app should recognise:
  `receipt_required`, `category_cap_exceeded`, `unknown_category`, `amount_not_positive`,
  `future_date`, `receipt_type`, `receipt_too_large`, `approval_route_not_configured`.
- `409` — the claim is not in the state the action needs (e.g. editing a submitted claim, or a
  concurrent change). Reload the claim and show `message`.
- `404` — not the caller's claim, or gone.
- `403` with `code: "ess_forbidden"` — the login is not linked to an employee, or lacks `ess.write`.
  Show `message` and hide the create button.

`config` response:

```json
{
  "currency": "SAR",
  "multiCurrencySupported": false,
  "maxReceiptBytes": 10485760,
  "receiptContentTypes": ["application/pdf", "image/jpeg", "image/png", "image/webp", "image/heic", "image/heif"],
  "categories": [
    { "id": "…", "code": "TRAVEL", "nameEn": "Travel", "nameAr": "سفر", "isActive": true, "maxAmountPerClaim": 1000, "receiptRequiredAbove": 200 }
  ]
}
```

Claim statuses: `Draft`, `Submitted`, `Approved`, `Rejected`, `Scheduled`, `Paid`, `Cancelled`.
A `Submitted` claim carries `approval: { status, currentStepOrder, currentApproverName,
currentApproverRole, dueAtUtc }`.

## 3. Screens

### 3.1 My expenses (list)

- Entry: Self-service home → "My expenses" tile. Also reachable from the payslip screen when a
  payslip contains an `Expense Reimbursement` earning (deep link to the claim).
- Header: "My expenses". Sub-line: "Paid with your salary, in {currency}." (from `config`).
- Segmented filter: All · Drafts · Awaiting approval · Paid. Maps to `status` = none / `Draft` /
  `Submitted` / `Paid`.
- Row: title (fallback: claim number), claim number, total in `currency`, status chip, one-line
  progress note:
  - Submitted: "Step {n}: waiting for {approver}"
  - Approved: "Approved — will be paid with the next payroll run"
  - Scheduled: "In the {payrollPeriod} payroll"
  - Paid: "Paid in the {payrollPeriod} payroll"
  - Rejected: "Rejected: {rejectionReason}" (rose)
- Empty state: receipt illustration, "No expense claims yet", CTA "New claim".
- Loading: 3 skeleton rows. Error: inline message with Retry. Pull to refresh.
- FAB / primary button: "New claim" — hidden when `config` returned 403.
- Pagination: infinite scroll on `page`.

### 3.2 Claim detail

- Header: claim number; title; status chip; total; progress note as above.
- Lines list: date, category name, description, amount, receipt indicator (paper-clip). Tap a
  receipt to open it (GET receipt → share sheet / in-app viewer).
- Draft actions (bottom bar): "Edit lines", "Submit for approval" (primary), overflow → "Cancel
  draft" (confirm dialog).
- Non-draft: read-only. Rejected shows the reason prominently.
- On `submit` 422 with `receipt_required`: highlight the offending line(s) by `lineNumber`, show the
  message, offer "Attach receipt" inline.
- On `submit` 422 `approval_route_not_configured`: show message verbatim ("…ask your administrator
  to configure an approval workflow…"). The claim stays a draft; do not retry automatically.

### 3.3 New / edit claim

- Sheet or full screen. Title field (optional, 200 chars).
- Currency banner: "All amounts in {currency}. Multi-currency is not supported." Never show a
  currency picker.
- Lines (min 1, max 50). Each line:
  - Date (date picker, max today).
  - Category (picker from `config.categories`, active only). Under the picker, the policy hint
    when present: "max {maxAmountPerClaim} per claim" and/or "receipt needed above
    {receiptRequiredAbove}".
  - Amount (decimal keyboard, 2 dp, > 0).
  - Description (required, 500 chars).
  - Remove line (disabled when it is the only line).
- Footer: running total; "Save draft".
- Save → `POST` (new) or `PUT` (existing, keep `id` on kept lines). On 422 render `violations[]`
  next to the line named by `lineNumber`; claim-level ones above the list.
- After a successful create, land on the detail screen with a hint: "Attach receipts, then
  submit."

### 3.4 Attach receipt

- From the detail screen on a draft line: "Attach receipt" → action sheet: Take photo · Choose
  photo · Choose file.
- Client-side checks before upload: size ≤ `maxReceiptBytes`, MIME in `receiptContentTypes`
  (HEIC from the camera is accepted server-side; no need to transcode). Reject with the same
  wording the server uses: "Receipts can be at most 10 MB." / "Receipts must be a PDF, JPEG, PNG,
  WEBP or HEIC file."
- Upload as multipart with field `file`; show per-line progress; on success the line's paper-clip
  appears and the returned claim replaces local state.
- Replacing: the same action on a line that already has a receipt ("Replace receipt").

## 4. Behaviour rules

- Offline: drafts may be composed offline and saved on reconnect; receipts are uploaded only
  online. Never queue `submit` offline — submission must show the server's answer.
- Optimistic updates: none. Every action replaces local state with the server's returned claim.
- Notifications: the existing push channel carries approval decisions (entity `ExpenseClaim`).
  Tapping opens the claim detail.
- Accessibility: status is conveyed by text, not colour alone; amounts read as
  "{amount} {currency}"; every line control is labelled with its line number.
- Localisation: category names come as `nameEn`/`nameAr`; pick by app locale. Dates in the
  device locale; amounts with 2 decimals.

## 5. Not supported (and what the UI says)

- Per-line currency or FX: "Multi-currency is not supported. Enter the amount in {currency}."
- Editing after submission: "This claim is {status}; only a draft can be changed." (server 409).
- Deleting a receipt without replacing it: not offered; replace instead.
- Choosing which payroll run pays the claim: payroll decides; the app shows the period once known.
