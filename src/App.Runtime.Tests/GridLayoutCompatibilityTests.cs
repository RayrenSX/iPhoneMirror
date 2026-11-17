using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunGridLayoutEntryProbe(string assemblyPath, string applicationTypeName)
    {
        // Run in a fresh process: a previously initialized App would conceal a
        // missing initializer in the independently launched driver manager.
        // Preparing the production entry type must protect the very first
        // Grid, without tests setting the switch or loading application XAML.
        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(assemblyPath));
        var applicationType = assembly.GetType(applicationTypeName, throwOnError: true)!;
        RuntimeHelpers.RunClassConstructor(applicationType.TypeHandle);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star), MinWidth = .05, MaxWidth = .1,
        });
        var size = new Size(Math.BitIncrement(.1), 100);
        grid.Measure(size);
        grid.Arrange(new Rect(size));
        InteractionAssert(Math.Abs(grid.ColumnDefinitions[0].ActualWidth - .1) < 1e-8,
            "The application's first Grid did not retain its finite maximum.");
        Console.WriteLine($"PASS first Grid before XAML: {applicationType.FullName}.");
        return 0;
    }

    private static int RunGridLayoutCompatibilityTests()
    {
        var app = new App { IsUiPreviewMode = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        try
        {
            // Exercise the first Grid before loading application resources, just
            // as startup must establish compatibility before any XAML layout.
            TestGridStarSizeBoundaries();
            TestGridMixedDefinitions();
            return 0;
        }
        finally { app.Shutdown(); }
    }

    private static void TestGridStarSizeBoundaries()
    {
        // The default test suite also exercises synthetic Grids without
        // constructing an Application. Run the production startup initializer.
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(App).TypeHandle);
        var cases = 0;
        var largestRoundingDelta = 0d;
        foreach (var dpi in new[] { 1d, 1.25, 1.5, 1.75, 2, 2.25, 2.5, 3 })
        foreach (var rounding in new[] { false, true })
        foreach (var columns in new[] { true, false })
        foreach (var maximum in new[] { .1, .2, 20.1, 100.1, 300.1, 320, 362, 853, 1365 })
        foreach (var steps in new[] { -4, -2, -1, 0, 1, 2, 4 })
        {
            var grid = new Grid { UseLayoutRounding = rounding };
            VisualTreeHelper.SetRootDpi(grid, new DpiScale(dpi, dpi));
            if (columns)
                grid.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = new GridLength(1, GridUnitType.Star),
                    MinWidth = maximum / 2,
                    MaxWidth = maximum,
                });
            else
                grid.RowDefinitions.Add(new RowDefinition
                {
                    Height = new GridLength(1, GridUnitType.Star),
                    MinHeight = maximum / 2,
                    MaxHeight = maximum,
                });

            // A few ULPs around a finite star maximum reproduces the reported
            // SetFinalSizeMaxDiscrepancy IndexOutOfRangeException on .NET 10.
            var available = maximum;
            for (var i = 0; i < Math.Abs(steps); i++)
                available = steps > 0 ? Math.BitIncrement(available) : Math.BitDecrement(available);
            var size = columns ? new Size(available, 100) : new Size(100, available);
            grid.Measure(size);
            grid.Arrange(new Rect(size));
            var actual = columns ? grid.ColumnDefinitions[0].ActualWidth : grid.RowDefinitions[0].ActualHeight;
            // The compatibility allocator balances rounding using a rounded
            // logical-pixel nudge. Allow that small correction at fractional DPI.
            var tolerance = (rounding ? 2 : 0) + 1e-8;
            if (rounding) largestRoundingDelta = Math.Max(largestRoundingDelta, Math.Abs(actual - maximum));
            InteractionAssert(double.IsFinite(actual) && actual >= 0 && Math.Abs(actual - maximum) <= tolerance,
                $"Star allocation exceeded rounding tolerance: columns={columns}, dpi={dpi}, rounding={rounding}, max={maximum:R}, available={available:R}, actual={actual:R}.");
            cases++;
        }
        Console.WriteLine($"PASS Grid star size boundaries: {cases} layouts, rows/columns, 100–300% DPI, rounding on/off, both sides of finite maximums; largest rounding delta={largestRoundingDelta:F3} DIP.");
    }

    private static void TestGridMixedDefinitions()
    {
        RuntimeHelpers.RunClassConstructor(typeof(App).TypeHandle);
        var cases = 0;
        foreach (var dpi in new[] { 1d, 1.25, 1.5, 1.75, 2, 2.25, 2.5, 3 })
        foreach (var rounding in new[] { false, true })
        foreach (var columns in new[] { true, false })
        {
            var grid = new Grid { UseLayoutRounding = rounding };
            VisualTreeHelper.SetRootDpi(grid, new DpiScale(dpi, dpi));
            var lengths = new[] { GridLength.Auto, new GridLength(24), new GridLength(1, GridUnitType.Star),
                new GridLength(2, GridUnitType.Star), new GridLength(12) };
            for (var i = 0; i < lengths.Length; i++)
            {
                if (columns) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = lengths[i] });
                else grid.RowDefinitions.Add(new RowDefinition { Height = lengths[i] });
                var child = new Border { MinWidth = columns && i == 0 ? 22 : 0, MinHeight = !columns && i == 0 ? 22 : 0 };
                if (columns) Grid.SetColumn(child, i); else Grid.SetRow(child, i);
                grid.Children.Add(child);
            }
            var span = new Border { MinWidth = columns ? 20 : 0, MinHeight = columns ? 0 : 20 };
            if (columns) { Grid.SetColumn(span, 2); Grid.SetColumnSpan(span, 2); }
            else { Grid.SetRow(span, 2); Grid.SetRowSpan(span, 2); }
            grid.Children.Add(span);
            if (columns) grid.ColumnDefinitions[3].MinWidth = 24;
            else grid.RowDefinitions[3].MinHeight = 24;

            foreach (var state in new[] { "star", "collapsed", "restored", "fixed" })
            {
                var length = state == "collapsed" ? new GridLength(0) : state == "fixed" ? new GridLength(72.3) : lengths[2];
                var minimum = state == "collapsed" ? 0 : 32;
                if (columns)
                {
                    var definition = grid.ColumnDefinitions[2];
                    definition.Width = length; definition.MinWidth = minimum; definition.MaxWidth = 180.1;
                }
                else
                {
                    var definition = grid.RowDefinitions[2];
                    definition.Height = length; definition.MinHeight = minimum; definition.MaxHeight = 180.1;
                }
                grid.Children[2].Visibility = state == "collapsed" ? Visibility.Collapsed : Visibility.Visible;
                foreach (var available in new[] { 180d, 240, 400.3, Math.BitIncrement(598.3), 800, 1365 })
                {
                    var size = columns ? new Size(available, 100) : new Size(100, available);
                    grid.Measure(size); grid.Arrange(new Rect(size));
                    var actual = Enumerable.Range(0, 5).Select(i => columns
                        ? grid.ColumnDefinitions[i].ActualWidth : grid.RowDefinitions[i].ActualHeight).ToArray();
                    var first = state == "collapsed" ? 0 : state == "fixed" ? 72.3 : Math.Clamp((available - 58) / 3, 32, 180.1);
                    var expected = new[] { 22d, 24, first, available - 58 - first, 12 };
                    var tolerance = rounding ? 2d : 1e-8;
                    var context = $"columns={columns}, dpi={dpi}, rounding={rounding}, state={state}, available={available:R}";
                    InteractionAssert(actual.All(double.IsFinite) && actual.All(value => value >= 0) &&
                        actual.Zip(expected).All(pair => Math.Abs(pair.First - pair.Second) <= tolerance) &&
                        Math.Abs(actual.Sum() - available) <= tolerance,
                        $"Mixed Grid lost space or violated constraints ({context}): [{string.Join(", ", actual)}].");
                    cases++;
                }
            }
        }
        Console.WriteLine($"PASS mixed Grid definitions: {cases} layouts; auto/pixel/weighted star, spans, finite limits, collapse/restore and dynamic width/height changes.");
    }
}
