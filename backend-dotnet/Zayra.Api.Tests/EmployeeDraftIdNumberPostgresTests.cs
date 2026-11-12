using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Employees;
using Zayra.Api.Controllers;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// A hire from an accepted offer is a draft, and a Saudi company's activation policy requires the government ID number
/// (Employee.IdNumber). The draft had no such field: the review said "add the government id number to the draft" and nothing
/// could. The draft now carries it, validated by the same identity rules an employee write uses, copied onto the employee
/// at activation, masked for readers without sensitive access, and kept per tenant.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class EmployeeDraftIdNumberPostgresTests(PostgresFixture fixture)
{
    private static readonly string NationalId = TestIds.ValidNationalId(41_000_001);
    private static readonly string Iqama = TestIds.ValidIqama(42_000_002);

    private static EmployeeDraftRequest Request() => new(
        null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null,
        null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null,
        null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null);

    private static T Ok<T>(ActionResult<T> result) =>
        (T)result.Result.Should().BeOfType<OkObjectResult>().Subject.Value!;

    [Fact]
    public async Task ASaudiDraft_WithoutAnIdNumber_IsBlockedOnIdNumber_AndApprovalRefuses()
    {
        var seeded = await SeedAsync();
        EmployeeDraftReviewDto review;
        await using (var db = fixture.CreateDb())
            review = Ok(await EmployeeDraftLifecyclePostgresTests.Employees(db, seeded.TenantId, seeded.Checker).GetDraft(seeded.DraftId, CancellationToken.None));
        review.ActivationCheck!.CanActivate.Should().BeFalse();
        review.ActivationCheck.Problems.Select(p => p.Key).Should().Contain("IdNumber");
        review.ActivationCheck.Problems.Select(p => p.Key).Should().NotContain(k => k != "IdNumber",
            string.Join("; ", review.ActivationCheck.Problems.Select(p => p.Reason)));

        await using var approve = fixture.CreateDb();
        (await EmployeeDraftLifecyclePostgresTests.Employees(approve, seeded.TenantId, seeded.Checker).ApproveDraft(seeded.DraftId, CancellationToken.None))
            .Result.Should().BeOfType<UnprocessableEntityObjectResult>();
    }

    [Fact]
    public async Task TheDraftAcceptsASaudiNationalId_ThenTheReviewClears_AndADifferentPersonActivatesIt_WithTheIdOnTheEmployee()
    {
        var seeded = await SeedAsync();
        await using (var db = fixture.CreateDb())
        {
            var saved = Ok(await EmployeeDraftLifecyclePostgresTests.Employees(db, seeded.TenantId, seeded.Maker)
                .UpdateDraft(seeded.DraftId, Request() with { IdNumber = $" {NationalId} " }, CancellationToken.None));
            saved.IdNumber.Should().Be(NationalId, "stored trimmed, as the employee form stores it");
        }
        await using (var db = fixture.CreateDb())
        {
            var review = Ok(await EmployeeDraftLifecyclePostgresTests.Employees(db, seeded.TenantId, seeded.Checker).GetDraft(seeded.DraftId, CancellationToken.None));
            review.Draft.IdNumber.Should().Be(NationalId);
            review.ActivationCheck!.CanActivate.Should().BeTrue(string.Join("; ", review.ActivationCheck.Problems.Select(p => p.Reason)));
        }
        await using (var db = fixture.CreateDb())
            (await EmployeeDraftLifecyclePostgresTests.Employees(db, seeded.TenantId, seeded.Checker).ApproveDraft(seeded.DraftId, CancellationToken.None))
                .Result.Should().BeOfType<OkObjectResult>();

        await using var read = fixture.CreateDb();
        // IgnoreQueryFilters is intentional: the test reads the one employee the activation created in its own tenant.
        var employee = await read.Employees.IgnoreQueryFilters().AsNoTracking().SingleAsync(e => e.TenantId == seeded.TenantId);
        employee.IdNumber.Should().Be(NationalId);
        employee.IdType.Should().Be("NationalId", "the wage file and Qiwa read the ID through its type");
    }

    [Fact]
    public async Task ANonSaudiDraft_TakesAnIqama_AndKeepsTheSaudiOnlyIdNumberOutOfItsRequirements()
    {
        var seeded = await SeedAsync(nationality: "Pakistani");
        await using (var db = fixture.CreateDb())
            (await EmployeeDraftLifecyclePostgresTests.Employees(db, seeded.TenantId, seeded.Maker)
                .UpdateDraft(seeded.DraftId, Request() with { IqamaNumber = Iqama }, CancellationToken.None))
                .Result.Should().BeOfType<OkObjectResult>();
        await using (var db = fixture.CreateDb())
        {
            var review = Ok(await EmployeeDraftLifecyclePostgresTests.Employees(db, seeded.TenantId, seeded.Checker).GetDraft(seeded.DraftId, CancellationToken.None));
            review.ActivationCheck!.Problems.Select(p => p.Key).Should().NotContain("IdNumber").And.NotContain("IqamaNumber");
        }
    }

    [Theory]
    [InlineData("123", "too short")]
    [InlineData("2000000000", "an Iqama-prefixed number for a Saudi national")]
    [InlineData("1000000001", "a wrong check digit")]
    [InlineData("abcdefghij", "not digits")]
    public async Task AnInvalidIdNumber_IsRefusedWithTheEmployeeRulesCode_AndNothingIsSaved(string value, string why)
    {
        var seeded = await SeedAsync();
        await using (var db = fixture.CreateDb())
        {
            var result = await EmployeeDraftLifecyclePostgresTests.Employees(db, seeded.TenantId, seeded.Maker)
                .UpdateDraft(seeded.DraftId, Request() with { IdNumber = value }, CancellationToken.None);
            var refused = result.Result.Should().BeOfType<UnprocessableEntityObjectResult>(why).Subject;
            JsonSerializer.Serialize(refused.Value).Should().Contain("idNumber").And.Contain("id_");
        }
        await using var read = fixture.CreateDb();
        (await read.EmployeeDrafts.AsNoTracking().SingleAsync(d => d.Id == seeded.DraftId)).IdNumber.Should().BeEmpty();
        (await read.AuditLogs.AsNoTracking().AnyAsync(a => a.Action == "employee.draft_updated" && a.EntityId == seeded.DraftId.ToString()))
            .Should().BeFalse("a refused edit must not make its editor a maker");
    }

    [Fact]
    public async Task AnEditorOfTheIdNumber_BecomesAMaker_AndCannotActivateTheirOwnHire()
    {
        var seeded = await SeedAsync();
        await using (var db = fixture.CreateDb())
            (await EmployeeDraftLifecyclePostgresTests.Employees(db, seeded.TenantId, seeded.Checker)
                .UpdateDraft(seeded.DraftId, Request() with { IdNumber = NationalId }, CancellationToken.None))
                .Result.Should().BeOfType<OkObjectResult>();
        await using var approve = fixture.CreateDb();
        (await EmployeeDraftLifecyclePostgresTests.Employees(approve, seeded.TenantId, seeded.Checker).ApproveDraft(seeded.DraftId, CancellationToken.None))
            .Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(403);
    }

    [Fact]
    public async Task AnotherTenant_CannotWriteOrReadTheDraftsIdNumber()
    {
        var seeded = await SeedAsync();
        var elsewhere = await SeedAsync();
        await using (var db = fixture.CreateDb())
            (await EmployeeDraftLifecyclePostgresTests.Employees(db, elsewhere.TenantId, elsewhere.Maker)
                .UpdateDraft(seeded.DraftId, Request() with { IdNumber = NationalId }, CancellationToken.None))
                .Result.Should().BeOfType<NotFoundResult>();
        await using var read = fixture.CreateDb();
        (await read.EmployeeDrafts.AsNoTracking().SingleAsync(d => d.Id == seeded.DraftId)).IdNumber.Should().BeEmpty();
    }

    [Fact]
    public async Task TheIdNumber_IsMaskedInTheReviewForAReaderWithoutSensitiveAccess()
    {
        var seeded = await SeedAsync();
        await using (var db = fixture.CreateDb())
            (await EmployeeDraftLifecyclePostgresTests.Employees(db, seeded.TenantId, seeded.Maker)
                .UpdateDraft(seeded.DraftId, Request() with { IdNumber = NationalId }, CancellationToken.None))
                .Result.Should().BeOfType<OkObjectResult>();
        await using (var db = fixture.CreateDb())
        {
            var review = Ok(await EmployeeDraftLifecyclePostgresTests.Employees(db, seeded.TenantId, seeded.Checker, role: "Recruiter")
                .GetDraft(seeded.DraftId, CancellationToken.None));
            review.Draft.IdNumber.Should().BeEmpty();
        }
    }

    private async Task<Seeded> SeedAsync(string nationality = "Saudi")
    {
        await using var db = fixture.CreateDb();
        var tenantId = Guid.NewGuid();
        var maker = Guid.NewGuid();
        var company = new Company
        {
            TenantId = tenantId, LegalNameEn = "Riyadh Co", RegistrationNumber = $"RC-{Guid.NewGuid():N}",
            CountryCode = "SA", Jurisdiction = "SA", DefaultCurrency = "SAR",
        };
        var branch = new Branch { TenantId = tenantId, CompanyId = company.Id, Code = "RUH", NameEn = "Riyadh", CountryCode = "SA", IsActive = true };
        var draft = new EmployeeDraft
        {
            TenantId = tenantId, CreatedByUserId = maker, Status = "PendingHrApproval", CurrentStep = "HrApproval",
            EnglishName = "Accepted Hire", PersonalEmail = $"hire-{Guid.NewGuid():N}@example.test",
            Branch = branch.NameEn, JoiningDate = DateTime.UtcNow.Date, SubmittedAtUtc = DateTime.UtcNow.AddHours(-1),
            Nationality = nationality,
        };
        db.AddRange(
            new Tenant { Id = tenantId, Name = "Riyadh Tenant", Slug = $"riyadh-{tenantId:N}" },
            company, branch,
            new Role { TenantId = tenantId, Name = "Employee", NormalizedName = "EMPLOYEE", IsActive = true },
            draft);
        await db.SaveChangesAsync();
        return new Seeded(tenantId, draft.Id, maker, Guid.NewGuid());
    }

    private sealed record Seeded(Guid TenantId, Guid DraftId, Guid Maker, Guid Checker);
}
