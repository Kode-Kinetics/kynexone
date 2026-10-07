using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Opt-in measurement (not part of the normal suite): the employee import's preview and commit of a file at the row
/// cap, with the peak managed heap and the time of each. Run under a hard heap limit to prove the cap fits:
/// <code>KYNEX_IMPORT_MEMORY_PROBE=1 DOTNET_GCHeapHardLimit=0x13000000 dotnet test --filter EmployeeImportMemoryProbe</code>
/// The figures are appended to $KYNEX_IMPORT_MEMORY_OUT when set.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class EmployeeImportMemoryProbe
{
    private readonly PostgresFixture _fixture;
    public EmployeeImportMemoryProbe(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task PreviewAndCommitAtTheRowCap_StayInsideTheHeap()
    {
        if (Environment.GetEnvironmentVariable("KYNEX_IMPORT_MEMORY_PROBE") != "1") return;
        var rows = EmployeesController.MaxImportRows;

        Guid tenant;
        await using (var seed = _fixture.CreateDb())
        {
            tenant = await PostgresFixture.SeedMinimalTenant(seed);
            seed.TenantSubscriptions.Add(new TenantSubscription { TenantId = tenant, Plan = "Enterprise", Status = "Active", MaxEmployees = 10_000 });
            seed.Companies.Add(new Company { TenantId = tenant, LegalNameEn = "Probe Co", CountryCode = "SA", Jurisdiction = "SA", RegistrationNumber = $"P-{Guid.NewGuid():N}"[..20], DefaultCurrency = "SAR", IsActive = true });
            seed.Grades.Add(new Grade { TenantId = tenant, Code = "G1", Name = "Grade", Currency = "SAR", MinSalary = 1, MaxSalary = 100_000, IsActive = true });
            seed.Departments.Add(new Department { TenantId = tenant, Code = "OPS", NameEn = "Operations", IsActive = true });
            await seed.SaveChangesAsync();
        }
        var csv = "FullName,CompanyLegalName,Department,Grade,BasicSalary,HousingAllowance,Currency,JoiningDate,Nationality,BankName,IBAN,ManagerEmployeeCode\n"
                  + string.Join("\n", Enumerable.Range(1, rows).Select(i =>
                      $"Probe Person {i},Probe Co,Operations,G1,{5000 + i},1500,SAR,2024-01-01,Indian,Probe Bank,SA0380000000608010167519,")) + "\n";

        var info = GC.GetGCMemoryInfo();
        var report = new List<string> { $"heap_limit_mb={info.TotalAvailableMemoryBytes / 1024.0 / 1024.0:F0} env={Environment.GetEnvironmentVariable("DOTNET_GCHeapHardLimit") ?? "unset"} rows={rows}" };
        foreach (var (stage, run) in new (string, Func<Task<IActionResult>>)[]
                 {
                     ("preview", async () => { await using var db = _fixture.CreateDb(); return await HrmHierarchyTests.BuildImportControllerInternal(db, tenant).ImportPreview(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None); }),
                     ("commit", async () => { await using var db = _fixture.CreateDb(); return await HrmHierarchyTests.BuildImportControllerInternal(db, tenant).Import(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None); }),
                 })
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            var baseline = GC.GetGCMemoryInfo().HeapSizeBytes;
            long peak = GC.GetGCMemoryInfo().TotalCommittedBytes, peakAfterGc = baseline;
            var gen2Before = GC.CollectionCount(2);
            using var cts = new CancellationTokenSource();
            var sampler = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    // Committed GC heap as of the last collection — what the hard limit actually bounds. (GetTotalMemory
                    // counts allocation-context bytes too and reads above the limit under a hard limit.)
                    var gc = GC.GetGCMemoryInfo();
                    peak = Math.Max(peak, gc.TotalCommittedBytes);
                    peakAfterGc = Math.Max(peakAfterGc, gc.HeapSizeBytes);
                    try { await Task.Delay(10, cts.Token); } catch (OperationCanceledException) { }
                }
            });
            var sw = Stopwatch.StartNew();
            var result = await run();
            sw.Stop();
            cts.Cancel();
            await sampler;
            Assert.IsType<OkObjectResult>(result);
            report.Add($"{stage}: peak_committed_mb={peak / 1024.0 / 1024.0:F1} peak_live_after_gc_mb={peakAfterGc / 1024.0 / 1024.0:F1} "
                       + $"(baseline {baseline / 1024.0 / 1024.0:F1}) gen2={GC.CollectionCount(2) - gen2Before} seconds={sw.Elapsed.TotalSeconds:F1}");
        }
        await using (var verify = _fixture.CreateDb())
            Assert.Equal(rows, await verify.Employees.IgnoreQueryFilters().CountAsync(e => e.TenantId == tenant));

        var line = string.Join(" | ", report);
        Console.WriteLine(line);
        if (Environment.GetEnvironmentVariable("KYNEX_IMPORT_MEMORY_OUT") is { Length: > 0 } outFile)
            await File.AppendAllTextAsync(outFile, line + Environment.NewLine);
    }
}
