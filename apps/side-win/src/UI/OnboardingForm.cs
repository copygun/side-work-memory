namespace Side.Win.UI;

/// <summary>
/// First-run window (port of OnboardingView.swift). Layout: title → one-line explanation →
/// step body → one primary button (+ secondary actions) → status/error line.
/// </summary>
public sealed class OnboardingForm : Form
{
    private static readonly Color Accent = Color.FromArgb(0, 103, 192);
    private static readonly Color Secondary = Color.FromArgb(96, 96, 104);
    private static readonly Color ErrorColor = Color.FromArgb(196, 43, 28);
    private static readonly Color RequiredTint = Color.FromArgb(240, 246, 255);

    private readonly OnboardingFlow _flow;
    private readonly ProviderDraft _draft = new();
    private readonly Label _title = new() { AutoSize = true, Font = new Font("Segoe UI", 16f, FontStyle.Bold), Dock = DockStyle.Left };
    private readonly ComboBox _languagePicker = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
    private readonly Label _languageLabel = new() { AutoSize = true, Margin = new Padding(0, 7, 6, 0) };
    private readonly FlowLayoutPanel _body = new()
    {
        Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true,
        Padding = new Padding(0, 8, 0, 0),
    };
    private readonly Label _error = new() { AutoSize = true, ForeColor = ErrorColor, MaximumSize = new Size(580, 0) };
    private readonly System.Windows.Forms.Timer _poll = new() { Interval = 1000 };
    private OnboardingStep? _renderedStep;
    private SideLanguage? _renderedLanguage;
    private bool _suppressLanguageEvent;

    public OnboardingForm(OnboardingFlow flow)
    {
        _flow = flow;
        Text = WindowTitle(flow.Language);
        Icon = TrayIcons.AppIcon();
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 10f);
        BackColor = Color.White;
        // Sizes below are logical (96 dpi) units scaled to the monitor, so 125-200% displays fit.
        ClientSize = new Size(S(680), S(500));
        Padding = new Padding(S(28), S(20), S(28), S(16));
        _error.MaximumSize = new Size(ContentWidth, 0);

        _languagePicker.Items.AddRange(["한국어", "English"]);
        _languagePicker.SelectedIndexChanged += async (_, _) =>
        {
            if (_suppressLanguageEvent) return;
            await _flow.SetLanguageAsync(_languagePicker.SelectedIndex == 0 ? SideLanguage.Ko : SideLanguage.En);
        };

        // Language picker sits on its own row above the title so long titles never wrap.
        var languageRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false,
        };
        languageRow.Controls.Add(_languagePicker);
        languageRow.Controls.Add(_languageLabel);
        var header = new Panel { Dock = DockStyle.Top, Height = S(50) };
        header.Controls.Add(_title);

        var footer = new Panel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(0, 8, 0, 0) };
        footer.Controls.Add(_error);

        Controls.Add(_body);
        Controls.Add(footer);
        Controls.Add(header);
        Controls.Add(languageRow);

        _flow.Changed += () => BeginInvokeIfNeeded(Render);
        _flow.Completed += () => BeginInvokeIfNeeded(Close);
        _poll.Tick += async (_, _) => await _flow.PollPermissionsAsync();
        _poll.Start();
        FormClosed += (_, _) => _poll.Dispose();
        // The macOS window has no close button until setup finishes; here closing just hides it and
        // setup resumes from the saved step next launch (or from the tray).
        Render();
    }

    private void BeginInvokeIfNeeded(Action action)
    {
        if (IsDisposed) return;
        if (InvokeRequired) BeginInvoke(action);
        else action();
    }

    private SideLanguage Lang => _flow.Language;

    private int S(int logical) => LogicalToDeviceUnits(logical);

    /// <summary>Usable width inside the padding, minus room for a vertical scrollbar.</summary>
    private int ContentWidth => ClientSize.Width - Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - S(4);

    /// <summary>Distinct from the Settings window title ("Side 설정") so the two are never confused.</summary>
    public static string WindowTitle(SideLanguage language) => language.L("Set up Side", "Side 시작 설정");

    private void Render()
    {
        if (IsDisposed) return;
        Text = WindowTitle(Lang);
        _title.Text = _flow.Step == OnboardingStep.Intro
            ? Lang.L("Let Side remember your day", "Side가 하루를 기억하도록")
            : WindowTitle(Lang);
        _languageLabel.Text = Lang.L("Language", "언어");
        _suppressLanguageEvent = true;
        _languagePicker.SelectedIndex = Lang == SideLanguage.Ko ? 0 : 1;
        _languagePicker.Enabled = _flow.IsLoaded && !_flow.IsWorking;
        _suppressLanguageEvent = false;
        _error.Text = _flow.ErrorMessage ?? (_flow.IsWorking ? Lang.L("Working…", "처리 중…") : "");
        _error.ForeColor = _flow.ErrorMessage is null ? Secondary : ErrorColor;

        // Rebuild the body only when the step or language changes, so typed text survives re-renders.
        var key = _flow.IsLoaded ? _flow.Step : (OnboardingStep?)null;
        if (key != _renderedStep || Lang != _renderedLanguage || !_flow.IsLoaded)
        {
            _renderedStep = key;
            _renderedLanguage = Lang;
            BuildBody();
        }
        foreach (Control control in _body.Controls)
            if (control is Button or FlowLayoutPanel) SetEnabled(control, !_flow.IsWorking);
    }

    private static void SetEnabled(Control control, bool enabled)
    {
        if (control is Button button) button.Enabled = enabled;
        foreach (Control child in control.Controls) SetEnabled(child, enabled);
    }

    private void BuildBody()
    {
        _body.SuspendLayout();
        _body.Controls.Clear();
        if (!_flow.IsLoaded)
        {
            AddText(Lang.L("Connecting to Side…", "Side에 연결 중…"));
            AddButtons(Primary(Lang.L("Retry", "다시 시도"), async () => { try { await _flow.LoadAsync(); } catch (Exception) { } }));
        }
        else
        {
            switch (_flow.Step)
            {
                case OnboardingStep.Intro: BuildIntro(); break;
                case OnboardingStep.Accessibility:
                case OnboardingStep.InputMonitoring:
                case OnboardingStep.ScreenRecording: BuildPermission(); break;
                case OnboardingStep.Provider: BuildProvider(); break;
                case OnboardingStep.Finish: BuildFinish(); break;
            }
        }
        _body.ResumeLayout();
    }

    private void BuildIntro()
    {
        AddText(Lang.L(
            "Everything stays on this PC unless you choose a summary provider.",
            "요약 제공자를 선택하지 않으면 모든 정보는 이 PC에만 저장됩니다."), 12f);
        AddText(Lang.L(
            "Side records which apps and pages you use, reads on-screen text, and writes 10-minute and 6-hour summaries you can search later.",
            "Side는 사용한 앱과 페이지, 화면의 글자를 기록하고 10분·6시간 단위 요약을 만들어 나중에 검색할 수 있게 합니다."), secondary: true);
        AddText(Lang.L(
            "Password fields, private browser windows and password managers are never recorded.",
            "비밀번호 입력칸, 브라우저 시크릿 창, 비밀번호 관리자는 기록하지 않습니다."), secondary: true);
        AddButtons(Primary(Lang.L("Continue", "계속"), () => { _flow.ContinueFromIntro(); return Task.CompletedTask; }));
    }

    private void BuildPermission()
    {
        var (title, explanation) = _flow.Step switch
        {
            OnboardingStep.Accessibility => (
                Lang.L("Window reading is blocked", "창 읽기가 차단되었습니다"),
                Lang.L("Side reads the active window through Windows UI Automation. Sign out and back in, or check that security software is not blocking Side, then select Check again.",
                    "Side는 Windows UI 자동화로 활성 창을 읽습니다. 로그아웃 후 다시 로그인하거나 보안 프로그램이 Side를 막지 않는지 확인한 뒤 [다시 확인]을 누르세요.")),
            OnboardingStep.InputMonitoring => (
                Lang.L("Input detection is blocked", "입력 감지가 차단되었습니다"),
                Lang.L("Side detects clicks and shortcuts (never passwords). Check that security software is not blocking Side, then select Check again.",
                    "Side는 클릭과 단축키를 감지합니다(비밀번호 제외). 보안 프로그램이 Side를 막지 않는지 확인한 뒤 [다시 확인]을 누르세요.")),
            _ => (
                Lang.L("Screen text recognition", "화면 텍스트 인식"),
                Lang.L("When a window cannot be read directly, Side can recognize the text on screen (OCR). Turn on screen capture for apps in Windows Settings, or skip this optional step.",
                    "창을 직접 읽을 수 없을 때 Side는 화면의 글자를 인식(OCR)할 수 있습니다. Windows 설정에서 앱의 화면 캡처를 허용하거나, 선택 사항이므로 건너뛰세요.")),
        };
        AddText(title, 14f, bold: true);
        AddText(explanation);
        var buttons = new List<Button>
        {
            Primary(Lang.L("Open Windows Settings", "Windows 설정 열기"), async () =>
            {
                SettingsWindow.OpenExternal(new Uri(_flow.Step == OnboardingStep.ScreenRecording
                    ? "ms-settings:privacy-graphicscaptureprogrammatic" : "ms-settings:privacy"));
                await _flow.RequestCurrentPermissionAsync();
            }),
            SecondaryButton(Lang.L("Check again", "다시 확인"), _flow.RequestCurrentPermissionAsync),
        };
        if (_flow.Step == OnboardingStep.ScreenRecording)
            buttons.Add(SecondaryButton(Lang.L("Skip screen text", "화면 텍스트 건너뛰기"), _flow.SkipScreenRecordingAsync));
        AddButtons([.. buttons]);
        AddText(Lang.L("Side checks every second and continues automatically.", "Side가 매초 확인하며, 해결되면 자동으로 계속합니다."), secondary: true);
    }

    private void BuildProvider()
    {
        AddText(Lang.L("Summary provider", "요약 제공자"), 14f, bold: true);
        AddText(Lang.L("Optional. Capture starts either way; summaries wait until a provider is set.",
            "선택 사항입니다. 캡처는 바로 시작되고, 요약은 제공자를 설정할 때까지 기다립니다."), secondary: true);
        AddText(Lang.L("To start summaries, also turn on \"Send evidence to this provider\" in Settings.",
            "요약을 시작하려면 설정에서 '이 제공자에게 증거 보내기'도 켜세요."), secondary: true);

        var grid = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Margin = new Padding(0, S(6), 0, 0) };
        var labelWidth = S(110);
        var fieldWidth = ContentWidth - labelWidth - S(12);
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, labelWidth));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, fieldWidth + S(8)));

        var presets = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = fieldWidth };
        presets.Items.Add(Lang.L("Select a provider", "제공자를 선택하세요"));
        foreach (var preset in Enum.GetValues<ProviderPreset>()) presets.Items.Add(ProviderDraft.Title(preset, Lang));
        presets.SelectedIndex = 0;

        var name = Input(Lang.L("Enter a provider name", "제공자 이름을 입력하세요"), fieldWidth);
        var baseUrl = Input(Lang.L("Enter the Base URL", "기본 URL을 입력하세요"), fieldWidth);
        var apiKey = Input(Lang.L("Enter the API key if required", "필요하면 API 키를 입력하세요"), fieldWidth, optional: true);
        apiKey.UseSystemPasswordChar = true;
        var model = Input(Lang.L("Enter the model ID", "모델 ID를 입력하세요"), fieldWidth);
        var note = new Label { AutoSize = true, MaximumSize = new Size(fieldWidth, 0), ForeColor = Secondary };

        void AddRow(string label, Control control)
        {
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
            grid.Controls.Add(control);
        }
        AddRow(Lang.L("Provider", "제공자"), presets);
        AddRow(Lang.L("Name", "이름"), name);
        var baseUrlLabel = new Label { Text = Lang.L("Base URL", "기본 URL"), AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
        grid.Controls.Add(baseUrlLabel);
        grid.Controls.Add(baseUrl);
        var apiKeyLabel = new Label { Text = Lang.L("API key", "API 키"), AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
        grid.Controls.Add(apiKeyLabel);
        grid.Controls.Add(apiKey);
        AddRow(Lang.L("Model", "모델"), model);
        grid.Controls.Add(new Label());
        grid.Controls.Add(note);

        void Sync()
        {
            var cli = _draft.Kind != ProviderKind.OpenAiCompatible;
            baseUrl.Visible = baseUrlLabel.Visible = apiKey.Visible = apiKeyLabel.Visible = !cli;
            model.PlaceholderText = _draft.Kind == ProviderKind.ClaudeCodeCli
                ? Lang.L("Enter a model ID beginning with claude-", "claude-로 시작하는 모델 ID를 입력하세요")
                : Lang.L("Enter the model ID", "모델 ID를 입력하세요");
            note.Text = _draft.Kind switch
            {
                ProviderKind.ClaudeCodeCli => Lang.L(
                    "Uses your existing Claude Code login. Summaries leave this PC only after you allow evidence in Settings. Using a CLI login is at your own risk; check the provider's terms and limits.",
                    "기존 Claude Code 로그인을 사용합니다. 설정에서 증거 전송을 허용한 뒤에만 요약이 이 PC 밖으로 나갑니다. CLI 로그인 사용은 사용자 책임이며 제공자 약관과 한도를 확인하세요."),
                ProviderKind.CodexCli => Lang.L(
                    "Uses your existing Codex CLI ChatGPT login. No API key is needed. Summaries reach OpenAI only after evidence consent in Settings. Using a CLI login is at your own risk.",
                    "기존 Codex CLI ChatGPT 로그인을 사용합니다. API 키는 필요 없습니다. 설정에서 증거 전송을 허용한 뒤에만 OpenAI로 보냅니다. CLI 로그인 사용은 사용자 책임입니다."),
                _ => "",
            };
            foreach (var box in new[] { name, baseUrl, model })
                box.BackColor = box.Text.Length == 0 ? RequiredTint : Color.White;
        }
        presets.SelectedIndexChanged += (_, _) =>
        {
            if (presets.SelectedIndex <= 0) return;
            _draft.Select((ProviderPreset)(presets.SelectedIndex - 1));
            name.Text = _draft.Name;
            baseUrl.Text = _draft.BaseUrl;
            model.Text = _draft.ModelId;
            apiKey.Text = "";
            Sync();
        };
        foreach (var box in new[] { name, baseUrl, model }) box.TextChanged += (_, _) => Sync();
        Sync();
        _body.Controls.Add(grid);

        AddButtons(
            Primary(Lang.L("Save and test", "저장 및 테스트"), async () =>
            {
                _draft.Name = name.Text;
                _draft.BaseUrl = baseUrl.Text;
                _draft.ApiKey = apiKey.Text;
                _draft.ModelId = model.Text;
                await _flow.SubmitProviderAsync(_draft);
                apiKey.Text = "";
                _draft.ApiKey = "";
            }),
            SecondaryButton(Lang.L("Skip", "건너뛰기"), _flow.SkipProviderAsync));
    }

    private void BuildFinish()
    {
        AddText(Lang.L("Side is ready to capture activity on this PC.", "Side가 이 PC의 활동을 기록할 준비가 되었습니다."), 13f);
        AddText(Lang.L("You can pause any time from the Side icon in the taskbar notification area.",
            "작업 표시줄 알림 영역의 Side 아이콘에서 언제든 일시정지할 수 있습니다."), secondary: true);
        AddButtons(Primary(Lang.L("Turn on Context Awareness", "Context Awareness 켜기"), _flow.EnableAsync));
    }

    // --- builders -----------------------------------------------------------------------

    private void AddText(string text, float size = 10.5f, bool bold = false, bool secondary = false) =>
        _body.Controls.Add(new Label
        {
            Text = text,
            AutoSize = true,
            MaximumSize = new Size(ContentWidth, 0),
            Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular),
            ForeColor = secondary ? Secondary : Color.FromArgb(28, 28, 30),
            Margin = new Padding(0, 0, 0, 10),
        });

    private void AddButtons(params Button[] buttons)
    {
        // Wraps to a second line instead of clipping when translated labels are long.
        var row = new FlowLayoutPanel
        {
            AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = true,
            MaximumSize = new Size(ContentWidth, 0), Margin = new Padding(0, S(8), 0, S(8)),
        };
        row.Controls.AddRange(buttons);
        _body.Controls.Add(row);
    }

    private Button Primary(string text, Func<Task> action)
    {
        var button = SecondaryButton(text, action);
        button.BackColor = Accent;
        button.ForeColor = Color.White;
        button.FlatAppearance.BorderColor = Accent;
        AcceptButton = button;
        return button;
    }

    private static Button SecondaryButton(string text, Func<Task> action)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            FlatStyle = FlatStyle.Flat,
            Padding = new Padding(10, 4, 10, 4),
            Margin = new Padding(0, 0, 10, 0),
            BackColor = Color.White,
        };
        button.FlatAppearance.BorderColor = Color.FromArgb(180, 180, 188);
        button.Click += async (_, _) => await action();
        return button;
    }

    private static TextBox Input(string placeholder, int width, bool optional = false) => new()
    {
        Width = width,
        PlaceholderText = placeholder,
        BackColor = optional ? Color.White : RequiredTint,
    };
}
