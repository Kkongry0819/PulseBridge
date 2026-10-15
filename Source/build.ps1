param([string]$Output = (Join-Path $PSScriptRoot '..\Build'))
$ErrorActionPreference = 'Stop'
$frameworkPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$referencePath = Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8'
if (-not (Test-Path -LiteralPath $referencePath)) { throw '.NET Framework 4.8 Developer Pack is required.' }
$metadataPath = Join-Path $env:WINDIR 'System32\WinMetadata'
$references = @((Join-Path $metadataPath 'Windows.Devices.winmd'),(Join-Path $metadataPath 'Windows.Foundation.winmd'),(Join-Path $metadataPath 'Windows.Storage.winmd'),(Join-Path $referencePath 'System.dll'),(Join-Path $referencePath 'System.Core.dll'),(Join-Path $referencePath 'System.Web.Extensions.dll'))
$references += Get-ChildItem -LiteralPath (Join-Path $referencePath 'Facades') -Filter '*.dll' | ForEach-Object FullName
$arguments = @('/nologo','/noconfig','/target:exe',('/out:' + (Join-Path $PSScriptRoot 'BleHelper.exe')))
$arguments += $references | ForEach-Object { '/reference:' + $_ }
$arguments += Join-Path $PSScriptRoot 'BleHelper.cs'
& (Join-Path $frameworkPath 'csc.exe') $arguments
if ($LASTEXITCODE -ne 0) { throw 'Bluetooth helper build failed.' }
dotnet publish (Join-Path $PSScriptRoot 'PulseBridge.csproj') -c Release -r win-x64 --self-contained true -p:NuGetAudit=false -o $Output
if ($LASTEXITCODE -ne 0) { throw 'Desktop build failed.' }
foreach ($name in @('README.md','status.ps1','TEST-REPORT.txt')) {
    $path = Join-Path $PSScriptRoot ('..\' + $name)
    if (Test-Path -LiteralPath $path) { Copy-Item -LiteralPath $path -Destination $Output }
}
# The repository landing page is concise; ship the full guide with the app.
$guidePath = Join-Path $PSScriptRoot '..\docs\USER-GUIDE.md'
if (Test-Path -LiteralPath $guidePath) { Copy-Item -LiteralPath $guidePath -Destination (Join-Path $Output 'README.md') }
Write-Output ('Ready: ' + (Join-Path $Output 'PulseBridge.exe'))
