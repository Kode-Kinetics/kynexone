namespace Zayra.Api.Application.Esign;

public sealed record ClickSignFacts(
    string StoredSha256, string? ClaimedSha256, Guid SignerUserId, Guid EmployeeUserId, bool StepUpFresh,
    string? ConsentVersion, string? TypedName, IReadOnlyList<string?> RecordNames, bool DocumentFlaggedTampered, bool AlreadySigned);

public sealed record ClickSignRefusal(int Status, string Code);

public static class ClickSignGuard
{
    public static ClickSignRefusal? Evaluate(ClickSignFacts f) => throw new NotImplementedException();
}
