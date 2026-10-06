using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Contracts;

/// <summary>One reminder the job owes: a case, one of its four deadlines, and the level (1 = HR, 2 = HR Director).</summary>
public sealed record RenewalReminder(Guid CaseId, Guid CompanyId, Guid EmployeePublicId, string Kind, DateOnly DueOn, int Level)
{
    /// <summary>Deterministic across runs and attempts: the job item key and the outbox entity id.</summary>
    public string Key => $"{CaseId:N}:{Kind}:{DueOn:yyyyMMdd}:L{Level}";
}

/// <summary>What one reminder send did.</summary>
public sealed record RenewalReminderResult(int Enqueued, int AlreadySent, bool NoRecipient);

/// <summary>
/// Renewal deadline reminders through the notification outbox (<see cref="INotificationService.EnqueueAsync"/>; the
/// delivery worker owns provider I/O). On a deadline's day HR Managers with access to the case's company are told;
/// if it is still pending the next day, it escalates to the HR Director (Admin when the tenant has no HR Director).
/// Deadlines more than <see cref="StaleAfterDays"/> old are not re-announced — the dashboard shows them as overdue.
///
/// <para><b>Never twice.</b> A reminder is identified by (case, deadline kind, due date, level) — <see cref="RenewalReminder.Key"/>
/// — stored as the outbox entity id. Before enqueueing for a recipient the outbox is checked for that key and user,
/// and the outbox's own business-identity dedupe key refuses a second row, so a re-run, a retried job attempt or
/// two overlapping jobs send each reminder to each person once.</para> Slice R4.
/// </summary>
public sealed class RenewalReminderService
{
    public const string DeadlineEvent = "CONTRACT_RENEWAL_DEADLINE";
    public const string EscalationEvent = "CONTRACT_RENEWAL_ESCALATION";
    public const string EntityName = "ContractRenewalReminder";
    public const int StaleAfterDays = 7;

    private static readonly string[] Level1Roles = ["HR Manager"];
    private static readonly string[] Level2Roles = ["HR Director"];
    private static readonly string[] Level2FallbackRoles = ["Admin"];

    private readonly ZayraDbContext _db;
    private readonly INotificationService _notifications;

    public RenewalReminderService(ZayraDbContext db, INotificationService notifications)
    {
        _db = db;
        _notifications = notifications;
    }

    /// <summary>The reminders due today for one case (pure).</summary>
    public static IReadOnlyList<RenewalReminder> Due(ContractRenewalCase c, DateOnly today)
    {
        var list = new List<RenewalReminder>();
        if (c.CompanyId is not { } company) return list;
        foreach (var d in RenewalNextStep.Pending(c))
        {
            var late = today.DayNumber - d.DueOn.DayNumber;
            if (late >= 0 && late <= StaleAfterDays) list.Add(new(c.Id, company, c.EmployeeId, d.Kind, d.DueOn, 1));
            if (late >= 1 && late <= StaleAfterDays + 1) list.Add(new(c.Id, company, c.EmployeeId, d.Kind, d.DueOn, 2));
        }
        return list;
    }

    /// <summary>Every reminder due today across the tenant's open cases.</summary>
    public async Task<IReadOnlyList<RenewalReminder>> PlanAsync(Guid tenantId, DateOnly today, CancellationToken ct)
    {
        var open = await _db.ContractRenewalCases.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.ClosedAt == null && c.State != RenewalStates.OnHold)
            .ToListAsync(ct);
        return open.SelectMany(c => Due(c, today)).ToList();
    }

    /// <summary>Enqueues one reminder to each recipient who has not had it yet.</summary>
    public async Task<RenewalReminderResult> SendAsync(Guid tenantId, RenewalReminder reminder, CancellationToken ct)
    {
        var eventCode = reminder.Level == 1 ? DeadlineEvent : EscalationEvent;
        var recipients = await RecipientsAsync(tenantId, reminder.CompanyId, reminder.Level == 1 ? Level1Roles : Level2Roles, ct);
        if (recipients.Count == 0 && reminder.Level == 2)
            recipients = await RecipientsAsync(tenantId, reminder.CompanyId, Level2FallbackRoles, ct);
        if (recipients.Count == 0) return new RenewalReminderResult(0, 0, true);

        var employee = await _db.Employees.AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.PublicId == reminder.EmployeePublicId)
            .Select(e => new { e.FullName, e.EmployeeCode })
            .FirstOrDefaultAsync(ct);
        var who = employee is null ? "an employee" : $"{employee.FullName} ({employee.EmployeeCode})";
        var (title, message) = Text(reminder, who);

        int enqueued = 0, already = 0;
        foreach (var userId in recipients)
        {
            var sent = await _db.NotificationDeliveries.AsNoTracking().AnyAsync(d =>
                d.TenantId == tenantId && d.EventCode == eventCode && d.EntityName == EntityName
                && d.EntityId == reminder.Key && d.UserId == userId, ct);
            if (sent) { already++; continue; }
            var rows = await _notifications.EnqueueAsync(new NotificationRequest
            {
                TenantId = tenantId,
                UserId = userId,
                EventCode = eventCode,
                EntityName = EntityName,
                EntityId = reminder.Key,
                Title = title,
                Message = message,
                Variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Subject"] = title, ["Body"] = message },
            }, ct);
            if (rows.Count > 0) enqueued++; else already++;
        }
        return new RenewalReminderResult(enqueued, already, false);
    }

    /// <summary>Plain-language title and body. Deterministic for a given reminder, so the outbox dedupe key is stable.</summary>
    public static (string Title, string Message) Text(RenewalReminder r, string who)
    {
        var due = r.DueOn.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
        var (what, ifMissed) = r.Kind switch
        {
            RenewalDeadlineKinds.Offer => ("the renewal offer is due", "without a decision the contract renews on its current terms"),
            RenewalDeadlineKinds.Notice => ("the last day to serve a non-renewal notice", "after it the contract renews on its current terms (Article 74(2))"),
            RenewalDeadlineKinds.QiwaSubmit => ("the renewal should be sent to Qiwa", "Qiwa may not confirm it before the contract ends"),
            RenewalDeadlineKinds.QiwaGate => ("the Qiwa outcome must be on file", "the new term cannot be applied before the contract ends"),
            _ => ("a renewal deadline", "the renewal may not complete on time"),
        };
        var title = r.Level == 1 ? $"Contract renewal due {due}" : $"Overdue contract renewal: {due}";
        var lead = r.Level == 1 ? $"{due} is the day {what} for {who}." : $"{due} was the day {what} for {who}, and it is still open.";
        return (title, $"{lead} If missed, {ifMissed}. Open Contract renewals to act on it.");
    }

    /// <summary>Active users holding one of <paramref name="roles"/> who can see <paramref name="companyId"/>.</summary>
    private async Task<List<Guid>> RecipientsAsync(Guid tenantId, Guid companyId, string[] roles, CancellationToken ct)
    {
        var staff = await (from user in _db.Users.AsNoTracking()
                           join assignment in _db.UserRoles.AsNoTracking() on user.Id equals assignment.UserId
                           join role in _db.Roles.AsNoTracking() on assignment.RoleId equals role.Id
                           where user.TenantId == tenantId && user.IsActive && !user.IsDeleted
                                 && (role.TenantId == tenantId || role.TenantId == null) && role.IsActive && !role.IsDeleted
                                 && roles.Contains(role.Name)
                           select new { user.Id, user.IsGroupScope }).Distinct().ToListAsync(ct);
        var result = new List<Guid>();
        foreach (var user in staff.OrderBy(u => u.Id))
        {
            if (user.IsGroupScope || await _db.UserEntityAccesses.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.UserId == user.Id
                    && x.IsActive && (x.CompanyId == companyId || x.GrantMode == "AllCurrentCompanies" || x.GrantMode == "AllCurrentAndFutureCompanies"), ct))
                result.Add(user.Id);
        }
        return result;
    }
}
