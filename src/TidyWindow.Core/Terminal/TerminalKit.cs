using System.Reflection;

namespace TidyWindow.Next.Core;

/// <summary>One program the terminal setup relies on. <paramref name="Command"/> is what is typed to run it, which is how it is found on the PC.</summary>
public sealed record TerminalTool(string WingetId, string Name, string Command, string Purpose)
{
    public Package ToPackage() => new(ManagerKind.Winget, WingetId, Name, "", "winget");
}

/// <summary>What the "Configure your terminal" setup installs and writes. The files are the ones the setup applies; they are embedded so nothing is downloaded to apply them.</summary>
public static class TerminalKit
{
    public const string FontFace = "CaskaydiaCove NFM";
    public const string FontPage = "https://github.com/ryanoasis/nerd-fonts/releases/latest";
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
