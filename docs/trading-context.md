# Trading context

Shared facts for the agents in `.claude/agents/`. Change a fact here once instead of in every prompt. This file is public: no account numbers, logins, keys or personal finances.

## Instrument and strategy
- Strategy: `Strategies/EmaRejectionExecution.cs` (NinjaTrader 8, C#). EMA 55 rejection / bounce entries on NQ, usually executed on MNQ.
- NQ $20 / pt, MNQ $2 / pt, tick 0.25. Default stop 25 pts = $500 per NQ, $50 per MNQ.
- Code makes every live decision. Agents work offline: research, sizing, session prep, review.

## Files
Paths are relative to the repo root, `NinjaTrader 8\bin\Custom` (Documents may sit under OneDrive).

| File | What | Written by |
|---|---|---|
| `..\..\log\EmaRejection.yyyyMMdd.txt` | strategy events: orders, fills, drags, chop, auto on/off | the strategy |
| `..\..\log\log.yyyyMMdd.*.txt` | NinjaTrader order states and fill prices | NinjaTrader |
| `..\..\analysis\trades.csv` | one row per live / Sim / Playback trade: size, entry, stop and TP (initial and final), exit, P&L, R, MAE/MFE, trend, chop | the strategy |
| `..\..\analysis\levels.txt` | current prior-session, overnight and session levels, VWAP, EMA, trend, chop (about once a minute) | the strategy |
| `..\..\analysis\*.csv` (other) | Strategy Analyzer trade exports (Trades tab → right-click → Export, CSV) | the user |
| `..\..\analysis\playbook.md` | adopted playbook cards | strategy-analyst |
| `..\..\analysis\sizing.md` | approved size per account and grade | from risk-manager's verdict |
| `..\..\analysis\lessons.md` | one-line lessons, append-only | strategy-analyst, market-regime |

`..\..\analysis\` is outside the repo, so trade and account data never get committed. Agents that may write, write only there or in the session scratchpad. Python is available (`python`, stdlib only).

## Rules for every agent
- **Cite every number:** file and row/line, the script that computed it, a chart value the user gave, or a URL. Never estimate a figure you could compute. If sources conflict, flag it.
- **No look-ahead:** use only data known at the time: completed bars, the last closed higher-timeframe bar, pre-open data for a session label. Lessons count only from outcomes known before the period being tested.
- **Read the last ~10 lines of `lessons.md`** before starting.
- **Lesson format:** `[yyyy-MM-dd | playbook | regime | result] one concrete lesson for next time`. Never rewrite earlier lines; say when the sample is too small to conclude anything.

## Every trade records
Size (contracts), entry, stop loss, take profit, grade, exit price, result in R. A trade missing any of these is flagged; one that can't be reconstructed is excluded and counted.

## Grades and size
| Grade | Conditions (hypothesis, the data confirms or changes it) | Starting size, MNQ (scale per account) |
|---|---|---|
| A+ | planned reward ≥ 3R, with higher-timeframe trend, not in chop, at a key level (PDH/PDL, VAH/VAL, VWAP) | max: 3–4 |
| A | planned reward ≥ 2R, with trend | average: 2–3 |
| B | planned reward ≥ 1R, everything else that passes the filters | base: 1–2 |

- A grade is real only if its expectancy clearly beats the grade below, out of sample. Until then it trades at B size. If A+ doesn't beat A, merge them.
- One full stop (incl. commission and 1 tick slippage per side) ≤ ~10% of the **remaining** drawdown (the full drawdown at the start). Example: $1,000 drawdown → A+ is 1–2 MNQ (2 MNQ ≈ $105 with costs). As room shrinks, size shrinks.
- No entry in `sizing.md` for an account: B size only.

## Accounts
- Prop firms: Tradeify and Lucid. Drawdown $1,000–2,000 depending on the account. Some plans trail intraday, others end-of-day; check per account and note where the floor stops trailing (often at the starting balance).
- With intraday trailing, the floor follows the highest **open** equity. A trade that runs +30 pts then stops at −25 costs 55 pts of room.
- The table below holds the user's rules. Firm rules change: ask the user to confirm against the firm's current rulebook before relying on one.
- The eval budget is limited: avoid buying evals until forward tests support the strategy.
- TradeSpacePro enforces account rules outside NinjaTrader and stays as an independent safety net. The strategy's own limits should trip before it does.

| Rule | Tradeify | Lucid | TradeSpacePro |
|---|---|---|---|
| Plan / size | | | |
| Max drawdown | | | |
| Trailing: intraday or EOD; stops trailing at | | | |
| Daily loss limit | | | |
| Max contracts | | | |
| Consistency rule | | | |
| Trading hours / news rules | | | |
| Max trades per day | | | |

## Who does what
- **strategy-analyst:** is the edge real? Builds and grades playbook cards.
- **risk-manager:** how much can the account afford? Sizes per grade, daily limits, eval odds. Has veto.
- **market-regime:** which playbook fits today? Pre-session brief, post-session review.
- **code-reviewer / debugger:** code correctness.
- Agents can't call each other. They hand off through the files above.
