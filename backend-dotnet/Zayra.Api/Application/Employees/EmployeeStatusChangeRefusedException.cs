namespace Zayra.Api.Application.Employees;

/// <summary>
/// A status change was refused by a business rule of the status change itself (an effective date before the joining date, an open
/// separation with a live final settlement). Derives from <see cref="InvalidOperationException"/> so every existing caller still
/// refuses it as before; callers that must tell a rule refusal from an unrelated failure (undoing a merge) catch this type only.
/// </summary>
public sealed class EmployeeStatusChangeRefusedException(string message) : InvalidOperationException(message);
