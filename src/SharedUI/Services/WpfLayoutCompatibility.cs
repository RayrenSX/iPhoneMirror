namespace IPhoneMirror.SharedUI.Services;

internal static class WpfLayoutCompatibility
{
    internal static void Enable()
    {
        // WPF's newer star-size allocator can index a removed definition when
        // a finite maximum and the available size differ by a floating-point
        // epsilon (Grid.SetFinalSizeMaxDiscrepancy). Use the compatibility
        // allocator before loading XAML in each executable. WPF caches this
        // switch on first layout, including third-party control templates.
        AppContext.SetSwitch(
            "Switch.System.Windows.Controls.Grid.StarDefinitionsCanExceedAvailableSpace", true);
    }
}
