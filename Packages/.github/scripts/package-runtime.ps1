[CmdletBinding()]
param(
    [string]$QuickServerRepo = "https://github.com/OneYoungMean/NvlabKimodoQuickServer.git",
    [string]$QuickServerRef = "main",
    [string]$OutputZip
)

$ErrorActionPreference = "Stop"

function Ensure-Command {
    param([string]$Name)

    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "Required command not found: $Name"
    }
}

Ensure-Command git
Ensure-Command robocopy
Ensure-Command tar

function Remove-PathIfExists {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    $item = Get-Item -LiteralPath $Path -Force
    if ($item.PSIsContainer) {
        [System.IO.Directory]::Delete($item.FullName, $true)
        return
    }

    [System.IO.File]::Delete($item.FullName)
}

function Get-GitHubZipballUrl {
    param(
        [string]$RepoUrl,
        [string]$Ref
    )

    $match = [regex]::Match($RepoUrl, 'github\.com[:/](?<owner>[^/]+)/(?<repo>[^/.]+?)(?:\.git)?$')
    if (-not $match.Success) {
        throw "Only GitHub repository URLs are supported: $RepoUrl"
    }

    return "https://api.github.com/repos/$($match.Groups['owner'].Value)/$($match.Groups['repo'].Value)/zipball/$Ref"
}

$scriptRoot = (Resolve-Path $PSScriptRoot).Path
$repoRoot = (Resolve-Path (Join-Path $scriptRoot "..\..")).Path

if ([string]::IsNullOrWhiteSpace($QuickServerRef)) {
    $QuickServerRef = "main"
}

if ([string]::IsNullOrWhiteSpace($OutputZip)) {
    $OutputZip = Join-Path $repoRoot "dist\KimodoUnityBridge.zip"
}

$tempRoot = Join-Path ([System.IO.Path]::GetPathRoot($repoRoot)) ("kpack-" + [Guid]::NewGuid().ToString("N"))
$repoArchive = Join-Path $tempRoot "repo.zip"
$stagingRoot = Join-Path $tempRoot "staging"
$quickServerArchive = Join-Path $tempRoot "quickserver.zip"
$quickServerExtract = Join-Path $tempRoot "quickserver-extract"
$runtimeRoot = Join-Path $stagingRoot "NvlabKimodoQuickServer~"
$quickServerZipballUrl = Get-GitHubZipballUrl -RepoUrl $QuickServerRepo -Ref $QuickServerRef

Write-Host "Repo root: $repoRoot"
Write-Host "QuickServer ref: $QuickServerRef"
Write-Host "Output zip: $OutputZip"

try {
    New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null
    New-Item -ItemType Directory -Force -Path $stagingRoot | Out-Null

    Write-Host "Exporting tracked files from this repository..."
    & git -C $repoRoot archive --format=zip -o $repoArchive HEAD
    if ($LASTEXITCODE -ne 0) {
        throw "git archive failed with exit code $LASTEXITCODE"
    }

    Expand-Archive -LiteralPath $repoArchive -DestinationPath $stagingRoot -Force

    $githubDir = Join-Path $stagingRoot ".github"
    if (Test-Path $githubDir) {
        Remove-PathIfExists -Path $githubDir
    }

    Write-Host "Downloading external runtime archive..."
    Invoke-WebRequest -Uri $quickServerZipballUrl -OutFile $quickServerArchive -Headers @{ "User-Agent" = "KimodoUnityBridge-Packager" }

    New-Item -ItemType Directory -Force -Path $quickServerExtract | Out-Null
    Expand-Archive -LiteralPath $quickServerArchive -DestinationPath $quickServerExtract -Force

    $quickServerContentRoot = Get-ChildItem -LiteralPath $quickServerExtract -Directory | Select-Object -First 1
    if ($null -eq $quickServerContentRoot) {
        throw "Downloaded runtime archive did not contain a top-level directory."
    }

    New-Item -ItemType Directory -Force -Path $runtimeRoot | Out-Null

    Write-Host "Copying runtime repo into NvlabKimodoQuickServer~..."
    & robocopy $quickServerContentRoot.FullName $runtimeRoot /MIR /R:2 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) {
        throw "robocopy failed with exit code $LASTEXITCODE"
    }

    $outputDir = Split-Path -Parent $OutputZip
    if (-not [string]::IsNullOrWhiteSpace($outputDir)) {
        New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
    }

    if (Test-Path $OutputZip) {
        Remove-PathIfExists -Path $OutputZip
    }

    Write-Host "Creating zip archive..."
    & tar -a -cf $OutputZip -C $stagingRoot .
    if ($LASTEXITCODE -ne 0) {
        throw "tar failed with exit code $LASTEXITCODE"
    }

    Write-Host "Package ready: $OutputZip"
}
finally {
    try {
        Remove-PathIfExists -Path $tempRoot
    }
    catch {
        Write-Warning ("Failed to remove temporary path: " + $tempRoot + " - " + $_.Exception.Message)
    }
}
