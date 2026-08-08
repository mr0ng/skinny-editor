[CmdletBinding()]
param(
    [string]$RuntimeIdentifier = "win-x64",
    [string]$Configuration = "Release",
    [string]$OutputDirectory = "artifacts\distribution"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$distributionRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $OutputDirectory))
$publishRoot = Join-Path $distributionRoot "SKinny-Editor-$RuntimeIdentifier"
$archivePath = Join-Path $distributionRoot "SKinny-Editor-$RuntimeIdentifier.zip"
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
    <PackageReference Include="SKinny.Editor.Runtime" Version="0.3.0-preview.1" />
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
Copy-Item -LiteralPath (Join-Path $repoRoot "docs\15-installation-and-onboarding.md") -Destination $publishRoot
Copy-Item -LiteralPath (Join-Path $repoRoot "docs\16-extension-authoring.md") -Destination $publishRoot
Copy-Item -LiteralPath (Join-Path $repoRoot "docs\17-completion-and-acceptance.md") -Destination $publishRoot
Copy-Item -LiteralPath (Join-Path $repoRoot "docs\19-visual-content-materials-text-and-spatial-ui.md") -Destination $publishRoot
if (Test-Path -LiteralPath (Join-Path $repoRoot "docs\20-visual-authoring-guide.md")) {
    Copy-Item -LiteralPath (Join-Path $repoRoot "docs\20-visual-authoring-guide.md") -Destination $publishRoot
}

if (Test-Path -LiteralPath $archivePath) {
    Remove-Item -LiteralPath $archivePath -Force
}

Compress-Archive -Path (Join-Path $publishRoot "*") -DestinationPath $archivePath -CompressionLevel Optimal
$hash = Get-FileHash -LiteralPath $archivePath -Algorithm SHA256
Set-Content -LiteralPath "$archivePath.sha256" -Value "$($hash.Hash.ToLowerInvariant())  $([System.IO.Path]::GetFileName($archivePath))"

Get-Item -LiteralPath $archivePath, "$archivePath.sha256" | Select-Object FullName, Length, LastWriteTime
