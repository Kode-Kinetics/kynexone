using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// Leave Batch 1, items 1, 2 and 4, end to end through the real pipeline.
///
/// Withdraw: the web posts NO body (axios), so the endpoint must accept an empty one; it must also go through
/// LeaveService (lock, audit, notice) like Cancel does. Cancel: a Rejected / Withdrawn / Cancelled request must
/// be refused without touching the balance. Attachment keys: List/Get must not hand a storage key to someone who is
/// neither the leave's employee nor HR/Admin, and the legacy ESS upload must not turn a key another employee's
/// document or leave already holds into a document of the caller's.
/// </summary>
public sealed class LeaveBatch1HttpTests(LeaveBatch1Fixture f) : IClassFixture<LeaveBatch1Fixture>
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private sealed record Bell(Guid Id, string Title, string? EntityId);

    private async Task<List<Bell>> BellAsync(string who)
    {
        using var response = await f.Send(HttpMethod.Get, "/api/notifications", f.Token(who));
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        return JsonSerializer.Deserialize<List<Bell>>(text, Web)!;
    }

    private Task<(decimal Pending, decimal Used, int Reversals)> BalanceAsync(Guid leaveId) => f.WithDb(async db =>
    {
        var balances = await db.EmployeeLeaveBalances.AsNoTracking().Where(b => b.TenantId == f.TenantId && b.EmployeeId == f.EmployeeId).ToListAsync();
        var reversals = await db.LeaveBalanceTransactions.AsNoTracking()
            .CountAsync(t => t.TenantId == f.TenantId && t.Reference == leaveId.ToString() && t.TransactionType == "Reversed");
        return (balances.Sum(b => b.Pending), balances.Sum(b => b.Used), reversals);
    });

    private Task<string> StatusAsync(Guid leaveId) =>
        f.WithDb(db => db.LeaveRequests.AsNoTracking().Where(r => r.Id == leaveId).Select(r => r.Status).SingleAsync());

    // ── Item 1: withdraw ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Withdraw_with_no_request_body_withdraws_the_request_and_releases_the_reservation()
    {
        var id = await f.SubmitAsync();
        (await BalanceAsync(id)).Pending.Should().BeGreaterThan(0);

        // Exactly what the web client sends: a POST with no content at all.
        using var withdraw = await f.Send(HttpMethod.Post, $"/api/leave/requests/{id}/withdraw", f.Token("employee"));
        withdraw.StatusCode.Should().Be(HttpStatusCode.OK, await withdraw.Content.ReadAsStringAsync());

        (await StatusAsync(id)).Should().Be("Withdrawn");
        var balance = await BalanceAsync(id);
        balance.Pending.Should().Be(0);
        balance.Reversals.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Withdraw_with_an_empty_json_body_still_works_as_the_mobile_app_sends_it()
    {
        var id = await f.SubmitAsync();
        using var withdraw = await f.Send(HttpMethod.Post, $"/api/leave/requests/{id}/withdraw", f.Token("employee"), new { reason = "changed plans" });
        withdraw.StatusCode.Should().Be(HttpStatusCode.OK, await withdraw.Content.ReadAsStringAsync());
        (await f.WithDb(db => db.LeaveRequests.AsNoTracking().Where(r => r.Id == id).Select(r => r.CancellationReason).SingleAsync()))
            .Should().Be("changed plans");
    }

    [Fact]
    public async Task Withdraw_is_audited_and_tells_the_pending_decider_and_the_employee()
    {
        var id = await f.SubmitAsync();
        using var withdraw = await f.Send(HttpMethod.Post, $"/api/leave/requests/{id}/withdraw", f.Token("employee"), new { reason = "plans" });
        withdraw.StatusCode.Should().Be(HttpStatusCode.OK, await withdraw.Content.ReadAsStringAsync());

        var audit = await f.WithDb(db => db.LeaveAuditLogs.AsNoTracking()
            .Where(a => a.TenantId == f.TenantId && a.EntityId == id.ToString() && a.Action == "Withdrawn").ToListAsync());
        audit.Should().ContainSingle("every other leave transition writes an audit row");
        audit[0].OldValue.Should().Be("PendingManagerApproval");

        (await BellAsync("manager")).Should().Contain(n => n.EntityId == id.ToString() && n.Title == "Leave Withdrawn",
            "whoever was deciding it is told it is gone");
        (await BellAsync("peer")).Should().NotContain(n => n.EntityId == id.ToString());
    }

    [Fact]
    public async Task Withdraw_closes_the_pending_approval_rows_and_the_approval_center_projection()
    {
        var id = await f.SubmitAsync();
        using var withdraw = await f.Send(HttpMethod.Post, $"/api/leave/requests/{id}/withdraw", f.Token("employee"));
        withdraw.StatusCode.Should().Be(HttpStatusCode.OK, await withdraw.Content.ReadAsStringAsync());

        await f.WithDb(async db =>
        {
            (await db.LeaveApprovals.AsNoTracking().Where(a => a.LeaveRequestId == id).Select(a => a.Decision).ToListAsync())
                .Should().NotContain("Pending").And.Contain("Cancelled");
            (await db.ApprovalRequests.AsNoTracking().Where(a => a.EntityId == id.ToString()).Select(a => a.Status).ToListAsync())
                .Should().NotContain("Pending");
            return 0;
        });
    }

    [Fact]
    public async Task Withdraw_of_an_approved_request_is_refused_and_the_approval_stands()
    {
        var id = await f.SubmitAsync();
        using (var approve = await f.Send(HttpMethod.Post, $"/api/leave/requests/{id}/approve", f.Token("manager"), new { notes = "ok" }))
            approve.StatusCode.Should().Be(HttpStatusCode.OK, await approve.Content.ReadAsStringAsync());

        using var withdraw = await f.Send(HttpMethod.Post, $"/api/leave/requests/{id}/withdraw", f.Token("employee"));
        withdraw.StatusCode.Should().Be(HttpStatusCode.BadRequest, await withdraw.Content.ReadAsStringAsync());
        (await StatusAsync(id)).Should().Be("Approved");
        (await BalanceAsync(id)).Used.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Withdraw_of_a_colleagues_request_is_forbidden()
    {
        var id = await f.SubmitAsync();
        using var withdraw = await f.Send(HttpMethod.Post, $"/api/leave/requests/{id}/withdraw", f.Token("peer"));
        withdraw.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await StatusAsync(id)).Should().Be("PendingManagerApproval");
    }

    [Fact]
    public async Task An_hr_manager_can_withdraw_an_employees_pending_request()
    {
        var id = await f.SubmitAsync();
        using var withdraw = await f.Send(HttpMethod.Post, $"/api/leave/requests/{id}/withdraw", f.Token("hr"));
        withdraw.StatusCode.Should().Be(HttpStatusCode.OK, await withdraw.Content.ReadAsStringAsync());
        (await StatusAsync(id)).Should().Be("Withdrawn");
    }

    // ── Item 2: cancel on a request that is not live ────────────────────────────────────────────

    [Fact]
    public async Task Cancel_of_a_rejected_request_is_refused_and_releases_no_balance()
    {
        // A is rejected (its Pending already released); B is live and holds its own Pending.
        var rejected = await f.SubmitAsync();
        var live = await f.SubmitAsync();
        using (var reject = await f.Send(HttpMethod.Post, $"/api/leave/requests/{rejected}/reject", f.Token("manager"), new { reason = "no cover" }))
            reject.StatusCode.Should().Be(HttpStatusCode.OK, await reject.Content.ReadAsStringAsync());
        var before = await BalanceAsync(rejected);

        using var cancel = await f.Send(HttpMethod.Post, $"/api/leave/requests/{rejected}/cancel", f.Token("employee"), new { reason = "oops" });
        cancel.StatusCode.Should().Be(HttpStatusCode.BadRequest, await cancel.Content.ReadAsStringAsync());

        (await StatusAsync(rejected)).Should().Be("Rejected", "history is not rewritten");
        var after = await BalanceAsync(rejected);
        after.Pending.Should().Be(before.Pending, "the live request's reservation must survive");
        after.Reversals.Should().Be(before.Reversals, "no second 'Reversed' row for the rejected request");
        (await StatusAsync(live)).Should().Be("PendingManagerApproval");
    }

    [Theory]
    [InlineData("withdraw")]
    [InlineData("cancel")]
    public async Task Cancel_of_a_withdrawn_or_cancelled_request_is_refused(string how)
    {
        var id = await f.SubmitAsync();
        var live = await f.SubmitAsync();
        using (var first = await f.Send(HttpMethod.Post, $"/api/leave/requests/{id}/{how}", f.Token("employee"), new { reason = "x" }))
            first.StatusCode.Should().Be(HttpStatusCode.OK, await first.Content.ReadAsStringAsync());
        var before = await BalanceAsync(id);

        using var again = await f.Send(HttpMethod.Post, $"/api/leave/requests/{id}/cancel", f.Token("employee"), new { reason = "again" });
        again.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await BalanceAsync(id)).Should().Be(before);
        (await StatusAsync(live)).Should().Be("PendingManagerApproval");
    }

    // ── Item 4: attachment keys ─────────────────────────────────────────────────────────────────

    private string NewKey(string file) => $"storage/documents/{f.TenantId:N}/{Guid.NewGuid():N}_{file}";

    private object Upload(string key) => new
    {
        documentType = "Other", fileName = "x.pdf", contentType = "application/pdf", storageUrl = key, expiryDate = (string?)null, isRequired = false,
    };

    private async Task<Guid> SeedLeaveWithKeyAsync(string key)
    {
        var day = f.NextWorkday().AddDays(300);
        return await f.SeedLeaveAsync("Approved", day, day, attachmentPath: key);
    }

    [Fact]
    public async Task A_colleague_cannot_register_a_storage_key_a_leave_request_holds()
    {
        var key = NewKey("sicknote.pdf");
        await SeedLeaveWithKeyAsync(key);

        using var response = await f.Send(HttpMethod.Post, "/api/ess/documents/upload", f.Token("peer"), Upload(key));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
        (await f.WithDb(db => db.EmployeeDocuments.IgnoreQueryFilters().CountAsync(d => d.EmployeeId == f.PeerId && d.StorageUrl == key))).Should().Be(0);
    }

    [Fact]
    public async Task A_colleague_cannot_register_a_storage_key_another_employees_document_holds()
    {
        var key = NewKey("passport.pdf");
        await f.WithDb(async db =>
        {
            db.EmployeeDocuments.Add(new EmployeeDocument
            {
                TenantId = f.TenantId, CompanyId = f.CompanyId, EmployeeId = f.EmployeeId, DocumentType = "Medical", FileName = "m.pdf",
                ContentType = "application/pdf", StorageUrl = key, ApprovalStatus = "Approved",
            });
            await db.SaveChangesAsync();
            return 0;
        });

        using var response = await f.Send(HttpMethod.Post, "/api/ess/documents/upload", f.Token("peer"), Upload(key));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("dot-segment")]
    [InlineData("double-slash")]
    [InlineData("upper-case")]
    [InlineData("backslash")]
    public async Task A_spelling_variant_of_a_held_key_is_refused_too(string variant)
    {
        var name = $"{Guid.NewGuid():N}_variant.pdf";
        var key = $"storage/documents/{f.TenantId:N}/{name}";
        await SeedLeaveWithKeyAsync(key);
        var candidate = variant switch
        {
            "dot-segment" => $"storage/documents/{f.TenantId:N}/./{name}",
            "double-slash" => $"storage/documents/{f.TenantId:N}//{name}",
            "upper-case" => key.ToUpperInvariant(),
            _ => key.Replace('/', '\\'),
        };

        using var response = await f.Send(HttpMethod.Post, "/api/ess/documents/upload", f.Token("peer"), Upload(candidate));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_key_nothing_else_holds_is_still_accepted_so_the_endpoint_keeps_working()
    {
        using var response = await f.Send(HttpMethod.Post, "/api/ess/documents/upload", f.Token("peer"), Upload(NewKey("fresh.pdf")));
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
    }

    private async Task<(Guid Id, string Key)> LeaveWithKeyForEmployeeAsync()
    {
        var key = NewKey("sick.pdf");
        var day = f.NextWorkday().AddDays(400);
        var id = await f.SeedLeaveAsync("Approved", day, day, attachmentPath: key);
        return (id, key);
    }

    private static string? ListedKey(string json, Guid id) =>
        JsonDocument.Parse(json).RootElement.GetProperty("items").EnumerateArray()
            .Single(x => x.GetProperty("id").GetString() == id.ToString()).GetProperty("attachmentPath").GetString();

    [Theory]
    [InlineData("manager", false)]
    [InlineData("employee", true)]
    [InlineData("hr", true)]
    [InlineData("admin", true)]
    public async Task The_leave_list_returns_the_attachment_key_only_to_the_employee_and_hr(string who, bool seesKey)
    {
        var (id, key) = await LeaveWithKeyForEmployeeAsync();
        using var response = await f.Send(HttpMethod.Get, $"/api/leave/requests?employeeId={f.EmployeeId}&pageSize=100", f.Token(who));
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        text.Contains(key, StringComparison.OrdinalIgnoreCase).Should().Be(seesKey);
        if (seesKey) ListedKey(text, id).Should().Be(key);
        else ListedKey(text, id).Should().BeNullOrEmpty();
    }

    [Theory]
    [InlineData("manager", false)]
    [InlineData("employee", true)]
    [InlineData("hr", true)]
    public async Task The_leave_detail_returns_the_attachment_key_only_to_the_employee_and_hr(string who, bool seesKey)
    {
        var (id, key) = await LeaveWithKeyForEmployeeAsync();
        using var response = await f.Send(HttpMethod.Get, $"/api/leave/requests/{id}", f.Token(who));
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        text.Contains(key, StringComparison.OrdinalIgnoreCase).Should().Be(seesKey);
    }
}
