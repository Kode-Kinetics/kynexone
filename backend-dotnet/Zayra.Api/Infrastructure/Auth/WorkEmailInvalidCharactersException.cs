namespace Zayra.Api.Infrastructure.Auth;

/// <summary>
/// A work email whose local part (before the '@') uses anything but ASCII letters, digits, '.', '_' or '-' (review P3).
/// The work email is the login's username: look-alike Unicode, quotes or spaces in it make two usernames that read the
/// same, and slips that cannot be typed. Refused on every path that sets a work email (create, update, PATCH, draft
/// approval, import, backfill) with 422 <see cref="Code"/>; '+' keeps its own code (<see cref="WorkEmailPlusAddressException"/>).
/// An existing address is left alone until it is changed.
/// </summary>
public sealed class WorkEmailInvalidCharactersException : Exception
{
    public const string Code = "work_email_invalid_characters";
    public const string Text = "Work email can only use English letters, digits, dots, hyphens and underscores before the @.";

    public WorkEmailInvalidCharactersException() : base(Text) { }

    /// <summary>True when the address is non-blank and its local part has a character outside [A-Za-z0-9._-] (or is empty).
    /// A '+' counts as invalid here too; callers check plus-addressing first so it keeps its own code.</summary>
    public static bool IsInvalid(string? workEmail)
    {
        var value = (workEmail ?? string.Empty).Trim();
        if (value.Length == 0) return false;
        var at = value.IndexOf('@');
        var local = at < 0 ? value : value[..at];
        if (local.Length == 0) return true;
        foreach (var ch in local)
            if (!(ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-')) return true;
        return false;
    }

    /// <summary>Plus-addressing first (its own code), then the character rule.</summary>
    public static void ThrowIfNotAllowed(string? workEmail)
    {
        WorkEmailPlusAddressException.ThrowIfPlusAddressed(workEmail);
        if (IsInvalid(workEmail)) throw new WorkEmailInvalidCharactersException();
    }
}
