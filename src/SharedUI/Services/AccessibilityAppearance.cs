using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace IPhoneMirror.UI.Services;

/// <summary>System text size and contrast override the selected application palette.</summary>
internal static class AccessibilityAppearance
{
    private static ResourceDictionary? _overrides;

    internal static void Apply(Application application, double? textScale = null, bool? highContrast = null)
    {
        var dictionaries = application.Resources.MergedDictionaries;
        if (_overrides is not null) dictionaries.Remove(_overrides);
        _overrides = new ResourceDictionary();
        var scale = Math.Clamp(textScale ?? ReadTextScale(), 1, 2.25);
        var tokens = dictionaries.FirstOrDefault(dictionary =>
            dictionary.Source?.OriginalString.EndsWith("DesignTokens.xaml", StringComparison.OrdinalIgnoreCase) == true);
        if (tokens is not null)
            foreach (DictionaryEntry entry in tokens)
                if (entry.Key is string key && entry.Value is double size &&
                    (key.Contains("FontSize", StringComparison.Ordinal) || key.EndsWith("LineHeight", StringComparison.Ordinal)))
                    _overrides[key] = size * scale;
        if (highContrast ?? SystemParameters.HighContrast) AddContrastPalette(_overrides);
        dictionaries.Add(_overrides);
    }

    private static double ReadTextScale()
    {
        try
        {
            return Convert.ToDouble(Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Accessibility", "TextScaleFactor", 100),
                CultureInfo.InvariantCulture) / 100;
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or
                                      System.IO.IOException or FormatException or InvalidCastException)
        { return 1; }
    }

    private static void AddContrastPalette(ResourceDictionary resources)
    {
        // Every semantic brush has an explicit contrast role; selected list items
        // retain WindowText on Window with a Highlight border, while filled action
        // buttons use the paired Highlight/HighlightText system colors.
        Add(resources, SystemColors.WindowBrush, WindowBrushKeys);
        Add(resources, SystemColors.WindowTextBrush, TextBrushKeys);
        Add(resources, SystemColors.HighlightBrush, HighlightBrushKeys);
        Add(resources, SystemColors.HighlightTextBrush, HighlightTextBrushKeys);
        Add(resources, SystemColors.GrayTextBrush, DisabledBrushKeys);
        resources["CardShadowColor"] = Colors.Transparent;
    }

    private static void Add(ResourceDictionary resources, Brush brush, string names)
    {
        foreach (var name in names.Split(' ', StringSplitOptions.RemoveEmptyEntries)) resources[name] = brush;
    }

    private const string WindowBrushKeys =
        "BackgroundBrush AppBackgroundBrush AppBackground WindowBackgroundBrush SidebarBrush PanelBrush PanelBackground " +
        "PanelAltBrush PanelRaisedBrush RaisedBackground CardBrush CardHoverBrush WorkspaceCardBrush WorkspaceCardHoverBrush " +
        "SelectionBrush WorkspaceItemBrush WorkspaceItemSelectedBrush DialogSurfaceBrush PrimaryActionDisabledBrush ControlFillBrush " +
        "ControlHoverBrush ControlPressedBrush IconButtonHoverBrush IconButtonPressedBrush WarningSurfaceBrush ErrorSurfaceBrush " +
        "SuccessSurfaceBrush InfoSurfaceBrush OverlayBrush MediaOverlayBrush MediaPlayerScrimBrush MediaPlayerControlFillBrush " +
        "MediaPlayerControlHoverBrush MediaPlayerControlPressedBrush PreviewChromeBrush PreviewPanelAltBrush ComboBackgroundBrush " +
        "ComboHoverBrush ComboOpenBrush ComboPopupBrush ComboItemHoverBrush ComboItemSelectedBrush ComboItemSelectedHoverBrush " +
        "ScrollTrackBrush ScrollTrackHoverBrush";
    private const string TextBrushKeys =
        "BorderBrush BorderSoftBrush TextBrush MutedTextBrush MutedBrush ControlDividerBrush SliderTrackBrush " +
        "ShortcutClearIconBrush SuccessBrush WarningBrush ErrorBrush InfoBrush StatusAppliedBrush StatusPendingBrush StatusFailedBrush " +
        "MediaOverlayTextBrush MediaPlayerSecondaryTextBrush MediaPlayerTrackBrush PreviewBorderBrush PreviewTextBrush PreviewMutedTextBrush " +
        "ScrollThumbBrush";
    private const string HighlightBrushKeys =
        "WorkspaceItemSelectedBorderBrush AccentBrush AccentHoverBrush FocusStrokeBrush PrimaryActionBrush PrimaryActionHoverBrush " +
        "PrimaryActionPressedBrush PrimaryActionBorderBrush PrimaryActionHoverBorderBrush PrimaryActionPressedBorderBrush PrimaryActionFocusBrush " +
        "TextSelectionBrush SliderThumbFillBrush SliderThumbHoverBrush SliderThumbBorderBrush DangerBrush DangerHoverBrush " +
        "DangerPressedBrush DangerBorderBrush DangerHoverBorderBrush DangerPressedBorderBrush MediaPlayerPrimaryBrush MediaPlayerPrimaryHoverBrush " +
        "MediaPlayerPrimaryPressedBrush MediaPlayerPrimaryBorderBrush MediaPlayerProgressBrush MediaPlayerThumbBrush MediaPlayerFocusBrush " +
        "CaptureStartBrush CaptureStartHoverBrush CaptureStartBorderBrush CaptureStopBrush CaptureStopHoverBrush CaptureStopBorderBrush " +
        "ComboBorderHoverBrush ScrollThumbHoverBrush ScrollThumbPressedBrush";
    private const string HighlightTextBrushKeys =
        "OnAccentBrush PrimaryActionTextBrush AboutCheckUpdatesTextBrush DangerButtonTextBrush MediaPlayerPrimaryIconBrush CaptureActionTextBrush SelectedTextBrush";
    private const string DisabledBrushKeys = "DisabledTextBrush PrimaryActionDisabledTextBrush";
}
