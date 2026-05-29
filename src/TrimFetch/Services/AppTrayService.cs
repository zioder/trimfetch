using H.NotifyIcon;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using TrimFetch.Helpers;
using ContextMenuMode = H.NotifyIcon.ContextMenuMode;

namespace TrimFetch.Services;

public sealed class AppTrayService : IDisposable
{
    private TaskbarIcon? _icon;
    private MenuFlyoutItem? _statusItem;
    private bool _isDownloading;
    private bool _disposed;

    private readonly XamlUICommand _openCommand;
    private readonly XamlUICommand _settingsCommand;
    private readonly XamlUICommand _exitCommand;

    public AppTrayService()
    {
        _openCommand = new XamlUICommand
        {
            Label = $"Open {AppBranding.ShortName}",
            Description = $"Show {AppBranding.ShortName}",
        };
        _openCommand.ExecuteRequested += (_, _) => ShowMainWindow();

        _settingsCommand = new XamlUICommand
        {
            Label = "Settings",
            Description = "Open TrimFetch settings",
        };
        _settingsCommand.ExecuteRequested += (_, _) => ShowSettings();

        _exitCommand = new XamlUICommand
        {
            Label = "Exit",
            Description = $"Quit {AppBranding.ShortName}",
        };
        _exitCommand.ExecuteRequested += (_, _) => ExitApplication();
    }

    public void Initialize()
    {
        if (_icon is not null)
        {
            return;
        }

        _statusItem = new MenuFlyoutItem
        {
            Text = "Ready",
            IsEnabled = false,
        };

        var menu = new MenuFlyout
        {
            AreOpenCloseAnimationsEnabled = false,
        };
        menu.Items.Add(new MenuFlyoutItem { Command = _openCommand });
        menu.Items.Add(_statusItem);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(new MenuFlyoutItem { Command = _settingsCommand });
        menu.Items.Add(new MenuFlyoutItem { Command = _exitCommand });

        _icon = new TaskbarIcon
        {
            ToolTipText = AppBranding.ShortName,
            NoLeftClickDelay = true,
            ContextFlyout = menu,
            LeftClickCommand = _openCommand,
            ContextMenuMode = ContextMenuMode.PopupMenu,
        };

        _icon.IconSource = AppIconHelper.CreateAssetImage(AppBranding.AppIconIcoPath);

        _icon.ForceCreate();
        UpdateStatusText();
    }

    public void SetDownloading(bool isDownloading)
    {
        _isDownloading = isDownloading;
        UpdateStatusText();
    }

    private void UpdateStatusText()
    {
        if (_icon is null)
        {
            return;
        }

        var status = _isDownloading ? "Downloading…" : "Ready";
        _icon.ToolTipText = _isDownloading
            ? $"{AppBranding.ShortName} — Downloading…"
            : AppBranding.ShortName;

        if (_statusItem is not null)
        {
            _statusItem.Text = status;
        }
    }

    private static void RunOnUiThread(Action action)
    {
        var queue = App.DispatcherQueue;
        if (queue is null)
        {
            action();
            return;
        }

        if (queue.HasThreadAccess)
        {
            action();
            return;
        }

        queue.TryEnqueue(DispatcherQueuePriority.Normal, () => action());
    }

    private static void ShowMainWindow()
    {
        RunOnUiThread(() =>
        {
            if (App.Window is MainWindow window)
            {
                window.ShowFromBackground();
            }
        });
    }

    private static void ShowSettings()
    {
        RunOnUiThread(() =>
        {
            if (App.Window is MainWindow window)
            {
                window.ShowSettings();
            }
        });
    }

    private static void ExitApplication()
    {
        RunOnUiThread(() =>
        {
            if (App.Window is MainWindow window)
            {
                window.ExitApplication();
            }
            else
            {
                Application.Current.Exit();
            }
        });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _icon?.Dispose();
        _icon = null;
    }
}
