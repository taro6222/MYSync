param()
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$dotnet = Join-Path $PSScriptRoot '.tools/dotnet/dotnet.exe'
if (!(Test-Path $dotnet)) { $dotnet = 'dotnet' }
$output = Join-Path $PSScriptRoot 'build/win-x64'
& $dotnet publish src/MYSync.Desktop/MYSync.Desktop.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o $output
if ($LASTEXITCODE -ne 0) { throw 'Desktop publish failed' }
foreach ($provider in @('Sample','WebDav')) {
    & $dotnet publish "src/MYSync.Provider.$provider/MYSync.Provider.$provider.csproj" -c Release -o "$output/plugins/$provider"
    if ($LASTEXITCODE -ne 0) { throw "Provider publish failed: $provider" }
}
Copy-Item docs/manual-test.md "$output/TEST-GUIDE.md" -Force
Write-Output "Executable: $output/MYSync.Desktop.exe"
