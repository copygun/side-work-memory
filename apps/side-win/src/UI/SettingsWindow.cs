using System.Diagnostics;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Side.Win.Core;

namespace Side.Win.UI;

public enum SettingsRoute { Settings, Today }

public static class SettingsRouteExtensions
{
    public static string Title(this SettingsRoute route, SideLanguage language) => route == SettingsRoute.Settings
        ? language.L("Side Settings", "Side 설정")
        : language.L("Today's summary — Side", "오늘의 요약 — Side");

    public static string Fragment(this SettingsRoute route, DateTime? now = null) => route == SettingsRoute.Settings
        ? "/settings/context-awareness"
        : $"/history/{(now ?? DateTime.Now):yyyy-MM-dd}";
}

/// <summary>Where a navigation may go (port of SettingsNavigationPolicy).</summary>
public sealed class SettingsNavigationPolicy(int localPort)
{
    public bool IsLocal(Uri? url) =>
        url is not null && url.Scheme == Uri.UriSchemeHttp && url.Host == "127.0.0.1" && url.Port == localPort &&
        string.IsNullOrEmpty(url.UserInfo);

    public static bool IsExternalHttp(Uri? url) =>
        url is not null && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps) && url.Host.Length > 0;

    /// <summary>Windows Settings deep links the web UI may show as permission help.</summary>
    public static bool IsSystemSettings(Uri? url) =>
        url is not null && url.Scheme.Equals("ms-settings", StringComparison.OrdinalIgnoreCase) &&
        url.AbsoluteUri.StartsWith("ms-settings:privacy", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Settings/"today" window hosting the daemon web UI in WebView2 (port of SettingsWindow.swift).
/// Authentication matches macOS: the first request carries "Authorization: Bearer" and a one-time
/// ?t= bootstrap parameter which the page removes from the address and keeps in memory. Nothing is
/// persisted; the session is dropped as soon as the daemon stops.
/// </summary>
public sealed class SettingsWindow : IDisposable
{
    private Form? _form;
    private WebView2? _webView;
    private Label? _message;
    private WebSession? _session;
    private SettingsRoute _route = SettingsRoute.Settings;
    private SideLanguage _language = SideLanguage.Ko;
    private bool _webViewReady;
    private Task? _initializing;

    public void UpdateLanguage(SideLanguage language)
    {
        _language = language;
        if (_form is null) return;
        _form.Text = _route.Title(language);
        if (_session is null) ShowUnavailable();
    }

    public void Accept(WebSession? session)
    {
        _session = session;
        if (_form is null || _form.IsDisposed) return;
        if (session is null)
        {
            if (_webViewReady) _webView?.CoreWebView2?.Stop();
            ShowUnavailable();
        }
        else if (_form.Visible)
        {
            Navigate();
        }
    }

    public void Open(SettingsRoute route, SideLanguage language)
    {
        _route = route;
        _language = language;
        var form = _form is { IsDisposed: false } existing ? existing : MakeForm();
        form.Text = route.Title(language);
        if (_session is null) ShowUnavailable();
        else Navigate();
        if (!form.Visible) form.Show();
        if (form.WindowState == FormWindowState.Minimized) form.WindowState = FormWindowState.Normal;
        form.Activate();
        form.BringToFront();
    }

    private Form MakeForm()
    {
        var state = UiState.Load();
        var form = new Form
        {
            Text = _route.Title(_language),
            Icon = TrayIcons.AppIcon(),
            StartPosition = FormStartPosition.Manual,
            AutoScaleMode = AutoScaleMode.Dpi,
        };
        // Defaults are logical (96 dpi) sizes; saved bounds are already device pixels.
        form.MinimumSize = form.LogicalToDeviceUnits(new Size(720, 520));
        form.Size = state.SettingsWidth is { } width && state.SettingsHeight is { } height
            ? new Size(width, height)
            : form.LogicalToDeviceUnits(new Size(980, 720));
        var screen = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);
        var location = state.SettingsX is { } x && state.SettingsY is { } y ? new Point(x, y) : Point.Empty;
        form.Location = location != Point.Empty && Screen.AllScreens.Any(s => s.WorkingArea.Contains(location))
            ? location
            : new Point(screen.Left + (screen.Width - form.Width) / 2, screen.Top + (screen.Height - form.Height) / 2);
        if (state.SettingsMaximized) form.WindowState = FormWindowState.Maximized;

        _message = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 11f),
            Visible = false,
        };
        _webView = new WebView2 { Dock = DockStyle.Fill, Visible = false };
        form.Controls.Add(_webView);
        form.Controls.Add(_message);
        // Hide instead of dispose so reopening is instant and the WebView2 environment is reused.
        form.FormClosing += (_, e) =>
        {
            SaveBounds(form);
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                form.Hide();
            }
        };
        form.ResizeEnd += (_, _) => SaveBounds(form);
        _form = form;
        return form;
    }

    private static void SaveBounds(Form form)
    {
        var bounds = form.WindowState == FormWindowState.Normal ? form.Bounds : form.RestoreBounds;
        UiState.Update(state =>
        {
            state.SettingsX = bounds.X;
            state.SettingsY = bounds.Y;
            state.SettingsWidth = bounds.Width;
            state.SettingsHeight = bounds.Height;
            state.SettingsMaximized = form.WindowState == FormWindowState.Maximized;
        });
    }

    private void ShowUnavailable(string? detail = null)
    {
        if (_message is null || _webView is null) return;
        _message.Text = detail ?? _language.L(
            "Settings are unavailable right now.\nSide is starting or restarting. Try again in a few seconds from the tray icon.",
            "현재 설정을 열 수 없습니다.\nSide가 시작 또는 재시작 중입니다. 잠시 후 트레이 아이콘에서 다시 여세요.");
        _message.Visible = true;
        _webView.Visible = false;
    }

    private async void Navigate()
    {
        if (_session is not { } session || _webView is null || _message is null) return;
        try
        {
            await EnsureWebViewAsync();
        }
        catch (Exception)
        {
            ShowUnavailable(_language.L(
                "Microsoft Edge WebView2 Runtime is required to show Settings.\nInstall it from https://go.microsoft.com/fwlink/p/?LinkId=2124703 and reopen Settings.",
                "설정 화면을 표시하려면 Microsoft Edge WebView2 런타임이 필요합니다.\nhttps://go.microsoft.com/fwlink/p/?LinkId=2124703 에서 설치한 뒤 설정을 다시 여세요."));
            return;
        }
        if (_session != session) return;
        var target = new Uri(session.BaseUri, $"?t={session.Token}#{_route.Fragment()}");
        NavigateAuthenticated(session, target);
        _message.Visible = false;
        _webView.Visible = true;
    }

    private void NavigateAuthenticated(WebSession session, Uri url)
    {
        var core = _webView!.CoreWebView2;
        var bootstrap = WithToken(url, session.Token);
        var request = core.Environment.CreateWebResourceRequest(
            bootstrap.AbsoluteUri, "GET", null, $"Authorization: Bearer {session.Token}\r\nCache-Control: no-cache");
        core.NavigateWithWebResourceRequest(request);
    }

    private static Uri WithToken(Uri url, string token)
    {
        var builder = new UriBuilder(url);
        var query = System.Web.HttpUtility.ParseQueryString(builder.Query);
        query.Remove("t");
        query["t"] = token;
        builder.Query = query.ToString();
        return builder.Uri;
    }

    private Task EnsureWebViewAsync()
    {
        if (_webViewReady) return Task.CompletedTask;
        return _initializing ??= InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            Directory.CreateDirectory(UiPaths.WebViewUserData);
            var environment = await CoreWebView2Environment.CreateAsync(null, UiPaths.WebViewUserData);
            await _webView!.EnsureCoreWebView2Async(environment);
            var core = _webView.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = true;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.NavigationStarting += OnNavigationStarting;
            core.NewWindowRequested += OnNewWindowRequested;
            _webViewReady = true;
        }
        catch
        {
            _initializing = null;
            throw;
        }
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (_session is not { } session)
        {
            e.Cancel = true;
            return;
        }
        Uri.TryCreate(e.Uri, UriKind.Absolute, out var url);
        var policy = new SettingsNavigationPolicy(session.Port);
        if (policy.IsLocal(url)) return;
        e.Cancel = true;
        if (!e.IsUserInitiated) return;
        if (SettingsNavigationPolicy.IsSystemSettings(url)) OpenExternal(url!);
        else if (SettingsNavigationPolicy.IsExternalHttp(url) && !e.Uri.Contains(session.Token, StringComparison.Ordinal))
            OpenExternal(url!);
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (_session is not { } session || !e.IsUserInitiated) return;
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var url)) return;
        var policy = new SettingsNavigationPolicy(session.Port);
        if (policy.IsLocal(url)) NavigateAuthenticated(session, url);
        else if (SettingsNavigationPolicy.IsSystemSettings(url)) OpenExternal(url);
        else if (SettingsNavigationPolicy.IsExternalHttp(url) && !e.Uri.Contains(session.Token, StringComparison.Ordinal))
            OpenExternal(url);
    }

    internal static void OpenExternal(Uri url)
    {
        try { Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception) { /* no handler */ }
    }

    public void Dispose()
    {
        _webView?.Dispose();
        _form?.Dispose();
    }
}
