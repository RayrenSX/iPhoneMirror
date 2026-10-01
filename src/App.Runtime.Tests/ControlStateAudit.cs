using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using IPhoneMirror.App;
using IPhoneMirror.App.Updater;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunControlStateAudit(string output)
    {
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        Directory.CreateDirectory(output);
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        typeof(App).GetProperty("IsUiPreviewMode", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(app, true);
        app.InitializeComponent();
        var panel = new StackPanel { Margin = new Thickness(24) };
        var window = new Window { Width = 440, SizeToContent = SizeToContent.Height, Content = panel, ShowInTaskbar = false };
        window.SetResourceReference(Control.BackgroundProperty, "DialogSurfaceBrush");
        var input = new TextBox { Text = "Readable path / 只读诊断 / 唯讀診斷", IsReadOnly = true,
            Style = (Style)app.FindResource("RoundedTextBox") };
        var check = new CheckBox { Content = "_Enable diagnostics / 启用诊断 / 啟用診斷", IsThreeState = true, Margin = new Thickness(0, 12, 0, 12) };
        var actions = new WrapPanel { Style = (Style)app.FindResource("DialogActions") };
        var cancel = new Button { Content = "Cancel / 取消", Margin = (Thickness)app.FindResource("DialogActionMargin") };
        var confirm = new Button { Content = "_Continue after confirming device trust / 确认后继续", Margin = cancel.Margin,
            Style = (Style)app.FindResource("PrimaryButton") };
        actions.Children.Add(cancel); actions.Children.Add(confirm);
        panel.Children.Add(input); panel.Children.Add(check); panel.Children.Add(actions);
        app.MainWindow = window;
        window.Show();
        try
        {
            foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
            {
                ApplyTheme(typeof(App).Assembly, theme);
                foreach (var fontSize in new[] { 13.0, 22.0 })
                {
                    input.FontSize = check.FontSize = cancel.FontSize = confirm.FontSize = fontSize;
                    input.IsEnabled = check.IsEnabled = true;
                    window.UpdateLayout(); DrainDispatcher();
                    foreach (var control in new Control[] { check, cancel, confirm })
                    {
                        var label = FindVisualDescendant<TextBlock>(control, _ => true);
                        if (label?.FontSize != fontSize)
                            throw new InvalidOperationException($"{control.GetType().Name} label size {label?.FontSize} ignored the requested font size {fontSize}.");
                    }
                    input.SelectAll();
                    if (input.SelectedText != input.Text || input.Opacity != 1 ||
                        ((Border)input.Template.FindName("InputRoot", input)).Opacity != 1)
                        throw new InvalidOperationException("Read-only diagnostics must remain selectable and fully readable.");
                    if (check.ActualHeight < 32 || check.FocusVisualStyle is null || !check.Focus())
                        throw new InvalidOperationException("Checkbox click area or keyboard focus is unavailable.");
                    var toggle = (IToggleProvider)new CheckBoxAutomationPeer(check).GetPattern(PatternInterface.Toggle)!;
                    check.IsChecked = false;
                    foreach (bool? expected in new bool?[] { true, null, false })
                    {
                        toggle.Toggle(); window.UpdateLayout();
                        if (check.IsChecked != expected) throw new InvalidOperationException("Checkbox tri-state sequence regressed.");
                        var mark = (System.Windows.Shapes.Path)check.Template.FindName(expected is null ? "IndeterminateMark" : "CheckMark", check);
                        if (mark.IsVisible != (expected is not false)) throw new InvalidOperationException("Checkbox state is not visually identifiable.");
                    }
                    var cancelBounds = cancel.TransformToAncestor(actions).TransformBounds(new Rect(cancel.RenderSize));
                    var confirmBounds = confirm.TransformToAncestor(actions).TransformBounds(new Rect(confirm.RenderSize));
                    if (confirmBounds.Top <= cancelBounds.Top || confirmBounds.Top - cancelBounds.Bottom < 7.9)
                        throw new InvalidOperationException("Wrapped dialog actions need at least eight DIP vertical separation.");
                    AssertVisibleButtonsFit(window);
                    SaveWindowRender(window, System.IO.Path.Combine(output, $"{theme}-{fontSize}-enabled.png"));
                    input.IsEnabled = check.IsEnabled = false;
                    check.IsChecked = null;
                    window.UpdateLayout(); DrainDispatcher();
                    var disabledColor = ((SolidColorBrush)app.FindResource("DisabledTextBrush")).Color;
                    if (((SolidColorBrush)input.Foreground).Color != disabledColor ||
                        ((SolidColorBrush)check.Foreground).Color != disabledColor || check.Opacity != 1)
                        throw new InvalidOperationException("Disabled inputs must retain the shared readable foreground.");
                    SaveWindowRender(window, System.IO.Path.Combine(output, $"{theme}-{fontSize}-disabled.png"));
                }
            }
        }
        finally { window.Close(); app.Shutdown(); }
        Console.WriteLine("Control states: 2 themes × 2 text sizes; read-only selection, focus, three states, disabled readability and wrapped action spacing passed.");
        return 0;
    }
}
