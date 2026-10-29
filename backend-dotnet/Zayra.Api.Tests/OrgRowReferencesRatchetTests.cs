using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Organization;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// RATCHET. "Is this department / grade / cost centre … in use?" is answered from the EF model, not from a hand list: any
/// column named like a reference to one of the six organisation rows is counted. A new table that adds such a column is
/// therefore counted automatically; the only way to leave it out is to list it, with a reason, in
/// <see cref="OrgRowReferences.NotUsage"/>, and a column that cannot be pinned to a tenant fails here until someone decides.
/// </summary>
public class OrgRowReferencesRatchetTests
{
    private static ZayraDbContext Db() => new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    [Fact]
    public void EveryColumnThatCanReferenceAnOrgRow_IsCounted_OrIsListedWithAReason()
    {
        using var db = Db();
        var unpinnable = Enum.GetValues<OrgRowKind>()
            .SelectMany(k => OrgRowReferences.For(db.Model, k).Unpinnable).Distinct()
            .Where(x => !OrgRowReferences.CannotBePinned.ContainsKey(x)).ToList();
        unpinnable.Should().BeEmpty("a column that points at an organisation row but has no TenantId to pin the count to would be silently skipped; "
            + "add a TenantId, or list it in OrgRowReferences.CannotBePinned with the reason");
    }

    [Fact]
    public void TheListedExceptions_StillMatchAColumn()
    {
        using var db = Db();
        var names = db.Model.GetEntityTypes().SelectMany(e => e.GetProperties().Select(p => $"{e.ClrType.Name}.{p.Name}")).ToHashSet();
        OrgRowReferences.NotUsage.Keys.Concat(OrgRowReferences.CannotBePinned.Keys).Where(k => !names.Contains(k)).Should().BeEmpty("a stale exception must be deleted");
        OrgRowReferences.NotUsage.Values.Concat(OrgRowReferences.CannotBePinned.Values).Should().OnlyContain(v => !string.IsNullOrWhiteSpace(v));
    }

    [Theory]
    [InlineData(OrgRowKind.Department, "Employee.DepartmentId")]
    [InlineData(OrgRowKind.Department, "Department.ParentDepartmentId")]
    [InlineData(OrgRowKind.Department, "EmployeeTransferRequest.NewDepartmentId")]
    [InlineData(OrgRowKind.Grade, "GradeEntitlement.GradeId")]
    [InlineData(OrgRowKind.Grade, "Designation.GradeId")]
    [InlineData(OrgRowKind.Designation, "Employee.DesignationId")]
    [InlineData(OrgRowKind.CostCenter, "Employee.CostCenterId")]
    [InlineData(OrgRowKind.CostCenter, "Department.CostCenterId")]
    [InlineData(OrgRowKind.Branch, "Department.BranchId")]
    [InlineData(OrgRowKind.Location, "Employee.WorkLocationId")]
    public void TheKnownReferencesAreDiscovered(OrgRowKind kind, string column)
    {
        using var db = Db();
        OrgRowReferences.For(db.Model, kind).References.Select(r => $"{r.Entity.Name}.{r.Property}").Should().Contain(column);
    }

    [Fact]
    public void PoliciesLearningCompensationAndRecruitmentColumnsAreAllCovered()
    {
        using var db = Db();
        var all = Enum.GetValues<OrgRowKind>().SelectMany(k => OrgRowReferences.For(db.Model, k).References).Select(r => r.Entity.Name).ToHashSet();
        foreach (var entity in new[] { "ApprovalPolicy", "ApprovalWorkflow", "LeavePolicyEligibility", "OvertimePolicy", "BenefitEligibilityRule",
                     "ManpowerRequisition", "JobOpening", "LearningAssignment", "TimesheetEntry", "PayBenchmarkRow", "AttendancePolicy", "EmployeeTransferRequest" })
        {
            var exists = db.Model.GetEntityTypes().Any(e => e.ClrType.Name == entity);
            if (exists) all.Should().Contain(entity);
        }
    }

    [Fact]
    public async Task ARowReferencedOnlyByAPolicyOrAnotherTable_IsTurnedOff_NotDeleted()
    {
        await using var db = Db();
        var tenant = Guid.NewGuid();
        var grade = new Grade { TenantId = tenant, Code = "G1", Name = "G1" };
        db.Add(grade);
        // Whatever tables the model has that reference a grade, one live row in each must keep the grade alive.
        var refs = OrgRowReferences.For(db.Model, OrgRowKind.Grade).References.Where(r => r.Shape == OrgRefShape.Guid && r.Entity != typeof(Employee)).ToList();
        refs.Should().NotBeEmpty();
        var made = 0;
        foreach (var r in refs)
        {
            var row = Activator.CreateInstance(r.Entity)!;
            r.Entity.GetProperty("TenantId")!.SetValue(row, r.Entity.GetProperty("TenantId")!.PropertyType == typeof(Guid) ? tenant : (Guid?)tenant);
            // A table that scopes its rows to a company (a hire template) requires one; any company will do for the reference test.
            if (r.Entity.GetProperty("CompanyId") is { CanWrite: true } company && company.Name != r.Property)
                company.SetValue(row, company.PropertyType == typeof(Guid) ? Guid.NewGuid() : (Guid?)Guid.NewGuid());
            var p = r.Entity.GetProperty(r.Property)!;
            p.SetValue(row, p.PropertyType == typeof(Guid) ? grade.Id : (Guid?)grade.Id);
            db.Add(row); made++;
            await db.SaveChangesAsync();
            var usage = await new OrgRowRemoval(db, null).UsageAsync(OrgRowKind.Grade, tenant, grade.Id, default);
            usage.InUse.Should().BeTrue($"{r.Entity.Name}.{r.Property} holds the grade");
            db.Remove(row); await db.SaveChangesAsync();
        }
        made.Should().BeGreaterThan(3);
    }

    [Fact]
    public async Task AJsonIdListReference_IsCounted_ByExactIdAndIgnoresDeletedRows()
    {
        await using var db = Db();
        var tenant = Guid.NewGuid();
        var grade = new Grade { TenantId = tenant, Code = "G1", Name = "G1" };
        var other = Guid.NewGuid();
        db.Add(grade);
        db.Add(new SalaryStructure { TenantId = tenant, Code = "S1", Name = "S1", EligibleGradeIdsJson = $"[\"{grade.Id}\",\"{other}\"]" });
        db.Add(new SalaryStructure { TenantId = tenant, Code = "S2", Name = "S2", EligibleGradeIdsJson = $"[\"{grade.Id}\"]", IsDeleted = true });
        await db.SaveChangesAsync();
        var usage = await new OrgRowRemoval(db, null).UsageAsync(OrgRowKind.Grade, tenant, grade.Id, default);
        usage.InUse.Should().BeTrue();
        (await new OrgRowRemoval(db, null).UsageAsync(OrgRowKind.Grade, tenant, Guid.NewGuid(), default)).InUse.Should().BeFalse();
    }
}
