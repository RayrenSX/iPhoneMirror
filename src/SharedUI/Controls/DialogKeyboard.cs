using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace IPhoneMirror.UI.Controls;

/// <summary>Escape invokes the same action as the visible dialog close button.</summary>
public static class DialogKeyboard
{
    public static readonly DependencyProperty IsEscapeActionProperty =
        DependencyProperty.RegisterAttached("IsEscapeAction", typeof(bool),
            typeof(DialogKeyboard), new PropertyMetadata(false, OnChanged));

    public static bool GetIsEscapeAction(DependencyObject target) =>
        (bool)target.GetValue(IsEscapeActionProperty);
    public static void SetIsEscapeAction(DependencyObject target, bool value) =>
        target.SetValue(IsEscapeActionProperty, value);

    private static void OnChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not Button button) return;
        if (args.NewValue is true) button.Loaded += OnLoaded;
        else button.Loaded -= OnLoaded;
    }

    private static void OnLoaded(object sender, RoutedEventArgs args)
    {
        var button = (Button)sender;
        if (Window.GetWindow(button) is not { } window) return;
        // Bubble after controls have consumed Escape (combo popups, shortcut capture).
        void OnKeyDown(object keySender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape || e.Handled || !GetIsEscapeAction(button) ||
                !button.IsVisible || !button.IsEnabled) return;
            e.Handled = true;
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
        }
        void OnUnloaded(object unloadSender, RoutedEventArgs e)
        {
            window.KeyDown -= OnKeyDown;
            button.Unloaded -= OnUnloaded;
        }
        window.KeyDown += OnKeyDown;
        button.Unloaded += OnUnloaded;
    }
}
