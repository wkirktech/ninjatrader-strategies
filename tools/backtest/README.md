# Bar-close backtester

Tests entry rules on the bar logs written by the [DeltaConfluence](../../Indicators/DeltaConfluence.cs) indicator (*Log bars to CSV*), without NinjaTrader. Python 3, standard library only.

```
python tools/backtest/bt.py "%USERPROFILE%\OneDrive\Documents\NinjaTrader 8\analysis\bars" --stop 25 --target-r 1 2 --out report.md
```

- **Rules:**
  - price + delta agreement (market at the close, or a limit at the EMA afterwards)
  - the same on the VWAP side only
  - price structure only
  - divergence, and divergence with the delta EMAs
- **Fills:**
  - A rule only sees closed bars. An entry fills at the close plus 1 tick, or at the limit.
  - Exits use the next bars' high and low. When the stop and the target are both inside one bar, the stop wins.
  - 1 MNQ, $1.50 commission. Flat at session end.
- **Report:** trades, win rate, expectancy (R and $), profit factor, max drawdown, the result without the top 5% of trades, and a split by session (Pacific time).

It checks entry ideas quickly. It doesn't replace the Strategy Analyzer: there's no tick-level fill order inside a bar, and no strategy state.
