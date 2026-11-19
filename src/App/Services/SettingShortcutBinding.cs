namespace IPhoneMirror.App.Services;

public enum PreviewShortcutSetting
{
    Opacity = 0, Topmost = 1, OpaqueOnHover = 2, PhoneFrame = 3,
    Recording = 4, Screenshot = 5, FullScreen = 6, MuteWindow = 7, RotateLeft = 8, RotateRight = 9,
    FixWindow = 10,
}
public enum SettingShortcutTrigger { Hold, Toggle }

// Values are percentages for opacity and 0/1 for boolean settings.
public sealed record SettingShortcutBinding
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public PreviewShortcutSetting Setting { get; init; }
    // Missing fields in older settings retain the original hold behavior.
    public SettingShortcutTrigger Trigger { get; init; }
    public int Modifiers { get; init; }
    public int VirtualKey { get; init; }
    public double Value { get; init; } = 100;
    internal bool IsAction => Setting is PreviewShortcutSetting.Recording or PreviewShortcutSetting.Screenshot or
        PreviewShortcutSetting.FullScreen or PreviewShortcutSetting.MuteWindow or
        PreviewShortcutSetting.RotateLeft or PreviewShortcutSetting.RotateRight or PreviewShortcutSetting.FixWindow;
    internal bool UsesMainPreview => Setting is PreviewShortcutSetting.Recording or PreviewShortcutSetting.Screenshot or PreviewShortcutSetting.FullScreen;
    internal KeyboardShortcut Shortcut => new((uint)Modifiers, (uint)VirtualKey);
    internal bool IsValid => Id != Guid.Empty && Enum.IsDefined(Setting) && Enum.IsDefined(Trigger) &&
        KeyboardShortcut.IsValid(Modifiers, VirtualKey) && double.IsFinite(Value) &&
        (IsAction ? Trigger == SettingShortcutTrigger.Toggle && Value == 0 :
            Setting == PreviewShortcutSetting.Opacity ? Value is >= 10 and <= 100 : Value is 0 or 1);
}
