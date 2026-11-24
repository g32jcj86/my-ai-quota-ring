param(
    [string]$InstallRoot = "$env:LOCALAPPDATA\Programs\QuotaRing",
    [switch]$KeepSettings
)

$ErrorActionPreference = 'Stop'
$installPath = [IO.Path]::GetFullPath($InstallRoot)
if ($installPath.TrimEnd('\') -eq [IO.Path]::GetPathRoot($installPath).TrimEnd('\')) {
    throw 'InstallRoot must be an application directory.'
}
Get-Process -Name QuotaRing -ErrorAction SilentlyContinue | Stop-Process -Force
$shortcut = Join-Path ([Environment]::GetFolderPath('Startup')) 'QuotaRing.lnk'
if (Test-Path -LiteralPath $shortcut) { Remove-Item -LiteralPath $shortcut -Force }
if (Test-Path -LiteralPath $installPath) { Remove-Item -LiteralPath $installPath -Recurse -Force }
if (-not $KeepSettings) {
    $settings = Join-Path $env:LOCALAPPDATA 'QuotaRing'
    if (Test-Path -LiteralPath $settings) { Remove-Item -LiteralPath $settings -Recurse -Force }
}
Write-Host 'QuotaRing was removed.'
