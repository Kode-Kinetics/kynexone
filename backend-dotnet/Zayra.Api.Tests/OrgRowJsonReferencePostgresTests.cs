using FluentAssertions;
using Zayra.Api.Application.Organization;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>A json-typed id-list column (SalaryStructure.EligibleGradeIdsJson is `json`) must be countable on real Postgres,
/// where a LIKE over json does not exist (42883).</summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class OrgRowJsonReferencePostgresTests(PostgresFixture fixture)
{
    [Fact]
    public async Task A_json_id_list_reference_is_counted_on_postgres()
    {
        await using var db = fixture.CreateDb();
        var tenant = Guid.NewGuid();
        var grade = new Grade { TenantId = tenant, Code = "G1", Name = "G1" };
        db.Add(grade);
        db.Add(new SalaryStructure { TenantId = tenant, Code = "S1", Name = "S1", EligibleGradeIdsJson = $"[\"{grade.Id}\",\"{Guid.NewGuid()}\"]" });
        db.Add(new SalaryStructure { TenantId = tenant, Code = "S2", Name = "S2", EligibleGradeIdsJson = $"[\"{grade.Id}\"]" });
        await db.SaveChangesAsync();
        var removal = new OrgRowRemoval(db, null);
        var usage = await removal.UsageAsync(OrgRowKind.Grade, tenant, grade.Id, default);
        usage.InUse.Should().BeTrue();
        (await removal.UsageAsync(OrgRowKind.Grade, tenant, Guid.NewGuid(), default)).InUse.Should().BeFalse();
    }
}
