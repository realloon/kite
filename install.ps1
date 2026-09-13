$ErrorActionPreference = 'Stop'

if (-not [Environment]::Is64BitOperatingSystem) {
    Write-Error "Unsupported architecture. kite requires 64-bit Windows."
    exit 1
}

$installDir = "$env:LOCALAPPDATA\kite"
$exePath = "$installDir\kite.exe"

New-Item -ItemType Directory -Force -Path $installDir | Out-Null

$url = "https://github.com/realloon/kite/releases/latest/download/kite-win-x64.exe"
Write-Host "Downloading kite from $url..."
Invoke-WebRequest -Uri $url -OutFile $exePath

$userPath = [Environment]::GetEnvironmentVariable("Path", "User")
if ($userPath -notlike "*$installDir*") {
    [Environment]::SetEnvironmentVariable("Path", "$installDir;$userPath", "User")
    Write-Host "Added $installDir to User PATH."
}

if ($env:Path -notlike "*$installDir*") {
    $env:Path = "$installDir;$env:Path"
}

Write-Host "kite installed to $exePath"
