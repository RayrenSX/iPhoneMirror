using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App.Windows;

public partial class FirstRunSetupWindow
{
    internal TimeProvider NoticeTimeProvider { get; set; } = TimeProvider.System;
    private DispatcherTimer? _noticeTimer;
    private long _noticeStarted;
    private bool _noticeClockStarted;
    private TextBlock? _noticeCountdownText;
    private ProgressBar? _noticeReadProgress;
    private bool NoticeCanContinue => _state.Step != SetupStep.FreeSoftwareNotice ||
        _noticeClockStarted && NoticeTimeProvider.GetElapsedTime(_noticeStarted) >= TimeSpan.FromSeconds(3);

    private void StopNoticeCountdown()
    {
        _noticeTimer?.Stop();
        _noticeTimer = null;
        _noticeClockStarted = false;
        _noticeCountdownText = null;
        _noticeReadProgress = null;
    }

    private void UpdateNoticeCountdown()
    {
        if (_closed || _state.Step != SetupStep.FreeSoftwareNotice)
        {
            StopNoticeCountdown();
            return;
        }
        UpdateNavigation();
        if (NoticeCanContinue) _noticeTimer?.Stop();
    }

    private void UpdateNoticeNavigation()
    {
        if (_state.Step != SetupStep.FreeSoftwareNotice) return;
        Skip.Visibility = Visibility.Collapsed;
        var elapsed = _noticeClockStarted ? NoticeTimeProvider.GetElapsedTime(_noticeStarted).TotalSeconds : 0;
        var remaining = Math.Max(0, (int)Math.Ceiling(3 - elapsed));
        Next.IsEnabled &= NoticeCanContinue;
        Next.Content = remaining > 0 ? LocalizationService.Format("SetupNoticeCountdownButton", remaining) : L("SetupNoticeContinue");
        if (_noticeCountdownText is not null)
            _noticeCountdownText.Text = remaining > 0 ? LocalizationService.Format("SetupNoticeCountdownHint", remaining) : L("SetupNoticeReadyHint");
        if (_noticeReadProgress is not null) _noticeReadProgress.Value = Math.Clamp(elapsed / 3, 0, 1);
    }

    private void FreeSoftwareNotice()
    {
        var badge = Text("SetupFreeSoftwareBadge", 18);
        badge.TextAlignment = TextAlignment.Center;
        badge.FontWeight = FontWeights.SemiBold;
        badge.Margin = new(0, 0, 0, 16);
        badge.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        Body.Children.Add(badge);

        var content = new StackPanel();
        var title = Text("DonationAntiResaleTitle", 18);
        title.FontWeight = FontWeights.SemiBold;
        title.Margin = new(0, 0, 0, 16);
        title.SetResourceReference(TextBlock.ForegroundProperty, "ErrorBrush");
        content.Children.Add(title);
        foreach (var (headingKey, bodyKey) in new[] {
            ("SetupNoticeFreeTitle", "SetupNoticeFreeBody"),
            ("SetupNoticeSellerTitle", "SetupNoticeSellerBody"),
            ("SetupNoticeRefundTitle", "SetupNoticeRefundBody") })
        {
            var row = new StackPanel { Margin = new(0, 0, 0, bodyKey == "SetupNoticeRefundBody" ? 0 : 12) };
            var heading = Text(headingKey, 20);
            heading.FontWeight = FontWeights.SemiBold;
            heading.Margin = new(0, 0, 0, 6);
            if (headingKey == "SetupNoticeFreeTitle") heading.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
            row.Children.Add(heading);
            var body = Text(bodyKey, 16);
            body.Margin = new(0);
            body.SetResourceReference(TextBlock.LineHeightProperty, "FontSize24");
            row.Children.Add(body);
            content.Children.Add(row);
        }
        var card = new Border
        {
            CornerRadius = new(18), BorderThickness = new(1), Padding = new(24), Child = content
        };
        card.SetResourceReference(Border.BackgroundProperty, "ErrorSurfaceBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "ErrorBrush");
        System.Windows.Automation.AutomationProperties.SetAutomationId(card, "SetupFreeSoftwareNotice");
        Body.Children.Add(card);
        _noticeCountdownText = Text("SetupNoticeReadyHint");
        _noticeCountdownText.TextAlignment = TextAlignment.Center;
        _noticeCountdownText.FontWeight = FontWeights.SemiBold;
        _noticeCountdownText.Margin = new(0, 12, 0, 8);
        _noticeCountdownText.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        System.Windows.Automation.AutomationProperties.SetLiveSetting(_noticeCountdownText,
            System.Windows.Automation.AutomationLiveSetting.Polite);
        Body.Children.Add(_noticeCountdownText);
        _noticeReadProgress = new ProgressBar { Minimum = 0, Maximum = 1, Height = 4, Width = 240,
            HorizontalAlignment = HorizontalAlignment.Center, IsIndeterminate = false, Margin = new(0, 0, 0, 4) };
        Body.Children.Add(_noticeReadProgress);
        if (!_noticeClockStarted)
        {
            _noticeStarted = NoticeTimeProvider.GetTimestamp();
            _noticeClockStarted = true;
            _noticeTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(100) };
            _noticeTimer.Tick += (_, _) => UpdateNoticeCountdown();
            _noticeTimer.Start();
        }
    }
}
