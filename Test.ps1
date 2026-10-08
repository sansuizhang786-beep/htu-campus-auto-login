param([switch]$TaskOnly)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
$outDir = Join-Path $root 'test-output'
New-Item -ItemType Directory -Path $outDir -Force | Out-Null
& $compiler /nologo /utf8output /target:exe /r:System.dll /r:System.Core.dll /r:System.Web.Extensions.dll /r:System.Security.dll "/out:$outDir\CoreTests.exe" "$root\Core.cs" "$root\AppSupport.cs" "$root\Worker.cs" "$root\Tests.cs"
if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
if ($TaskOnly) { & (Join-Path $outDir 'CoreTests.exe') --task-tests }
else { & (Join-Path $outDir 'CoreTests.exe') }
if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
