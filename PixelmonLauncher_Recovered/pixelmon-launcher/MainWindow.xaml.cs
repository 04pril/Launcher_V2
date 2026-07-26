using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace PixelmonLauncher;

public partial class MainWindow : Window
{
    private const string DisplayVersion = "Mon-1.21.1";
    private const string FallbackLaunchVersion = "neoforge-21.1.218";
    private const int DefaultMaxMemoryMb = 4096;

    private static readonly Regex NicknamePattern = new("^[A-Za-z0-9_]{3,16}$", RegexOptions.Compiled);
    private static readonly string LauncherRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "pixelmon-mode-04pril-1.21.1");
    private static readonly string GameDirectory = Path.Combine(LauncherRoot, "game");
    private static readonly string ModsDirectory = Path.Combine(GameDirectory, "mods");
    private static readonly string ShaderpacksDirectory = Path.Combine(GameDirectory, "shaderpacks");
    private static readonly string AppSettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PixelmonLauncher");
    private static readonly string AppSettingsFile = Path.Combine(AppSettingsDirectory, "settings.json");

    private LauncherSettings _settings = new();
    private bool _isLaunching;

    public MainWindow()
    {
        InitializeComponent();
        LoadSettings();
    }

    private void LoadSettings()
    {
        _settings = LauncherSettings.Load(AppSettingsFile);
        NicknameBox.Text = "";
        StatusText.Text = Directory.Exists(GameDirectory) ? "Offline mode ready" : "Game folder not found";
    }

    private void SaveSettings()
    {
        _settings.Nickname = NicknameBox.Text.Trim();
        _settings.DisplayVersion = DisplayVersion;
        _settings.Save(AppSettingsFile);
    }

    private async void LaunchButton_OnClick(object sender, RoutedEventArgs e)
    {
        await LaunchMinecraftAsync();
    }

    private async void LaunchButton_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        await LaunchMinecraftAsync();
    }

    private async Task LaunchMinecraftAsync()
    {
        if (_isLaunching)
        {
            return;
        }

        var nickname = NicknameBox.Text.Trim();
        if (!NicknamePattern.IsMatch(nickname))
        {
            StatusText.Text = "닉네임은 3~16자 영문/숫자/_ 만 가능";
            return;
        }

        _isLaunching = true;
        LaunchButton.IsEnabled = false;
        LaunchButtonText.Text = "실행 중...";
        StatusText.Text = "NeoForge starting...";
        LaunchProgressFill.Width = 0;

        try
        {
            SaveSettings();

            var request = new MinecraftLaunchRequest
            {
                LauncherRoot = LauncherRoot,
                GameDirectory = GameDirectory,
                VersionId = GetSelectedLaunchVersion(),
                Nickname = nickname,
                MaxMemoryMb = DefaultMaxMemoryMb
            };

            var launchTask = MinecraftLaunchService.LaunchAsync(request);
            while (!launchTask.IsCompleted)
            {
                LaunchProgressFill.Width = Math.Min(286, LaunchProgressFill.Width + 12);
                await Task.Delay(45);
            }

            var result = await launchTask;
            LaunchProgressFill.Width = 314;
            StatusText.Text = $"Started PID {result.ProcessId}";
            await Task.Delay(650);
        }
        catch (Exception ex)
        {
            StatusText.Text = "실행 실패: " + ex.Message;
            MessageBox.Show(this, ex.Message, "Pixelmon launch failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            LaunchProgressFill.Width = 0;
            LaunchButtonText.Text = "게임 시작";
            LaunchButton.IsEnabled = true;
            _isLaunching = false;
        }
    }

    private string GetSelectedLaunchVersion()
    {
        return FallbackLaunchVersion;
    }

    private void OpenModsButton_OnClick(object sender, RoutedEventArgs e)
    {
        OpenDirectory(ModsDirectory);
    }

    private void OpenShaderpacksButton_OnClick(object sender, RoutedEventArgs e)
    {
        OpenDirectory(ShaderpacksDirectory);
    }

    private void OpenDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
            StatusText.Text = "Folder opened";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Open folder failed: " + ex.Message;
        }
    }

    private void MinimizeButton_OnClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void WindowDragArea_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed &&
            !IsInteractiveElement(e.OriginalSource as DependencyObject))
        {
            DragMove();
        }
    }

    private static bool IsInteractiveElement(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is ButtonBase or TextBox or ComboBox)
            {
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }
}
