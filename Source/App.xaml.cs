using System.IO;
using System.Windows;
namespace PulseBridge;
public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        if (e.Args.Contains("--self-test")) { Shutdown(await SelfTests.Run(e.Args)); return; }
        int at = Array.IndexOf(e.Args, "--data-dir");
        string dir = at >= 0 && at + 1 < e.Args.Length ? e.Args[at + 1] : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PulseBridge");
        try
        {
            var window = new MainWindow(dir);
            MainWindow = window;
            at = Array.IndexOf(e.Args, "--ui-test");
            if (at >= 0 && at + 1 < e.Args.Length)
            {
                await window.VerifyUi(e.Args[at + 1]);
                window.Close(); Shutdown(); return;
            }
            at = Array.IndexOf(e.Args, "--screenshot");
            if (at >= 0 && at + 1 < e.Args.Length)
            {
                await window.Capture(e.Args[at + 1]);
                window.Close(); Shutdown(); return;
            }
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            window.Show();
        }
        catch (Exception ex)
        {
            at = Array.IndexOf(e.Args, "--ui-test");
            if (at >= 0 && at + 1 < e.Args.Length)
            {
                File.AppendAllText(e.Args[at + 1], "FAIL " + ex + Environment.NewLine);
                MainWindow?.Close(); Shutdown(1); return;
            }
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "PulseBridge-error.txt"), ex.ToString());
            MessageBox.Show("无法启动 PulseBridge：" + ex.Message, "PulseBridge"); Shutdown(1);
        }
    }
}
