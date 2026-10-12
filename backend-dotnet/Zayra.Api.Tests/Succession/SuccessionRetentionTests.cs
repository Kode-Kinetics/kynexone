using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Infrastructure.Succession;
using Zayra.Api.Models;
using static Zayra.Api.Tests.Succession.SuccessionTestWorld;

namespace Zayra.Api.Tests.Succession;

/// <summary>
/// Retention (SUC_DESIGN §0): three years after employment ends, a person's succession free text is redacted and their
/// name in every slate snapshot pseudonymised — in place, through the immutability triggers, keeping each snapshot's
/// hash and recording anonymised_at. Someone who left two years ago is untouched; a second run finds nothing.
/// </summary>
[Collection("Integration")]
[Trait("Category", "Integration")]
public sealed class SuccessionRetentionTests(PostgresFixture fx)
{
    [Fact]
    public async Task ThreeYearsAfterLeaving_TextIsRedacted_NamesArePseudonymised_AndHashesAreKept()
    {
        await using var w = await CreateAsync(fx);
        var cfo = await w.DesignateCfoAsync();
        var cara = await w.CalibratedCandidateAsync(cfo.Id, w.Cara, 3, ReadinessLevels.ReadyNow);
        var cody = await w.CalibratedCandidateAsync(cfo.Id, w.Cody, 2, ReadinessLevels.OneToTwoYears);
        var v1 = await w.SubmitSlateAsync((await w.DraftSlateAsync(cfo.Id, (cara.Id, 1), (cody.Id, 2))).Id);
        await w.DecideAsync(w.HrDirectorP(), v1.ApprovalRequestId!.Value, "Approve", null);
        var originalHash = (await w.SlateRowAsync(v1.Id)).SnapshotSha256;

        var now = DateTime.UtcNow;
        await using (var db = w.Db())
        {
            foreach (var (id, years) in new[] { (w.Cara, 4), (w.Cody, 2) })
            {
                var e = await db.Employees.IgnoreQueryFilters().SingleAsync(x => x.Id == id);
                e.Status = EmployeeStatuses.Offboarded;
                db.EmployeeOffboardings.Add(new EmployeeOffboarding
                {
                    TenantId = w.TenantA, EmployeeId = id, EmployeeName = e.FullName, LastWorkingDay = DateOnly.FromDateTime(now.AddYears(-years)),
                });
            }
            await db.SaveChangesAsync();
        }

        var job = new SuccessionRetentionJob();
        await using (var db = w.Db())
        {
            var due = await job.DueAsync(db, w.TenantA, now, 100, default);
            due.Select(d => d.EmployeeId).Should().Equal(w.Cara);
            due.Single().Basis.Should().Be("offboarding last working day");
            await job.AnonymiseAsync(db, w.TenantA, w.Cara, now, default);
            await db.SaveChangesAsync();
        }

        await using (var db = w.Db())
        {
            var slate = await db.SuccessionSlateVersions.IgnoreQueryFilters().SingleAsync(s => s.Id == v1.Id);
            slate.SnapshotSha256.Should().Be(originalHash, "the hash remains the witness of what was approved");
            slate.AnonymisedAtUtc.Should().NotBeNull();
            var snapshot = SlateSnapshot.Parse(slate.SnapshotJson)!;
            var anonymised = snapshot.Candidates.Single(c => c.EmployeeId == w.Cara);
            anonymised.NameEn.Should().StartWith("Former employee ");
            anonymised.NameAr.Should().StartWith("Former employee ");
            anonymised.Rationale.Should().Be(SuccessionRetentionJob.Redacted);
            snapshot.Candidates.Single(c => c.EmployeeId == w.Cody).NameEn.Should().Be("Cody Candidate", "Cody left two years ago");
            slate.SnapshotJson.Should().NotContain("Cara Candidate").And.NotContain("كارا");

            var assessment = await db.TalentAssessments.IgnoreQueryFilters().SingleAsync(a => a.EmployeeId == w.Cara);
            assessment.PotentialEvidence.Should().Be(SuccessionRetentionJob.Redacted);
            assessment.Status.Should().Be(TalentAssessmentStatuses.Calibrated);
            assessment.AnonymisedAtUtc.Should().NotBeNull();
            var nomination = await db.SuccessionNominations.IgnoreQueryFilters().SingleAsync(n => n.Id == cara.Id);
            nomination.Evidence.Should().Be(SuccessionRetentionJob.Redacted);
            nomination.AnonymisedAtUtc.Should().NotBeNull();

            (await job.DueAsync(db, w.TenantA, now, 100, default)).Should().BeEmpty("a person is anonymised once");
        }
    }
}
