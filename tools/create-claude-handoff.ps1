[CmdletBinding()]
param(
    [string]$OutputDirectory,
    [string]$ArchiveLabel = "NexusShell-ClaudeCode-Handoff"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$workspaceRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot ".."))
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $workspaceRoot "outputs"
}

$resolvedOutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
$workspaceBoundary = $workspaceRoot.TrimEnd(
    [char[]]@(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)) +
    [System.IO.Path]::DirectorySeparatorChar
if (-not $resolvedOutputDirectory.StartsWith(
        $workspaceBoundary,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputDirectory must stay inside the project: $workspaceRoot"
}

$allowedRootFiles = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::OrdinalIgnoreCase)
foreach ($name in @(
        ".gitattributes",
        ".gitignore",
        "AGENTS.md",
        "CLAUDE.md",
        "CONTRIBUTING.md",
        "DESIGN.md",
        "Directory.Build.props",
        "global.json",
        "NexusShell.sln",
        "NuGet.Config",
        "PRODUCT.md",
        "README.md",
        "START-HERE-CLAUDE-DESKTOP.md")) {
    [void]$allowedRootFiles.Add($name)
}

$allowedDirectories = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::OrdinalIgnoreCase)
foreach ($name in @(
        ".claude",
        ".github",
        ".interface-design",
        "design",
        "docs",
        "installer",
        "src",
        "tests",
        "tools")) {
    [void]$allowedDirectories.Add($name)
}

$excludedDirectoryNames = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::OrdinalIgnoreCase)
foreach ($name in @(
        ".git",
        ".idea",
        ".vs",
        ".vscode",
        "bin",
        "obj",
        "TestResults")) {
    [void]$excludedDirectoryNames.Add($name)
}

$excludedFileNames = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::OrdinalIgnoreCase)
foreach ($name in @(
        ".env",
        "secrets.json",
        "CLAUDE.local.md",
        "PHASE_1_REPORT.md",
        "RELEASE_0.4.0.md",
        "RELEASE_0.5.0.md",
        "RELEASE_0.6.0.md",
        "settings.local.json")) {
    [void]$excludedFileNames.Add($name)
}

$excludedExtensions = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::OrdinalIgnoreCase)
foreach ($extension in @(
        ".7z",
        ".appx",
        ".appxbundle",
        ".bak",
        ".db",
        ".dmp",
        ".dll",
        ".exe",
        ".log",
        ".key",
        ".msix",
        ".msixbundle",
        ".msixupload",
        ".nupkg",
        ".p12",
        ".pdb",
        ".pem",
        ".pfx",
        ".rar",
        ".snk",
        ".snupkg",
        ".sqlite",
        ".sqlite3",
        ".suo",
        ".temp",
        ".tmp",
        ".trx",
        ".user",
        ".zip")) {
    [void]$excludedExtensions.Add($extension)
}

function Get-RelativePath([string]$FullPath) {
    $resolvedPath = [System.IO.Path]::GetFullPath($FullPath)
    if (-not $resolvedPath.StartsWith(
            $workspaceBoundary,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Source path escaped the project: $resolvedPath"
    }

    return $resolvedPath.Substring($workspaceRoot.Length).TrimStart(
        [char[]]@(
            [System.IO.Path]::DirectorySeparatorChar,
            [System.IO.Path]::AltDirectorySeparatorChar))
}

$sourceFiles = Get-ChildItem -LiteralPath $workspaceRoot -File -Recurse |
    Where-Object {
        $relativePath = Get-RelativePath $_.FullName
        $segments = $relativePath -split '[\\/]'
        $topLevel = $segments[0]

        if ($segments.Count -eq 1) {
            return $allowedRootFiles.Contains($relativePath)
        }

        if (-not $allowedDirectories.Contains($topLevel)) {
            return $false
        }

        if ($segments | Where-Object { $excludedDirectoryNames.Contains($_) }) {
            return $false
        }

        if ($excludedFileNames.Contains($_.Name)) {
            return $false
        }

        if ($_.Name.Equals(".env", [System.StringComparison]::OrdinalIgnoreCase) -or
            $_.Name.StartsWith(".env.", [System.StringComparison]::OrdinalIgnoreCase) -or
            $_.Name -like "appsettings.*.local.json") {
            return $false
        }

        return -not $excludedExtensions.Contains($_.Extension)
    } |
    Sort-Object { Get-RelativePath $_.FullName }

$requiredPaths = @(
    "AGENTS.md",
    "CLAUDE.md",
    "START-HERE-CLAUDE-DESKTOP.md",
    "docs\CLAUDE_HANDOFF.md",
    "docs\CLAUDE_START_PROMPT.md",
    "NexusShell.sln",
    "src\Nexus.App\Nexus.App.csproj",
    "src\Nexus.Core\Nexus.Core.csproj",
    "tests\Nexus.Core.Tests\Nexus.Core.Tests.csproj",
    "tests\Nexus.RecycleBin.SmokeTests\Nexus.RecycleBin.SmokeTests.csproj",
    "design\nexus-reference.png",
    "design\nexus-reference-compact.png"
)
foreach ($requiredPath in $requiredPaths) {
    if (-not ($sourceFiles | Where-Object {
                (Get-RelativePath $_.FullName).Equals(
                    $requiredPath,
                    [System.StringComparison]::OrdinalIgnoreCase)
            })) {
        throw "Required handoff file is missing: $requiredPath"
    }
}

New-Item -ItemType Directory -Path $resolvedOutputDirectory -Force |
    Out-Null

$timestamp = [DateTime]::UtcNow.ToString("yyyyMMdd-HHmmss")
$archivePath = Join-Path `
    $resolvedOutputDirectory `
    "$ArchiveLabel-$timestamp.zip"

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::Open(
    $archivePath,
    [System.IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in $sourceFiles) {
        $relativePath = (Get-RelativePath $file.FullName).Replace('\', '/')
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $archive,
            $file.FullName,
            $relativePath,
            [System.IO.Compression.CompressionLevel]::Optimal) |
            Out-Null
    }
}
finally {
    $archive.Dispose()
}

$hash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
$hashPath = "$archivePath.sha256"
[System.IO.File]::WriteAllText(
    $hashPath,
    "$hash *$([System.IO.Path]::GetFileName($archivePath))$([Environment]::NewLine)",
    [System.Text.UTF8Encoding]::new($false))

Write-Host "Claude Code handoff ready"
Write-Host "  Archive: $archivePath"
Write-Host "  Files: $($sourceFiles.Count)"
Write-Host "  SHA256: $hash"
Write-Host "  Checksum: $hashPath"
