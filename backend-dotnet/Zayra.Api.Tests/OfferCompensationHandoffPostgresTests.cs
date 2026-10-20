using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Zayra.Api.Application.Employees;
using Zayra.Api.Controllers;
using Npgsql;
using Zayra.Api.Application.Recruitment;
using Zayra.Api.Infrastructure.Recruitment;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

[Collection("Integration")]
[Trait("Category", "Integration")]
public sealed class OfferCompensationHandoffPostgresTests(PostgresFixture fixture)
{
    [Fact]
    public async Task AcceptanceCopiesEveryFrozenRowAndReplayPreservesOneDraftAfterCatalogueChanges()
    {
        var seed = await OfferLifecycleTestData.SeedAsync(fixture);
        string source;
        await using (var db = fixture.CreateDb())
        {
            var offer = await db.OfferLetters.SingleAsync(o => o.Id == seed.OfferId);
            var snapshot = OfferCompensation.Calculate([
                new("BASIC", "Basic salary", null, "Cash", 1000m, null, "Monthly", true, null, null, 0),
                new("FOOD", "Meals", "وجبات", "Cash", 150m, null, "Monthly", true, null, null, 1),
                new("TICKET", "Annual flight", null, "Cash", 1200m, null, "Annual", true, null, null, 2),
                new("MEDICAL", "Medical", null, "Benefit", null, "Family cover", null, true, null, null, 3)
            ], "SAR", 2, new HashSet<string> { "BASIC" });
            OfferCompensation.ApplySnapshot(offer, snapshot with { PolicyVersion = 7 });
            source = offer.CompensationSnapshotJson!;
            await db.SaveChangesAsync();
        }
        Guid? first;
        await using (var db = fixture.CreateDb())
            first = (await new RecruitmentService(db).AcceptOfferAsync(seed.Actor.TenantId, seed.OfferId, seed.Actor.UserId, "Recruiter")).OnboardingDraftId;
        await using (var db = fixture.CreateDb())
        {
            var company = await db.Companies.SingleAsync(c => c.Id == seed.Actor.CompanyId);
            company.DefaultCurrency = "USD";
            await db.SaveChangesAsync();
            var replay = await new RecruitmentService(db).AcceptOfferAsync(seed.Actor.TenantId, seed.OfferId, seed.Actor.UserId, "Replay");
            replay.OnboardingDraftId.Should().Be(first);
            var draft = await db.EmployeeDrafts.SingleAsync(d => d.Id == first);
            draft.AcceptedOfferCompensationJson.Should().Be(source);
            draft.AcceptedOfferId.Should().Be(seed.OfferId);
            draft.AcceptedOfferCompanyId.Should().Be(seed.Actor.CompanyId);
            (await db.EmployeeDrafts.CountAsync(d => d.TenantId == seed.Actor.TenantId)).Should().Be(1);
        }
    }

    [Fact]
    public async Task UnconfirmedSnapshotBlocksActivationAndReviewWithoutLeakingPay()
    {
        var (seed, id) = await SeedAccepted(); await using var db = fixture.CreateDb();
        var controller = EmployeeDraftLifecyclePostgresTests.Employees(db, seed.Actor.TenantId, Guid.NewGuid(), role: "HR Officer");
        (await controller.ApproveDraft(id, default)).Result.Should().BeOfType<UnprocessableEntityObjectResult>();
        var review = (await controller.GetDraft(id, default)).Result.Should().BeOfType<OkObjectResult>().Subject.Value;
        var json = JsonSerializer.Serialize(review); json.Should().Contain("compensation").And.NotContain("Family cover");
        ((EmployeeDraftReviewDto)review!).Draft.Salary.Should().BeNull();
        (await db.Employees.CountAsync(e => e.TenantId == seed.Actor.TenantId)).Should().Be(0);
    }

    [Fact]
    public async Task ConfirmationIsSensitiveVersionedAuditedAndCannotBeSelfApproved()
    {
        var (seed, id) = await SeedAccepted(); var editor = Guid.NewGuid(); await using var db = fixture.CreateDb();
        var hidden = EmployeeDraftLifecyclePostgresTests.Employees(db, seed.Actor.TenantId, editor, role: "HR Officer");
        (await hidden.GetDraftOfferCompensation(id, default)).Result.Should().BeOfType<ForbidResult>();
        (await hidden.PutDraftOfferCompensation(id, new(1, OfferCompensationHandoffTests.Dispositions(), true), default)).Result.Should().BeOfType<ForbidResult>();
        var controller = EmployeeDraftLifecyclePostgresTests.Employees(db, seed.Actor.TenantId, editor);
        (await controller.PutDraftOfferCompensation(id, new(99, OfferCompensationHandoffTests.Dispositions(), true), default)).Result.Should().BeOfType<ConflictObjectResult>();
        (await controller.PutDraftOfferCompensation(id, new(1, OfferCompensationHandoffTests.Dispositions(), true), default)).Result.Should().BeOfType<OkObjectResult>();
        (await db.AuditLogs.CountAsync(a => a.EntityId == id.ToString() && a.UserId == editor && a.Action == "employee.draft_compensation_updated")).Should().Be(1);
        (await controller.ApproveDraft(id, default)).Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(403);
        (await controller.PutDraftOfferCompensation(id, new(1, OfferCompensationHandoffTests.Dispositions(), true), default)).Result.Should().BeOfType<ConflictObjectResult>();
    }

    [Fact]
    public async Task ConfirmedActivationWritesFrozenSalaryAndEffectiveDateExactlyOnce()
    {
        var (seed, id) = await SeedAccepted(); await Confirm(seed, id);
        await using var db = fixture.CreateDb(); var controller = EmployeeDraftLifecyclePostgresTests.Employees(db, seed.Actor.TenantId, Guid.NewGuid());
        var approval = await controller.ApproveDraft(id, default);
        approval.Result.Should().BeOfType<OkObjectResult>(JsonSerializer.Serialize((approval.Result as ObjectResult)?.Value));
        (await controller.ApproveDraft(id, default)).Result.Should().BeOfType<ConflictObjectResult>();
        var employee = await db.Employees.SingleAsync(e => e.TenantId == seed.Actor.TenantId);
        employee.CompanyId.Should().Be(seed.Actor.CompanyId); employee.Salary.Should().Be(1150);
        var assignment = await db.EmployeeSalaryStructures.SingleAsync(e => e.TenantId == seed.Actor.TenantId);
        assignment.BasicSalary.Should().Be(1000); assignment.FoodAllowance.Should().Be(150);
        assignment.OtherAllowance.Should().Be(0); assignment.FixedDeduction.Should().Be(0);
        assignment.Currency.Should().Be("SAR"); assignment.EffectiveDate.Should().Be(new DateOnly(2027, 1, 1));
    }

    /// <summary>
    /// The draft's PayrollProfileCode is the PAYROLL GROUP, not a salary structure. The hand-off used to pass it as the
    /// structure code, so a hire with payroll group "MONTHLY" was put on (and, before structure codes were checked, given
    /// a brand-new empty) structure called MONTHLY. The accepted offer names no structure: the default applies.
    /// </summary>
    [Fact]
    public async Task ThePayrollGroupIsNeverUsedAsTheSalaryStructure()
    {
        var (seed, id) = await SeedAccepted();
        await using (var db = fixture.CreateDb())
        {
            (await db.EmployeeDrafts.SingleAsync(d => d.Id == id)).PayrollProfileCode = "MONTHLY";
            await db.SaveChangesAsync();
        }
        await Confirm(seed, id);
        await using var verify = fixture.CreateDb();
        var approval = await EmployeeDraftLifecyclePostgresTests.Employees(verify, seed.Actor.TenantId, Guid.NewGuid()).ApproveDraft(id, default);
        approval.Result.Should().BeOfType<OkObjectResult>(JsonSerializer.Serialize((approval.Result as ObjectResult)?.Value));
        var assignment = await verify.EmployeeSalaryStructures.SingleAsync(e => e.TenantId == seed.Actor.TenantId);
        var structure = await verify.SalaryStructures.SingleAsync(s => s.Id == assignment.SalaryStructureId);
        structure.Code.Should().Match(c => c == "DIRECT" || c.StartsWith("GRADE-"), "the default structure applies when the offer names none");
        (await verify.SalaryStructures.CountAsync(s => s.TenantId == seed.Actor.TenantId && s.Code == "MONTHLY")).Should().Be(0);
    }

    [Theory]
    [InlineData("salary")][InlineData("date")][InlineData("currency")][InlineData("company")]
    public async Task ChangedInputsInvalidateOrRefusePreviouslyConfirmedTerms(string change)
    {
        var (seed, id) = await SeedAccepted(); await Confirm(seed, id);
        await using var db = fixture.CreateDb();
        if (change == "currency")
        { (await db.Companies.SingleAsync(c => c.Id == seed.Actor.CompanyId)).DefaultCurrency = "USD"; await db.SaveChangesAsync(); }
        else
        {
            var request = JsonSerializer.Deserialize<EmployeeDraftRequest>("{}", OfferCompensation.Json)!;
            if (change == "salary") request = request with { Salary = 1150 };
            if (change == "date") request = request with { JoiningDate = new DateTime(2027, 2, 1, 0, 0, 0, DateTimeKind.Utc) };
            if (change == "company")
            {
                var other = new Company { TenantId = seed.Actor.TenantId, DefaultCurrency = "SAR" };
                var branch = new Branch { TenantId = seed.Actor.TenantId, CompanyId = other.Id, NameEn = "Other entity", Code = "OTHER" };
                db.AddRange(other, branch); await db.SaveChangesAsync(); request = request with { Branch = branch.NameEn };
            }
            (await EmployeeDraftLifecyclePostgresTests.Employees(db, seed.Actor.TenantId, seed.Actor.UserId).UpdateDraft(id, request, default)).Result.Should().BeOfType<OkObjectResult>();
        }
        (await EmployeeDraftLifecyclePostgresTests.Employees(db, seed.Actor.TenantId, Guid.NewGuid()).ApproveDraft(id, default)).Result.Should().BeOfType<UnprocessableEntityObjectResult>();
        (await db.EmployeeSalaryStructures.CountAsync(e => e.TenantId == seed.Actor.TenantId)).Should().Be(0);
    }

    private async Task<(OfferLifecycleSeed Seed, Guid Id)> SeedAccepted()
    {
        var seed = await OfferLifecycleTestData.SeedAsync(fixture); await using var db = fixture.CreateDb();
        var offer = await db.OfferLetters.SingleAsync(o => o.Id == seed.OfferId);
        offer.OfferedDepartment = ""; offer.OfferedJobTitle = "";
        var draft = OfferCompensationHandoffTests.Draft();
        OfferCompensation.ApplySnapshot(offer, JsonSerializer.Deserialize<OfferCompensationSnapshot>(draft.AcceptedOfferCompensationJson!, OfferCompensation.Json)!);
        await db.SaveChangesAsync();
        var accepted = await new RecruitmentService(db).AcceptOfferAsync(seed.Actor.TenantId, seed.OfferId, seed.Actor.UserId, "Recruiter");
        await DraftStatutoryIdentity.ApplyAsync(db, accepted.OnboardingDraftId!.Value, "SA");
        return (seed, accepted.OnboardingDraftId!.Value);
    }
    private async Task Confirm(OfferLifecycleSeed seed, Guid id)
    {
        await using var db = fixture.CreateDb();
        (await EmployeeDraftLifecyclePostgresTests.Employees(db, seed.Actor.TenantId, seed.Actor.UserId)
            .PutDraftOfferCompensation(id, new(1, OfferCompensationHandoffTests.Dispositions(), true), default)).Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task DownstreamFailureRollsBackSalaryEmployeeAndActivationButRetainsConfirmation()
    {
        var (seed, id) = await SeedAccepted(); await Confirm(seed, id);
        await using var db = fixture.CreateDb();
        db.OnboardingTasks.Add(new() { TenantId = seed.Actor.TenantId, ApplicationId = seed.ApplicationId,
            EmployeeId = Guid.NewGuid(), TaskTitle = "Conflicting existing identity" }); await db.SaveChangesAsync();
        await FluentActions.Awaiting(async () => { await EmployeeDraftLifecyclePostgresTests.Employees(db, seed.Actor.TenantId, Guid.NewGuid()).ApproveDraft(id, default); })
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*different employee identity*");
        db.ChangeTracker.Clear();
        (await db.EmployeeSalaryStructures.CountAsync(e => e.TenantId == seed.Actor.TenantId)).Should().Be(0);
        (await db.SalaryStructures.CountAsync(e => e.TenantId == seed.Actor.TenantId)).Should().Be(0);
        (await db.Employees.CountAsync(e => e.TenantId == seed.Actor.TenantId)).Should().Be(0);
        (await db.EmployeeHistories.CountAsync(e => e.TenantId == seed.Actor.TenantId)).Should().Be(0);
        (await db.AuditLogs.CountAsync(e => e.TenantId == seed.Actor.TenantId && e.Action == "employee.activated")).Should().Be(0);
        var draft = await db.EmployeeDrafts.SingleAsync(d => d.Id == id);
        draft.Status.Should().Be("PendingHrApproval"); draft.OfferCompensationConfirmedFingerprint.Should().NotBeNull();
    }

    [Fact]
    public async Task ConfirmationAndApprovalRaceRechecksNewMakerUnderTheDraftLock()
    {
        var (seed, id) = await SeedAccepted(); var actor = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(fixture.ConnectionString); await connection.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand("SELECT id FROM employee_drafts WHERE id=@id FOR UPDATE", connection, tx))
        { command.Parameters.AddWithValue("id", id); await command.ExecuteScalarAsync(); }
        async Task<int> Waiters()
        {
            await using var db = fixture.CreateDb();
            // pg_stat_activity truncates long SELECT projections before their table name.
            return await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM pg_stat_activity WHERE datname=current_database() AND wait_event_type='Lock' AND pid<>pg_backend_pid()").SingleAsync();
        }
        async Task WaitFor(int count)
        { var until = DateTime.UtcNow.AddSeconds(15); while (await Waiters() < count && DateTime.UtcNow < until) await Task.Delay(20); (await Waiters()).Should().BeGreaterThanOrEqualTo(count); }
        await using var confirmationDb = fixture.CreateDb(); await using var approvalDb = fixture.CreateDb();
        var confirmation = EmployeeDraftLifecyclePostgresTests.Employees(confirmationDb, seed.Actor.TenantId, actor)
            .PutDraftOfferCompensation(id, new(1, OfferCompensationHandoffTests.Dispositions(), true), default);
        await WaitFor(1);
        var approval = EmployeeDraftLifecyclePostgresTests.Employees(approvalDb, seed.Actor.TenantId, actor).ApproveDraft(id, default);
        try { await WaitFor(2); } finally { await tx.CommitAsync(); }
        (await confirmation).Result.Should().BeOfType<OkObjectResult>();
        (await approval).Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(403);
        await using var verify = fixture.CreateDb();
        (await verify.Employees.CountAsync(e => e.TenantId == seed.Actor.TenantId)).Should().Be(0);
    }

    [Fact]
    public async Task ConfirmationAuditFailureCannotLeaveConfirmedUnauditedTerms()
    {
        var (seed, id) = await SeedAccepted(); await using var db = fixture.CreateDb();
        await db.Database.ExecuteSqlRawAsync("CREATE OR REPLACE FUNCTION handoff_test_fail_audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.action = 'employee.draft_compensation_updated' THEN RAISE EXCEPTION 'injected confirmation audit failure'; END IF; RETURN NEW; END $$; CREATE TRIGGER handoff_test_fail_audit BEFORE INSERT ON audit_logs FOR EACH ROW EXECUTE FUNCTION handoff_test_fail_audit();");
        try
        {
            await FluentActions.Awaiting(async () => { await EmployeeDraftLifecyclePostgresTests.Employees(db, seed.Actor.TenantId, seed.Actor.UserId)
                .PutDraftOfferCompensation(id, new(1, OfferCompensationHandoffTests.Dispositions(), true), default); }).Should().ThrowAsync<DbUpdateException>();
        }
        finally { await db.Database.ExecuteSqlRawAsync("DROP TRIGGER handoff_test_fail_audit ON audit_logs; DROP FUNCTION handoff_test_fail_audit();"); }
        db.ChangeTracker.Clear(); var draft = await db.EmployeeDrafts.SingleAsync(d => d.Id == id);
        draft.OfferCompensationVersion.Should().Be(1); draft.OfferCompensationConfirmedFingerprint.Should().BeNull();
        (await db.AuditLogs.CountAsync(a => a.EntityId == id.ToString() && a.Action == "employee.draft_compensation_updated")).Should().Be(0);
    }

    [Fact]
    public async Task AllSixEarningsSurviveCatalogueAndGradeChangesWithZeroDeduction()
    {
        var seed = await OfferLifecycleTestData.SeedAsync(fixture); await using var db = fixture.CreateDb();
        var catalogue = new PayComponent { TenantId = seed.Actor.TenantId, CompanyId = seed.Actor.CompanyId, Code = "FOOD",
            NameEn = "Meals", CalcMethod = "StructureField", StructureField = "FoodAllowance", Value = 999m };
        var grade = new Grade { TenantId = seed.Actor.TenantId, Code = "G1", Name = "Grade one", Currency = "USD" };
        db.AddRange(catalogue, grade, new GradePayScaleComponent { TenantId = seed.Actor.TenantId, GradeId = grade.Id,
            ComponentCode = "BASIC", ComponentName = "Grade basic", Amount = 7000 });
        var rows = new[] { ("BASIC", "BasicSalary", 1000m), ("HOUSE", "HousingAllowance", 200m), ("TRANS", "TransportAllowance", 75m),
            ("FOOD", "FoodAllowance", 150m), ("PHONE", "MobileAllowance", 25m), ("EXTRA", "OtherAllowance", 50m) };
        var snapshot = OfferCompensation.Calculate(rows.Select((r, i) => new OfferComponentInput(r.Item1, r.Item1, null, "Cash", r.Item3, null, "Monthly", true, null,
            r.Item1 == "FOOD" ? catalogue.Id : null, i)).ToArray(), "SAR", 2, new HashSet<string> { "BASIC" });
        snapshot = snapshot with { Components = snapshot.Components.Select((r, i) => r with { StructureField = rows[i].Item2 }).ToArray() };
        var offer = await db.OfferLetters.SingleAsync(o => o.Id == seed.OfferId); offer.OfferedDepartment = ""; offer.OfferedJobTitle = "";
        OfferCompensation.ApplySnapshot(offer, snapshot); await db.SaveChangesAsync();
        var accepted = await new RecruitmentService(db).AcceptOfferAsync(seed.Actor.TenantId, seed.OfferId, seed.Actor.UserId, "Recruiter");
        var id = accepted.OnboardingDraftId!.Value; await DraftStatutoryIdentity.ApplyAsync(db, id, "SA");
        (await db.EmployeeDrafts.SingleAsync(d => d.Id == id)).Grade = grade.Code;
        catalogue.Value = 8888m; catalogue.StructureField = "OtherAllowance"; catalogue.NameEn = "Changed after acceptance"; await db.SaveChangesAsync();
        var dispositions = rows.Select(r => new OfferCompensationDisposition(r.Item1, "Payroll", r.Item2)).ToArray();
        (await EmployeeDraftLifecyclePostgresTests.Employees(db, seed.Actor.TenantId, seed.Actor.UserId).PutDraftOfferCompensation(id, new(1, dispositions, true), default)).Result.Should().BeOfType<OkObjectResult>();
        var approval = await EmployeeDraftLifecyclePostgresTests.Employees(db, seed.Actor.TenantId, Guid.NewGuid()).ApproveDraft(id, default);
        approval.Result.Should().BeOfType<OkObjectResult>(JsonSerializer.Serialize((approval.Result as ObjectResult)?.Value));
        var salary = await db.EmployeeSalaryStructures.SingleAsync(s => s.TenantId == seed.Actor.TenantId);
        new[] { salary.BasicSalary, salary.HousingAllowance, salary.TransportAllowance, salary.FoodAllowance, salary.MobileAllowance, salary.OtherAllowance, salary.FixedDeduction }
            .Should().Equal(1000m, 200m, 75m, 150m, 25m, 50m, 0m);
        salary.Currency.Should().Be("SAR"); (await db.PayComponents.AsNoTracking().SingleAsync(p => p.Id == catalogue.Id)).Value.Should().Be(8888m);
        (await db.Employees.SingleAsync(e => e.TenantId == seed.Actor.TenantId)).Salary.Should().Be(1500m);
    }

    [Fact]
    public async Task JoiningDateChangeRequiresNewVersionedConfirmationAndWritesThatEffectiveDate()
    {
        var (seed, id) = await SeedAccepted(); await Confirm(seed, id); await using var db = fixture.CreateDb();
        var maker = EmployeeDraftLifecyclePostgresTests.Employees(db, seed.Actor.TenantId, seed.Actor.UserId);
        var request = JsonSerializer.Deserialize<EmployeeDraftRequest>("{}", OfferCompensation.Json)! with { JoiningDate = new DateTime(2027, 2, 1, 0, 0, 0, DateTimeKind.Utc) };
        (await maker.UpdateDraft(id, request, default)).Result.Should().BeOfType<OkObjectResult>();
        var draft = await db.EmployeeDrafts.AsNoTracking().SingleAsync(d => d.Id == id);
        draft.OfferCompensationVersion.Should().Be(3); draft.OfferCompensationConfirmedFingerprint.Should().BeNull();
        (await maker.PutDraftOfferCompensation(id, new(2, OfferCompensationHandoffTests.Dispositions(), true), default)).Result.Should().BeOfType<ConflictObjectResult>();
        (await maker.PutDraftOfferCompensation(id, new(3, OfferCompensationHandoffTests.Dispositions(), true), default)).Result.Should().BeOfType<OkObjectResult>();
        (await EmployeeDraftLifecyclePostgresTests.Employees(db, seed.Actor.TenantId, Guid.NewGuid()).ApproveDraft(id, default)).Result.Should().BeOfType<OkObjectResult>();
        (await db.EmployeeSalaryStructures.SingleAsync(s => s.TenantId == seed.Actor.TenantId)).EffectiveDate.Should().Be(new DateOnly(2027, 2, 1));
    }

    [Fact]
    public async Task LegacyFourFieldSnapshotNeedsExplicitGroupConfirmationThenCannotTransferItsPlacement()
    {
        var seed = await OfferLifecycleTestData.SeedAsync(fixture); await using var db = fixture.CreateDb();
        var offer = await db.OfferLetters.SingleAsync(o => o.Id == seed.OfferId);
        var app = await db.JobApplications.SingleAsync(a => a.Id == seed.ApplicationId);
        offer.CompanyId = null; app.CompanyId = null; offer.OfferedDepartment = ""; offer.OfferedJobTitle = "";
        var snapshot = await OfferComponentOptions.CalculateAsync(db, seed.Actor.TenantId, null, offer.StartDate, null, 1000, 200, 75, 25, default);
        OfferCompensation.ApplySnapshot(offer, snapshot);
        var company = await db.Companies.SingleAsync(c => c.Id == seed.Actor.CompanyId); company.DefaultCurrency = snapshot.Currency;
        var branch = new Branch { TenantId = seed.Actor.TenantId, CompanyId = company.Id, Code = "LEGACY", NameEn = "Explicit legacy placement" };
        db.Branches.Add(branch); await db.SaveChangesAsync();
        var accepted = await new RecruitmentService(db).AcceptOfferAsync(seed.Actor.TenantId, seed.OfferId, seed.Actor.UserId, "Legacy recruiter");
        var id = accepted.OnboardingDraftId!.Value; await DraftStatutoryIdentity.ApplyAsync(db, id, "SA");
        var maker = EmployeeDraftLifecyclePostgresTests.Employees(db, seed.Actor.TenantId, seed.Actor.UserId);
        var rows = snapshot.Components.Select(r => new OfferCompensationDisposition(r.Code, "Payroll", r.StructureField)).ToArray();
        var safeReviewer = EmployeeDraftLifecyclePostgresTests.Employees(db, seed.Actor.TenantId, Guid.NewGuid(), role: "Checker");
        var review = (EmployeeDraftReviewDto)((OkObjectResult)(await safeReviewer.GetDraft(id, default)).Result!).Value!;
        review.Summary.OfferPlacementProvenance.Should().Be("ExplicitLegacyPlacementRequired");
        review.Summary.OfferCompensationGuidance.Should().Contain("group-level");
        review.Draft.Salary.Should().BeNull();
        (await maker.PutDraftOfferCompensation(id, new(1, rows, true), default)).Result.Should().BeOfType<UnprocessableEntityObjectResult>();
        var request = JsonSerializer.Deserialize<EmployeeDraftRequest>("{}", OfferCompensation.Json)! with { Branch = branch.NameEn };
        (await maker.UpdateDraft(id, request, default)).Result.Should().BeOfType<OkObjectResult>();
        var companyMaker = EmployeeDraftLifecyclePostgresTests.Employees(db, seed.Actor.TenantId, seed.Actor.UserId, companies: [company.Id]);
        (await companyMaker.PutDraftOfferCompensation(id, new(2, rows, true), default)).Result.Should().BeOfType<ForbidResult>();
        db.ChangeTracker.Clear(); company = await db.Companies.SingleAsync(c => c.Id == company.Id); company.DefaultCurrency = snapshot.Currency == "USD" ? "SAR" : "USD"; await db.SaveChangesAsync();
        (await maker.PutDraftOfferCompensation(id, new(2, rows, true), default)).Result.Should().BeOfType<UnprocessableEntityObjectResult>();
        db.ChangeTracker.Clear(); company = await db.Companies.SingleAsync(c => c.Id == company.Id); company.DefaultCurrency = snapshot.Currency; await db.SaveChangesAsync();
        var confirmed = await maker.PutDraftOfferCompensation(id, new(2, rows, true), default);
        confirmed.Result.Should().BeOfType<OkObjectResult>(JsonSerializer.Serialize((confirmed.Result as ObjectResult)?.Value));
        var source = await db.EmployeeDrafts.AsNoTracking().SingleAsync(d => d.Id == id);
        source.AcceptedOfferCompanyId.Should().BeNull(); source.AcceptedOfferCompensationJson.Should().Be(offer.CompensationSnapshotJson);
        source.OfferCompensationPlacementCompanyId.Should().Be(company.Id);
        var confirmedDto = (OfferCompensationHandoffDto)((OkObjectResult)confirmed.Result!).Value!;
        confirmedDto.PlacementProvenance.Should().Be("ConfirmedLegacyPlacement"); confirmedDto.PlacementCompanyId.Should().Be(company.Id);
        var other = new Company { TenantId = seed.Actor.TenantId, DefaultCurrency = snapshot.Currency };
        var otherBranch = new Branch { TenantId = seed.Actor.TenantId, CompanyId = other.Id, Code = "TRANSFER", NameEn = "Other company" };
        db.AddRange(other, otherBranch); await db.SaveChangesAsync();
        (await maker.UpdateDraft(id, request with { Branch = otherBranch.NameEn }, default)).Result.Should().BeOfType<OkObjectResult>();
        (await maker.PutDraftOfferCompensation(id, new(4, rows, true), default)).Result.Should().BeOfType<UnprocessableEntityObjectResult>();
        (await EmployeeDraftLifecyclePostgresTests.Employees(db, seed.Actor.TenantId, Guid.NewGuid()).ApproveDraft(id, default)).Result.Should().BeOfType<UnprocessableEntityObjectResult>();
        (await maker.UpdateDraft(id, request, default)).Result.Should().BeOfType<OkObjectResult>();
        (await maker.PutDraftOfferCompensation(id, new(5, rows, true), default)).Result.Should().BeOfType<OkObjectResult>();
        (await EmployeeDraftLifecyclePostgresTests.Employees(db, seed.Actor.TenantId, Guid.NewGuid()).ApproveDraft(id, default)).Result.Should().BeOfType<OkObjectResult>();
        (await db.Employees.SingleAsync(e => e.TenantId == seed.Actor.TenantId)).CompanyId.Should().Be(company.Id);
        (await db.EmployeeSalaryStructures.SingleAsync(s => s.TenantId == seed.Actor.TenantId)).Currency.Should().Be(snapshot.Currency);
    }
}
