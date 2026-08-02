param(
    [ValidateSet("Release")]
    [string]$Configuration = "Release",

    [ValidatePattern("^\d+\.\d+\.\d+$")]
    [string]$Version = "0.8.0",

    [switch]$NoRestore,

    [switch]$SignInstaller,

    [ValidatePattern("^[A-Fa-f0-9]{40}$")]
    [string]$CertificateThumbprint,

    [string]$SignToolPath,

    [string]$InnoUnpackerPath,

    [ValidatePattern("^https?://")]
    [string]$TimestampServer = "https://timestamp.digicert.com",

    [switch]$ValidateOnly
)

$ErrorActionPreference = "Stop"
$workspaceRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$solutionPath = Join-Path $workspaceRoot "NexusShell.sln"
$applicationProject = Join-Path $workspaceRoot "src\Nexus.App\Nexus.App.csproj"
$testProject = Join-Path $workspaceRoot "tests\Nexus.Core.Tests\Nexus.Core.Tests.csproj"
$nugetConfig = Join-Path $workspaceRoot "NuGet.Config"
$publishDirectory = Join-Path $workspaceRoot "work\installer\publish"
$outputDirectory = Join-Path $workspaceRoot "outputs"
$artifactBaseName = "NexusShell-Setup-$Version-x64"
$setupPath = Join-Path $outputDirectory "$artifactBaseName.exe"
$checksumPath = Join-Path $outputDirectory "$artifactBaseName.sha256"
$jsonManifestPath = Join-Path $outputDirectory "$artifactBaseName.manifest.json"
$textManifestPath = Join-Path $outputDirectory "$artifactBaseName.manifest.txt"
$installerDefinition = Join-Path $PSScriptRoot "NexusShell.iss"
$payloadDirectoryName = "app-$Version"
$payloadMarkerName = ".nexus-shell-payload"
$payloadMarkerMagic = "NEXUS_SHELL_PAYLOAD_V1"
$payloadProductId = "F20C84DD-1DF1-4F1C-95B9-EA4340F6B6C2"
$extractionSmokeDirectory = Join-Path $workspaceRoot "work\installer\extract-smoke"

function Assert-WorkspacePath {
    param([Parameter(Mandatory)][string]$Path)

    $resolvedPath = [System.IO.Path]::GetFullPath($Path)
    if (-not $resolvedPath.StartsWith(
            $workspaceRoot + [System.IO.Path]::DirectorySeparatorChar,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Путь сборки вышел за границы рабочей папки: $resolvedPath"
    }

    return $resolvedPath
}

function Invoke-CheckedCommand {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][string]$FailureMessage
    )

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FailureMessage Код завершения: $LASTEXITCODE."
    }
}

function Get-InnoCompilerPath {
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
        "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
        "C:\Program Files\Inno Setup 6\ISCC.exe"
    )

    $compiler = $candidates |
        Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
        Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($compiler)) {
        throw "Inno Setup 6 не найден. Установите JRSoftware.InnoSetup через winget."
    }

    return $compiler
}

function Get-InnoUnpackerPath {
    if (-not [string]::IsNullOrWhiteSpace($InnoUnpackerPath)) {
        $explicitPath = [System.IO.Path]::GetFullPath($InnoUnpackerPath)
        if (-not (Test-Path -LiteralPath $explicitPath -PathType Leaf)) {
            throw "Указанный innounp.exe не найден: $explicitPath"
        }

        return $explicitPath
    }

    $candidates = @()
    if (-not [string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
        $candidates += Join-Path $env:LOCALAPPDATA "Programs\innounp\innounp.exe"
    }

    $unpacker = $candidates |
        Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
        Select-Object -First 1
    if (-not [string]::IsNullOrWhiteSpace($unpacker)) {
        return $unpacker
    }

    $command = Get-Command "innounp.exe" -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    return $null
}

function Get-PayloadMarkerLines {
    param([Parameter(Mandatory)][string]$PayloadVersion)

    return @(
        "Magic=$payloadMarkerMagic",
        "ProductId=$payloadProductId",
        "Version=$PayloadVersion",
        "Executable=Nexus.App.exe"
    )
}

function Write-PayloadMarker {
    param(
        [Parameter(Mandatory)][string]$Directory,
        [Parameter(Mandatory)][string]$PayloadVersion
    )

    $markerPath = Join-Path $Directory $payloadMarkerName
    $utf8WithoutBom = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllLines(
        $markerPath,
        (Get-PayloadMarkerLines -PayloadVersion $PayloadVersion),
        $utf8WithoutBom)
}

function Assert-PayloadMarker {
    param(
        [Parameter(Mandatory)][string]$Directory,
        [Parameter(Mandatory)][string]$PayloadVersion
    )

    $markerPath = Join-Path $Directory $payloadMarkerName
    if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf)) {
        throw "В payload отсутствует маркер владения: $markerPath"
    }
    if (-not (Test-Path -LiteralPath (Join-Path $Directory "Nexus.App.exe") -PathType Leaf)) {
        throw "В отмеченном payload отсутствует Nexus.App.exe: $Directory"
    }

    $expectedLines = @(Get-PayloadMarkerLines -PayloadVersion $PayloadVersion)
    $actualLines = @([System.IO.File]::ReadAllLines($markerPath))
    if ($actualLines.Count -ne $expectedLines.Count) {
        throw "Маркер payload имеет неожиданное число строк: $markerPath"
    }

    for ($index = 0; $index -lt $expectedLines.Count; $index++) {
        if (-not [string]::Equals(
                $actualLines[$index],
                $expectedLines[$index],
                [System.StringComparison]::Ordinal)) {
            throw "Маркер payload не соответствует версии/продукту: $markerPath"
        }
    }
}

function Assert-InstallerDefinitionSafety {
    param([Parameter(Mandatory)][string]$DefinitionPath)

    if (-not (Test-Path -LiteralPath $DefinitionPath -PathType Leaf)) {
        throw "Сценарий Inno Setup не найден: $DefinitionPath"
    }

    $definition = [System.IO.File]::ReadAllText($DefinitionPath)
    $requiredFragments = @(
        '#define MyPayloadMarker ".nexus-shell-payload"',
        "function IsStrictVersionedPayloadName",
        "function IsOwnedPayloadDirectory",
        "LoadStringsFromFile(MarkerPath, MarkerLines)",
        "and ((Entry.Attributes and ReparsePointAttribute) = 0)",
        "and IsOwnedPayloadDirectory(RootPath, Entry.Name)",
        "if not IsOwnedPayloadDirectory(InstallRoot, '{#MyPayloadDirectory}')",
        'Filename: "{app}\{#MyPayloadDirectory}\{#MyAppExeName}"'
    )
    foreach ($fragment in $requiredFragments) {
        if ($definition.IndexOf(
                $fragment,
                [System.StringComparison]::Ordinal) -lt 0) {
            throw "Статическая проверка Inno Setup: отсутствует обязательная защита: $fragment"
        }
    }

    $forbiddenFragments = @(
        "procedure RemoveLegacyRootPayload",
        "DelTree(RootPath",
        "DelTree(ExpandConstant('{app}')",
        "[UninstallDelete]"
    )
    foreach ($fragment in $forbiddenFragments) {
        if ($definition.IndexOf(
                $fragment,
                [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            throw "Статическая проверка Inno Setup: найдено опасное удаление: $fragment"
        }
    }

    $recursiveDeleteCount = [regex]::Matches(
        $definition,
        "\bDelTree\s*\(",
        [System.Text.RegularExpressions.RegexOptions]::IgnoreCase).Count
    if ($recursiveDeleteCount -ne 1) {
        throw "Статическая проверка Inno Setup ожидала одно маркер-защищённое рекурсивное удаление, найдено: $recursiveDeleteCount"
    }

    $guardedDeletePattern =
        "(?s)and\s+IsOwnedPayloadDirectory\(RootPath,\s*Entry\.Name\)\s+then\s*" +
        "begin.*?DelTree\(EntryPath,\s*True,\s*True,\s*True\)"
    if (-not [regex]::IsMatch(
            $definition,
            $guardedDeletePattern,
            [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)) {
        throw "Статическая проверка Inno Setup не подтвердила marker-gate перед DelTree."
    }

    $iconsSection = [regex]::Match(
        $definition,
        "(?ms)^\[Icons\]\s*(?<Body>.*?)^\[Run\]\s*$")
    if (-not $iconsSection.Success) {
        throw "Статическая проверка Inno Setup не нашла секцию [Icons]."
    }

    $iconLines = $iconsSection.Groups["Body"].Value -split "\r?\n" |
        Where-Object { $_.TrimStart().StartsWith("Name:", [System.StringComparison]::Ordinal) }
    if ($iconLines.Count -ne 3) {
        throw "Ожидались три ярлыка Nexus (Пуск, рабочий стол, автозапуск), найдено: $($iconLines.Count)"
    }
    foreach ($iconLine in $iconLines) {
        if ($iconLine.IndexOf(
                'Filename: "{app}\{#MyPayloadDirectory}\{#MyAppExeName}"',
                [System.StringComparison]::Ordinal) -lt 0) {
            throw "Ярлык указывает не на versioned payload: $iconLine"
        }
    }
}

function Test-SelfContainedPublish {
    param([Parameter(Mandatory)][string]$Directory)

    $runtimeConfigPath = Join-Path $Directory "Nexus.App.runtimeconfig.json"
    $depsPath = Join-Path $Directory "Nexus.App.deps.json"
    try {
        $runtimeConfig = Get-Content -LiteralPath $runtimeConfigPath -Raw |
            ConvertFrom-Json
        $dependencies = Get-Content -LiteralPath $depsPath -Raw |
            ConvertFrom-Json
    }
    catch {
        throw "Не удалось прочитать runtime/deps JSON из publish: $($_.Exception.Message)"
    }

    if ($null -eq $runtimeConfig.runtimeOptions) {
        throw "runtimeconfig не содержит runtimeOptions."
    }

    $runtimeOptionNames = @($runtimeConfig.runtimeOptions.PSObject.Properties.Name)
    if ($runtimeOptionNames -contains "framework" -or
        $runtimeOptionNames -contains "frameworks") {
        throw "Publish зависит от внешнего shared framework и не является self-contained."
    }

    $includedFrameworkNames = @(
        $runtimeConfig.runtimeOptions.includedFrameworks |
            ForEach-Object { $_.name }
    )
    foreach ($frameworkName in @(
            "Microsoft.NETCore.App",
            "Microsoft.WindowsDesktop.App")) {
        if ($includedFrameworkNames -notcontains $frameworkName) {
            throw "Self-contained runtimeconfig не включает $frameworkName."
        }
    }

    $runtimeTargetName = [string]$dependencies.runtimeTarget.name
    if (-not $runtimeTargetName.EndsWith(
            "/win-x64",
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "deps.json не подтверждает runtime target win-x64: $runtimeTargetName"
    }

    return [ordered]@{
        passed = $true
        runtimeTarget = $runtimeTargetName
        includedFrameworks = $includedFrameworkNames
    }
}

function Invoke-InstallerExtractionSmoke {
    param(
        [Parameter(Mandatory)][string]$InstallerPath,
        [Parameter(Mandatory)][string]$UnpackerPath,
        [Parameter(Mandatory)][string]$PayloadVersion
    )

    $smokeDirectory = Assert-WorkspacePath $extractionSmokeDirectory
    if (Test-Path -LiteralPath $smokeDirectory) {
        Remove-Item -LiteralPath $smokeDirectory -Recurse -Force
    }
    New-Item -ItemType Directory -Path $smokeDirectory -Force | Out-Null

    try {
        Invoke-CheckedCommand `
            -FilePath $UnpackerPath `
            -Arguments @(
                "-x",
                "-y",
                "-d$smokeDirectory",
                $InstallerPath
            ) `
            -FailureMessage "Безустановочная распаковка установщика завершилась с ошибкой."

        $markers = @(
            Get-ChildItem -LiteralPath $smokeDirectory -File -Recurse |
                Where-Object { $_.Name -eq $payloadMarkerName }
        )
        if ($markers.Count -ne 1) {
            throw "После распаковки ожидался один маркер payload, найдено: $($markers.Count)"
        }

        $payloadDirectory = $markers[0].Directory
        if (-not [string]::Equals(
                $payloadDirectory.Name,
                "app-$PayloadVersion",
                [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Распакованный payload имеет неожиданное имя: $($payloadDirectory.Name)"
        }

        Assert-PayloadMarker `
            -Directory $payloadDirectory.FullName `
            -PayloadVersion $PayloadVersion

        return [ordered]@{
            status = "PASSED"
            tool = [System.IO.Path]::GetFileName($UnpackerPath)
            payloadDirectory = $payloadDirectory.Name
        }
    }
    finally {
        if (Test-Path -LiteralPath $smokeDirectory) {
            Remove-Item -LiteralPath $smokeDirectory -Recurse -Force
        }
    }
}

function Get-SignToolPath {
    if (-not [string]::IsNullOrWhiteSpace($SignToolPath)) {
        $explicitPath = [System.IO.Path]::GetFullPath($SignToolPath)
        if (-not (Test-Path -LiteralPath $explicitPath -PathType Leaf)) {
            throw "Указанный signtool.exe не найден: $explicitPath"
        }

        return $explicitPath
    }

    $kitsRoot = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
    if (Test-Path -LiteralPath $kitsRoot -PathType Container) {
        $candidate = Get-ChildItem -LiteralPath $kitsRoot -Directory |
            Sort-Object Name -Descending |
            ForEach-Object { Join-Path $_.FullName "x64\signtool.exe" } |
            Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
            Select-Object -First 1
        if (-not [string]::IsNullOrWhiteSpace($candidate)) {
            return $candidate
        }
    }

    throw "signtool.exe не найден. Укажите его через -SignToolPath."
}

function Get-CodeSigningCertificate {
    if ([string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
        throw "Для подписи укажите -CertificateThumbprint сертификата из CurrentUser\My."
    }

    $normalizedThumbprint = $CertificateThumbprint.ToUpperInvariant()
    $certificatePath = "Cert:\CurrentUser\My\$normalizedThumbprint"
    if (-not (Test-Path -LiteralPath $certificatePath)) {
        throw "Сертификат подписи не найден в CurrentUser\My: $normalizedThumbprint"
    }

    $certificate = Get-Item -LiteralPath $certificatePath
    if (-not $certificate.HasPrivateKey) {
        throw "У сертификата нет доступного закрытого ключа: $normalizedThumbprint"
    }

    $now = Get-Date
    if ($certificate.NotBefore -gt $now -or $certificate.NotAfter -lt $now) {
        throw "Срок действия сертификата подписи не включает текущую дату."
    }

    $codeSigningOid = "1.3.6.1.5.5.7.3.3"
    if (-not ($certificate.EnhancedKeyUsageList |
            Where-Object { $_.ObjectId.Value -eq $codeSigningOid })) {
        throw "Сертификат не предназначен для подписи кода."
    }

    return $certificate
}

function Invoke-AuthenticodeSigning {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string]$ToolPath,
        [Parameter(Mandatory)][System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate
    )

    Invoke-CheckedCommand `
        -FilePath $ToolPath `
        -Arguments @(
            "sign",
            "/sha1", $Certificate.Thumbprint,
            "/fd", "SHA256",
            "/td", "SHA256",
            "/tr", $TimestampServer,
            $FilePath
        ) `
        -FailureMessage "Не удалось подписать $FilePath."

    $signature = Get-AuthenticodeSignature -LiteralPath $FilePath
    if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or
        $signature.SignerCertificate.Thumbprint -ne $Certificate.Thumbprint) {
        throw "Проверка цифровой подписи не пройдена: $FilePath ($($signature.Status))."
    }
}

function Get-SignatureManifest {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][bool]$SigningRequested
    )

    $signature = Get-AuthenticodeSignature -LiteralPath $FilePath
    if (-not $SigningRequested) {
        return [ordered]@{
            status = "UNSIGNED"
            authenticodeStatus = $signature.Status.ToString()
            signer = $null
            thumbprint = $null
        }
    }

    if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or
        $null -eq $signature.SignerCertificate) {
        throw "Артефакт должен быть подписан, но подпись недействительна: $FilePath."
    }

    return [ordered]@{
        status = "SIGNED"
        authenticodeStatus = $signature.Status.ToString()
        signer = $signature.SignerCertificate.Subject
        thumbprint = $signature.SignerCertificate.Thumbprint
    }
}

$publishDirectory = Assert-WorkspacePath $publishDirectory
$outputDirectory = Assert-WorkspacePath $outputDirectory
$setupPath = Assert-WorkspacePath $setupPath
$checksumPath = Assert-WorkspacePath $checksumPath
$jsonManifestPath = Assert-WorkspacePath $jsonManifestPath
$textManifestPath = Assert-WorkspacePath $textManifestPath
$extractionSmokeDirectory = Assert-WorkspacePath $extractionSmokeDirectory

Assert-InstallerDefinitionSafety -DefinitionPath $installerDefinition
if ($ValidateOnly) {
    Write-Host "Installer static safety checks: PASSED"
    Write-Host "  Versioned payload: $payloadDirectoryName"
    Write-Host "  Ownership marker: $payloadMarkerName"
    Write-Host "  No publish, installer compilation, installation, or deletion was performed."
    return
}

$dotnetCommand = Get-Command dotnet -ErrorAction Stop
$dotnetPath = $dotnetCommand.Source
$resolvedInnoUnpackerPath = Get-InnoUnpackerPath

if ($SignInstaller -and [string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
    throw "Параметр -SignInstaller требует -CertificateThumbprint."
}
if (-not $SignInstaller -and
    (-not [string]::IsNullOrWhiteSpace($CertificateThumbprint) -or
        -not [string]::IsNullOrWhiteSpace($SignToolPath))) {
    throw "CertificateThumbprint и SignToolPath используются только вместе с -SignInstaller."
}

if (-not $NoRestore) {
    Invoke-CheckedCommand `
        -FilePath $dotnetPath `
        -Arguments @(
            "restore",
            $solutionPath,
            "--configfile", $nugetConfig,
            "-p:NuGetAudit=true",
            "-p:NuGetAuditMode=all"
        ) `
        -FailureMessage "Восстановление и аудит NuGet-пакетов завершились с ошибкой."
}

Write-Host "Release gate: Nexus.Core console checks"
Invoke-CheckedCommand `
    -FilePath $dotnetPath `
    -Arguments @(
        "run",
        "--project", $testProject,
        "-c", $Configuration,
        "--no-restore",
        "-p:NuGetAudit=false"
    ) `
    -FailureMessage "Проверки Nexus.Core завершились с ошибкой."

if (Test-Path -LiteralPath $publishDirectory) {
    Remove-Item -LiteralPath $publishDirectory -Recurse -Force
}
New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null

Write-Host "Publishing Nexus Shell $Version"
Invoke-CheckedCommand `
    -FilePath $dotnetPath `
    -Arguments @(
        "publish",
        $applicationProject,
        "-c", $Configuration,
        "-p:Platform=x64",
        "-r", "win-x64",
        "--self-contained", "true",
        "--no-restore",
        "-p:NuGetAudit=false",
        "-p:WindowsAppSDKSelfContained=true",
        "-p:Version=$Version",
        "-p:AssemblyVersion=$Version.0",
        "-p:FileVersion=$Version.0",
        "-p:InformationalVersion=$Version",
        "-o", $publishDirectory
    ) `
    -FailureMessage "Публикация Nexus Shell завершилась с ошибкой."

Get-ChildItem -LiteralPath $publishDirectory -Filter "*.pdb" -File -Recurse |
    Remove-Item -Force

Write-PayloadMarker `
    -Directory $publishDirectory `
    -PayloadVersion $Version
Assert-PayloadMarker `
    -Directory $publishDirectory `
    -PayloadVersion $Version

$requiredPublishFiles = @(
    $payloadMarkerName,
    "Nexus.App.exe",
    "Nexus.App.dll",
    "Nexus.App.deps.json",
    "Nexus.App.runtimeconfig.json",
    "Nexus.App.pri",
    "Nexus.Core.dll",
    "App.xbf",
    "MainWindow.xbf",
    "hostfxr.dll",
    "hostpolicy.dll",
    "coreclr.dll",
    "clrjit.dll",
    "System.Private.CoreLib.dll",
    "Microsoft.WindowsAppRuntime.dll",
    "Microsoft.WindowsAppRuntime.Bootstrap.dll",
    "Microsoft.ui.xaml.dll"
)
$missingPublishFiles = $requiredPublishFiles |
    Where-Object {
        -not (Test-Path -LiteralPath (Join-Path $publishDirectory $_) -PathType Leaf)
    }
if ($missingPublishFiles.Count -gt 0) {
    throw "Self-contained publish неполон. Отсутствуют: $($missingPublishFiles -join ', ')"
}
$selfContainedValidation = Test-SelfContainedPublish -Directory $publishDirectory

$applicationPath = Join-Path $publishDirectory "Nexus.App.exe"
$applicationVersion = (Get-Item -LiteralPath $applicationPath).VersionInfo.FileVersion
if ([string]::IsNullOrWhiteSpace($applicationVersion) -or
    -not $applicationVersion.StartsWith(
        "$Version.",
        [System.StringComparison]::Ordinal)) {
    throw "Версия Nexus.App.exe ($applicationVersion) не соответствует релизу $Version."
}

$certificate = $null
$resolvedSignToolPath = $null
if ($SignInstaller) {
    $certificate = Get-CodeSigningCertificate
    $resolvedSignToolPath = Get-SignToolPath
    Invoke-AuthenticodeSigning `
        -FilePath $applicationPath `
        -ToolPath $resolvedSignToolPath `
        -Certificate $certificate
}

foreach ($artifactPath in @(
        $setupPath,
        $checksumPath,
        $jsonManifestPath,
        $textManifestPath)) {
    if (Test-Path -LiteralPath $artifactPath) {
        Remove-Item -LiteralPath $artifactPath -Force
    }
}

$compilerPath = Get-InnoCompilerPath
Invoke-CheckedCommand `
    -FilePath $compilerPath `
    -Arguments @(
        "/Qp",
        "/DMyAppVersion=$Version",
        $installerDefinition
    ) `
    -FailureMessage "Inno Setup не создал установщик."

if (-not (Test-Path -LiteralPath $setupPath -PathType Leaf)) {
    throw "Inno Setup не создал ожидаемый файл: $setupPath"
}

if ($SignInstaller) {
    Invoke-AuthenticodeSigning `
        -FilePath $setupPath `
        -ToolPath $resolvedSignToolPath `
        -Certificate $certificate
}

$setup = Get-Item -LiteralPath $setupPath
$setupVersion = $setup.VersionInfo.FileVersion
if ([string]::IsNullOrWhiteSpace($setupVersion) -or
    -not $setupVersion.StartsWith(
        "$Version.",
        [System.StringComparison]::Ordinal)) {
    throw "Версия установщика ($setupVersion) не соответствует релизу $Version."
}

$extractionSmoke = if ($null -eq $resolvedInnoUnpackerPath) {
    Write-Warning "innounp.exe не найден: безустановочная распаковка пропущена."
    [ordered]@{
        status = "SKIPPED_UNPACKER_NOT_FOUND"
        tool = $null
        payloadDirectory = $null
    }
}
else {
    Invoke-InstallerExtractionSmoke `
        -InstallerPath $setupPath `
        -UnpackerPath $resolvedInnoUnpackerPath `
        -PayloadVersion $Version
}

$setupHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $setupPath).Hash.ToUpperInvariant()
$applicationSignature = Get-SignatureManifest `
    -FilePath $applicationPath `
    -SigningRequested $SignInstaller.IsPresent
$installerSignature = Get-SignatureManifest `
    -FilePath $setupPath `
    -SigningRequested $SignInstaller.IsPresent
$publishFiles = Get-ChildItem -LiteralPath $publishDirectory -File -Recurse
$publishBytes = ($publishFiles | Measure-Object Length -Sum).Sum
$dotnetSdkVersion = (& $dotnetPath --version).Trim()
$builtAtUtc = [DateTimeOffset]::UtcNow.ToString("O")

$manifest = [ordered]@{
    schemaVersion = 2
    product = "Nexus Shell"
    version = $Version
    configuration = $Configuration
    architecture = "x64"
    runtimeIdentifier = "win-x64"
    selfContained = $true
    windowsAppSdkSelfContained = $true
    restoreSkipped = $NoRestore.IsPresent
    consoleChecksPassed = $true
    dotnetSdkVersion = $dotnetSdkVersion
    builtAtUtc = $builtAtUtc
    payload = [ordered]@{
        directoryName = $payloadDirectoryName
        markerFile = $payloadMarkerName
        markerMagic = $payloadMarkerMagic
        cleanupPolicy = "owned-marker-and-matching-semver-only"
        legacyFlatPayloadPolicy = "preserve-unmarked"
    }
    publish = [ordered]@{
        directory = "work/installer/publish"
        fileCount = $publishFiles.Count
        bytes = $publishBytes
        requiredFiles = $requiredPublishFiles
        selfContainedValidation = $selfContainedValidation
        applicationSignature = $applicationSignature
    }
    installer = [ordered]@{
        fileName = $setup.Name
        bytes = $setup.Length
        sha256 = $setupHash
        signature = $installerSignature
        extractionSmoke = $extractionSmoke
    }
}

$utf8WithoutBom = [System.Text.UTF8Encoding]::new($false)
$json = $manifest | ConvertTo-Json -Depth 8
[System.IO.File]::WriteAllText(
    $jsonManifestPath,
    $json + [Environment]::NewLine,
    $utf8WithoutBom)
[System.IO.File]::WriteAllText(
    $checksumPath,
    "$setupHash *$($setup.Name)$([Environment]::NewLine)",
    $utf8WithoutBom)

$textManifest = @(
    "Nexus Shell release manifest",
    "Version: $Version",
    "Configuration: $Configuration",
    "Architecture: x64",
    "Runtime: win-x64, self-contained",
    "Payload directory: $payloadDirectoryName",
    "Payload marker: $payloadMarkerName",
    "Payload cleanup: owned marker + matching semantic version only",
    "Legacy flat payload: preserved when unmarked",
    "Built UTC: $builtAtUtc",
    "Dotnet SDK: $dotnetSdkVersion",
    "Console checks: PASSED",
    "Restore skipped: $($NoRestore.IsPresent)",
    "Publish files: $($publishFiles.Count)",
    "Publish bytes: $publishBytes",
    "Installer: $($setup.Name)",
    "Installer bytes: $($setup.Length)",
    "Installer SHA256: $setupHash",
    "Extraction smoke: $($extractionSmoke.status)",
    "Application signature: $($applicationSignature.status)",
    "Installer signature: $($installerSignature.status)"
) -join [Environment]::NewLine
[System.IO.File]::WriteAllText(
    $textManifestPath,
    $textManifest + [Environment]::NewLine,
    $utf8WithoutBom)

Write-Host "Release artifacts ready:"
Write-Host "  Installer: $($setup.FullName)"
Write-Host "  Size: $([Math]::Round($setup.Length / 1MB, 1)) MB"
Write-Host "  SHA256: $setupHash"
Write-Host "  Signature: $($installerSignature.status)"
Write-Host "  JSON manifest: $jsonManifestPath"
Write-Host "  Text manifest: $textManifestPath"
