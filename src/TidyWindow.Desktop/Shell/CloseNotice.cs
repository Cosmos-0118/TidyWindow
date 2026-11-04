using TidyWindow.Next.Core;

namespace TidyWindow.Next.Desktop;

/// <summary>What the window says while it waits for running work to reach a safe stop before it closes.</summary>
internal static class CloseNotice
{
    public static string Finishing(QueueEntry? running)
    {
        if (running is null) return "Wrapping up. This window closes by itself.";
        if (running.Operation.Fix is not null)
            return $"{running.Operation.Name} is finishing the step it is on. Ending that step half way could leave Windows damaged, so TidyWindow waits for it, puts back anything it changed, and then closes by itself.\n\n" +
                "Close now ends the wait. Do that only if you have to: the fix may be left half done.";
        return $"Stopping {running.Operation.Name}. This window closes by itself.\n\nClose now ends the wait; what was running may be left half done.";
    }
}
