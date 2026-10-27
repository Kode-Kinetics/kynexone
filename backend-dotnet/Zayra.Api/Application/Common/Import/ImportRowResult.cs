namespace Zayra.Api.Application.Common.Import;

public record ImportRowResult(
    int RowNumber,
    string? EntityCode,
    string? EntityName,
    ImportRowStatus Status,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string?>? ErrorsAr = null,
    IReadOnlyList<string?>? WarningsAr = null);

public enum ImportRowStatus { Ok, Warning, Error, Skipped }
