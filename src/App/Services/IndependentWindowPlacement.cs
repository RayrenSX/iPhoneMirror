using System.Drawing;

namespace IPhoneMirror.App.Services;

internal static class IndependentWindowPlacement
{
    internal static Rectangle Place(Rectangle workArea, Rectangle preferred,
        IReadOnlyList<Rectangle> occupied, int gap, int minimumEdge, bool allowCompact)
    {
        if (allowCompact && occupied.Count == 0)
        {
            // Bias the first window only when centering it would split a usable
            // strip of space into two gaps too small for a neighboring preview.
            var portrait = preferred.Height >= preferred.Width;
            var extent = portrait ? workArea.Width : workArea.Height;
            var windowEdge = portrait ? preferred.Width : preferred.Height;
            var neighbor = Math.Max(minimumEdge, (int)Math.Ceiling(windowEdge * 0.75));
            var available = extent - windowEdge - gap;
            if (available >= neighbor && available - neighbor < (extent - windowEdge) / 2)
            {
                if (portrait) preferred.X = Math.Min(preferred.X, workArea.Left + available - neighbor);
                else preferred.Y = Math.Min(preferred.Y, workArea.Top + available - neighbor);
            }
        }
        var placed = FindRoom(workArea, preferred, occupied, gap);
        if (!allowCompact || Overlap(placed, occupied, 0) == 0) return placed;

        // Retain the largest comfortable size that leaves neighboring windows visible.
        for (var percent = 90; percent >= 65; percent -= 5)
        {
            var width = (int)Math.Round(preferred.Width * percent / 100.0);
            var height = (int)Math.Round(preferred.Height * percent / 100.0);
            if (Math.Min(width, height) < minimumEdge) break;
            var compact = new Rectangle(preferred.Left + (preferred.Width - width) / 2,
                preferred.Top + (preferred.Height - height) / 2, width, height);
            var candidate = FindRoom(workArea, compact, occupied, gap);
            if (Overlap(candidate, occupied, 0) == 0) return candidate;
        }
        return placed;
    }

    internal static Rectangle FindRoom(Rectangle workArea, Rectangle preferred,
        IReadOnlyList<Rectangle> occupied, int gap)
    {
        var best = Fit(workArea, preferred);
        if (occupied.Count == 0) return best;
        var bestGapOverlap = Overlap(best, occupied, gap);
        if (bestGapOverlap == 0) return best;
        var bestOverlap = Overlap(best, occupied, 0);

        // Try the preferred position and both sides of each neighboring window.
        // Combining edges also finds openings above and below mixed-size windows.
        var xs = new HashSet<int> { best.Left, workArea.Left, workArea.Right - best.Width };
        var ys = new HashSet<int> { best.Top, workArea.Top, workArea.Bottom - best.Height };
        foreach (var other in occupied)
        {
            AddEdges(other);
            AddEdges(Rectangle.Inflate(other, gap, gap));
        }

        var bestDistance = Distance(best, preferred);
        foreach (var x in xs)
        foreach (var y in ys)
        {
            var candidate = Fit(workArea, new Rectangle(x, y, best.Width, best.Height));
            var overlap = Overlap(candidate, occupied, 0);
            if (overlap > bestOverlap) continue;
            var gapOverlap = Overlap(candidate, occupied, gap);
            var distance = Distance(candidate, preferred);
            if ((overlap, gapOverlap, distance, candidate.Top, candidate.Left)
                .CompareTo((bestOverlap, bestGapOverlap, bestDistance, best.Top, best.Left)) >= 0) continue;
            best = candidate;
            bestOverlap = overlap;
            bestGapOverlap = gapOverlap;
            bestDistance = distance;
        }
        return best;

        void AddEdges(Rectangle obstacle)
        {
            xs.Add(obstacle.Left - best.Width);
            xs.Add(obstacle.Right);
            xs.Add(obstacle.Left);
            xs.Add(obstacle.Right - best.Width);
            ys.Add(obstacle.Top - best.Height);
            ys.Add(obstacle.Bottom);
            ys.Add(obstacle.Top);
            ys.Add(obstacle.Bottom - best.Height);
        }
    }

    private static long Distance(Rectangle bounds, Rectangle preferred)
    {
        var dx = (long)bounds.Left - preferred.Left;
        var dy = (long)bounds.Top - preferred.Top;
        return dx * dx + dy * dy;
    }

    internal static Rectangle Fit(Rectangle workArea, Rectangle bounds) =>
        new(Math.Clamp(bounds.Left, workArea.Left, Math.Max(workArea.Left, workArea.Right - bounds.Width)),
            Math.Clamp(bounds.Top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - bounds.Height)),
            bounds.Width, bounds.Height);

    internal static Rectangle FitOnResizedDisplay(Rectangle workArea, Rectangle bounds)
    {
        var scale = Math.Min(1, Math.Min(workArea.Width / (double)bounds.Width,
            workArea.Height / (double)bounds.Height));
        if (scale >= 1) return Fit(workArea, bounds);
        return Fit(workArea, new Rectangle(bounds.Location, new Size(
            Math.Max(1, (int)Math.Floor(bounds.Width * scale)),
            Math.Max(1, (int)Math.Floor(bounds.Height * scale)))));
    }

    internal static long Overlap(Rectangle bounds, IReadOnlyList<Rectangle> occupied, int gap)
    {
        long total = 0;
        foreach (var other in occupied)
        {
            var intersection = Rectangle.Intersect(bounds, Rectangle.Inflate(other, gap, gap));
            total += (long)intersection.Width * intersection.Height;
        }
        return total;
    }
}
