using System.Windows;
using System.Windows.Input;
using IPhoneMirror.DriverInstaller.Models;
using IPhoneMirror.DriverInstaller.Services;

namespace IPhoneMirror.DriverInstaller.Windows;

public partial class ParentDriverWindow : IPhoneMirror.UI.Controls.RoundedWindow
{
    public sealed record DriverOption(ParentDriverChoice Driver, bool IsRecommended)
    {
        public string PackageText => $"{Driver.Provider} · {Driver.VersionText} · {System.IO.Path.GetFileName(Driver.InfPath)}";
    }

    public AppleDeviceRecord Device { get; }
    public IReadOnlyList<DriverOption> Choices { get; }
    public DriverOption? SelectedDriver { get; set; }
    public string Diagnostic { get; }
    public bool HasChoices => Choices.Count > 0;
    public bool HasDiagnostics => !string.IsNullOrWhiteSpace(Diagnostic);
    public bool ShowDriverListNotice => !HasChoices || HasDiagnostics;
    public string DriverListNotice => DriverLocalization.Get(HasChoices ? "ParentDriverListLimited" : "ParentNoChoices");
    public string CurrentDriverText => DriverLocalization.Format("ParentCurrentDriver",
        string.IsNullOrWhiteSpace(Device.DriverInf) ? DriverLocalization.Get("ParentNoInf") : Device.DriverInf,
        DriverLocalization.Get(Device.IsHealthy ? "ParentHealthy" : "ParentNeedsAttention"));
    internal ParentDriverConsent? Consent { get; private set; }

    internal ParentDriverWindow(AppleDeviceRecord device, IReadOnlyList<ParentDriverChoice> choices, string diagnostic)
    {
        Device = device;
        var recommended = ParentDriverChoice.SelectCaptureDriver(choices);
        Choices = choices.Select(choice => new DriverOption(choice, ReferenceEquals(choice, recommended))).ToArray();
        Diagnostic = diagnostic;
        SelectedDriver = Choices.FirstOrDefault(option => option.IsRecommended) ?? Choices.FirstOrDefault();
        InitializeComponent();
        DataContext = this;
    }

    internal static ParentDriverConsent? ConfirmChange(Window owner, AppleDeviceRecord device,
        ParentDriverAction action, ParentDriverChoice? driver)
    {
        var rebuild = action == ParentDriverAction.Reenumerate;
        var body = rebuild
            ? DriverLocalization.Format("ParentReenumerateConfirmBody", device.SelectionText)
            : DriverLocalization.Format("ParentConfirmBody", device.SelectionText,
                $"{driver!.Provider} · {System.IO.Path.GetFileName(driver.InfPath)} · {driver.VersionText}");
        var confirmed = PromptWindow.Confirm(owner,
            DriverLocalization.Get(rebuild ? "ParentReset" : "ParentConfirmTitle"), body,
            DriverLocalization.Get(rebuild ? "ParentReset" : "ParentConfirmApply"), danger: true);
        return confirmed ? ParentDriverConsent.Create(device, action, driver) : null;
    }

    private void OnBindClick(object sender, RoutedEventArgs e)
    {
        if (SelectedDriver is null) return;
        Consent = ConfirmChange(this, Device, ParentDriverAction.Bind, SelectedDriver.Driver);
        if (Consent is not null) DialogResult = true;
    }

    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        Consent = ConfirmChange(this, Device, ParentDriverAction.Reenumerate, null);
        if (Consent is not null) DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
    private void OnDetailsClick(object sender, RoutedEventArgs e)
    {
        var opening = TechnicalDetails.Visibility != Visibility.Visible;
        TechnicalDetails.Visibility = opening ? Visibility.Visible : Visibility.Collapsed;
        DetailsButton.SetResourceReference(ContentProperty, opening ? "ParentHideDetails" : "ParentShowDetails");
        if (opening)
        {
            DriverContentScroll.UpdateLayout();
            TechnicalDetails.BringIntoView(new Rect(0, 0, TechnicalDetails.ActualWidth, Math.Min(72, TechnicalDetails.ActualHeight)));
        }
    }

    private void OnHeaderMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }
}
