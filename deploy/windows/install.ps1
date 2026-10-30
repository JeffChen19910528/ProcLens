<#
.SYNOPSIS
    Installs or removes ProcLens for the current user. No administrator rights are needed.

.DESCRIPTION
    Copies proclens.exe, README.txt and the docs folder to the install directory and adds that directory to your
    user PATH. Uninstalling removes only those files and the PATH entry.

    It never touches your audit log or your dump master key (%LOCALAPPDATA%\ProcLens). Dumps protected only by that
    key cannot be opened without it, so deleting it is left to you.

.PARAMETER InstallDir
    Where to install. Default: %LOCALAPPDATA%\Programs\ProcLens

.PARAMETER NoPath
    Do not change the PATH.

.PARAMETER Uninstall
    Remove ProcLens instead of installing it.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\install.ps1
    powershell -File .\install.ps1 -Uninstall
#>
[CmdletBinding()]
param(
    [string] $InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\ProcLens'),
    [switch] $NoPath,
    [switch] $Uninstall
)

$ErrorActionPreference = 'Stop'
$source = Split-Path -Parent $MyInvocation.MyCommand.Path
$InstallDir = [System.IO.Path]::GetFullPath($InstallDir)
$shipped = @('proclens.exe', 'README.txt')

function Get-UserPath { [Environment]::GetEnvironmentVariable('Path', 'User') }

function Test-OnPath([string] $path, [string] $dir) {
    if ([string]::IsNullOrEmpty($path)) { return $false }
    $wanted = $dir.TrimEnd('\')
    foreach ($entry in $path.Split(';')) {
        if ($entry.TrimEnd('\') -ieq $wanted) { return $true }
    }
    return $false
}

if ($Uninstall) {
    if (-not $NoPath) {
        $path = Get-UserPath
        if (Test-OnPath $path $InstallDir) {
            $kept = $path.Split(';') | Where-Object { $_ -and ($_.TrimEnd('\') -ine $InstallDir.TrimEnd('\')) }
            [Environment]::SetEnvironmentVariable('Path', ($kept -join ';'), 'User')
            Write-Host "Removed $InstallDir from your user PATH."
        }
    }

    foreach ($name in $shipped) {
        $file = Join-Path $InstallDir $name
        if (Test-Path -LiteralPath $file) {
            try { Remove-Item -LiteralPath $file -Force }
            catch { throw "Could not remove $file. Is proclens still running? ($($_.Exception.Message))" }
        }
    }
    $docs = Join-Path $InstallDir 'docs'
    if (Test-Path -LiteralPath $docs) {
        Get-ChildItem -LiteralPath $docs -Filter '*.md' -File | Remove-Item -Force
        if (-not (Get-ChildItem -LiteralPath $docs -Force)) { Remove-Item -LiteralPath $docs }
    }
    if ((Test-Path -LiteralPath $InstallDir) -and -not (Get-ChildItem -LiteralPath $InstallDir -Force)) {
        Remove-Item -LiteralPath $InstallDir
    }

    Write-Host "ProcLens was removed from $InstallDir."
    Write-Host "Your audit log and dump master key were NOT touched: $(Join-Path $env:LOCALAPPDATA 'ProcLens')"
    return
}

foreach ($name in $shipped) {
    if (-not (Test-Path -LiteralPath (Join-Path $source $name))) {
        throw "$name was not found next to this script. Run install.ps1 from the extracted ProcLens package."
    }
}

New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
foreach ($name in $shipped) {
    try { Copy-Item -LiteralPath (Join-Path $source $name) -Destination (Join-Path $InstallDir $name) -Force }
    catch { throw "Could not write $name to $InstallDir. Is proclens still running? ($($_.Exception.Message))" }
}
if (Test-Path -LiteralPath (Join-Path $source 'docs')) {
    Copy-Item -LiteralPath (Join-Path $source 'docs') -Destination $InstallDir -Recurse -Force
}

$exe = Join-Path $InstallDir 'proclens.exe'
$version = & $exe --version
if ($LASTEXITCODE -ne 0) { throw "The installed proclens.exe did not start (exit code $LASTEXITCODE)." }
Write-Host "Installed ProcLens $version to $InstallDir"

if (-not $NoPath) {
    $path = Get-UserPath
    if (-not (Test-OnPath $path $InstallDir)) {
        $new = if ([string]::IsNullOrEmpty($path)) { $InstallDir } else { $path.TrimEnd(';') + ';' + $InstallDir }
        [Environment]::SetEnvironmentVariable('Path', $new, 'User')
        Write-Host "Added $InstallDir to your user PATH. Open a new terminal to use 'proclens'."
    }
}
else {
    Write-Host "PATH was not changed. Run: $exe"
}

Write-Host "Next: proclens system capabilities   (documentation: $(Join-Path $InstallDir 'docs\CLI_USAGE.md'))"
Write-Host "This build is not code-signed. Verify the archive against SHA256SUMS.txt before first use."
