# README artwork

- `hero.png` is a composite banner: the Install Hub (light) in front of Tune (dark) on a brand gradient, with rounded corners and soft shadows.
- `tour.gif` is a short animated tour (Overview, Install Hub, Cleanup, Tune, Quiet, Unstick, Fixes, then Cleanup and Tune in dark) cross-faded from the stills below.
- `*-light.png` and `*-dark.png` are real TidyWindow 5 app captures on Windows at 1272×792. Pages with a pair are shown with a `<picture>` element, so GitHub picks the one that matches the reader's theme. Package versions, cleanup sizes and setting states are examples from that machine.

## Refreshing the captures

1. Use the same window size (1280×800 including the invisible resize border), let loading finish, and move the pointer entirely outside the app so the sidebar is not expanded by hover.
2. Capture only the app window, not the screen, so the desktop wallpaper, taskbar and other windows never appear. Check that the pointer and its highlight are absent, and keep local paths, personal filenames and process lists out of the image. Installed, Startup, Lab and Crashes list this PC's own apps and history, so they are deliberately not used.
3. Capturing a scan or settings page does not require running a clean, installing a package or applying a setting.
4. Crop the 8px transparent border (`ffmpeg -i in.png -vf crop=1272:792:0:0 out.png`).

The hero and the GIF are built from those crops with ffmpeg: a gradient canvas with two rounded, shadowed windows for the hero, and an `xfade` slideshow with a two-pass palette for the GIF.
