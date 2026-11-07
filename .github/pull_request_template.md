## What changed and why

<!-- Describe the change and the problem it solves. Link the issue it closes, for example "Closes #123". -->

## How it was tested

<!-- Tick what you ran and add anything you checked by hand. -->

- [ ] `dotnet build TidyWindow.slnx -c Release --no-restore` builds without warnings
- [ ] `dotnet test tests/TidyWindow.Tests/TidyWindow.Tests.csproj -c Release --no-build` passes
- [ ] Added or updated tests for the changed behavior
- [ ] Tried the change in the running app

## Checklist

- [ ] The pull request does one thing; unrelated changes are left out.
- [ ] Updated `packages.lock.json` if a dependency changed.
- [ ] **Catalog changes only:** every entry is tied to documented Windows behavior, the source is linked above, the matching research note in `docs/` is updated, and the original state can be restored.
- [ ] **UI changes only:** checked in light theme, dark theme and high contrast, with the keyboard alone, and new controls have an accessible name. Screenshots are below.
- [ ] Updated the README or `docs/` if user-visible behavior changed.

## Screenshots

<!-- For UI changes, add before and after screenshots in light and dark themes. Delete this section otherwise. -->
