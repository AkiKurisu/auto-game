[CmdletBinding()]
param(
    [string]$Version = 'latest',
    [string]$InstallDir = (Join-Path ([Environment]::GetFolderPath('UserProfile')) '.auto-game\bin')
)

$ErrorActionPreference = 'Stop'
$repository = 'AkiKurisu/auto-game'

function Add-AutoGamePath([string]$Directory) {
    $target = [IO.Path]::GetFullPath($Directory).TrimEnd('\', '/')
    $user = @([Environment]::GetEnvironmentVariable('Path', 'User') -split ';' | Where-Object { $_ })
    if (@($user | Where-Object { $_.TrimEnd('\', '/') -ieq $target }).Count -eq 0) {
        [Environment]::SetEnvironmentVariable('Path', (($user + $target) -join ';'), 'User')
    }
    $current = @($env:Path -split ';' | Where-Object { $_ })
    if (@($current | Where-Object { $_.TrimEnd('\', '/') -ieq $target }).Count -eq 0) {
        $env:Path = (($current + $target) -join ';')
    }
}

function Install-AutoGame([string]$RequestedVersion, [string]$Destination) {
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT -or
        [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString() -ne 'X64') {
        throw 'Auto Game supports Windows x64 only.'
    }
    $headers = @{ 'User-Agent' = 'auto-game-install' }
    $tag = $RequestedVersion
    if ($tag -eq 'latest') {
        $tag = (Invoke-RestMethod -Uri "https://api.github.com/repos/$repository/releases/latest" -Headers $headers).tag_name
    }
    if ($tag -notmatch '^v?\d+\.\d+\.\d+$') { throw 'Invalid release version. Expected latest or vX.Y.Z.' }
    $number = $tag -replace '^v', ''
    $base = "https://github.com/$repository/releases/download/v$number"
    $artifact = Invoke-RestMethod -Uri "$base/artifact.json" -Headers $headers
    if ($artifact.version -cne $number -or $artifact.rid -cne 'win-x64' -or
        $artifact.fileName -cne 'auto-game.exe' -or $artifact.sha256 -notmatch '^[a-fA-F0-9]{64}$') {
        throw 'Release manifest does not match the requested Auto Game Windows x64 release.'
    }

    $root = [IO.Path]::GetFullPath($Destination)
    [IO.Directory]::CreateDirectory($root) | Out-Null
    $stage = Join-Path $root ('.auto-game-install-' + [Guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($stage) | Out-Null
    try {
        $staged = Join-Path $stage 'auto-game.exe'
        Write-Host "Downloading Auto Game $number (win-x64)"
        Invoke-WebRequest -Uri "$base/auto-game.exe" -OutFile $staged -Headers $headers
        if ((Get-FileHash -LiteralPath $staged -Algorithm SHA256).Hash -ine $artifact.sha256) {
            throw 'Downloaded executable failed SHA-256 validation. The existing installation was preserved.'
        }
        $target = Join-Path $root 'auto-game.exe'
        if ([IO.File]::Exists($target)) { [IO.File]::Replace($staged, $target, [NullString]::Value) }
        else { [IO.File]::Move($staged, $target) }
        Add-AutoGamePath $root
        Write-Host "Installed Auto Game $number to $root"
        Write-Host 'Open a new terminal if a running app cannot find auto-game yet.'
    }
    finally {
        Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if ($MyInvocation.InvocationName -ne '.') {
    Install-AutoGame -RequestedVersion $Version -Destination $InstallDir
}
