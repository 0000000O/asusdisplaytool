$ErrorActionPreference = 'Stop'
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$src = Join-Path $PSScriptRoot 'AsusModeTool.cs'
$out = Join-Path $PSScriptRoot 'AsusModeTool.exe'

& $csc /nologo /target:winexe /platform:x64 /optimize+ /warn:4 /utf8output `
  /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
  /out:$out $src

if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE" }
Get-Item $out | Select-Object FullName, Length, LastWriteTime | Format-List
