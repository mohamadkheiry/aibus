param(
    [int]$DockerReadyTimeoutSeconds = 120,
    [int]$ApplicationReadyTimeoutSeconds = 120
)

$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$dockerDesktop = 'C:\Program Files\Docker\Docker\Docker Desktop.exe'
$dockerExe = 'C:\Program Files\Docker\Docker\resources\bin\docker.exe'
$healthUrl = 'http://127.0.0.1:8088/health'
$logDirectory = Join-Path $env:LOCALAPPDATA 'AiBus\watchdog'
$logFile = Join-Path $logDirectory ("watchdog-{0}.log" -f (Get-Date -Format 'yyyy-MM-dd'))
$mutex = New-Object System.Threading.Mutex($false, 'Local\AiBusLocalWatchdog')
$hasLock = $false

function Write-WatchdogLog([string]$Message) {
    if (-not (Test-Path -LiteralPath $logDirectory)) {
        New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
    }
    Add-Content -LiteralPath $logFile -Value ("{0:o} {1}" -f (Get-Date), $Message) -Encoding UTF8
}

function Test-AiBusHealth {
    try {
        $response = Invoke-WebRequest -UseBasicParsing -Uri $healthUrl -TimeoutSec 5
        return $response.StatusCode -eq 200 -and $response.Content.Trim() -eq 'Healthy'
    }
    catch {
        return $false
    }
}

function Test-DockerReady {
    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $dockerExe
    $startInfo.Arguments = 'version --format "{{.Server.Version}}"'
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $startInfo
    try {
        [void]$process.Start()
        if (-not $process.WaitForExit(8000)) {
            $process.Kill()
            return $false
        }
        return $process.ExitCode -eq 0 -and -not [string]::IsNullOrWhiteSpace($process.StandardOutput.ReadToEnd())
    }
    catch {
        return $false
    }
    finally {
        $process.Dispose()
    }
}

function Wait-Until([scriptblock]$Condition, [int]$TimeoutSeconds) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        if (& $Condition) { return $true }
        Start-Sleep -Seconds 5
    } while ((Get-Date) -lt $deadline)
    return $false
}

try {
    $hasLock = $mutex.WaitOne(0)
    if (-not $hasLock) { exit 0 }
    if (Test-AiBusHealth) { exit 0 }

    Write-WatchdogLog 'Health check failed; starting recovery.'
    if (-not (Test-Path -LiteralPath $dockerDesktop)) { throw "Docker Desktop was not found: $dockerDesktop" }
    if (-not (Test-Path -LiteralPath $dockerExe)) { throw "Docker CLI was not found: $dockerExe" }

    if (-not (Test-DockerReady)) {
        if (-not (Get-Process -Name 'Docker Desktop' -ErrorAction SilentlyContinue)) {
            Start-Process -FilePath $dockerDesktop -ArgumentList '--accept-license' -WindowStyle Hidden
            Write-WatchdogLog 'Docker Desktop launch requested.'
        }
        if (-not (Wait-Until ${function:Test-DockerReady} $DockerReadyTimeoutSeconds)) {
            throw "Docker did not become ready within $DockerReadyTimeoutSeconds seconds."
        }
    }

    $compose = Start-Process -FilePath $dockerExe -ArgumentList @('compose','up','-d') -WorkingDirectory $repoRoot -NoNewWindow -PassThru -Wait
    if ($compose.ExitCode -ne 0) { throw "docker compose up failed with exit code $($compose.ExitCode)." }
    if (-not (Wait-Until ${function:Test-AiBusHealth} $ApplicationReadyTimeoutSeconds)) {
        throw "AiBus did not become healthy within $ApplicationReadyTimeoutSeconds seconds."
    }

    Write-WatchdogLog 'Recovery completed; AiBus is healthy.'
    Get-ChildItem -LiteralPath $logDirectory -Filter 'watchdog-*.log' -File -ErrorAction SilentlyContinue |
        Where-Object LastWriteTime -lt (Get-Date).AddDays(-30) |
        Remove-Item -Force -ErrorAction SilentlyContinue
    exit 0
}
catch {
    Write-WatchdogLog ("Recovery failed: {0}" -f $_.Exception.Message)
    exit 1
}
finally {
    if ($hasLock) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
