using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;

namespace Zayra.Api.Infrastructure.Operations;

public static class LedgerProtection
{
    public const string Enforced = "enforced";
    public const string Degraded = "degraded";

    public static void ResetForTests() { }
    public static Task<string> ReportAsync(Func<Task<string>> probe, Microsoft.Extensions.Logging.ILogger log, bool force = false) => probe();
    public static Task<string> ProbeAsync(ZayraDbContext db, CancellationToken ct) => Task.FromResult(Enforced);
}
