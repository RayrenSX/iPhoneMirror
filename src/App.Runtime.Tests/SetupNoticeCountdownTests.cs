using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Updater;
using IPhoneMirror.App.ViewModels;
using IPhoneMirror.App.Windows;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private sealed class NoticeTestClock : TimeProvider
    {
        private long _ticks = 1;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        internal void Advance(TimeSpan elapsed) => _ticks += elapsed.Ticks;
    }

    private static int RunSetupNoticeCountdownTests(string output)
    {
        Directory.CreateDirectory(output);
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var app = new App { IsUiPreviewMode = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        SetKeyboardField(app, "_settingsStore", new UpdateSettingsStore(Path.Combine(Path.GetFullPath(output), "settings.json")));
        var main = CreateWorkspaceTestWindow(app, includeNativePreview: false);
        var vm = (MainViewModel)main.DataContext;
        FirstRunSetupWindow? notice = null;
        try
        {
            foreach (var language in new[] { "zh-CN", "zh-TW", "zh-HK", "en-US", "ja-JP" })
            foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
            {
                LocalizationService.SetLanguage(language); ThemeService.Apply(theme);
                var store = new FirstRunSetupStore(Path.Combine(Path.GetFullPath(output), language + theme + ".json"));
                store.Save(new FirstRunSetupState { Step = SetupStep.FreeSoftwareNotice });
                var clock = new NoticeTestClock();
                notice = new FirstRunSetupWindow(vm, store, previewOnly: true) { Width = 880, Height = 760, NoticeTimeProvider = clock };
                notice.Show(); ToolsPump();
                var next = (Button)notice.FindName("Next");
                var skip = (Button)notice.FindName("Skip");
                InteractionAssert(!next.IsEnabled && next.Content.ToString()!.Contains('3') && !skip.IsVisible,
                    "Entry must show the three-second countdown and hide Skip.");
                next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                KeyboardCall(notice, "SkipClick", skip, new RoutedEventArgs());
                KeyboardCall(notice, "SkipCurrentStep");
                ToolsComplete((Task)KeyboardCall(notice, "AdvanceAsync")!);
                InteractionAssert(notice.State.Step == SetupStep.FreeSoftwareNotice,
                    "Routed clicks, skip and direct advance must all respect the reading delay.");
                if (language == "zh-CN") SaveWindowRender(notice, Path.Combine(output, theme + "-reading-3s.png"));
                clock.Advance(TimeSpan.FromMilliseconds(2999));
                KeyboardCall(notice, "UpdateNoticeCountdown");
                typeof(FirstRunSetupWindow).GetMethod("Render", KeyboardTestMembers | System.Reflection.BindingFlags.DeclaredOnly)!.Invoke(notice, null);
                InteractionAssert(!next.IsEnabled && next.Content.ToString()!.Contains('1'),
                    "The timer must not round up elapsed time or reset on rerender.");
                clock.Advance(TimeSpan.FromMilliseconds(1));
                KeyboardCall(notice, "UpdateNoticeCountdown");
                notice.UpdateLayout(); ToolsPump();
                InteractionAssert(next.IsEnabled && next.Content.ToString() == LocalizationService.Get("SetupNoticeContinue"),
                    "At exactly three seconds the explicit Continue action must become available.");
                if (language == "zh-CN") SaveWindowRender(notice, Path.Combine(output, theme + "-ready.png"));
                next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); ToolsPump();
                InteractionAssert(notice.State.Step == SetupStep.SupportAuthor, "Continue must open voluntary support once the delay ends.");
                var qrImages = ComponentVisuals<Image>(notice).Where(image => image.Source is not null).ToArray();
                InteractionAssert(qrImages.Length == 2 && qrImages.All(image => image.Source.IsFrozen && image.ActualWidth == 192 && image.ActualHeight == 216) &&
                    next.IsEnabled && skip.IsVisible,
                    "Support must show both verified codes at equal size and allow immediate Next or Skip.");
                notice.UpdateLayout(); ToolsPump(180);
                store.Save(notice.State);
                InteractionAssert(store.Load().Step == SetupStep.SupportAuthor, "Support checkpoint was not preserved.");
                if (language == "zh-CN") SaveWindowRender(notice, Path.Combine(output, theme + "-support-author.png"));
                if (language == "zh-CN" && theme == AppTheme.Light)
                {
                    notice.LoadSetupDonationImage = method => method == DonationPaymentMethod.Alipay
                        ? throw new InvalidDataException("Test-only image verification failure") : DonationAssets.Load(method);
                    typeof(FirstRunSetupWindow).GetMethod("Render", KeyboardTestMembers | System.Reflection.BindingFlags.DeclaredOnly)!.Invoke(notice, null);
                    notice.UpdateLayout(); ToolsPump();
                    InteractionAssert(!ComponentVisuals<Image>(notice).Any(image => image.Source is not null) && next.IsEnabled &&
                        ComponentVisuals<TextBlock>(notice).Any(text => text.Text == LocalizationService.Get("DonationIntegrityFailure")),
                        "If either code fails verification, hide both codes and keep setup usable.");
                    notice.LoadSetupDonationImage = DonationAssets.Load;
                }
                next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); ToolsPump();
                InteractionAssert(notice.State.Step == SetupStep.ApplicationMode, "Support must allow continuing without payment.");
                ((Button)notice.FindName("Previous")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); ToolsPump();
                skip.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); ToolsPump();
                InteractionAssert(notice.State.Step == SetupStep.ApplicationMode, "Support Skip must continue without payment.");
                ((Button)notice.FindName("Previous")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); ToolsPump();
                ((Button)notice.FindName("Previous")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); ToolsPump();
                InteractionAssert(notice.State.Step == SetupStep.FreeSoftwareNotice && !next.IsEnabled &&
                    next.Content.ToString()!.Contains('3'), "Reentering the page must start a fresh visible countdown.");
                var timer = (DispatcherTimer)KeyboardField(notice, "_noticeTimer");
                SetKeyboardField(notice, "_allowClose", true); notice.Close();
                InteractionAssert(!timer.IsEnabled, "Closing setup must stop the countdown timer.");
                notice = null;
            }
            Console.WriteLine("PASS notice/support: 3-second reading gate, verified matching QR images, integrity-failure fallback, immediate voluntary skip/continue, saved checkpoints, back/reentry, timer cleanup and both themes in five languages.");
            return 0;
        }
        finally
        {
            if (notice is not null) { SetKeyboardField(notice, "_allowClose", true); notice.Close(); }
            CloseWorkspaceTestWindow(main); app.Shutdown();
        }
    }
}
