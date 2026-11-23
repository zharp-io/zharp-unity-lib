# Waits (up to 60 s) for the Unity crash handlers started from a player's folder to exit: the
# handler is a process of its own and may still be writing the crash folder after the game died.
# Usage: wait-crash-handler.ps1 <player folder>
param([string]$Folder)
$full = [System.IO.Path]::GetFullPath($Folder).TrimEnd('\') + '\'
for ($i = 0; $i -lt 60; $i++) {
    $running = Get-CimInstance Win32_Process -Filter "Name='UnityCrashHandler64.exe'" |
        Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($full, [System.StringComparison]::OrdinalIgnoreCase) }
    if (-not $running) { exit 0 }
    Start-Sleep -Seconds 1
}
Write-Output "the crash handler still runs after 60 s"
