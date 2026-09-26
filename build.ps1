<#
.SYNOPSIS
  Builds the shared C++ core (and runs its tests), then the Windows app.

.EXAMPLE
  ./build.ps1            # Release, native architecture
  ./build.ps1 -Run       # build and launch
  ./build.ps1 -Platform ARM64

  Needs: Visual Studio 2022 (Desktop C++ workload), CMake 3.20+, .NET 8 SDK.
#>
param(
    [ValidateSet('x64', 'ARM64')]
    [string]$Platform = $(if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'ARM64' } else { 'x64' }),
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$Run
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$coreBuild = Join-Path $root "core/build/$Platform"

Write-Host "== swingcore ($Platform)" -ForegroundColor Cyan
cmake -S (Join-Path $root 'core') -B $coreBuild -A $Platform -DSWINGCORE_TESTS=ON
if ($LASTEXITCODE) { throw 'CMake configure failed' }
cmake --build $coreBuild --config Release --parallel
if ($LASTEXITCODE) { throw 'Core build failed' }
ctest --test-dir $coreBuild -C Release --output-on-failure
if ($LASTEXITCODE) { throw 'Core tests failed' }

Write-Host "== SwingLoop for Windows ($Configuration, $Platform)" -ForegroundColor Cyan
$project = Join-Path $root 'windows/SwingLoop.Windows/SwingLoop.Windows.csproj'
$rid = if ($Platform -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }
dotnet build $project -c $Configuration -p:Platform=$Platform -r $rid "-p:SwingCoreDir=$coreBuild/Release"
if ($LASTEXITCODE) { throw 'App build failed' }

$model = Join-Path $root 'windows/SwingLoop.Windows/Models/movenet.onnx'
if (-not (Test-Path $model)) {
    Write-Warning "No pose model at $model. Swing detection will use impact sound + motion only. See tools/export_movenet.py."
}

if ($Run) {
    $exe = Get-ChildItem (Join-Path $root "windows/SwingLoop.Windows/bin/$Platform/$Configuration") -Recurse -Filter SwingLoop.exe | Select-Object -First 1
    if (-not $exe) { throw 'SwingLoop.exe not found' }
    & $exe.FullName
}
