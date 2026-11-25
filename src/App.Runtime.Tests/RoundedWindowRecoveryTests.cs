using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Updater;
using IPhoneMirror.App.ViewModels;
using IPhoneMirror.App.Windows;
using Wpf.Ui.Appearance;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunRoundedWindowRecoveryTests(string output)
    {
        Directory.CreateDirectory(output);
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        RenderOptions.ProcessRenderMode = RenderMode.Default;
        var app = new App { IsUiPreviewMode = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        var owner = CreateWorkspaceTestWindow(app, includeNativePreview: false);
        var vm = (MainViewModel)owner.DataContext;
        var failures = new List<string>();
        var samples = new List<object>();
        var changes = new List<object>();
        var cover = new Window
        {
            Width = 1060, Height = 900, Background = Brushes.DarkSlateGray,
            Style = null, ShowInTaskbar = false, Title = "Rounded-window recovery fixture",
        };
        try
        {
            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
            {
                ThemeService.Apply(theme);
                foreach (var window in new Window[]
                {
                    new AboutWindow(app, vm),
                    new ShortcutSettingsWindow(new Dictionary<BluetoothShortcutAction, KeyboardShortcut>(), _ => null),
                    (Window)Activator.CreateInstance(typeof(AppPromptWindow), BindingFlags.Instance | BindingFlags.NonPublic,
                        null, ["Recovery test", "Foreground recovery must preserve all four transparent corners.",
                            false, true, "OK", false, IPhoneMirror.UI.Controls.StatusTone.Info], null)!,
                })
                {
                    window.Owner = owner;
                    window.ShowInTaskbar = false;
                    var descriptor = DependencyPropertyDescriptor.FromProperty(Window.BackgroundProperty, window.GetType());
                    EventHandler changed = (_, _) => changes.Add(new
                    {
                        window = window.GetType().Name, theme = theme.ToString(), background = window.Background?.ToString(),
                        stack = Environment.StackTrace,
                    });
                    descriptor.AddValueChanged(window, changed);
                    try
                    {
                        ThemeService.Apply(theme);
                        ThemeService.Attach(window);
                        window.Show();
                        AdvanceDispatcher(TimeSpan.FromMilliseconds(300));
                        Check("first-show");
                        // The owner's theme backend propagates its Mica backdrop
                        // to owned HWNDs, including per-pixel-alpha dialogs.
                        ThemeService.Apply(theme == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark);
                        AdvanceDispatcher(TimeSpan.FromMilliseconds(250));
                        Check("owner-theme-refresh");
                        cover.Show(); cover.Activate();
                        AdvanceDispatcher(TimeSpan.FromMilliseconds(60));
                        window.Activate();
                        RoundedRecoverySetActiveWindow(new WindowInteropHelper(window).Handle);
                        AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
                        Check("background-foreground");
                        cover.Hide();
                        window.Hide();
                        ThemeService.Apply(theme == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark);
                        AdvanceDispatcher(TimeSpan.FromMilliseconds(220));
                        window.Show(); window.Activate();
                        AdvanceDispatcher(TimeSpan.FromMilliseconds(250));
                        Check("hidden-theme-reopen");
                        window.WindowState = WindowState.Minimized;
                        AdvanceDispatcher(TimeSpan.FromMilliseconds(60));
                        window.WindowState = WindowState.Normal;
                        window.Activate();
                        AdvanceDispatcher(TimeSpan.FromMilliseconds(150));
                        Check("minimize-restore");

                        // Exercise the actual theme backend. Its HWND background
                        // is not included in RenderTargetBitmap, so inspect both.
                        WindowBackgroundManager.UpdateBackground(window,
                            theme == AppTheme.Dark ? ApplicationTheme.Dark : ApplicationTheme.Light,
                            Wpf.Ui.Controls.WindowBackdropType.None);
                        AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
                        Check("theme-backend-refresh");

                        // Recover an already-corrupted compositor surface even
                        // when the WPF Background brush did not change.
                        var target = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)!.CompositionTarget!;
                        for (var cycle = 0; cycle < 3; cycle++)
                        {
                            cover.Show(); cover.Activate();
                            RoundedRecoverySetActiveWindow(new WindowInteropHelper(cover).Handle);
                            AdvanceDispatcher(TimeSpan.FromMilliseconds(60));
                            target.BackgroundColor = Colors.MediumVioletRed;
                            window.Activate();
                            RoundedRecoverySetActiveWindow(new WindowInteropHelper(window).Handle);
                            AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
                            Check($"corrupted-foreground-{cycle}");
                            cover.Hide();
                            window.Hide();
                            target.BackgroundColor = Colors.MediumVioletRed;
                            window.Show(); window.Activate();
                            AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
                            Check($"corrupted-reopen-{cycle}");
                            window.WindowState = WindowState.Minimized;
                            AdvanceDispatcher(TimeSpan.FromMilliseconds(60));
                            target.BackgroundColor = Colors.MediumVioletRed;
                            window.WindowState = WindowState.Normal;
                            AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
                            Check($"corrupted-restore-{cycle}");
                        }

                        void Check(string stage)
                        {
                            window.UpdateLayout();
                            var width = (int)Math.Ceiling(window.ActualWidth);
                            var height = (int)Math.Ceiling(window.ActualHeight);
                            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                            bitmap.Render(window);
                            var cornerAlpha = new List<byte>();
                            foreach (var (x, y) in new[] { (0, 0), (width - 1, 0), (0, height - 1), (width - 1, height - 1) })
                            {
                                var pixel = new byte[4];
                                bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
                                cornerAlpha.Add(pixel[3]);
                            }
                            var target = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)!.CompositionTarget!;
                            var backdropResult = RoundedRecoveryGetDwmAttribute(new WindowInteropHelper(window).Handle,
                                38, out var backdrop, sizeof(int));
                            var transparent = window.Background is SolidColorBrush { Color.A: 0 } &&
                                target.BackgroundColor.A == 0 && cornerAlpha.All(alpha => alpha == 0) &&
                                (backdropResult != 0 || backdrop == 1);
                            var context = $"{theme}-{window.GetType().Name}-{stage}";
                            samples.Add(new { context, background = window.Background?.ToString(),
                                compositionBackground = target.BackgroundColor.ToString(), cornerAlpha, transparent,
                                backdrop, backdropResult, active = window.IsActive,
                                renderingTier = RenderCapability.Tier >> 16 });
                            if (!transparent) failures.Add(context + ": opaque outer background or corner.");
                            if (stage is "hidden-theme-reopen" or "theme-backend-refresh")
                                SaveWindowRender(window, Path.Combine(output, context + ".png"));
                        }
                    }
                    finally
                    {
                        descriptor.RemoveValueChanged(window, changed);
                        window.Close();
                    }
                }
            }
        }
        finally
        {
            cover.Close();
            CloseWorkspaceTestWindow(owner);
            app.Shutdown();
            File.WriteAllText(Path.Combine(output, "recovery-results.json"), JsonSerializer.Serialize(
                new { samples, changes, failures }, new JsonSerializerOptions { WriteIndented = true }));
        }
        foreach (var failure in failures) Console.Error.WriteLine(failure);
        Console.WriteLine($"Rounded window recovery: {samples.Count} samples, {failures.Count} failures.");
        return failures.Count == 0 ? 0 : 1;
    }

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int RoundedRecoveryGetDwmAttribute(nint window, int attribute, out int value, int size);
    [DllImport("user32.dll", EntryPoint = "SetActiveWindow")]
    private static extern nint RoundedRecoverySetActiveWindow(nint window);
}
