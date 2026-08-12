param()
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$src = Join-Path $root 'src'
$build = Join-Path $root 'build'
$compiler = @((Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'), (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $compiler) { throw 'The .NET Framework C# compiler was not found.' }
New-Item -ItemType Directory -Path $build -Force | Out-Null
$component = Join-Path $build 'LidWorkModeComponent.dll'
$guard = Join-Path $build 'PowerGuard.exe'
& $compiler /nologo /target:library /platform:anycpu /optimize+ "/out:$component" /reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll (Join-Path $src 'PowerPlan.cs') (Join-Path $src 'LidWorkModeControl.cs')
if ($LASTEXITCODE -ne 0) { throw 'Component compilation failed.' }
& $compiler /nologo /target:winexe /platform:anycpu /optimize+ "/out:$guard" /reference:System.dll /reference:System.Core.dll /reference:System.Runtime.Serialization.dll /reference:System.Security.dll (Join-Path $src 'PowerPlan.cs') (Join-Path $src 'PowerGuard.cs')
if ($LASTEXITCODE -ne 0) { throw 'PowerGuard compilation failed.' }
$test = Start-Process -FilePath $guard -ArgumentList 'self-test' -PassThru -Wait
if ($test.ExitCode -ne 0) { throw "PowerGuard self-test failed: $($test.ExitCode)" }
Get-Item -LiteralPath $component, $guard | Select-Object FullName, Length, LastWriteTime
