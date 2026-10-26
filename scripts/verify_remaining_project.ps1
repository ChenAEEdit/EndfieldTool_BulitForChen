param(
    [Parameter(Mandatory)] [string] $Project,
    [Parameter(Mandatory)] [string] $ReportDirectory,
    [Parameter(Mandatory)] [string[]] $Configs,
    [ValidateSet('mapping', 'knee')] [string] $Mode = 'mapping',
    [string] $Editor = 'D:\Unity 3D\Editor\6000.0.84f1\Editor\Unity.exe'
)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath($Project)
if (-not $projectRoot.StartsWith('D:\AAAChatGPT\UnityValidation\', [StringComparison]::OrdinalIgnoreCase)) { throw '仅使用独立验收工程。' }
if (Get-Process Unity -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $Editor }) { throw '请先等待现有 Unity 验收进程结束。' }
$repoRoot = Split-Path -Parent $PSScriptRoot
$reports = [IO.Path]::GetFullPath($ReportDirectory)
New-Item -ItemType Directory -Path $reports -Force | Out-Null
$validation = Join-Path $projectRoot 'Assets/EndfieldCharacterValidation/Editor'
New-Item -ItemType Directory -Path $validation -Force | Out-Null
$name = if ($Mode -eq 'knee') { 'UnityGeneratedKneeVerification' } else { 'UnityMappingVerification' }
Copy-Item -LiteralPath (Join-Path $repoRoot "sources/verification/$name.cs") -Destination $validation -Force
if ($Mode -eq 'knee') { Copy-Item -LiteralPath (Join-Path $repoRoot 'templates/workspace/KneeDiagnostics.cs') -Destination $validation -Force }
$request = Join-Path $reports 'request.json'
@{ directory = $reports; configs = $Configs } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $request -Encoding utf8
$start = [Diagnostics.ProcessStartInfo]::new($Editor)
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
foreach ($argument in @('-batchmode', '-projectPath', $projectRoot, '-executeMethod', "EndfieldCharacterWorkbench.Verification.$name.Run", '-logFile', (Join-Path $reports 'unity.log'))) { $start.ArgumentList.Add($argument) }
$variable = if ($Mode -eq 'knee') { 'ENDFIELD_GENERATED_KNEE_CHECK' } else { 'ENDFIELD_MAPPING_CHECK' }
$resultName = if ($Mode -eq 'knee') { 'knee-verification.json' } else { 'mapping-capability-verification.json' }
$start.Environment[$variable] = $request
$owned = [Diagnostics.Process]::Start($start)
try {
    if (-not $owned.WaitForExit(180000)) { throw '验收进程超时。' }
    $result = Get-Content -LiteralPath (Join-Path $reports $resultName) -Raw | ConvertFrom-Json
    if ($owned.ExitCode -ne 0 -or -not $result.passed) { throw '验收失败，请查看原生报告。' }
    Write-Output "PASS $Mode verification: $(Join-Path $reports $resultName)"
}
finally { if (-not $owned.HasExited) { $owned.Kill($true); $owned.WaitForExit() }; $owned.Dispose() }
