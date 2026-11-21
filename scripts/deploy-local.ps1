<#
.SYNOPSIS
    Builds the plugin and installs it into a local Jellyfin server (Windows), then restarts the server.

.PARAMETER DataDir
    Jellyfin data directory (contains the "plugins" folder).

.PARAMETER JellyfinExe
    Path to jellyfin.exe.

.PARAMETER ServerUrl
    URL used to wait for the server to come back.
#>
param(
    [string]$DataDir = "C:\ProgramData\Jellyfin\Server",
    [string]$JellyfinExe = "C:\Program Files\Jellyfin\Server\jellyfin.exe",
    [string]$ServerUrl = "http://localhost:8096"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\Jellyfin.Plugin.CueSplitter\Jellyfin.Plugin.CueSplitter.csproj"

[xml]$csproj = Get-Content $project
$version = $csproj.Project.PropertyGroup.AssemblyVersion | Where-Object { $_ } | Select-Object -First 1
$targetAbi = (Select-String -Path (Join-Path $root "build.yaml") -Pattern '^targetAbi:\s*"?([\d.]+)"?').Matches[0].Groups[1].Value

Write-Host "Building $version..."
dotnet build $project -c Release --nologo -v q | Out-Host
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

$outDir = Join-Path $root "src\Jellyfin.Plugin.CueSplitter\bin\Release\net10.0"
$pluginDir = Join-Path $DataDir "plugins\CUE Splitter_$version"

$trayExe = Join-Path (Split-Path -Parent $JellyfinExe) "jellyfin-windows-tray\Jellyfin.Windows.Tray.exe"
$useTray = (Get-Process Jellyfin.Windows.Tray -ErrorAction SilentlyContinue) -and (Test-Path $trayExe)

Write-Host "Stopping Jellyfin..."
Get-Process Jellyfin.Windows.Tray, jellyfin -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2

# Remove other installed versions of this plugin.
Get-ChildItem (Join-Path $DataDir "plugins") -Directory -Filter "CUE Splitter_*" | Where-Object { $_.FullName -ne $pluginDir } | Remove-Item -Recurse -Force

New-Item -ItemType Directory -Force $pluginDir | Out-Null
Copy-Item (Join-Path $outDir "Jellyfin.Plugin.CueSplitter.dll") $pluginDir -Force
Copy-Item (Join-Path $outDir "Jellyfin.Plugin.CueSplitter.pdb") $pluginDir -Force -ErrorAction SilentlyContinue

$meta = [ordered]@{
    category    = "Music"
    changelog   = ""
    description = "Shows the tracks of single-file album images described by a .cue sheet as individual songs, without splitting the original file."
    guid        = "ad5ffba7-aeea-42e5-a9fe-e3f8e8daaf4c"
    name        = "CUE Splitter"
    overview    = "Virtual tracks from CUE sheets"
    owner       = "local"
    targetAbi   = $targetAbi
    timestamp   = (Get-Date).ToUniversalTime().ToString("o")
    version     = $version
    status      = "Active"
    autoUpdate  = $false
    assemblies  = @("Jellyfin.Plugin.CueSplitter.dll")
}
$meta | ConvertTo-Json | Set-Content (Join-Path $pluginDir "meta.json") -Encoding utf8

if ($useTray) {
    # Restart through the tray app so that its icon keeps controlling the server.
    Write-Host "Starting Jellyfin through the tray app..."
    Start-Process -FilePath $trayExe
} else {
    Write-Host "Starting Jellyfin..."
    Start-Process -FilePath $JellyfinExe -ArgumentList "--datadir `"$DataDir`"" -WindowStyle Hidden
}

$deadline = (Get-Date).AddMinutes(2)
while ((Get-Date) -lt $deadline) {
    try {
        $info = Invoke-RestMethod "$ServerUrl/System/Info/Public" -TimeoutSec 3
        Write-Host "Jellyfin $($info.Version) is up."
        exit 0
    } catch { Start-Sleep -Seconds 2 }
}
throw "Jellyfin did not come back within 2 minutes"
