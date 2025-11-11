namespace TidyWindow.App.Services;

public interface IHighFrictionPromptService
{
    void TryShowPrompt(HighFrictionScenario scenario, ActivityLogEntry entry);
}
