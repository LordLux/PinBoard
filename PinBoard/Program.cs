using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace PinBoard;

public static class Program
{
    [STAThread]
    static void Main(string[] _)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try
            {
                var dir  = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PinBoard");
                Directory.CreateDirectory(dir);
                File.AppendAllText(
                    Path.Combine(dir, "crash.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] terminating={e.IsTerminating}\n{e.ExceptionObject}\n\n"
                );
            }
            catch {}
        };

        WinRT.ComWrappersSupport.InitializeComWrappers();

        var mainInstance = AppInstance.FindOrRegisterForKey("PinBoard-singleton");

        if (!mainInstance.IsCurrent)
        {
            // Another PinBoard is already running — redirect this launch to it.
            var args = AppInstance.GetCurrent().GetActivatedEventArgs();
            mainInstance.RedirectActivationToAsync(args).AsTask().GetAwaiter().GetResult();
            return;
        }

        // When a second instance tries to launch, show our popup in the existing instance.
        mainInstance.Activated += (_, _) => App.Current?.DispatchShowPopup();

        Application.Start(p =>
        {
            var syncCtx = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(syncCtx);

            new App();
        });
    }
}
