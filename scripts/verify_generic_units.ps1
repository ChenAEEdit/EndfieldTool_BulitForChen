param(
    [Parameter(Mandatory)] [string] $Project,
    [Parameter(Mandatory)] [string] $ReportDirectory,
    [string] $Editor = 'D:\Unity 3D\Editor\6000.0.84f1\Editor\Unity.exe'
)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath($Project)
if (-not $projectRoot.StartsWith('D:\AAAChatGPT\UnityValidation\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Use an independent verification project.' }
if (-not (Test-Path -LiteralPath (Join-Path $projectRoot 'ProjectSettings/ProjectVersion.txt'))) { throw 'Create the independent project first.' }
$lock = Join-Path $projectRoot 'Temp/UnityLockfile'
if (Test-Path -LiteralPath $lock) {
    try { $probe = [IO.File]::Open($lock, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None); $probe.Dispose() }
    catch { throw 'Verification project is open.' }
}
$reports = [IO.Path]::GetFullPath($ReportDirectory)
$repo = Split-Path -Parent $PSScriptRoot
$cases = @()
foreach ($directory in Get-ChildItem -LiteralPath $reports -Directory) {
    $casePath = Join-Path $directory.FullName 'case.json'
    if (-not (Test-Path -LiteralPath $casePath)) { continue }
    $case = Get-Content -LiteralPath $casePath -Raw | ConvertFrom-Json
    $relative = 'Assets/GenericFeasibilityUnits/' + $case.role
    $target = Join-Path $projectRoot $relative
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    $fbx = @($case.exported.files | Where-Object kind -eq 'FBX')
    if ($fbx.Count -ne 1) { throw 'Model export is ambiguous.' }
    $destination = Join-Path $target 'Model.fbx'
    Copy-Item -LiteralPath $fbx[0].path -Destination $destination -Force
    if ((Get-FileHash -LiteralPath $destination).Hash -ne $fbx[0].sha256) { throw 'Model snapshot changed.' }
    $clips = @()
    foreach ($clip in $case.exported.files | Where-Object kind -eq 'AnimationSource') {
        $name = [IO.Path]::GetFileName($clip.path)
        $destination = Join-Path $target $name
        Copy-Item -LiteralPath $clip.path -Destination $destination -Force
        if ((Get-FileHash -LiteralPath $destination).Hash -ne $clip.sha256) { throw 'Animation snapshot changed.' }
        $clips += $relative + '/' + $name
    }
    $cases += @{ role = $case.role; model = $relative + '/Model.fbx'; evidence = $case.model.rigEvidencePath; clips = $clips
        mappings = (Join-Path $case.exported.outputDirectory 'model/avatar_human_candidates.json') }
}
if ($cases.Count -eq 0) { throw 'No actual exported sample cases.' }
$validation = Join-Path $projectRoot 'Assets/GenericFeasibilityValidation/Editor'
New-Item -ItemType Directory -Path $validation -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $repo 'sources/verification/UnityUnitInferenceVerification.cs') -Destination $validation -Force
$request = Join-Path $reports 'unit-request.json'
@{ directory = $reports; cases = $cases } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $request -Encoding utf8
$start = [Diagnostics.ProcessStartInfo]::new($Editor)
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
foreach ($argument in @('-batchmode','-projectPath',$projectRoot,'-executeMethod','EndfieldCharacterWorkbench.Verification.UnityUnitInferenceVerification.Run','-logFile',(Join-Path $reports 'units-unity.log'))) { $start.ArgumentList.Add($argument) }
$start.Environment['ENDFIELD_UNIT_INFERENCE_CHECK'] = $request
$owned = [Diagnostics.Process]::Start($start)
try {
    if (-not $owned.WaitForExit(180000)) { throw 'Unit inference verification timed out.' }
    $result = Get-Content -LiteralPath (Join-Path $reports 'unit-verification.json') -Raw | ConvertFrom-Json
    if ($owned.ExitCode -ne 0 -or -not $result.passed) { throw 'Unit inference verification failed; inspect its report and log.' }
    $result | ConvertTo-Json -Depth 7
}
finally { if (-not $owned.HasExited) { $owned.Kill($true) }; $owned.Dispose() }
