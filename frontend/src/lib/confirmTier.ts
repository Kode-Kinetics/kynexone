/**
 * The three confirmation tiers of the Screen Standard (section 5), as pure rules.
 *
 *   Tier 1  reversible or low impact (remove a draft row, discard an edit): no dialog, act and offer Undo for 8 s.
 *   Tier 2  delete or revoke (delete a record, reset a password, withdraw a request): a danger dialog. The title is
 *           verb + named object, the body states the consequence with counts, the confirm button repeats the verb,
 *           focus starts on Cancel and Enter does not confirm. Busy and error states stay inside the dialog.
 *   Tier 3  irreversible, money or legal (finalise a payroll run, purge tenant data): tier 2 plus an impact preview,
 *           the name or number typed back, a required reason written to the audit log. Step-up MFA, where it applies,
 *           is the server's challenge on the confirmed request (StepUpHost), so it needs nothing here.
 */
export type ConfirmTier = 1 | 2 | 3;

export const UNDO_WINDOW_MS = 8000;

export interface ConfirmRequirements {
  tier: ConfirmTier;
  /** Tier 3: the exact name or number the user must type back. */
  typeToConfirm?: string;
  /** A reason is required (always for tier 3; optional for tier 2, which is how a `prompt()` becomes a dialog). */
  reasonRequired?: boolean;
  /** The shortest acceptable reason (trimmed). A reason is "required" with at least 1 character unless this says more. */
  reasonMinLength?: number;
}

export interface ConfirmInput {
  typed: string;
  reason: string;
}

export type ConfirmMissing = 'typed' | 'reason';

/** What still stands between the user and the confirm button. */
export function confirmMissing(req: ConfirmRequirements, input: ConfirmInput): ConfirmMissing[] {
  const missing: ConfirmMissing[] = [];
  if (req.tier === 3) {
    // The typed text is compared exactly (after trimming the user's side only): a near miss must not confirm.
    if (req.typeToConfirm === undefined || input.typed.trim() !== req.typeToConfirm) missing.push('typed');
  }
  const reasonNeeded = req.tier === 3 || req.reasonRequired === true;
  if (reasonNeeded && input.reason.trim().length < (req.reasonMinLength ?? 1)) missing.push('reason');
  return missing;
}

export const canConfirm = (req: ConfirmRequirements, input: ConfirmInput) => confirmMissing(req, input).length === 0;

/** Tier 1 shows no dialog at all. */
export const showsDialog = (tier: ConfirmTier) => tier !== 1;
