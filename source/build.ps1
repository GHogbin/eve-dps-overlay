$ErrorActionPreference = 'Stop'
$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
if (-not (Test-Path -LiteralPath $compilerPath)) {
    $compilerPath = Join-Path $env:WINDIR 'Microsoft.NET/Framework/v4.0.30319/csc.exe'
}
if (-not (Test-Path -LiteralPath $compilerPath)) { throw 'The Windows .NET Framework C# compiler was not found.' }
$appOutput = Join-Path (Split-Path -Parent $PSScriptRoot) 'EveDps.exe'
$sourceFiles = @('Combat.cs', 'App.cs', 'SelfTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$iconPath = Join-Path $PSScriptRoot 'EveDps.ico'
$iconArguments = @()
if (Test-Path -LiteralPath $iconPath) { $iconArguments = @("/win32icon:$iconPath") }
& $compilerPath /nologo /target:winexe /platform:anycpu /optimize+ /warn:4 "/out:$appOutput" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Xml.dll $iconArguments $sourceFiles
if ($LASTEXITCODE -ne 0) { throw 'EVE DPS build failed.' }
Write-Output "Built $appOutput"
