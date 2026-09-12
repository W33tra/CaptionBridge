$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path

& (Join-Path $projectRoot "build_prerelease.ps1") `
    -EntryPoint "release.py" `
    -ExecutableName "CaptionBridge"

if ($LASTEXITCODE -ne 0) {
    throw "Release build failed."
}
