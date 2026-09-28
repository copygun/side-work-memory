using System.Runtime.InteropServices;

namespace Side.Win.Capture;

// Minimal hand-declared COM interop for UIAutomationClient (UIA3). Declaring it ourselves keeps the
// project free of the WPF-only System.Windows.Automation (UIA2 proxy) assemblies. Vtable order must
// match UIAutomationClient.h exactly; unused slots are placeholders that are never called.

[ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomation
{
    int CompareElements(IUIAutomationElement a, IUIAutomationElement b);
    void _CompareRuntimeIds();
    IUIAutomationElement GetRootElement();
    IUIAutomationElement? ElementFromHandle(IntPtr hwnd);
    IUIAutomationElement? ElementFromPoint(Native.POINT point);
    IUIAutomationElement? GetFocusedElement();
    void _GetRootElementBuildCache();
    void _ElementFromHandleBuildCache();
    void _ElementFromPointBuildCache();
    void _GetFocusedElementBuildCache();
    void _CreateTreeWalker();
    IUIAutomationTreeWalker get_ControlViewWalker();
    IUIAutomationTreeWalker get_ContentViewWalker();
    IUIAutomationTreeWalker get_RawViewWalker();
}

[ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomationElement
{
    void _SetFocus();
    void _GetRuntimeId();
    void _FindFirst();
    void _FindAll();
    void _FindFirstBuildCache();
    void _FindAllBuildCache();
    void _BuildUpdatedCache();
    [return: MarshalAs(UnmanagedType.Struct)] object? GetCurrentPropertyValue(int propertyId);
    void _GetCurrentPropertyValueEx();
    void _GetCachedPropertyValue();
    void _GetCachedPropertyValueEx();
    void _GetCurrentPatternAs();
    void _GetCachedPatternAs();
    [return: MarshalAs(UnmanagedType.IUnknown)] object? GetCurrentPattern(int patternId);
    void _GetCachedPattern();
    void _GetCachedParent();
    void _GetCachedChildren();
    int get_CurrentProcessId();
    int get_CurrentControlType();
    [return: MarshalAs(UnmanagedType.BStr)] string? get_CurrentLocalizedControlType();
    [return: MarshalAs(UnmanagedType.BStr)] string? get_CurrentName();
    [return: MarshalAs(UnmanagedType.BStr)] string? get_CurrentAcceleratorKey();
    [return: MarshalAs(UnmanagedType.BStr)] string? get_CurrentAccessKey();
    int get_CurrentHasKeyboardFocus();
    int get_CurrentIsKeyboardFocusable();
    int get_CurrentIsEnabled();
    [return: MarshalAs(UnmanagedType.BStr)] string? get_CurrentAutomationId();
    [return: MarshalAs(UnmanagedType.BStr)] string? get_CurrentClassName();
    [return: MarshalAs(UnmanagedType.BStr)] string? get_CurrentHelpText();
    int get_CurrentCulture();
    int get_CurrentIsControlElement();
    int get_CurrentIsContentElement();
    int get_CurrentIsPassword();
    IntPtr get_CurrentNativeWindowHandle();
}

[ComImport, Guid("4042c624-389c-4afc-a630-9df854a541fc"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomationTreeWalker
{
    IUIAutomationElement? GetParentElement(IUIAutomationElement element);
    IUIAutomationElement? GetFirstChildElement(IUIAutomationElement element);
    IUIAutomationElement? GetLastChildElement(IUIAutomationElement element);
    IUIAutomationElement? GetNextSiblingElement(IUIAutomationElement element);
}

[ComImport, Guid("32eba289-3583-42c9-9c59-3b6d9a1e9b6a"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomationTextPattern
{
    void _RangeFromPoint();
    void _RangeFromChild();
    IUIAutomationTextRangeArray? GetSelection();
}

[ComImport, Guid("ce4ae76a-e717-4c98-81ea-47371d028eb6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomationTextRangeArray
{
    int get_Length();
    IUIAutomationTextRange? GetElement(int index);
}

[ComImport, Guid("a543cc6a-f4ae-494b-8239-c814481187a8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomationTextRange
{
    void _Clone();
    void _Compare();
    void _CompareEndpoints();
    void _ExpandToEnclosingUnit();
    void _FindAttribute();
    void _FindText();
    void _GetAttributeValue();
    void _GetBoundingRectangles();
    void _GetEnclosingElement();
    [return: MarshalAs(UnmanagedType.BStr)] string? GetText(int maxLength);
}

/// <summary>UIA ids (UIAutomationClient.h) and small safe accessors.</summary>
internal static class Uia
{
    public const int ControlTypeButton = 50000;
    public const int ControlTypeEdit = 50004;
    public const int ControlTypeHyperlink = 50005;
    public const int ControlTypeText = 50020;
    public const int ControlTypeDocument = 50030;
    public const int ControlTypeToolBar = 50021;

    public const int PropValueValue = 30045;
    public const int PropValueIsReadOnly = 30046;
    public const int PropIsValuePatternAvailable = 30043;
    public const int PropAriaProperties = 30102;
    public const int PropAriaRole = 30101;
    public const int PropFullDescription = 30159;
    public const int PatternText = 10014;

    private static readonly Guid ClsidCUIAutomation8 = new("e22ad333-b25f-460c-83d0-0581107395c9");
    private static readonly Guid ClsidCUIAutomation = new("ff48dba4-60ef-4201-aa87-54103eef594e");

    private static readonly Lazy<IUIAutomation?> Instance = new(Create, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Shared client. Created from an MTA thread so every worker/thread-pool thread may use it.</summary>
    public static IUIAutomation? Client => Instance.Value;

    private static IUIAutomation? Create()
    {
        IUIAutomation? Make()
        {
            foreach (var clsid in new[] { ClsidCUIAutomation8, ClsidCUIAutomation })
            {
                try
                {
                    var type = Type.GetTypeFromCLSID(clsid, throwOnError: false);
                    if (type is not null && Activator.CreateInstance(type) is IUIAutomation automation) return automation;
                }
                catch (Exception) { }
            }
            return null;
        }
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.MTA) return Make();
        IUIAutomation? result = null;
        var thread = new Thread(() => result = Make()) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        thread.Join();
        return result;
    }

    public static T? Try<T>(Func<T?> read)
    {
        try { return read(); }
        catch (Exception) { return default; }
    }

    public static string? Name(IUIAutomationElement e) => Try(e.get_CurrentName);
    public static int ControlType(IUIAutomationElement e) => Try(e.get_CurrentControlType);
    public static bool IsPassword(IUIAutomationElement e) => Try(e.get_CurrentIsPassword) != 0;
    public static bool HasFocus(IUIAutomationElement e) => Try(e.get_CurrentHasKeyboardFocus) != 0;
    public static int ProcessId(IUIAutomationElement e) => Try(e.get_CurrentProcessId);

    public static string? StringProperty(IUIAutomationElement e, int id) =>
        Try(() => e.GetCurrentPropertyValue(id)) as string;

    public static bool? BoolProperty(IUIAutomationElement e, int id) =>
        Try(() => e.GetCurrentPropertyValue(id)) is bool value ? value : null;

    public static string? Value(IUIAutomationElement e) =>
        BoolProperty(e, PropIsValuePatternAvailable) == true ? StringProperty(e, PropValueValue) : null;

    /// <summary>Editable text control (Windows analogue of AXTextField/AXTextArea).</summary>
    public static bool IsTextField(IUIAutomationElement e)
    {
        var type = ControlType(e);
        if (type != ControlTypeEdit && type != ControlTypeDocument) return false;
        return BoolProperty(e, PropIsValuePatternAvailable) == true && BoolProperty(e, PropValueIsReadOnly) == false;
    }

    public static FieldMetadata Metadata(IUIAutomationElement e)
    {
        var aria = StringProperty(e, PropAriaProperties);
        string? AriaValue(string key)
        {
            if (string.IsNullOrEmpty(aria)) return null;
            foreach (var pair in aria.Split(';'))
            {
                var index = pair.IndexOf('=');
                if (index > 0 && pair[..index].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                    return pair[(index + 1)..].Trim();
            }
            return null;
        }
        var helpText = Try(e.get_CurrentHelpText);
        return new FieldMetadata(
            Role: IsPassword(e) ? FieldMetadata.SecureRole : ControlType(e).ToString(),
            Title: Name(e),
            Description: StringProperty(e, PropFullDescription) ?? helpText,
            Placeholder: helpText,
            AriaName: null,
            Autocomplete: AriaValue("autocomplete"),
            InputType: AriaValue("type"));
    }

    public static string? SelectedText(IUIAutomationElement e, int maxChars)
    {
        try
        {
            if (e.GetCurrentPattern(PatternText) is not IUIAutomationTextPattern pattern) return null;
            var ranges = pattern.GetSelection();
            if (ranges is null || ranges.get_Length() < 1) return null;
            return ranges.GetElement(0)?.GetText(maxChars);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
