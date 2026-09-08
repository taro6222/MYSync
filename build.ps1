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
$webDavDestination = 'src/MYSync.Desktop/bin/Debug/net10.0-windows/plugins/WebDAV'
New-Item -ItemType Directory -Path $webDavDestination -Force | Out-Null
Get-ChildItem 'src/MYSync.Provider.WebDav/bin/Debug/net10.0' -File | Copy-Item -Destination $webDavDestination -Force
$googleDestination = 'src/MYSync.Desktop/bin/Debug/net10.0-windows/plugins/GoogleDrive'
New-Item -ItemType Directory -Path $googleDestination -Force | Out-Null
Get-ChildItem 'src/MYSync.Provider.GoogleDrive/bin/Debug/net10.0' -File | Copy-Item -Destination $googleDestination -Force
# Remove legacy loose configuration from this generated plugin output only.
$legacyOAuth = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "$googleDestination/oauth-client.json"))
$debugRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'src/MYSync.Desktop/bin/Debug')) + [IO.Path]::DirectorySeparatorChar
if (!$legacyOAuth.StartsWith($debugRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid OAuth output path' }
if (Test-Path -LiteralPath $legacyOAuth) { Remove-Item -LiteralPath $legacyOAuth -Force }
if ($Check) { & $dotnet run --project tests/MYSync.Checks --no-build -- $PSScriptRoot; if ($LASTEXITCODE -ne 0) { throw 'Checks failed' } }
if ($Run) { & $dotnet 'src/MYSync.Desktop/bin/Debug/net10.0-windows/MYSync.Desktop.dll' }
