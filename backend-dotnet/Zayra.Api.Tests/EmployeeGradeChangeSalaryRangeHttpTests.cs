using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Zayra.Api.Application.Employees;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// One edit that moves an employee to a new grade AND states a salary package: the package's range check is made against the grade the employee is moving
/// TO, not the grade they are leaving. Judged against the old grade, an employee promoted G1 → G5 on a G5-sized salary was refused as "outside grade G1's
/// salary range", nothing was saved, and the grade never changed.
/// </summary>
public class EmployeeGradeChangeSalaryRangeHttpTests : IClassFixture<EmployeeDraftCreateHttpFixture>
{
    private readonly EmployeeDraftCreateHttpFixture _fx;
    public EmployeeGradeChangeSalaryRangeHttpTests(EmployeeDraftCreateHttpFixture fixture) => _fx = fixture;

    // Whole pay = 14000 + 1000 + 400 + 0 + 0 + 0 = 15400.
    private static object Salary(string structure) => Pay(structure, 14000m);

    private static object Pay(string structure, decimal basic, string date = "2026-01-01") => new
    {
        basicSalary = basic, housingAllowance = 1000m, transportAllowance = 400m, foodAllowance = 0m,
        mobileAllowance = 0m, otherAllowance = 0m, fixedDeduction = 0m,
        salaryStructureCode = structure, effectiveDate = date, currency = "SAR",
    };

    private async Task<(int EmployeeId, Grade Low, Grade High, string Structure)> SeedAsync(bool startOnHigh = false, bool active = false)
    {
        var suffix = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var structure = $"GRADE-RANGE-{suffix}";
        using var scope = _fx.Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var low = new Grade { TenantId = _fx.TenantId, CompanyId = _fx.CompanyId, Code = $"LO{suffix}", Name = $"Low {suffix}", Currency = "SAR", MinSalary = 1000m, MaxSalary = 3000m, IsActive = true };
        var high = new Grade { TenantId = _fx.TenantId, CompanyId = _fx.CompanyId, Code = $"HI{suffix}", Name = $"High {suffix}", Currency = "SAR", MinSalary = 10000m, MaxSalary = 20000m, IsActive = true };
        db.Grades.AddRange(low, high);
        db.SalaryStructures.Add(new SalaryStructure { TenantId = _fx.TenantId, Code = structure, Name = structure, Currency = "SAR", EffectiveDate = new DateOnly(2025, 1, 1) });
        await db.SaveChangesAsync();

        using var created = await _fx.SendAsync(HttpMethod.Post, "/api/employees", new { englishName = $"Promoted {suffix}" });
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        var employee = await db.Employees.SingleAsync(e => e.Id == id);
        var start = startOnHigh ? high : low;
        employee.Grade = start.Code; employee.GradeId = start.Id;
        if (active) { employee.Status = "Active"; employee.ActivatedAtUtc = DateTime.UtcNow; }
        await db.SaveChangesAsync();
        return (id, low, high, structure);
    }

    [Fact]
    public async Task AGradeChangeInTheSameEdit_JudgesTheSalaryAgainstTheNewGrade_AndMovesTheGrade()
    {
        var (id, _, high, structure) = await SeedAsync();

        using var response = await _fx.SendAsync(HttpMethod.Put, $"/api/employees/{id}", new
        {
            effectiveDate = "2026-01-01", changes = new { grade = high.Code, salaryBreakdown = Salary(structure) },
        });

        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().NotBe(HttpStatusCode.UnprocessableEntity, text);
        response.IsSuccessStatusCode.Should().BeTrue(text);
        using var verify = _fx.Host.Services.CreateScope();
        var employee = await verify.ServiceProvider.GetRequiredService<ZayraDbContext>().Employees.AsNoTracking().SingleAsync(e => e.Id == id);
        employee.Grade.Should().Be(high.Code);
        employee.GradeId.Should().Be(high.Id, "the grade the user picked is the grade saved, not only its label");
    }

    [Fact]
    public async Task ASalaryOutsideTheNewGradesRange_IsStillRefused_EvenWhenItFitsTheOldGrade()
    {
        var (id, low, _, structure) = await SeedAsync(startOnHigh: true);

        using var response = await _fx.SendAsync(HttpMethod.Put, $"/api/employees/{id}", new
        {
            effectiveDate = "2026-01-01", changes = new { grade = low.Code, salaryBreakdown = Salary(structure) },
        });

        // 15,400 is inside the grade the employee is leaving (10,000-20,000) but outside the one they are moving to (1,000-3,000).
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, text);
        text.Should().Contain($"outside grade {low.Code}");
    }

    private async Task<Employee> LoadAsync(ZayraDbContext db, int id) => await db.Employees.SingleAsync(e => e.Id == id);

    private static Dictionary<string, JsonElement> Changes(object value) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(value))!;

    [Fact]
    public async Task AnApprovedGradeChange_SetsGradeId_ThenAnApprovedSalarySizedForTheNewGrade_Applies()
    {
        var (id, low, high, structure) = await SeedAsync(active: true);
        using var scope = _fx.Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var employee = await LoadAsync(db, id);

        // Applied exactly as an approval (or the effective-date job) applies it: the grade first, in its own change.
        await EmployeeChangeApplier.ApplyApprovedChangeAsync(db, _fx.TenantId, employee, Changes(new { grade = high.Code }), null, CancellationToken.None);
        await db.SaveChangesAsync();
        employee.GradeId.Should().Be(high.Id);
        employee.Grade.Should().Be(high.Code);

        // Then the salary, in a second change, judged against the grade now in force.
        await EmployeeChangeApplier.ApplyApprovedChangeAsync(db, _fx.TenantId, employee, Changes(new { salaryBreakdown = Salary(structure) }), null, CancellationToken.None);
        await db.SaveChangesAsync();
        (await db.EmployeeSalaryStructures.AnyAsync(s => s.TenantId == _fx.TenantId && s.EmployeeId == id && s.BasicSalary == 14000m && s.IsActive)).Should().BeTrue();
        low.Code.Should().NotBe(high.Code);
    }

    [Fact]
    public async Task AGradeThatMatchesNoGradeOrSeveral_IsRefusedInBothLanguages_AndNothingChanges()
    {
        var (id, low, _, _) = await SeedAsync(active: true);
        using (var scope = _fx.Host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
            db.Grades.AddRange(
                new Grade { TenantId = _fx.TenantId, CompanyId = _fx.CompanyId, Code = $"DA{low.Code[2..]}", Name = $"Twin {low.Code}", Currency = "SAR", IsActive = true },
                new Grade { TenantId = _fx.TenantId, CompanyId = _fx.CompanyId, Code = $"DB{low.Code[2..]}", Name = $"Twin {low.Code}", Currency = "SAR", IsActive = true });
            await db.SaveChangesAsync();
        }

        foreach (var term in new[] { "NO-SUCH-GRADE", $"Twin {low.Code}" })
        {
            using var response = await _fx.SendAsync(HttpMethod.Put, $"/api/employees/{id}", new { effectiveDate = "2026-01-01", changes = new { grade = term } });
            var text = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, text);
            var body = JsonSerializer.Deserialize<JsonElement>(text);
            body.GetProperty("code").GetString().Should().Be("grade_not_resolved");
            body.GetProperty("message").GetString().Should().Contain("No change was applied");
            body.GetProperty("messageAr").GetString().Should().NotBeNullOrWhiteSpace();
        }

        using var verify = _fx.Host.Services.CreateScope();
        var employee = await LoadAsync(verify.ServiceProvider.GetRequiredService<ZayraDbContext>(), id);
        employee.Grade.Should().Be(low.Code);
        employee.GradeId.Should().Be(low.Id);
    }

    // ── Review follow-ups: an ACTIVE employee's grade change is approved, judged on current pay, and audited ─────────────────────────────────

    private async Task<Guid> ChangeIdAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.Accepted)
    {
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(expected, text);
        return JsonSerializer.Deserialize<JsonElement>(text).GetProperty("changeRequestId").GetGuid();
    }

    private async Task ApproveAsync(Guid changeId)
    {
        using var scope = _fx.Host.Services.CreateScope();
        await ApprovalCenterDriver.ApproveChangeAsync(scope.ServiceProvider.GetRequiredService<ZayraDbContext>(), _fx.TenantId, changeId);
    }

    private async Task<Employee> ReloadAsync(int id)
    {
        using var scope = _fx.Host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ZayraDbContext>().Employees.AsNoTracking().SingleAsync(e => e.Id == id);
    }

    private async Task GiveCurrentPayAsync(int id, string structure)
    {
        using var scope = _fx.Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var employee = await LoadAsync(db, id);
        await EmployeeChangeApplier.ApplyApprovedChangeAsync(db, _fx.TenantId, employee, Changes(new { salaryBreakdown = Salary(structure) }), null, CancellationToken.None);
        await db.SaveChangesAsync();
    }

    private async Task<Grade> AddLooseGradeAsync()
    {
        using var scope = _fx.Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var loose = new Grade { TenantId = _fx.TenantId, CompanyId = _fx.CompanyId, Code = $"LS{Guid.NewGuid():N}"[..8].ToUpperInvariant(), Name = "Loose", Currency = "SAR", MinSalary = 0m, MaxSalary = 0m, IsActive = true };
        db.Grades.Add(loose);
        await db.SaveChangesAsync();
        return loose;
    }

    [Fact]
    public async Task AGradeEditOnAnActiveEmployee_IsQueuedForApproval_AndMovesOnlyWhenApproved_WithOldAndNewGradeAndTheApproverRecorded()
    {
        var (id, low, high, structure) = await SeedAsync(active: true);

        var changeId = await ChangeIdAsync(await _fx.SendAsync(HttpMethod.Put, $"/api/employees/{id}", new { effectiveDate = "2026-01-01", changes = new { grade = high.Code } }));
        var before = await ReloadAsync(id);
        before.GradeId.Should().Be(low.Id, "nothing moves until the change is approved");
        before.Grade.Should().Be(low.Code);

        await ApproveAsync(changeId);

        var after = await ReloadAsync(id);
        after.GradeId.Should().Be(high.Id);
        after.Grade.Should().Be(high.Code);
        using var scope = _fx.Host.Services.CreateScope();
        var history = await scope.ServiceProvider.GetRequiredService<ZayraDbContext>().EmployeeHistories.AsNoTracking()
            .Where(h => h.EmployeeId == id && h.EventType == "GradeChanged").ToListAsync();
        history.Should().ContainSingle();
        history[0].OldValue.Should().Be(low.Code);
        history[0].NewValue.Should().Be(high.Code);
        history[0].ApprovedByUserId.Should().NotBeNull("the approver is recorded when the grade is applied");

        // The salary sized for the new grade is then a separate step and is accepted.
        using var salary = await _fx.SendAsync(HttpMethod.Put, $"/api/employees/{id}", new { effectiveDate = "2026-01-01", changes = new { salaryBreakdown = Salary(structure) } });
        salary.StatusCode.Should().Be(HttpStatusCode.Accepted, await salary.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AGradeEditOnADraftEmployee_StillAppliesAtOnce_AndAuditsTheOldGradeToo()
    {
        var (id, low, high, _) = await SeedAsync();

        using var response = await _fx.SendAsync(HttpMethod.Put, $"/api/employees/{id}", new { effectiveDate = "2026-01-01", changes = new { grade = high.Code } });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        (await ReloadAsync(id)).GradeId.Should().Be(high.Id);
        using var scope = _fx.Host.Services.CreateScope();
        var history = await scope.ServiceProvider.GetRequiredService<ZayraDbContext>().EmployeeHistories.AsNoTracking()
            .Where(h => h.EmployeeId == id && h.EventType == "GradeChanged").ToListAsync();
        history.Should().ContainSingle();
        history[0].OldValue.Should().Be(low.Code);
        history[0].NewValue.Should().Be(high.Code);
    }

    [Fact]
    public async Task AGradeChangeNeedsAFinanceApprover_AndTheRequesterCannotApproveTheirOwn()
    {
        var (id, _, high, _) = await SeedAsync(active: true);
        var changeId = await ChangeIdAsync(await _fx.SendAsync(HttpMethod.Put, $"/api/employees/{id}", new { effectiveDate = "2026-01-01", changes = new { grade = high.Code } }));

        using var scope = _fx.Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var approval = await db.ApprovalRequests.AsNoTracking().SingleAsync(a => a.TenantId == _fx.TenantId && a.EntityId == changeId.ToString());
        approval.RequestedForEmployeeId.Should().Be(id, "the request is about the employee, so the subject-never-decides rule applies to it");
        var service = new Zayra.Api.Infrastructure.Approvals.ApprovalWorkflowService(db, new Zayra.Api.Infrastructure.Audit.AuditService(db));

        // A decider without payroll.approve cannot apply it, whatever else they hold.
        var noFinance = new Zayra.Api.Application.Auth.RequestContext("127.0.0.1", "xunit", Guid.NewGuid(), _fx.TenantId, ["Admin"], ["approvals.decide", "approvals.override"]);
        var refused = async () => await service.DecideAsync(_fx.TenantId, approval.Id, new Zayra.Api.Application.Approvals.ApprovalDecisionRequest("Approve", "no finance"), noFinance, CancellationToken.None);
        await refused.Should().ThrowAsync<Zayra.Api.Application.Approvals.ApprovalRoutingException>();

        // The person who raised it cannot approve it, finance rights or not.
        var writerId = (await db.Users.IgnoreQueryFilters().AsNoTracking().SingleAsync(u => u.TenantId == _fx.TenantId && u.Email == "writer@draft-create.local")).Id;
        var maker = new Zayra.Api.Application.Auth.RequestContext("127.0.0.1", "xunit", writerId, _fx.TenantId, ["Admin"], ["approvals.decide", "approvals.override", "payroll.approve"]);
        var self = async () => await service.DecideAsync(_fx.TenantId, approval.Id, new Zayra.Api.Application.Approvals.ApprovalDecisionRequest("Approve", "mine"), maker, CancellationToken.None);
        await self.Should().ThrowAsync<InvalidOperationException>();
        (await ReloadAsync(id)).GradeId.Should().NotBe(high.Id);
    }

    [Fact]
    public async Task AGradeMove_IsRefused_WhenTheEmployeesCurrentPayIsOutsideTheNewGradesRange()
    {
        var (id, low, _, structure) = await SeedAsync(startOnHigh: true, active: true);
        await GiveCurrentPayAsync(id, structure); // 15,400, inside the high grade

        using var response = await _fx.SendAsync(HttpMethod.Put, $"/api/employees/{id}", new { effectiveDate = "2026-01-01", changes = new { grade = low.Code } });

        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, text);
        text.Should().Contain("current pay").And.Contain("No change was applied");
        text.Should().NotContain("package", "there is no package in this edit, and the wording must not describe one");
        using var scope = _fx.Host.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<ZayraDbContext>().EmployeeChangeRequests.CountAsync(c => c.EmployeeId == id)).Should().Be(0);
    }

    [Fact]
    public async Task MovingToALooserGrade_RaisingThePay_AndMovingBack_CannotLaunderAnOutOfRangeSalary()
    {
        var (id, _, high, structure) = await SeedAsync(startOnHigh: true, active: true);
        await GiveCurrentPayAsync(id, structure);
        var loose = await AddLooseGradeAsync();

        await ApproveAsync(await ChangeIdAsync(await _fx.SendAsync(HttpMethod.Put, $"/api/employees/{id}", new { effectiveDate = "2026-01-01", changes = new { grade = loose.Code } })));
        (await ReloadAsync(id)).GradeId.Should().Be(loose.Id);
        await ApproveAsync(await ChangeIdAsync(await _fx.SendAsync(HttpMethod.Put, $"/api/employees/{id}", new { effectiveDate = "2026-01-02", changes = new { salaryBreakdown = Pay(structure, 90000m, "2026-01-02") } })));

        using var back = await _fx.SendAsync(HttpMethod.Put, $"/api/employees/{id}", new { effectiveDate = "2026-01-01", changes = new { grade = high.Code } });
        var text = await back.Content.ReadAsStringAsync();
        back.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, text);
        text.Should().Contain("current pay");
    }

    [Fact]
    public async Task ClearingTheGrade_IsRefusedWhileTheEmployeeHasASalaryRecord_ButAllowedForADraftWithNone()
    {
        var (id, _, high, structure) = await SeedAsync(startOnHigh: true, active: true);
        await GiveCurrentPayAsync(id, structure); // a salary record exists, inside the high grade
        foreach (var cleared in new object?[] { "", "   " })
        {
            using var response = await _fx.SendAsync(HttpMethod.Put, $"/api/employees/{id}", new { effectiveDate = "2026-01-01", changes = new { grade = cleared } });
            var text = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, text);
            JsonSerializer.Deserialize<JsonElement>(text).GetProperty("messageAr").GetString().Should().NotBeNullOrWhiteSpace();
        }
        (await ReloadAsync(id)).GradeId.Should().Be(high.Id);

        var (draftId, _, _, _) = await SeedAsync();
        using var draft = await _fx.SendAsync(HttpMethod.Put, $"/api/employees/{draftId}", new { effectiveDate = "2026-01-01", changes = new { grade = "" } });
        draft.StatusCode.Should().Be(HttpStatusCode.OK, await draft.Content.ReadAsStringAsync());
        (await ReloadAsync(draftId)).GradeId.Should().BeNull();
    }

    [Fact]
    public async Task AnUnchangedOrNullGrade_InAForm_IsIgnored_AndNeverFailsTheWholeSave()
    {
        var (id, low, _, _) = await SeedAsync(active: true);
        using (var scope = _fx.Host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
            var employee = await LoadAsync(db, id);
            employee.Grade = "Legacy free text"; employee.GradeId = null;
            await db.SaveChangesAsync();
        }

        using var echoed = await _fx.SendAsync(HttpMethod.Put, $"/api/employees/{id}", new { effectiveDate = "2026-01-01", changes = new { grade = "Legacy free text", jobTitle = "Echo test" } });
        echoed.StatusCode.Should().Be(HttpStatusCode.OK, await echoed.Content.ReadAsStringAsync());
        using var nulled = await _fx.SendAsync(HttpMethod.Put, $"/api/employees/{id}", new { effectiveDate = "2026-01-01", changes = new { grade = (string?)null, jobTitle = "Null test" } });
        nulled.StatusCode.Should().Be(HttpStatusCode.OK, await nulled.Content.ReadAsStringAsync());

        var employeeAfter = await ReloadAsync(id);
        employeeAfter.Grade.Should().Be("Legacy free text");
        employeeAfter.JobTitle.Should().Be("Null test");
        using var scope2 = _fx.Host.Services.CreateScope();
        (await scope2.ServiceProvider.GetRequiredService<ZayraDbContext>().EmployeeChangeRequests.CountAsync(c => c.EmployeeId == id)).Should().Be(0);
        low.Code.Should().NotBeNull();
    }

    // ── Final review: the "started" test cannot be flipped, a decoy package cannot hide in-force pay, grade text and link are written together ──

    private async Task SetImporterStyleActiveAsync(int id)
    {
        // Imported and legacy Active staff were never stamped as activated.
        using var scope = _fx.Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var employee = await LoadAsync(db, id);
        employee.Status = "Active"; employee.ActivatedAtUtc = null;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task AnActiveEmployee_FlippedBackToDraft_StillNeedsApprovalToChangeGrade_AndTheDraftSaveCannotChangeIt()
    {
        var (id, low, high, _) = await SeedAsync();
        await SetImporterStyleActiveAsync(id);

        using var flip = await _fx.SendAsync(HttpMethod.Patch, $"/api/employees/{id}/status", new { status = "Draft", effectiveDate = "2026-01-01", reason = "flip to draft" });
        flip.StatusCode.Should().Be(HttpStatusCode.OK, await flip.Content.ReadAsStringAsync());
        (await ReloadAsync(id)).Status.Should().Be("Draft");

        // The grade edit is still approved like any started employee's: queued, nothing moved.
        using var edit = await _fx.SendAsync(HttpMethod.Put, $"/api/employees/{id}", new { effectiveDate = "2026-01-01", changes = new { grade = high.Code } });
        await ChangeIdAsync(edit);
        (await ReloadAsync(id)).GradeId.Should().Be(low.Id, "a started employee does not become a new hire by being set to Draft");

        // The draft autosave is the other door that writes GradeId directly: it refuses to change the grade of someone who has started.
        var basis = (await ReloadAsync(id)).UpdatedAtUtc ?? DateTime.UtcNow;
        using var autosave = await _fx.SendAsync(HttpMethod.Put, $"/api/employees/{id}/hire-draft",
            new { baseUpdatedAtUtc = basis, request = new Dictionary<string, object?> { ["englishName"] = "Flipped", ["companyId"] = _fx.CompanyId, ["gradeId"] = high.Id } });
        autosave.StatusCode.Should().Be(HttpStatusCode.Conflict, await autosave.Content.ReadAsStringAsync());
        (await ReloadAsync(id)).GradeId.Should().Be(low.Id);
    }

    [Fact]
    public async Task AGenuineNeverStartedDraft_IsStillADraft_AfterAStatusRowWasWrittenForItsCreation()
    {
        var (id, _, high, _) = await SeedAsync();
        using var scope = _fx.Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        db.EmployeeStatusHistories.Add(new EmployeeStatusHistory { TenantId = _fx.TenantId, EmployeeId = id, OldStatus = "", NewStatus = "Draft", EffectiveDate = new DateOnly(2026, 1, 1), Reason = "created" });
        await db.SaveChangesAsync();

        using var edit = await _fx.SendAsync(HttpMethod.Put, $"/api/employees/{id}", new { effectiveDate = "2026-01-01", changes = new { grade = high.Code } });
        edit.StatusCode.Should().Be(HttpStatusCode.OK, await edit.Content.ReadAsStringAsync());
        (await ReloadAsync(id)).GradeId.Should().Be(high.Id);
    }

    [Fact]
    public async Task ABackdatedDecoyPackage_CannotHideInForcePayThatIsOutsideTheNewGrade()
    {
        var (id, low, _, structure) = await SeedAsync(startOnHigh: true, active: true);
        await GiveCurrentPayAsync(id, structure); // 15,400 in force since 2026-01-01, inside the high grade

        // The package is dated BEFORE the pay in force and sits inside the low grade (1,900 + 1,400 = 2,900 of 1,000-3,000): it is not the pay in force.
        using var decoy = await _fx.SendAsync(HttpMethod.Put, $"/api/employees/{id}", new
        {
            effectiveDate = "2025-06-01", changes = new { grade = low.Code, salaryBreakdown = Pay(structure, 1500m, "2025-06-01") },
        });
        var text = await decoy.Content.ReadAsStringAsync();
        decoy.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, text);
        text.Should().Contain("current pay").And.Contain("on or after 2026-01-01", "the refusal says what date would make the package the pay in force");
        using var scope = _fx.Host.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<ZayraDbContext>().EmployeeChangeRequests.CountAsync(c => c.EmployeeId == id)).Should().Be(0);

    }

    [Fact]
    public async Task APackageDatedAfterThePayInForce_ReplacesIt_SoOnlyThePackageIsJudgedAgainstTheNewGrade()
    {
        var (id, _, high, structure) = await SeedAsync(active: true);
        using (var scope = _fx.Host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
            await EmployeeChangeApplier.ApplyApprovedChangeAsync(db, _fx.TenantId, await LoadAsync(db, id), Changes(new { salaryBreakdown = Pay(structure, 1500m) }), null, CancellationToken.None);
            await db.SaveChangesAsync(); // 2,900 in force, inside the low grade and outside the high one
        }

        using var bare = await _fx.SendAsync(HttpMethod.Put, $"/api/employees/{id}", new { effectiveDate = "2026-01-02", changes = new { grade = high.Code } });
        bare.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, await bare.Content.ReadAsStringAsync());

        using var replaces = await _fx.SendAsync(HttpMethod.Put, $"/api/employees/{id}", new
        {
            effectiveDate = "2026-01-02", changes = new { grade = high.Code, salaryBreakdown = Pay(structure, 14000m, "2026-01-02") },
        });
        await ChangeIdAsync(replaces);
    }

    [Fact]
    public async Task ABackdatedDecoyPackage_IsAlsoRefusedAtApproval_NotOnlyAtSubmit()
    {
        var (id, low, _, structure) = await SeedAsync(startOnHigh: true, active: true);
        await GiveCurrentPayAsync(id, structure);
        using var scope = _fx.Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var employee = await LoadAsync(db, id);

        var apply = async () => await EmployeeChangeApplier.ApplyApprovedChangeAsync(db, _fx.TenantId, employee,
            Changes(new { grade = low.Code, salaryBreakdown = Pay(structure, 1500m, "2025-06-01") }), null, CancellationToken.None);

        (await apply.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("current pay");
    }

    [Fact]
    public async Task AFutureDatedPackage_CannotHideTodaysOutOfRangePay_WhenTheGradeMovesNow()
    {
        var (id, low, _, structure) = await SeedAsync(startOnHigh: true, active: true);
        await GiveCurrentPayAsync(id, structure); // 15,400 in force
        using var scope = _fx.Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var employee = await LoadAsync(db, id);
        var tomorrow = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1).ToString("yyyy-MM-dd");

        // Applied now, the grade lands today; a package dated tomorrow is not the pay in force today, so today's pay is judged against the new grade.
        var apply = async () => await EmployeeChangeApplier.ApplyApprovedChangeAsync(db, _fx.TenantId, employee,
            Changes(new { grade = low.Code, salaryBreakdown = Pay(structure, 1500m, tomorrow) }), null, CancellationToken.None);

        (await apply.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("current pay");
    }

    private async Task<int> StartedDraftAsync(Grade? grade)
    {
        var (id, _, _, _) = await SeedAsync(active: true);
        using (var scope = _fx.Host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
            var employee = await LoadAsync(db, id);
            employee.ActivatedAtUtc = null; employee.Grade = grade?.Code ?? ""; employee.GradeId = grade?.Id;
            await db.SaveChangesAsync();
        }
        using var flip = await _fx.SendAsync(HttpMethod.Patch, $"/api/employees/{id}/status", new { status = "Draft", effectiveDate = "2026-01-01", reason = "flip to draft" });
        flip.StatusCode.Should().Be(HttpStatusCode.OK, await flip.Content.ReadAsStringAsync());
        return id;
    }

    private async Task<HttpResponseMessage> AutosaveAsync(int id, Dictionary<string, object?> request)
    {
        var basis = (await ReloadAsync(id)).UpdatedAtUtc ?? DateTime.UtcNow;
        return await _fx.SendAsync(HttpMethod.Put, $"/api/employees/{id}/hire-draft", new { baseUpdatedAtUtc = basis, request });
    }

    [Fact]
    public async Task AStartedDraft_CanAutosaveOtherFields_WhenTheFormEchoesItsUnchangedGradeId()
    {
        var (_, low, _, _) = await SeedAsync();
        var id = await StartedDraftAsync(low);

        using var saved = await AutosaveAsync(id, new() { ["englishName"] = "Renamed while started", ["companyId"] = _fx.CompanyId, ["gradeId"] = low.Id, ["jobTitle"] = "Analyst" });

        saved.StatusCode.Should().Be(HttpStatusCode.OK, await saved.Content.ReadAsStringAsync());
        var after = await ReloadAsync(id);
        (after.EnglishName, after.JobTitle, after.GradeId, after.Grade).Should().Be(("Renamed while started", "Analyst", (Guid?)low.Id, low.Code));
    }

    [Fact]
    public async Task AStartedDraftWithNoGrade_CannotGainOneThroughADesignationsDefault()
    {
        var (_, _, high, _) = await SeedAsync();
        var id = await StartedDraftAsync(null);
        Guid designationId;
        using (var scope = _fx.Host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
            var designation = new Designation { TenantId = _fx.TenantId, CompanyId = _fx.CompanyId, Code = $"D{Guid.NewGuid():N}"[..10].ToUpperInvariant(), TitleEn = "Senior", GradeId = high.Id, IsActive = true };
            db.Designations.Add(designation);
            await db.SaveChangesAsync();
            designationId = designation.Id;
        }

        using var saved = await AutosaveAsync(id, new() { ["englishName"] = "Designated", ["companyId"] = _fx.CompanyId, ["designationId"] = designationId });

        saved.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.UnprocessableEntity, HttpStatusCode.Conflict);
        var after = await ReloadAsync(id);
        after.GradeId.Should().BeNull("a grade for someone who has started comes only through an approved grade change, not a designation's default");
    }

    [Fact]
    public async Task ARehire_WritesTheGradeTextWithTheGradeLink()
    {
        var (id, _, high, _) = await SeedAsync();
        using (var scope = _fx.Host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
            var employee = await LoadAsync(db, id);
            employee.Status = "Terminated";
            await db.SaveChangesAsync();
        }

        using var rehire = await _fx.SendAsync(HttpMethod.Post, $"/api/employees/{id}/rehire",
            new { startDate = DateTime.UtcNow.Date, reason = "Rehired for the new season", gradeId = high.Id });
        rehire.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Created, HttpStatusCode.NoContent);
        var after = await ReloadAsync(id);
        after.GradeId.Should().Be(high.Id);
        after.Grade.Should().Be(high.Code, "the grade text follows the grade link, so a leave policy that matches on text sees the same grade");
    }

    [Fact]
    public async Task ADraftSave_KeepsGradeTextAndLinkTogether_IncludingWhenTheGradeIsCleared()
    {
        var (id, _, high, _) = await SeedAsync();

        async Task<HttpResponseMessage> SaveAsync(Guid? gradeId)
        {
            var basis = (await ReloadAsync(id)).UpdatedAtUtc ?? DateTime.UtcNow;
            return await _fx.SendAsync(HttpMethod.Put, $"/api/employees/{id}/hire-draft",
                new { baseUpdatedAtUtc = basis, request = new Dictionary<string, object?> { ["englishName"] = "Draft grade", ["companyId"] = _fx.CompanyId, ["gradeId"] = gradeId } });
        }

        using var set = await SaveAsync(high.Id);
        set.StatusCode.Should().Be(HttpStatusCode.OK, await set.Content.ReadAsStringAsync());
        var withGrade = await ReloadAsync(id);
        (withGrade.GradeId, withGrade.Grade).Should().Be((high.Id, high.Code));

        using var cleared = await SaveAsync(null);
        cleared.StatusCode.Should().Be(HttpStatusCode.OK, await cleared.Content.ReadAsStringAsync());
        var without = await ReloadAsync(id);
        without.GradeId.Should().BeNull();
        without.Grade.Should().BeNullOrEmpty("clearing the grade clears the text too, so no stale grade is left behind");
    }
}
