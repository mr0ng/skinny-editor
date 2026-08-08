[CmdletBinding()]
param(
    [string]$RuntimeIdentifier = "win-x64",
    [string]$Configuration = "Release",
    [string]$OutputDirectory = "artifacts\distribution",
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "release-common.ps1")
$Version = Get-SKinnyReleaseVersion -RepositoryRoot $repoRoot -RequestedVersion $Version
$distributionRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $OutputDirectory))
$packageName = "SKinny-Editor-$Version-$RuntimeIdentifier"
$publishRoot = Join-Path $distributionRoot $packageName
$archivePath = Join-Path $distributionRoot "$packageName.zip"
$distributionPrefix = $distributionRoot.TrimEnd(
    [System.IO.Path]::DirectorySeparatorChar,
    [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
$resolvedPublishRoot = [System.IO.Path]::GetFullPath($publishRoot)
if (-not $resolvedPublishRoot.StartsWith($distributionPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "The package staging directory resolved outside the requested distribution directory."
}

if (Test-Path -LiteralPath $publishRoot) {
    Remove-Item -LiteralPath $publishRoot -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $publishRoot | Out-Null
& dotnet publish (Join-Path $repoRoot "src\StereoKitEditor.App\StereoKitEditor.App.csproj") `
    --configuration $Configuration `
    --runtime $RuntimeIdentifier `
    --self-contained true `
    --output $publishRoot `
    -p:PublishSingleFile=false `
    -p:DebugType=None `
    -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) {
    throw "SKinny Editor publish failed."
}

$sdkDirectory = Join-Path $publishRoot "sdk"
& (Join-Path $PSScriptRoot "package-sdk.ps1") -Configuration $Configuration -OutputDirectory $sdkDirectory

$examplesDirectory = Join-Path $publishRoot "examples"
$exampleEditor = Join-Path $examplesDirectory "HelloEditor"
$exampleRuntime = Join-Path $examplesDirectory "HelloStereoKitProject"
New-Item -ItemType Directory -Force -Path $exampleEditor, $exampleRuntime | Out-Null
Copy-Item -LiteralPath (Join-Path $repoRoot "samples\HelloEditor\HelloEditor.skproject.json") -Destination $exampleEditor
Copy-Item -LiteralPath (Join-Path $repoRoot "samples\HelloEditor\Scenes") -Destination $exampleEditor -Recurse
if (Test-Path -LiteralPath (Join-Path $repoRoot "samples\HelloEditor\Assets")) {
    Copy-Item -LiteralPath (Join-Path $repoRoot "samples\HelloEditor\Assets") -Destination $exampleEditor -Recurse
}
if (Test-Path -LiteralPath (Join-Path $repoRoot "samples\HelloEditor\Templates")) {
    Copy-Item -LiteralPath (Join-Path $repoRoot "samples\HelloEditor\Templates") -Destination $exampleEditor -Recurse
}
Copy-Item -LiteralPath (Join-Path $repoRoot "samples\HelloStereoKitProject\Program.cs") -Destination $exampleRuntime

$exampleDescriptor = Join-Path $exampleEditor "HelloEditor.skproject.json"
$descriptorJson = (Get-Content -LiteralPath $exampleDescriptor -Raw).Replace(
    '"solution": "../../StereoKitEditor.sln"',
    '"solution": "../HelloStereoKitProject/HelloStereoKitProject.csproj"')
Set-Content -LiteralPath $exampleDescriptor -Value $descriptorJson -Encoding utf8

@"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="SKinny.Editor.Runtime" Version="$Version" />
  </ItemGroup>
</Project>
"@ | Set-Content -LiteralPath (Join-Path $exampleRuntime "HelloStereoKitProject.csproj") -Encoding utf8

@'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <config>
    <add key="globalPackagesFolder" value=".packages" />
  </config>
  <packageSources>
    <clear />
    <add key="skinny-bundled" value="..\..\sdk" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
'@ | Set-Content -LiteralPath (Join-Path $exampleRuntime "NuGet.config") -Encoding utf8

Copy-Item -LiteralPath (Join-Path $repoRoot "README.md") -Destination $publishRoot
Copy-Item -LiteralPath (Join-Path $repoRoot "LICENSE") -Destination $publishRoot
Copy-Item -LiteralPath (Join-Path $repoRoot "THIRD-PARTY-NOTICES.md") -Destination $publishRoot
Copy-Item -LiteralPath (Join-Path $repoRoot "docs") -Destination $publishRoot -Recurse

$licensesDirectory = Join-Path $publishRoot "licenses"
New-Item -ItemType Directory -Force -Path $licensesDirectory | Out-Null

$globalPackagesOutput = (& dotnet nuget locals global-packages --list | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $globalPackagesOutput -notmatch '^global-packages:\s*(.+)$') {
    throw "Unable to resolve the NuGet global-packages directory for third-party notices."
}
$globalPackagesDirectory = [System.IO.Path]::GetFullPath($Matches[1].Trim())

$packageNoticeFiles = @(
    @("avalonia.angle.windows.natives", "2.1.25547.20250602", "LICENSE", "Avalonia.ANGLE-LICENSE.txt"),
    @("harfbuzzsharp.nativeassets.win32", "8.3.1.1", "LICENSE.txt", "HarfBuzzSharp-LICENSE.txt"),
    @("harfbuzzsharp.nativeassets.win32", "8.3.1.1", "THIRD-PARTY-NOTICES.txt", "HarfBuzzSharp-THIRD-PARTY-NOTICES.txt"),
    @("skiasharp.nativeassets.win32", "2.88.9", "LICENSE.txt", "SkiaSharp-LICENSE.txt"),
    @("skiasharp.nativeassets.win32", "2.88.9", "THIRD-PARTY-NOTICES.txt", "SkiaSharp-THIRD-PARTY-NOTICES.txt"),
    @("system.io.pipelines", "8.0.0", "LICENSE.TXT", "System.IO.Pipelines-LICENSE.txt"),
    @("system.io.pipelines", "8.0.0", "THIRD-PARTY-NOTICES.TXT", "System.IO.Pipelines-THIRD-PARTY-NOTICES.txt")
)
foreach ($notice in $packageNoticeFiles) {
    $noticeSource = Join-Path $globalPackagesDirectory (Join-Path $notice[0] (Join-Path $notice[1] $notice[2]))
    if (-not (Test-Path -LiteralPath $noticeSource -PathType Leaf)) {
        throw "Required third-party notice was not found: $noticeSource"
    }
    Copy-Item -LiteralPath $noticeSource -Destination (Join-Path $licensesDirectory $notice[3])
}

$dotnetRoot = Split-Path -Parent (Get-Command dotnet -ErrorAction Stop).Source
foreach ($dotnetNotice in @("LICENSE.txt", "ThirdPartyNotices.txt")) {
    $dotnetNoticeSource = Join-Path $dotnetRoot $dotnetNotice
    if (-not (Test-Path -LiteralPath $dotnetNoticeSource -PathType Leaf)) {
        throw "Required .NET redistribution notice was not found: $dotnetNoticeSource"
    }
    Copy-Item -LiteralPath $dotnetNoticeSource -Destination (Join-Path $licensesDirectory "dotnet-$dotnetNotice")
}

if (Test-Path -LiteralPath $archivePath) {
    Remove-Item -LiteralPath $archivePath -Force
}

Compress-Archive -Path (Join-Path $publishRoot "*") -DestinationPath $archivePath -CompressionLevel Optimal
$hash = Get-FileHash -LiteralPath $archivePath -Algorithm SHA256
Set-Content -LiteralPath "$archivePath.sha256" -Value "$($hash.Hash.ToLowerInvariant())  $([System.IO.Path]::GetFileName($archivePath))"

Get-Item -LiteralPath $archivePath, "$archivePath.sha256" | Select-Object FullName, Length, LastWriteTime
