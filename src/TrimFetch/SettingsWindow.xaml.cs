using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.Storage;
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
        AboutAppIcon.Source = AppIconHelper.CreatePackagedImage(AppBranding.AppIconPngUri);
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

        var dialog = new ContentDialog

        {

            Title = "Checking for updates",

            Content = new ProgressRing { IsActive = true, Width = 28, Height = 28 },

            XamlRoot = Content.XamlRoot,

        };



        _ = dialog.ShowAsync();



        try

        {

            var currentLabel = AppVersion.GetDisplayLabel();

            var result = await _updateChecker.CheckAsync(currentLabel);

            var downloadedUpdate = result.Kind == UpdateCheckResultKind.UpdateAvailable

                ? await _updateChecker.DownloadAsync(result)

                : null;

            dialog.Hide();



            if (result.Kind == UpdateCheckResultKind.UpToDate)

            {

                await ShowMessageAsync($"{AppBranding.ShortName} is up to date", $"You are running {GetVersionLabel()}.");

                return;

            }



            var updateDialog = new ContentDialog

            {

                Title = $"{AppBranding.ShortName} {result.Version} is ready",

                Content = downloadedUpdate is null

                    ? $"A new version is available. You are running {GetVersionLabel()}."

                    : $"The update has been downloaded in the background. You are running {GetVersionLabel()}.",

                PrimaryButtonText = "Update",

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

            dialog.Hide();

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



    private static async Task LaunchDownloadedUpdateAsync(DownloadedUpdate update)

    {

        var file = await StorageFile.GetFileFromPathAsync(update.FilePath);

        _ = await Launcher.LaunchFileAsync(file);

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

}


