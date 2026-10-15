param([string]$ReportPath = '')
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$appPath = Join-Path $repositoryRoot 'EveDps.exe'
if (-not (Test-Path -LiteralPath $appPath)) { & (Join-Path $PSScriptRoot 'build.ps1') }
if (-not $ReportPath) { $ReportPath = Join-Path $repositoryRoot 'work/test-results.txt' }
$ReportPath = [IO.Path]::GetFullPath($ReportPath)
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $ReportPath) | Out-Null
$testProcess = Start-Process -FilePath $appPath -ArgumentList @('--self-test', ('"' + $ReportPath + '"')) -WindowStyle Hidden -PassThru -Wait
if (Test-Path -LiteralPath $ReportPath) { Get-Content -LiteralPath $ReportPath }
if ($testProcess.ExitCode -ne 0) { throw "Tests failed with exit code $($testProcess.ExitCode). See $ReportPath" }
