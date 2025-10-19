using System;

namespace TidyWindow.App.ViewModels;

public sealed class NavigationItemViewModel
{
    public NavigationItemViewModel(string title, string description, Type pageType)
    {
        Title = title;
        Description = description;
        PageType = pageType;
    }

    public string Title { get; }

    public string Description { get; }

    public Type PageType { get; }
}
