<#
.SYNOPSIS
    Per-user install of Slack Huddle Notifier (Windows). No admin required.

.DESCRIPTION
    Publishes the WPF head as a self-contained build (no .NET runtime install
    needed) into %LocalAppData%\Programs\SlackHuddleNotifier, adds a Start Menu
    shortcut, and optionally a Startup-folder shortcut so it launches at logon.

    An existing appsettings.json (your Slack tokens) is preserved on upgrade.

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

$appName      = 'Slack Huddle Notifier'
$installDir   = Join-Path $env:LOCALAPPDATA 'Programs\SlackHuddleNotifier'
$exePath      = Join-Path $installDir 'SlackHuddleNotifier.exe'
$startMenuLnk = Join-Path ([Environment]::GetFolderPath('Programs')) "$appName.lnk"
$startupLnk   = Join-Path ([Environment]::GetFolderPath('Startup'))  "$appName.lnk"
$repoRoot     = Split-Path -Parent $MyInvocation.MyCommand.Path
$project      = Join-Path $repoRoot 'SlackHuddleNotifier\SlackHuddleNotifier.csproj'

function New-Shortcut([string]$linkPath, [string]$target) {
    $shell = New-Object -ComObject WScript.Shell
    $lnk = $shell.CreateShortcut($linkPath)
    $lnk.TargetPath = $target
    $lnk.WorkingDirectory = Split-Path -Parent $target
    $lnk.Description = $appName
    $lnk.Save()
}

if ($Uninstall) {
    Get-Process SlackHuddleNotifier -ErrorAction SilentlyContinue | Stop-Process -Force
    foreach ($item in @($installDir, $startMenuLnk, $startupLnk)) {
        if (Test-Path $item) {
            Remove-Item $item -Recurse -Force
            Write-Host "Removed $item"
        }
    }
    Write-Host "$appName uninstalled."
    return
}

Write-Host "Publishing self-contained build (this can take a minute)..."
$staging = Join-Path $env:TEMP "shn-publish-$([Guid]::NewGuid().ToString('N'))"
dotnet publish $project -c Release -r win-x64 --self-contained -o $staging -v q -nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

# Preserve the user's configured tokens across upgrades.
$existingSettings = Join-Path $installDir 'appsettings.json'
$keepSettings = $null
if (Test-Path $existingSettings) {
    $keepSettings = Get-Content $existingSettings -Raw
    Write-Host "Preserving existing appsettings.json."
}

Get-Process SlackHuddleNotifier -ErrorAction SilentlyContinue | Stop-Process -Force
New-Item -ItemType Directory -Force $installDir | Out-Null
Remove-Item "$installDir\*" -Recurse -Force
Copy-Item "$staging\*" $installDir -Recurse -Force
Remove-Item $staging -Recurse -Force

if ($keepSettings) {
    Set-Content -Path $existingSettings -Value $keepSettings -Encoding utf8
}

New-Shortcut $startMenuLnk $exePath
Write-Host "Start Menu shortcut created."

if ($Autostart) {
    New-Shortcut $startupLnk $exePath
    Write-Host "Startup shortcut created (runs at logon)."
}

Write-Host ""
Write-Host "$appName installed to $installDir"
if (-not $keepSettings) {
    Write-Host "Next step: edit $existingSettings and add your Slack tokens (xapp-/xoxb-)."
}
