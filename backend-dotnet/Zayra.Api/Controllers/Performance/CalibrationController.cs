using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Performance;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers.Performance;

[ApiController]
[Route("api/performance/calibration")]
[Authorize(Roles = "Admin,HR Manager")]
public class CalibrationController : ControllerBase
{
    private readonly ZayraDbContext _db;
    private readonly IPerformanceService _svc;

    public CalibrationController(ZayraDbContext db, IPerformanceService svc)
    { _db = db; _svc = svc; }

    [HttpGet("{cycleId:guid}")]
    public async Task<IActionResult> GetBoard(
        Guid cycleId,
        [FromQuery] string? department,
        CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;

        var query = _db.AppraisalReviews.Where(r => r.TenantId == tenantId && r.CycleId == cycleId);
        if (!string.IsNullOrWhiteSpace(department)) query = query.Where(r => r.DepartmentName == department);

        var reviews = await query
            .OrderByDescending(r => r.FinalScore)
            .ToListAsync(ct);

        var calibrations = await _db.AppraisalCalibrations
            .Where(c => c.TenantId == tenantId && c.CycleId == cycleId)
            .ToListAsync(ct);
        var calibMap = calibrations.ToDictionary(c => c.ReviewId);

        // Distribution bands
        var distribution = new Dictionary<string, int>
        {
            ["Outstanding"]           = reviews.Count(r => r.FinalScore >= 90),
            ["Exceeds Expectations"]  = reviews.Count(r => r.FinalScore >= 75 && r.FinalScore < 90),
            ["Meets Expectations"]    = reviews.Count(r => r.FinalScore >= 60 && r.FinalScore < 75),
            ["Developing"]            = reviews.Count(r => r.FinalScore >= 45 && r.FinalScore < 60),
            ["Unsatisfactory"]        = reviews.Count(r => r.FinalScore < 45),
        };

        // Detect manager bias: managers with avg > 85 or < 50
        var managerStats = reviews
            .Where(r => r.ReviewerManagerId.HasValue)
            .GroupBy(r => new { r.ReviewerManagerId, r.ReviewerManagerName })
            .Select(g => new
            {
                ManagerId   = g.Key.ReviewerManagerId,
                ManagerName = g.Key.ReviewerManagerName,
                AvgScore    = Math.Round(g.Average(r => r.FinalScore), 1),
                Count       = g.Count(),
                PossibleBias = g.Average(r => r.FinalScore) > 85 || g.Average(r => r.FinalScore) < 50,
            })
            .ToList();

        var board = reviews.Select(r =>
        {
            calibMap.TryGetValue(r.Id, out var cal);
            return new
            {
                r.Id, r.EmployeeId, r.EmployeeName, r.DepartmentName, r.DesignationTitle,
                r.FinalScore, r.FinalRating, r.Status, r.ManagerNotes, r.CalibrationAdjustment,
                CalibrationRecord = cal,
            };
        });

        return Ok(new { reviews = board, distribution, managerStats, totalReviews = reviews.Count });
    }

    [HttpPost("{cycleId:guid}/adjust")]
    public async Task<IActionResult> AdjustScore(
        Guid cycleId, [FromBody] CalibrationAdjustRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var userId   = this.GetUserId();
        var userName = HttpContext.User.FindFirst("FullName")?.Value ?? "HR";

        var cycle = await _db.PerformanceCycles.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == cycleId && c.TenantId == tenantId, ct);
        if (cycle is null) return NotFound(new { message = "Performance cycle not found." });
        if (cycle.Status != "Calibration")
            return Conflict(new { error = "cycle_not_in_calibration", message = $"Calibration adjustments require a cycle in Calibration status (current: {cycle.Status})." });
        if (req.Adjustment is < -100 or > 100)
            return BadRequest(new { error = "invalid_adjustment", message = "Calibration adjustment must be between -100 and 100 points." });

        var review = await _db.AppraisalReviews
            .FirstOrDefaultAsync(r => r.Id == req.ReviewId && r.TenantId == tenantId && r.CycleId == cycleId, ct);
        if (review is null) return NotFound();
        if (review.Status != "ManagerReviewComplete")
            return Conflict(new { error = "review_not_calibratable", message = $"Manager review must be complete before calibration (current: {review.Status})." });
        if (string.IsNullOrWhiteSpace(req.Reason))
            return BadRequest(new { message = "A reason is mandatory for all calibration adjustments." });

        // Program.cs registers the DbContext with EnableRetryOnFailure, so the ambient execution
        // strategy is NpgsqlRetryingExecutionStrategy, and it refuses a user-initiated
        // BeginTransactionAsync unless the whole unit runs inside
        // Database.CreateExecutionStrategy().ExecuteAsync(...). The bare transaction that stood
        // here threw InvalidOperationException before doing any work and the generic exception
        // handler turned it into HTTP 400, so EVERY calibration adjustment failed — 100% of the
        // time — on the /performance board.
        AppraisalReview subject = review;
        var originalScore  = subject.FinalScore;
        var originalRating = subject.FinalRating;
        var newScore       = subject.FinalScore;
        var newRating      = subject.FinalRating;

        // The calibration unit itself, behaviourally unchanged. It runs either directly
        // (non-relational providers have no transactions) or inside the execution-strategy
        // delegate below, so it has to be safe to run more than once against freshly loaded
        // state. Both _svc calls SaveChanges through this same DbContext, so they carry the same
        // retry hazard as the writes made here.
        async Task AdjustAsync(AppraisalReview target)
        {
            originalScore  = target.FinalScore;
            originalRating = target.FinalRating;

            target.CalibrationAdjustment = req.Adjustment;
            target.CalibrationNotes      = req.Reason;
            target.UpdatedAtUtc          = DateTime.UtcNow;

            newScore = await _svc.CalculateAndSaveFinalScoreAsync(tenantId, req.ReviewId, ct);

            _db.AppraisalCalibrations.Add(new AppraisalCalibration
            {
                TenantId           = tenantId,
                ReviewId           = req.ReviewId,
                CycleId            = cycleId,
                EmployeeName       = target.EmployeeName,
                DepartmentName     = target.DepartmentName,
                OriginalScore      = originalScore,
                AdjustedScore      = newScore,
                AdjustmentReason   = req.Reason,
                OriginalRating     = originalRating,
                AdjustedRating     = target.FinalRating,
                CalibratedByUserId = userId,
                CalibratedByName   = userName,
            });

            await _svc.LogAuditAsync(tenantId, "AppraisalReview", req.ReviewId.ToString(),
                "CalibrationAdjustment",
                $"Score:{originalScore},Rating:{originalRating}",
                $"Score:{newScore},Rating:{target.FinalRating}",
                req.Reason, userId, userName, ct);

            await _db.SaveChangesAsync(ct);
            newRating = target.FinalRating;
        }

        if (!_db.Database.IsRelational())
        {
            // Preserved branch: the in-memory provider the fast unit tests use has neither
            // transactions nor an execution strategy to satisfy.
            await AdjustAsync(subject);
            return Ok(new { originalScore, newScore, newRating });
        }

        var strategy = _db.Database.CreateExecutionStrategy();
        var attempt = 0;
        IActionResult? raced = null;
        await strategy.ExecuteAsync(async () =>
        {
            var target = subject;
            raced = null;
            if (attempt++ > 0)
            {
                // ExecuteAsync may re-run this delegate. A retry must not inherit the change
                // tracker a failed attempt left behind: its pending AppraisalCalibration and
                // PerformanceAuditLog rows would be inserted twice, and — worse — anything whose
                // SaveChanges succeeded before the COMMIT was lost to a transient failure is
                // tracked as Unchanged, so a naive retry would silently rewrite nothing at all.
                // The first attempt keeps the already-loaded review, so the success path is
                // unchanged; any retry restarts from persisted state.
                _db.ChangeTracker.Clear();
                var reloaded = await _db.AppraisalReviews
                    .FirstOrDefaultAsync(r => r.Id == req.ReviewId && r.TenantId == tenantId && r.CycleId == cycleId, ct);
                if (reloaded is null || reloaded.Status != "ManagerReviewComplete")
                {
                    // The review stopped being calibratable between attempts. That is a genuine
                    // race, not transaction plumbing, so report it as the conflict it is.
                    raced = Conflict(new
                    {
                        error = "review_not_calibratable",
                        message = "The review changed while the calibration was being retried; reload the board and try again."
                    });
                    return;
                }

                // Lost-COMMIT case: SaveChanges succeeded and only the commit acknowledgement was
                // lost, so this adjustment is already durable. Recomputing the score is
                // idempotent, but appending a second calibration history row is not — report the
                // record that persisted instead of duplicating it.
                var recorded = await _db.AppraisalCalibrations.AsNoTracking()
                    .FirstOrDefaultAsync(c => c.TenantId == tenantId
                        && c.ReviewId == req.ReviewId
                        && c.CycleId == cycleId
                        && c.CalibratedByUserId == userId
                        && c.AdjustmentReason == req.Reason, ct);
                if (recorded is not null && reloaded.CalibrationAdjustment == req.Adjustment)
                {
                    originalScore  = recorded.OriginalScore;
                    originalRating = recorded.OriginalRating;
                    newScore       = recorded.AdjustedScore;
                    newRating      = reloaded.FinalRating;
                    return;
                }

                target = reloaded;
            }

            var transaction = await _db.Database.BeginTransactionAsync(ct);
            try
            {
                await AdjustAsync(target);
                await transaction.CommitAsync(ct);
            }
            catch
            {
                // Never let a failing rollback mask the original error: the strategy has to see
                // the real exception to classify it as transient. Dispose still releases.
                try { await transaction.RollbackAsync(ct); } catch { /* connection already gone */ }
                throw;
            }
            finally
            {
                await transaction.DisposeAsync();
            }
        });

        return raced ?? Ok(new { originalScore, newScore, newRating });
    }
}

public record CalibrationAdjustRequest(Guid ReviewId, decimal Adjustment, string Reason);
