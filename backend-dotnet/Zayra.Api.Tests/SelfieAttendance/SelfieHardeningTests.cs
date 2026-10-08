using Xunit;
using Zayra.Api.Controllers;
using Zayra.Api.Infrastructure.Attendance;

namespace Zayra.Api.Tests.SelfieAttendance;

/// <summary>
/// The hardening follow-up's constants and model facts. They name members the follow-up added, so (unlike
/// <see cref="SelfieHardeningRefusalTests"/>) they do not compile against the #212 head (e835077f).
/// </summary>
public sealed class SelfieHardeningTests
{
    [Fact]
    public void Item1_TheUploadDeadline_Is45Seconds_AndEndsBeforeTheReservationStopsCountingAsInFlight()
    {
        Assert.Equal(TimeSpan.FromSeconds(45), AttendanceEvidenceController.UploadDeadline);
        Assert.True(AttendanceEvidenceController.UploadDeadline < SelfieWaivers.InFlight,
            "a drip-fed body could outlive the window in which its reservation blocks a second upload");
    }

    [Fact]
    public void Item1_AServerFailureIsJudgedAgainstEveryPendingAttemptOfTheLastHour_TheSweepersAge()
    {
        Assert.Equal(TimeSpan.FromHours(1), SelfieWaivers.FailureInFlightLookback);
        Assert.Equal(SelfieEvidenceRetention.AbandonedPending, SelfieWaivers.FailureInFlightLookback);
    }

    [Fact]
    public void Item2_TheUploadDeadline_StaysSafelyBelowTheWithdrawalsInFlightGrace()
    {
        // A withdrawal leaves a Pending row younger than the grace for its upload; an upload can write for at most the
        // deadline after reserving. Deadline < grace means a row past the grace can no longer gain a file.
        Assert.True(AttendanceEvidenceController.UploadDeadline < EssAttendanceVerificationController.InFlightPendingGrace);
        Assert.True(EssAttendanceVerificationController.InFlightPendingGrace - AttendanceEvidenceController.UploadDeadline >= TimeSpan.FromSeconds(60),
            "keep at least a minute of margin (activation, lock waits, clock skew)");
    }
}
