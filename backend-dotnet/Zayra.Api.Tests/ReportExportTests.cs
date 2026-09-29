using System.Security.Claims;
using System.Text;
using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers.Reports;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Email;
using Zayra.Api.Infrastructure.Reports;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// F3 — you can now download a report, and the file is what it says it is.
/// </summary>
public sealed class ReportExportTests
{
    /// <summary>The first four bytes of every OPC package (a ZIP local file header).</summary>
    private static readonly byte[] ZipSignature = [0x50, 0x4B, 0x03, 0x04];

    [Fact]
    public async Task Export_Xlsx_ReturnsARealWorkbook_AssertedOnTheFileSignature()
    {
        await using var db = CreateDb();
        var tenantId = await SeedHeadcountAsync(db);
        var controller = Controller(db, tenantId, "reports.read", "reports.export");

        var result = await controller.ExportReport(
            new ExportReportRequest("hr.headcount", null, "xlsx"), CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(result);

        // Not the MIME header we set ourselves — the bytes. An .xlsx is a ZIP; a CSV is not.
        Assert.Equal(ZipSignature, file.FileContents.Take(4).ToArray());
        Assert.Equal(ReportWorkbookWriter.ContentType, file.ContentType);
        Assert.EndsWith(".xlsx", file.FileDownloadName);

        // And it is a workbook OpenXml can open, with the rows in it.
        using var stream = new MemoryStream(file.FileContents);
        using var document = SpreadsheetDocument.Open(stream, false);
        var sheet = Assert.Single(document.WorkbookPart!.Workbook.Sheets!.Elements<Sheet>());
        var rows = ((WorksheetPart)document.WorkbookPart.GetPartById(sheet.Id!)).Worksheet
            .GetFirstChild<SheetData>()!.Elements<Row>().ToList();

        Assert.Equal(3, rows.Count);                       // header + two departments
        Assert.Contains("Department", CellText(rows[0]));
        Assert.Contains("Engineering", CellText(rows[1]).Concat(CellText(rows[2])));

        // A count written as a number, not a string, so the column sums in Excel.
        var countCell = rows[1].Elements<Cell>().Last();
        Assert.Equal(CellValues.Number, countCell.DataType!.Value);
    }

    [Fact]
    public async Task Export_Csv_ReturnsCsvBytes_WithABomSoExcelReadsArabicCorrectly()
    {
        await using var db = CreateDb();
        var tenantId = await SeedHeadcountAsync(db);
        var controller = Controller(db, tenantId, "reports.read", "reports.export");

        var result = await controller.ExportReport(
            new ExportReportRequest("hr.headcount", null, "csv"), CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("text/csv", file.ContentType);
        Assert.EndsWith(".csv", file.FileDownloadName);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, file.FileContents.Take(3).ToArray());
        Assert.NotEqual(ZipSignature, file.FileContents.Take(4).ToArray());

        var text = Encoding.UTF8.GetString(file.FileContents);
        Assert.Contains("\"Department\"", text);
        Assert.Contains("\"Engineering\"", text);
    }

    [Fact]
    public async Task Export_RefusesPdf_RatherThanDeliveringSomethingElse()
    {
        await using var db = CreateDb();
        var tenantId = await SeedHeadcountAsync(db);
        var controller = Controller(db, tenantId, "reports.read", "reports.export");

        var result = await controller.ExportReport(
            new ExportReportRequest("hr.headcount", null, "pdf"), CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("unsupported_export_format", JsonSerializer.Serialize(bad.Value));
    }

    [Fact]
    public async Task Export_RequiresTheExportPermission()
    {
        await using var db = CreateDb();
        var tenantId = await SeedHeadcountAsync(db);
        // reports.read alone is enough to LOOK at a report on screen. Taking the whole,
        // untruncated result out of the product is a separate act with a separate permission —
        // reports.export, which was seeded and mapped and had no consumer at all.
        var controller = Controller(db, tenantId, "reports.read");

        var result = await controller.ExportReport(
            new ExportReportRequest("hr.headcount", null, "csv"), CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task Export_RecordsTheFormatInTheExecutionLog()
    {
        await using var db = CreateDb();
        var tenantId = await SeedHeadcountAsync(db);
        var controller = Controller(db, tenantId, "reports.read", "reports.export");

        await controller.ExportReport(new ExportReportRequest("hr.headcount", null, "xlsx"), CancellationToken.None);

        var log = await db.ReportExecutionLogs.SingleAsync();
        // Every row used to say "JSON", so the log could not answer "did anyone export this?"
        Assert.Equal("xlsx", log.ExportFormat);
        Assert.Equal("Success", log.Status);
        Assert.Equal(2, log.RowCount);
    }

    [Fact]
    public async Task Export_OfAMultiSectionReport_ProducesASheetPerSection()
    {
        await using var db = CreateDb();
        var tenantId = await SeedHeadcountAsync(db);
        var controller = Controller(db, tenantId, "reports.read", "reports.export");

        // hr.nationality-mix returns { byNationality: [...], byGender: [...] } — not a flat list.
        var result = await controller.ExportReport(
            new ExportReportRequest("hr.nationality-mix", null, "xlsx"), CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(result);
        using var stream = new MemoryStream(file.FileContents);
        using var document = SpreadsheetDocument.Open(stream, false);
        var sheets = document.WorkbookPart!.Workbook.Sheets!.Elements<Sheet>().Select(x => x.Name!.Value).ToList();

        Assert.Equal(2, sheets.Count);
        Assert.Contains("By Nationality", sheets);
        Assert.Contains("By Gender", sheets);
    }

    // ── The scheduled artifact ────────────────────────────────────────────────────────────

    [Fact]
    public async Task AScheduledExcelReport_ArrivesAsAWorkbook_NotARenamedCsv()
    {
        await using var db = CreateDb();
        await SeedScheduleAsync(db, exportFormat: "Excel");
        var email = new RecordingEmail();
        using var services = BuildWorkerServices(db, email);
        var worker = new ReportScheduleWorker(
            services.GetRequiredService<IServiceScopeFactory>(), NullLogger<ReportScheduleWorker>.Instance);

        await worker.ProcessOnceAsync(CancellationToken.None);

        var attachment = Assert.Single(email.Messages).Attachment;

        // Before the fix: FileName "Headcount.csv", ContentType "application/vnd.ms-excel",
        // and CSV bytes. The user picked Excel and got a CSV with a lie on the envelope.
        Assert.EndsWith(".xlsx", attachment.FileName);
        Assert.Equal(ReportWorkbookWriter.ContentType, attachment.ContentType);
        Assert.Equal(ZipSignature, attachment.Data.Take(4).ToArray());
    }

    [Fact]
    public async Task ASchedulePdfRequest_IsRefusedAtCreation_NotDeliveredAsACsv()
    {
        Assert.False(ReportSchedulePolicy.TryValidate(
            new CreateScheduleRequest("hr.headcount", "Headcount", "HR", null, "Monthly", "Email",
                "a@b.com", "PDF"), out var error));
        Assert.Contains("csv", error, StringComparison.OrdinalIgnoreCase);

        Assert.False(ReportSchedulePolicy.TryValidate(
            new CreateScheduleRequest("hr.headcount", "Headcount", "HR", null, "Monthly", "SFTP",
                "a@b.com", "csv"), out var deliveryError));
        Assert.Contains("Email only", deliveryError);
    }

    // ── The schedule whose owner walked out ───────────────────────────────────────────────

    [Fact]
    public async Task AScheduleWhoseOwnerIsDeactivated_SurfacesTheFailureToSomebodyWhoCanFixIt()
    {
        await using var db = CreateDb();
        var (tenantId, ownerId) = await SeedScheduleAsync(db, exportFormat: "csv");

        // A second person who still holds reports.schedule — the one who can take it over.
        var rescuerId = await AddScheduleHolderAsync(db, tenantId, "rescuer@example.com");

        // The HR manager who set up the monthly pack resigns.
        var owner = await db.Users.FirstAsync(x => x.Id == ownerId);
        owner.IsActive = false;
        await db.SaveChangesAsync();

        var email = new RecordingEmail();
        using var services = BuildWorkerServices(db, email);
        var worker = new ReportScheduleWorker(
            services.GetRequiredService<IServiceScopeFactory>(), NullLogger<ReportScheduleWorker>.Instance);

        await worker.ProcessOnceAsync(CancellationToken.None);

        Assert.Empty(email.Messages);

        // Still logged, as before…
        var execution = await db.ReportExecutionLogs.SingleAsync();
        Assert.Equal("Failed", execution.Status);

        // …but no longer ONLY logged. The schedule itself now says it is broken, and says why.
        var schedule = await db.ReportSchedules.AsNoTracking().SingleAsync();
        Assert.Equal(1, schedule.ConsecutiveFailureCount);
        Assert.NotNull(schedule.OwnerInvalidatedAtUtc);
        Assert.Contains("inactive", schedule.LastFailureReason, StringComparison.OrdinalIgnoreCase);

        // And a human was told — the person who can repair it, not the person who left.
        var notifications = await db.Notifications.AsNoTracking().ToListAsync();
        Assert.Contains(notifications, n => n.UserId == rescuerId);
        Assert.All(notifications, n => Assert.Contains("stopped", n.Title, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ABrokenScheduleAlertsOnce_NotEveryPeriodForever()
    {
        await using var db = CreateDb();
        var (tenantId, ownerId) = await SeedScheduleAsync(db, exportFormat: "csv");
        await AddScheduleHolderAsync(db, tenantId, "rescuer@example.com");
        var owner = await db.Users.FirstAsync(x => x.Id == ownerId);
        owner.IsActive = false;
        await db.SaveChangesAsync();

        using var services = BuildWorkerServices(db, new RecordingEmail());
        var worker = new ReportScheduleWorker(
            services.GetRequiredService<IServiceScopeFactory>(), NullLogger<ReportScheduleWorker>.Instance);

        await worker.ProcessOnceAsync(CancellationToken.None);
        var afterFirst = await db.Notifications.CountAsync();

        // Make it due again and fail again.
        var schedule = await db.ReportSchedules.SingleAsync();
        schedule.NextRunAtUtc = DateTime.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await worker.ProcessOnceAsync(CancellationToken.None);

        Assert.Equal(afterFirst, await db.Notifications.CountAsync());
        Assert.Equal(2, (await db.ReportSchedules.AsNoTracking().SingleAsync()).ConsecutiveFailureCount);
    }

    // ── Tabulator ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Tabulate_UnionsHeaders_AcrossRowsThatOmitAProperty()
    {
        var data = JsonSerializer.SerializeToElement(new object[]
        {
            new { name = "A", count = 1 },
            new { name = "B", count = 2, extra = "x" },
        });

        var table = Assert.Single(ReportTabulator.Tabulate(data));

        Assert.Equal(["Name", "Count", "Extra"], table.Headers);
        Assert.Equal(["A", "1", ""], table.Rows[0]);
        Assert.Equal(["B", "2", "x"], table.Rows[1]);
    }

    [Fact]
    public void Csv_NeutralisesAFormulaInjection()
    {
        var data = JsonSerializer.SerializeToElement(new[] { new { name = "=cmd|'/c calc'!A1" } });

        var csv = Encoding.UTF8.GetString(ReportTabulator.ToCsv(ReportTabulator.Tabulate(data)));

        Assert.Contains("\"'=cmd", csv);
    }

    [Fact]
    public void Xlsx_KeepsAnIdentifierWithALeadingZeroAsText()
    {
        // "0042" coerced to the number 42 is an employee code that no longer matches anything.
        var data = JsonSerializer.SerializeToElement(new[] { new { code = "0042", amount = "1500.50" } });

        var bytes = ReportWorkbookWriter.ToXlsx(ReportTabulator.Tabulate(data));

        using var stream = new MemoryStream(bytes);
        using var document = SpreadsheetDocument.Open(stream, false);
        var sheet = document.WorkbookPart!.Workbook.Sheets!.Elements<Sheet>().Single();
        var rows = ((WorksheetPart)document.WorkbookPart.GetPartById(sheet.Id!)).Worksheet
            .GetFirstChild<SheetData>()!.Elements<Row>().ToList();
        var cells = rows[1].Elements<Cell>().ToList();

        Assert.Equal(CellValues.InlineString, cells[0].DataType!.Value);
        Assert.Equal(CellValues.Number, cells[1].DataType!.Value);
    }

    [Fact]
    public void SheetNames_AreTrimmedAndDeduplicated_SoExcelWillOpenTheFile()
    {
        var used = new List<string>();
        Assert.Equal("Headcount by department", ReportWorkbookWriter.UniqueSheetName("Head:count by department", used));
        Assert.Equal("Headcount by department_2", ReportWorkbookWriter.UniqueSheetName("Headcount by department", used));
        Assert.True(ReportWorkbookWriter.UniqueSheetName(new string('x', 60), used).Length <= 31);
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────

    private static IEnumerable<string> CellText(Row row) =>
        row.Elements<Cell>().Select(c => c.InlineString?.Text?.Text ?? c.CellValue?.Text ?? string.Empty);

    private static ZayraDbContext CreateDb() => new(new DbContextOptionsBuilder<ZayraDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static ReportsController Controller(ZayraDbContext db, Guid tenantId, params string[] permissions)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()),
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Name, "Reports Tester"),
            // employees.read without manager.read is what DataScopeService reads as
            // organization-wide scope — the shape an HR user's token actually has.
            new("permission", "employees.read"),
        };
        claims.AddRange(permissions.Select(p => new Claim("permission", p)));

        return new ReportsController(db, new DataScopeService(db))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) },
            },
        };
    }

    private static async Task<Guid> SeedHeadcountAsync(ZayraDbContext db)
    {
        var tenantId = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Export Tenant", Slug = $"export-{Guid.NewGuid():N}" });
        db.Employees.AddRange(
            new Employee { TenantId = tenantId, EmployeeCode = "E-1", FullName = "A", Department = "Engineering", Nationality = "Saudi", Gender = "Male", Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-1) },
            new Employee { TenantId = tenantId, EmployeeCode = "E-2", FullName = "B", Department = "Engineering", Nationality = "Indian", Gender = "Female", Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-1) },
            new Employee { TenantId = tenantId, EmployeeCode = "E-3", FullName = "C", Department = "Finance", Nationality = "Saudi", Gender = "Male", Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-1) });
        await db.SaveChangesAsync();
        return tenantId;
    }

    private static async Task<(Guid TenantId, Guid OwnerId)> SeedScheduleAsync(ZayraDbContext db, string exportFormat)
    {
        var tenantId = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Schedule Tenant", Slug = $"sched-{Guid.NewGuid():N}" });
        db.Permissions.Add(new Permission { Id = SchedulePermissionId, Key = "reports.schedule", Module = "Reports" });
        db.Employees.Add(new Employee
        {
            TenantId = tenantId, EmployeeCode = "E-1", FullName = "Engineer", Department = "Engineering",
            Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-1),
        });
        await db.SaveChangesAsync();

        var ownerId = await AddScheduleHolderAsync(db, tenantId, "owner@example.com");
        db.ReportSchedules.Add(new ReportSchedule
        {
            TenantId = tenantId, CreatedBy = ownerId, ReportKey = "hr.headcount", ReportName = "Headcount",
            Category = "HR", FiltersJson = "{}", Frequency = "Daily", DeliveryMethod = "Email",
            Recipients = "recipient@example.com", ExportFormat = exportFormat, IsActive = true,
            NextRunAtUtc = DateTime.UtcNow.AddMinutes(-1),
        });
        await db.SaveChangesAsync();
        return (tenantId, ownerId);
    }

    private static readonly Guid SchedulePermissionId = Guid.NewGuid();

    private static async Task<Guid> AddScheduleHolderAsync(ZayraDbContext db, Guid tenantId, string email)
    {
        var userId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        db.Users.Add(new User
        {
            Id = userId, TenantId = tenantId, Email = email, NormalizedEmail = email.ToUpperInvariant(),
            FullName = email, PasswordHash = "hash", IsActive = true, IsGroupScope = true,
        });
        db.Roles.Add(new Role { Id = roleId, TenantId = tenantId, Name = $"Analyst {roleId:N}", NormalizedName = $"ANALYST {roleId:N}" });
        db.UserRoles.Add(new UserRole { UserId = userId, RoleId = roleId });
        db.RolePermissions.Add(new RolePermission { RoleId = roleId, PermissionId = SchedulePermissionId });
        await db.SaveChangesAsync();
        return userId;
    }

    private static ServiceProvider BuildWorkerServices(ZayraDbContext db, IEmailService email)
    {
        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddSingleton(email);
        services.AddSingleton<IDataScopeService>(new DataScopeService(db));
        services.AddSingleton<Zayra.Api.Infrastructure.Notifications.INotificationService>(TestNotifications.For(db));
        return services.BuildServiceProvider();
    }

    private sealed class RecordingEmail : IEmailService
    {
        public List<(string To, EmailAttachment Attachment)> Messages { get; } = [];
        public Task SendAsync(string toAddress, string toName, string subject, string htmlBody,
            IReadOnlyList<EmailAttachment>? attachments = null, CancellationToken cancellationToken = default)
        {
            Messages.Add((toAddress, Assert.Single(attachments!)));
            return Task.CompletedTask;
        }
        public Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    }
}
