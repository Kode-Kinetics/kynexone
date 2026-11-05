namespace Zayra.Api.Application.Organization;

/// <summary>
/// Another writer saved the same organisation code first: the database's unique index refused this save after the
/// duplicate pre-check had passed. Answered as 409 <see cref="ErrorCode"/> by the global exception handler. Deliberately
/// NOT an <see cref="InvalidOperationException"/>, which the organisation controllers map to 400.
/// </summary>
public sealed class OrgCodeConflictException(string entity, string code)
    : Exception($"{entity} code '{code}' was just taken by someone else. Choose a different code, or reload and edit the existing {entity.ToLowerInvariant()}.")
{
    public const string ErrorCode = "code_taken";
}
