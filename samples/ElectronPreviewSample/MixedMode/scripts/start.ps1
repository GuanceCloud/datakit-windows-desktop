$ErrorActionPreference = "Stop"

$sampleRoot = Split-Path -Parent $PSScriptRoot
$hostPath = Join-Path $sampleRoot "native-host\build\Release\guance_windows_electron_mixed_host.exe"
$electronCommand = Join-Path $sampleRoot "node_modules\.bin\electron.cmd"

if (-not $env:GUANCE_RUM_APP_ID) {
    throw "Set GUANCE_RUM_APP_ID before starting the mixed preview."
}
if (-not (Test-Path -LiteralPath $hostPath)) {
    throw "Mixed C++ host was not built. Run npm run native:build first."
}
if (-not (Test-Path -LiteralPath $electronCommand)) {
    throw "Electron was not installed. Run npm install first."
}

$env:GUANCE_RUM_NATIVE_DATAKIT_URL = if ($env:GUANCE_RUM_DATAKIT_URL) {
    $env:GUANCE_RUM_DATAKIT_URL
} else {
    "http://127.0.0.1:9529"
}
$env:GUANCE_RUM_NATIVE_APP_ID = $env:GUANCE_RUM_APP_ID
$env:GUANCE_RUM_NATIVE_SERVICE = if ($env:GUANCE_RUM_SERVICE) {
    $env:GUANCE_RUM_SERVICE
} else {
    "electron-mixed-preview"
}
$env:GUANCE_RUM_NATIVE_ENV = if ($env:GUANCE_RUM_ENV) { $env:GUANCE_RUM_ENV } else { "local" }
$env:GUANCE_RUM_NATIVE_VERSION = "0.1.0"
$env:GUANCE_RUM_NATIVE_CACHE_PATH = Join-Path $env:LOCALAPPDATA "Guance\electron-mixed-preview"
$env:GUANCE_RUM_NATIVE_SAMPLE_RATE = "1"
$env:GUANCE_RUM_NATIVE_DEBUG = "1"

$hostStartInfo = [Diagnostics.ProcessStartInfo]::new()
$hostStartInfo.FileName = $hostPath
$hostStartInfo.WorkingDirectory = Split-Path -Parent $hostPath
$hostStartInfo.UseShellExecute = $false
$hostStartInfo.CreateNoWindow = $true
$hostStartInfo.RedirectStandardInput = $true
$hostProcess = [Diagnostics.Process]::Start($hostStartInfo)
try {
    & $electronCommand .
    $electronExitCode = $LASTEXITCODE
} finally {
    if (-not $hostProcess.HasExited) {
        $hostProcess.StandardInput.Close()
    }
    if (-not $hostProcess.HasExited) {
        try { $hostProcess.WaitForExit(3000) } catch { }
    }
    if (-not $hostProcess.HasExited) {
        Stop-Process -Id $hostProcess.Id -Force
    }
}
exit $electronExitCode
