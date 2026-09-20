using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Application.Assets;
using Zayra.Api.Application.Auth;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Assets;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Email;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// W2-C — asset and equipment custody, proven on PostgreSQL with the retrying execution strategy
/// production uses.
/// <list type="number">
///   <item>One asset cannot hold two active assignments — enforced by the database (partial unique
///     index), and under concurrent requests exactly one issue wins.</item>
///   <item>Offboarding archive is blocked while the leaver holds an asset, and unblocked by a recorded
///     return or an APPROVED write-off; the legacy AssetsReturned checkbox cannot override the register.</item>
///   <item>A write-off needs approval; step 1 of a two-step workflow does not write the asset off.</item>
///   <item>Overdue reminders are enqueued once, not every sweep.</item>
///   <item>Tenant and company isolation; an employee sees only their own assets.</item>
/// </list>
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class AssetCustodyPostgresTests
{
    private readonly PostgresFixture _fx;
    public AssetCustodyPostgresTests(PostgresFixture fx) => _fx = fx;

    // ── Harness ──────────────────────────────────────────────────────────────────

    private sealed record World(Guid TenantId, Guid CompanyId, Guid HrUserId, int EmployeeId, int Employee2Id);

    private async Task<World> SeedWorldAsync(bool withDefaultWriteOffWorkflow = true, Guid? tenantId = null)
    {
        await using var db = _fx.CreateDb();
        var tid = tenantId ?? await PostgresFixture.SeedMinimalTenant(db);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var company = new Company
        {
            TenantId = tid, LegalNameEn = $"Assets Co {suffix}", CountryCode = "SAU", Jurisdiction = "mainland",
            DefaultCurrency = "SAR", RegistrationNumber = $"REG-{suffix}", IsActive = true,
        };
        var hrUserId = Guid.NewGuid();
        db.Companies.Add(company);
        db.Users.Add(new User
        {
            Id = hrUserId, TenantId = tid, Email = $"hr-{suffix}@assets.test", NormalizedEmail = $"HR-{suffix}@ASSETS.TEST",
            FullName = "Asset Custodian", PasswordHash = "x",
        });
        var e1 = new Employee
        {
            TenantId = tid, CompanyId = company.Id, EmployeeCode = $"AS1-{suffix}", FullName = "Holder One",
            Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-3),
        };
        var e2 = new Employee
        {
            TenantId = tid, CompanyId = company.Id, EmployeeCode = $"AS2-{suffix}", FullName = "Holder Two",
            Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-1),
        };
        db.Employees.AddRange(e1, e2);
        if (withDefaultWriteOffWorkflow)
        {
            var wf = new ApprovalWorkflow
            {
                TenantId = tid, Code = $"AWO-{suffix}", Name = "Asset write-off", EntityName = AssetCustodyService.ApprovalEntityName,
                IsDefault = true, IsActive = true,
            };
            wf.Steps.Add(new ApprovalWorkflowStep
            {
                TenantId = tid, WorkflowId = wf.Id, StepOrder = 1, StepName = "HR Approval",
                ApproverType = "Role", ApproverRole = "HR Manager", IsFinalStep = true,
            });
            db.ApprovalWorkflows.Add(wf);
        }
        await db.SaveChangesAsync();
        return new World(tid, company.Id, hrUserId, e1.Id, e2.Id);
    }

    private static AssetCustodyService Custody(ZayraDbContext db)
    {
        var audit = new AuditService(db);
        return new AssetCustodyService(db, audit, new ApprovalWorkflowService(db, audit));
    }

    private static RequestContext Ctx(World w, Guid? userId = null, string[]? roles = null)
        => new("127.0.0.1", "w2c-test", userId ?? w.HrUserId, w.TenantId, roles);

    private async Task<Guid> CreateAssetAsync(World w, string? tag = null)
    {
        await using var db = _fx.CreateDb();
        var asset = await Custody(db).CreateAsync(w.TenantId, new AssetUpsertRequest(
            tag ?? $"LAP-{Guid.NewGuid().ToString("N")[..10]}", "Dell Latitude 7440", $"SN{Guid.NewGuid():N}", "LAPTOP",
            "Dell", "Latitude 7440", new DateOnly(2025, 1, 15), 5200m, "SAR", "NEW",
            w.CompanyId, null, null, "HQ IT store", null), Ctx(w), CancellationToken.None);
        return asset.Id;
    }

    private async Task<Guid> IssueAsync(World w, Guid assetId, int employeeId, DateOnly? expectedReturn = null)
    {
        await using var db = _fx.CreateDb();
        var a = await Custody(db).IssueAsync(w.TenantId, assetId,
            new IssueAssetRequest(employeeId, null, expectedReturn, "GOOD", "Onboarding kit"), Ctx(w), CancellationToken.None);
        return a.Id;
    }

    private static void SetPrincipal(ControllerBase controller, Guid tenantId, Guid userId, params Claim[] extra)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()),
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Role, "Admin"),
            new("permission", "employees.write"),
            new("permission", "employees.read"),
        };
        claims.AddRange(extra);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) },
        };
    }

    // ── 1. One active holder: the database enforces it ───────────────────────────

    [Fact]
    public async Task OneActiveHolder_IsAPartialUniqueIndex_AndPostgresRejectsASecondActiveRowWrittenBehindTheCodesBack()
    {
        // The predicate must really be there: a partial index silently emitted WITHOUT its WHERE would make
        // the table accept only one custody row per asset EVER, and the behavioural test below would pass
        // for the wrong reason.
        var ddl = await _fx.GetIndexDefinitionAsync("ux_asset_assignments_one_active_holder");
        ddl.Should().NotBeNull();
        ddl!.Should().Contain("UNIQUE").And.Contain("(asset_id)").And.Contain("WHERE").And.Contain("'Active'");

        var w = await SeedWorldAsync();
        var assetId = await CreateAssetAsync(w);
        await IssueAsync(w, assetId, w.EmployeeId);

        // Bypass the service entirely — raw EF insert of a second Active row for the same asset.
        await using var db = _fx.CreateDb();
        db.AssetAssignments.Add(new AssetAssignment
        {
            TenantId = w.TenantId, CompanyId = w.CompanyId, AssetId = assetId, EmployeeId = w.Employee2Id,
            EmployeeName = "Sneaky", Status = AssetAssignmentStatuses.Active, IssuedOn = AssetCustodyService.Today(),
        });
        var act = () => db.SaveChangesAsync();
        var ex = (await act.Should().ThrowAsync<DbUpdateException>()).Which;
        var pg = ex.InnerException.Should().BeOfType<PostgresException>().Which;
        pg.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
        pg.ConstraintName.Should().Be("ux_asset_assignments_one_active_holder");

        // Closed rows are unconstrained: full history per asset is allowed.
        await using var db2 = _fx.CreateDb();
        db2.AssetAssignments.Add(new AssetAssignment
        {
            TenantId = w.TenantId, CompanyId = w.CompanyId, AssetId = assetId, EmployeeId = w.Employee2Id,
            EmployeeName = "Historic", Status = AssetAssignmentStatuses.Returned, IssuedOn = new DateOnly(2024, 1, 1),
            ReturnedOn = new DateOnly(2024, 6, 1),
        });
        await db2.SaveChangesAsync();
    }

    [Fact]
    public async Task ConcurrentIssues_OfTheSameAsset_ExactlyOneWins_AndTheRestGetATyped409()
    {
        var w = await SeedWorldAsync();
        var assetId = await CreateAssetAsync(w);

        // Eight more employees, all eligible; eight simultaneous requests, each on its own connection.
        var employeeIds = new List<int>();
        await using (var db = _fx.CreateDb())
        {
            for (var i = 0; i < 8; i++)
            {
                var e = new Employee
                {
                    TenantId = w.TenantId, CompanyId = w.CompanyId, EmployeeCode = $"RACE-{i}-{Guid.NewGuid():N}",
                    FullName = $"Racer {i}", Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-1),
                };
                db.Employees.Add(e);
                await db.SaveChangesAsync();
                employeeIds.Add(e.Id);
            }
        }

        using var gate = new SemaphoreSlim(0, employeeIds.Count);
        var attempts = employeeIds.Select(async empId =>
        {
            await gate.WaitAsync();
            await using var db = _fx.CreateDb();
            try
            {
                await Custody(db).IssueAsync(w.TenantId, assetId, new IssueAssetRequest(empId, null, null, null, null),
                    Ctx(w), CancellationToken.None);
                return "ok";
            }
            catch (AssetOperationException ex) { return ex.Code; }
        }).ToList();
        gate.Release(employeeIds.Count);
        var outcomes = await Task.WhenAll(attempts);

        outcomes.Count(o => o == "ok").Should().Be(1, "only one of eight concurrent issues may succeed");
        outcomes.Where(o => o != "ok").Should().OnlyContain(o => o == "asset_already_assigned" || o == "asset_changed_concurrently");

        await using var check = _fx.CreateDb();
        (await check.AssetAssignments.CountAsync(a => a.AssetId == assetId && a.Status == AssetAssignmentStatuses.Active)).Should().Be(1);
        (await check.AssetAssignments.CountAsync(a => a.AssetId == assetId)).Should().Be(1, "losers leave no rows behind");
        (await check.Assets.SingleAsync(a => a.Id == assetId)).Status.Should().Be(AssetStatuses.Assigned);
        (await check.AuditLogs.CountAsync(l => l.TenantId == w.TenantId && l.Action == "asset.issued" && l.EntityId == assetId.ToString()))
            .Should().Be(1, "the audit row commits with the winning issue only");
    }

    [Fact]
    public async Task IssueReturnTransfer_KeepFullHistory_AndTheServiceRefusesASecondHolder()
    {
        var w = await SeedWorldAsync();
        var assetId = await CreateAssetAsync(w);
        await IssueAsync(w, assetId, w.EmployeeId);

        await using (var db = _fx.CreateDb())
        {
            var act = () => Custody(db).IssueAsync(w.TenantId, assetId, new IssueAssetRequest(w.Employee2Id, null, null, null, null), Ctx(w), CancellationToken.None);
            (await act.Should().ThrowAsync<AssetOperationException>()).Which.Code.Should().Be("asset_already_assigned");
        }

        await using (var db = _fx.CreateDb())
            await Custody(db).TransferAsync(w.TenantId, assetId, new TransferAssetRequest(w.Employee2Id, null, "GOOD", "Team move"), Ctx(w), CancellationToken.None);
        await using (var db = _fx.CreateDb())
            await Custody(db).ReturnAsync(w.TenantId, assetId, new ReturnAssetRequest("FAIR", null, "Scratched lid"), Ctx(w), CancellationToken.None);

        await using var check = _fx.CreateDb();
        var history = await check.AssetAssignments.Where(a => a.AssetId == assetId).OrderBy(a => a.IssuedAtUtc).ToListAsync();
        history.Select(h => (h.EmployeeId, h.Status)).Should().Equal(
            (w.EmployeeId, AssetAssignmentStatuses.Transferred),
            (w.Employee2Id, AssetAssignmentStatuses.Returned));
        history[0].TransferredToAssignmentId.Should().Be(history[1].Id);
        history[1].ConditionOnReturn.Should().Be("FAIR");
        var asset = await check.Assets.SingleAsync(a => a.Id == assetId);
        asset.Status.Should().Be(AssetStatuses.InStock);
        asset.Condition.Should().Be("FAIR");
        var actions = await check.AuditLogs.Where(l => l.EntityId == assetId.ToString()).OrderBy(l => l.CreatedAtUtc).Select(l => l.Action).ToListAsync();
        actions.Should().Equal("asset.created", "asset.issued", "asset.transferred", "asset.returned");
    }

    // ── 2. Offboarding clearance ─────────────────────────────────────────────────

    private async Task<Guid> InitiateOffboardingReadyToArchiveAsync(World w, int employeeId)
    {
        await using var db = _fx.CreateDb();
        var controller = new OffboardingController(db);
        SetPrincipal(controller, w.TenantId, w.HrUserId);
        var today = AssetCustodyService.Today();
        var result = await controller.Initiate(new InitiateOffboardingRequest(employeeId, "Resignation", "Relocating",
            today.AddDays(-40), 30, today.AddDays(-10), true, RaiseBackfill: false), CancellationToken.None);
        var off = Assert.IsType<EmployeeOffboarding>(Assert.IsType<OkObjectResult>(result).Value);

        // Everything else the archive needs, so the ONLY thing standing in the way is the asset.
        var tracked = await db.EmployeeOffboardings.SingleAsync(o => o.Id == off.Id);
        tracked.KnowledgeHandover = true;
        tracked.ExitInterviewStatus = "Waived";
        var emp = await db.Employees.SingleAsync(e => e.Id == employeeId);
        db.EmployeeFinalSettlements.Add(new EmployeeFinalSettlement
        {
            TenantId = w.TenantId, CompanyId = w.CompanyId, EmployeeId = employeeId,
            EmployeeCode = emp.EmployeeCode, EmployeeName = emp.FullName,
            OffboardingId = off.Id, LastWorkingDay = off.LastWorkingDay,
            ServiceStartDate = DateOnly.FromDateTime(emp.JoiningDate),
            SettlementDueDate = off.LastWorkingDay, Status = FinalSettlementStatuses.Paid,
        });
        await db.SaveChangesAsync();
        return off.Id;
    }

    private async Task<IActionResult> CompleteAsync(World w, Guid offboardingId)
    {
        await using var db = _fx.CreateDb();
        var controller = new OffboardingController(db);
        SetPrincipal(controller, w.TenantId, w.HrUserId);
        return await controller.Complete(offboardingId, CancellationToken.None);
    }

    private async Task<IActionResult> TickAssetsReturnedAsync(World w, Guid offboardingId)
    {
        await using var db = _fx.CreateDb();
        var controller = new OffboardingController(db);
        SetPrincipal(controller, w.TenantId, w.HrUserId);
        return await controller.Checklist(offboardingId, new OffboardingChecklistRequest(true, null, null, null), CancellationToken.None);
    }

    private static string ErrorCode(IActionResult result)
    {
        var conflict = result.Should().BeOfType<ConflictObjectResult>().Which;
        return JsonDocument.Parse(JsonSerializer.Serialize(conflict.Value)).RootElement.GetProperty("error").GetString()!;
    }

    [Fact]
    public async Task OffboardingArchive_IsBlockedWhileAnAssetIsOutstanding_AndUnblockedByTheReturn()
    {
        var w = await SeedWorldAsync();
        var assetId = await CreateAssetAsync(w);
        await IssueAsync(w, assetId, w.EmployeeId); // no expected return date yet
        var offId = await InitiateOffboardingReadyToArchiveAsync(w, w.EmployeeId);

        await using (var db = _fx.CreateDb())
        {
            // Initiation armed the reminder: the item is now due back on the last working day.
            var a = await db.AssetAssignments.SingleAsync(x => x.AssetId == assetId && x.Status == AssetAssignmentStatuses.Active);
            var off = await db.EmployeeOffboardings.SingleAsync(o => o.Id == offId);
            a.ExpectedReturnDate.Should().Be(off.LastWorkingDay);
        }

        // The legacy checkbox cannot be ticked over the register…
        ErrorCode(await TickAssetsReturnedAsync(w, offId)).Should().Be("assets_outstanding");
        // …and a hand-set flag (direct DB write, as legacy data might have) cannot archive either.
        await using (var db = _fx.CreateDb())
        {
            var off = await db.EmployeeOffboardings.SingleAsync(o => o.Id == offId);
            off.AssetsReturned = true;
            await db.SaveChangesAsync();
        }
        ErrorCode(await CompleteAsync(w, offId)).Should().Be("assets_outstanding");
        await using (var db = _fx.CreateDb())
        {
            var getController = new OffboardingController(db);
            SetPrincipal(getController, w.TenantId, w.HrUserId);
            var got = Assert.IsType<EmployeeOffboarding>(Assert.IsType<OkObjectResult>(await getController.Get(offId, CancellationToken.None)).Value);
            got.AssetsReturned.Should().BeFalse("the API derives the flag from the register");
            (await db.Employees.SingleAsync(e => e.Id == w.EmployeeId)).Status.Should().Be("Offboarded");
        }

        // Record the return → the register clears the leaver and sets the legacy flag itself.
        await using (var db = _fx.CreateDb())
            await Custody(db).ReturnAsync(w.TenantId, assetId, new ReturnAssetRequest("GOOD", null, "Exit clearance"), Ctx(w), CancellationToken.None);
        await using (var db = _fx.CreateDb())
            (await db.EmployeeOffboardings.SingleAsync(o => o.Id == offId)).AssetsReturned.Should().BeTrue();

        (await CompleteAsync(w, offId)).Should().BeOfType<OkObjectResult>();
        await using (var db = _fx.CreateDb())
            (await db.Employees.SingleAsync(e => e.Id == w.EmployeeId)).Status.Should().Be("Archived");
    }

    [Fact]
    public async Task OffboardingWithoutRegisterAssets_KeepsTheLegacyCheckboxBehaviour()
    {
        var w = await SeedWorldAsync();
        var offId = await InitiateOffboardingReadyToArchiveAsync(w, w.EmployeeId);
        // No register items: the checkbox is still the attestation, and it is still required.
        ErrorCode(await CompleteAsync(w, offId)).Should().Be("offboarding_checklist_incomplete");
        (await TickAssetsReturnedAsync(w, offId)).Should().BeOfType<OkObjectResult>();
        (await CompleteAsync(w, offId)).Should().BeOfType<OkObjectResult>();
    }

    // ── 3. Write-off needs approval, and only the FINAL step writes it off ───────

    [Fact]
    public async Task WriteOff_Step1OfATwoStepWorkflowDoesNotWriteOff_FinalStepDoes_AndThatUnblocksTheArchive()
    {
        var w = await SeedWorldAsync(withDefaultWriteOffWorkflow: false);
        Guid workflowId;
        await using (var db = _fx.CreateDb())
        {
            var wf = new ApprovalWorkflow
            {
                TenantId = w.TenantId, Code = $"AWO2-{Guid.NewGuid():N}"[..20], Name = "Two-step write-off",
                EntityName = AssetCustodyService.ApprovalEntityName, IsDefault = true, IsActive = true,
            };
            wf.Steps.Add(new ApprovalWorkflowStep { TenantId = w.TenantId, WorkflowId = wf.Id, StepOrder = 1, StepName = "IT sign-off", ApproverType = "Role", ApproverRole = "IT Manager" });
            wf.Steps.Add(new ApprovalWorkflowStep { TenantId = w.TenantId, WorkflowId = wf.Id, StepOrder = 2, StepName = "Finance sign-off", ApproverType = "Role", ApproverRole = "Finance Approver", IsFinalStep = true });
            db.ApprovalWorkflows.Add(wf);
            await db.SaveChangesAsync();
            workflowId = wf.Id;
        }
        var assetId = await CreateAssetAsync(w);
        await IssueAsync(w, assetId, w.EmployeeId);
        var offId = await InitiateOffboardingReadyToArchiveAsync(w, w.EmployeeId);

        AssetWriteOffRequest writeOff;
        await using (var db = _fx.CreateDb())
            writeOff = await Custody(db).RequestWriteOffAsync(w.TenantId, assetId,
                new WriteOffAssetRequest("Lost", "Left in a taxi; police report filed."), Ctx(w), CancellationToken.None);

        await using (var db = _fx.CreateDb())
        {
            var approval = await db.ApprovalRequests.SingleAsync(a => a.Id == writeOff.ApprovalRequestId);
            approval.EntityName.Should().Be("AssetWriteOff");
            approval.EntityId.Should().Be(writeOff.Id.ToString());
            approval.WorkflowId.Should().Be(workflowId, "WorkflowId = null routes through the F1 router to the configured workflow");
            approval.CurrentStepOrder.Should().Be(1);
            approval.CompanyId.Should().Be(w.CompanyId);
        }
        // A requested write-off clears nothing.
        ErrorCode(await CompleteAsync(w, offId)).Should().Be("assets_outstanding");

        // The requester cannot approve their own write-off (maker-checker).
        await using (var db = _fx.CreateDb())
        {
            var selfApprove = () => new ApprovalWorkflowService(db, new AuditService(db)).DecideAsync(w.TenantId, writeOff.ApprovalRequestId!.Value,
                new ApprovalDecisionRequest("Approve", "self"), Ctx(w, w.HrUserId, ["IT Manager", "Finance Approver"]), CancellationToken.None);
            await selfApprove.Should().ThrowAsync<InvalidOperationException>();
        }

        // Step 1 approves.
        await using (var db = _fx.CreateDb())
            await new ApprovalWorkflowService(db, new AuditService(db)).DecideAsync(w.TenantId, writeOff.ApprovalRequestId!.Value,
                new ApprovalDecisionRequest("Approve", "IT confirms loss"), Ctx(w, Guid.NewGuid(), ["IT Manager"]), CancellationToken.None);
        await using (var db = _fx.CreateDb())
        {
            (await Custody(db).ReconcileWriteOffsAsync(w.TenantId, Ctx(w), CancellationToken.None)).Should().Be(0);
            (await db.ApprovalRequests.SingleAsync(a => a.Id == writeOff.ApprovalRequestId)).Status.Should().Be("Pending");
            (await db.AssetWriteOffRequests.SingleAsync(x => x.Id == writeOff.Id)).Status.Should().Be(AssetWriteOffStatuses.Pending);
            (await db.Assets.SingleAsync(a => a.Id == assetId)).Status.Should().Be(AssetStatuses.Assigned, "step 1 of 2 must not write the asset off");
            (await db.AssetAssignments.SingleAsync(a => a.AssetId == assetId)).Status.Should().Be(AssetAssignmentStatuses.Active);
        }
        ErrorCode(await CompleteAsync(w, offId)).Should().Be("assets_outstanding");

        // Step 2 (final) approves. The archive is unblocked by the approval ITSELF, before any reconcile has run.
        await using (var db = _fx.CreateDb())
            await new ApprovalWorkflowService(db, new AuditService(db)).DecideAsync(w.TenantId, writeOff.ApprovalRequestId!.Value,
                new ApprovalDecisionRequest("Approve", "Finance accepts the loss"), Ctx(w, Guid.NewGuid(), ["Finance Approver"]), CancellationToken.None);
        await using (var db = _fx.CreateDb())
            (await new AssetClearanceService(db).CountOutstandingAsync(w.TenantId, w.EmployeeId, CancellationToken.None)).Should().Be(0);

        // Reconcile materialises it in the register (idempotently).
        await using (var db = _fx.CreateDb())
        {
            (await Custody(db).ReconcileWriteOffsAsync(w.TenantId, Ctx(w), CancellationToken.None)).Should().Be(1);
            (await Custody(db).ReconcileWriteOffsAsync(w.TenantId, Ctx(w), CancellationToken.None)).Should().Be(0);
        }
        await using (var db = _fx.CreateDb())
        {
            (await db.Assets.SingleAsync(a => a.Id == assetId)).Status.Should().Be(AssetStatuses.Lost);
            var closed = await db.AssetAssignments.SingleAsync(a => a.AssetId == assetId);
            closed.Status.Should().Be(AssetAssignmentStatuses.WrittenOff);
            closed.WriteOffRequestId.Should().Be(writeOff.Id);
            (await db.AssetWriteOffRequests.SingleAsync(x => x.Id == writeOff.Id)).Status.Should().Be(AssetWriteOffStatuses.Approved);
            (await db.EmployeeOffboardings.SingleAsync(o => o.Id == offId)).AssetsReturned.Should().BeTrue();
            (await db.AuditLogs.AnyAsync(l => l.EntityId == assetId.ToString() && l.Action == "asset.written_off")).Should().BeTrue();
        }
        (await CompleteAsync(w, offId)).Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task WriteOff_Rejected_LeavesTheAssetWithItsHolder()
    {
        var w = await SeedWorldAsync();
        var assetId = await CreateAssetAsync(w);
        await IssueAsync(w, assetId, w.EmployeeId);
        AssetWriteOffRequest writeOff;
        await using (var db = _fx.CreateDb())
            writeOff = await Custody(db).RequestWriteOffAsync(w.TenantId, assetId, new WriteOffAssetRequest("Damaged", "Screen cracked in transit"), Ctx(w), CancellationToken.None);

        // While pending, custody is frozen: no return/transfer until the approver decides.
        await using (var db = _fx.CreateDb())
        {
            var act = () => Custody(db).ReturnAsync(w.TenantId, assetId, new ReturnAssetRequest("GOOD", null, null), Ctx(w), CancellationToken.None);
            (await act.Should().ThrowAsync<AssetOperationException>()).Which.Code.Should().Be("write_off_pending");
        }

        await using (var db = _fx.CreateDb())
            await new ApprovalWorkflowService(db, new AuditService(db)).DecideAsync(w.TenantId, writeOff.ApprovalRequestId!.Value,
                new ApprovalDecisionRequest("Reject", "It was found in the office"), Ctx(w, Guid.NewGuid(), ["HR Manager"]), CancellationToken.None);
        await using (var db = _fx.CreateDb())
            (await Custody(db).ReconcileWriteOffsAsync(w.TenantId, Ctx(w), CancellationToken.None)).Should().Be(1);
        await using (var db = _fx.CreateDb())
        {
            (await db.AssetWriteOffRequests.SingleAsync(x => x.Id == writeOff.Id)).Status.Should().Be(AssetWriteOffStatuses.Rejected);
            (await db.Assets.SingleAsync(a => a.Id == assetId)).Status.Should().Be(AssetStatuses.Assigned);
            (await new AssetClearanceService(db).CountOutstandingAsync(w.TenantId, w.EmployeeId, CancellationToken.None)).Should().Be(1);
            await Custody(db).ReturnAsync(w.TenantId, assetId, new ReturnAssetRequest("GOOD", null, "Found"), Ctx(w), CancellationToken.None);
        }
    }

    [Fact]
    public async Task WriteOff_WithNoWorkflowConfigured_IsATyped422_AndLeavesNothingBehind()
    {
        var w = await SeedWorldAsync(withDefaultWriteOffWorkflow: false);
        var assetId = await CreateAssetAsync(w);
        await IssueAsync(w, assetId, w.EmployeeId);

        await using (var db = _fx.CreateDb())
        {
            var controller = new AssetsController(db, Custody(db));
            SetPrincipal(controller, w.TenantId, w.HrUserId);
            var result = await controller.WriteOff(assetId, new WriteOffAssetRequest("Lost", "Missing after the move"), CancellationToken.None);
            result.Should().BeOfType<UnprocessableEntityObjectResult>();
        }
        await using var check = _fx.CreateDb();
        (await check.AssetWriteOffRequests.CountAsync(x => x.AssetId == assetId)).Should().Be(0, "the write-off row rolls back with the failed routing");
        (await check.ApprovalRequests.CountAsync(x => x.TenantId == w.TenantId && x.EntityName == "AssetWriteOff")).Should().Be(0);
        (await check.Assets.SingleAsync(a => a.Id == assetId)).Version.Should().Be(1, "create=0, issue=1; the failed write-off's bump rolled back");
    }

    // ── 4. Reminders: once, not repeatedly ───────────────────────────────────────

    private ServiceProvider ReminderServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMemoryCache();
        services.AddDataProtection();
        services.AddScoped(_ => _fx.CreateDb());
        services.AddSingleton<IEmailService>(new NoEmail());
        services.AddScoped<INotificationRecipientResolver, NotificationRecipientResolver>();
        services.AddScoped<INotificationProviderConfigReader, NotificationProviderConfigReader>();
        services.AddScoped<ISmsProvider, NullSmsProvider>();
        services.AddScoped<IWhatsAppProvider, NullWhatsAppProvider>();
        services.AddScoped<IPushProvider, NullPushProvider>();
        services.AddScoped<INotificationChannelDispatcher, EmailChannelDispatcher>();
        services.AddScoped<INotificationChannelDispatcher, SmsChannelDispatcher>();
        services.AddScoped<INotificationChannelDispatcher, WhatsAppChannelDispatcher>();
        services.AddScoped<INotificationChannelDispatcher, PushChannelDispatcher>();
        services.AddScoped<INotificationService, NotificationService>();
        services.AddScoped<IAuditService>(p => new AuditService(p.GetRequiredService<ZayraDbContext>()));
        services.AddScoped<IApprovalWorkflowService>(p => new ApprovalWorkflowService(p.GetRequiredService<ZayraDbContext>(), p.GetRequiredService<IAuditService>()));
        services.AddScoped<IAssetCustodyService, AssetCustodyService>();
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task OverdueAndDueSoonReminders_AreEnqueuedOnce_NotOnEverySweep()
    {
        var w = await SeedWorldAsync();
        var today = AssetCustodyService.Today();
        var overdueAsset = await CreateAssetAsync(w);
        var soonAsset = await CreateAssetAsync(w);
        var overdueAssignment = await IssueAsync(w, overdueAsset, w.EmployeeId);
        var soonAssignment = await IssueAsync(w, soonAsset, w.Employee2Id, today.AddDays(2));
        await using (var db = _fx.CreateDb())
        {
            // Issued long ago, due back last week.
            var a = await db.AssetAssignments.SingleAsync(x => x.Id == overdueAssignment);
            a.IssuedOn = today.AddDays(-60);
            a.ExpectedReturnDate = today.AddDays(-7);
            await db.SaveChangesAsync();
        }

        await using var sp = ReminderServices();
        var worker = new AssetReturnReminderWorker(sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<AssetReturnReminderWorker>>());

        async Task<(int Overdue, int DueSoon, int IssuerOverdue)> CountAsync()
        {
            await using var db = _fx.CreateDb();
            var rows = await db.NotificationDeliveries.AsNoTracking()
                .Where(d => d.TenantId == w.TenantId && d.EntityName == AssetReturnReminderWorker.EntityName).ToListAsync();
            return (rows.Count(d => d.EventCode == AssetReturnReminderWorker.OverdueEventCode && d.EmployeeId == w.EmployeeId && d.EntityId == overdueAssignment.ToString()),
                    rows.Count(d => d.EventCode == AssetReturnReminderWorker.DueSoonEventCode && d.EntityId == soonAssignment.ToString()),
                    rows.Count(d => d.EventCode == AssetReturnReminderWorker.OverdueEventCode && d.UserId == w.HrUserId && d.EmployeeId == null));
        }

        (await worker.DrainOnceAsync(CancellationToken.None, today)).Should().BeGreaterThanOrEqualTo(2);
        var first = await CountAsync();
        first.Overdue.Should().BeGreaterThan(0, "the holder is told the item is overdue");
        first.DueSoon.Should().BeGreaterThan(0, "the other holder is told the item is due soon");
        first.IssuerOverdue.Should().BeGreaterThan(0, "whoever issued an overdue item is told too");
        await using (var db = _fx.CreateDb())
        {
            (await db.AssetAssignments.SingleAsync(x => x.Id == overdueAssignment)).OverdueReminderSentAtUtc.Should().NotBeNull();
            (await db.AssetAssignments.SingleAsync(x => x.Id == soonAssignment)).DueSoonReminderSentAtUtc.Should().NotBeNull();
            (await db.AssetAssignments.SingleAsync(x => x.Id == overdueAssignment)).DueSoonReminderSentAtUtc.Should().BeNull("an already-overdue item never gets a 'due soon'");
        }

        // Hourly sweeps continue — nothing new is enqueued for these assignments. (The sweep is system-wide
        // over the shared test database, so assert on this tenant's outbox, not on the sweep's return value.)
        await worker.DrainOnceAsync(CancellationToken.None, today);
        await worker.DrainOnceAsync(CancellationToken.None, today.AddDays(1));
        (await CountAsync()).Should().Be(first);

        // Even if the ledger stamp is lost (crash after enqueue, before the stamp saved), the outbox's
        // unique dedupe key refuses a second copy.
        await using (var db = _fx.CreateDb())
        {
            var a = await db.AssetAssignments.SingleAsync(x => x.Id == overdueAssignment);
            a.OverdueReminderSentAtUtc = null;
            await db.SaveChangesAsync();
        }
        await worker.DrainOnceAsync(CancellationToken.None, today);
        (await CountAsync()).Should().Be(first, "the outbox dedupe key is the second guard");
        await using (var db = _fx.CreateDb())
            (await db.AssetAssignments.SingleAsync(x => x.Id == overdueAssignment)).OverdueReminderSentAtUtc.Should().NotBeNull("the stamp is re-set from outbox evidence");
    }

    [Fact]
    public async Task Worker_SettlesDecidedWriteOffs_ForEveryTenant()
    {
        var w = await SeedWorldAsync();
        var assetId = await CreateAssetAsync(w);
        await IssueAsync(w, assetId, w.EmployeeId);
        AssetWriteOffRequest writeOff;
        await using (var db = _fx.CreateDb())
            writeOff = await Custody(db).RequestWriteOffAsync(w.TenantId, assetId, new WriteOffAssetRequest("Lost", "Stolen from vehicle"), Ctx(w), CancellationToken.None);
        await using (var db = _fx.CreateDb())
            await new ApprovalWorkflowService(db, new AuditService(db)).DecideAsync(w.TenantId, writeOff.ApprovalRequestId!.Value,
                new ApprovalDecisionRequest("Approve", "ok"), Ctx(w, Guid.NewGuid(), ["HR Manager"]), CancellationToken.None);

        await using var sp = ReminderServices();
        var worker = new AssetReturnReminderWorker(sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<AssetReturnReminderWorker>>());
        (await worker.SettleWriteOffsOnceAsync(CancellationToken.None)).Should().BeGreaterThanOrEqualTo(1);
        await using var check = _fx.CreateDb();
        (await check.Assets.SingleAsync(a => a.Id == assetId)).Status.Should().Be(AssetStatuses.Lost);
    }

    // ── 5. Isolation ─────────────────────────────────────────────────────────────

    private sealed class Accessor : IHttpContextAccessor { public HttpContext? HttpContext { get; set; } }

    private static HttpContext Principal(Guid tenantId, Guid userId, Guid? companyId, params Claim[] extra)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()),
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new("permission", "employees.read"),
            new("permission", "employees.write"),
        };
        if (companyId is Guid c) claims.Add(new Claim("entity_access", JsonSerializer.Serialize(new { c, r = "Admin" })));
        claims.AddRange(extra);
        return new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) };
    }

    [Fact]
    public async Task TenantIsolation_AnotherTenantCannotSeeOrOperateOnTheAsset()
    {
        var a = await SeedWorldAsync();
        var b = await SeedWorldAsync();
        var assetA = await CreateAssetAsync(a);

        var accessor = new Accessor { HttpContext = Principal(b.TenantId, b.HrUserId, null) };
        await using var db = _fx.CreateDbWithAccessor(accessor);
        var controller = new AssetsController(db, Custody(db)) { ControllerContext = new ControllerContext { HttpContext = accessor.HttpContext! } };

        var list = Assert.IsType<AssetPagedResult>(Assert.IsType<OkObjectResult>((await controller.List(null, null, null, null, null)).Result).Value);
        list.Items.Should().NotContain(x => x.Id == assetA);
        (await controller.Get(assetA, CancellationToken.None)).Result.Should().BeOfType<NotFoundResult>();

        // Even naming tenant A's asset while holding tenant B's token finds nothing to issue.
        var issue = await controller.Issue(assetA, new IssueAssetRequest(b.EmployeeId, null, null, null, null), CancellationToken.None);
        issue.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(404);
        await using var check = _fx.CreateDb();
        (await check.AssetAssignments.CountAsync(x => x.AssetId == assetA)).Should().Be(0);
    }

    [Fact]
    public async Task CompanyIsolation_AUserScopedToOneCompanySeesOnlyThatCompanysAssets()
    {
        var w = await SeedWorldAsync();
        var assetA = await CreateAssetAsync(w);
        // A second company in the same tenant with its own asset.
        Guid companyB;
        await using (var db = _fx.CreateDb())
        {
            var company = new Company
            {
                TenantId = w.TenantId, LegalNameEn = "Sister Co", CountryCode = "SAU", Jurisdiction = "mainland",
                DefaultCurrency = "SAR", RegistrationNumber = $"REG-{Guid.NewGuid():N}", IsActive = true,
            };
            db.Companies.Add(company);
            await db.SaveChangesAsync();
            companyB = company.Id;
        }
        Guid assetB;
        await using (var db = _fx.CreateDb())
            assetB = (await Custody(db).CreateAsync(w.TenantId, new AssetUpsertRequest($"B-{Guid.NewGuid():N}"[..12], "Sister laptop", null, "LAPTOP",
                null, null, null, null, null, null, companyB, null, null, null, null), Ctx(w), CancellationToken.None)).Id;

        var accessor = new Accessor { HttpContext = Principal(w.TenantId, w.HrUserId, w.CompanyId) };
        await using var scoped = _fx.CreateDbWithAccessor(accessor);
        var controller = new AssetsController(scoped, Custody(scoped)) { ControllerContext = new ControllerContext { HttpContext = accessor.HttpContext! } };
        var list = Assert.IsType<AssetPagedResult>(Assert.IsType<OkObjectResult>((await controller.List(null, null, null, null, null)).Result).Value);
        list.Items.Select(x => x.Id).Should().Contain(assetA).And.NotContain(assetB);
        (await controller.Get(assetB, CancellationToken.None)).Result.Should().BeOfType<NotFoundResult>();
        var issue = await controller.Issue(assetB, new IssueAssetRequest(w.EmployeeId, null, null, null, null), CancellationToken.None);
        issue.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(404);

        // Cross-company custody is refused even for an unscoped admin: the holder must belong to the owner.
        await using var db2 = _fx.CreateDb();
        var cross = () => Custody(db2).IssueAsync(w.TenantId, assetB, new IssueAssetRequest(w.EmployeeId, null, null, null, null), Ctx(w), CancellationToken.None);
        (await cross.Should().ThrowAsync<AssetOperationException>()).Which.Code.Should().Be("company_mismatch");
    }

    [Fact]
    public async Task EssMyAssets_ReturnsOnlyTheCallersOwnAssets()
    {
        var w = await SeedWorldAsync();
        var mine = await CreateAssetAsync(w);
        var theirs = await CreateAssetAsync(w);
        var myAssignment = await IssueAsync(w, mine, w.EmployeeId);
        await IssueAsync(w, theirs, w.Employee2Id);
        await using (var seed = _fx.CreateDb())
        {
            var a = await seed.AssetAssignments.SingleAsync(x => x.Id == myAssignment);
            a.IssuedOn = AssetCustodyService.Today().AddDays(-30);
            a.ExpectedReturnDate = AssetCustodyService.Today().AddDays(-1);
            await seed.SaveChangesAsync();
        }

        var userId = Guid.NewGuid();
        var accessor = new Accessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("tenant_id", w.TenantId.ToString()),
                    new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                    new Claim("permission", "ess.read"),
                    new Claim("employee_id", w.EmployeeId.ToString()),
                }, "test")),
            },
        };
        await using var db = _fx.CreateDbWithAccessor(accessor);
        var controller = new EssAssetsController(db) { ControllerContext = new ControllerContext { HttpContext = accessor.HttpContext! } };
        var dto = Assert.IsType<EmployeeAssetsDto>(Assert.IsType<OkObjectResult>((await controller.MyAssets(CancellationToken.None)).Result).Value);
        dto.EmployeeId.Should().Be(w.EmployeeId);
        dto.Current.Select(x => x.AssetId).Should().Equal(mine);
        dto.Current.Single().IsOverdue.Should().BeTrue();
        dto.History.Should().BeEmpty();

        // No ESS permission → refused, whatever employee_id the token carries.
        var noEss = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim("tenant_id", w.TenantId.ToString()),
                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                new Claim("employee_id", w.Employee2Id.ToString()),
            }, "test")),
        };
        var refused = new EssAssetsController(db) { ControllerContext = new ControllerContext { HttpContext = noEss } };
        (await refused.MyAssets(CancellationToken.None)).Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(403);
    }

    // ── 6. Migration data step ───────────────────────────────────────────────────

    [Fact]
    public async Task MigrationDataStep_InstallsTheDefaultWriteOffWorkflow_Idempotently()
    {
        var w = await SeedWorldAsync(withDefaultWriteOffWorkflow: false);
        await using var db = _fx.CreateDb();
        // Run inside a transaction and roll back: the SQL targets every tenant in the shared test database.
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlRawAsync(Zayra.Api.Migrations.AddAssetCustody.InstallDefaultWriteOffWorkflowSql);
            await db.Database.ExecuteSqlRawAsync(Zayra.Api.Migrations.AddAssetCustody.InstallDefaultWriteOffWorkflowSql);
            var wfs = await db.ApprovalWorkflows.Include(x => x.Steps)
                .Where(x => x.TenantId == w.TenantId && x.EntityName == "AssetWriteOff").ToListAsync();
            wfs.Should().ContainSingle();
            wfs[0].IsActive.Should().BeTrue();
            wfs[0].IsDefault.Should().BeTrue();
            wfs[0].Steps.Should().ContainSingle(s => s.IsFinalStep && s.ApproverRole == "HR Manager");
            await tx.RollbackAsync();
        });
    }

    private sealed class NoEmail : IEmailService
    {
        public Task SendAsync(string toAddress, string toName, string subject, string htmlBody,
            IReadOnlyList<EmailAttachment>? attachments = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("must not be called when unconfigured");
        public Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
    }
}
