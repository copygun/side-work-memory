using Microsoft.Win32;

namespace Side.Win.UI;

/// <summary>
/// "Open at sign-in" via HKCU\Software\Microsoft\Windows\CurrentVersion\Run (port of LoginItem.swift).
/// Windows can additionally disable a Run entry from Task Manager > Startup apps; that state lives in
/// ...\Explorer\StartupApproved\Run and is reported as RequiresApproval.
/// </summary>
public sealed class LoginItem(string? executablePath = null, string valueName = "Side")
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    private readonly string _executablePath = executablePath ?? Environment.ProcessPath ?? Application.ExecutablePath;

    public bool IsEnabled { get; private set; }
    /// <summary>Registered, but switched off in Task Manager > Startup apps.</summary>
    public bool RequiresApproval { get; private set; }
    public string? ErrorMessage { get; private set; }

    public string Command => $"\"{_executablePath}\"";

    public void Refresh()
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey);
            var registered = run?.GetValue(valueName) is string value &&
                             string.Equals(value.Trim(), Command, StringComparison.OrdinalIgnoreCase);
            var disabledInTaskManager = false;
            using (var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey))
            {
                // First byte 0x02/0x06 = enabled, 0x03/0x07 = disabled by the user.
                if (approved?.GetValue(valueName) is byte[] { Length: > 0 } flags) disabledInTaskManager = (flags[0] & 1) == 1;
            }
            IsEnabled = registered;
            RequiresApproval = registered && disabledInTaskManager;
        }
        catch (Exception)
        {
            IsEnabled = false;
            RequiresApproval = false;
        }
    }

    public void SetEnabled(bool enabled)
    {
        try
        {
            using var run = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (enabled) run.SetValue(valueName, Command, RegistryValueKind.String);
            else run.DeleteValue(valueName, throwOnMissingValue: false);
            ErrorMessage = null;
        }
        catch (Exception)
        {
            ErrorMessage = "Could not update Open at sign-in.";
        }
        Refresh();
    }
}
