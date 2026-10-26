param(
    [Parameter(Mandatory)] [string] $Project,
    [Parameter(Mandatory)] [string] $ReportDirectory,
    [Parameter(Mandatory)] [string] $Config,
    [ValidateSet('original-menu', 'fixed-menu', 'play')] [string] $Mode = 'play',
    [switch] $UseEntry,
    [string] $LegacyConfig,
    [string] $Editor = 'D:\Unity 3D\Editor\6000.0.84f1\Editor\Unity.exe'
)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath($Project)
if (-not $projectRoot.StartsWith('D:\AAAChatGPT\UnityValidation\', [StringComparison]::OrdinalIgnoreCase)) {
    throw '此验证入口只操作独立验证工程，不操作用户工程。'
}
$repo = Split-Path -Parent $PSScriptRoot
$reports = [IO.Path]::GetFullPath($ReportDirectory)
New-Item -ItemType Directory -Path $reports -Force | Out-Null
$validation = Join-Path $projectRoot 'Assets\EndfieldCharacterValidation\Editor'
New-Item -ItemType Directory -Path $validation -Force | Out-Null
foreach ($name in @('UnityMenuVerification.cs', 'UnityPreviewVerification.cs')) {
    Copy-Item -LiteralPath (Join-Path $repo ('sources\verification\' + $name)) -Destination $validation -Force
}
$invocation = [Guid]::NewGuid().ToString('N')
$start = [Diagnostics.ProcessStartInfo]::new($Editor)
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
$start.ArgumentList.Add('-batchmode')
if ($Mode -ne 'play') { $start.ArgumentList.Add('-quit') }
foreach ($argument in @('-projectPath', $projectRoot, '-executeMethod')) { $start.ArgumentList.Add($argument) }
if ($Mode -eq 'play') {
    $start.ArgumentList.Add('EndfieldCharacterWorkbench.Verification.UnityPreviewVerification.Begin')
    $requestFile = Join-Path $reports 'play-request.json'
    @{ scene = ($Config -replace 'adaptation.json$', 'Generated/CharacterPreview.unity'); directory = $reports; invocation = $invocation } |
        ConvertTo-Json | Set-Content -LiteralPath $requestFile -Encoding utf8
    $start.Environment['ENDFIELD_PREVIEW_CHECK'] = $requestFile
    $start.Environment['ENDFIELD_REQUIRE_MAIN_BODY'] = '1'
    if ($UseEntry) {
        $work = Join-Path $projectRoot 'Library\EndfieldCharacterWorkbench'
        New-Item -ItemType Directory -Path $work -Force | Out-Null
        @{ schema = 'endfield-preview-open-v1'; requestId = $invocation; configPath = $Config; configSha256 = (Get-FileHash -LiteralPath (Join-Path $projectRoot $Config)).Hash; play = $true } |
            ConvertTo-Json | Set-Content -LiteralPath (Join-Path $work 'preview-open.json') -Encoding utf8
        $start.Environment['ENDFIELD_PREVIEW_USE_ENTRY'] = '1'
    }
    $resultFile = Join-Path $reports 'play-verification.json'
}
else {
    $method = if ($Mode -eq 'original-menu') { 'ReproduceOriginal' } else { 'VerifyFixed' }
    $start.ArgumentList.Add('EndfieldCharacterWorkbench.Verification.UnityMenuVerification.' + $method)
    $start.Environment['ENDFIELD_MENU_CONFIG'] = $Config
    $resultFile = Join-Path $reports ($Mode + '.json')
    $start.Environment['ENDFIELD_MENU_REPORT'] = $resultFile
    if ($LegacyConfig) { $start.Environment['ENDFIELD_OLD_CONFIG'] = $LegacyConfig }
}
$start.ArgumentList.Add('-logFile')
$log = Join-Path $reports 'unity.log'
$start.ArgumentList.Add($log)
$process = [Diagnostics.Process]::Start($start)
if (-not $process.WaitForExit(180000)) { $process.Kill($true); throw '验证超过 180 秒，已停止本次创建的验证进程。' }
if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $resultFile)) {
    Get-Content -LiteralPath $log -Tail 45
    throw 'Unity 验证未完成。'
}
$result = Get-Content -LiteralPath $resultFile -Raw | ConvertFrom-Json
$result | ConvertTo-Json -Depth 6
if (-not $result.passed) { throw 'Unity 验证报告未通过。' }
exit 0
