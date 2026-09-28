namespace Side.Win.UI;

/// <summary>UI language (settings.ui_language). Port of SideLanguage.swift.</summary>
public enum SideLanguage { Ko, En }

public static class SideLanguageExtensions
{
    public static string L(this SideLanguage language, string english, string korean) =>
        language == SideLanguage.Ko ? korean : english;

    public static string WireValue(this SideLanguage language) => language == SideLanguage.Ko ? "ko" : "en";

    public static SideLanguage? Parse(string? value) => value switch
    {
        "ko" => SideLanguage.Ko,
        "en" => SideLanguage.En,
        _ => null,
    };
}
