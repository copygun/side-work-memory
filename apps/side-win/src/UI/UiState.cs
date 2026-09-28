using System.Text.Json;

namespace Side.Win.UI;

/// <summary>Well-known paths used by the UI layer.</summary>
public static class UiPaths
{
    /// <summary>Daemon data directory: SIDE_DATA_DIR or %APPDATA%\Side (matches src/config dataDirectory()).</summary>
    public static string DataDirectory =>
        Environment.GetEnvironmentVariable("SIDE_DATA_DIR") is { Length: > 0 } overridden
            ? overridden
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Side");

    public static string SocketPath => Path.Combine(DataDirectory, "run", "daemon.sock");

    /// <summary>Machine-local UI files (WebView2 profile, window geometry). Never synced.</summary>
    public static string LocalDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Side");

    public static string WebViewUserData => Path.Combine(LocalDirectory, "WebView2");

    public static string AssetPath(string name) => Path.Combine(AppContext.BaseDirectory, "Assets", name);
}

/// <summary>Small persisted UI preferences (UserDefaults analogue): window bounds, onboarding resume step.</summary>
public sealed class UiState
{
    public int? SettingsX { get; set; }
    public int? SettingsY { get; set; }
    public int? SettingsWidth { get; set; }
    public int? SettingsHeight { get; set; }
    public bool SettingsMaximized { get; set; }
    public string? OnboardingStep { get; set; }

    private static string FilePath => Path.Combine(UiPaths.LocalDirectory, "ui-state.json");
    private static readonly object Gate = new();

    public static UiState Load()
    {
        try
        {
            lock (Gate)
                return File.Exists(FilePath)
                    ? JsonSerializer.Deserialize<UiState>(File.ReadAllText(FilePath)) ?? new UiState()
                    : new UiState();
        }
        catch (Exception)
        {
            return new UiState();
        }
    }

    public void Save()
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(UiPaths.LocalDirectory);
                var temporary = FilePath + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(this));
                File.Move(temporary, FilePath, overwrite: true);
            }
        }
        catch (Exception)
        {
            // Preferences are best-effort.
        }
    }

    public static void Update(Action<UiState> change)
    {
        var state = Load();
        change(state);
        state.Save();
    }
}
