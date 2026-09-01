<#
.SYNOPSIS
    Publishes Device Lab as a self-contained win-x64 tree, with its licence notices.

.DESCRIPTION
    This is what a release ships and what WSGM builds from the pinned source submodule. The output
    is complete on its own: a machine with no .NET installed can run it, which is the point for a
    tool that inspects handhelds that are not development machines.

    The .NET runtime notices are copied out of the exact restored runtime pack rather than a
    checked-in copy. A self-contained publish redistributes that runtime, so the notice has to
    match the version actually embedded, and hardcoding it would drift silently on every bump.
#>
[CmdletBinding()]
param(
    [string]$OutputRoot = "publish",

    [string]$Configuration = "Release",

    [string]$RuntimeIdentifier = "win-x64",

    [string]$Version = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\WSGM.DeviceLab\WSGM.DeviceLab.csproj"
if ([string]::IsNullOrWhiteSpace($Version)) {
    $projectText = Get-Content -LiteralPath $project -Raw
    if ($projectText -notmatch '<Version>([^<]+)</Version>') {
        throw "Device Lab project does not declare a version."
    }
    $Version = $Matches[1]
}
$resolvedRoot = [IO.Path]::GetFullPath($root).TrimEnd([IO.Path]::DirectorySeparatorChar)
$destination = if ([IO.Path]::IsPathRooted($OutputRoot)) {
    [IO.Path]::GetFullPath($OutputRoot)
} else {
    [IO.Path]::GetFullPath((Join-Path $resolvedRoot $OutputRoot))
}
$markerName = ".wsgm-devicelab-publish-root"
$markerValue = "WSGM.DeviceLab publish output v1"

function Assert-SafePublishPath {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$RepositoryRoot
    )

    $resolved = [IO.Path]::GetFullPath($Path).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $relative = [IO.Path]::GetRelativePath($RepositoryRoot, $resolved)
    if ([string]::IsNullOrWhiteSpace($relative) -or $relative -eq "." -or
        [IO.Path]::IsPathRooted($relative) -or $relative -eq ".." -or
        $relative.StartsWith("..$([IO.Path]::DirectorySeparatorChar)", [StringComparison]::Ordinal)) {
        throw "Publish output must be a dedicated child of the Device Lab repository: $resolved"
    }

    $current = Split-Path -Parent $resolved
    while (-not [string]::IsNullOrWhiteSpace($current) -and
        $current.StartsWith($RepositoryRoot, [StringComparison]::OrdinalIgnoreCase)) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Publish output ancestry cannot contain a reparse point: $current"
            }
        }
        if ($current -ieq $RepositoryRoot) {
            break
        }
        $current = Split-Path -Parent $current
    }

    return $resolved
}

function Test-TreeContainsReparsePoint {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($Path)
    while ($pending.Count -gt 0) {
        $current = $pending.Pop()
        foreach ($entry in [IO.Directory]::EnumerateFileSystemEntries($current)) {
            $attributes = [IO.File]::GetAttributes($entry)
            if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                return $true
            }
            if (($attributes -band [IO.FileAttributes]::Directory) -ne 0) {
                $pending.Push($entry)
            }
        }
    }
    return $false
}

$destination = Assert-SafePublishPath -Path $destination -RepositoryRoot $resolvedRoot
$destinationParent = Split-Path -Parent $destination
$destinationLeaf = Split-Path -Leaf $destination
$staging = Join-Path $destinationParent ".$destinationLeaf.$([Guid]::NewGuid().ToString('N')).tmp"
$staging = Assert-SafePublishPath -Path $staging -RepositoryRoot $resolvedRoot
$backup = $null

if (-not (Test-Path -LiteralPath (Join-Path $root "external\WSGM.Device.Sdk\src") -PathType Container)) {
    throw "external\WSGM.Device.Sdk is empty. Clone with --recursive, or run: git submodule update --init"
}

if (Test-Path -LiteralPath $destination) {
    if (-not (Test-Path -LiteralPath $destination -PathType Container)) {
        throw "Publish destination exists and is not a directory: $destination"
    }
    $destinationItem = Get-Item -LiteralPath $destination -Force
    if (($destinationItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Publish destination cannot be a reparse point: $destination"
    }
    if (Test-TreeContainsReparsePoint -Path $destination) {
        throw "Publish destination contains a reparse point and will not be replaced: $destination"
    }
    $marker = Join-Path $destination $markerName
    if (-not (Test-Path -LiteralPath $marker -PathType Leaf) -or
        (Get-Content -LiteralPath $marker -Raw).Trim() -cne $markerValue) {
        throw "Refusing to replace an unowned directory. Remove it manually or choose a new -OutputRoot: $destination"
    }
}

try {
    [IO.Directory]::CreateDirectory($destinationParent) | Out-Null
    [IO.Directory]::CreateDirectory($staging) | Out-Null

    & dotnet publish $project `
        --configuration $Configuration `
        --runtime $RuntimeIdentifier `
        --self-contained true `
        --output $staging `
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
    Copy-Item -LiteralPath $source -Destination (Join-Path $staging $notice.Destination) -Force
}

Copy-Item -LiteralPath (Join-Path $root "LICENSE") `
    -Destination (Join-Path $staging "LICENSE.txt") -Force

Set-Content -LiteralPath (Join-Path $staging $markerName) `
    -Value $markerValue -NoNewline -Encoding UTF8
[IO.File]::SetAttributes(
    (Join-Path $staging $markerName),
    [IO.FileAttributes]::Hidden)

if (Test-TreeContainsReparsePoint -Path $staging) {
    throw "Publish staging unexpectedly contains a reparse point: $staging"
}

if (Test-Path -LiteralPath $destination) {
    if (Test-TreeContainsReparsePoint -Path $destination) {
        throw "Publish destination changed to contain a reparse point and will not be replaced: $destination"
    }
    $backup = Join-Path $destinationParent ".$destinationLeaf.$([Guid]::NewGuid().ToString('N')).backup"
    $backup = Assert-SafePublishPath -Path $backup -RepositoryRoot $resolvedRoot
    Move-Item -LiteralPath $destination -Destination $backup
}
Move-Item -LiteralPath $staging -Destination $destination

if ($null -ne $backup) {
    try {
        if (Test-TreeContainsReparsePoint -Path $backup) {
            throw "Previous publish changed to contain a reparse point: $backup"
        }
        Remove-Item -LiteralPath $backup -Recurse -Force
        $backup = $null
    } catch {
        Write-Warning "The new publish is complete, but the previous owned publish could not be removed: $backup"
    }
}
} catch {
    $publishFailure = $_
    if ($null -ne $backup -and
        -not (Test-Path -LiteralPath $destination) -and
        (Test-Path -LiteralPath $backup -PathType Container)) {
        try {
            $backupMarker = Join-Path $backup $markerName
            if ((Get-Item -LiteralPath $backup -Force).Attributes -band [IO.FileAttributes]::ReparsePoint -or
                (Test-TreeContainsReparsePoint -Path $backup) -or
                -not (Test-Path -LiteralPath $backupMarker -PathType Leaf) -or
                (Get-Content -LiteralPath $backupMarker -Raw).Trim() -cne $markerValue) {
                throw "The previous publish backup failed its ownership check: $backup"
            }
            Move-Item -LiteralPath $backup -Destination $destination
            $backup = $null
        } catch {
            Write-Warning "Publish replacement failed and the previous owned publish could not be restored: $backup"
        }
    }
    try {
        if (Test-Path -LiteralPath $staging -PathType Container) {
            $stagingItem = Get-Item -LiteralPath $staging -Force
            if (($stagingItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0 -and
                (Split-Path -Parent $staging) -ieq $destinationParent -and
                (Split-Path -Leaf $staging).StartsWith(".$destinationLeaf.", [StringComparison]::Ordinal) -and
                -not (Test-TreeContainsReparsePoint -Path $staging)) {
                Remove-Item -LiteralPath $staging -Recurse -Force
            }
        }
    } catch {
        Write-Warning "Publish staging cleanup failed: $staging"
    }
    throw $publishFailure
}

Write-Host "Device Lab published to $destination"
