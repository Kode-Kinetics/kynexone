using Zayra.Api.Application.Recruitment;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Notifications;

namespace Zayra.Api.Infrastructure.Recruitment;

/// <summary>
/// When an accepted offer starts a hire draft, the people who can approve that hire are told, once, in English and Arabic. The audience is
/// the logins holding <c>employees.approve</c> whose company access covers the offer's company, minus the person who recorded the
/// acceptance (they made the hire and may not decide it). Post-commit and best-effort: a notification outage never undoes an acceptance.
/// </summary>
public static class HireDraftNotifier
{
    public const string Title = "New hire awaiting approval | تعيين جديد بانتظار الاعتماد";

    public static string Message(string candidateName, string jobTitle) =>
        $"{candidateName} accepted the offer for {jobTitle}. The new-hire draft is waiting for your approval. | "
        + $"قبل {candidateName} عرض العمل لوظيفة {jobTitle}. مسودة التعيين بانتظار اعتمادك.";

    public static async Task NotifyApproversAsync(INotificationService notifications, ZayraDbContext db, OfferActor actor,
        Guid draftId, string candidateName, string jobTitle, CancellationToken ct)
    {
        try
        {
            var approvers = await NotificationAudience.PermissionHoldersAsync(db, actor.TenantId, ["employees.approve"], actor.CompanyId, [actor.UserId], ct);
            await notifications.NotifyEachAsync(actor.TenantId, approvers.Select(id => (Guid?)id), Title, Message(candidateName, jobTitle),
                "EmployeeDraft", draftId.ToString(), ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The acceptance is already committed; the draft still shows in the New hires queue.
        }
    }
}
