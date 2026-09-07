param([switch]$Run, [switch]$Check)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$dotnet = Join-Path $PSScriptRoot '.tools/dotnet/dotnet.exe'
if (!(Test-Path $dotnet)) { $dotnet = 'dotnet' }
& $dotnet build MYSync.slnx
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
$destination = 'src/MYSync.Desktop/bin/Debug/net10.0-windows/plugins/Sample'
New-Item -ItemType Directory -Path $destination -Force | Out-Null
Get-ChildItem 'src/MYSync.Provider.Sample/bin/Debug/net10.0' -File | Copy-Item -Destination $destination -Force
if ($Check) { & $dotnet run --project tests/MYSync.Checks --no-build -- $PSScriptRoot; if ($LASTEXITCODE -ne 0) { throw 'Checks failed' } }
if ($Run) { & $dotnet 'src/MYSync.Desktop/bin/Debug/net10.0-windows/MYSync.Desktop.dll' }
