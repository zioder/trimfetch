using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Diagnostics;
using Windows.Graphics;
using Windows.System;
using TrimFetch.Helpers;
using TrimFetch.Models;
using TrimFetch.Services;
using TrimFetch.ViewModels;



namespace TrimFetch;



public sealed partial class SettingsWindow : Window

{

    private readonly PreferencesService _preferences = new();

    private readonly UpdateCheckerService _updateChecker = new();

    private readonly SemaphoreSlim _shortcutLoadGate = new(1, 1);

    private readonly Dictionary<HotKeyAction, ShortcutRow> _rows = new();

    private bool _shortcutsInitialized;
    private bool _placementApplied;



    public SettingsViewModel ViewModel { get; } = new();



    public event EventHandler? HotKeysChanged;



    public event EventHandler? DependenciesChanged;

    public event EventHandler? PreferencesChanged;



    public SettingsWindow()
    {
        InitializeComponent();

        Title = $"{AppBranding.ShortName} — Settings";
        FluentWindowChrome.ApplySettingsWindowChrome(this, AppTitleBar);
        FluentWindowChrome.ConfigureSettingsPresenter(AppWindow);
        FluentWindowChrome.TrySetWindowIcon(AppWindow);
        // Size and centre before the caller activates the window. Doing this in Loaded
        // (after it is already on screen) causes a visible resize jump with a black flash.
        FluentWindowChrome.ResizeAndCenter(
            this,
            FluentWindowChrome.DefaultSettingsWidthDip,
            FluentWindowChrome.DefaultSettingsHeightDip,
            App.Window);
        AboutAppIcon.Source = AppIconHelper.CreateAssetImage(AppBranding.AppIconPngPath);
        ProductNameText.Text = AppBranding.FullName;
        VersionText.Text = GetVersionLabel();
        ViewModel.DependenciesChanged += (_, _) => DependenciesChanged?.Invoke(this, EventArgs.Empty);
        ViewModel.PreferencesChanged += (_, _) => PreferencesChanged?.Invoke(this, EventArgs.Empty);
        Activated += SettingsWindow_Activated;
        RootGrid.Loaded += SettingsWindow_Loaded;
        _ = ViewModel.InitializeAsync();
    }



    public static Visibility BoolToVisibility(bool value) =>

        value ? Visibility.Visible : Visibility.Collapsed;



    public static Visibility InverseBoolToVisibility(bool value) =>

        value ? Visibility.Collapsed : Visibility.Visible;



    public static Visibility StringToVisibility(string? value) =>

        string.IsNullOrWhiteSpace(value) ? Visibility.Collapsed : Visibility.Visible;



    public Task ReloadShortcutsAsync() => LoadShortcutsAsync();



    private void SettingsWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_placementApplied)
        {
            return;
        }

        _placementApplied = true;
        FluentWindowChrome.SyncTitleBarHostHeight(this, AppTitleBar);
    }

    private async void SettingsWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        FluentWindowChrome.SyncTitleBarHostHeight(this, AppTitleBar);

        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            return;
        }

        if (!_shortcutsInitialized)
        {
            await LoadShortcutsAsync();
            return;
        }

        foreach (var action in Enum.GetValues<HotKeyAction>())
        {
            if (_rows.TryGetValue(action, out var row))
            {
                row.UpdateShortcut(await _preferences.GetHotKeyAsync(action));
            }
        }
    }



    private static string GetVersionLabel() => $"Version {AppVersion.GetDisplayLabel()}";



    private async Task LoadShortcutsAsync()

    {

        await _shortcutLoadGate.WaitAsync();

        try

        {

            if (!_shortcutsInitialized)

            {

                ShortcutRows.Children.Clear();

                _rows.Clear();



                foreach (var action in Enum.GetValues<HotKeyAction>())

                {

                    var shortcut = await _preferences.GetHotKeyAsync(action);

                    var row = new ShortcutRow(action, shortcut, OnShortcutRecorded);

                    _rows[action] = row;

                    ShortcutRows.Children.Add(row.Root);



                    if (action != HotKeyAction.ActivateApp)

                    {

                        ShortcutRows.Children.Add(CreateDivider());

                    }

                }



                _shortcutsInitialized = true;

                return;

            }



            foreach (var action in Enum.GetValues<HotKeyAction>())

            {

                if (_rows.TryGetValue(action, out var row))

                {

                    row.UpdateShortcut(await _preferences.GetHotKeyAsync(action));

                }

            }

        }

        finally

        {

            _shortcutLoadGate.Release();

        }

    }



    private static Border CreateDivider() =>

        new()

        {

            Height = 1,

            Margin = new Thickness(0, 4, 0, 4),

            Background = Application.Current.Resources["DividerStrokeColorDefaultBrush"] as Brush,

            Opacity = 0.55,

        };



    private async void OnShortcutRecorded(HotKeyAction action, HotKeyShortcut shortcut)

    {

        await _preferences.SetHotKeyAsync(action, shortcut);

        HotKeysChanged?.Invoke(this, EventArgs.Empty);

    }



    private static async Task OpenUriAsync(string uri)
    {
        _ = await Launcher.LaunchUriAsync(new Uri(uri));
    }

    private async void Donate_Click(object sender, RoutedEventArgs e)
    {
        await OpenUriAsync("https://buymeacoffee.com/zioder");
    }

    private async void CheckForUpdates_Click(object sender, RoutedEventArgs e)
    {
        var waitDialog = new UpdateWaitDialog(Content.XamlRoot);
        waitDialog.ShowChecking();
        _ = waitDialog.ShowAsync();

        try
        {
            var currentLabel = AppVersion.GetDisplayLabel();
            var result = await _updateChecker.CheckAsync(currentLabel);

            if (result.Kind == UpdateCheckResultKind.UpToDate)
            {
                waitDialog.Close();
                await ShowMessageAsync($"{AppBranding.ShortName} is up to date", $"You are running {GetVersionLabel()}.");
                return;
            }

            DownloadedUpdate? downloadedUpdate = null;
            if (result.DownloadUrl is not null)
            {
                var fileName = Path.GetFileName(result.DownloadUrl.LocalPath);
                waitDialog.ShowDownloading(result.Version ?? "update", fileName);

                var dispatcher = Content.DispatcherQueue;
                var progress = new Progress<UpdateDownloadProgress>(report =>
                {
                    _ = dispatcher.TryEnqueue(() => waitDialog.ReportDownload(report));
                });

                downloadedUpdate = await _updateChecker.DownloadAsync(result, progress);
            }

            waitDialog.Close();

            var updateDialog = new ContentDialog
            {
                Title = $"{AppBranding.ShortName} {result.Version} is ready",
                Content = downloadedUpdate is null
                    ? $"A new version is available. You are running {GetVersionLabel()}."
                    : $"The installer has been downloaded. You are running {GetVersionLabel()}.",
                PrimaryButtonText = "Install",
                CloseButtonText = "Later",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Content.XamlRoot,
            };

            if (await updateDialog.ShowAsync() == ContentDialogResult.Primary)
            {
                if (downloadedUpdate is not null)
                {
                    await LaunchDownloadedUpdateAsync(downloadedUpdate);
                }
                else
                {
                    await LaunchUpdateUriAsync(result);
                }
            }
        }
        catch (Exception ex)
        {
            waitDialog.Close();
            await ShowMessageAsync("Could not check for updates", ex.Message);
        }
    }



    private async Task ShowMessageAsync(string title, string message)

    {

        var dialog = new ContentDialog

        {

            Title = title,

            Content = message,

            CloseButtonText = "OK",

            XamlRoot = Content.XamlRoot,

        };



        _ = await dialog.ShowAsync();

    }



    private static Task LaunchDownloadedUpdateAsync(DownloadedUpdate update)
    {
        Process.Start(new ProcessStartInfo(update.FilePath)
        {
            UseShellExecute = true,
        });

        if (App.Window is MainWindow mainWindow)
        {
            mainWindow.ExitApplication();
        }
        else
        {
            Application.Current.Exit();
        }

        return Task.CompletedTask;
    }



    private static async Task LaunchUpdateUriAsync(UpdateCheckResult result)

    {

        var uri = result.DownloadUrl ?? result.ReleaseUrl;

        if (uri is not null)

        {

            _ = await Launcher.LaunchUriAsync(uri);

        }

    }



    private sealed class ShortcutRow

    {

        private readonly HotKeyAction _action;

        private readonly Action<HotKeyAction, HotKeyShortcut> _onRecord;

        private readonly TextBlock _title;

        private readonly Button _button;

        private HotKeyShortcut _shortcut;

        private bool _isRecording;



        public Grid Root { get; }



        public ShortcutRow(

            HotKeyAction action,

            HotKeyShortcut shortcut,

            Action<HotKeyAction, HotKeyShortcut> onRecord)

        {

            _action = action;

            _shortcut = shortcut;

            _onRecord = onRecord;



            _title = new TextBlock

            {

                Text = action.GetTitle(),

                Style = Application.Current.Resources["BodyTextBlockStyle"] as Style,

                VerticalAlignment = VerticalAlignment.Center,

            };



            _button = CreateShortcutButton(shortcut.DisplayText);

            _button.Click += (_, _) => BeginRecording();



            Root = new Grid

            {

                MinHeight = 44,

                Padding = new Thickness(0, 6, 0, 6),

                ColumnDefinitions =

                {

                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },

                    new ColumnDefinition { Width = GridLength.Auto },

                },

                Children = { _title, _button },

            };



            Grid.SetColumn(_button, 1);

        }



        public void UpdateShortcut(HotKeyShortcut shortcut)

        {

            _shortcut = shortcut;

            if (!_isRecording)

            {

                _button.Content = shortcut.DisplayText;

            }

        }



        private void BeginRecording()

        {

            _isRecording = true;

            _button.Content = "Press keys…";

            Root.KeyDown += Root_KeyDown;

            Root.IsTabStop = true;

            Root.Focus(FocusState.Programmatic);

        }



        private void Root_KeyDown(object sender, KeyRoutedEventArgs e)

        {

            if (!_isRecording)

            {

                return;

            }



            if (e.Key == VirtualKey.Escape)

            {

                EndRecording(cancelled: true);

                e.Handled = true;

                return;

            }



            if (IsModifierOnly(e.Key))

            {

                return;

            }



            var mods = ReadModifiers();

            _shortcut = new HotKeyShortcut(e.Key, mods);

            EndRecording(cancelled: false);

            _onRecord(_action, _shortcut);

            e.Handled = true;

        }



        private void EndRecording(bool cancelled)

        {

            _isRecording = false;

            Root.KeyDown -= Root_KeyDown;

            _button.Content = _shortcut.DisplayText;

        }



        private static Button CreateShortcutButton(string label) =>

            new()

            {

                Content = label,

                MinWidth = 132,

                MinHeight = 32,

                Padding = new Thickness(12, 6, 12, 6),

                HorizontalAlignment = HorizontalAlignment.Right,

                FontFamily = new FontFamily("Segoe UI Variable Text"),

                Background = Application.Current.Resources["ControlFillColorSecondaryBrush"] as Brush,

                BorderBrush = Application.Current.Resources["ControlStrokeColorDefaultBrush"] as Brush,

                BorderThickness = new Thickness(1),

                CornerRadius = new CornerRadius(4),

            };



        private static bool IsModifierOnly(VirtualKey key) =>

            key is VirtualKey.Control or VirtualKey.Shift or VirtualKey.Menu

                or VirtualKey.LeftWindows or VirtualKey.RightWindows;



        private static HotKeyModifiers ReadModifiers()

        {

            var mods = HotKeyModifiers.None;

            if (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu)

                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))

            {

                mods |= HotKeyModifiers.Alt;

            }



            if (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)

                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))

            {

                mods |= HotKeyModifiers.Control;

            }



            if (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)

                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))

            {

                mods |= HotKeyModifiers.Shift;

            }



            if (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.LeftWindows)

                    .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)

                || Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.RightWindows)

                    .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))

            {

                mods |= HotKeyModifiers.Windows;

            }



            return mods;

        }

    }

    private sealed class UpdateWaitDialog
    {
        private readonly ContentDialog _dialog;
        private readonly TextBlock _statusText;
        private readonly TextBlock _detailText;
        private readonly ProgressRing _ring;
        private readonly ProgressBar _progressBar;

        public UpdateWaitDialog(XamlRoot xamlRoot)
        {
            _statusText = new TextBlock { TextWrapping = TextWrapping.WrapWholeWords };
            _detailText = new TextBlock
            {
                Opacity = 0.75,
                FontSize = 12,
                TextWrapping = TextWrapping.WrapWholeWords,
            };
            _ring = new ProgressRing { IsActive = true, Width = 28, Height = 28, HorizontalAlignment = HorizontalAlignment.Left };
            _progressBar = new ProgressBar
            {
                Minimum = 0,
                Maximum = 100,
                Height = 6,
                IsIndeterminate = true,
            };

            var panel = new StackPanel { Spacing = 12, MinWidth = 340 };
            panel.Children.Add(_statusText);
            panel.Children.Add(_ring);
            panel.Children.Add(_progressBar);
            panel.Children.Add(_detailText);

            _dialog = new ContentDialog
            {
                Title = "Updates",
                Content = panel,
                XamlRoot = xamlRoot,
            };
        }

        public void ShowChecking()
        {
            _statusText.Text = "Checking for updates…";
            _detailText.Text = string.Empty;
            _ring.Visibility = Visibility.Visible;
            _ring.IsActive = true;
            _progressBar.Visibility = Visibility.Collapsed;
        }

        public void ShowDownloading(string version, string fileName)
        {
            _statusText.Text = $"Downloading TrimFetch {version}…";
            _detailText.Text = fileName;
            _ring.Visibility = Visibility.Collapsed;
            _ring.IsActive = false;
            _progressBar.Visibility = Visibility.Visible;
            _progressBar.IsIndeterminate = true;
            _progressBar.Value = 0;
        }

        public void ReportDownload(UpdateDownloadProgress progress)
        {
            if (progress.TotalBytes is > 0)
            {
                _progressBar.IsIndeterminate = false;
                _progressBar.Value = progress.Percent;
                _detailText.Text =
                    $"{FormatByteSize(progress.BytesReceived)} / {FormatByteSize(progress.TotalBytes.Value)} ({progress.Percent:0}%) — {progress.FileName}";
            }
            else
            {
                _progressBar.IsIndeterminate = true;
                _detailText.Text = $"{FormatByteSize(progress.BytesReceived)} downloaded — {progress.FileName}";
            }
        }

        public Task<ContentDialogResult> ShowAsync() => _dialog.ShowAsync().AsTask();

        public void Close() => _dialog.Hide();

        private static string FormatByteSize(long bytes)
        {
            if (bytes < 1_024)
            {
                return $"{bytes} B";
            }

            var size = bytes / 1024.0;
            if (size < 1_024)
            {
                return $"{size:0.#} KB";
            }

            size /= 1024.0;
            if (size < 1_024)
            {
                return $"{size:0.#} MB";
            }

            return $"{size / 1024.0:0.#} GB";
        }
    }
}


