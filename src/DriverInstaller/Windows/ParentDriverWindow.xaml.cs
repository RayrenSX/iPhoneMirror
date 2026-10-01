using System.Windows;
using System.Windows.Input;
using IPhoneMirror.DriverInstaller.Models;
using IPhoneMirror.DriverInstaller.Services;

namespace IPhoneMirror.DriverInstaller.Windows;

public partial class ParentDriverWindow : IPhoneMirror.UI.Controls.RoundedWindow
{
    public AppleDeviceRecord Device { get; }
    public IReadOnlyList<ParentDriverChoice> Choices { get; }
    public ParentDriverChoice? SelectedDriver { get; set; }
    public string Diagnostic { get; }
    internal ParentDriverConsent? Consent { get; private set; }

    internal ParentDriverWindow(AppleDeviceRecord device, IReadOnlyList<ParentDriverChoice> choices, string diagnostic)
    {
        Device = device;
        Choices = choices;
        Diagnostic = diagnostic;
        SelectedDriver = choices.FirstOrDefault(item => item.IsComposite) ?? choices.FirstOrDefault();
        InitializeComponent();
        DataContext = this;
    }

    internal static ParentDriverConsent? ConfirmChange(Window owner, AppleDeviceRecord device,
        ParentDriverAction action, ParentDriverChoice? driver)
    {
        var target = action == ParentDriverAction.Reenumerate
            ? DriverLocalization.Get("ParentReset") : driver!.DisplayText;
        var confirmed = PromptWindow.Confirm(owner, DriverLocalization.Get("ParentConfirmTitle"),
            DriverLocalization.Format("ParentConfirmBody", device.SelectionText, device.InstanceId,
                device.ParentStatusText, target), DriverLocalization.Get("ParentConfirmApply"), danger: true);
        return confirmed ? ParentDriverConsent.Create(device, action, driver) : null;
    }

    private void OnBindClick(object sender, RoutedEventArgs e)
    {
        if (SelectedDriver is null) return;
        Consent = ConfirmChange(this, Device, ParentDriverAction.Bind, SelectedDriver);
        if (Consent is not null) DialogResult = true;
    }

    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        Consent = ConfirmChange(this, Device, ParentDriverAction.Reenumerate, null);
        if (Consent is not null) DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
    private void OnHeaderMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }
}
