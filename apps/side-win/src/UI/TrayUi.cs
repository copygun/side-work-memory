using System.Reflection;
using Side.Win.Core;

namespace Side.Win.UI;

/// <summary>
/// Notification-area icon + menu, settings window and first-run window (port of SideApp.swift,
/// MenuBar.swift and OnboardingWindowController.swift). Must be constructed on the WinForms UI
/// thread; IUiLayer members may be called from any thread and are marshalled onto it.
/// </summary>
public sealed class TrayUi : IUiLayer, IDisposable
{
    private const int OnboardingStartupAttempts = 30;

    private readonly Action _retryKeyStore;
    private readonly Func<Task> _quitAsync;
    private readonly DaemonRpcClient _rpc;
    private readonly Control _marshal;
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly MenuState _state;
    private readonly LoginItem _loginItem;
    private readonly SettingsWindow _settings = new();
    private readonly System.Windows.Forms.Timer _poll;
    private SupervisorState _supervisorState = new SupervisorState.Stopped();
    private OnboardingForm? _onboarding;
    private OnboardingFlow? _onboardingFlow;
    private bool _onboardingStarted;
    private bool _quitting;

    /// <param name="retryKeyStore">DaemonSupervisor.RetryKeyStore (menu item when the key store is locked).</param>
    /// <param name="quitAsync">SideRuntime.QuitAsync; the tray exits the message loop after it completes.</param>
    /// <param name="rpc">Daemon JSON-RPC client; defaults to the socket under the data directory.</param>
    /// <param name="loginItem">Run-key registration; defaults to this executable.</param>
    public TrayUi(Action retryKeyStore, Func<Task> quitAsync, DaemonRpcClient? rpc = null, LoginItem? loginItem = null)
    {
        _retryKeyStore = retryKeyStore;
        _quitAsync = quitAsync;
        _rpc = rpc ?? new DaemonRpcClient();
        _loginItem = loginItem ?? new LoginItem();
        _state = new MenuState(_rpc);
        _marshal = new Control();
        _marshal.CreateControl();
        _ = _marshal.Handle; // force handle creation on the UI thread

        _menu = new ContextMenuStrip { ShowImageMargin = false, Font = new Font("Segoe UI", 9.5f) };
        _menu.Opening += (_, _) =>
        {
            _loginItem.Refresh();
            RebuildMenu();
            _ = OpenedAsync();
        };
        _icon = new NotifyIcon
        {
            Icon = TrayIcons.Tray(MenuGlyph.Warning),
            Text = "Side",
            ContextMenuStrip = _menu,
            Visible = false,
        };
        _icon.MouseUp += (_, e) =>
        {
            // Left click opens the same menu, like a macOS menu bar extra.
            if (e.Button == MouseButtons.Left)
                typeof(NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(_icon, null);
        };
        _icon.DoubleClick += (_, _) => _settings.Open(SettingsRoute.Settings, _state.Language);
        _state.Changed += () => OnUi(UpdateIcon);
        _poll = new System.Windows.Forms.Timer { Interval = (int)MenuState.CacheDuration.TotalMilliseconds };
        _poll.Tick += async (_, _) => await _state.RefreshAsync();
    }

    /// <summary>Show the icon, start status polling and the first-run check. Call once after the runtime started.</summary>
    public void Start()
    {
        _icon.Visible = true;
        UpdateIcon();
        _poll.Start();
        _ = _state.RefreshAsync(force: true);
        StartOnboarding();
    }

    private async Task OpenedAsync()
    {
        await _state.MenuDidOpenAsync();
        if (_menu.Visible) RebuildMenu();
    }

    // --- IUiLayer ------------------------------------------------------------------------

    public void OnSupervisorStateChanged(SupervisorState state) => OnUi(() =>
    {
        _supervisorState = state;
        UpdateIcon();
        if (_menu.Visible) RebuildMenu();
        if (state.IsRunning) _ = RefreshSoonAsync();
    });

    public void OnWebSessionChanged(WebSession? session) => OnUi(() => _settings.Accept(session));

    public void OpenSettings() => OnUi(() => _settings.Open(SettingsRoute.Settings, _state.Language));

    public void ShowPermissionHelp(PermissionKind kind) => OnUi(() =>
    {
        var target = kind == PermissionKind.ScreenRecording ? "ms-settings:privacy-graphicscaptureprogrammatic" : "ms-settings:privacy";
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception)
        {
            SettingsWindow.OpenExternal(new Uri("ms-settings:privacy"));
        }
    });

    // --- menu ----------------------------------------------------------------------------

    private SideLanguage Lang => _state.Language;

    private MenuDisplay CurrentDisplay =>
        _supervisorState is SupervisorState.KeyStoreLocked or SupervisorState.CaptureNotRunning
            ? MenuDisplay.NotRunning(Lang)
            : _state.Display;

    private void UpdateIcon()
    {
        var display = CurrentDisplay;
        _icon.Icon = TrayIcons.Tray(display.Glyph);
        var text = $"Side — {display.Text}";
        _icon.Text = text.Length > 127 ? text[..127] : text;
    }

    private void RebuildMenu()
    {
        var display = CurrentDisplay;
        _menu.SuspendLayout();
        _menu.Items.Clear();

        _menu.Items.Add(new ToolStripLabel(display.Text) { Font = new Font(_menu.Font, FontStyle.Bold), Margin = new Padding(4, 4, 4, 2) });
        if (_supervisorState is SupervisorState.KeyStoreLocked)
            _menu.Items.Add(Item(Lang.L("Unlock credentials…", "자격 증명 허용…"), () => _retryKeyStore()));
        _menu.Items.Add(new ToolStripSeparator());

        var pause = new ToolStripMenuItem(Lang.L("Pause", "일시정지")) { Enabled = !_state.IsWorking };
        foreach (var option in Enum.GetValues<MenuPauseOption>())
            pause.DropDownItems.Add(Item(option.Title(Lang), () => _ = PauseAsync(option)));
        _menu.Items.Add(pause);
        if (display.IsPaused)
            _menu.Items.Add(Item(Lang.L("Resume", "재개"), () => _ = ResumeAsync(), !_state.IsWorking));
        _menu.Items.Add(new ToolStripSeparator());

        _menu.Items.Add(Item(Lang.L("Today's summary…", "오늘의 요약…"), () => _settings.Open(SettingsRoute.Today, Lang)));
        _menu.Items.Add(Item(Lang.L("Settings…", "설정…"), () => _settings.Open(SettingsRoute.Settings, Lang)));
        if (_onboardingFlow is { IsFinished: false, IsLoaded: true })
            _menu.Items.Add(Item(Lang.L("Continue setup…", "설정 마저 하기…"), ShowOnboarding));
        var login = new ToolStripMenuItem(Lang.L("Open at sign-in", "로그인 시 열기"))
        {
            Checked = _loginItem.IsEnabled,
            CheckOnClick = false,
        };
        login.Click += (_, _) => _loginItem.SetEnabled(!_loginItem.IsEnabled);
        _menu.Items.Add(login);
        if (_loginItem.RequiresApproval)
            _menu.Items.Add(Note(Lang.L("Turn Side on in Task Manager → Startup apps",
                "작업 관리자 → 시작 앱에서 Side를 사용으로 바꾸세요")));
        if (_loginItem.ErrorMessage is not null)
            _menu.Items.Add(Note(Lang.L("Could not update Open at sign-in. Try again.",
                "로그인 시 열기 설정을 바꿀 수 없습니다. 다시 시도하세요.")));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(Item(Lang.L("Quit Side", "Side 종료"), () => _ = QuitAsync()));
        _menu.ResumeLayout();
    }

    private static ToolStripMenuItem Item(string text, Action onClick, bool enabled = true)
    {
        var item = new ToolStripMenuItem(text) { Enabled = enabled };
        item.Click += (_, _) => onClick();
        return item;
    }

    private static ToolStripLabel Note(string text) =>
        new(text) { ForeColor = Color.FromArgb(96, 96, 104), Margin = new Padding(4, 0, 4, 2) };

    private async Task PauseAsync(MenuPauseOption option)
    {
        await _state.PauseAsync(option);
        UpdateIcon();
    }

    private async Task ResumeAsync()
    {
        await _state.ResumeAsync();
        UpdateIcon();
    }

    private async Task RefreshSoonAsync()
    {
        // The daemon needs a moment after spawn before its socket answers.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            await _state.RefreshAsync(force: true);
            if (!ReferenceEquals(_state.Display, MenuDisplay.NotRunning(Lang)) &&
                _state.Display.Glyph != MenuGlyph.Warning) return;
        }
    }

    private async Task QuitAsync()
    {
        if (_quitting) return;
        _quitting = true;
        _poll.Stop();
        _onboarding?.Close();
        try { await _quitAsync(); }
        catch (Exception) { /* exit anyway */ }
        _icon.Visible = false;
        Application.ExitThread();
    }

    // --- onboarding ------------------------------------------------------------------------

    private void StartOnboarding()
    {
        if (_onboardingStarted) return;
        _onboardingStarted = true;
        var flow = new OnboardingFlow(_rpc);
        _onboardingFlow = flow;
        flow.LanguageChanged += language => OnUi(() =>
        {
            _state.SetLanguage(language);
            _settings.UpdateLanguage(language);
        });
        flow.Completed += () => OnUi(() => _ = _state.RefreshAsync(force: true));
        _ = LoadOnboardingAsync(flow);
    }

    private async Task LoadOnboardingAsync(OnboardingFlow flow)
    {
        for (var attempt = 0; attempt < OnboardingStartupAttempts && !_quitting; attempt++)
        {
            try
            {
                await flow.LoadAsync();
                break;
            }
            catch (Exception)
            {
                await Task.Delay(TimeSpan.FromSeconds(1));
            }
        }
        if (_quitting || flow.IsFinished) return;
        ShowOnboarding();
    }

    private void ShowOnboarding()
    {
        if (_onboardingFlow is null || _onboardingFlow.IsFinished) return;
        if (_onboarding is { IsDisposed: false })
        {
            _onboarding.Activate();
            return;
        }
        _onboarding = new OnboardingForm(_onboardingFlow);
        _onboarding.FormClosed += (_, _) => _onboarding = null;
        _onboarding.Show();
        _onboarding.Activate();
    }

    // --- threading -----------------------------------------------------------------------

    private void OnUi(Action action)
    {
        if (_marshal.IsDisposed) return;
        if (_marshal.InvokeRequired) _marshal.BeginInvoke(action);
        else action();
    }

    public void Dispose()
    {
        _poll.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        _settings.Dispose();
        _marshal.Dispose();
    }
}
