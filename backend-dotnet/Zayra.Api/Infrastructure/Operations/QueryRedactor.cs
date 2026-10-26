namespace Zayra.Api.Infrastructure.Operations;

public static class QueryRedactor
{
    public static string Redact(string? query) => query ?? "";
}
