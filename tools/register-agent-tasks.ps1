# Registers the scheduled agent jobs in Windows Task Scheduler (current user, runs only while logged on).
# Times are local; the defaults assume Pacific time (ET - 3h). Re-run to update, or -Remove to delete.
param(
    [string]$PremarketAt = '05:00',   # 08:00 ET
    [string]$ReviewAt    = '13:30',   # 16:30 ET
    [string]$WeeklyAt    = '09:00',   # Saturday
    [switch]$Remove
)

$ErrorActionPreference = 'Stop'
$script = Join-Path $PSScriptRoot 'run-agent.ps1'
$folder = '\EmaRejection\'
$days   = 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday'

$jobs = @(
    @{ Name = 'Premarket brief'; Job = 'premarket'; Trigger = New-ScheduledTaskTrigger -Weekly -DaysOfWeek $days -At $PremarketAt },
    @{ Name = 'Session review';  Job = 'review';    Trigger = New-ScheduledTaskTrigger -Weekly -DaysOfWeek $days -At $ReviewAt },
    @{ Name = 'Weekly roll-up';  Job = 'weekly';    Trigger = New-ScheduledTaskTrigger -Weekly -DaysOfWeek Saturday -At $WeeklyAt }
)

foreach ($j in $jobs) {
    if ($Remove) {
        Unregister-ScheduledTask -TaskPath $folder -TaskName $j.Name -Confirm:$false -ErrorAction SilentlyContinue
        continue
    }
    $action   = New-ScheduledTaskAction -Execute 'pwsh.exe' -Argument "-NoProfile -WindowStyle Hidden -File `"$script`" -Job $($j.Job)"
    $settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Minutes 30)
    Register-ScheduledTask -TaskPath $folder -TaskName $j.Name -Action $action -Trigger $j.Trigger -Settings $settings -Force | Out-Null
    Write-Output "Registered $folder$($j.Name)"
}
