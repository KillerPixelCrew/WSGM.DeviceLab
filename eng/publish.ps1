<#
.SYNOPSIS
    Publishes Device Lab as a self-contained win-x64 tree, with its licence notices.

.DESCRIPTION
    This is what a release ships and what WSGM pins. The output is complete on its own: a machine
    with no .NET installed can run it, which is the point for a tool that inspects handhelds that
    are not development machines.

    The .NET runtime notices are copied out of the exact restored runtime pack rather than a
    checked-in copy. A self-contained publish redistributes that runtime, so the notice has to
    match the version actually embedded, and hardcoding it would drift silently on every bump.
#>
[CmdletBinding()]
param(
    [string]$OutputRoot = "publish",

    [string]$Configuration = "Release",

    [string]$RuntimeIdentifier = "win-x64",

    [string]$Version = "0.1.0"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\WSGM.DeviceLab\WSGM.DeviceLab.csproj"
$destination = [IO.Path]::GetFullPath((Join-Path $root $OutputRoot))

if (-not (Test-Path -LiteralPath (Join-Path $root "external\WSGM.Device.Sdk\src") -PathType Container)) {
    throw "external\WSGM.Device.Sdk is empty. Clone with --recursive, or run: git submodule update --init"
}

if (Test-Path -LiteralPath $destination) {
    Remove-Item -LiteralPath $destination -Recurse -Force
}

& dotnet publish $project `
    --configuration $Configuration `
    --runtime $RuntimeIdentifier `
    --self-contained true `
    --output $destination `
    /p:Version=$Version `
    /p:PublishSingleFile=false `
    /p:TreatWarningsAsErrors=true `
    -m:1
if ($LASTEXITCODE -ne 0) {
    throw "Publishing Device Lab failed."
}

# The runtime notices, taken from the pack this publish actually restored.
$assetsPath = Join-Path $root "src\WSGM.DeviceLab\obj\project.assets.json"
if (-not (Test-Path -LiteralPath $assetsPath -PathType Leaf)) {
    throw "Restore assets are missing: $assetsPath"
}

$assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json -Depth 100
$runtimePackName = "Microsoft.NETCore.App.Runtime.$RuntimeIdentifier"
$frameworks = @($assets.project.frameworks.psobject.Properties | ForEach-Object { $_.Value })
$runtimeDependencies = @(
    $frameworks |
        ForEach-Object { $_.downloadDependencies } |
        Where-Object { [string]$_.name -ieq $runtimePackName }
)
if ($runtimeDependencies.Count -ne 1) {
    throw "Restore must resolve exactly one $runtimePackName pack."
}

# An exact pin, not a range: the notice must describe one specific redistributed runtime.
$versionRange = ([string]$runtimeDependencies[0].version -replace '^\[|\]$', '')
$bounds = @($versionRange.Split(',') | ForEach-Object { $_.Trim() })
if ($bounds.Count -ne 2 -or $bounds[0] -cne $bounds[1] -or [string]::IsNullOrWhiteSpace($bounds[0])) {
    throw "Runtime pack version is not exact: $($runtimeDependencies[0].version)"
}

$runtimePack = $null
foreach ($packageFolder in $assets.packageFolders.psobject.Properties.Name) {
    $candidate = Join-Path (Join-Path $packageFolder ($runtimePackName.ToLowerInvariant())) $bounds[0]
    if (Test-Path -LiteralPath $candidate -PathType Container) {
        $runtimePack = $candidate
        break
    }
}
if ($null -eq $runtimePack) {
    throw "Resolved runtime pack was not found in the restored package folders."
}

foreach ($notice in @(
    @{ Source = "LICENSE.TXT"; Destination = "DotNetRuntime-LICENSE.txt" },
    @{ Source = "THIRD-PARTY-NOTICES.TXT"; Destination = "DotNetRuntime-THIRD-PARTY-NOTICES.txt" }
)) {
    $source = Join-Path $runtimePack $notice.Source
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Required .NET runtime notice is missing: $source"
    }
    Copy-Item -LiteralPath $source -Destination (Join-Path $destination $notice.Destination) -Force
}

Copy-Item -LiteralPath (Join-Path $root "LICENSE") `
    -Destination (Join-Path $destination "LICENSE.txt") -Force

Write-Host "Device Lab published to $destination"
