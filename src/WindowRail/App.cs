using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace WindowRail;

public sealed class App : Application
{
    private Mutex? mutex;
    private EventWaitHandle? wake;
    private EventWaitHandle? exitSignal;
    private RegisteredWaitHandle? listener;
    private RegisteredWaitHandle? exitListener;
    internal ConfigStore Store { get; private set; } = null!;
    [STAThread]
    public static int Main(string[] args)
    {
        // The small transparent toolbar does not need a dedicated GPU rendering surface.
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        return app.Start(args);
    }
    private int Start(string[] args)
    {
        try
        {
            string directory = Path.Combine(AppContext.BaseDirectory, "data");
            int index = Array.IndexOf(args, "--data-dir");
            if (index >= 0 && index + 1 < args.Length) directory = args[index + 1];
            Store = new ConfigStore(directory);
            string identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Store.DirectoryPath.ToUpperInvariant())))[..20];
            if (args.Contains("--shutdown"))
            {
                if (EventWaitHandle.TryOpenExisting(@"Local\WindowRail-exit-" + identity, out var signal)) { using (signal) signal.Set(); }
                return 0;
            }
            mutex = new Mutex(true, @"Local\WindowRail-" + identity, out bool created);
            wake = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\WindowRail-show-" + identity);
            if (!created) { wake.Set(); mutex.Dispose(); wake.Dispose(); return 0; }
            exitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\WindowRail-exit-" + identity);
            DispatcherUnhandledException += (_, e) => { Store.Log("unhandled", e.Exception); MessageBox.Show("窗栏遇到错误，请重新打开。配置保存在程序旁的 data 文件夹。", "窗栏"); e.Handled = true; Shutdown(1); };
            var main = new MainWindow(Store, args.Contains("--test-ui"));
            MainWindow = main;
            listener = ThreadPool.RegisterWaitForSingleObject(wake, (_, _) => Dispatcher.BeginInvoke(main.Reveal), null, Timeout.Infinite, false);
            exitListener = ThreadPool.RegisterWaitForSingleObject(exitSignal, (_, _) => Dispatcher.BeginInvoke(() => Shutdown()), null, Timeout.Infinite, false);
            main.Show();
            int result = Run();
            listener.Unregister(null); exitListener.Unregister(null); exitSignal.Dispose(); wake.Dispose(); mutex.ReleaseMutex(); mutex.Dispose();
            return result;
        }
        catch (Exception error)
        {
            Store?.Log("startup", error);
            MessageBox.Show("窗栏无法启动。请把整个程序文件夹解压到可写的位置后重试。\n\n" + error.Message, "窗栏", MessageBoxButton.OK, MessageBoxImage.Warning);
            return 1;
        }
    }
}
