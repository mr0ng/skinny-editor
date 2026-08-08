param(
    [string] $GlbPath
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$probeProject = Join-Path $repositoryRoot 'tools\StereoKitEditor.NativeInputProbe\StereoKitEditor.NativeInputProbe.csproj'

$probeArguments = @('run', '--project', $probeProject, '--configuration', 'Debug')
if (-not [string]::IsNullOrWhiteSpace($GlbPath)) {
    $probeArguments += @('--', [IO.Path]::GetFullPath($GlbPath))
}

dotnet @probeArguments
if ($LASTEXITCODE -ne 0) {
    throw "Native scene input probe failed with exit code $LASTEXITCODE."
}
