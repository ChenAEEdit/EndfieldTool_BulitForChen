param(
    [Parameter(Mandatory)] [string] $Project,
    [Parameter(Mandatory)] [string] $ReportDirectory,
    [Parameter(Mandatory)] [string[]] $Configs,
    [ValidateSet('mapping', 'knee', 'rig')] [string] $Mode = 'mapping',
    [string] $Editor = 'D:\Unity 3D\Editor\6000.0.84f1\Editor\Unity.exe'
)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath($Project)
if (-not $projectRoot.StartsWith('D:\AAAChatGPT\UnityValidation\', [StringComparison]::OrdinalIgnoreCase)) { throw '仅使用独立验收工程。' }
if ($Configs.Count -eq 0) { throw '至少指定一个适配配置。' }
foreach ($configPath in $Configs) {
    if ($configPath -cnotmatch '^Assets/EndfieldCharacterExports/[0-9a-f]{8}_[0-9a-f]{32}/adaptation\.json$' -or
        -not [IO.File]::Exists([IO.Path]::Combine($projectRoot, $configPath))) { throw "适配配置路径无效或不存在：$configPath" }
}
$projectLock = Join-Path $projectRoot 'Temp/UnityLockfile'
if (Test-Path -LiteralPath $projectLock -PathType Leaf) {
    try { $lockProbe = [IO.File]::Open($projectLock, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None); $lockProbe.Dispose() }
    catch { throw '此独立验收工程正在 Unity 中使用，请先保存并关闭该工程。其他工程无需关闭。' }
}
$repoRoot = Split-Path -Parent $PSScriptRoot
$reports = [IO.Path]::GetFullPath($ReportDirectory)
New-Item -ItemType Directory -Path $reports -Force | Out-Null
$validation = Join-Path $projectRoot 'Assets/EndfieldCharacterValidation/Editor'
New-Item -ItemType Directory -Path $validation -Force | Out-Null
$name = switch ($Mode) { 'knee' { 'UnityGeneratedKneeVerification' } 'rig' { 'UnitySharedRigVerification' } default { 'UnityMappingVerification' } }
Copy-Item -LiteralPath (Join-Path $repoRoot "sources/verification/$name.cs") -Destination $validation -Force
if ($Mode -eq 'knee') { Copy-Item -LiteralPath (Join-Path $repoRoot 'templates/workspace/KneeDiagnostics.cs') -Destination $validation -Force }
$request = Join-Path $reports 'request.json'
@{ directory = $reports; configs = $Configs } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $request -Encoding utf8
$start = [Diagnostics.ProcessStartInfo]::new($Editor)
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
foreach ($argument in @('-batchmode', '-projectPath', $projectRoot, '-executeMethod', "EndfieldCharacterWorkbench.Verification.$name.Run", '-logFile', (Join-Path $reports 'unity.log'))) { $start.ArgumentList.Add($argument) }
$variable = switch ($Mode) { 'knee' { 'ENDFIELD_GENERATED_KNEE_CHECK' } 'rig' { 'ENDFIELD_SHARED_RIG_CHECK' } default { 'ENDFIELD_MAPPING_CHECK' } }
$resultName = switch ($Mode) { 'knee' { 'knee-verification.json' } 'rig' { 'shared-rig-verification.json' } default { 'mapping-capability-verification.json' } }
$start.Environment[$variable] = $request
$owned = [Diagnostics.Process]::Start($start)
try {
    if (-not $owned.WaitForExit(180000)) { throw '验收进程超时。' }
    $result = Get-Content -LiteralPath (Join-Path $reports $resultName) -Raw | ConvertFrom-Json
    if ($owned.ExitCode -ne 0 -or -not $result.passed) { throw '验收失败，请查看原生报告。' }
    Write-Output "PASS $Mode verification: $(Join-Path $reports $resultName)"
}
finally { if (-not $owned.HasExited) { $owned.Kill($true); $owned.WaitForExit() }; $owned.Dispose() }
exit 0
