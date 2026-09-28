using System.Reflection;
using Side.Win.Capture;
using Side.Win.Core;
using Side.Win.UI;

namespace Side.Win.App;

/// <summary>Composition root: single instance, tray UI, capture layer, daemon supervisor.</summary>
internal static class Program
{
    [STAThread]
    private static int Main()
    {
        using var mutex = new Mutex(initiallyOwned: true, @"Local\Side.Win.SingleInstance", out var created);
        if (!created) return 0;

        ApplicationConfiguration.Initialize();

        var exeDirectory = AppContext.BaseDirectory;
        var daemonPath = Path.Combine(exeDirectory, "resources", "side.exe");
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0";

        SideRuntime? runtime = null;
        var ui = new TrayUi(
            retryKeyStore: () => runtime?.Supervisor.RetryKeyStore(),
            quitAsync: () => runtime?.QuitAsync() ?? Task.CompletedTask);

        if (!File.Exists(daemonPath))
        {
            MessageBox.Show(
                $"Side 설치가 손상되었습니다. 다시 설치하세요.\n\n찾을 수 없는 파일:\n{daemonPath}",
                "Side", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }

        var supervisor = new DaemonSupervisor(daemonPath, version, new WindowsCredentialKeyStore());
        var capture = new CaptureLayer();
        runtime = new SideRuntime(supervisor, capture, ui);

        runtime.Start();
        ui.Start();
        Application.Run();

        ui.Dispose();
        runtime.Dispose();
        return 0;
    }
}
