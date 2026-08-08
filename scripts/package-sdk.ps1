[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$OutputDirectory = "artifacts\packages"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$output = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) {
    [System.IO.Path]::GetFullPath($OutputDirectory)
} else {
    [System.IO.Path]::GetFullPath((Join-Path $repoRoot $OutputDirectory))
}
New-Item -ItemType Directory -Force -Path $output | Out-Null

$projects = @(
    "src\StereoKitEditor.Adapter.Abstractions\StereoKitEditor.Adapter.Abstractions.csproj",
    "src\StereoKitEditor.Scene\StereoKitEditor.Scene.csproj",
    "src\StereoKitEditor.Protocol\StereoKitEditor.Protocol.csproj",
    "src\StereoKitEditor.Runtime\StereoKitEditor.Runtime.csproj"
)

foreach ($project in $projects) {
    & dotnet pack (Join-Path $repoRoot $project) --configuration $Configuration --output $output
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet pack failed for $project"
    }
}

Get-ChildItem -LiteralPath $output -Filter "SKinny.Editor.*.nupkg" |
    Sort-Object Name |
    Select-Object Name, Length, LastWriteTime
