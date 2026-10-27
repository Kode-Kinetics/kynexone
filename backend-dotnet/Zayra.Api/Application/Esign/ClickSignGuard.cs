namespace Zayra.Api.Application.Esign;

public sealed record ClickSignFacts(
    string StoredSha256, string? ClaimedSha256, Guid SignerUserId, Guid EmployeeUserId, bool StepUpFresh,
    string? ConsentVersion, string? TypedName, IReadOnlyList<string?> RecordNames, bool DocumentFlaggedTampered, bool AlreadySigned);

public sealed record ClickSignRefusal(int Status, string Code);

/// <summary>
/// The refusals of a sign request, in the order they are applied, with no I/O. The service loads the facts; this decides.
/// A request is refused for the FIRST reason found: the order is part of the contract (a tampered document is never signable,
/// whoever asks; a person who is not the employee never gets as far as being told what the document hash should be).
/// </summary>
public static class ClickSignGuard
{
    public static ClickSignRefusal? Evaluate(ClickSignFacts f)
    {
        if (f.DocumentFlaggedTampered) return new(409, EsignProblemCodes.DocumentTampered);
        if (f.AlreadySigned) return new(409, EsignProblemCodes.AlreadySigned);
        if (f.SignerUserId != f.EmployeeUserId) return new(403, EsignProblemCodes.SubjectCannotSignForEmployee);
        if (!ConsentCatalog.IsKnown(f.ConsentVersion)) return new(409, EsignProblemCodes.ConsentVersionStale);
        if (!f.StepUpFresh) return new(401, EsignProblemCodes.StepUpRequired);
        if (!string.Equals(f.ClaimedSha256?.Trim(), f.StoredSha256, StringComparison.OrdinalIgnoreCase)) return new(409, EsignProblemCodes.DocumentChanged);
        if (!TypedNameMatcher.Matches(f.TypedName, [.. f.RecordNames])) return new(422, EsignProblemCodes.TypedNameMismatch);
        return null;
    }
}
