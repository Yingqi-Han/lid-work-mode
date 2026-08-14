param([switch]$LockedMode)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dotnet = $env:YINGQI_DOTNET
if (-not $dotnet) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
if (-not (Test-Path -LiteralPath $dotnet)) { throw 'Set YINGQI_DOTNET to a valid .NET 10 SDK dotnet.exe.' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$build = [System.IO.Path]::GetFullPath((Join-Path $root 'build'))
$rootPrefix = [System.IO.Path]::GetFullPath($root).TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
if (-not $build.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) { throw "Unsafe build path: $build" }
$solution = Join-Path $root 'LidWorkMode.slnx'
New-Item -ItemType Directory -Force -Path $build | Out-Null
Get-ChildItem -LiteralPath $build -Force -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force
$restoreArgs = @('restore', $solution)
if ($LockedMode) { $restoreArgs += '--locked-mode' }
& $dotnet @restoreArgs
if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
& $dotnet build $solution -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
& $dotnet test (Join-Path $root 'tests\LidWorkMode.Tests\LidWorkMode.Tests.csproj') -c Release --no-build --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Unit tests failed.' }
& $dotnet publish (Join-Path $root 'src\PowerGuard\PowerGuard.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true --no-restore -o (Join-Path $build 'guard')
if ($LASTEXITCODE -ne 0) { throw 'PowerGuard publish failed.' }
Copy-Item (Join-Path $root 'src\LidWorkModeComponent\bin\Release\net10.0-windows\LidWorkModeComponent.dll') $build -Force
Copy-Item (Join-Path $build 'guard\PowerGuard.exe') $build -Force
$test = Start-Process (Join-Path $build 'PowerGuard.exe') -ArgumentList 'self-test' -PassThru -Wait
if ($test.ExitCode -ne 0) { throw "PowerGuard self-test failed: $($test.ExitCode)" }
$guardSize = (Get-Item -LiteralPath (Join-Path $build 'PowerGuard.exe')).Length
if ($guardSize -ge 15000000) { throw "PowerGuard size regression: $guardSize bytes." }
Get-Item (Join-Path $build 'LidWorkModeComponent.dll'), (Join-Path $build 'PowerGuard.exe') | Select-Object FullName, Length, LastWriteTime
