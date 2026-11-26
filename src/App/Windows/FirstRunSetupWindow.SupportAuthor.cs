using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App.Windows;

public partial class FirstRunSetupWindow
{
    internal Func<DonationPaymentMethod, BitmapSource> LoadSetupDonationImage { get; set; } = DonationAssets.Load;

    private void SupportAuthor()
    {
        var badge = Text("DonationFreeBadge");
        badge.FontWeight = FontWeights.SemiBold;
        badge.TextAlignment = TextAlignment.Center;
        badge.SetResourceReference(TextBlock.ForegroundProperty, "SuccessBrush");
        badge.Margin = new(0, 0, 0, 12);
        Body.Children.Add(badge);

        var recipient = new TextBlock { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new(0, 0, 0, 16) };
        recipient.SetResourceReference(TextBlock.FontSizeProperty, "SecondaryFontSize");
        recipient.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        var label = new Run(); label.SetResourceReference(Run.TextProperty, "DonationRecipientLabel");
        recipient.Inlines.Add(label);
        recipient.Inlines.Add(new Run(DonationAssets.RecipientDisplayName) { FontWeight = FontWeights.SemiBold });
        Body.Children.Add(recipient);

        try
        {
            // Verify both embedded images before displaying either payment code.
            var wechat = LoadSetupDonationImage(DonationPaymentMethod.WeChat);
            var alipay = LoadSetupDonationImage(DonationPaymentMethod.Alipay);
            var cards = new Grid { UseLayoutRounding = true };
            cards.ColumnDefinitions.Add(new());
            cards.ColumnDefinitions.Add(new() { Width = new GridLength(16) });
            cards.ColumnDefinitions.Add(new());
            cards.RowDefinitions.Add(new() { Height = GridLength.Auto });
            cards.RowDefinitions.Add(new() { Height = new GridLength(0) });
            cards.RowDefinitions.Add(new() { Height = GridLength.Auto });
            var first = DonationCard(wechat, "DonationWeChatTitle", "DonationWeChatScanHint", "DonationWeChatQrAccessibleName",
                "SuccessSurfaceBrush", "SuccessBrush", "SetupWeChatDonationQrCode");
            var second = DonationCard(alipay, "DonationAlipayTitle", "DonationScanHint", "DonationQrAccessibleName",
                "InfoSurfaceBrush", "InfoBrush", "SetupAlipayDonationQrCode");
            cards.Children.Add(first); cards.Children.Add(second);
            void ArrangeCards()
            {
                var textScale = cards.TryFindResource("BodyFontSize") is double size ? size / 14 : 1;
                var stacked = cards.ActualWidth < 560 * textScale;
                Grid.SetColumnSpan(first, stacked ? 3 : 1);
                Grid.SetColumn(second, stacked ? 0 : 2);
                Grid.SetColumnSpan(second, stacked ? 3 : 1);
                Grid.SetRow(second, stacked ? 2 : 0);
                cards.RowDefinitions[1].Height = new GridLength(stacked ? 16 : 0);
            }
            cards.SizeChanged += (_, _) => ArrangeCards();
            Body.Children.Add(cards);
        }
        catch (Exception error)
        {
            var warning = Text("DonationIntegrityFailure");
            warning.SetResourceReference(TextBlock.ForegroundProperty, "ErrorBrush");
            Body.Children.Add(warning);
            DiagnosticLogger.Exception("setup", "donation_integrity_failed", error);
        }
    }

    private static Border DonationCard(BitmapSource bitmap, string titleKey, string hintKey, string accessibleKey,
        string surfaceKey, string colorKey, string automationId)
    {
        var content = new StackPanel();
        var title = Text(titleKey, 16);
        title.FontWeight = FontWeights.SemiBold; title.TextAlignment = TextAlignment.Center;
        title.Margin = new(0, 0, 0, 12); title.SetResourceReference(TextBlock.ForegroundProperty, colorKey);
        content.Children.Add(title);
        var image = new Image { Source = bitmap, Width = 192, Height = 216, Stretch = Stretch.Uniform, SnapsToDevicePixels = true };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        System.Windows.Automation.AutomationProperties.SetAutomationId(image, automationId);
        image.SetResourceReference(System.Windows.Automation.AutomationProperties.NameProperty, accessibleKey);
        content.Children.Add(new Border { Child = image, Background = Brushes.White, CornerRadius = new(12),
            Padding = new(8), HorizontalAlignment = HorizontalAlignment.Center });
        var hint = Text(hintKey, 13); hint.TextAlignment = TextAlignment.Center; hint.Margin = new(0, 10, 0, 0);
        hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush"); content.Children.Add(hint);
        var card = new Border { Padding = new(16), CornerRadius = new(14), BorderThickness = new(1), Child = content };
        card.SetResourceReference(Border.BackgroundProperty, surfaceKey); card.SetResourceReference(Border.BorderBrushProperty, colorKey);
        return card;
    }
}
