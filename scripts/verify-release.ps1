[CmdletBinding()]
param(
    [string]$ReferenceProjectVerifier = $env:SKINNY_REFERENCE_PROJECT_VERIFIER
)

$ErrorActionPreference = "Stop"
$workspace = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$solution = Join-Path $workspace "StereoKitEditor.sln"

function Invoke-Checked([scriptblock]$Action, [string]$FailureMessage) {
    & $Action
    if ($LASTEXITCODE -ne 0) {
        throw $FailureMessage
    }
}

Write-Host "[1/8] Release build"
Invoke-Checked { dotnet build $solution --configuration Release } "Release build failed."

Write-Host "[2/8] Automated tests"
Invoke-Checked { dotnet test $solution --configuration Release --no-build --no-restore } "Automated tests failed."

Write-Host "[3/8] Native Scene input"
& (Join-Path $PSScriptRoot "verify-native-scene-input.ps1")

Write-Host "[4/8] SDK packages"
& (Join-Path $PSScriptRoot "package-sdk.ps1") -Configuration Release

Write-Host "[5/8] Clean package consumer"
& (Join-Path $PSScriptRoot "verify-package-consumer.ps1") -Configuration Release

Write-Host "[6/8] Windows distribution"
& (Join-Path $PSScriptRoot "package-windows.ps1") -Configuration Release

Write-Host "[7/8] Packaged startup and checksum"
& (Join-Path $PSScriptRoot "verify-packaged-editor.ps1")

Write-Host "[8/8] Reference-project integration"
if ([string]::IsNullOrWhiteSpace($ReferenceProjectVerifier)) {
    Write-Host "  Skipped. Set SKINNY_REFERENCE_PROJECT_VERIFIER to an optional local verification script."
}
else {
    $resolvedVerifier = [System.IO.Path]::GetFullPath($ReferenceProjectVerifier)
    if (-not (Test-Path -LiteralPath $resolvedVerifier -PathType Leaf)) {
        throw "The optional reference-project verifier was not found at $resolvedVerifier."
    }

    & $resolvedVerifier
}

Write-Host "SKinny Editor release verification passed."
