param([string]$InstallRoot = "$env:LOCALAPPDATA\Programs\QuotaRing")

$ErrorActionPreference = 'Stop'
$sdks = dotnet --list-sdks
if ($LASTEXITCODE -ne 0 -or -not ($sdks -match '^10\.')) {
    throw 'Install .NET 10 SDK: winget install Microsoft.DotNet.SDK.10'
}
Get-Process -Name QuotaRing -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet publish src\QuotaRing\QuotaRing.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $InstallRoot
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
Start-Process -FilePath (Join-Path $InstallRoot 'QuotaRing.exe') -WindowStyle Hidden
Write-Host "$InstallRoot - Startup shortcut created on first launch"
