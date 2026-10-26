param(
    [Parameter(Mandatory)] [string] $Project,
    [Parameter(Mandatory)] [string] $ReportDirectory,
    [Parameter(Mandatory)] [string] $Config,
    [ValidateSet('prefab')] [string] $Mode = 'prefab',
    [string] $Editor = 'D:\Unity 3D\Editor\6000.0.84f1\Editor\Unity.exe'
)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath($Project)
if (-not $projectRoot.StartsWith('D:\AAAChatGPT\UnityValidation\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Only independent validation projects are permitted.' }
$reports = [IO.Path]::GetFullPath($ReportDirectory)
[IO.Directory]::CreateDirectory($reports) | Out-Null
$validation = Join-Path $projectRoot 'Assets/EndfieldCharacterValidation/Editor'
[IO.Directory]::CreateDirectory($validation) | Out-Null
Copy-Item -LiteralPath (Join-Path (Split-Path -Parent $PSScriptRoot) 'sources/verification/UnityEdgeVerification.cs') -Destination $validation -Force
$start = [Diagnostics.ProcessStartInfo]::new($Editor)
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
foreach ($argument in @('-batchmode', '-quit', '-projectPath', $projectRoot, '-executeMethod', 'EndfieldCharacterWorkbench.Verification.UnityEdgeVerification.PrefabOnly', '-logFile', (Join-Path $reports 'unity.log'))) { $start.ArgumentList.Add($argument) }
$start.Environment['ENDFIELD_EDGE_CONFIG'] = $Config
$resultFile = Join-Path $reports 'prefab-menu.json'
$start.Environment['ENDFIELD_EDGE_REPORT'] = $resultFile
$process = [Diagnostics.Process]::Start($start)
if (-not $process.WaitForExit(180000)) { $process.Kill($true); throw 'Owned verification process exceeded 180 seconds.' }
if (Test-Path -LiteralPath $resultFile) { Get-Content -LiteralPath $resultFile -Raw }
if ($process.ExitCode -ne 0) { throw ('Verification failed. See ' + (Join-Path $reports 'unity.log')) }
$result = Get-Content -LiteralPath $resultFile -Raw | ConvertFrom-Json
if (-not $result.passed) { throw 'Verification report failed.' }
exit 0
