using System.Security.Cryptography;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Employees;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers;

[ApiController]
[Route("api/migrations")]
[Authorize(Roles = "Admin,HR Manager")]
public sealed partial class MigrationImportController : ControllerBase
{
    /// <summary>
    /// Section apply order, and it is load-bearing in two places.
    ///
    /// <para><c>companyCutover</c> is FIRST because every opening-balance section is validated against
    /// the cutover of the company that owns the employee, and a consultant declaring the cutover in the
    /// same package as the balances is the normal case, not the exception.</para>
    ///
    /// <para><c>loans</c> precedes <c>advances</c> only for a stable reconciliation ordering; they are
    /// independent. <c>reconciliationSignoffs</c> stays last because it records a sign-off on what the
    /// sections above it just did.</para>
    /// </summary>
    private static readonly string[] SupportedSections =
    {
        "companyCutover",
        "roles",
        "users",
        "leaveBalances",
        "attendanceDaily",
        "employeeHistory",
        "payrollOpeningBalances",
        "loans",
        "advances",
        "eosbOpeningProvision",
        "benefitsEnrollments",
        "documentManifests",
        "contracts",
        "reconciliationSignoffs"
    };

    /// <summary>Sections that carry a balance in from a previous system, and therefore (a) write an
    /// <see cref="OpeningBalanceOrigin"/> row and (b) are refused into a locked period.</summary>
    private static readonly string[] OpeningBalanceSections =
    {
        "leaveBalances", "payrollOpeningBalances", "loans", "advances", "eosbOpeningProvision"
    };
    private readonly ZayraDbContext _db;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IAuditService _audit;

    public MigrationImportController(ZayraDbContext db, IPasswordHasher passwordHasher, IAuditService audit)
    {
        _db = db;
        _passwordHasher = passwordHasher;
        _audit = audit;
    }

    [HttpGet("template")]
    public IActionResult Template() => Ok(new Dictionary<string, string>
    {
        ["companyCutover"] = "CompanyRegistrationNumber,CompanyLegalName,CutoverDate,SourceSystem,Status,Notes\n1010101010,,2026-09-01,SAP,Active,Wave 1 - KSA manufacturing\n",
        ["loans"] = "EmployeeCode,LoanNumber,LoanTypeCode,LoanTypeName,OriginalAmount,InstallmentAmount,TotalInstallments,InstallmentsPaid,OutstandingBalance,FirstUnpaidDueDate,DisbursementDate,Currency,SourceSystem,SourceRecordId\nEMP-001,LEG-LN-0001,PERSONAL,Personal Loan,24000,1000,24,7,17000,2026-09-25,2026-02-25,SAR,SAP,LN-001\n",
        ["advances"] = "EmployeeCode,AdvanceNumber,OriginalAmount,InstallmentAmount,TotalInstallments,InstallmentsPaid,OutstandingBalance,FirstUnpaidDueDate,Currency,SourceSystem,SourceRecordId\nEMP-001,LEG-ADV-0001,6000,1000,6,2,4000,2026-09-25,SAR,SAP,ADV-001\n",
        ["eosbOpeningProvision"] = "EmployeeCode,AsAtDate,AccruedMonths,AccruedAmount,PriorServiceStartDate,Currency,SourceSystem,SourceRecordId\nEMP-001,2026-08-31,42,52500,2023-03-01,SAR,SAP,EOSB-001\n",
        ["roles"] = "Name,Description,AuthorityLevel,IsActive\nHR Manager,Imported HR role,50,true\n",
        ["users"] = "Email,FullName,PhoneNumber,PreferredLanguage,Timezone,Status,RoleNames,IsGroupScope\nhr@example.com,Imported HR,,en,UTC,Invited,HR Manager,false\n",
        ["leaveBalances"] = "EmployeeCode,LeaveTypeCode,Year,Entitled,Accrued,Used,Pending,CarriedForward,Encashed,Expired,ManualAdjustment,NegativeAllowed\nEMP-001,ANNUAL,2026,30,30,0,0,0,0,0,0,false\n",
        ["attendanceDaily"] = "EmployeeCode,WorkDate,FirstInUtc,LastOutUtc,TotalWorkedMinutes,BreakMinutes,LateMinutes,EarlyExitMinutes,OvertimeMinutes,UndertimeMinutes,MissingPunch,Status,WorkMode\nEMP-001,2026-01-01,2026-01-01T08:00:00Z,2026-01-01T17:00:00Z,480,60,0,0,0,0,false,Present,Work from site\n",
        ["employeeHistory"] = "EmployeeCode,EventType,FieldName,OldValue,NewValue,EffectiveDate,Reason,SourceSystem,SourceRecordId\nEMP-001,JobChange,Designation,Associate,Manager,2026-01-01,Legacy migration,Workday,HIST-001\n",
        // BalanceType vocabulary is closed — see OpeningBalanceTypes. YTD_GROSS/YTD_DEDUCTIONS/YTD_NET are
        // the aggregates the payslip's YTD block consumes; YTD_STATUTORY_EE/_ER, YTD_TAX and
        // YTD_COVERED_WAGE are carried detail for statutory reconciliation and are deliberately NOT
        // summed into the payslip (importing them as YTD_DEDUCTIONS would double-count).
        ["payrollOpeningBalances"] = "EmployeeCode,Year,BalanceType,ComponentCode,Amount,Currency,SourceSystem,SourceRecordId\n"
            + "EMP-001,2026,YTD_GROSS,BASIC,120000,SAR,SAP,PAY-OB-001\n"
            + "EMP-001,2026,YTD_DEDUCTIONS,TOTAL,14625,SAR,SAP,PAY-OB-002\n"
            + "EMP-001,2026,YTD_NET,TOTAL,105375,SAR,SAP,PAY-OB-003\n"
            + "EMP-001,2026,YTD_STATUTORY_EE,GOSI-ANN-EE,10800,SAR,SAP,PAY-OB-004\n"
            + "EMP-001,2026,YTD_STATUTORY_ER,GOSI-ANN-ER,10800,SAR,SAP,PAY-OB-005\n"
            + "EMP-001,2026,YTD_COVERED_WAGE,TOTAL,120000,SAR,SAP,PAY-OB-006\n",
        ["benefitsEnrollments"] = "EmployeeCode,PlanCode,PlanName,CoverageTier,EffectiveDate,EndDate,EmployeeContribution,EmployerContribution,Currency,Status,SourceSystem,SourceRecordId\nEMP-001,MED-GOLD,Medical Gold,Family,2026-01-01,,500,1500,SAR,Active,Oracle,BEN-001\n",
        ["documentManifests"] = "EmployeeCode,DocumentType,DocumentCategory,FileName,ContentType,StorageUrl,Checksum,VersionNumber,IssueDate,ExpiryDate,ApprovalStatus,RetentionClass,SourceSystem,SourceDocumentId\nEMP-001,Passport,Identity,passport.pdf,application/pdf,s3://legacy/passport.pdf,sha256:abc,1,2024-01-01,2034-01-01,Verified,EmployeeRecord,Workday,DOC-001\n",
        ["contracts"] = "EmployeeCode,ContractNumber,ContractType,Status,StartDate,EndDate,BasicSalary,CurrencyCode,FileUrl,Version,SourceSystem,SourceRecordId\nEMP-001,LEG-CON-001,Employment,Active,2026-01-01,,25000,SAR,s3://legacy/contracts/LEG-CON-001.pdf,1,Oracle,CON-001\n",
        ["reconciliationSignoffs"] = "ReconciliationType,SourceSystem,PreparedBy,ApprovedBy,SignedAtUtc,VarianceCount,VarianceAmount,Status,EvidenceUri\nPayrollOpeningBalances,SAP,payroll.lead@example.com,cfo@example.com,2026-01-05T10:00:00Z,0,0,Signed,s3://legacy/recon/payroll.pdf\n"
    });

    [HttpPost("preview")]
    public async Task<ActionResult<MigrationReconciliationDto>> Preview(MigrationPackageRequest request, CancellationToken ct)
    {
        var tenantId = RequireTenant();
        var validation = ValidatePackage(request);
        if (validation.Errors.Count > 0) return UnprocessableEntity(validation.Errors);

        var plan = await BuildPlanAsync(tenantId, request, ct);
        var batch = new MigrationImportBatch
        {
            TenantId = tenantId,
            ExternalBatchId = request.ExternalBatchId,
            PackageType = "MigrationPackage",
            PackageChecksum = PackageChecksum(request),
            Status = "Previewed",
            DryRun = request.DryRun,
            ReceivedRows = plan.ReceivedRows,
            CreatedRows = plan.WouldCreate,
            UpdatedRows = plan.WouldUpdate,
            SkippedRows = plan.WouldSkip,
            ErrorRows = plan.Errors.Count,
            ReconciliationJson = JsonSerializer.Serialize(plan.SectionCounts),
            ErrorJson = JsonSerializer.Serialize(plan.Errors),
            ResultJson = JsonSerializer.Serialize(plan.ToLedger()),
            PayloadJson = JsonSerializer.Serialize(request),
            CreatedBy = UserId()
        };
        _db.MigrationImportBatches.Add(batch);
        await _db.SaveChangesAsync(ct);
        return Ok(ToDto(batch, plan.SectionCounts, plan.Errors, plan.LockedPeriodRefusals));
    }

    [HttpPost("commit")]
    public async Task<ActionResult<MigrationReconciliationDto>> Commit(MigrationPackageRequest request, CancellationToken ct)
    {
        var tenantId = RequireTenant();
        var validation = ValidatePackage(request);
        if (validation.Errors.Count > 0) return UnprocessableEntity(validation.Errors);

        // ── LOCKED-PERIOD REFUSAL ───────────────────────────────────────────────────────────────────
        // Runs BEFORE the lease and before a single row is written. Restating an employee's opening
        // position for a period this product has already locked and paid would move the ground under a
        // payslip that is already in the employee's hands and a journal that is already in the GL. The
        // refusal is loud and names the run, the period and the entity — a consultant must be able to
        // act on it without opening a database.
        var lockedRefusals = await FindLockedPeriodRefusalsAsync(tenantId, request, ct);
        if (lockedRefusals.Count > 0)
            return Conflict(new
            {
                code = "cutover_period_locked",
                message = "Opening balances cannot be imported into a period that already has a locked payroll run.",
                refusals = lockedRefusals
            });

        var checksum = PackageChecksum(request);
        await using var lease = await MigrationImportLease.AcquireAsync(
            _db, tenantId, request.ExternalBatchId ?? checksum, ct);
        var existing = await FindBatchAsync(tenantId, request.ExternalBatchId, checksum, ct);
        if (existing is not null && existing.Status == "Completed")
            return Ok(ToDto(existing, ReadCounts(existing.ReconciliationJson), ReadErrors(existing.ErrorJson)));
        if (existing is not null && existing.Status == "Processing" && !_db.Database.IsNpgsql())
            return Conflict(new { message = "This migration package is already being processed." });
        // On PostgreSQL the session advisory lock above is held for the whole import. Therefore a
        // Processing row observed after acquiring it cannot still have a live owner; it is a crash/
        // cancellation remnant. Reuse the governed ledger and idempotent upserts instead of leaving
        // the batch permanently unresumable.
        if (existing is not null && existing.Status == "Processing")
        {
            existing.Status = "Failed";
            existing.CompletedAtUtc = DateTime.UtcNow;
            existing.ErrorJson = JsonSerializer.Serialize(new[] { "Recovered an interrupted migration attempt." });
            existing.UpdatedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
        if (existing is not null && existing.PackageChecksum != checksum)
            return Conflict(new { message = "ExternalBatchId is already associated with a different package checksum." });

        var batch = existing ?? new MigrationImportBatch
        {
            TenantId = tenantId,
            ExternalBatchId = request.ExternalBatchId,
            PackageChecksum = checksum,
            CreatedBy = UserId()
        };
        if (existing is null) _db.MigrationImportBatches.Add(batch);
        batch.Status = "Processing";
        batch.PackageType = "MigrationPackage";
        batch.PayloadJson = JsonSerializer.Serialize(request);
        batch.DryRun = request.DryRun;
        batch.ReceivedRows = 0;
        batch.CreatedRows = 0;
        batch.UpdatedRows = 0;
        batch.SkippedRows = 0;
        batch.ErrorRows = 0;
        batch.ReconciliationJson = "{}";
        batch.ErrorJson = "[]";
        batch.ResultJson = "{}";
        batch.StartedAtUtc = DateTime.UtcNow;
        batch.CompletedAtUtc = null;
        batch.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        try
        {
            var totals = new PlanTotals();
            // Cutover map is rebuilt AFTER the companyCutover section runs (it may declare new ones), so
            // it is loaded lazily inside the loop rather than hoisted out of it.
            var cutover = await LoadCutoverContextAsync(tenantId, request, batch.Id, ct);
            foreach (var section in SupportedSections)
            {
                if (!request.Sections.TryGetValue(section, out var csv)) continue;
                batch.CurrentSection = section;
                await _db.SaveChangesAsync(ct);
                var result = await ApplySectionAsync(section, csv, tenantId, request.DryRun, cutover, ct);
                if (section == "companyCutover")
                    cutover = await LoadCutoverContextAsync(tenantId, request, batch.Id, ct);
                totals.Add(section, result);
                batch.ReceivedRows += result.Received;
                batch.CreatedRows += result.Created;
                batch.UpdatedRows += result.Updated;
                batch.SkippedRows += result.Skipped;
                batch.ErrorRows += result.Errors.Count;
                batch.ReconciliationJson = JsonSerializer.Serialize(totals.SectionCounts);
                batch.ErrorJson = JsonSerializer.Serialize(totals.Errors);
                batch.ResultJson = JsonSerializer.Serialize(totals.ToLedger());
                await _db.SaveChangesAsync(ct);
            }
            batch.Status = request.DryRun ? "DryRunCompleted" : "Completed";
            batch.CurrentSection = string.Empty;
            batch.CompletedAtUtc = DateTime.UtcNow;
            batch.UpdatedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            await _audit.WriteAsync("migration.import_completed", nameof(MigrationImportBatch), batch.Id.ToString(), Context(), JsonSerializer.Serialize(new { batch.PackageChecksum, batch.Status, batch.ReceivedRows, batch.ErrorRows }), ct);
            return Ok(ToDto(batch, ReadCounts(batch.ReconciliationJson), ReadErrors(batch.ErrorJson)));
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or DbUpdateException)
        {
            batch.Status = "Failed";
            batch.ErrorJson = JsonSerializer.Serialize(new[] { ex.Message });
            batch.ErrorRows++;
            batch.CompletedAtUtc = DateTime.UtcNow;
            batch.UpdatedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            return UnprocessableEntity(ToDto(batch, ReadCounts(batch.ReconciliationJson), ReadErrors(batch.ErrorJson)));
        }
    }

    [HttpPost("{batchId:guid}/resume")]
    public async Task<ActionResult<MigrationReconciliationDto>> Resume(Guid batchId, MigrationPackageRequest request, CancellationToken ct)
    {
        var batch = await _db.MigrationImportBatches.FirstOrDefaultAsync(x => x.TenantId == RequireTenant() && x.Id == batchId, ct);
        if (batch is null) return NotFound();
        if (!string.Equals(batch.PackageChecksum, PackageChecksum(request), StringComparison.OrdinalIgnoreCase))
            return Conflict(new { message = "Resume package checksum does not match the persisted migration batch." });
        if (batch.Status == "Completed" || batch.Status == "DryRunCompleted")
            return Ok(ToDto(batch, ReadCounts(batch.ReconciliationJson), ReadErrors(batch.ErrorJson)));
        return await Commit(request with { ExternalBatchId = batch.ExternalBatchId ?? batchId.ToString("N") }, ct);
    }

    [HttpGet("{batchId:guid}")]
    public async Task<ActionResult<MigrationReconciliationDto>> Get(Guid batchId, CancellationToken ct)
    {
        var batch = await _db.MigrationImportBatches.FirstOrDefaultAsync(x => x.TenantId == RequireTenant() && x.Id == batchId, ct);
        return batch is null ? NotFound() : Ok(ToDto(batch, ReadCounts(batch.ReconciliationJson), ReadErrors(batch.ErrorJson)));
    }

    private async Task<MigrationImportBatch?> FindBatchAsync(Guid tenantId, string? externalId, string checksum, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(externalId))
            return await _db.MigrationImportBatches.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.ExternalBatchId == externalId, ct);
        return await _db.MigrationImportBatches.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.PackageChecksum == checksum && x.Status != "Previewed", ct);
    }

    private sealed class MigrationImportLease : IAsyncDisposable
    {
        private readonly ZayraDbContext? _db;
        private readonly long _key;

        private MigrationImportLease(ZayraDbContext? db, long key) { _db = db; _key = key; }

        public static async Task<MigrationImportLease> AcquireAsync(
            ZayraDbContext db, Guid tenantId, string packageKey, CancellationToken ct)
        {
            if (!db.Database.IsNpgsql()) return new MigrationImportLease(null, 0);
            var material = Encoding.UTF8.GetBytes($"migration-import:{tenantId:D}:{packageKey}");
            var digest = SHA256.HashData(material);
            var key = System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(digest.AsSpan(0, 8));
            await db.Database.OpenConnectionAsync(ct);
            try
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_lock({key})", ct);
                return new MigrationImportLease(db, key);
            }
            catch
            {
                await db.Database.CloseConnectionAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_db is null) return;
            try { await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_unlock({_key})"); }
            finally { await _db.Database.CloseConnectionAsync(); }
        }
    }

    private async Task<PlanTotals> BuildPlanAsync(Guid tenantId, MigrationPackageRequest request, CancellationToken ct)
    {
        var totals = new PlanTotals();
        var cutover = await LoadCutoverContextAsync(tenantId, request, Guid.Empty, ct);
        totals.LockedPeriodRefusals.AddRange(await FindLockedPeriodRefusalsAsync(tenantId, request, ct));
        foreach (var section in SupportedSections)
        {
            if (!request.Sections.TryGetValue(section, out var csv)) continue;
            var result = await ValidateSectionAsync(section, csv, tenantId, cutover, ct);
            totals.ReceivedRows += result.Received;
            totals.WouldCreate += result.WouldCreate;
            totals.WouldUpdate += result.WouldUpdate;
            totals.WouldSkip += result.WouldSkip;
            totals.Errors.AddRange(result.Errors);
            totals.SectionCounts[section] = result.Received;
            totals.SectionResults[section] = new MigrationSectionResultDto(section, result.Received, 0, 0, result.WouldSkip, result.AmountTotal);
        }
        return totals;
    }

    private async Task<SectionResult> ValidateSectionAsync(string section, string csv, Guid tenantId, CutoverContext cutover, CancellationToken ct)
    {
        var rows = Csv.Parse(csv);
        var result = new SectionResult { Received = rows.Count, AmountTotal = SectionControlTotal(section, rows) };
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (row, index) in rows.Select((r, i) => (r, i + 2)))
        {
            try
            {
                GuardDuplicate(section, row, seenKeys);
                var action = await ValidateRowAsync(section, row, tenantId, cutover, ct);
                if (action == "updated") result.WouldUpdate++;
                else result.WouldCreate++;
            }
            catch (Exception ex) { result.WouldSkip++; result.Errors.Add($"{section} row {index}: {ex.Message}"); }
        }
        return result;
    }

    private async Task<SectionResult> ApplySectionAsync(string section, string csv, Guid tenantId, bool dryRun, CutoverContext cutover, CancellationToken ct)
    {
        var parsedRows = Csv.Parse(csv);
        var result = new SectionResult { Received = parsedRows.Count, AmountTotal = SectionControlTotal(section, parsedRows) };
        if (dryRun) return (await ValidateSectionAsync(section, csv, tenantId, cutover, ct)).ToApplyResult();
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (row, index) in parsedRows.Select((r, i) => (r, i + 2)))
        {
            try
            {
                GuardDuplicate(section, row, seenKeys);
                var action = section switch
                {
                    "companyCutover" => await UpsertCompanyCutoverAsync(row, tenantId, ct),
                    "loans" => await UpsertLoanOpeningBalanceAsync(row, tenantId, cutover, result, ct),
                    "advances" => await UpsertAdvanceOpeningBalanceAsync(row, tenantId, cutover, result, ct),
                    "eosbOpeningProvision" => await UpsertEosbOpeningProvisionAsync(row, tenantId, cutover, result, ct),
                    "roles" => await UpsertRoleAsync(row, tenantId, ct),
                    "users" => await UpsertUserAsync(row, tenantId, ct),
                    "leaveBalances" => await UpsertLeaveBalanceAsync(row, tenantId, cutover, ct),
                    "attendanceDaily" => await UpsertAttendanceAsync(row, tenantId, ct),
                    "employeeHistory" => await UpsertEmployeeHistoryAsync(row, tenantId, ct),
                    "payrollOpeningBalances" => await UpsertPayrollOpeningBalanceAsync(row, tenantId, cutover, result, ct),
                    "benefitsEnrollments" => await UpsertBenefitsEnrollmentAsync(row, tenantId, result, ct),
                    "documentManifests" => await UpsertDocumentManifestAsync(row, tenantId, ct),
                    "contracts" => await UpsertContractAsync(row, tenantId, ct),
                    "reconciliationSignoffs" => AddReconciliationSignoff(row, result),
                    _ => throw new InvalidOperationException($"Unsupported section '{section}'.")
                };
                if (action == "created") result.Created++; else if (action == "updated") result.Updated++; else result.Skipped++;
            }
            catch (Exception ex) { result.Skipped++; result.Errors.Add($"{section} row {index}: {ex.Message}"); }
        }
        await _db.SaveChangesAsync(ct);
        return result;
    }

    private async Task<string> ValidateRowAsync(string section, Dictionary<string, string> row, Guid tenantId, CutoverContext cutover, CancellationToken ct)
    {
        switch (section)
        {
            case "companyCutover":
                var cutoverCompany = await ResolveCutoverCompanyAsync(row, tenantId, ct);
                _ = DateReq(row, "CutoverDate");
                RequireCutoverStatus(row);
                return await _db.CompanyCutovers.AnyAsync(x => x.TenantId == tenantId && x.CompanyId == cutoverCompany.Id, ct)
                    ? "updated" : "created";
            case "loans":
                var loanPlan = await PlanLoanAsync(row, tenantId, cutover, ct);
                return await _db.EmployeeLoans.AnyAsync(x => x.TenantId == tenantId && x.LoanNumber == loanPlan.LoanNumber && !x.IsDeleted, ct)
                    ? "updated" : "created";
            case "advances":
                var advancePlan = await PlanAdvanceAsync(row, tenantId, cutover, ct);
                return await _db.SalaryAdvances.AnyAsync(x => x.TenantId == tenantId && x.AdvanceNumber == advancePlan.AdvanceNumber && !x.IsDeleted, ct)
                    ? "updated" : "created";
            case "eosbOpeningProvision":
                var eosbEmployee = await Employee(row, tenantId, ct);
                RequireCutoverFor(eosbEmployee, cutover);
                var eosbAsAt = DateReq(row, "AsAtDate");
                _ = DecRequired(row, "AccruedAmount");
                _ = DecRequired(row, "AccruedMonths");
                return await _db.EmployeeEosbOpeningBalances.AnyAsync(x => x.TenantId == tenantId && x.EmployeeId == eosbEmployee.Id && x.AsAtDate == eosbAsAt, ct)
                    ? "updated" : "created";
            case "roles":
                var roleName = Require(row, "Name").Trim().ToUpperInvariant();
                return await _db.Roles.AnyAsync(x => x.TenantId == tenantId && x.NormalizedName == roleName && !x.IsDeleted, ct)
                    ? "updated" : "created";
            case "users":
                var email = Require(row, "Email");
                if (!email.Contains('@')) throw new FormatException("Email is invalid.");
                Require(row, "FullName");
                return await _db.Users.AnyAsync(x => x.TenantId == tenantId && x.NormalizedEmail == email.Trim().ToUpperInvariant() && !x.IsDeleted, ct)
                    ? "updated" : "created";
            case "leaveBalances":
                var employee = await Employee(row, tenantId, ct);
                var leave = await LeaveType(row, tenantId, ct);
                var leaveYear = Int(row, "Year");
                return await _db.EmployeeLeaveBalances.AnyAsync(x => x.TenantId == tenantId && x.EmployeeId == employee.Id && x.LeaveTypeId == leave.Id && x.Year == leaveYear, ct)
                    ? "updated" : "created";
            case "attendanceDaily":
                var attendanceEmployee = await Employee(row, tenantId, ct);
                var workDate = DateReq(row, "WorkDate");
                return await _db.AttendanceDailyRecords.AnyAsync(x => x.TenantId == tenantId && x.EmployeeId == attendanceEmployee.Id && x.WorkDate == workDate, ct)
                    ? "updated" : "created";
            case "employeeHistory":
                var historyEmployee = await Employee(row, tenantId, ct);
                var eventType = Require(row, "EventType").Trim();
                var fieldName = Require(row, "FieldName").Trim();
                var effectiveDate = DateReq(row, "EffectiveDate");
                var reason = Val(row, "Reason");
                return await _db.EmployeeHistories.AnyAsync(x => x.TenantId == tenantId && x.EmployeeId == historyEmployee.Id
                    && x.EventType == eventType && x.FieldName == fieldName && x.EffectiveDate == effectiveDate && x.Reason == reason, ct)
                    ? "updated" : "created";
            case "payrollOpeningBalances":
                var payrollEmployee = await Employee(row, tenantId, ct);
                ResolveCutoverFor(payrollEmployee, cutover, mandatory: false);
                var payrollYear = IntRequired(row, "Year");
                var balanceType = RequireBalanceType(row);
                var componentCode = Require(row, "ComponentCode").Trim();
                _ = DecRequired(row, "Amount");
                return await _db.PayrollOpeningBalances.AnyAsync(x => x.TenantId == tenantId && x.EmployeeId == payrollEmployee.Id
                    && x.Year == payrollYear && x.BalanceType == balanceType && x.ComponentCode == componentCode, ct)
                    ? "updated" : "created";
            case "benefitsEnrollments":
                var benefitEmployee = await Employee(row, tenantId, ct);
                var planCode = Require(row, "PlanCode").Trim();
                Require(row, "PlanName");
                Require(row, "CoverageTier");
                var benefitEffectiveDate = DateReq(row, "EffectiveDate");
                _ = DateOnlyNullable(row, "EndDate");
                _ = Dec(row, "EmployeeContribution");
                _ = Dec(row, "EmployerContribution");
                var plan = await _db.BenefitPlans.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Code == planCode && !x.IsDeleted
                    && (x.CompanyId == benefitEmployee.CompanyId || x.CompanyId == null), ct);
                return plan is not null && await _db.BenefitEnrollments.AnyAsync(x => x.TenantId == tenantId
                    && x.EmployeeId == benefitEmployee.Id && x.BenefitPlanId == plan.Id && x.EffectiveFrom == benefitEffectiveDate, ct)
                    ? "updated" : "created";
            case "documentManifests":
                var documentEmployee = await Employee(row, tenantId, ct);
                var documentType = Require(row, "DocumentType").Trim();
                var fileName = Require(row, "FileName").Trim();
                var storageUrl = Require(row, "StorageUrl").Trim();
                _ = Int(row, "VersionNumber", 1);
                return await _db.EmployeeDocuments.AnyAsync(x => x.TenantId == tenantId && x.EmployeeId == documentEmployee.Id
                    && x.DocumentType == documentType && x.FileName == fileName && x.StorageUrl == storageUrl && !x.IsDeleted, ct)
                    ? "updated" : "created";
            case "contracts":
                _ = await Employee(row, tenantId, ct);
                var contractNumber = Require(row, "ContractNumber").Trim();
                Require(row, "ContractType");
                _ = DateReq(row, "StartDate");
                _ = DateOnlyNullable(row, "EndDate");
                _ = Dec(row, "BasicSalary");
                return await _db.EmployeeContracts.AnyAsync(x => x.TenantId == tenantId && x.ContractNumber == contractNumber && !x.IsDeleted, ct)
                    ? "updated" : "created";
            case "reconciliationSignoffs":
                Require(row, "ReconciliationType");
                Require(row, "PreparedBy");
                Require(row, "ApprovedBy");
                _ = DateTimeReq(row, "SignedAtUtc");
                _ = Int(row, "VarianceCount");
                _ = Dec(row, "VarianceAmount");
                if (!Val(row, "Status", "Signed").Equals("Signed", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Status must be Signed for migration sign-off.");
                return "created";
            default:
                throw new InvalidOperationException($"Unsupported section '{section}'.");
        }
    }

    private async Task<string> UpsertRoleAsync(Dictionary<string, string> row, Guid tenantId, CancellationToken ct)
    {
        var name = Require(row, "Name").Trim();
        var normalized = name.ToUpperInvariant();
        var role = await _db.Roles.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.NormalizedName == normalized && !x.IsDeleted, ct);
        var created = role is null;
        role ??= new Role { TenantId = tenantId, Name = name, NormalizedName = normalized };
        role.Name = name; role.NormalizedName = normalized; role.Description = Val(row, "Description"); role.AuthorityLevel = Int(row, "AuthorityLevel", 99); role.IsActive = Bool(row, "IsActive", true); role.IsEditable = true;
        if (created) _db.Roles.Add(role);
        return created ? "created" : "updated";
    }

    private async Task<string> UpsertUserAsync(Dictionary<string, string> row, Guid tenantId, CancellationToken ct)
    {
        var email = Require(row, "Email").Trim(); var normalized = email.ToUpperInvariant();
        var user = await _db.Users.Include(x => x.UserRoles).FirstOrDefaultAsync(x => x.TenantId == tenantId && x.NormalizedEmail == normalized && !x.IsDeleted, ct);
        var created = user is null;
        user ??= new User { TenantId = tenantId, Email = email, NormalizedEmail = normalized, PasswordHash = _passwordHasher.Hash(Guid.NewGuid().ToString("N") + "!Aa1"), MustChangePassword = true, IsEmailConfirmed = false };
        user.Email = email; user.NormalizedEmail = normalized; user.FullName = Require(row, "FullName"); user.PhoneNumber = Val(row, "PhoneNumber"); user.PreferredLanguage = Val(row, "PreferredLanguage", "en"); user.Timezone = Val(row, "Timezone", "UTC"); user.Status = Val(row, "Status", "Invited"); user.IsActive = user.Status == "Active"; user.IsGroupScope = Bool(row, "IsGroupScope", false);
        var names = Val(row, "RoleNames").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (names.Length > 0)
        {
            var roles = await _db.Roles.Where(x => x.TenantId == tenantId && names.Contains(x.Name) && !x.IsDeleted).ToListAsync(ct);
            if (roles.Count != names.Length) throw new InvalidOperationException("One or more RoleNames do not exist in this tenant.");
            _db.UserRoles.RemoveRange(user.UserRoles);
            user.UserRoles = roles.Select(r => new UserRole { UserId = user.Id, RoleId = r.Id }).ToList();
            _db.UserRoles.AddRange(user.UserRoles);
        }
        if (created) _db.Users.Add(user);
        return created ? "created" : "updated";
    }

    private async Task<string> UpsertLeaveBalanceAsync(Dictionary<string, string> row, Guid tenantId, CutoverContext cutover, CancellationToken ct)
    {
        var employee = await Employee(row, tenantId, ct); var leave = await LeaveType(row, tenantId, ct); var year = IntRequired(row, "Year");
        var cutoverDate = ResolveCutoverFor(employee, cutover, mandatory: false);
        var item = await _db.EmployeeLeaveBalances.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.EmployeeId == employee.Id && x.LeaveTypeId == leave.Id && x.Year == year, ct);
        var created = item is null; item ??= new EmployeeLeaveBalance { TenantId = tenantId, EmployeeId = employee.Id, EmployeeName = employee.FullName, LeaveTypeId = leave.Id, LeaveTypeName = leave.NameEn, Year = year };
        // Entitled/Accrued/Used are the three a consultant always has and always gets wrong if blank is
        // read as nought; the rest legitimately default to zero on most source extracts.
        item.Entitled = DecRequired(row, "Entitled"); item.Accrued = DecRequired(row, "Accrued"); item.Used = DecRequired(row, "Used");
        item.Pending = Dec(row, "Pending"); item.CarriedForward = Dec(row, "CarriedForward"); item.Encashed = Dec(row, "Encashed"); item.Expired = Dec(row, "Expired"); item.ManualAdjustment = Dec(row, "ManualAdjustment"); item.NegativeAllowed = Bool(row, "NegativeAllowed", false); item.UpdatedAtUtc = DateTime.UtcNow;
        if (created) _db.EmployeeLeaveBalances.Add(item);
        // The carried figure is the NET balance as at cutover — what the employee could actually take on
        // day one. That is the number the consultant ties to the legacy system's leave report.
        // Math.Max(Entitled, Accrued), NOT Entitled + Accrued: the two columns are alternative
        // representations of one grant, and the shipped template makes BOTH mandatory, so adding them
        // doubled the opening balance of every migrated tenant that filled in both — which is all of
        // them. (Pending is deliberately not subtracted here: this is the cutover control total the
        // consultant ties to the legacy leave report, which has no notion of an in-flight request.)
        var netBalance = item.Granted + item.CarriedForward + item.ManualAdjustment
                       - item.Used - item.Encashed - item.Expired;
        await StampOriginAsync(OpeningBalanceEntityTypes.LeaveBalance, item.Id, employee, cutoverDate, netBalance,
            string.Empty, Val(row, "SourceSystem"), Val(row, "SourceRecordId"), cutover, ct);
        return created ? "created" : "updated";
    }

    private async Task<string> UpsertAttendanceAsync(Dictionary<string, string> row, Guid tenantId, CancellationToken ct)
    {
        var employee = await Employee(row, tenantId, ct); var date = DateReq(row, "WorkDate");
        var item = await _db.AttendanceDailyRecords.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.EmployeeId == employee.Id && x.WorkDate == date, ct);
        var created = item is null; item ??= new AttendanceDailyRecord { TenantId = tenantId, EmployeeId = employee.Id, EmployeeName = employee.FullName };
        item.WorkDate = date; item.FirstInUtc = DateTimeNullable(row, "FirstInUtc"); item.LastOutUtc = DateTimeNullable(row, "LastOutUtc"); item.TotalWorkedMinutes = Int(row, "TotalWorkedMinutes"); item.BreakMinutes = Int(row, "BreakMinutes"); item.LateMinutes = Int(row, "LateMinutes"); item.EarlyExitMinutes = Int(row, "EarlyExitMinutes"); item.OvertimeMinutes = Int(row, "OvertimeMinutes"); item.UndertimeMinutes = Int(row, "UndertimeMinutes"); item.MissingPunch = Bool(row, "MissingPunch", false); item.Status = Val(row, "Status", "Absent"); item.WorkMode = Val(row, "WorkMode", "Work from site"); item.UpdatedAtUtc = DateTime.UtcNow;
        if (created) _db.AttendanceDailyRecords.Add(item); return created ? "created" : "updated";
    }

    private async Task<string> UpsertEmployeeHistoryAsync(Dictionary<string, string> row, Guid tenantId, CancellationToken ct)
    {
        var employee = await Employee(row, tenantId, ct);
        var eventType = Require(row, "EventType").Trim();
        var fieldName = Require(row, "FieldName").Trim();
        var effectiveDate = DateReq(row, "EffectiveDate");
        var reason = Val(row, "Reason");
        var item = await _db.EmployeeHistories.FirstOrDefaultAsync(x =>
            x.TenantId == tenantId && x.EmployeeId == employee.Id && x.EventType == eventType
            && x.FieldName == fieldName && x.EffectiveDate == effectiveDate && x.Reason == reason, ct);
        var created = item is null;
        item ??= new EmployeeHistory { TenantId = tenantId, EmployeeId = employee.Id, EventType = eventType, FieldName = fieldName, EffectiveDate = effectiveDate };
        item.OldValue = Val(row, "OldValue");
        item.NewValue = Val(row, "NewValue");
        item.Reason = reason;
        item.CreatedByUserId = UserId();
        // EmployeeSafeSnapshot deliberately excludes salary, banking, and government identifiers.
        item.SnapshotJson = EmployeeSafeSnapshot.Serialize(employee);
        if (created) _db.EmployeeHistories.Add(item);
        return created ? "created" : "updated";
    }

    private async Task<string> UpsertPayrollOpeningBalanceAsync(Dictionary<string, string> row, Guid tenantId, CutoverContext cutover, SectionResult result, CancellationToken ct)
    {
        var employee = await Employee(row, tenantId, ct);
        var cutoverDate = ResolveCutoverFor(employee, cutover, mandatory: false);
        var year = IntRequired(row, "Year");
        var balanceType = RequireBalanceType(row);
        var componentCode = Require(row, "ComponentCode").Trim();
        var amount = DecRequired(row, "Amount");
        // Currency defaults from the employee's own legal entity, never from a literal. The old
        // `Val(row, "Currency", "AED")` handed a KSA customer AED whenever the column was omitted, while
        // the shipped template said SAR.
        var currency = await ResolveCurrencyAsync(row, employee, tenantId, ct);
        var sourceSystem = Val(row, "SourceSystem");
        var sourceRecordId = Val(row, "SourceRecordId");
        result.PayrollOpeningBalances.Add(new PayrollOpeningBalanceLedgerRow(employee.EmployeeCode, year, balanceType, componentCode, amount, currency, sourceSystem, sourceRecordId));

        var item = await _db.PayrollOpeningBalances.FirstOrDefaultAsync(x =>
            x.TenantId == tenantId
            && x.EmployeeId == employee.Id
            && x.Year == year
            && x.BalanceType == balanceType
            && x.ComponentCode == componentCode, ct);
        var created = item is null;
        item ??= new PayrollOpeningBalance
        {
            TenantId = tenantId,
            EmployeeId = employee.Id,
            CreatedBy = UserId()
        };

        item.CompanyId = employee.CompanyId;
        item.EmployeeCode = employee.EmployeeCode;
        item.Year = year;
        item.BalanceType = balanceType;
        item.ComponentCode = componentCode;
        item.Amount = amount;
        item.Currency = currency;
        item.SourceSystem = sourceSystem;
        item.SourceRecordId = sourceRecordId;
        if (!created)
        {
            item.UpdatedAtUtc = DateTime.UtcNow;
            item.UpdatedBy = UserId();
        }
        if (created) _db.PayrollOpeningBalances.Add(item);
        await StampOriginAsync(OpeningBalanceEntityTypes.PayrollYtd, item.Id, employee, cutoverDate, amount,
            currency, sourceSystem, sourceRecordId, cutover, ct);
        return created ? "created" : "updated";
    }

    private async Task<string> UpsertBenefitsEnrollmentAsync(Dictionary<string, string> row, Guid tenantId, SectionResult result, CancellationToken ct)
    {
        var employee = await Employee(row, tenantId, ct);
        var planCode = Require(row, "PlanCode").Trim();
        var effectiveDate = DateReq(row, "EffectiveDate");
        var endDate = DateOnlyNullable(row, "EndDate");
        var currency = Val(row, "Currency", "AED");
        var employeeContribution = Dec(row, "EmployeeContribution");
        var employerContribution = Dec(row, "EmployerContribution");
        result.BenefitsEnrollmentHistory.Add(new BenefitEnrollmentHistoryLedgerRow(Require(row, "EmployeeCode"), planCode, Require(row, "PlanName"), Require(row, "CoverageTier"), effectiveDate, endDate, employeeContribution, employerContribution, currency, Val(row, "Status", "Active"), Val(row, "SourceSystem"), Val(row, "SourceRecordId")));

        var plan = await _db.BenefitPlans.FirstOrDefaultAsync(x =>
            x.TenantId == tenantId && x.Code == planCode && !x.IsDeleted
            && (x.CompanyId == employee.CompanyId || x.CompanyId == null), ct);
        if (plan is null)
        {
            plan = new BenefitPlan
            {
                TenantId = tenantId,
                CompanyId = employee.CompanyId,
                Code = planCode,
                Name = Require(row, "PlanName").Trim(),
                PlanType = Val(row, "PlanType", "Legacy"),
                Currency = currency,
                EffectiveFrom = effectiveDate,
                EffectiveTo = endDate,
                RequiresEnrollment = true,
                IsActive = true,
                CreatedBy = UserId()
            };
            _db.BenefitPlans.Add(plan);
        }
        else
        {
            plan.Name = Require(row, "PlanName").Trim();
            plan.Currency = currency;
            if (effectiveDate < plan.EffectiveFrom) plan.EffectiveFrom = effectiveDate;
            plan.EffectiveTo = endDate ?? plan.EffectiveTo;
        }

        var enrollment = await _db.BenefitEnrollments.FirstOrDefaultAsync(x =>
            x.TenantId == tenantId && x.EmployeeId == employee.Id && x.BenefitPlanId == plan.Id
            && x.EffectiveFrom == effectiveDate, ct);
        var created = enrollment is null;
        enrollment ??= new BenefitEnrollment
        {
            TenantId = tenantId,
            CompanyId = employee.CompanyId,
            BenefitPlanId = plan.Id,
            EmployeeId = employee.Id,
            EmployeeName = employee.FullName,
            EffectiveFrom = effectiveDate,
            CreatedBy = UserId()
        };
        enrollment.CompanyId = employee.CompanyId;
        enrollment.EmployeeName = employee.FullName;
        enrollment.CoverageTier = Require(row, "CoverageTier").Trim();
        enrollment.EffectiveTo = endDate;
        enrollment.Status = Val(row, "Status", "Active");
        enrollment.UpdatedAtUtc = DateTime.UtcNow;
        enrollment.UpdatedBy = UserId();
        if (created) _db.BenefitEnrollments.Add(enrollment);

        var contribution = await _db.BenefitContributions.FirstOrDefaultAsync(x =>
            x.TenantId == tenantId && x.EmployeeId == employee.Id && x.BenefitPlanId == plan.Id
            && x.BenefitEnrollmentId == enrollment.Id && x.EffectiveFrom == effectiveDate, ct);
        contribution ??= new BenefitContribution
        {
            TenantId = tenantId,
            CompanyId = employee.CompanyId,
            BenefitEnrollmentId = enrollment.Id,
            BenefitPlanId = plan.Id,
            EmployeeId = employee.Id,
            EffectiveFrom = effectiveDate,
            CreatedBy = UserId()
        };
        contribution.CompanyId = employee.CompanyId;
        contribution.EmployeeAmount = employeeContribution;
        contribution.EmployerAmount = employerContribution;
        contribution.Frequency = Val(row, "Frequency", "Monthly");
        contribution.PayrollComponentCode = Val(row, "PayrollComponentCode");
        contribution.EffectiveTo = endDate;
        contribution.IsActive = string.Equals(enrollment.Status, "Active", StringComparison.OrdinalIgnoreCase);
        if (contribution.Id == Guid.Empty || _db.Entry(contribution).State == EntityState.Detached)
            _db.BenefitContributions.Add(contribution);

        return created ? "created" : "updated";
    }

    private async Task<string> UpsertDocumentManifestAsync(Dictionary<string, string> row, Guid tenantId, CancellationToken ct)
    {
        var employee = await Employee(row, tenantId, ct);
        var documentType = Require(row, "DocumentType").Trim();
        var fileName = Require(row, "FileName").Trim();
        var storageUrl = Require(row, "StorageUrl").Trim();
        var version = Int(row, "VersionNumber", 1);
        var item = await _db.EmployeeDocuments.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.EmployeeId == employee.Id && x.DocumentType == documentType && x.FileName == fileName && x.StorageUrl == storageUrl && !x.IsDeleted, ct);
        var created = item is null;
        item ??= new EmployeeDocument { TenantId = tenantId, EmployeeId = employee.Id, CompanyId = employee.CompanyId };
        item.DocumentType = documentType;
        item.DocumentCategory = Val(row, "DocumentCategory");
        item.FileName = fileName;
        item.ContentType = Val(row, "ContentType", "application/octet-stream");
        item.StorageUrl = storageUrl;
        item.VersionNumber = version;
        item.IssueDate = DateOnlyNullable(row, "IssueDate");
        item.ExpiryDate = DateOnlyNullable(row, "ExpiryDate");
        item.ApprovalStatus = Val(row, "ApprovalStatus", "Imported");
        item.Notes = JsonSerializer.Serialize(new { migration = true, checksum = Val(row, "Checksum"), retentionClass = Val(row, "RetentionClass"), sourceSystem = Val(row, "SourceSystem"), sourceDocumentId = Val(row, "SourceDocumentId") });
        if (created) _db.EmployeeDocuments.Add(item);
        if (!await _db.EmployeeDocumentVersions.AnyAsync(x => x.TenantId == tenantId && x.EmployeeDocumentId == item.Id && x.VersionNumber == version, ct))
            _db.EmployeeDocumentVersions.Add(new EmployeeDocumentVersion { TenantId = tenantId, EmployeeDocumentId = item.Id, VersionNumber = version, FileName = fileName, ContentType = item.ContentType, StorageUrl = storageUrl, CreatedBy = UserId() });
        return created ? "created" : "updated";
    }

    private async Task<string> UpsertContractAsync(Dictionary<string, string> row, Guid tenantId, CancellationToken ct)
    {
        var employee = await Employee(row, tenantId, ct);
        var contractNumber = Require(row, "ContractNumber").Trim();
        var item = await _db.EmployeeContracts.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.ContractNumber == contractNumber && !x.IsDeleted, ct);
        var created = item is null;
        item ??= new EmployeeContract { TenantId = tenantId, ContractNumber = contractNumber, CompanyId = employee.CompanyId };
        item.EmployeeId = employee.PublicId;
        item.EmployeeName = employee.FullName;
        item.ContractType = Val(row, "ContractType", "Employment");
        item.Status = Val(row, "Status", "Active");
        item.StartDate = DateReq(row, "StartDate");
        item.EndDate = DateOnlyNullable(row, "EndDate");
        item.BasicSalary = Dec(row, "BasicSalary");
        item.CurrencyCode = Val(row, "CurrencyCode", "AED");
        item.FileUrl = Val(row, "FileUrl");
        item.Version = Int(row, "Version", 1);
        item.CreatedByUserId = UserId();
        item.UpdatedAtUtc = DateTime.UtcNow;
        if (created) _db.EmployeeContracts.Add(item);
        return created ? "created" : "updated";
    }

    private static string AddReconciliationSignoff(Dictionary<string, string> row, SectionResult result)
    {
        result.ReconciliationSignoffs.Add(new MigrationSignoffLedgerRow(Require(row, "ReconciliationType"), Val(row, "SourceSystem"), Require(row, "PreparedBy"), Require(row, "ApprovedBy"), DateTimeReq(row, "SignedAtUtc"), Int(row, "VarianceCount"), Dec(row, "VarianceAmount"), Val(row, "Status", "Signed"), Val(row, "EvidenceUri")));
        return "created";
    }

    private async Task<Employee> Employee(Dictionary<string, string> row, Guid tenantId, CancellationToken ct) { var code = Require(row, "EmployeeCode"); var e = await _db.Employees.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.EmployeeCode == code && !x.IsDeleted, ct); return e ?? throw new InvalidOperationException($"Employee '{code}' was not found."); }
    private async Task<LeaveType> LeaveType(Dictionary<string, string> row, Guid tenantId, CancellationToken ct) { var code = Require(row, "LeaveTypeCode"); var l = await _db.LeaveTypes.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Code == code, ct); return l ?? throw new InvalidOperationException($"Leave type '{code}' was not found."); }
    private static string Require(Dictionary<string, string> row, string key) => string.IsNullOrWhiteSpace(Val(row, key)) ? throw new InvalidOperationException($"{key} is required.") : Val(row, key);
    private static string Val(Dictionary<string, string> row, string key, string fallback = "") => row.TryGetValue(key, out var value) ? value : fallback;

    // ── Parsing: culture-invariant, and loud about what it cannot read ───────────────────────────────
    //
    // Every parse below pins InvariantCulture. It used to inherit the host's culture, so on a container
    // whose locale uses a comma decimal separator "120000.50" either silently misparsed or threw at row
    // 2,847 of a cutover-weekend import. The locale is not pinned anywhere in the deploy manifest, so
    // this could not be ruled out by configuration.
    //
    // They also REPORT a malformed value instead of swallowing it. `decimal.Parse` throwing
    // FormatException("The input string was not in a correct format") gives a consultant nothing; the
    // column name and the offending text give them the cell to fix.
    private static readonly IFormatProvider Inv = System.Globalization.CultureInfo.InvariantCulture;

    private static int Int(Dictionary<string, string> row, string key, int fallback = 0)
    {
        var raw = Val(row, key);
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        return int.TryParse(raw.Trim(), System.Globalization.NumberStyles.Integer, Inv, out var v)
            ? v
            : throw new FormatException($"{key} is not a whole number (found '{raw.Trim()}').");
    }

    /// <summary>
    /// Blank means ZERO. Retained verbatim for the pre-existing sections so their behaviour does not
    /// move under them — but every opening-balance section uses <see cref="DecRequired"/> instead,
    /// because on a YTD or an outstanding balance "the consultant left the cell empty" and "the figure
    /// really is nought" are different facts and guessing between them is how a whole column quietly
    /// zeroes itself.
    /// </summary>
    private static decimal Dec(Dictionary<string, string> row, string key)
    {
        var raw = Val(row, key);
        if (string.IsNullOrWhiteSpace(raw)) return 0m;
        return decimal.TryParse(raw.Trim(), System.Globalization.NumberStyles.Number, Inv, out var v)
            ? v
            : throw new FormatException($"{key} is not a number (found '{raw.Trim()}').");
    }

    /// <summary>A money column that must be PRESENT. Blank is an error, not a zero.</summary>
    private static decimal DecRequired(Dictionary<string, string> row, string key)
    {
        var raw = Val(row, key);
        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidOperationException(
                $"{key} is required and was blank. Enter 0 explicitly if the amount really is nil — a blank cell is not read as zero on an opening balance.");
        return decimal.TryParse(raw.Trim(), System.Globalization.NumberStyles.Number, Inv, out var v)
            ? v
            : throw new FormatException($"{key} is not a number (found '{raw.Trim()}').");
    }

    /// <summary>A whole-number column that must be PRESENT. Blank is an error, not a zero.</summary>
    private static int IntRequired(Dictionary<string, string> row, string key)
    {
        var raw = Val(row, key);
        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidOperationException(
                $"{key} is required and was blank. Enter 0 explicitly if it really is nought.");
        return int.TryParse(raw.Trim(), System.Globalization.NumberStyles.Integer, Inv, out var v)
            ? v
            : throw new FormatException($"{key} is not a whole number (found '{raw.Trim()}').");
    }

    private static bool Bool(Dictionary<string, string> row, string key, bool fallback)
    {
        var raw = Val(row, key);
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        return bool.TryParse(raw.Trim(), out var v)
            ? v
            : throw new FormatException($"{key} is not true/false (found '{raw.Trim()}').");
    }

    private static DateOnly DateReq(Dictionary<string, string> row, string key)
    {
        var raw = Require(row, key).Trim();
        return DateOnly.TryParse(raw, Inv, System.Globalization.DateTimeStyles.None, out var v)
            ? v
            : throw new FormatException($"{key} is not a date in yyyy-MM-dd form (found '{raw}').");
    }

    private static DateTime? DateTimeNullable(Dictionary<string, string> row, string key)
    {
        var raw = Val(row, key);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return DateTime.TryParse(raw.Trim(), Inv, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var v)
            ? v
            : throw new FormatException($"{key} is not a timestamp (found '{raw.Trim()}').");
    }

    private static DateTime DateTimeReq(Dictionary<string, string> row, string key)
    {
        var raw = Require(row, key).Trim();
        return DateTime.TryParse(raw, Inv, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var v)
            ? v
            : throw new FormatException($"{key} is not a timestamp (found '{raw}').");
    }

    private static DateOnly? DateOnlyNullable(Dictionary<string, string> row, string key)
    {
        var raw = Val(row, key);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return DateOnly.TryParse(raw.Trim(), Inv, System.Globalization.DateTimeStyles.None, out var v)
            ? v
            : throw new FormatException($"{key} is not a date in yyyy-MM-dd form (found '{raw.Trim()}').");
    }
    private PackageValidation ValidatePackage(MigrationPackageRequest request) { var errors = request.Sections.Keys.Except(SupportedSections, StringComparer.OrdinalIgnoreCase).Select(x => $"Unsupported migration section '{x}'.").ToList(); if (request.Sections.Count == 0) errors.Add("At least one migration section is required."); return new(errors); }
    private static string PackageChecksum(MigrationPackageRequest request) { using var sha = SHA256.Create(); var canonical = string.Join("\n", request.Sections.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Select(x => x.Key.ToLowerInvariant() + "\n" + x.Value.Replace("\r\n", "\n").Replace('\r', '\n'))); return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant(); }
    /// <summary>
    /// The control total for a whole section: the money the FILE claims, over every row received,
    /// whether or not each row will survive validation.
    ///
    /// <para>That is deliberate and it is the only version a consultant can use. The number exists to
    /// be tied to the bottom line of a report printed out of the outgoing system; if it silently
    /// excluded the rows that are going to be rejected, it would never match that report and the
    /// consultant would be reconciling two figures that were never meant to agree. The rejected rows
    /// are named separately in <c>Errors</c>, so "the file says 26,000, 9,000 of it will not land, and
    /// here is which row and why" is readable off one response.</para>
    ///
    /// <para>It is also computed identically on preview and on commit, so the figure signed off before
    /// the import is the figure recorded in the batch ledger after it.</para>
    /// </summary>
    private static decimal SectionControlTotal(string section, List<Dictionary<string, string>> rows)
        => Math.Round(rows.Sum(r => RowControlTotal(section, r)), 2);

    /// <summary>
    /// The money one row carries. Only the sections whose figures a consultant reconciles against a
    /// source-system report contribute; a role or a user has no amount and contributes nothing rather
    /// than a misleading zero.
    /// </summary>
    private static decimal RowControlTotal(string section, Dictionary<string, string> row)
    {
        try
        {
            return section switch
            {
                "payrollOpeningBalances" => Dec(row, "Amount"),
                "loans" => Dec(row, "OutstandingBalance"),
                "advances" => Dec(row, "OutstandingBalance"),
                "eosbOpeningProvision" => Dec(row, "AccruedAmount"),
                // Math.Max, not +, for the same reason as UpsertLeaveBalanceAsync: Entitled and Accrued
                // are one grant in two columns. This control total must agree with the row it totals.
                "leaveBalances" => Math.Max(Dec(row, "Entitled"), Dec(row, "Accrued")) + Dec(row, "CarriedForward")
                                 + Dec(row, "ManualAdjustment") - Dec(row, "Used") - Dec(row, "Encashed") - Dec(row, "Expired"),
                "benefitsEnrollments" => Dec(row, "EmployeeContribution") + Dec(row, "EmployerContribution"),
                "contracts" => Dec(row, "BasicSalary"),
                _ => 0m
            };
        }
        catch { return 0m; } // a malformed amount is already reported as a row error
    }

    private MigrationReconciliationDto ToDto(
        MigrationImportBatch b,
        IReadOnlyDictionary<string, int> counts,
        IReadOnlyList<string> errors,
        IReadOnlyList<object>? lockedPeriodRefusals = null) => new(
            b.Id, b.Status, b.PackageChecksum, b.ReceivedRows, b.CreatedRows, b.UpdatedRows,
            b.SkippedRows, b.ErrorRows, b.CurrentSection, counts, errors)
        {
            SectionTotals = ReadSectionTotals(b.ResultJson),
            LockedPeriodRefusals = lockedPeriodRefusals ?? Array.Empty<object>()
        };

    private static Dictionary<string, decimal> ReadSectionTotals(string resultJson)
    {
        try
        {
            var ledger = JsonSerializer.Deserialize<MigrationGovernedLedgerDto>(resultJson);
            return ledger?.Sections.ToDictionary(x => x.Key, x => x.Value.AmountTotal) ?? new();
        }
        catch (JsonException) { return new(); }
    }
    private static Dictionary<string, int> ReadCounts(string json) => JsonSerializer.Deserialize<Dictionary<string, int>>(json) ?? new();
    private static List<string> ReadErrors(string json) => JsonSerializer.Deserialize<List<string>>(json) ?? new();
    private Guid RequireTenant() => this.GetTenantId() ?? throw new UnauthorizedAccessException("Tenant context is required.");
    private Guid? UserId() => Guid.TryParse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;
    private RequestContext Context() => new(
        HttpContext.Connection.RemoteIpAddress?.ToString(),
        Request.Headers.UserAgent.ToString(),
        UserId(),
        RequireTenant(),
        User.FindAll(ClaimTypes.Role).Select(x => x.Value).ToArray(),
        Array.Empty<string>());

    private sealed record PackageValidation(IReadOnlyList<string> Errors);
    private sealed class SectionResult
    {
        public int Received;
        public int WouldCreate;
        public int WouldUpdate;
        public int WouldSkip;
        public int Created;
        public int Updated;
        public int Skipped;
        public decimal AmountTotal;
        public List<string> Errors { get; } = new();
        public List<PayrollOpeningBalanceLedgerRow> PayrollOpeningBalances { get; } = new();
        public List<BenefitEnrollmentHistoryLedgerRow> BenefitsEnrollmentHistory { get; } = new();
        public List<MigrationSignoffLedgerRow> ReconciliationSignoffs { get; } = new();
        public SectionResult ToApplyResult() => this;
    }

    private sealed class PlanTotals
    {
        public int ReceivedRows;
        public int WouldCreate;
        public int WouldUpdate;
        public int WouldSkip;
        public List<string> Errors { get; } = new();
        public List<object> LockedPeriodRefusals { get; } = new();
        public Dictionary<string, int> SectionCounts { get; } = new();
        public Dictionary<string, MigrationSectionResultDto> SectionResults { get; } = new();
        public List<PayrollOpeningBalanceLedgerRow> PayrollOpeningBalances { get; } = new();
        public List<BenefitEnrollmentHistoryLedgerRow> BenefitsEnrollmentHistory { get; } = new();
        public List<MigrationSignoffLedgerRow> ReconciliationSignoffs { get; } = new();

        public void Add(string section, SectionResult r)
        {
            ReceivedRows += r.Received;
            Errors.AddRange(r.Errors);
            SectionCounts[section] = r.Received;
            SectionResults[section] = new MigrationSectionResultDto(section, r.Received, r.Created, r.Updated, r.Skipped, r.AmountTotal);
            PayrollOpeningBalances.AddRange(r.PayrollOpeningBalances);
            BenefitsEnrollmentHistory.AddRange(r.BenefitsEnrollmentHistory);
            ReconciliationSignoffs.AddRange(r.ReconciliationSignoffs);
        }

        public MigrationGovernedLedgerDto ToLedger() => new(SectionResults, PayrollOpeningBalances, BenefitsEnrollmentHistory, ReconciliationSignoffs);
    }
}
