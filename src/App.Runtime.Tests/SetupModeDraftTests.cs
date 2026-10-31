using System.Text.Json;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Updater;

namespace IPhoneMirror.App.Runtime.Tests;

internal static class SetupModeDraftTests
{
    internal static void Run()
    {
        static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        foreach (var original in Enum.GetValues<ApplicationDisplayMode>())
        {
            var saved = original;
            var state = new FirstRunSetupState { Step = SetupStep.ApplicationMode };
            Check(state.PrepareApplicationModeDraft(saved) == ApplicationDisplayMode.Lightweight,
                "First setup must recommend Lightweight.");
            Check(saved == original, "Rendering the recommendation changed the saved mode.");
            state.ApplicationModeDraft = ApplicationDisplayMode.Tray;
            Check(saved == original, "Selecting a card changed the saved mode.");

            // Closing checkpoints the unfinished choice, without applying it.
            state = JsonSerializer.Deserialize<FirstRunSetupState>(JsonSerializer.Serialize(state))!;
            Check(state.PrepareApplicationModeDraft(saved) == ApplicationDisplayMode.Tray && saved == original,
                "Resuming setup lost the uncommitted selection or applied it.");
            state.Previous(); state.Next();
            Check(state.PrepareApplicationModeDraft(saved) == ApplicationDisplayMode.Tray,
                "Returning to an unfinished mode page lost its draft.");

            state.Skip();
            Check(saved == original && state.ApplicationModeDraft is null &&
                state.GlobalOutcomes[SetupStep.ApplicationMode] == SetupOutcome.Skipped,
                "Skip must discard the choice and preserve the saved mode.");
            state.Previous();
            Check(state.PrepareApplicationModeDraft(saved) == original,
                "Returning after Skip must show the preserved mode.");

            // Explicit Next commits before recording verification, as the window does.
            state.ApplicationModeDraft = ApplicationDisplayMode.Lightweight;
            saved = state.PrepareApplicationModeDraft(saved);
            state.Record(SetupOutcome.Verified); state.Next(); state.Previous();
            Check(state.PrepareApplicationModeDraft(saved) == saved && state.ApplicationModeDraft == saved,
                "Returning after a committed choice must show the saved mode.");

            var rerun = new FirstRunSetupState { Step = SetupStep.ApplicationMode, PreferencesApplied = true };
            Check(rerun.PrepareApplicationModeDraft(original) == original,
                "Reconfiguration must start from the existing mode.");
            rerun.ApplicationModeDraft = ApplicationDisplayMode.Lightweight;
            rerun.Skip();
            Check(rerun.ApplicationModeDraft is null, "Reconfiguration Skip retained an uncommitted choice.");
        }

        var legacy = JsonSerializer.Deserialize<FirstRunSetupState>("{\"PreferencesApplied\":true,\"Step\":17}")!;
        Check(legacy.PrepareApplicationModeDraft(ApplicationDisplayMode.Tray) == ApplicationDisplayMode.Tray,
            "A legacy checkpoint must preserve the current mode.");
        legacy.ApplicationModeDraft = (ApplicationDisplayMode)999;
        Check(legacy.PrepareApplicationModeDraft(ApplicationDisplayMode.Complete) == ApplicationDisplayMode.Complete,
            "Invalid draft values must fall back to the current mode.");
        Console.WriteLine("PASS setup mode draft: recommendation, selection, close/resume, back, skip, commit, rerun and legacy checkpoints.");
    }
}
