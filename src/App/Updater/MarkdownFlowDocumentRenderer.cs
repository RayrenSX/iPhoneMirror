using System.Diagnostics;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using IPhoneMirror.App.Localization;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace IPhoneMirror.App.Updater;

internal static class MarkdownFlowDocumentRenderer
{
    private static readonly MarkdownPipeline Pipeline =
        new MarkdownPipelineBuilder().UseAutoLinks().Build();

    internal static FlowDocument Render(string markdown)
    {
        var flow = new FlowDocument
        {
            PagePadding = new Thickness(0),
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            FontSize = 13,
            LineHeight = 21,
        };
        flow.SetResourceReference(FlowDocument.ForegroundProperty, "TextBrush");
        flow.SetResourceReference(FlowDocument.FontFamilyProperty, "AppFontFamily");
        var document = Markdown.Parse(markdown ?? string.Empty, Pipeline);
        foreach (var block in document)
            AddBlock(flow.Blocks, block);
        if (flow.Blocks.Count == 0)
            flow.Blocks.Add(new Paragraph(new Run(LocalizationService.Get("ReleaseNotesEmpty"))));
        return flow;
    }

    private static void AddBlock(BlockCollection target, Markdig.Syntax.Block block)
    {
        switch (block)
        {
            case HeadingBlock heading:
                var headingParagraph = CreateParagraph(heading.Inline);
                headingParagraph.FontWeight = FontWeights.SemiBold;
                headingParagraph.FontSize = heading.Level switch
                {
                    1 => 22,
                    2 => 18,
                    _ => 15,
                };
                headingParagraph.Margin = new Thickness(0,
                    heading.Level == 1 ? 0 : 16, 0, 8);
                target.Add(headingParagraph);
                break;
            case ParagraphBlock paragraph:
                var paragraphElement = CreateParagraph(paragraph.Inline);
                paragraphElement.Margin = new Thickness(0, 0, 0, 10);
                target.Add(paragraphElement);
                break;
            case Markdig.Syntax.ListBlock listBlock:
                var list = new System.Windows.Documents.List
                {
                    MarkerStyle = listBlock.IsOrdered
                        ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                    Margin = new Thickness(18, 0, 0, 10),
                    Padding = new Thickness(0),
                };
                foreach (var itemBlock in listBlock.OfType<ListItemBlock>())
                {
                    var item = new System.Windows.Documents.ListItem();
                    foreach (var child in itemBlock)
                        AddBlock(item.Blocks, child);
                    list.ListItems.Add(item);
                }
                target.Add(list);
                break;
            case QuoteBlock quote:
                var section = new Section
                {
                    Margin = new Thickness(14, 4, 0, 12),
                    Padding = new Thickness(12, 2, 0, 2),
                    BorderThickness = new Thickness(3, 0, 0, 0),
                };
                section.SetResourceReference(System.Windows.Documents.Block.BorderBrushProperty, "AccentBrush");
                foreach (var child in quote)
                    AddBlock(section.Blocks, child);
                target.Add(section);
                break;
            case CodeBlock code:
                target.Add(WithBrush(new Paragraph(new Run(code.Lines.ToString()))
                {
                    FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                    FontSize = 12,
                    Padding = new Thickness(12),
                    Margin = new Thickness(0, 4, 0, 12),
                }, TextElement.BackgroundProperty, "ControlFillBrush"));
                break;
            case ThematicBreakBlock:
                target.Add(WithBrush(new Paragraph
                {
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Margin = new Thickness(0, 6, 0, 14),
                }, System.Windows.Documents.Block.BorderBrushProperty, "BorderBrush"));
                break;
            case ContainerBlock container:
                foreach (var child in container)
                    AddBlock(target, child);
                break;
        }
    }

    private static T WithBrush<T>(T element, DependencyProperty property, string key)
        where T : FrameworkContentElement
    {
        element.SetResourceReference(property, key);
        return element;
    }

    private static Paragraph CreateParagraph(ContainerInline? inline)
    {
        var paragraph = new Paragraph();
        if (inline is not null) AddInlines(paragraph.Inlines, inline);
        return paragraph;
    }

    private static void AddInlines(InlineCollection target, ContainerInline container)
    {
        for (var inline = container.FirstChild; inline is not null;
             inline = inline.NextSibling)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    target.Add(new Run(literal.Content.ToString()));
                    break;
                case CodeInline code:
                    target.Add(WithBrush(new Run(code.Content)
                    {
                        FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                    }, TextElement.BackgroundProperty, "ControlFillBrush"));
                    break;
                case LineBreakInline:
                    target.Add(new LineBreak());
                    break;
                case EmphasisInline emphasis:
                    var span = new Span();
                    if (emphasis.DelimiterCount >= 2) span.FontWeight = FontWeights.SemiBold;
                    else span.FontStyle = FontStyles.Italic;
                    AddInlines(span.Inlines, emphasis);
                    target.Add(span);
                    break;
                case LinkInline link:
                    var hyperlink = WithBrush(new Hyperlink(), TextElement.ForegroundProperty, "AccentBrush");
                    AddInlines(hyperlink.Inlines, link);
                    if (Uri.TryCreate(link.Url, UriKind.Absolute, out var uri) &&
                        uri.Scheme is "https" or "http")
                    {
                        hyperlink.NavigateUri = uri;
                        hyperlink.RequestNavigate += (_, args) =>
                            Process.Start(new ProcessStartInfo(args.Uri.AbsoluteUri)
                            {
                                UseShellExecute = true,
                            });
                    }
                    target.Add(hyperlink);
                    break;
                case ContainerInline nested:
                    AddInlines(target, nested);
                    break;
                case HtmlInline html:
                    target.Add(new Run(html.Tag));
                    break;
            }
        }
    }
}
