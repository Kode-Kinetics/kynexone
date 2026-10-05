using System.Runtime.CompilerServices;
using Zayra.Api.Infrastructure.Auth;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// Default-constructed password hashers in this test assembly use 1,000 PBKDF2 iterations instead of
/// the production 600,000. Thousands of tests hash fixture passwords; at the production work factor
/// the CPU load made timing-sensitive Postgres tests (advisory-lock keepalive) flaky. Tests that are
/// ABOUT the work factor or the rehash path build hashers with an explicit iteration count.
/// </summary>
internal static class CheapTestPasswordHashing
{
    [ModuleInitializer]
    internal static void UseCheapHashing() => Pbkdf2PasswordHasher.DefaultIterationsOverride = 1_000;
}
