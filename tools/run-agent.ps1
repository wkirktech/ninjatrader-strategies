# Runs one scheduled agent job headless and saves its output to analysis\briefs\.
# Usage: run-agent.ps1 -Job premarket | review | weekly
param(
    [Parameter(Mandatory)][ValidateSet('premarket', 'review', 'weekly')][string]$Job
)

$ErrorActionPreference = 'Stop'
$repo     = Split-Path -Parent $PSScriptRoot                  # NinjaTrader 8\bin\Custom
$analysis = Join-Path $repo '..\..\analysis' | Resolve-Path   # outside the repo
$briefs   = Join-Path $analysis 'briefs'
$claude   = Join-Path $env:USERPROFILE '.local\bin\claude.exe'
New-Item -ItemType Directory -Force $briefs | Out-Null

$today = Get-Date -Format 'yyyy-MM-dd'
$log   = "..\..\log\EmaRejection.$(Get-Date -Format 'yyyyMMdd').txt"

switch ($Job) {
    'premarket' {
        $agent  = 'market-regime'
        $tools  = 'Read', 'Grep', 'Glob', 'WebSearch', 'WebFetch'
        $prompt = "Pre-session brief for today's NQ session, $today. Follow your instructions. " +
                  "Levels are in ..\..\analysis\levels.txt (written by the strategy while the chart runs) and the Session start lines in ..\..\log\EmaRejection.*.txt; if neither has them, mark levels unknown. " +
                  "Your reply is saved as the brief, so reply with the brief only."
    }
    'review' {
        $agent  = 'market-regime'
        $tools  = 'Read', 'Grep', 'Glob', 'Write', 'Bash(python *)'
        $prompt = "Post-session review for $today. Follow your instructions. Read $log and today's rows in ..\..\analysis\trades.csv if it exists. " +
                  "Append one lesson to ..\..\analysis\lessons.md. If there were no trades, say so and append nothing. " +
                  "Your reply is saved as the review, so reply with the review only."
    }
    'weekly' {
        $agent  = 'strategy-analyst'
        $tools  = 'Read', 'Grep', 'Glob', 'Bash(python *)'
        $prompt = "Weekly roll-up for the week ending $today. Read the last 7 days of ..\..\analysis\lessons.md, briefs\ and trades.csv. " +
                  "Summarise what worked and what didn't, and propose at most ONE change with its evidence, or none. " +
                  "Do not edit any file. Your reply is saved as the roll-up."
    }
}

$out = Join-Path $briefs "$today-$Job.md"
Push-Location $repo
try {
    & $claude -p $prompt --agent $agent --allowedTools ($tools -join ',') --add-dir $analysis 2>&1 |
        Out-File -FilePath $out -Encoding utf8
}
finally {
    Pop-Location
}
