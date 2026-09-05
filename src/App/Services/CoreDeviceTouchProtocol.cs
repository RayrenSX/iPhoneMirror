namespace IPhoneMirror.App.Services;

/// <summary>
///   CoreDevice USB 直连触控协议常量。
    ///   所有值通过公开 CoreDevice 协议和本项目实现恢复。
/// </summary>
public static class CoreDeviceTouchProtocol
{
    public const int ProtocolVersion = 2;
    public const string MessageSchema = "iphoneMirror.touch.v2";
    public const string MessageKind = "touch_batch";
    public const string KeyboardMessageKind = "keyboard_batch";
    public const string ButtonMessageKind = "button_event";

    public const string CapabilityTouch5pt = "iphoneMirror.usb_touch.v2";

    public const int MaxSlots = 5;

    public static bool IsNormalizedCoordinate(double value) =>
        double.IsFinite(value) && value is >= 0.0 and <= 1.0;

    public const int DigitizerSurfaceMainTouchscreen = 257;
    public const int DigitizerSurfaceGesture = 1281;
    public const int KeyboardSurface = 512;
    public const int MainScreenButtonsSurface = 1026;

    public const byte TouchscreenReportId = 0x09;
    public const byte DigitizerReportId = 0x13;
    public const byte KeyboardReportId = 0x01;

    public const byte TouchscreenStateContact = 0xC2;
    public const byte TouchscreenStateRelease = 0x02;

    public const byte HidButtonStateDown = 1;
    public const byte HidButtonStateUp = 2;
    public const byte HidButtonStateCanceled = 3;

    public const string ServiceUniversalHid = "com.apple.coredevice.hid.universalhidservice";
    public const string ServiceIndigo = "com.apple.coredevice.hid.indigo";
    public const string ServiceDisplay = "com.apple.coredevice.displayservice";
    public const string ServiceDeviceControl = "com.apple.coredevice.devicecontrol";

    public const string FeatureUniversalHid = "com.apple.coredevice.feature.remote.universalhidservice";
    public const string FeatureStartMediaStream = "com.apple.coredevice.feature.startmediastream";
    public const string FeatureStopMediaStream = "com.apple.coredevice.feature.stopmediastream";
    public const string FeatureHidButton = "com.apple.coredevice.feature.remote.hid.button";
    public const string FeatureHidDigitizer = "com.apple.coredevice.feature.remote.hid.digitizer";
    public const string FeatureHidScroll = "com.apple.coredevice.feature.remote.hid.scroll";

    public const int ErrorRemoteControlGate = 9021;

    public const int MaxKeyboardUsages = 30;

    // CoreDevice Indigo hardware-button usages (HID Consumer page).
    public const ushort IndigoConsumerUsagePage = 0x0C;
    public const ushort IndigoHome = 0x40;
    public const ushort IndigoLock = 0x30;
    public const ushort IndigoVolumeUp = 0xE9;
    public const ushort IndigoVolumeDown = 0xEA;
    public const ushort IndigoMute = 0xE2;
    public const ushort IndigoSiri = 0xCF;
}
