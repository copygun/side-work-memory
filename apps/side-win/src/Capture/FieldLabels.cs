using System.Text;
using System.Text.RegularExpressions;

namespace Side.Win.Capture;

/// <summary>Field description used for privacy blocking (port of SideCaptureKit/FieldLabels.swift).</summary>
public sealed record FieldMetadata(
    string? Role = null, string? Title = null, string? Description = null, string? Placeholder = null,
    string? AriaName = null, string? Autocomplete = null, string? InputType = null)
{
    /// <summary>Role marker for UIA elements with IsPassword=true (AXSecureTextField analogue).</summary>
    public const string SecureRole = "secure";
}

public enum FieldBlockRule { SecureTextField, Autocomplete, InputType, Label }

public static class FieldLabels
{
    private static readonly HashSet<string> BlockedAutocomplete =
    [
        "current-password", "new-password", "one-time-code", "cc-number", "cc-csc",
        "cc-exp", "cc-exp-month", "cc-exp-year", "cc-name",
    ];
    private static readonly HashSet<string> BlockedInputTypes = ["password", "tel"];
    private static readonly Regex LabelPattern = new(
        @"(^|[^a-z])(cvv|cvc|csc|otp|one[- ]?time|pin|passcode|password|passwd|ssn|social security|security[- ]?code|card[- ]?number|카드\s*번호|비밀\s*번호|인증\s*번호|주민\s*(등록)?\s*번호|보안\s*코드)([^a-z]|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static FieldBlockRule? BlockedRule(FieldMetadata field)
    {
        if (field.Role == FieldMetadata.SecureRole) return FieldBlockRule.SecureTextField;
        if (field.Autocomplete is { } autocomplete &&
            autocomplete.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Any(BlockedAutocomplete.Contains))
            return FieldBlockRule.Autocomplete;
        if (field.InputType is { } inputType && BlockedInputTypes.Contains(inputType.ToLowerInvariant()))
            return FieldBlockRule.InputType;
        foreach (var label in new[] { field.Title, field.Description, field.Placeholder, field.AriaName })
            if (!string.IsNullOrEmpty(label) && LabelPattern.IsMatch(label)) return FieldBlockRule.Label;
        return null;
    }
}

/// <summary>Chord notation for shortcuts. Physical virtual-key codes only; typed characters are never read.</summary>
public static class Chord
{
    [Flags]
    public enum Modifiers { None = 0, Control = 1, Alt = 2, Shift = 4, Win = 8 }

    private static readonly Dictionary<uint, string> Keys = BuildKeys();

    private static Dictionary<uint, string> BuildKeys()
    {
        var keys = new Dictionary<uint, string>();
        for (uint vk = 0x41; vk <= 0x5A; vk++) keys[vk] = ((char)vk).ToString();
        for (uint vk = 0x30; vk <= 0x39; vk++) keys[vk] = ((char)vk).ToString();
        for (uint n = 1; n <= 12; n++) keys[0x6F + n] = $"F{n}";
        keys[0x0D] = "Enter"; keys[0x09] = "Tab"; keys[0x20] = "Space"; keys[0x08] = "Backspace";
        keys[0x1B] = "Esc"; keys[0x2E] = "Delete"; keys[0x25] = "Left"; keys[0x27] = "Right";
        keys[0x26] = "Up"; keys[0x28] = "Down"; keys[0x24] = "Home"; keys[0x23] = "End";
        keys[0x21] = "PageUp"; keys[0x22] = "PageDown";
        keys[0xBA] = ";"; keys[0xBB] = "="; keys[0xBC] = ","; keys[0xBD] = "-"; keys[0xBE] = ".";
        keys[0xBF] = "/"; keys[0xC0] = "`"; keys[0xDB] = "["; keys[0xDC] = "\\"; keys[0xDD] = "]"; keys[0xDE] = "'";
        return keys;
    }

    /// <summary>"Ctrl+Shift+S". A chord needs Ctrl, Alt or Win (Shift alone is typing).</summary>
    public static string? Notation(uint vk, Modifiers modifiers)
    {
        if ((modifiers & (Modifiers.Control | Modifiers.Alt | Modifiers.Win)) == 0) return null;
        if (!Keys.TryGetValue(vk, out var key)) return null;
        var builder = new StringBuilder();
        if (modifiers.HasFlag(Modifiers.Control)) builder.Append("Ctrl+");
        if (modifiers.HasFlag(Modifiers.Alt)) builder.Append("Alt+");
        if (modifiers.HasFlag(Modifiers.Shift)) builder.Append("Shift+");
        if (modifiers.HasFlag(Modifiers.Win)) builder.Append("Win+");
        return builder.Append(key).ToString();
    }
}

public static class TextLimits
{
    /// <summary>Longest prefix whose UTF-8 encoding fits <paramref name="maxBytes"/>, never splitting a code point.</summary>
    public static string PrefixUtf8(string text, int maxBytes)
    {
        var count = 0;
        var builder = new StringBuilder();
        foreach (var rune in text.EnumerateRunes())
        {
            var size = rune.Utf8SequenceLength;
            if (count + size > maxBytes) break;
            builder.Append(rune.ToString());
            count += size;
        }
        return builder.ToString();
    }
}
