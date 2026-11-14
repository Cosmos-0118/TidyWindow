using System.Reflection;

namespace TidyWindow.Next.Core;

/// <summary>One program the terminal setup relies on. <paramref name="Command"/> is what is typed to run it, which is how it is found on the PC.</summary>
public sealed record TerminalTool(string WingetId, string Name, string Command, string Purpose)
{
    public Package ToPackage() => new(ManagerKind.Winget, WingetId, Name, "", "winget");
}

/// <summary>One face of the terminal font: <paramref name="File"/> is its name in the archive and in the Fonts folder, <paramref name="Registration"/> the name it is registered under.</summary>
public sealed record FontFile(string File, string Registration);

/// <summary>What the "Configure your terminal" setup installs and writes. The files are the ones the setup applies; they are embedded so nothing is downloaded to apply them.</summary>
public static class TerminalKit
{
    public const string FontFace = "CaskaydiaCove NFM";
    /// <summary>
    /// The font is downloaded from a fixed release and accepted only if it matches this checksum, so what gets installed machine-wide is exactly
    /// the file that was reviewed, whatever the network or the download host returns. Moving to a newer Nerd Fonts release means changing both.
    /// </summary>
    public const string FontUrl = "https://github.com/ryanoasis/nerd-fonts/releases/download/v3.5.1/CascadiaCode.zip";
    public const string FontSha256 = "1298bf92698afa06185cf1d05e6ae05f2d8a1e8c3cb45ddf4c3035168ab342a1";
    public const long FontMaxBytes = 80L * 1024 * 1024;
    /// <summary>The size of the package, for a progress bar when the server does not say how large the download is.</summary>
    public const long FontApproxBytes = 56_585_471;
    /// <summary>The four faces Windows Terminal needs (regular, bold, italic, bold italic): the archive's file name and the name Windows lists the face under.</summary>
    public static IReadOnlyList<FontFile> FontFiles { get; } =
    [
        new("CaskaydiaCoveNerdFontMono-Regular.ttf", "CaskaydiaCove NFM Regular (TrueType)"),
        new("CaskaydiaCoveNerdFontMono-Bold.ttf", "CaskaydiaCove NFM Bold (TrueType)"),
        new("CaskaydiaCoveNerdFontMono-Italic.ttf", "CaskaydiaCove NFM Italic (TrueType)"),
        new("CaskaydiaCoveNerdFontMono-BoldItalic.ttf", "CaskaydiaCove NFM Bold Italic (TrueType)")
    ];
    public const string SchemeName = "Catppuccin Mocha";
    /// <summary>The id Windows Terminal gives PowerShell 7. It is the same on every PC, because it is derived from the profile's name.</summary>
    public const string PowerShell7ProfileId = "{574e775e-4f2a-5b96-ac1e-a2962a402336}";

    public static IReadOnlyList<TerminalTool> Tools { get; } =
    [
        new("Microsoft.PowerShell", "PowerShell 7", "pwsh", "The modern PowerShell the profile is written for"),
        new("Microsoft.WindowsTerminal", "Windows Terminal", "wt", "Tabs, themes and fonts"),
        new("Git.Git", "Git", "git", "Version control, shown in the prompt"),
        new("Starship.Starship", "Starship", "starship", "The two-line prompt"),
        new("ajeetdsouza.zoxide", "zoxide", "zoxide", "z <folder> jumps to folders you use"),
        new("junegunn.fzf", "fzf", "fzf", "Fuzzy search for history and files"),
        new("eza-community.eza", "eza", "eza", "ls with icons and Git status"),
        new("sharkdp.bat", "bat", "bat", "cat with colours"),
        new("BurntSushi.ripgrep.MSVC", "ripgrep", "rg", "Fast text search"),
        new("sharkdp.fd", "fd", "fd", "Fast file search"),
        new("JesseDuffield.lazygit", "lazygit", "lazygit", "Git in a terminal screen"),
        new("Fastfetch-cli.Fastfetch", "Fastfetch", "fastfetch", "System summary"),
        new("Clement.bottom", "bottom", "btm", "Resource monitor")
    ];

    public static string Profile => Read("terminal-profile.ps1");
    public static string Starship => Read("terminal-starship.toml");
    public static string WindowsTerminal => Read("terminal-windows-terminal.json");

    private static string Read(string name)
    {
        using var stream = typeof(TerminalKit).Assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException($"{name} is missing from TidyWindow.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
