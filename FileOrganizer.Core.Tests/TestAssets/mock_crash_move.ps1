# Simulates the file-move side effect of a real move operation, then hangs so the parent
# test can forcibly terminate this process (Process.Kill / taskkill /F equivalent) BEFORE it
# would have reported completion back to the DB layer.
#
# Used by StartupRecoveryServiceForcedTerminationTests (FileOrganizer.Core.Tests) to reproduce
# 仕様書§7.2-2「移動中やDB書き込み中にプロセスが強制終了しても...復旧できること」end-to-end:
# a REAL child process performs the REAL file move, is REALLY killed mid-flight (not a graceful
# exit), and StartupRecoveryService is then run against the resulting real DB + real filesystem
# state to verify it recovers correctly.
#
# Kept ASCII-only for the same reason as mock_py_service.ps1 (system-codepage script reading).
#
# Parameters:
#   -SourcePath   File to move (must already exist)
#   -DestPath     Destination path for the move
#   -MarkerPath   Created immediately AFTER the move succeeds, so the parent test can detect
#                 "the file-system side effect is done" and kill this process at that precise
#                 point -- simulating a crash between "operation completed" and
#                 "DB updated to Completed".

param(
    [Parameter(Mandatory = $true)][string]$SourcePath,
    [Parameter(Mandatory = $true)][string]$DestPath,
    [Parameter(Mandatory = $true)][string]$MarkerPath
)

$destDir = Split-Path -Parent $DestPath
if (-not (Test-Path $destDir)) {
    New-Item -ItemType Directory -Path $destDir -Force | Out-Null
}

Move-Item -Path $SourcePath -Destination $DestPath -Force

# Signal "the move is done" and then hang, awaiting a forced kill from the parent test --
# mimicking a process that dies before it can write OperationState.Completed to the DB.
New-Item -ItemType File -Path $MarkerPath -Force | Out-Null

while ($true) {
    Start-Sleep -Seconds 3600
}
