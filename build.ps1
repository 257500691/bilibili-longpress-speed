# Build dist\BiliLongPress.exe with the built-in .NET Framework compiler (no SDK needed).
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc  = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw 'csc.exe not found (.NET Framework 4.x required)' }

$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force -Path $dist | Out-Null
$out = Join-Path $dist 'BiliLongPress.exe'
$src = Join-Path $root 'src\BiliLongPress.cs'

& $csc /nologo /noconfig /target:winexe /platform:anycpu /optimize+ /codepage:65001 `
    /out:$out `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
    $src

if ($LASTEXITCODE -ne 0) { throw "compile failed, exit code $LASTEXITCODE" }
Write-Host "built: $out"
