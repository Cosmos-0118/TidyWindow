using TidyWindow.App.ViewModels;

namespace TidyWindow.App.ViewModels.Preview;

public interface IPreviewFilter
{
    bool Matches(CleanupPreviewItemViewModel item);
}
