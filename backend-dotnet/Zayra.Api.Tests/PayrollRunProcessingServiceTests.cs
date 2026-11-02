using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Jobs;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// The request side of background payroll processing: <c>POST runs/{id}/process</c> queues (202) after the same
/// synchronous refusals as before, a double click returns the same job, and <c>GET runs/{id}/processing</c>
/// reports it — scoped to the caller's tenant, with a message that is safe to show. No Docker: SQLite.
/// </summary>
public sealed class PayrollRunProcessingServiceTests
{
    private static readonly Guid User = Guid.NewGuid();

    private sealed class World : IDisposable
    {
        public required SqliteConnection Conn { get; init; }
        public required ZayraDbContext Db { get; init; }
        public required Guid Tenant { get; init; }
        public required Guid CompanyId { get; init; }
        public required PayrollRun Run { get; init; }
        public PayrollRunProcessingService Service => new(Db, new BackgroundJobStore(Db, Registry));
        public static BackgroundJobTypeRegistry Registry => new([PayrollRunProcessJobHandler.Descriptor]);
        public PayrollProcessCaller Caller(Guid? tenant = null, bool group = true, params Guid[] companies) =>
            new(tenant ?? Tenant, User, "10.0.0.9", group, companies);
        public void Dispose() { Db.Dispose(); Conn.Dispose(); }
    }

    private static async Task<World> SeedAsync(string status = "Draft")
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        var tenant = Guid.NewGuid();
        var company = new Company { TenantId = tenant, LegalNameEn = "Svc Co", CountryCode = "SAU", Jurisdiction = "KSA-mainland", IsActive = true };
        var run = new PayrollRun { TenantId = tenant, CompanyId = company.Id, Year = 2026, Month = 6, Status = status };
        db.Companies.Add(company);
        db.PayrollRuns.Add(run);
        await db.SaveChangesAsync();
        return new World { Conn = conn, Db = db, Tenant = tenant, CompanyId = company.Id, Run = run };
    }

    // ── Enqueue ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Queueing_a_draft_run_answers_with_a_queued_job_and_stores_who_asked()
    {
        using var w = await SeedAsync();

        var result = await w.Service.EnqueueAsync(w.Run.Id, w.Caller(), default);

        result.Refusal.Should().BeNull();
        result.Accepted!.RunId.Should().Be(w.Run.Id);
        result.Accepted.Status.Should().Be("Queued");
        var job = await w.Db.BackgroundJobs.AsNoTracking().SingleAsync();
        job.Id.Should().Be(result.Accepted.JobId);
        job.JobType.Should().Be("payroll.run.process");
        job.TenantId.Should().Be(w.Tenant);
        job.CreatedByUserId.Should().Be(User);
        var payload = JsonSerializer.Deserialize<PayrollProcessJobPayload>(job.PayloadJson)!;
        payload.Should().BeEquivalentTo(new PayrollProcessJobPayload(w.Run.Id, w.Tenant, w.CompanyId, User, "10.0.0.9", true, []));
    }

    [Fact]
    public async Task A_double_post_returns_the_same_job_and_never_creates_a_second()
    {
        using var w = await SeedAsync();

        var first = await w.Service.EnqueueAsync(w.Run.Id, w.Caller(), default);
        var second = await w.Service.EnqueueAsync(w.Run.Id, w.Caller(), default);

        second.Accepted!.JobId.Should().Be(first.Accepted!.JobId);
        (await w.Db.BackgroundJobs.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_post_while_the_job_is_running_returns_that_running_job()
    {
        using var w = await SeedAsync();
        var first = await w.Service.EnqueueAsync(w.Run.Id, w.Caller(), default);
        await w.Db.BackgroundJobs.ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, BackgroundJobStatuses.Running));

        var second = await w.Service.EnqueueAsync(w.Run.Id, w.Caller(), default);

        second.Accepted!.JobId.Should().Be(first.Accepted!.JobId);
        second.Accepted.Status.Should().Be("Running");
        (await w.Db.BackgroundJobs.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task After_a_failed_job_a_new_post_queues_a_new_job()
    {
        using var w = await SeedAsync();
        var first = await w.Service.EnqueueAsync(w.Run.Id, w.Caller(), default);
        await w.Db.BackgroundJobs.ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, BackgroundJobStatuses.Failed));

        var retry = await w.Service.EnqueueAsync(w.Run.Id, w.Caller(), default);

        retry.Accepted!.JobId.Should().NotBe(first.Accepted!.JobId, "a retry after a failure is safe and is a new job");
        (await w.Service.GetStatusAsync(w.Run.Id, w.Tenant, default))!.JobId.Should().Be(retry.Accepted.JobId, "status follows the latest job");
    }

    [Theory]
    [InlineData("Processed")]
    [InlineData("PendingFinanceReview")]
    [InlineData("Locked")]
    [InlineData("Approved")]
    [InlineData("Paid")]
    [InlineData("Voided")]
    public async Task A_run_in_a_completed_state_is_refused_400_before_anything_is_queued(string status)
    {
        using var w = await SeedAsync(status);

        var result = await w.Service.EnqueueAsync(w.Run.Id, w.Caller(), default);

        result.Accepted.Should().BeNull();
        result.Refusal!.StatusCode.Should().Be(400);
        (await w.Db.BackgroundJobs.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task A_run_of_another_tenant_or_an_unknown_run_is_a_404_and_queues_nothing()
    {
        using var w = await SeedAsync();

        (await w.Service.EnqueueAsync(w.Run.Id, w.Caller(tenant: Guid.NewGuid()), default)).Refusal!.StatusCode.Should().Be(404);
        (await w.Service.EnqueueAsync(Guid.NewGuid(), w.Caller(), default)).Refusal!.StatusCode.Should().Be(404);
        (await w.Db.BackgroundJobs.AnyAsync()).Should().BeFalse();
    }

    // ── Status ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_run_that_was_never_queued_reads_as_idle()
    {
        using var w = await SeedAsync();

        var status = await w.Service.GetStatusAsync(w.Run.Id, w.Tenant, default);

        status.Should().Be(new PayrollProcessingStatus(w.Run.Id, null, "Idle", 0, 0, null, null, null, null, null));
    }

    [Fact]
    public async Task Status_follows_the_job_and_reports_progress()
    {
        using var w = await SeedAsync();
        var accepted = (await w.Service.EnqueueAsync(w.Run.Id, w.Caller(), default)).Accepted!;

        (await w.Service.GetStatusAsync(w.Run.Id, w.Tenant, default))!.Status.Should().Be("Queued");

        var started = DateTime.UtcNow;
        await w.Db.BackgroundJobs.ExecuteUpdateAsync(s => s
            .SetProperty(j => j.Status, BackgroundJobStatuses.Running).SetProperty(j => j.StartedAtUtc, started)
            .SetProperty(j => j.ProgressTotal, 80).SetProperty(j => j.ProgressCompleted, 25));
        var running = (await w.Service.GetStatusAsync(w.Run.Id, w.Tenant, default))!;
        running.JobId.Should().Be(accepted.JobId);
        running.Status.Should().Be("Running");
        (running.ProcessedEmployees, running.TotalEmployees).Should().Be((25, 80));
        running.StartedAtUtc.Should().NotBeNull();
        running.FinishedAtUtc.Should().BeNull();
        running.ErrorCode.Should().BeNull();

        var finished = DateTime.UtcNow;
        await w.Db.BackgroundJobs.ExecuteUpdateAsync(s => s
            .SetProperty(j => j.Status, BackgroundJobStatuses.Succeeded).SetProperty(j => j.CompletedAtUtc, finished));
        var done = (await w.Service.GetStatusAsync(w.Run.Id, w.Tenant, default))!;
        done.Status.Should().Be("Succeeded");
        (done.ProcessedEmployees, done.TotalEmployees).Should().Be((80, 80), "a finished job reads as complete");
        done.FinishedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task Another_tenants_run_and_an_unknown_run_have_no_status()
    {
        using var w = await SeedAsync();
        await w.Service.EnqueueAsync(w.Run.Id, w.Caller(), default);

        (await w.Service.GetStatusAsync(w.Run.Id, Guid.NewGuid(), default)).Should().BeNull("another tenant must not learn the run exists");
        (await w.Service.GetStatusAsync(Guid.NewGuid(), w.Tenant, default)).Should().BeNull();
    }

    [Fact]
    public void A_failed_job_shows_its_code_and_message_and_the_employees_it_names()
    {
        var failure = new PayrollProcessFailure("negative_net_unsupported", "2 employee(s) on this supplemental run have deductions exceeding their earnings.",
            [new FailedEmployee(7, "E007", "negative_net_unsupported")]);
        var job = new BackgroundJob { Status = BackgroundJobStatuses.Failed, LastError = failure.Encode(), ProgressTotal = 10, ProgressCompleted = 4 };

        var status = PayrollRunProcessingService.Describe(Guid.NewGuid(), job);

        status.Status.Should().Be("Failed");
        status.ErrorCode.Should().Be("negative_net_unsupported");
        status.ErrorMessage.Should().Be(failure.Message);
        status.FailedEmployees.Should().Equal(new FailedEmployee(7, "E007", "negative_net_unsupported"));
        status.ProcessedEmployees.Should().Be(4);
    }

    [Theory]
    [InlineData("InvalidOperationException: relation \"payroll_slips\" does not exist\n   at Npgsql.NpgsqlConnector.ReadMessage()")]
    [InlineData("Failed after 3 attempt(s). Last error: PostgresException: 23505: duplicate key value violates unique constraint \"ux_payroll_slips\"")]
    [InlineData("Lease expired after 3 attempt(s) without the job completing; the worker most likely crashed while running it.")]
    [InlineData("payroll.failure:{not json")]
    [InlineData(null)]
    public void Anything_the_runner_stored_that_this_feature_did_not_write_is_never_shown(string? lastError)
    {
        var job = new BackgroundJob { Status = BackgroundJobStatuses.Failed, LastError = lastError };

        var status = PayrollRunProcessingService.Describe(Guid.NewGuid(), job);

        status.ErrorCode.Should().Be("processing_failed");
        status.ErrorMessage.Should().Be(PayrollProcessFailure.UnexpectedMessage);
        status.ErrorMessage.Should().NotContainAny("Npgsql", "relation", "duplicate key", "   at ", "Exception");
        status.FailedEmployees.Should().BeNull();
    }

    [Fact]
    public void A_cancelled_job_reads_as_failed_with_nothing_saved()
    {
        var status = PayrollRunProcessingService.Describe(Guid.NewGuid(), new BackgroundJob { Status = BackgroundJobStatuses.Cancelled });

        status.Status.Should().Be("Failed");
        status.ErrorCode.Should().Be("processing_cancelled");
        status.ErrorMessage.Should().Contain("No changes were saved");
    }

    [Fact]
    public void The_stored_failure_fits_the_column_even_with_a_long_message_and_many_employees()
    {
        var many = Enumerable.Range(1, 200).Select(i => new FailedEmployee(i, $"EMP-{i:00000}", "negative_net_unsupported")).ToList();
        var encoded = new PayrollProcessFailure("x", new string('m', 5000), many).Encode();

        encoded.Length.Should().BeLessThan(4000, "background_jobs.last_error is capped at 4000 characters");
        PayrollProcessFailure.Decode(encoded).FailedEmployees!.Count.Should().BeLessThanOrEqualTo(15);
    }

    [Fact]
    public void A_refusal_body_becomes_a_failure_with_its_error_code_message_and_named_employees()
    {
        var outcome = new PayrollProcessOutcome(422, new
        {
            error = "negative_net_unsupported",
            message = "Reduce the negative adjustments.",
            employees = new[] { new { employeeId = 12, code = "E012", name = "Someone", shortfall = 5m } },
        });

        var failure = PayrollProcessFailure.FromOutcome(outcome);

        failure.Code.Should().Be("negative_net_unsupported");
        failure.Message.Should().Be("Reduce the negative adjustments.");
        failure.FailedEmployees.Should().Equal(new FailedEmployee(12, "E012", "negative_net_unsupported"));
    }

    // ── Fairness, committed-run status, and error redaction ──────────────────────────────────────────

    private static async Task<PayrollRun> AddRunAsync(World w, int month)
    {
        var run = new PayrollRun { TenantId = w.Tenant, CompanyId = w.CompanyId, Year = 2026, Month = month, Status = "Draft" };
        w.Db.PayrollRuns.Add(run);
        await w.Db.SaveChangesAsync();
        return run;
    }

    [Fact]
    public async Task A_third_live_job_for_the_tenant_is_refused_429_but_a_runs_own_job_is_still_returned()
    {
        using var w = await SeedAsync();
        var second = await AddRunAsync(w, 7);
        var third = await AddRunAsync(w, 8);
        var first = await w.Service.EnqueueAsync(w.Run.Id, w.Caller(), default);
        await w.Service.EnqueueAsync(second.Id, w.Caller(), default);

        var refused = await w.Service.EnqueueAsync(third.Id, w.Caller(), default);
        refused.Accepted.Should().BeNull();
        refused.Refusal!.StatusCode.Should().Be(429);
        JsonSerializer.SerializeToElement(refused.Refusal.Body).GetProperty("error").GetString().Should().Be("too_many_processing_jobs");

        (await w.Service.EnqueueAsync(w.Run.Id, w.Caller(), default)).Accepted!.JobId.Should().Be(first.Accepted!.JobId);
        (await w.Db.BackgroundJobs.CountAsync()).Should().Be(2);

        // Once one finishes there is room again.
        await w.Db.BackgroundJobs.Where(j => j.Id == first.Accepted.JobId)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, BackgroundJobStatuses.Succeeded));
        (await w.Service.EnqueueAsync(third.Id, w.Caller(), default)).Accepted.Should().NotBeNull();
    }

    [Theory]
    [InlineData(BackgroundJobStatuses.Failed)]
    [InlineData(BackgroundJobStatuses.Cancelled)]
    public async Task A_job_that_ended_after_its_pass_committed_reads_as_succeeded(string endedAs)
    {
        using var w = await SeedAsync();
        var accepted = (await w.Service.EnqueueAsync(w.Run.Id, w.Caller(), default)).Accepted!;
        await w.Db.BackgroundJobs.ExecuteUpdateAsync(s => s
            .SetProperty(j => j.Status, endedAs).SetProperty(j => j.ProgressTotal, 5).SetProperty(j => j.ProgressCompleted, 3)
            .SetProperty(j => j.LastError, "Lease expired after 3 attempt(s) without the job completing"));

        (await w.Service.GetStatusAsync(w.Run.Id, w.Tenant, default))!.Status.Should().Be("Failed", "no checkpoint, so nothing was committed");

        w.Db.BackgroundJobItems.Add(new BackgroundJobItem { TenantId = w.Tenant, JobId = accepted.JobId, ItemKey = PayrollRunProcessJobHandler.ItemKey });
        await w.Db.SaveChangesAsync();
        var status = (await w.Service.GetStatusAsync(w.Run.Id, w.Tenant, default))!;
        status.Status.Should().Be("Succeeded");
        status.ErrorCode.Should().BeNull();
        (status.ProcessedEmployees, status.TotalEmployees).Should().Be((5, 5));
    }

    [Fact]
    public void The_jobs_api_never_shows_raw_runner_errors_and_a_payroll_failure_shows_only_its_own_message()
    {
        const string raw = "NpgsqlException: Exception while reading from stream (Host=db.internal.example;Port=5432)";
        var job = new BackgroundJob { JobType = PayrollRunProcessJobHandler.JobType, Status = BackgroundJobStatuses.Failed, LastError = raw };

        var payroll = BackgroundJobDto.From(job, PayrollRunProcessJobHandler.Descriptor);
        payroll.LastError.Should().Be(PayrollProcessFailure.UnexpectedMessage);
        JsonSerializer.Serialize(payroll).Should().NotContain("Npgsql").And.NotContain("5432").And.NotContain("db.internal");

        // A job type that has not opted in gets the generic text, whatever the runner stored.
        var other = new BackgroundJobTypeDescriptor("other.type", typeof(PayrollRunProcessJobHandler), [], []);
        BackgroundJobDto.From(job, other).LastError.Should().Be(BackgroundJobDto.GenericError);
        BackgroundJobDto.From(job, null).LastError.Should().Be(BackgroundJobDto.GenericError);
        BackgroundJobDto.From(new BackgroundJob { LastError = null }, other).LastError.Should().BeNull();

        var ours = new PayrollProcessFailure("negative_net_unsupported", "Reduce the negative adjustments.", null);
        BackgroundJobDto.From(new BackgroundJob { Status = BackgroundJobStatuses.Failed, LastError = ours.Encode() },
            PayrollRunProcessJobHandler.Descriptor).LastError.Should().Be("Reduce the negative adjustments.");
    }

    // ── The controller: thin, permissioned, and the old entry point is no longer a route ─────────

    private static PayrollController Controller(World w, params string[] permissions)
    {
        var claims = new List<Claim> { new("tenant_id", w.Tenant.ToString()), new(ClaimTypes.NameIdentifier, User.ToString()) };
        claims.AddRange(permissions.Select(p => new Claim("permission", p)));
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) };
        var ctrl = new PayrollController(w.Db, null!, new HttpContextAccessor { HttpContext = http }, null!, null!, null!, null!, null!, null!);
        ctrl.ControllerContext = new ControllerContext { HttpContext = http };
        return ctrl;
    }

    [Fact]
    public async Task The_endpoint_answers_202_with_the_agreed_body_and_the_same_job_on_a_second_post()
    {
        using var w = await SeedAsync();
        var ctrl = Controller(w, "payroll.write");

        var first = Assert.IsType<AcceptedResult>(await ctrl.EnqueueProcess(w.Run.Id, w.Service, default));
        var second = Assert.IsType<AcceptedResult>(await ctrl.EnqueueProcess(w.Run.Id, w.Service, default));

        var body = Assert.IsType<PayrollProcessingAccepted>(first.Value);
        body.RunId.Should().Be(w.Run.Id);
        body.Status.Should().Be("Queued");
        JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web)).Should()
            .Be($"{{\"runId\":\"{w.Run.Id}\",\"jobId\":\"{body.JobId}\",\"status\":\"Queued\"}}");
        Assert.Equal(body.JobId, Assert.IsType<PayrollProcessingAccepted>(second.Value).JobId);
    }

    [Fact]
    public async Task The_endpoint_still_refuses_synchronously_with_the_4xx_it_always_gave()
    {
        using var voided = await SeedAsync("Voided");
        using var processed = await SeedAsync("Processed");
        using var missing = await SeedAsync();

        Assert.IsType<BadRequestObjectResult>(await Controller(voided, "payroll.write").EnqueueProcess(voided.Run.Id, voided.Service, default));
        var processedResult = Assert.IsType<BadRequestObjectResult>(await Controller(processed, "payroll.write").EnqueueProcess(processed.Run.Id, processed.Service, default));
        JsonSerializer.Serialize(processedResult.Value).Should().Contain("cannot be reprocessed");
        Assert.IsType<NotFoundResult>(await Controller(missing, "payroll.write").EnqueueProcess(Guid.NewGuid(), missing.Service, default));
    }

    [Fact]
    public async Task The_status_endpoint_returns_200_idle_then_the_job_and_404_for_a_run_that_is_not_the_callers()
    {
        using var w = await SeedAsync();
        using var elsewhere = await SeedAsync();
        var ctrl = Controller(w, "payroll.read", "payroll.write");

        var idle = Assert.IsType<OkObjectResult>(await ctrl.ProcessingStatus(w.Run.Id, w.Service, default));
        Assert.Equal("Idle", Assert.IsType<PayrollProcessingStatus>(idle.Value).Status);

        await ctrl.EnqueueProcess(w.Run.Id, w.Service, default);
        var queued = Assert.IsType<OkObjectResult>(await ctrl.ProcessingStatus(w.Run.Id, w.Service, default));
        Assert.Equal("Queued", Assert.IsType<PayrollProcessingStatus>(queued.Value).Status);

        // The other tenant's run id, asked by this tenant's user: 404, not 403.
        Assert.IsType<NotFoundResult>(await ctrl.ProcessingStatus(elsewhere.Run.Id, w.Service, default));
    }

    [Fact]
    public void The_endpoints_require_the_run_permissions_the_old_one_did_and_no_role_gate()
    {
        var enqueue = typeof(PayrollController).GetMethod(nameof(PayrollController.EnqueueProcess))!;
        enqueue.GetCustomAttribute<HttpPostAttribute>()!.Template.Should().Be("runs/{id:guid}/process");
        enqueue.GetCustomAttribute<HasPermissionAttribute>()!.Permissions.Should().Equal("payroll.write");
        enqueue.GetCustomAttributes<AuthorizeAttribute>().Should().OnlyContain(a => a.Roles == null, "new endpoints use permissions, not role lists");

        var status = typeof(PayrollController).GetMethod(nameof(PayrollController.ProcessingStatus))!;
        status.GetCustomAttribute<HttpGetAttribute>()!.Template.Should().Be("runs/{id:guid}/processing");
        status.GetCustomAttribute<HasPermissionAttribute>()!.Permissions.Should().Equal("payroll.read");
        status.GetCustomAttributes<AuthorizeAttribute>().Should().OnlyContain(a => a.Roles == null);

        // The in-process entry point is not routable, so POST .../process can only ever queue.
        var inProcess = typeof(PayrollController).GetMethod(nameof(PayrollController.Process))!;
        inProcess.GetCustomAttribute<NonActionAttribute>().Should().NotBeNull();
        inProcess.GetCustomAttribute<HttpPostAttribute>().Should().BeNull();
    }

    [Fact]
    public void The_job_type_is_visible_to_payroll_readers_and_the_jobs_api_gate_admits_them()
    {
        PayrollRunProcessJobHandler.Descriptor.ViewPermissions.Should().Equal("payroll.read");
        BackgroundJobPermissions.AnyView.Should().Contain("payroll.read");
        typeof(JobsController).GetCustomAttribute<HasPermissionAttribute>()!.Permissions
            .Should().BeEquivalentTo(BackgroundJobPermissions.AnyView);
        PayrollRunProcessJobHandler.IdempotencyKey(Guid.Empty).Should().Be("payroll.run.process:00000000-0000-0000-0000-000000000000");
    }
}
