using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Side.Win.UI;

/// <summary>
/// First-run steps. On Windows, Accessibility and Input Monitoring never need a grant (UI Automation
/// and listen-only hooks work for the signed-in user), so those steps only appear if the helper
/// reports them unavailable. Screen text recognition (OCR) needs Windows.Graphics.Capture support.
/// </summary>
public enum OnboardingStep { Intro, Accessibility, InputMonitoring, ScreenRecording, Provider, Finish, Complete }

public enum ProviderKind { OpenAiCompatible, ClaudeCodeCli, CodexCli }

public sealed record OnboardingProvider(
    string Id, string? BaseUrl, IReadOnlyList<string> Models, bool SupportsToolChoice, bool AllowEvidence, ProviderKind Kind)
{
    public static string KindWire(ProviderKind kind) => kind switch
    {
        ProviderKind.ClaudeCodeCli => "claude-code-cli",
        ProviderKind.CodexCli => "codex-cli",
        _ => "openai-compatible",
    };

    public JsonObject ToPatch()
    {
        var models = new JsonArray();
        foreach (var model in Models) models.Add(model);
        if (Kind != ProviderKind.OpenAiCompatible)
            return new JsonObject { ["id"] = Id, ["kind"] = KindWire(Kind), ["models"] = models, ["allowEvidence"] = AllowEvidence };
        return new JsonObject
        {
            ["id"] = Id, ["baseUrl"] = BaseUrl, ["models"] = models,
            ["supportsToolChoice"] = SupportsToolChoice, ["allowEvidence"] = AllowEvidence,
        };
    }
}

public sealed record OnboardingSettings(bool Enabled, bool ScreenOcr, IReadOnlyList<OnboardingProvider> Providers, SideLanguage Language)
{
    public static OnboardingSettings Parse(JsonNode? node)
    {
        if (node is not JsonObject value) throw new DaemonRpcException("invalid settings");
        var providers = new List<OnboardingProvider>();
        if (value["providers"] is JsonArray array)
            foreach (var item in array.OfType<JsonObject>())
            {
                var kind = item["kind"]?.GetValue<string>() switch
                {
                    "claude-code-cli" => ProviderKind.ClaudeCodeCli,
                    "codex-cli" => ProviderKind.CodexCli,
                    _ => ProviderKind.OpenAiCompatible,
                };
                providers.Add(new OnboardingProvider(
                    item["id"]?.GetValue<string>() ?? "",
                    item["base_url"]?.GetValue<string>(),
                    item["models"] is JsonArray models ? models.Select(m => m?.GetValue<string>() ?? "").ToList() : [],
                    item["supports_tool_choice"]?.GetValue<bool>() ?? false,
                    item["allow_evidence"]?.GetValue<bool>() ?? false,
                    kind));
            }
        return new OnboardingSettings(
            value["enabled"]?.GetValue<bool>() ?? false,
            value["screen_ocr"]?.GetValue<bool>() ?? false,
            providers,
            SideLanguageExtensions.Parse(value["ui_language"]?.GetValue<string>()) ?? SideLanguage.Ko);
    }
}

public sealed record OnboardingPermissions(bool Accessibility, bool InputMonitoring, bool ScreenRecording)
{
    public static OnboardingPermissions Parse(JsonNode? node) => node is JsonObject value
        ? new(value["accessibility"]?.GetValue<bool>() ?? false,
              value["input_monitoring"]?.GetValue<bool>() ?? false,
              value["screen_recording"]?.GetValue<bool>() ?? false)
        : throw new DaemonRpcException("invalid permissions");
}

public enum ProviderPreset { XiaomiMiMo26Pro, MiniMaxM3, OpenAi, CodexLogin, ClaudeCode, Custom }

/// <summary>Provider presets (port of OnboardingProviderPreset / OnboardingProviderDraft).</summary>
public sealed class ProviderDraft
{
    public ProviderPreset Preset { get; private set; } = ProviderPreset.Custom;
    public string Name { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public string ModelId { get; set; } = "";
    public string ApiKey { get; set; } = "";

    public bool SupportsToolChoice => Preset is not (ProviderPreset.XiaomiMiMo26Pro or ProviderPreset.ClaudeCode or ProviderPreset.CodexLogin);

    public ProviderKind Kind => Preset switch
    {
        ProviderPreset.ClaudeCode => ProviderKind.ClaudeCodeCli,
        ProviderPreset.CodexLogin => ProviderKind.CodexCli,
        _ => ProviderKind.OpenAiCompatible,
    };

    public static string Title(ProviderPreset preset, SideLanguage language) => preset switch
    {
        ProviderPreset.XiaomiMiMo26Pro => language.L("Xiaomi MiMo 2.6 Pro (Singapore Token Plan)", "Xiaomi MiMo 2.6 Pro (싱가포르 토큰 요금제)"),
        ProviderPreset.MiniMaxM3 => "MiniMax M3",
        ProviderPreset.OpenAi => "OpenAI",
        ProviderPreset.CodexLogin => language.L("OpenAI (Codex login)", "OpenAI (Codex 로그인)"),
        ProviderPreset.ClaudeCode => language.L("Claude Code login", "Claude Code 로그인"),
        _ => language.L("Custom URL", "직접 URL 입력"),
    };

    private static string CanonicalName(ProviderPreset preset) => preset switch
    {
        ProviderPreset.XiaomiMiMo26Pro => "Xiaomi MiMo 2.6 Pro (Singapore Token Plan)",
        ProviderPreset.MiniMaxM3 => "MiniMax M3",
        ProviderPreset.OpenAi => "OpenAI",
        ProviderPreset.CodexLogin => "OpenAI (Codex login)",
        ProviderPreset.ClaudeCode => "Claude Code",
        _ => "",
    };

    public void Select(ProviderPreset next)
    {
        Preset = next;
        ApiKey = "";
        Name = CanonicalName(next);
        (BaseUrl, ModelId) = next switch
        {
            ProviderPreset.XiaomiMiMo26Pro => ("https://token-plan-sgp.xiaomimimo.com/v1", "mimo-v2.6-pro"),
            ProviderPreset.MiniMaxM3 => ("https://api.minimax.io/v1", "MiniMax-M3"),
            ProviderPreset.OpenAi => ("https://api.openai.com/v1", ""),
            ProviderPreset.CodexLogin => ("", "gpt-6-luna"),
            _ => ("", ""),
        };
    }
}

/// <summary>Onboarding state machine (port of OnboardingFlow.swift). Runs on the UI thread.</summary>
public sealed class OnboardingFlow(DaemonRpcClient rpc)
{
    public OnboardingStep Step { get; private set; } = OnboardingStep.Intro;
    public bool IsLoaded { get; private set; }
    public bool IsWorking { get; private set; }
    public bool IsFinished { get; private set; }
    public SideLanguage Language { get; private set; } = SideLanguage.Ko;
    public string? ErrorMessage { get; private set; }
    public event Action? Changed;
    public event Action<SideLanguage>? LanguageChanged;
    public event Action? Completed;

    private OnboardingSettings? _settings;
    private OnboardingPermissions? _status;

    public async Task LoadAsync()
    {
        try
        {
            _settings = OnboardingSettings.Parse(await rpc.CallAsync("settings.get"));
            SetLanguageLocal(_settings.Language);
            if (_settings.Enabled)
            {
                Complete();
                return;
            }
            _status = OnboardingPermissions.Parse(await rpc.CallAsync("permissions"));
            Step = Enum.TryParse<OnboardingStep>(UiState.Load().OnboardingStep, out var resume) ? resume : OnboardingStep.Intro;
            if (Step == OnboardingStep.Complete) Step = OnboardingStep.Intro;
            NormalizeStep();
            IsLoaded = true;
            ErrorMessage = null;
        }
        catch (Exception)
        {
            ErrorMessage = Language.L("Side is still starting. Select Retry in a few seconds.", "Side가 시작 중입니다. 잠시 후 [다시 시도]를 누르세요.");
            throw;
        }
        finally
        {
            Changed?.Invoke();
        }
    }

    public Task SetLanguageAsync(SideLanguage next) => WorkAsync(async () =>
    {
        if (!IsLoaded || next == Language) return;
        var updated = OnboardingSettings.Parse(await rpc.CallAsync("settings.patch", new JsonObject { ["uiLanguage"] = next.WireValue() }));
        if (updated.Language != next) throw new DaemonRpcException("language not saved");
        _settings = updated;
        SetLanguageLocal(next);
    }, () => Language.L("Could not save the language. Select it again.", "언어를 저장할 수 없습니다. 다시 선택하세요."));

    public void ContinueFromIntro()
    {
        if (Step != OnboardingStep.Intro) return;
        Go(OnboardingStep.Accessibility);
        NormalizeStep();
        Changed?.Invoke();
    }

    public async Task PollPermissionsAsync()
    {
        if (IsFinished || !IsLoaded || IsWorking) return;
        try
        {
            _status = OnboardingPermissions.Parse(await rpc.CallAsync("permissions"));
            var before = Step;
            NormalizeStep();
            if (before != Step || ErrorMessage is not null)
            {
                ErrorMessage = null;
                Changed?.Invoke();
            }
        }
        catch (Exception)
        {
            ErrorMessage = Language.L("Could not check the capture status. Side will retry.", "캡처 상태를 확인할 수 없습니다. Side가 다시 시도합니다.");
            Changed?.Invoke();
        }
    }

    /// <summary>Ask the helper again (it opens Windows Settings when something is blocked).</summary>
    public Task RequestCurrentPermissionAsync() => WorkAsync(async () =>
    {
        var kind = Step switch
        {
            OnboardingStep.Accessibility => "accessibility",
            OnboardingStep.InputMonitoring => "inputMonitoring",
            OnboardingStep.ScreenRecording => "screenRecording",
            _ => null,
        };
        if (kind is null) return;
        await rpc.CallAsync("requestPermissions", new JsonObject { ["kinds"] = new JsonArray(kind) });
        _status = OnboardingPermissions.Parse(await rpc.CallAsync("permissions"));
        NormalizeStep();
    }, () => Language.L("Could not check access. Open Windows Settings, then select Check again.", "권한을 확인할 수 없습니다. Windows 설정을 연 뒤 [다시 확인]을 누르세요."));

    public Task SkipScreenRecordingAsync() => WorkAsync(async () =>
    {
        if (Step != OnboardingStep.ScreenRecording) return;
        var updated = OnboardingSettings.Parse(await rpc.CallAsync("settings.patch", new JsonObject { ["screenOcr"] = false }));
        if (updated.ScreenOcr) throw new DaemonRpcException("not saved");
        _settings = updated;
        Go(OnboardingStep.Provider);
    }, () => Language.L("Could not save the screen text choice. Select Skip again.", "화면 텍스트 인식 선택을 저장할 수 없습니다. [건너뛰기]를 다시 누르세요."));

    public Task SkipProviderAsync() => WorkAsync(async () =>
    {
        if (Step != OnboardingStep.Provider) return;
        _settings = OnboardingSettings.Parse(await rpc.CallAsync("settings.patch",
            new JsonObject { ["summaryModel"] = null, ["defaultModel"] = null }));
        Go(OnboardingStep.Finish);
    }, () => Language.L("Could not save the provider choice. Select Skip again.", "제공자 선택을 저장할 수 없습니다. [건너뛰기]를 다시 누르세요."));

    public async Task SubmitProviderAsync(ProviderDraft draft)
    {
        if (Step != OnboardingStep.Provider || IsWorking || _settings is null) return;
        var id = draft.Name.Trim();
        var endpoint = draft.BaseUrl.Trim();
        var model = draft.ModelId.Trim();
        var apiKey = draft.ApiKey;
        string? invalid = draft.Kind switch
        {
            ProviderKind.OpenAiCompatible when id.Length == 0 || model.Length == 0 || !IsSafeBaseUrl(endpoint) =>
                Language.L("Enter a name, an HTTP or HTTPS Base URL without credentials or query, and a model.",
                    "이름, 자격 증명과 쿼리가 없는 HTTP 또는 HTTPS 기본 URL, 모델을 입력하세요."),
            ProviderKind.ClaudeCodeCli when id.Length == 0 || !Regex.IsMatch(model, "^claude-[A-Za-z0-9-]+$") =>
                Language.L("Enter an explicit Claude Code model ID beginning with claude-.", "claude-로 시작하는 Claude Code 모델 ID를 입력하세요."),
            ProviderKind.CodexCli when id.Length == 0 || !Regex.IsMatch(model, "^[A-Za-z0-9][A-Za-z0-9._-]*$") =>
                Language.L("Enter an explicit Codex model ID, such as gpt-6-luna.", "gpt-6-luna와 같은 Codex 모델 ID를 입력하세요."),
            _ => null,
        };
        if (invalid is not null)
        {
            ErrorMessage = invalid;
            Changed?.Invoke();
            return;
        }
        await WorkAsync(async () =>
        {
            var provider = new OnboardingProvider(id, draft.Kind == ProviderKind.OpenAiCompatible ? endpoint : null, [model],
                draft.Kind == ProviderKind.OpenAiCompatible && draft.SupportsToolChoice, false, draft.Kind);
            var providers = new JsonArray();
            foreach (var existing in _settings.Providers.Where(p => p.Id != id)) providers.Add(existing.ToPatch());
            providers.Add(provider.ToPatch());
            _settings = OnboardingSettings.Parse(await rpc.CallAsync("settings.patch", new JsonObject { ["providers"] = providers }));
            if (draft.Kind == ProviderKind.OpenAiCompatible && apiKey.Length > 0)
            {
                var reference = await rpc.CallAsync("providers.setKey", new JsonObject { ["providerId"] = id, ["apiKey"] = apiKey });
                if (string.IsNullOrEmpty(reference?["apiKeyRef"]?.GetValue<string>())) throw new DaemonRpcException("no key ref");
            }
            var test = await rpc.CallAsync("providers.test", new JsonObject { ["providerId"] = id, ["modelId"] = model },
                DaemonRpcClient.ProviderTestTimeout);
            if (test?["ok"]?.GetValue<bool>() != true)
            {
                ErrorMessage = ProviderFailureMessage(test?["error"]?.GetValue<string>());
                return;
            }
            _settings = OnboardingSettings.Parse(await rpc.CallAsync("settings.patch",
                new JsonObject { ["summaryModel"] = new JsonObject { ["provider"] = id, ["modelId"] = model } }));
            Go(OnboardingStep.Finish);
        }, () => Language.L("Could not save or test the provider. Check the details and select Save and test again.",
            "제공자를 저장하거나 테스트할 수 없습니다. 입력값을 확인한 뒤 [저장 및 테스트]를 다시 누르세요."));
    }

    public Task EnableAsync() => WorkAsync(async () =>
    {
        if (Step != OnboardingStep.Finish) return;
        _status = OnboardingPermissions.Parse(await rpc.CallAsync("permissions"));
        NormalizeStep();
        if (Step != OnboardingStep.Finish)
        {
            ErrorMessage = Language.L("Resolve the step above before turning on capture.", "캡처를 켜기 전에 위 단계를 먼저 해결하세요.");
            return;
        }
        var updated = OnboardingSettings.Parse(await rpc.CallAsync("settings.patch", new JsonObject { ["enabled"] = true }));
        if (!updated.Enabled) throw new DaemonRpcException("not enabled");
        _settings = updated;
        Complete();
    }, () => Language.L("Could not turn on Context Awareness. Select the button again.", "Context Awareness를 켤 수 없습니다. 버튼을 다시 누르세요."));

    private async Task WorkAsync(Func<Task> action, Func<string> failure)
    {
        if (IsWorking) return;
        IsWorking = true;
        ErrorMessage = null;
        Changed?.Invoke();
        try
        {
            await action();
        }
        catch (Exception)
        {
            ErrorMessage = failure();
        }
        finally
        {
            IsWorking = false;
            Changed?.Invoke();
        }
    }

    private static bool IsSafeBaseUrl(string endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var url) &&
        (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps) && url.Host.Length > 0 &&
        url.UserInfo.Length == 0 && url.Query.Length == 0 && url.Fragment.Length == 0;

    private string ProviderFailureMessage(string? detail)
    {
        switch (detail)
        {
            case "Provider or model is not configured":
                return Language.L("Provider or model is not configured.", "제공자 또는 모델이 설정되지 않았습니다.");
            case "record_summary response failed validation":
                return Language.L("The provider returned an invalid summary response.", "제공자가 유효하지 않은 요약 응답을 반환했습니다.");
        }
        var message = Language.L("Provider test failed. Check the details and select Save and test again.",
            "제공자 연결 테스트에 실패했습니다. 입력값을 확인한 뒤 [저장 및 테스트]를 다시 누르세요.");
        if (detail is not null && detail.StartsWith("HTTP ") && int.TryParse(detail[5..], out var status) && status is >= 400 and <= 599)
            return $"{message} (HTTP {status})";
        return message;
    }

    private void NormalizeStep()
    {
        if (Step is OnboardingStep.Intro or OnboardingStep.Complete || _status is null || _settings is null) return;
        if (!_status.Accessibility) { Go(OnboardingStep.Accessibility); return; }
        if (!_status.InputMonitoring) { Go(OnboardingStep.InputMonitoring); return; }
        if (_settings.ScreenOcr && !_status.ScreenRecording) { Go(OnboardingStep.ScreenRecording); return; }
        if (Step is OnboardingStep.Accessibility or OnboardingStep.InputMonitoring or OnboardingStep.ScreenRecording)
            Go(OnboardingStep.Provider);
    }

    private void Go(OnboardingStep next)
    {
        Step = next;
        UiState.Update(state => state.OnboardingStep = next.ToString());
    }

    private void SetLanguageLocal(SideLanguage language)
    {
        Language = language;
        LanguageChanged?.Invoke(language);
    }

    private void Complete()
    {
        Step = OnboardingStep.Complete;
        IsFinished = true;
        UiState.Update(state => state.OnboardingStep = null);
        ErrorMessage = null;
        Completed?.Invoke();
    }
}
