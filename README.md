# NinjaTrader Strategies

Automated trading strategies for [NinjaTrader 8](https://ninjatrader.com/), written in C# (NinjaScript).

| Strategy | What it does |
|---|---|
| [EmaRejectionExecution](Strategies/EmaRejectionExecution.cs) | Detects price rejecting (or bouncing off) an EMA with lower-high / higher-low structure, then works a limit order that rides the EMA, with an on-chart dashboard and an AUTO on/off switch. |

> **Disclaimer:** this is personal project code shared for educational and portfolio purposes. It is not financial advice. Futures trading carries substantial risk of loss. Test in a simulation account first.

---

## EmaRejectionExecution

Built for NQ / MNQ index futures on fast execution charts (377-tick, 1-min, 3-min). It reads the signal from one instrument and can execute on another, e.g. NQ data with orders on the MNQ micro contract.

### Setup detection

**Short (rejection):**
1. **Test:** a bar's high reaches the EMA. It is still a test when:
   - the wick goes through the EMA by up to *Max wick pierce*, or
   - the bar closes over the EMA by up to *Close-over tolerance*.
2. **Rejection:** within a few bars, a bar closes back under the EMA by at least *Reject close*.
3. **Setup:** *Required rejections* (default 2) in a row, each with a **lower high** than the last.
4. **Invalidation:** any of these cancels the setup:
   - a close over the EMA beyond the tolerance
   - a wick through it beyond the max pierce
   - too many closes in a row over the EMA
   - the rejections getting older than the lookback

**Long (bounce)** is the mirror image, with bounces making **higher lows**.

**Trend pullback** (*Setup mode*: Rejection / Trend pullback / Either, default Either). Long side; shorts mirror it:
1. **Trend on:** a bar closes back above the EMA and VWAP after being below (the reclaim). The low of that move is the floor.
2. **Entry:** from the **first** pullback into the EMA. *On reclaim* (default): at market on the close of the bar that bounces back over the EMA, unless it closed more than *Reclaim max chase* (8 pts) away. *Limit at EMA*: a buy limit resting at EMA + *Pullback offset*.
3. **Higher lows:** when a pullback leaves the EMA zone, its low becomes the new floor. The next entry needs the next pullback, up to *max entries per leg* (default 2).
4. **Break and reclaim allowed:** a pullback may close through the EMA by up to 5 pts, up to 2 closes in a row.
5. **Trend off:** a deeper close through the EMA or VWAP, 3 closes through the EMA, a lower low, or a stop-out in that leg.

The chart marks a reclaim with a gold **T** and each higher low with a gold triangle. trades.csv tags every trade `rejection`, `trend` or `forced` (column *setup*), so the modes can be compared.

### Order management

| | Behaviour |
|---|---|
| **Entry** | Limit at EMA − offset (short) / EMA + offset (long). It is moved with the EMA every bar, and only placed while it can rest a minimum distance away from the market, so it never fills instantly. |
| **Stop** | Fixed points from the fill (default 25). You can drag it tighter; if you drag it further away, it snaps back. |
| **Target** | The setup's lower low (short) / higher high (long) plus a buffer, never closer than a minimum R-multiple (default 1:1). It extends with the leg until you drag it; wherever you drop it, it stays. |
| **Split targets** | Off by default. With 2+ contracts: one entry. When price reaches TP1 (default 1R from the fill, gold dashed line) the strategy sells / buys half at market; the runner keeps the leg target and the stop, which moves to breakeven after TP1 (optional). TP1 is a setting, not a draggable order. Skipped when the target is within 5 pts of TP1 (setting). |
| **Profit lock** | Off by default. When on, at +30 pts the stop moves to +12 pts (both configurable). |
| **Automation** | Stays armed and takes every new setup until a full stop-loss hit (configurable), or until you switch it off. |
| **Day limits** | Optional daily stop ($), max losing trades and max trades. A trade is only taken if its full stop keeps the day inside the daily stop; once a limit is hit, AUTO turns off for the day. |

### Dashboard

![EmaRejectionExecution dashboard on an NQ chart executing on MNQ](docs/images/dashboard.png)

A panel on the chart shows:
- the EMA value and slope
- the rejection / bounce count on each side, with its structure
- the planned entry, stop, target and R:R
- the order status and the last event

The buttons are:
- **AUTO ON/OFF**
- **Direction** (Both / Long / Short)
- **FLAT+OFF**: cancels the order, closes the position and turns AUTO off
- **BUY @EMA / SELL @EMA**: forces a setup on that side

Every event (orders, fills, drags, chop, auto on/off) is also written to `Documents\NinjaTrader 8\log\EmaRejection.yyyyMMdd.txt`, so a session can be reviewed after a restart. Each closed trade is added to `Documents\NinjaTrader 8\analysis\trades.csv` (size, entry, stop, target, exit, P&L, R, MAE/MFE), and `analysis\levels.txt` holds the current levels (prior session, overnight, VWAP, trend, chop) for the agents.

### Design notes

- **No repainting.** Setup logic runs only on closed bars. The bar index is offset correctly between historical and realtime processing, so backtest and live behaviour match.
- **Multi-instrument execution.** Orders are routed to a secondary data series. *Execute on micro* maps NQ→MNQ, ES→MES and so on, keeping the contract month.
- **Managed orders.** Per-entry stops and targets. The live target is updated with `SetProfitTarget`, and a manual change to it is detected in `OnOrderUpdate`.
- **Thread-safe UI.** The WPF controls are updated through the chart dispatcher. Button clicks are routed into the strategy's own thread with `TriggerCustomEvent`.
- **Safe defaults.** It starts disarmed, only trades on realtime data when on a chart, and is always armed in the Strategy Analyzer so it can be backtested.

### Install

1. Copy `Strategies/EmaRejectionExecution.cs` to `Documents\NinjaTrader 8\bin\Custom\Strategies\`.
2. In NinjaTrader, open **New → NinjaScript Editor** and press **F5** to compile.
3. Add it to a chart: **right-click → Strategies → EmaRejectionExecution**. Use a **Sim101** account first.

### Testing approach

1. **Watch mode** (AUTO off): check that the setup markers match a manual read of the chart.
2. **Market Replay (Playback):** check order placement, EMA tracking, cancellation, the profit lock and auto-disarm.
3. **Strategy Analyzer:** backtest on tick data with high fill resolution.
4. **Live Sim101** before any funded account.

---

## Agents

`.claude/agents/` holds Claude Code agents for this repo: code-reviewer, debugger, strategy-analyst, risk-manager and market-regime. They share the facts in [docs/trading-context.md](docs/trading-context.md). Code makes every live trading decision; the agents work offline.

`tools/run-agent.ps1` runs a scheduled job headless and saves the result to `Documents\NinjaTrader 8\analysis\briefs\`:
- **premarket** (weekdays): levels, news and regime brief
- **review** (weekdays, after the close): session review and one lesson
- **weekly** (Saturday): roll-up with at most one proposed change

Register them in Windows Task Scheduler with `tools/register-agent-tasks.ps1` (`-Remove` to delete). They run only while you are logged on, with read-only tools: no job can write, edit or run code. The script passes files in and appends the review's lesson itself.

## Development workflow

- `main`: stable, tested releases (tagged, e.g. `v1.0.0`).
- `develop`: where finished features are combined and tested.
- `feature/<name>`: one branch per change, merged into `develop` through a reviewed pull request.

Both `main` and `develop` are protected, so changes land only through pull requests.

## License

[MIT](LICENSE)
