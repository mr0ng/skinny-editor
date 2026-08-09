param(
    [string]$DistributionDirectory = "artifacts\distribution",
    [string]$RuntimeIdentifier = "win-x64",
    [string]$Project = "",
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"
$workspace = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
. (Join-Path $PSScriptRoot "release-common.ps1")
$Version = Get-SKinnyReleaseVersion -RepositoryRoot $workspace -RequestedVersion $Version
$distribution = [System.IO.Path]::GetFullPath((Join-Path $workspace $DistributionDirectory))
$packageName = "SKinny-Editor-$Version-$RuntimeIdentifier"
$packageRoot = [System.IO.Path]::GetFullPath((Join-Path $distribution $packageName))
$archive = [System.IO.Path]::GetFullPath((Join-Path $distribution "$packageName.zip"))
$checksum = "$archive.sha256"
$verificationRoot = [System.IO.Path]::GetFullPath((Join-Path $workspace "artifacts\verification"))
$extractedRoot = [System.IO.Path]::GetFullPath((Join-Path $verificationRoot $packageName))
$verificationPrefix = $verificationRoot.TrimEnd(
    [System.IO.Path]::DirectorySeparatorChar,
    [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
if (-not $extractedRoot.StartsWith($verificationPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "The package extraction directory resolved outside the verification directory."
}
$executable = Join-Path $extractedRoot "SKinny.Editor.exe"
$projectPath = if ([string]::IsNullOrWhiteSpace($Project)) {
    Join-Path $extractedRoot "examples\HelloEditor\HelloEditor.skproject.json"
}
else {
    [System.IO.Path]::GetFullPath((Join-Path $workspace $Project))
}

foreach ($required in @($packageRoot, $archive, $checksum)) {
    if (-not (Test-Path -LiteralPath $required)) {
        throw "Required packaged-smoke input was not found: $required"
    }
}

$expectedHash = ((Get-Content -LiteralPath $checksum -Raw).Trim() -split '\s+')[0]
$actualHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
if (-not [string]::Equals($expectedHash, $actualHash, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "The packaged ZIP checksum does not match its SHA-256 file."
}

if (Test-Path -LiteralPath $extractedRoot) {
    Remove-Item -LiteralPath $extractedRoot -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $extractedRoot | Out-Null
Expand-Archive -LiteralPath $archive -DestinationPath $extractedRoot

foreach ($required in @(
    $executable,
    $projectPath,
    (Join-Path $extractedRoot "Templates\StereoKitApp\1\__PROJECT_NAME__.csproj"),
    (Join-Path $extractedRoot "LICENSE"),
    (Join-Path $extractedRoot "THIRD-PARTY-NOTICES.md"))) {
    if (-not (Test-Path -LiteralPath $required)) {
        throw "Required extracted-package input was not found: $required"
    }
}

function Test-Startup([string[]]$Arguments, [string]$Label) {
    $startParameters = @{
        FilePath = $executable
        WorkingDirectory = $extractedRoot
        PassThru = $true
        WindowStyle = "Hidden"
    }
    if ($Arguments.Count -gt 0) {
        $startParameters.ArgumentList = $Arguments | ForEach-Object {
            if ($_ -match '\s') { '"' + $_.Replace('"', '\"') + '"' } else { $_ }
        }
    }
    $process = Start-Process @startParameters
    try {
        Start-Sleep -Seconds 6
        $process.Refresh()
        if ($process.HasExited) {
            throw "$Label exited early with code $($process.ExitCode)."
        }
    }
    finally {
        if (-not $process.HasExited) {
            & taskkill.exe /PID $process.Id /T /F 2>$null | Out-Null
        }
    }

    Write-Host "  $Label passed."
}

Write-Host "Packaged editor verification"
Write-Host "  ZIP SHA-256 passed."
Test-Startup @() "No-project launcher startup"
Test-Startup @('--project', $projectPath) "Explicit-project startup"
$probeProject = Join-Path $workspace "tools\StereoKitEditor.ProjectProbe\StereoKitEditor.ProjectProbe.csproj"
& dotnet run --project $probeProject --configuration Debug -- $projectPath "com.example.marker"
if ($LASTEXITCODE -ne 0) {
    throw "The bundled example failed its isolated Scene/Play project probe."
}
Write-Host "  Bundled example Scene/Play probe passed."
Write-Host "Packaged editor verification passed."
