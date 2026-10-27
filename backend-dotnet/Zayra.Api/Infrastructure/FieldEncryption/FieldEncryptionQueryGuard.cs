using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Zayra.Api.Infrastructure.FieldEncryption;

/// <summary>
/// Once the plaintext column stops being authoritative (ReadEncrypted and later) a predicate, ordering, grouping or join
/// over an encrypted property would silently compare against stale or blank data. This guard fails the query instead of
/// returning wrong rows: use <see cref="FieldQueryExtensions.WhereBlindIndex"/> for equality. Projections (<c>Select</c>)
/// are not inspected. Inert while the mode is DualWrite or Off.
/// </summary>
public sealed class FieldEncryptionQueryGuard(FieldEncryptionOptions options) : IQueryExpressionInterceptor
{
    public Expression QueryCompilationStarting(Expression queryExpression, QueryExpressionEventData eventData)
    {
        if (options.Mode >= FieldEncryptionMode.ReadEncrypted)
        {
            var violation = FindViolation(queryExpression);
            if (violation is not null)
                throw new InvalidOperationException(
                    $"Query uses encrypted field {violation} in a predicate, ordering, grouping or join. Compare by blind index instead (WhereBlindIndex).");
        }
        return queryExpression;
    }

    /// <summary>The first guarded <c>Entity.Property</c> used outside a Select projection, or null.</summary>
    public static string? FindViolation(Expression expression)
    {
        var finder = new Finder();
        finder.Visit(expression);
        return finder.Violation;
    }

    private sealed class Finder : ExpressionVisitor
    {
        public string? Violation;

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            var isQueryable = node.Method.DeclaringType == typeof(Queryable)
                              || node.Method.DeclaringType == typeof(EntityFrameworkQueryableExtensions);
            if (isQueryable && node.Method.Name is not ("Select" or "SelectMany"))
            {
                foreach (var arg in node.Arguments)
                {
                    if (arg is UnaryExpression { Operand: LambdaExpression lambda }) Inspect(lambda.Body);
                }
            }
            return base.VisitMethodCall(node);
        }

        private void Inspect(Expression body)
        {
            if (Violation is not null) return;
            new MemberScan(this).Visit(body);
        }
    }

    private sealed class MemberScan(Finder owner) : ExpressionVisitor
    {
        protected override Expression VisitMember(MemberExpression node)
        {
            if (owner.Violation is null && node.Member is PropertyInfo p && node.Expression is not null
                && EncryptedFieldCatalog.Find(p.DeclaringType!, p.Name) is { } hit)
                owner.Violation = $"{hit.Entity.Name}.{hit.Property}";
            return base.VisitMember(node);
        }

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            // EF.Property<T>(entity, "IqamaNumber")
            if (owner.Violation is null && node.Method.Name == "Property" && node.Method.DeclaringType == typeof(EF)
                && node.Arguments is [var entity, ConstantExpression { Value: string name }]
                && EncryptedFieldCatalog.Find(entity.Type, name) is { } hit)
                owner.Violation = $"{hit.Entity.Name}.{hit.Property}";
            return base.VisitMethodCall(node);
        }
    }
}

public static class FieldQueryExtensions
{
    /// <summary>Equality over an encrypted field via its blind index. Blank / unmatchable input matches nothing.</summary>
    public static IQueryable<T> WhereBlindIndex<T>(this IQueryable<T> source, DbContext db, TenantKeyRing ring,
        Expression<Func<T, string>> property, string? value) where T : class
    {
        var name = ((MemberExpression)property.Body).Member.Name;
        var field = EncryptedFieldCatalog.Find(typeof(T), name)
                    ?? throw new ArgumentException($"{typeof(T).Name}.{name} is not an encrypted field.");
        if (!field.BlindIndexed) throw new ArgumentException($"{typeof(T).Name}.{name} has no blind index.");
        var table = db.Model.FindEntityType(typeof(T))!.GetTableName()!;
        var bidx = FieldCipher.Index(ring, table, field, value);
        if (bidx is null) return source.Where(_ => false);
        return source.Where(e => EF.Property<byte[]>(e, field.BlindIndexProperty) == bidx);
    }
}
