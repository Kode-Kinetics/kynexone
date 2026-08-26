using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

public sealed class MigrationImportControllerTests
{
    [Fact]
    public async Task Commit_ImportsIdentityAndOperationalSections_Idempotently()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var employee = new Employee { TenantId = tenantId, EmployeeCode = "EMP-001", FullName = "Imported Employee", Status = EmployeeStatuses.Active };
        var leaveType = new LeaveType { TenantId = tenantId, Code = "ANNUAL", NameEn = "Annual Leave" };
        db.Employees.Add(employee);
        db.LeaveTypes.Add(leaveType);
        await db.SaveChangesAsync();
        var controller = CreateController(db, tenantId);
        var request = Package(false);

        var result = await controller.Commit(request, CancellationToken.None);
        var dto = Assert.IsType<MigrationReconciliationDto>(Assert.IsType<OkObjectResult>(result.Result).Value);

        Assert.Equal("Completed", dto.Status);
        Assert.Equal(1, await db.Roles.CountAsync(x => x.TenantId == tenantId && x.Name == "Imported HR"));
        Assert.Equal(1, await db.Users.CountAsync(x => x.TenantId == tenantId && x.NormalizedEmail == "HR@EXAMPLE.COM"));
        Assert.Equal(1, await db.EmployeeLeaveBalances.CountAsync(x => x.TenantId == tenantId));
        Assert.Equal(1, await db.AttendanceDailyRecords.CountAsync(x => x.TenantId == tenantId));

        var second = await controller.Commit(request, CancellationToken.None);
        var secondDto = Assert.IsType<MigrationReconciliationDto>(Assert.IsType<OkObjectResult>(second.Result).Value);
        Assert.Equal(dto.BatchId, secondDto.BatchId);
        Assert.Equal(1, await db.Users.CountAsync(x => x.TenantId == tenantId));
        Assert.Equal(1, await db.EmployeeLeaveBalances.CountAsync(x => x.TenantId == tenantId));
        Assert.Equal(1, await db.AttendanceDailyRecords.CountAsync(x => x.TenantId == tenantId));
    }

    [Fact]
    public async Task Preview_DryRunPersistsLedgerButDoesNotMutateOperationalData()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        db.Employees.Add(new Employee { TenantId = tenantId, EmployeeCode = "EMP-001", FullName = "Imported Employee" });
        await db.SaveChangesAsync();
        var controller = CreateController(db, tenantId);

        var result = await controller.Preview(Package(true), CancellationToken.None);
        var dto = Assert.IsType<MigrationReconciliationDto>(Assert.IsType<OkObjectResult>(result.Result).Value);

        Assert.Equal("Previewed", dto.Status);
        Assert.Equal(1, await db.MigrationImportBatches.CountAsync(x => x.Id == dto.BatchId));
        Assert.Empty(await db.Roles.Where(x => x.TenantId == tenantId).ToListAsync());
        Assert.Empty(await db.Users.Where(x => x.TenantId == tenantId).ToListAsync());
    }

    [Fact]
    public async Task Preview_ClassifiesExistingUpsertsAsUpdates()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var employee = new Employee { TenantId = tenantId, EmployeeCode = "EMP-001", FullName = "Imported Employee" };
        var leaveType = new LeaveType { TenantId = tenantId, Code = "ANNUAL", NameEn = "Annual Leave" };
        db.Employees.Add(employee);
        db.LeaveTypes.Add(leaveType);
        db.Roles.Add(new Role { TenantId = tenantId, Name = "Imported HR", NormalizedName = "IMPORTED HR" });
        db.Users.Add(new User
        {
            TenantId = tenantId,
            Email = "hr@example.com",
            NormalizedEmail = "HR@EXAMPLE.COM",
            FullName = "Existing HR User",
            PasswordHash = "hash"
        });
        db.EmployeeLeaveBalances.Add(new EmployeeLeaveBalance
        {
            TenantId = tenantId,
            EmployeeId = employee.Id,
            EmployeeName = employee.FullName,
            LeaveTypeId = leaveType.Id,
            LeaveTypeName = leaveType.NameEn,
            Year = 2026
        });
        db.AttendanceDailyRecords.Add(new AttendanceDailyRecord
        {
            TenantId = tenantId,
            EmployeeId = employee.Id,
            EmployeeName = employee.FullName,
            WorkDate = new DateOnly(2026, 1, 2)
        });
        await db.SaveChangesAsync();

        var result = await CreateController(db, tenantId).Preview(Package(true), CancellationToken.None);
        var dto = Assert.IsType<MigrationReconciliationDto>(Assert.IsType<OkObjectResult>(result.Result).Value);

        Assert.Equal(0, dto.CreatedRows);
        Assert.Equal(4, dto.UpdatedRows);
        Assert.Equal(0, dto.SkippedRows);
    }

    [Fact]
    public async Task Commit_ImportsEnterpriseCompletionSections_WithGovernedLedger()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        db.Employees.Add(new Employee
        {
            TenantId = tenantId,
            EmployeeCode = "EMP-001",
            FullName = "Enterprise Employee",
            Status = EmployeeStatuses.Active
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db, tenantId);

        var result = await controller.Commit(EnterprisePackage(), CancellationToken.None);
        var dto = Assert.IsType<MigrationReconciliationDto>(Assert.IsType<OkObjectResult>(result.Result).Value);

        Assert.Equal("Completed", dto.Status);
        Assert.Equal(1, await db.EmployeeHistories.CountAsync(x => x.TenantId == tenantId));
        Assert.Equal(1, await db.EmployeeDocuments.CountAsync(x => x.TenantId == tenantId));
        Assert.Equal(1, await db.EmployeeDocumentVersions.CountAsync(x => x.TenantId == tenantId));
        Assert.Equal(1, await db.EmployeeContracts.CountAsync(x => x.TenantId == tenantId && x.ContractNumber == "LEG-CON-001"));
        var openingBalance = await db.PayrollOpeningBalances.SingleAsync(x => x.TenantId == tenantId);
        Assert.Equal(120000m, openingBalance.Amount);
        Assert.Equal("BASIC", openingBalance.ComponentCode);
        Assert.Equal(1, dto.SectionCounts["payrollOpeningBalances"]);
        Assert.Equal(1, dto.SectionCounts["benefitsEnrollments"]);
        Assert.Equal(1, dto.SectionCounts["reconciliationSignoffs"]);

        var stored = await db.MigrationImportBatches.SingleAsync(x => x.Id == dto.BatchId);
        var ledger = JsonSerializer.Deserialize<MigrationGovernedLedgerDto>(stored.ResultJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(ledger);
        Assert.Single(ledger!.PayrollOpeningBalances);
        Assert.Equal(120000m, ledger.PayrollOpeningBalances[0].Amount);
        Assert.Single(ledger.BenefitsEnrollmentHistory);
        Assert.Equal("MED-GOLD", ledger.BenefitsEnrollmentHistory[0].PlanCode);
        Assert.Single(ledger.ReconciliationSignoffs);
        Assert.Equal("Signed", ledger.ReconciliationSignoffs[0].Status);
    }

    private static MigrationPackageRequest Package(bool dryRun) => new(
        "migration-test-001",
        new Dictionary<string, string>
        {
            ["roles"] = "Name,Description,AuthorityLevel,IsActive\nImported HR,Imported role,50,true\n",
            ["users"] = "Email,FullName,PhoneNumber,PreferredLanguage,Timezone,Status,RoleNames,IsGroupScope\nhr@example.com,Imported HR User,,en,UTC,Invited,Imported HR,false\n",
            ["leaveBalances"] = "EmployeeCode,LeaveTypeCode,Year,Entitled,Accrued,Used,Pending,CarriedForward,Encashed,Expired,ManualAdjustment,NegativeAllowed\nEMP-001,ANNUAL,2026,30,30,0,0,0,0,0,0,false\n",
            ["attendanceDaily"] = "EmployeeCode,WorkDate,FirstInUtc,LastOutUtc,TotalWorkedMinutes,BreakMinutes,LateMinutes,EarlyExitMinutes,OvertimeMinutes,UndertimeMinutes,MissingPunch,Status,WorkMode\nEMP-001,2026-01-02,2026-01-02T08:00:00Z,2026-01-02T17:00:00Z,480,60,0,0,0,0,false,Present,Work from site\n"
        }, dryRun);

    private static MigrationPackageRequest EnterprisePackage() => new(
        "enterprise-migration-001",
        new Dictionary<string, string>
        {
            ["employeeHistory"] = "EmployeeCode,EventType,FieldName,OldValue,NewValue,EffectiveDate,Reason,SourceSystem,SourceRecordId\nEMP-001,JobChange,Designation,Associate,Manager,2026-01-01,Legacy migration,Workday,HIST-001\n",
            ["payrollOpeningBalances"] = "EmployeeCode,Year,BalanceType,ComponentCode,Amount,Currency,SourceSystem,SourceRecordId\nEMP-001,2026,YTD_GROSS,BASIC,120000,SAR,SAP,PAY-OB-001\n",
            ["benefitsEnrollments"] = "EmployeeCode,PlanCode,PlanName,CoverageTier,EffectiveDate,EndDate,EmployeeContribution,EmployerContribution,Currency,Status,SourceSystem,SourceRecordId\nEMP-001,MED-GOLD,Medical Gold,Family,2026-01-01,,500,1500,SAR,Active,Oracle,BEN-001\n",
            ["documentManifests"] = "EmployeeCode,DocumentType,DocumentCategory,FileName,ContentType,StorageUrl,Checksum,VersionNumber,IssueDate,ExpiryDate,ApprovalStatus,RetentionClass,SourceSystem,SourceDocumentId\nEMP-001,Passport,Identity,passport.pdf,application/pdf,s3://legacy/passport.pdf,sha256:abc,1,2024-01-01,2034-01-01,Verified,EmployeeRecord,Workday,DOC-001\n",
            ["contracts"] = "EmployeeCode,ContractNumber,ContractType,Status,StartDate,EndDate,BasicSalary,CurrencyCode,FileUrl,Version,SourceSystem,SourceRecordId\nEMP-001,LEG-CON-001,Employment,Active,2026-01-01,,25000,SAR,s3://legacy/contracts/LEG-CON-001.pdf,1,Oracle,CON-001\n",
            ["reconciliationSignoffs"] = "ReconciliationType,SourceSystem,PreparedBy,ApprovedBy,SignedAtUtc,VarianceCount,VarianceAmount,Status,EvidenceUri\nPayrollOpeningBalances,SAP,payroll.lead@example.com,cfo@example.com,2026-01-05T10:00:00Z,0,0,Signed,s3://legacy/recon/payroll.pdf\n"
        }, false);

    private static MigrationImportController CreateController(ZayraDbContext db, Guid tenantId)
    {
        var controller = new MigrationImportController(db, new Pbkdf2PasswordHasher(), new AuditService(db));
        var claims = new[]
        {
            new Claim("tenant_id", tenantId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Role, "Admin")
        };
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) } };
        return controller;
    }

    private static ZayraDbContext CreateDb() => new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
}
