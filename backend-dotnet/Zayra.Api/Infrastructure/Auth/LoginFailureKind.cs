namespace Zayra.Api.Infrastructure.Auth;

/// <summary>
/// Tags a sign-in refusal as "no such account" without changing the exception type or message the
/// client sees (both stay identical to a wrong password). Only the per-IP failure budget reads it:
/// failures against accounts that do not exist are the spraying signal; wrong passwords on real
/// accounts are already bounded per account.
/// </summary>
public static class LoginFailureKind
{
    private const string Key = "kx.login.unknown_account";

    public static void MarkUnknownAccount(Exception ex) => ex.Data[Key] = true;

    public static bool IsUnknownAccount(Exception ex) => ex.Data[Key] is true;
}
