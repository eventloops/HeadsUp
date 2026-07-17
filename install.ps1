<#
.SYNOPSIS
    Per-user install of Heads-Up (Windows). No admin required.

.DESCRIPTION
    Publishes the WPF head as a self-contained build (no .NET runtime install
    needed) into %LocalAppData%\Programs\HeadsUp, adds a Start Menu
    shortcut, and optionally a Startup-folder shortcut so it launches at logon.

    Your configuration is preserved on upgrade: appsettings.json and the
    git-ignored appsettings.Local.json (Slack session, priority users,
    alert toggles).

.PARAMETER Autostart
    Also create a shortcut in the user's Startup folder.

.PARAMETER Uninstall
    Remove the installed app and both shortcuts.

.EXAMPLE
    .\install.ps1                # install/upgrade
    .\install.ps1 -Autostart     # install/upgrade + run at logon
    .\install.ps1 -Uninstall     # remove everything
#>
[CmdletBinding()]
param(
    [switch]$Autostart,
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'

$appName      = 'Heads-Up'
$legacyName   = 'Slack Huddle Notifier'   # pre-rename shortcuts, cleaned up on install/uninstall
$installDir   = Join-Path $env:LOCALAPPDATA 'Programs\HeadsUp'
$exePath      = Join-Path $installDir 'HeadsUp.exe'
$startMenuLnk = Join-Path ([Environment]::GetFolderPath('Programs')) "$appName.lnk"
$startupLnk   = Join-Path ([Environment]::GetFolderPath('Startup'))  "$appName.lnk"
$legacyStartMenuLnk = Join-Path ([Environment]::GetFolderPath('Programs')) "$legacyName.lnk"
$legacyStartupLnk   = Join-Path ([Environment]::GetFolderPath('Startup'))  "$legacyName.lnk"
$legacyLnks   = @($legacyStartMenuLnk, $legacyStartupLnk)
$repoRoot     = Split-Path -Parent $MyInvocation.MyCommand.Path
$project      = Join-Path $repoRoot 'HeadsUp\HeadsUp.csproj'

# The user's config files, preserved across upgrades. appsettings.Local.json is the
# git-ignored override where the app persists secrets and dashboard edits.
$settingsFiles = @('appsettings.json', 'appsettings.Local.json')

function New-Shortcut([string]$linkPath, [string]$target) {
    $shell = New-Object -ComObject WScript.Shell
    $lnk = $shell.CreateShortcut($linkPath)
    $lnk.TargetPath = $target
    $lnk.WorkingDirectory = Split-Path -Parent $target
    $lnk.Description = $appName
    $lnk.Save()
}

if ($Uninstall) {
    Get-Process HeadsUp -ErrorAction SilentlyContinue | Stop-Process -Force
    foreach ($item in (@($installDir, $startMenuLnk, $startupLnk) + $legacyLnks)) {
        if (Test-Path $item) {
            Remove-Item $item -Recurse -Force
            Write-Host "Removed $item"
        }
    }
    Write-Host "$appName uninstalled."
    return
}

Write-Host "Publishing self-contained build (this can take a minute)..."
$staging = Join-Path $env:TEMP "headsup-publish-$([Guid]::NewGuid().ToString('N'))"
dotnet publish $project -c Release -r win-x64 --self-contained -o $staging -v q -nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

# Preserve the user's configuration across upgrades - both appsettings.json and the
# local override holding their Slack session, priority users, and alert settings.
$keepSettings = @{}
foreach ($name in $settingsFiles) {
    $path = Join-Path $installDir $name
    if (Test-Path $path) {
        $keepSettings[$name] = Get-Content $path -Raw
        Write-Host "Preserving existing $name."
    }
}

Get-Process HeadsUp -ErrorAction SilentlyContinue | Stop-Process -Force
New-Item -ItemType Directory -Force $installDir | Out-Null
Remove-Item "$installDir\*" -Recurse -Force
Copy-Item "$staging\*" $installDir -Recurse -Force
Remove-Item $staging -Recurse -Force

foreach ($name in $keepSettings.Keys) {
    Set-Content -Path (Join-Path $installDir $name) -Value $keepSettings[$name] -Encoding utf8
}

New-Shortcut $startMenuLnk $exePath
Write-Host "Start Menu shortcut created."

# Migrate from installs under the old product name: keep their autostart choice,
# then clean up the old-name shortcuts.
if (-not $Autostart -and (Test-Path $legacyStartupLnk)) {
    $Autostart = $true
    Write-Host "Keeping autostart from previous install."
}
foreach ($lnk in $legacyLnks) {
    if (Test-Path $lnk) {
        Remove-Item $lnk -Force
        Write-Host "Removed legacy shortcut $lnk"
    }
}

if ($Autostart) {
    New-Shortcut $startupLnk $exePath
    Write-Host "Startup shortcut created (runs at logon)."
}

Write-Host ""
Write-Host "$appName installed to $installDir"
if ($keepSettings.Count -eq 0) {
    Write-Host "Next step: launch $appName from the Start Menu and sign in to Slack when prompted (see README)."
}
