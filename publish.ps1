param()
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$dotnet = Join-Path $PSScriptRoot '.tools/dotnet/dotnet.exe'
if (!(Test-Path $dotnet)) { $dotnet = 'dotnet' }
$destination = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'build/win-x64'))
$output = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ('build/.publish-' + [Guid]::NewGuid().ToString('N'))))
New-Item -ItemType Directory -Path $output -Force | Out-Null
& $dotnet publish src/MYSync.Desktop/MYSync.Desktop.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o $output
if ($LASTEXITCODE -ne 0) { throw 'Desktop publish failed' }
foreach ($provider in @('Sample','WebDav','GoogleDrive')) {
    & $dotnet publish "src/MYSync.Provider.$provider/MYSync.Provider.$provider.csproj" -c Release -o "$output/plugins/$provider"
    if ($LASTEXITCODE -ne 0) { throw "Provider publish failed: $provider" }
}
Get-ChildItem $output -Recurse -File -Include *.pdb,*.runtimeconfig.json | Remove-Item -Force
Get-ChildItem $output -Recurse -File -Filter 'MYSync.Provider.Abstractions.dll' | Remove-Item -Force
Get-ChildItem $output -Recurse -File -Filter 'MYSync.Sync.Core.dll' | Remove-Item -Force
Copy-Item docs/manual-test.md "$output/TEST-GUIDE.md" -Force
Copy-Item docs/google-drive-setup.md "$output/google-drive-setup.md" -Force
Copy-Item docs/google-drive-transfer.md "$output/google-drive-transfer.md" -Force
# OAuth configuration is embedded in the Google plugin; no loose JSON is published.
$buildRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'build')) + [IO.Path]::DirectorySeparatorChar
if (!$destination.StartsWith($buildRoot, [StringComparison]::OrdinalIgnoreCase) -or !$output.StartsWith($buildRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid publish path' }
if (Test-Path -LiteralPath $destination) {
    if (((Get-Item -LiteralPath $destination).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Publish destination must not be a link' }
    $active = Get-Process 'MYSync.Desktop' -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq (Join-Path $destination 'MYSync.Desktop.exe') }
    if ($active) { throw "MYSync is running. Exit via the tray before publishing. New build is preserved at $output" }
    $backup = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ('.tools/publish-backups/' + [Guid]::NewGuid().ToString('N'))))
    $backupRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '.tools/publish-backups')) + [IO.Path]::DirectorySeparatorChar
    if (!$backup.StartsWith($backupRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid backup path' }
    New-Item -ItemType Directory -Path (Split-Path $backup) -Force | Out-Null
    Move-Item -LiteralPath $destination -Destination $backup
}
Move-Item -LiteralPath $output -Destination $destination
Write-Output "Executable: $destination/MYSync.Desktop.exe"
