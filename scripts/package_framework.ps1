param(
    [string] $LegacyPackage = 'D:\AAAChatGPT\EndfieldChenTool',
    [string] $OutputDirectory = 'D:\AAAChatGPT\EndfieldCharacterTool-Framework'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$sourceRoot = [IO.Path]::GetFullPath($LegacyPackage).TrimEnd('\', '/')
$packageRoot = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\', '/')
$repositoryRoot = [IO.Path]::GetFullPath($repoRoot).TrimEnd('\', '/')
$markerName = 'framework-package.json'

function Test-Within([string] $Candidate, [string] $Parent) {
    return $Candidate.Equals($Parent, [StringComparison]::OrdinalIgnoreCase) -or
        $Candidate.StartsWith($Parent + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
}

if ($packageRoot -eq [IO.Path]::GetPathRoot($packageRoot).TrimEnd('\', '/') -or
    (Test-Within $packageRoot $sourceRoot) -or (Test-Within $sourceRoot $packageRoot) -or
    (Test-Within $packageRoot $repositoryRoot) -or (Test-Within $repositoryRoot $packageRoot)) {
    throw '框架输出目录必须独立于现有工具、源码目录及其上级目录。'
}
foreach ($requiredFile in @('pipeline.py', 'profiles\chen.json', 'runtime\model\ModelExportCompat.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $sourceRoot $requiredFile) -PathType Leaf)) {
        throw "既有工具包缺少依赖：$requiredFile"
    }
}
if (Test-Path -LiteralPath $packageRoot) {
    $markerPath = Join-Path $packageRoot $markerName
    if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf)) {
        throw '目标目录已存在且不是本框架生成的运行目录，请选择新目录。'
    }
    $marker = Get-Content -LiteralPath $markerPath -Raw | ConvertFrom-Json
    if ($marker.schema -ne 'endfield-character-framework-package-v1') { throw '目标目录标识不兼容。' }
}

# Build only. No extraction, Unity import, application launch or tests are performed.
$compiledRoot = Join-Path (Split-Path -Parent $repoRoot) 'outputs\character_tool_framework\compiled'
& dotnet build (Join-Path $repoRoot 'EndfieldChenTool.csproj') --no-restore --configuration Release --output $compiledRoot -p:UseSharedCompilation=false
if ($LASTEXITCODE -ne 0) { throw '框架编译失败。' }
$cryptoRoot = Join-Path (Split-Path -Parent $repoRoot) 'outputs\character_tool_framework\crypto'
& dotnet build (Join-Path $repoRoot 'Crypto\VfsCrypto.csproj') --no-restore --configuration Release --output $cryptoRoot -p:UseSharedCompilation=false
if ($LASTEXITCODE -ne 0) { throw '索引解码组件编译失败。' }
$exporterBuilds = @{
    'object-probe' = 'BundleObjectProbe'
    'object-model' = 'ModelExportCompat'
    'object-animation' = 'AnimationExportCompat'
}
foreach ($runtimeName in $exporterBuilds.Keys) {
    $projectName = $exporterBuilds[$runtimeName]
    $buildDirectory = Join-Path (Split-Path -Parent $repoRoot) ('outputs\character_tool_framework\' + $runtimeName)
    & dotnet build (Join-Path $repoRoot ('sources\exporters\' + $projectName + '\' + $projectName + '.csproj')) `
        --configuration Release --output $buildDirectory --configfile (Join-Path $repoRoot 'NuGet.Config') -p:UseSharedCompilation=false -warnaserror:MSB3277
    if ($LASTEXITCODE -ne 0) { throw "对象组件编译失败：$projectName" }
}

# Check before copying any file; an open desktop application may lock its assembly.
$lockCandidates = @('EndfieldChenTool.exe', 'EndfieldChenTool.dll', 'runtime\crypto\VfsCrypto.exe', 'runtime\crypto\VfsCrypto.dll')
foreach ($runtimeName in $exporterBuilds.Keys) {
    $installedRuntime = Join-Path $packageRoot ('runtime\' + $runtimeName)
    if (Test-Path -LiteralPath $installedRuntime -PathType Container) {
        $lockCandidates += Get-ChildItem -LiteralPath $installedRuntime -File -Recurse |
            Where-Object { $_.Extension -in @('.dll', '.exe') } |
            ForEach-Object { [IO.Path]::GetRelativePath($packageRoot, $_.FullName) }
    }
}
foreach ($relative in $lockCandidates) {
    $installedFile = Join-Path $packageRoot $relative
    if (Test-Path -LiteralPath $installedFile -PathType Leaf) {
        try {
            $probeStream = [IO.File]::Open($installedFile, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
            $probeStream.Dispose()
        }
        catch { throw "目标程序仍被占用，请关闭工作台后重试，或指定新的输出目录：$installedFile" }
    }
}
New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null

# Copy runtime dependencies from the known delivery. Preserve the original package.
foreach ($name in @('pipeline.py', 'THIRD_PARTY.md')) {
    Copy-Item -LiteralPath (Join-Path $sourceRoot $name) -Destination (Join-Path $packageRoot $name) -Force
}
Copy-Item -LiteralPath (Join-Path $repoRoot 'THIRD_PARTY.md') -Destination (Join-Path $packageRoot 'THIRD_PARTY.md') -Force
foreach ($folder in @('profiles', 'runtime', 'templates', 'scripts', 'licenses', 'sources')) {
    Copy-Item -LiteralPath (Join-Path $sourceRoot $folder) -Destination $packageRoot -Recurse -Force
}
Copy-Item -LiteralPath (Join-Path $repoRoot 'sources\exporters\RuntimeDependencies.props') -Destination (Join-Path $packageRoot 'sources\exporters\RuntimeDependencies.props') -Force
foreach ($file in Get-ChildItem -LiteralPath $cryptoRoot -File) {
    Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $packageRoot ('runtime\crypto\' + $file.Name)) -Force
}
# Keep legacy runtimes intact; the model and ACL decoder use different AnimeStudio versions.
foreach ($runtimeName in $exporterBuilds.Keys) {
    $baselineRuntime = if ($runtimeName -eq 'object-model') { 'model' } else { 'animation' }
    $destination = Join-Path $packageRoot ('runtime\' + $runtimeName)
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    # Legacy FBXWrapper loads native libraries from x64/, not only the runtime root.
    foreach ($entry in Get-ChildItem -LiteralPath (Join-Path $sourceRoot ('runtime\' + $baselineRuntime))) {
        Copy-Item -LiteralPath $entry.FullName -Destination $destination -Recurse -Force
    }
    $buildDirectory = Join-Path (Split-Path -Parent $repoRoot) ('outputs\character_tool_framework\' + $runtimeName)
    foreach ($file in Get-ChildItem -LiteralPath $buildDirectory -File) {
        Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $destination $file.Name) -Force
    }
    $projectName = $exporterBuilds[$runtimeName]
    $exporterSource = Join-Path $packageRoot ('sources\exporters\' + $projectName)
    New-Item -ItemType Directory -Path $exporterSource -Force | Out-Null
    foreach ($file in @('Program.cs', ($projectName + '.csproj'))) {
        Copy-Item -LiteralPath (Join-Path $repoRoot ('sources\exporters\' + $projectName + '\' + $file)) -Destination (Join-Path $exporterSource $file) -Force
    }
    if ($projectName -eq 'BundleObjectProbe') {
        Copy-Item -LiteralPath (Join-Path $repoRoot 'sources/exporters/BundleObjectProbe/RigEvidenceWriter.cs') -Destination $exporterSource -Force
        Copy-Item -LiteralPath (Join-Path $repoRoot 'sources/exporters/BundleObjectProbe/SerializedEvidenceWriter.cs') -Destination $exporterSource -Force
    }
}
foreach ($file in @('index_game_resources.py', 'endfield_manifest.py', 'index_formats.py', 'extract_plan_bundles.py', 'object_pipeline.py', 'source_rig_discovery.py', 'role_bundle_discovery.py', 'prop_reference_discovery.py', 'model_assembly.py', 'summarize_composite_roles.py', 'prepare_unity_adaptation.py', 'verify_object_export_files.py')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination (Join-Path $packageRoot ('scripts\' + $file)) -Force
}
Copy-Item -LiteralPath (Join-Path $repoRoot 'profiles\known_builds.json') -Destination (Join-Path $packageRoot 'profiles\known_builds.json') -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'Crypto\Program.cs') -Destination (Join-Path $packageRoot 'sources\crypto\Program.cs') -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'Crypto\BundleExtractor.cs') -Destination (Join-Path $packageRoot 'sources\crypto\BundleExtractor.cs') -Force
foreach ($file in Get-ChildItem -LiteralPath $compiledRoot -File) {
    Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $packageRoot $file.Name) -Force
}
$guiSource = Join-Path $packageRoot 'sources\gui'
New-Item -ItemType Directory -Path $guiSource -Force | Out-Null
foreach ($file in @('Program.cs', 'EndfieldChenTool.csproj', 'NuGet.Config', 'build_tool.py')) {
    Copy-Item -LiteralPath (Join-Path $repoRoot $file) -Destination (Join-Path $guiSource $file) -Force
}
Copy-Item -LiteralPath (Join-Path $repoRoot 'Framework') -Destination $guiSource -Recurse -Force
$workspaceTemplates = Join-Path $packageRoot 'templates\workspace'
New-Item -ItemType Directory -Path $workspaceTemplates -Force | Out-Null
foreach ($file in @('CharacterAnimationAdapter.cs', 'CharacterPreviewControls.cs', 'KneeDiagnostics.cs')) {
    Copy-Item -LiteralPath (Join-Path $repoRoot ('templates\workspace\' + $file)) -Destination (Join-Path $workspaceTemplates $file) -Force
}
$verificationSource = Join-Path $packageRoot 'sources\verification\WorkspaceExportVerification'
$compilationSource = Join-Path $packageRoot 'sources\compilation\UnityTemplates'
New-Item -ItemType Directory -Path $compilationSource -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $repoRoot 'sources\compilation\UnityTemplates\UnityTemplates.csproj') -Destination $compilationSource -Force
New-Item -ItemType Directory -Path $verificationSource -Force | Out-Null
foreach ($file in @('Program.cs', 'WorkspaceExportVerification.csproj')) {
    Copy-Item -LiteralPath (Join-Path $repoRoot ('sources\verification\WorkspaceExportVerification\' + $file)) -Destination (Join-Path $verificationSource $file) -Force
}
$adaptationVerification = Join-Path $packageRoot 'sources\verification\WorkspaceAdaptationVerification'
New-Item -ItemType Directory -Path $adaptationVerification -Force | Out-Null
foreach ($file in @('Program.cs', 'WorkspaceAdaptationVerification.csproj')) {
    Copy-Item -LiteralPath (Join-Path $repoRoot ('sources\verification\WorkspaceAdaptationVerification\' + $file)) -Destination (Join-Path $adaptationVerification $file) -Force
}
Copy-Item -LiteralPath (Join-Path $repoRoot 'sources\verification\UnityPreviewVerification.cs') -Destination (Join-Path $packageRoot 'sources\verification\UnityPreviewVerification.cs') -Force
foreach ($name in @('UnityMenuVerification.cs', 'UnityLaunchVerification.cs', 'UnityEdgeVerification.cs', 'UnityHoldVerification.cs', 'UnityMouseVerification.cs', 'UnityGeneratedKneeVerification.cs', 'UnityMappingVerification.cs', 'UnitySharedRigVerification.cs', 'UnityModelAssemblyVerification.cs', 'test_model_assembly.py', 'investigate_prop_references.py', 'inspect_shared_rig_catalog.py', 'inspect_candidate_dependencies.py')) {
    Copy-Item -LiteralPath (Join-Path $repoRoot ('sources\verification\' + $name)) -Destination (Join-Path $packageRoot ('sources\verification\' + $name)) -Force
}
Copy-Item -LiteralPath (Join-Path $repoRoot 'scripts\verify_preview_project.ps1') -Destination (Join-Path $packageRoot 'scripts\verify_preview_project.ps1') -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'scripts\verify_edge_project.ps1') -Destination (Join-Path $packageRoot 'scripts\verify_edge_project.ps1') -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'scripts\verify_remaining_project.ps1') -Destination (Join-Path $packageRoot 'scripts\verify_remaining_project.ps1') -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'scripts\verify_display_scale.ps1') -Destination (Join-Path $packageRoot 'scripts\verify_display_scale.ps1') -Force
foreach ($name in @('RemainingAcceptance', 'DesktopAcceptance', 'UnityMouseAcceptance', 'CompositeRoleVerification')) {
    $destination = Join-Path $packageRoot ('sources\verification\' + $name)
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    foreach ($file in @('Program.cs', ($name + '.csproj'))) {
        Copy-Item -LiteralPath (Join-Path $repoRoot ('sources\verification\' + $name + '\' + $file)) -Destination $destination -Force
    }
    if ($name -eq 'CompositeRoleVerification') {
        foreach ($file in @('AssociationGuards.cs', 'BaselineRegression.cs')) {
            Copy-Item -LiteralPath (Join-Path $repoRoot ('sources\verification\' + $name + '\' + $file)) -Destination $destination -Force
        }
    }
}
$uiVerification = Join-Path $packageRoot 'sources\verification\WorkspaceUiVerification'
New-Item -ItemType Directory -Path $uiVerification -Force | Out-Null
foreach ($name in @('Program.cs', 'WorkspaceUiVerification.csproj')) {
    Copy-Item -LiteralPath (Join-Path $repoRoot ('sources\verification\WorkspaceUiVerification\' + $name)) -Destination $uiVerification -Force
}
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs') -Destination $packageRoot -Recurse -Force
$readme = [IO.File]::ReadAllText((Join-Path $repoRoot 'docs\框架说明.md'))
# README is one directory above its source document; keep local report links usable.
$readme = [regex]::Replace($readme, '\]\((?!<|[A-Za-z][A-Za-z0-9+.-]*:|/|#)([^)\r\n]+\.md(?:#[^)\r\n]*)?)\)', '](docs/$1)')
[IO.File]::WriteAllText((Join-Path $packageRoot 'README.md'), $readme, [Text.UTF8Encoding]::new($false))
Copy-Item -LiteralPath $PSCommandPath -Destination (Join-Path $packageRoot 'scripts\package_framework.ps1') -Force
@{ schema = 'endfield-character-framework-package-v1'; version = '0.15.0-model-assembly-evidence'; executable = 'EndfieldChenTool.exe' } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $packageRoot $markerName) -Encoding utf8
Write-Output "框架运行目录：$packageRoot"
