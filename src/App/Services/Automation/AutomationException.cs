namespace IPhoneMirror.App.Services.Automation;

internal sealed class AutomationException(string code, string message, int status = 409) : Exception(message)
{
    internal string Code { get; } = code;
    internal int Status { get; } = status;
}
