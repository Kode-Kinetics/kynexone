using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Entitlements;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.ReleaseA;

/// <summary>
/// G-011. A cash allowance added without Finance rights waits for Finance, but nobody told Finance: no queue, no notice, and the
/// run then refused on payroll day. The notice goes to exactly the people who could confirm it, judged by the confirm endpoint's own
/// rule: an active login that holds payroll.approve (role or allow override, not denied), with access to every company, and who is
/// not the person who added it.
/// </summary>
public class CashAllowanceFinanceNoticeTests
{
    private sealed class FixedClock(DateOnly today) : ITenantClock
    {
        public Task<DateOnly> TodayAsync(Guid tenantId, CancellationToken ct) => Task.FromResult(today);
    }

    private sealed class World : IAsyncDisposable
    {
        public ZayraDbContext Db { get; } = new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        public Guid Tid { get; } = Guid.NewGuid();
        public Guid OtherTid { get; } = Guid.NewGuid();
        public EntitlementMatrixService Service { get; private set; } = null!;
        public Permission Approve { get; } = new() { Key = "payroll.approve", Module = "Payroll" };
        public Role FinanceRole { get; private set; } = null!;
        public Role PlainRole { get; private set; } = null!;

        public Guid Maker = Guid.NewGuid();             // HR: adds the allowance, holds no payroll.approve
        public Guid FinanceGroup = Guid.NewGuid();      // payroll.approve via role, group scope
        public Guid FinanceViaOverride = Guid.NewGuid();// payroll.approve via an allow override, group scope
        public Guid FinanceCompanyOnly = Guid.NewGuid();// payroll.approve but only one company
        public Guid FinanceAllCompanies = Guid.NewGuid();// payroll.approve, grant mode AllCurrentAndFutureCompanies (group level)
        public Guid FinanceInactive = Guid.NewGuid();
        public Guid FinanceDeleted = Guid.NewGuid();
        public Guid FinanceDenied = Guid.NewGuid();     // role grants it, a deny override removes it
        public Guid NoPermission = Guid.NewGuid();
        public Guid OtherTenantFinance = Guid.NewGuid();
        public Guid CompanyA = Guid.NewGuid();

        public static async Task<World> Create()
        {
            var w = new World();
            var db = w.Db;
            db.Tenants.AddRange(new Tenant { Id = w.Tid, Name = "T", Slug = $"t-{w.Tid:N}"[..20] }, new Tenant { Id = w.OtherTid, Name = "O", Slug = $"o-{w.OtherTid:N}"[..20] });
            db.Companies.AddRange(new Company { Id = w.CompanyA, TenantId = w.Tid, LegalNameEn = "A Co.", DefaultCurrency = "SAR" },
                new Company { TenantId = w.Tid, LegalNameEn = "B Co.", DefaultCurrency = "SAR" });
            db.GlDrivers.Add(new GlDriver
            {
                TenantId = w.Tid, CompanyId = null, Key = "EARN:CAR", Label = "EARN:CAR", Category = GlDriverCategories.Earning, PostingSide = "DR",
                DefaultCode = "5210", DefaultName = "Account 5210", MatchMode = "Exact", MatchComponentCode = "CAR", SortOrder = 70,
            });
            db.Permissions.Add(w.Approve);
            w.FinanceRole = new Role { TenantId = w.Tid, Name = "Payroll Manager", NormalizedName = "PAYROLL MANAGER", IsSystem = true };
            w.PlainRole = new Role { TenantId = w.Tid, Name = "HR Manager", NormalizedName = "HR MANAGER", IsSystem = true };
            var otherFinance = new Role { TenantId = w.OtherTid, Name = "Payroll Manager", NormalizedName = "PAYROLL MANAGER", IsSystem = true };
            db.Roles.AddRange(w.FinanceRole, w.PlainRole, otherFinance);
            db.RolePermissions.AddRange(new RolePermission { RoleId = w.FinanceRole.Id, PermissionId = w.Approve.Id },
                new RolePermission { RoleId = otherFinance.Id, PermissionId = w.Approve.Id });

            User U(Guid id, Guid tenant, string name, bool group, Role? role, bool active = true, bool deleted = false)
            {
                var u = new User
                {
                    Id = id, TenantId = tenant, FullName = name, Email = $"{name}@x.test", NormalizedEmail = $"{name}@X.TEST", IsGroupScope = group,
                    IsActive = active, IsDeleted = deleted, Status = active ? "Active" : "Deactivated",
                };
                db.Users.Add(u);
                if (role is not null) db.UserRoles.Add(new UserRole { UserId = id, RoleId = role.Id });
                return u;
            }
            U(w.Maker, w.Tid, "maker", true, w.PlainRole);
            U(w.FinanceGroup, w.Tid, "fin-group", true, w.FinanceRole);
            U(w.FinanceViaOverride, w.Tid, "fin-override", true, w.PlainRole);
            db.UserPermissionOverrides.Add(new UserPermissionOverride { TenantId = w.Tid, UserId = w.FinanceViaOverride, PermissionKey = "payroll.approve", Effect = "Allow", IsActive = true });
            U(w.FinanceCompanyOnly, w.Tid, "fin-company", false, w.FinanceRole);
            db.UserEntityAccesses.Add(new UserEntityAccess { TenantId = w.Tid, UserId = w.FinanceCompanyOnly, CompanyId = w.CompanyA, GrantMode = EntityGrantModes.SelectedCompanies, IsActive = true });
            U(w.FinanceAllCompanies, w.Tid, "fin-all", false, w.FinanceRole);
            db.UserEntityAccesses.Add(new UserEntityAccess { TenantId = w.Tid, UserId = w.FinanceAllCompanies, CompanyId = null, GrantMode = EntityGrantModes.AllCurrentAndFutureCompanies, IsActive = true });
            U(w.FinanceInactive, w.Tid, "fin-inactive", true, w.FinanceRole, active: false);
            U(w.FinanceDeleted, w.Tid, "fin-deleted", true, w.FinanceRole, deleted: true);
            U(w.FinanceDenied, w.Tid, "fin-denied", true, w.FinanceRole);
            db.UserPermissionOverrides.Add(new UserPermissionOverride { TenantId = w.Tid, UserId = w.FinanceDenied, PermissionKey = "payroll.approve", Effect = "Deny", IsActive = true });
            U(w.NoPermission, w.Tid, "nobody", true, w.PlainRole);
            U(w.OtherTenantFinance, w.OtherTid, "other-tenant", true, otherFinance);
            await db.SaveChangesAsync();
            w.Service = new EntitlementMatrixService(db, new FixedClock(new DateOnly(2026, 10, 6)));
            return w;
        }

        public MatrixActor Actor(Guid user, bool finance) =>
            new(Tid, user, EntityScopeContext.GroupLevel, "127.0.0.1", finance);

        public Task<MatrixResult> AddCash(Guid user, bool finance, string name = "Car") =>
            Service.CreateBenefitAsync(Actor(user, finance), new CreateBenefitRequest(name, "سيارة", BenefitKinds.Cash, "EARN:CAR", true, null, null), default);

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    [Fact]
    public async Task AnAllowanceWaitingForFinance_NotifiesEveryoneWhoCouldConfirmIt_AndNobodyElse()
    {
        await using var w = await World.Create();

        var added = await w.AddCash(w.Maker, finance: false, "Car");

        added.IsSuccess.Should().BeTrue(System.Text.Json.JsonSerializer.Serialize(added.Body));
        var notices = await w.Db.Notifications.AsNoTracking().ToListAsync();
        notices.Select(n => n.UserId).Should().BeEquivalentTo(new Guid?[] { w.FinanceGroup, w.FinanceViaOverride, w.FinanceAllCompanies },
            "an active, group-scoped payroll.approve holder who did not add it: by role, by allow override, or by an all-companies grant");
        notices.Should().OnlyContain(n => n.TenantId == w.Tid && n.EntityName == "CashAllowance" && n.EntityId == "ALW_CAR" && n.Status == "Unread");
        notices.Should().OnlyContain(n => n.Title.Contains("Waiting for Finance") && n.Message.Contains("Car") && n.Message.Contains("Benefits by grade"));
        notices.Should().OnlyContain(n => n.Message.Contains("سيارة"), "the Arabic name and sentence ride along");
    }

    [Fact]
    public async Task TheMakerIsNeverNotified_EvenIfTheyHoldPayrollApprove()
    {
        await using var w = await World.Create();
        // The maker gains the right later, e.g. a role change; the endpoint would still refuse them (maker_cannot_confirm).
        w.Db.UserPermissionOverrides.Add(new UserPermissionOverride { TenantId = w.Tid, UserId = w.Maker, PermissionKey = "payroll.approve", Effect = "Allow", IsActive = true });
        await w.Db.SaveChangesAsync();

        await w.AddCash(w.Maker, finance: false);

        (await w.Db.Notifications.AsNoTracking().Select(n => n.UserId).ToListAsync()).Should().NotContain(w.Maker);
    }

    [Fact]
    public async Task AFinanceMakerConfirmsByAdding_SoNobodyIsToldToConfirm()
    {
        await using var w = await World.Create();

        var added = await w.AddCash(w.FinanceGroup, finance: true);

        added.IsSuccess.Should().BeTrue();
        (await w.Db.Notifications.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task AContractBenefit_NeedsNoFinance_AndNotifiesNobody()
    {
        await using var w = await World.Create();

        var added = await w.Service.CreateBenefitAsync(w.Actor(w.Maker, false), new CreateBenefitRequest("Gym", null, BenefitKinds.Contract, null, true, null, null), default);

        added.IsSuccess.Should().BeTrue();
        (await w.Db.Notifications.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ARefusedAdd_NotifiesNobody()
    {
        await using var w = await World.Create();

        var refused = await w.Service.CreateBenefitAsync(w.Actor(w.Maker, false), new CreateBenefitRequest("Car", null, BenefitKinds.Cash, null, true, null, null), default);

        refused.IsSuccess.Should().BeFalse("a cash allowance without a GL driver is refused");
        (await w.Db.Notifications.CountAsync()).Should().Be(0);
    }
}
