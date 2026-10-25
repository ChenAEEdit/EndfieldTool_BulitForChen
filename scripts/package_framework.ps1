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

# Check before copying any file; an open desktop application may lock its assembly.
foreach ($relative in @('EndfieldChenTool.exe', 'EndfieldChenTool.dll', 'runtime\crypto\VfsCrypto.exe', 'runtime\crypto\VfsCrypto.dll')) {
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
foreach ($file in Get-ChildItem -LiteralPath $cryptoRoot -File) {
    Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $packageRoot ('runtime\crypto\' + $file.Name)) -Force
}
foreach ($file in @('index_game_resources.py', 'endfield_manifest.py', 'index_formats.py')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination (Join-Path $packageRoot ('scripts\' + $file)) -Force
}
Copy-Item -LiteralPath (Join-Path $repoRoot 'profiles\known_builds.json') -Destination (Join-Path $packageRoot 'profiles\known_builds.json') -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'Crypto\Program.cs') -Destination (Join-Path $packageRoot 'sources\crypto\Program.cs') -Force
foreach ($file in Get-ChildItem -LiteralPath $compiledRoot -File) {
    Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $packageRoot $file.Name) -Force
}
$guiSource = Join-Path $packageRoot 'sources\gui'
New-Item -ItemType Directory -Path $guiSource -Force | Out-Null
foreach ($file in @('Program.cs', 'EndfieldChenTool.csproj', 'NuGet.Config', 'build_tool.py')) {
    Copy-Item -LiteralPath (Join-Path $repoRoot $file) -Destination (Join-Path $guiSource $file) -Force
}
Copy-Item -LiteralPath (Join-Path $repoRoot 'Framework') -Destination $guiSource -Recurse -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs') -Destination $packageRoot -Recurse -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs\框架说明.md') -Destination (Join-Path $packageRoot 'README.md') -Force
Copy-Item -LiteralPath $PSCommandPath -Destination (Join-Path $packageRoot 'scripts\package_framework.ps1') -Force
@{ schema = 'endfield-character-framework-package-v1'; version = '0.4.0-format-preview'; executable = 'EndfieldChenTool.exe' } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $packageRoot $markerName) -Encoding utf8
Write-Output "框架运行目录：$packageRoot"
