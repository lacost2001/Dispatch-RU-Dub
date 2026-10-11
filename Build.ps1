$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$outputDirectory = Join-Path $PSScriptRoot 'bin'
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
& $compiler /nologo /target:winexe ("/out:" + (Join-Path $outputDirectory 'Dispatch-RU-Patcher.exe')) /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll (Join-Path $PSScriptRoot 'DispatchRuPatcher.cs') (Join-Path $PSScriptRoot 'DispatchRuUpdater.cs')
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed' }
