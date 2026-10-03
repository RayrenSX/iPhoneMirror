using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IPhoneMirror.App;
using IPhoneMirror.App.Updater;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunPreviewShellAudit(string output)
    {
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        Directory.CreateDirectory(output);
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        typeof(App).GetProperty("IsUiPreviewMode", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(app, true);
        app.InitializeComponent();
        var assembly = typeof(App).Assembly;
        var type = assembly.GetType("IPhoneMirror.App.Windows.NativePreviewWindow")!;
        var constructor = type.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
        var language = assembly.GetType("IPhoneMirror.App.Localization.LocalizationService")!
            .GetMethod("ApplyLanguage", BindingFlags.Static | BindingFlags.NonPublic)!;
        var failures = new List<string>();
        var count = 0;
        using var trace = new AuditTraceListener();
        PresentationTraceSources.DataBindingSource.Listeners.Add(trace);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        try
        {
            foreach (var culture in new[] { "zh-CN", "en-US", "zh-HK", "zh-TW" })
            foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
            foreach (var managed in new[] { false, true })
            {
                var name = $"{culture}-{theme}-{(managed ? "media-shell" : "mirror-shell")}";
                IDisposable? preview = null;
                try
                {
                    language.Invoke(null, [culture, false, false]);
                    ApplyTheme(assembly, theme);
                    // Real native window chrome/menu lifecycle with inert rendering
                    // callbacks: this audits shell UI without a device or GPU stream.
                    var values = constructor.GetParameters().Select(p => p.DefaultValue).ToArray();
                    object?[] required = [390u, 844u, "UI audit preview", (Func<nint, bool>)(_ => true),
                        (Action<nint>)(_ => { }), (Func<nint, bool>)(_ => true), 0UL, 0d, 1d];
                    Array.Copy(required, values, required.Length);
                    void Set(string parameter, object value) => values[Array.FindIndex(constructor.GetParameters(), p => p.Name == parameter)] = value;
                    Set("requestReverseControl", (Action<nint>)(_ => { }));
                    Set("requestUsbControl", (Action<nint>)(_ => { }));
                    Set("requestWirelessControl", (Action<nint>)(_ => { }));
                    Set("showImageSettings", (Action<nint>)(_ => { }));
                    Set("showProjectionSettings", (Action)(() => { }));
                    Set("isAudioEnabled", (Func<bool>)(() => true));
                    Set("connectedDeviceCount", (Func<int>)(() => 2));
                    Set("setAudioEnabled", (Action<bool>)(_ => { }));
                    if (managed) Set("managedContent", new Border { Background = Brushes.Black });
                    preview = (IDisposable)constructor.Invoke(values);
                    void Call(string method, params object[] args) => type.GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(preview, args);
                    Call("ShowInitially");
                    AdvanceDispatcher(TimeSpan.FromMilliseconds(80));
                    var menu = (ContextMenu)type.GetField("_contextMenu", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(preview)!;
                    Call("ShowContextMenu");
                    menu.UpdateLayout();
                    var menuFrame = Visuals(menu).OfType<Border>().First(b => b.Child is ItemsPresenter);
                    if (menuFrame.CornerRadius != new CornerRadius(12))
                        throw new InvalidOperationException("Preview menu did not use the shared popup radius.");
                    foreach (var item in menu.Items.OfType<MenuItem>())
                    {
                        if (string.IsNullOrWhiteSpace(item.Header?.ToString()))
                            throw new InvalidOperationException("Unlabelled preview menu item.");
                        if (!item.HasItems) continue;
                        item.IsSubmenuOpen = true;
                        menu.UpdateLayout();
                        AdvanceDispatcher(TimeSpan.FromMilliseconds(20));
                        var popup = item.Template.FindName("PART_Popup", item) as Popup;
                        if (popup is not { AllowsTransparency: true, Child: Border { CornerRadius.TopLeft: 12 } })
                            throw new InvalidOperationException("Preview submenu did not use transparent rounded chrome.");
                        item.IsSubmenuOpen = false;
                    }
                    var bitmap = new RenderTargetBitmap((int)Math.Ceiling(menu.ActualWidth),
                        (int)Math.Ceiling(menu.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(menu);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using (var stream = File.Create(Path.Combine(output, name + "-menu.png"))) encoder.Save(stream);
                    menu.IsOpen = false;
                    if (!managed) AuditDefaultPopupCorners(assembly);
                    Call("ToggleFullScreen");
                    if (type.GetProperty("IsFullScreen", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(preview) is not true)
                        throw new InvalidOperationException("Preview failed to enter fullscreen.");
                    Call("ToggleFullScreen");
                    if (type.GetProperty("IsFullScreen", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(preview) is not false)
                        throw new InvalidOperationException("Preview failed to leave fullscreen.");
                    if (!managed)
                    {
                        Call("SetProtectedContent", true, "UI audit");
                        AdvanceDispatcher(TimeSpan.FromMilliseconds(40));
                        var overlay = (Window?)type.GetField("_protectedOverlay", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(preview);
                        if (overlay is not { IsVisible: true }) throw new InvalidOperationException("Protected overlay did not open.");
                        SaveWindowRender(overlay, Path.Combine(output, name + "-protected.png"));
                        Call("SetProtectedContent", false, "UI audit");
                    }
                    AuditPreviewStyle(preview, Path.Combine(output, name + "-style.png"), managed);
                    preview.Dispose();
                    if (type.GetField("_styleWindow", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(preview) is not null)
                        throw new InvalidOperationException("Disposed preview leaked its style settings window.");
                    count++;
                }
                catch (Exception error) { failures.Add($"{name}: {error.GetBaseException().Message}"); }
                finally { preview?.Dispose(); }
            }
        }
        finally
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(trace);
            app.Shutdown();
        }
        File.WriteAllText(Path.Combine(output, "preview-shell-results.json"), JsonSerializer.Serialize(
            new { count, failures, bindingErrors = trace.Messages, renderingCallbacks = "inert; no device/video stream" }, new JsonSerializerOptions { WriteIndented = true }));
        foreach (var failure in failures) Console.Error.WriteLine(failure);
        Console.WriteLine($"Preview shell audit: {count} cases, {failures.Count} failures, {trace.Messages.Count} binding diagnostics.");
        return failures.Count == 0 && trace.Messages.Count == 0 ? 0 : 1;
    }

    private static void AuditDefaultPopupCorners(Assembly assembly)
    {
        var window = (Window)Activator.CreateInstance(assembly.GetType("IPhoneMirror.UI.Controls.RoundedWindow")!)!;
        window.Width = 400; window.Height = 150; window.ShowInTaskbar = false;
        var combo = new ComboBox { Margin = new Thickness(24), VerticalAlignment = VerticalAlignment.Top };
        combo.Items.Add("First item"); combo.Items.Add("Second item"); combo.SelectedIndex = 0;
        window.Content = combo;
        var tooltip = new ToolTip { Content = "UI audit tooltip", PlacementTarget = combo };
        try
        {
            window.Show(); window.UpdateLayout(); combo.IsDropDownOpen = true;
            AdvanceDispatcher(TimeSpan.FromMilliseconds(40));
            var popup = (combo.Template.FindName("PART_Popup", combo) ??
                combo.Template.FindName("Popup", combo)) as Popup;
            if (popup is not { AllowsTransparency: true, Child: not null } ||
                combo.Template.FindName("DropDownBorder", combo) is not Border { CornerRadius.TopLeft: 12 })
                throw new InvalidOperationException("Default ComboBox popup has no transparent rounded surface.");
            combo.IsDropDownOpen = false;
            tooltip.IsOpen = true;
            AdvanceDispatcher(TimeSpan.FromMilliseconds(40));
            if (!Visuals(tooltip).OfType<Border>().Any(b => b.CornerRadius.TopLeft > 0 && b.Background != null))
                throw new InvalidOperationException("Default tooltip has no self-drawn rounded surface.");
        }
        finally { tooltip.IsOpen = false; combo.IsDropDownOpen = false; window.Close(); }
    }
}
