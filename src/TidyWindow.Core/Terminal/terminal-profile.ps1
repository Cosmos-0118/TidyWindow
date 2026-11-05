# ─────────────────────────────────────────────────────────────
#  PowerShell 7 profile — Catppuccin Mocha + Starship + zoxide + fzf
#  Interactive sessions only; kept light for fast startup.
#  Installed by TidyWindow (Overview > Configure your terminal).
#  "Restore previous" there puts your earlier profile back.
# ─────────────────────────────────────────────────────────────
if (-not [Environment]::UserInteractive -or $Host.Name -ne 'ConsoleHost') { return }

[Console]::OutputEncoding = [Console]::InputEncoding = $OutputEncoding = [System.Text.UTF8Encoding]::new()

# ── Environment ──────────────────────────────────────────────
$env:POWERSHELL_TELEMETRY_OPTOUT   = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT   = '1'
$env:STARSHIP_CONFIG               = "$HOME\.config\starship.toml"
$env:BAT_THEME                     = 'OneHalfDark'
$fdSkip = '--exclude .git --exclude node_modules --exclude AppData --exclude .cache --exclude .nuget --exclude .gradle --exclude __pycache__ --exclude target --exclude "WPS*" --exclude "*.tmp"'
$env:FZF_DEFAULT_COMMAND           = "fd --type f --hidden $fdSkip"
$env:FZF_CTRL_T_COMMAND            = $env:FZF_DEFAULT_COMMAND
$env:FZF_ALT_C_COMMAND             = "fd --type d --hidden $fdSkip"
$env:FZF_DEFAULT_OPTS = @(
    '--height=40%', '--layout=reverse', '--border=rounded', '--multi',
    '--color=bg+:#313244,bg:#1E1E2E,spinner:#F5E0DC,hl:#F38BA8',
    '--color=fg:#CDD6F4,header:#F38BA8,info:#CBA6F7,pointer:#F5E0DC',
    '--color=marker:#B4BEFE,fg+:#CDD6F4,prompt:#CBA6F7,hl+:#F38BA8',
    '--color=selected-bg:#45475A'
) -join ' '

# ── PSReadLine: prediction, colours, keys ────────────────────
function script:Fg([string]$hex) {
    $h = $hex.TrimStart('#')
    "`e[38;2;{0};{1};{2}m" -f [Convert]::ToInt32($h.Substring(0,2),16), [Convert]::ToInt32($h.Substring(2,2),16), [Convert]::ToInt32($h.Substring(4,2),16)
}
try { Set-PSReadLineOption -PredictionSource HistoryAndPlugin -PredictionViewStyle ListView -ErrorAction Stop } catch { }  # needs a real VT console
Set-PSReadLineOption -EditMode Windows `
    -HistorySearchCursorMovesToEnd `
    -HistoryNoDuplicates `
    -MaximumHistoryCount 10000 `
    -BellStyle None `
    -Colors @{
        Command            = Fg '#89B4FA'
        Parameter          = Fg '#FAB387'
        String             = Fg '#A6E3A1'
        Number             = Fg '#FAB387'
        Operator           = Fg '#89DCEB'
        Variable           = Fg '#F5C2E7'
        Member             = Fg '#CDD6F4'
        Type               = Fg '#F9E2AF'
        Keyword            = Fg '#CBA6F7'
        Comment            = Fg '#6C7086'
        Error              = Fg '#F38BA8'
        Default            = Fg '#CDD6F4'
        Emphasis           = Fg '#F5E0DC'
        InlinePrediction   = Fg '#6C7086'
        ListPrediction     = Fg '#CBA6F7'
        ListPredictionSelected = "`e[48;2;69;71;90m"
        ContinuationPrompt = Fg '#6C7086'
        Selection          = "`e[48;2;88;91;112m"
    }
Set-PSReadLineKeyHandler -Key Tab        -Function MenuComplete
Set-PSReadLineKeyHandler -Key UpArrow    -Function HistorySearchBackward
Set-PSReadLineKeyHandler -Key DownArrow  -Function HistorySearchForward
Set-PSReadLineKeyHandler -Key Ctrl+Backspace -Function BackwardKillWord
Set-PSReadLineKeyHandler -Key Ctrl+Delete    -Function KillWord
Set-PSReadLineKeyHandler -Key Ctrl+RightArrow -Function ForwardWord
Set-PSReadLineKeyHandler -Key F2 -Function SwitchPredictionView

# ── Cached init for starship / zoxide (avoids spawning them every launch) ──
$script:InitCache = Join-Path $env:LOCALAPPDATA 'pwsh-init-cache'
function script:Get-InitScript([string]$Name, [string[]]$InitArgs) {
    $exe = (Get-Command $Name -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1).Source
    if (-not $exe) { return $null }
    $file = Join-Path $script:InitCache "$Name.ps1"
    if (-not (Test-Path $file) -or (Get-Item $file).LastWriteTime -lt (Get-Item $exe).LastWriteTime) {
        New-Item -ItemType Directory -Force $script:InitCache | Out-Null
        (& $exe @InitArgs | Out-String) | Set-Content -Path $file -Encoding utf8
    }
    $file
}

# ── Prompt (Starship) ────────────────────────────────────────
if ($f = Get-InitScript 'starship' @('init', 'powershell', '--print-full-init')) {
    . $f
    # keep Starship's prompt, and also set a tidy tab title (current folder)
    $global:__StarshipPrompt = $function:prompt
    function global:prompt {
        $p = & $global:__StarshipPrompt
        $leaf = Split-Path -Leaf (Get-Location).ProviderPath
        $Host.UI.RawUI.WindowTitle = if ($leaf) { $leaf } else { (Get-Location).ProviderPath }
        $p
    }
}

# ── zoxide: z <dir>, zi (interactive) ────────────────────────
if ($f = Get-InitScript 'zoxide' @('init', 'powershell', '--cmd', 'z')) { . $f }

# ── fzf integration: Ctrl+R history, Ctrl+T files, Alt+C dirs ─
# PSFzf takes ~220 ms to import, so it loads lazily on the first Ctrl+R / Ctrl+T / Alt+C
# (it then rebinds those keys to its own handlers).
function global:Enable-Fzf {
    Import-Module PSFzf -Global
    Set-PsFzfOption -PSReadlineChordProvider 'Ctrl+t' -PSReadlineChordReverseHistory 'Ctrl+r' -PSReadlineChordSetLocation 'Alt+c'
}
Set-PSReadLineKeyHandler -Key Ctrl+t -BriefDescription 'Fuzzy file finder'    -ScriptBlock { Enable-Fzf; Invoke-FzfPsReadlineHandlerProvider }
Set-PSReadLineKeyHandler -Key Ctrl+r -BriefDescription 'Fuzzy history search' -ScriptBlock { Enable-Fzf; Invoke-FzfPsReadlineHandlerHistory }
Set-PSReadLineKeyHandler -Key Alt+c  -BriefDescription 'Fuzzy cd'             -ScriptBlock { Enable-Fzf; Invoke-FzfPsReadlineHandlerSetLocation }

# ── winget tab completion ────────────────────────────────────
Register-ArgumentCompleter -Native -CommandName winget -ScriptBlock {
    param($wordToComplete, $commandAst, $cursorPosition)
    [Console]::InputEncoding = [Console]::OutputEncoding = $OutputEncoding = [System.Text.UTF8Encoding]::new()
    $word = $wordToComplete.Replace('"', '""')
    $ast  = $commandAst.ToString().Replace('"', '""')
    winget complete --word="$word" --commandline "$ast" --position $cursorPosition | ForEach-Object {
        [System.Management.Automation.CompletionResult]::new($_, $_, 'ParameterValue', $_)
    }
}

# ── Listings (eza) ───────────────────────────────────────────
if (Get-Command eza -ErrorAction SilentlyContinue) {
    Remove-Alias ls -Force -ErrorAction SilentlyContinue
    function ls { eza --icons --group-directories-first @args }
    function ll { eza -l  --icons --git --group-directories-first --time-style=relative @args }
    function la { eza -la --icons --git --group-directories-first --time-style=relative @args }
    function lt { eza --tree --level=2 --icons --group-directories-first @args }
}

# ── Viewing / searching ──────────────────────────────────────
if (Get-Command bat -ErrorAction SilentlyContinue) { Set-Alias cat bat -Option AllScope }
if (Get-Command rg  -ErrorAction SilentlyContinue) { function search { rg --smart-case --hidden --glob '!.git' @args } }

# ── Navigation ───────────────────────────────────────────────
function c    { Clear-Host }
function ..   { Set-Location .. }
function ...  { Set-Location ..\.. }
function .... { Set-Location ..\..\.. }
function mkcd ([Parameter(Mandatory)][string]$Path) { New-Item -ItemType Directory -Force $Path | Out-Null; Set-Location $Path }
function open ([string]$Path = '.') { Invoke-Item $Path }
function which ([Parameter(Mandatory)][string]$Name) { (Get-Command $Name -All).Source | Where-Object { $_ } }

# ── Git ──────────────────────────────────────────────────────
Remove-Alias gp, gl, gc -Force -ErrorAction SilentlyContinue
function gs  { git status -sb @args }
function gl  { git log --oneline --graph --decorate -20 @args }
function gp  { git push @args }
function gpl { git pull @args }
function ga  { git add @args }
function gc  { git commit @args }
function gco { git checkout @args }
function gb  { git branch @args }
function gd  { git diff @args }
function lg  { lazygit @args }

# ── Extras ───────────────────────────────────────────────────
function ff  { fastfetch @args }
function top { btm @args }

# Reload this profile in the current session
function reload {
    $ExecutionContext.SessionState.InvokeCommand.InvokeScript($false, [scriptblock]::Create(". '$PROFILE'"), $null, $null)
    Write-Host 'profile reloaded' -ForegroundColor Green
}

# ── Chocolatey tab-completion (from the old profile), loaded lazily on first `choco` call ──
$global:ChocolateyProfile = "$env:ChocolateyInstall\helpers\chocolateyProfile.psm1"
if ($env:ChocolateyInstall -and (Test-Path $global:ChocolateyProfile)) {
    function global:choco {
        Remove-Item Function:\choco -ErrorAction SilentlyContinue
        Import-Module $global:ChocolateyProfile -Global
        & (Get-Command choco -CommandType Application | Select-Object -First 1) @args
    }
}
