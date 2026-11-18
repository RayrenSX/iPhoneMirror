using IPhoneMirror.App.Services;

internal static class WirelessOriginalCapabilityTests
{
    internal static void Run()
    {
        var original = WirelessReceiverConfiguration.DisplayProfiles[0];
        var wide = WirelessReceiverConfiguration.ExperimentalOriginalWideProfile;
        Assert((wide.Width, wide.Height, wide.FrameRate) == (5120u, 2880u, 60u),
            "The diagnostic wide capability must preserve the original portrait height limit.");
        Assert(WirelessReceiverConfiguration.DisplayProfiles.Count == 4 &&
            !WirelessReceiverConfiguration.DisplayProfiles.Contains(wide) &&
            !WirelessReceiverConfiguration.SupportedProfiles.Contains(wide),
            "Experimental capability must not become a UI choice or saved profile.");
        foreach (var value in new string?[] { null, "", " ", "square" })
            Assert(ReferenceEquals(original,
                WirelessReceiverConfiguration.ReceiverCapabilityFor(original, value)),
                "An ordinary launch must retain the existing 2880-square original capability.");
        Assert(ReferenceEquals(wide,
            WirelessReceiverConfiguration.ReceiverCapabilityFor(original, "wide")),
            "Only an explicit wide diagnostic value must select the wide capability.");

        foreach (var product in AppleDisplayCatalog.Products)
            foreach (var identifier in product.Identifiers)
            {
                var actual = WirelessReceiverConfiguration.ReceiverCapabilityFor(original, "native:" + identifier);
                Assert((actual.Width, actual.Height, actual.FrameRate) ==
                    (product.LongEdge, product.LongEdge, 60u),
                    "A native diagnostic must use the exact catalog model's long edge on both axes.");
            }
        var invalid = new[]
        {
            "5120x2880", "3840x2160", "2622x2622", "wide:5120x2880", "WIDE",
            "native:", "native:iPhone99,1", "native:IPHONE18,3", "native: iPhone18,3",
            "native:iPhone18,3:2880", "wide\nprivate-value", "native:../2622",
        };
        foreach (var value in invalid)
            Assert(ReferenceEquals(original,
                WirelessReceiverConfiguration.ReceiverCapabilityFor(original, value)),
                "Unknown modes, dimensions and model identifiers must never alter the default capability.");

        foreach (var scaled in WirelessReceiverConfiguration.DisplayProfiles.Skip(1))
            foreach (var value in invalid.Concat(new[] { "wide", "square", "native:iPhone18,3" }))
                Assert(ReferenceEquals(scaled,
                    WirelessReceiverConfiguration.ReceiverCapabilityFor(scaled, value)),
                    "A diagnostic environment must never change an explicit scaled quality choice.");
        foreach (var legacy in WirelessReceiverConfiguration.SupportedProfiles.Where(
                     WirelessReceiverConfiguration.RequiresOriginalQualityWarning))
            Assert(ReferenceEquals(wide,
                WirelessReceiverConfiguration.ReceiverCapabilityFor(legacy, "wide")),
                "Saved original-quality variants must use the same explicit diagnostic policy.");

        var variable = WirelessReceiverConfiguration.DiagnosticOriginalCapabilityEnvironment;
        var previous = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, "wide");
            Assert(ReferenceEquals(wide, WirelessReceiverConfiguration.ReceiverCapabilityFor(original)),
                "The actual receiver entry point must read the process diagnostic environment.");
            Environment.SetEnvironmentVariable(variable, "native:iPhone18,3");
            var native = WirelessReceiverConfiguration.ReceiverCapabilityFor(original);
            Assert((native.Width, native.Height) == (2622u, 2622u),
                "An explicitly named native model must reach the receiver entry point.");
            Environment.SetEnvironmentVariable(variable, null);
            Assert(ReferenceEquals(original, WirelessReceiverConfiguration.ReceiverCapabilityFor(original)),
                "Clearing the diagnostic variable must restore the production default.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
        Console.WriteLine("PASS wireless original capability: unchanged default, explicit wide/model diagnostics, strict catalog validation, and scaled quality isolation.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
