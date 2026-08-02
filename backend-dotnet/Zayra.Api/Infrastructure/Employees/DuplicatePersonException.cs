namespace Zayra.Api.Infrastructure.Employees;

/// <summary>
/// Thrown by the authoritative create backstop when a STRONG national-identity duplicate exists and the
/// caller has not set the explicit "create anyway" acknowledgement. NEVER-SILENT-DUP + NEVER-HARD-BLOCK:
/// the controller maps this to an advisory 409 { error:"possible_duplicate", matches:[…] } that the create
/// modal re-surfaces; re-submitting with AcknowledgeDuplicate records an audited override and proceeds.
/// Only STRONG matches raise this (S1) — probable name+DOB / passport-only matches are surfaced by the
/// advisory pre-check and never hard-stop the commit.
/// </summary>
public sealed class DuplicatePersonException : System.Exception
{
    public IReadOnlyList<DuplicateMatch> Matches { get; }

    public DuplicatePersonException(IReadOnlyList<DuplicateMatch> matches)
        : base("A possible existing employee was detected for this identity.")
    {
        Matches = matches;
    }
}
