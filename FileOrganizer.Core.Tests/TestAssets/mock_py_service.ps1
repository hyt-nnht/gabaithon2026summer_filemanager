# Dummy stand-in for py_service (uvicorn/FastAPI) startup handshake.
# AI_IMPLEMENTATION_GUIDE.md section 3.1: read ORGANIZER_IPC_TOKEN from the environment,
# then print a single "PORT: {number}" line to stdout before staying resident.
#
# Launched by PythonProcessManagerTests via powershell.exe.
# Kept ASCII-only: Windows PowerShell 5.1 reads a BOM-less script file using the
# system codepage, and non-ASCII comments can get mangled into a parser error.
#
# Parameters:
#   -Port          Port number to print (default 55123)
#   -DelaySeconds  Delay before printing the PORT line, for the timeout test case (default 0)
#   -SuppressPort  When set, never prints the PORT line, for the timeout test case
#   -ExitCode      When set (>= 0), exits immediately with this code before printing PORT,
#                  for the early-exit-before-handshake test case

param(
    [int]$Port = 55123,
    [int]$DelaySeconds = 0,
    [switch]$SuppressPort,
    [int]$ExitCode = -1
)

if ($ExitCode -ge 0) {
    [Console]::Error.WriteLine("mock_py_service: simulating early exit with code $ExitCode")
    exit $ExitCode
}

if (-not $env:ORGANIZER_IPC_TOKEN) {
    [Console]::Error.WriteLine("mock_py_service: ORGANIZER_IPC_TOKEN is not set")
    exit 1
}

if ($DelaySeconds -gt 0) {
    Start-Sleep -Seconds $DelaySeconds
}

if (-not $SuppressPort) {
    Write-Output "PORT: $Port"
    [Console]::Out.Flush()
}

# Stay resident until killed via the Job Object, mimicking the real uvicorn process.
while ($true) {
    Start-Sleep -Seconds 3600
}
