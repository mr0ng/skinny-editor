param(
    [string]$Configuration = "Release",
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"
$workspace = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
. (Join-Path $PSScriptRoot "release-common.ps1")
$Version = Get-SKinnyReleaseVersion -RepositoryRoot $workspace -RequestedVersion $Version
$verificationRoot = [System.IO.Path]::GetFullPath((Join-Path $workspace "artifacts\verification"))
$consumerRoot = [System.IO.Path]::GetFullPath((Join-Path $verificationRoot "package-consumer"))
$packageRoot = [System.IO.Path]::GetFullPath((Join-Path $workspace "artifacts\packages"))

if (-not $consumerRoot.StartsWith($verificationRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to replace a verification directory outside $verificationRoot"
}

if (Test-Path -LiteralPath $consumerRoot) {
    Remove-Item -LiteralPath $consumerRoot -Recurse -Force
}

New-Item -ItemType Directory -Path $consumerRoot -Force | Out-Null

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
"@ | Set-Content -LiteralPath (Join-Path $consumerRoot "PackageConsumer.csproj") -Encoding utf8

@'
using StereoKitEditor.Adapter;
using StereoKitEditor.Protocol;
using StereoKitEditor.Runtime;
using StereoKitEditor.Scene;

var scene = new SceneDocument { Name = "Package consumer" };
Console.WriteLine($"contract={AdapterContractVersion.Current};protocol={ProtocolVersion.Major};scene={scene.FormatVersion};runtime={typeof(EditorRuntimeHost).Assembly.GetName().Name}");
'@ | Set-Content -LiteralPath (Join-Path $consumerRoot "Program.cs") -Encoding utf8

$escapedPackageRoot = [System.Security.SecurityElement]::Escape($packageRoot)
@"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="skinny-local" value="$escapedPackageRoot" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
"@ | Set-Content -LiteralPath (Join-Path $consumerRoot "NuGet.config") -Encoding utf8

$packagesPath = Join-Path $consumerRoot ".packages"
dotnet restore (Join-Path $consumerRoot "PackageConsumer.csproj") `
    --configfile (Join-Path $consumerRoot "NuGet.config") `
    --packages $packagesPath `
    --no-cache
if ($LASTEXITCODE -ne 0) { throw "Package consumer restore failed." }

dotnet build (Join-Path $consumerRoot "PackageConsumer.csproj") `
    --configuration $Configuration `
    --no-restore
if ($LASTEXITCODE -ne 0) { throw "Package consumer build failed." }

$output = & dotnet run `
    --project (Join-Path $consumerRoot "PackageConsumer.csproj") `
    --configuration $Configuration `
    --no-build `
    --no-restore
if ($LASTEXITCODE -ne 0) { throw "Package consumer execution failed." }

$expected = "contract=0.3;protocol=2;scene=2;runtime=StereoKitEditor.Runtime"
if ($output -notcontains $expected) {
    throw "Package consumer returned unexpected output: $($output -join ' ')"
}

Write-Host "Package consumer verification passed."
Write-Host "  $expected"
