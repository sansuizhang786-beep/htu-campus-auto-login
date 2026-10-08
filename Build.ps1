param([string]$OutputName = 'HTUAutoLogin.exe')
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if ([IO.Path]::GetFileName($OutputName) -ne $OutputName -or -not $OutputName.EndsWith('.exe')) { throw 'OutputName must be a plain .exe filename.' }
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Windows .NET Framework compiler was not found.' }
$common = @('/nologo', '/utf8output', '/optimize+', '/platform:anycpu',
    '/r:System.dll', '/r:System.Core.dll', '/r:System.Web.Extensions.dll',
    '/r:System.Security.dll', '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll')
$sources = @('Program.cs', 'AppSupport.cs', 'Core.cs', 'Worker.cs') | ForEach-Object { Join-Path $root $_ }
& $compiler @common /target:winexe "/out:$root\$OutputName" @sources
if ($LASTEXITCODE -ne 0) { throw 'Application build failed.' }
Write-Output "Built $OutputName"
