using System.Globalization;
using System.Text;
using System.Windows;

namespace IPhoneMirror.App.Services;

internal sealed class ScreenTextPage
{
    internal sealed record Word(int Start, int[] Boundaries, Rect Bounds)
    {
        internal int End => Start + Boundaries[^1];

        internal int Caret(double x)
        {
            var fraction = Math.Clamp((x - Bounds.Left) / Bounds.Width, 0, 1);
            return Start + Boundaries[(int)Math.Round(fraction * (Boundaries.Length - 1))];
        }

        internal Rect Highlight(int start, int end)
        {
            var first = Array.BinarySearch(Boundaries, start - Start);
            var last = Array.BinarySearch(Boundaries, end - Start);
            var unit = Bounds.Width / (Boundaries.Length - 1);
            return first < 0 || last <= first ? Rect.Empty :
                new Rect(Bounds.Left + first * unit, Bounds.Top, (last - first) * unit, Bounds.Height);
        }
    }

    internal string Text { get; }
    internal IReadOnlyList<Word> Words { get; }

    internal ScreenTextPage(IEnumerable<(string Text, IEnumerable<(string Text, Rect Bounds)> Words)> lines)
    {
        var text = new StringBuilder();
        var words = new List<Word>();
        foreach (var (line, lineWords) in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (text.Length > 0) text.AppendLine();
            var lineStart = text.Length;
            text.Append(line);
            var previous = 0;
            foreach (var (token, bounds) in lineWords)
            {
                if (token.Length == 0 || bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) continue;
                var index = line.IndexOf(token, previous, StringComparison.Ordinal);
                if (index < 0) continue;
                int[] boundaries = [.. StringInfo.ParseCombiningCharacters(token), token.Length];
                words.Add(new Word(lineStart + index, boundaries, bounds));
                previous = index + token.Length;
            }
        }
        Text = text.ToString();
        Words = words;
    }

    internal Word? HitWord(Point point, bool nearest)
    {
        Word? closest = null;
        var distance = double.PositiveInfinity;
        foreach (var word in Words)
        {
            var rect = word.Bounds;
            var dx = Math.Max(0, Math.Max(rect.Left - point.X, point.X - rect.Right));
            var dy = Math.Max(0, Math.Max(rect.Top - point.Y, point.Y - rect.Bottom));
            if (dx == 0 && dy == 0) return word;
            var score = dx * dx + dy * dy;
            if (nearest && score < distance)
            {
                distance = score;
                closest = word;
            }
        }
        return closest;
    }

    internal IEnumerable<Rect> Highlights(int start, int end)
    {
        foreach (var word in Words)
        {
            if (word.End <= start || word.Start >= end) continue;
            var highlight = word.Highlight(Math.Max(start, word.Start), Math.Min(end, word.End));
            if (!highlight.IsEmpty) yield return highlight;
        }
    }
}
