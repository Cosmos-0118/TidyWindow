using System;

namespace TidyWindow.App.ViewModels;

public sealed class NavigationItemViewModel
{
    public NavigationItemViewModel(string title, string description, string iconGlyph, Type pageType)
    {
        Title = title;
        Description = description;
        IconGlyph = iconGlyph;
        PageType = pageType;
    }

    public string Title { get; }

    public string Description { get; }

    public string IconGlyph { get; }

    public Type PageType { get; }
}
