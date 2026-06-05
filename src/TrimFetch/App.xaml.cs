using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Windows.AppLifecycle;
using TrimFetch.Helpers;
using TrimFetch.Services;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace TrimFetch;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private const string MainInstanceKey = "TrimFetch.Main";

    /// <summary>
    /// The main application window. Use <c>App.Window</c> from any class that needs
    /// the window reference (for dialogs, pickers, interop, etc.).
    /// </summary>
    public static Window Window { get; private set; } = null!;

    /// <summary>
    /// The UI thread dispatcher. Use <c>App.DispatcherQueue</c> to marshal calls
    /// to the UI thread. Fully qualified to avoid CS0104 ambiguity with
    /// <see cref="Windows.System.DispatcherQueue"/>.
    /// </summary>
    public static Microsoft.UI.Dispatching.DispatcherQueue DispatcherQueue { get; private set; } = null!;

    /// <summary>
    /// The native window handle (HWND). Use for file pickers,
    /// <c>DataTransferManager</c>, and any WinRT interop that requires
    /// <c>InitializeWithWindow</c>.
    /// </summary>
    public static nint WindowHandle =>
        WinRT.Interop.WindowNative.GetWindowHandle(Window);

    public static Microsoft.UI.WindowId WindowId =>
        Microsoft.UI.Win32Interop.GetWindowIdFromWindow(WindowHandle);

    /// <summary>
    /// Initializes the singleton application object.
    /// </summary>
    public App()
    {
        InitializeComponent();
        UnhandledException += App_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
    }

    private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        AppDiagnostic.LogException("Unhandled", e.Exception);
        System.Diagnostics.Debug.WriteLine($"Unhandled: {e.Exception}");

        if (Window is MainWindow mainWindow)
        {
            mainWindow.NotifyLayoutException(e.Exception);
        }

        e.Handled = true;
    }

    private void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        AppDiagnostic.LogException("UnobservedTask", e.Exception);
        System.Diagnostics.Debug.WriteLine($"Unobserved: {e.Exception}");
        e.SetObserved();
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        ExternalToolLocator.Refresh();
        var isStartupLaunch = IsStartupLaunch();
        AppDiagnostic.Log($"App launch startup={isStartupLaunch} args=\"{args.Arguments}\"");

        if (RedirectToMainInstanceIfNeeded(isStartupLaunch))
        {
            return;
        }

        AppServices.Tray.Initialize();
        AppServices.Notifications.EnsureRegistered();
        _ = AppServices.Startup.SyncWithPreferenceAsync(new PreferencesService());
        var mainWindow = new MainWindow
        {
            // Reveal the overlay on a direct user launch; stay tray-only at login (startup task),
            // ready for the global hotkey.
            ShouldRevealOnLaunch = !isStartupLaunch,
        };
        Window = mainWindow;
        DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        AppInstance.GetCurrent().Activated += OnAppInstanceActivated;

        // Bootstrap WinUI. MainPage decides whether to reveal the overlay (direct launch) or
        // hide to the tray (login launch); when tools are missing it shows the setup card.
        Window.Activate();
    }

    private static bool RedirectToMainInstanceIfNeeded(bool isStartupLaunch)
    {
        try
        {
            var mainInstance = AppInstance.FindOrRegisterForKey(MainInstanceKey);
            if (mainInstance.IsCurrent)
            {
                return false;
            }

            if (!isStartupLaunch)
            {
                _ = mainInstance.RedirectActivationToAsync(AppInstance.GetCurrent().GetActivatedEventArgs());
            }

            Environment.Exit(0);
            return true;
        }
        catch (Exception ex)
        {
            AppDiagnostic.Log($"Instance redirect failed: {ex.Message}");
            return false;
        }
    }

    private void OnAppInstanceActivated(object? sender, AppActivationArguments args)
    {
        if (args.Kind == ExtendedActivationKind.StartupTask)
        {
            return;
        }

        DispatcherQueue?.TryEnqueue(() =>
        {
            if (Window is MainWindow mainWindow)
            {
                mainWindow.ShowFromBackground();
            }
        });
    }

    /// <summary>
    /// True when this process was started by the Windows startup task (login), as opposed to
    /// the user opening the app. Used to keep login launches tray-only.
    /// </summary>
    private static bool IsStartupLaunch()
    {
        if (Environment.GetCommandLineArgs()
            .Any(arg => string.Equals(arg, "--startup", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        try
        {
            var kind = Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent()
                .GetActivatedEventArgs()
                .Kind;
            return kind == Microsoft.Windows.AppLifecycle.ExtendedActivationKind.StartupTask;
        }
        catch
        {
            // If activation info is unavailable, treat it as a direct launch and reveal.
            return false;
        }
    }
}
