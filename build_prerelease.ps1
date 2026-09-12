$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$buildPython = Join-Path $projectRoot ".build-venv\Scripts\python.exe"
$nativeOutput = Join-Path $projectRoot "build\native\win-x64"
$pyinstallerWork = Join-Path $projectRoot "build\pyinstaller"
$distribution = Join-Path $projectRoot "dist"

if (-not (Test-Path -LiteralPath $buildPython)) {
    python -m venv (Join-Path $projectRoot ".build-venv")
    if ($LASTEXITCODE -ne 0) { throw "Build environment creation failed." }
    & $buildPython -m pip install --disable-pip-version-check `
        -r (Join-Path $projectRoot "requirements.txt") `
        pyinstaller
    if ($LASTEXITCODE -ne 0) { throw "Build dependencies installation failed." }
}

New-Item -ItemType Directory -Force -Path $nativeOutput | Out-Null
New-Item -ItemType Directory -Force -Path $pyinstallerWork | Out-Null
New-Item -ItemType Directory -Force -Path $distribution | Out-Null

dotnet publish (Join-Path $projectRoot "capture_helper\CaptionBridge.Capture.csproj") `
    -c Release `
    -r win-x64 `
    --self-contained true `
    --nologo `
    -o $nativeOutput
if ($LASTEXITCODE -ne 0) { throw "Capture helper publish failed." }

dotnet publish (Join-Path $projectRoot "overlay_helper\CaptionBridge.Overlay.csproj") `
    -c Release `
    -r win-x64 `
    --self-contained true `
    --nologo `
    -o $nativeOutput
if ($LASTEXITCODE -ne 0) { throw "Overlay helper publish failed." }

& $buildPython -m PyInstaller `
    --noconfirm `
    --clean `
    --onefile `
    --console `
    --name "CaptionBridge-Prerelease" `
    --distpath $distribution `
    --workpath $pyinstallerWork `
    --specpath $pyinstallerWork `
    --add-data "$(Join-Path $projectRoot 'web\final.html');web" `
    --add-data "$(Join-Path $projectRoot 'web\style.css');web" `
    --add-data "$(Join-Path $projectRoot 'web\final.css');web" `
    --add-data "$(Join-Path $projectRoot 'web\app.js');web" `
    --add-data "$(Join-Path $projectRoot 'web\final.js');web" `
    --add-data "$(Join-Path $projectRoot 'audio\caption_check_ru.wav');audio" `
    --add-binary "$nativeOutput;native" `
    (Join-Path $projectRoot "prerelease.py")
if ($LASTEXITCODE -ne 0) { throw "PyInstaller build failed." }

$resultPath = Join-Path $distribution "CaptionBridge-Prerelease.exe"
Write-Output "Built: $resultPath"
