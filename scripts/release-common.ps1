function Get-SKinnyReleaseVersion {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepositoryRoot,
        [string]$RequestedVersion = ""
    )

    if (-not [string]::IsNullOrWhiteSpace($RequestedVersion)) {
        $version = $RequestedVersion.Trim()
    }
    else {
        $propsPath = Join-Path $RepositoryRoot "Directory.Build.props"
        if (-not (Test-Path -LiteralPath $propsPath -PathType Leaf)) {
            throw "Release version source was not found: $propsPath"
        }

        [xml]$props = Get-Content -LiteralPath $propsPath -Raw
        $version = [string]$props.Project.PropertyGroup.Version
    }

    if ([string]::IsNullOrWhiteSpace($version) -or $version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$') {
        throw "Invalid SKinny Editor release version: '$version'"
    }

    return $version
}
