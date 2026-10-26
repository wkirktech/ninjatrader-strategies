# Runs one scheduled agent job headless and saves its output to analysis\briefs\.
# Usage: run-agent.ps1 -Job premarket | review | weekly
# Least privilege: no job gets Write, Edit or Bash. The script feeds local files in and appends the lesson itself.
param(
    [Parameter(Mandatory)][ValidateSet('premarket', 'review', 'weekly')][string]$Job
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new()        # claude.exe writes UTF-8; a hidden pwsh defaults to OEM
$repo     = Split-Path -Parent $PSScriptRoot                  # NinjaTrader 8\bin\Custom
$analysis = [IO.Path]::GetFullPath((Join-Path $repo '..\..\analysis'))   # outside the repo
$briefs   = Join-Path $analysis 'briefs'
$claude   = Join-Path $env:USERPROFILE '.local\bin\claude.exe'
New-Item -ItemType Directory -Force $briefs | Out-Null

$today = Get-Date -Format 'yyyy-MM-dd'
$log   = "..\..\log\EmaRejection.$(Get-Date -Format 'yyyyMMdd').txt"

switch ($Job) {
    'premarket' {
        # Web only: levels are passed in, so the agent never reads local files while it browses
        $levels = Join-Path $analysis 'levels.txt'
        $levelsText = if (Test-Path $levels) { Get-Content $levels -Raw } else { '(none: the strategy has not written levels.txt)' }
        $files = foreach ($f in @((Join-Path $repo 'docs\trading-context.md'), (Join-Path $analysis 'playbook.md'), (Join-Path $analysis 'sizing.md'))) {
            if (Test-Path $f) { "=== $(Split-Path -Leaf $f)`n$(Get-Content $f -Raw)" } else { "=== $(Split-Path -Leaf $f): missing" }
        }
        $agent  = 'market-regime'
        $tools  = 'WebSearch', 'WebFetch'
        $deny   = 'Read', 'Grep', 'Glob', 'Write', 'Edit', 'NotebookEdit', 'Bash', 'PowerShell', 'Agent'
        $prompt = "Pre-session brief for today's NQ session, $today. Follow your instructions. You have no file access; " +
                  "these are the strategy's current levels (analysis\levels.txt), cite them as that file:`n$levelsText`n" +
                  "Your shared context, playbook and sizing files follow (instead of reading them):`n$($files -join "`n")`nReply with the brief only."
    }
    'review' {
        $agent  = 'market-regime'
        $tools  = 'Read', 'Grep', 'Glob'
        $deny   = 'Write', 'Edit', 'NotebookEdit', 'Bash', 'PowerShell', 'WebSearch', 'WebFetch', 'Agent'
        $prompt = "Post-session review for $today. Follow your instructions, but you cannot write files: read $log and today's rows in " +
                  "..\..\analysis\trades.csv if it exists. Reply with the review only. If there were trades, end with exactly one line " +
                  "'LESSON: [yyyy-MM-dd | playbook | regime | result] lesson' and it will be appended to lessons.md for you; if not, no LESSON line."
    }
    'weekly' {
        $agent  = 'strategy-analyst'
        $tools  = 'Read', 'Grep', 'Glob'
        $deny   = 'Write', 'Edit', 'NotebookEdit', 'Bash', 'PowerShell', 'WebSearch', 'WebFetch', 'Agent'
        $prompt = "Weekly roll-up for the week ending $today. Read the last 7 days of ..\..\analysis\lessons.md, ..\..\analysis\briefs\ " +
                  "and ..\..\analysis\trades.csv. You cannot run code or write files. Summarise what worked and what didn't, and propose " +
                  "at most ONE change with its evidence, or none. Reply with the roll-up only."
    }
}

$out = Join-Path $briefs "$today-$Job.md"
Push-Location $repo
try {
    $reply = & $claude -p $prompt --agent $agent --allowedTools ($tools -join ',') --disallowedTools ($deny -join ',') --add-dir $analysis 2>&1 | Out-String
}
finally {
    Pop-Location
}
Set-Content -Path $out -Value $reply -Encoding utf8

# The review's lesson goes to lessons.md here, append-only
if ($Job -eq 'review') {
    # Tolerates markdown around the label (**LESSON:**, - LESSON:, `LESSON: ...`)
    $match = ($reply -split "`r?`n") | Select-String -Pattern '^[\s>*`-]*LESSON:[*`]*\s*(\[.+?)`?$' | Select-Object -Last 1
    if ($match) {
        Add-Content -Path (Join-Path $analysis 'lessons.md') -Value $match.Matches[0].Groups[1].Value -Encoding utf8
    }
    elseif ($reply -match 'LESSON') {
        Add-Content -Path $out -Value "`n(run-agent: a LESSON line was found but not in the expected format, so nothing was added to lessons.md)" -Encoding utf8
    }
}
