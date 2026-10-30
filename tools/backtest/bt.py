"""Bar-close backtester for the DeltaConfluence bar logs.

Reads the per-day CSVs written by the DeltaConfluence indicator (analysis/bars/*.csv), runs a few entry rules
on closed bars, and reports results per rule. Standard library only.

Rules see only the closed bar and earlier: an entry is decided on bar i's close and fills at that close plus
slippage. Exits are checked from bar i+1 on, using the bar's high and low. When the stop and the target are both
inside one bar, the stop is assumed (conservative). One position at a time; flat at the end of each session.

Usage: python bt.py [bars_folder] [--stop 25] [--target-r 1 2] [--chase 8] [--out report.md]
"""
import argparse
import csv
import glob
import os
from collections import defaultdict

POINT_VALUE = 2.0          # MNQ $ per point
COMMISSION = 1.50          # $ round trip per MNQ (estimate)
SLIPPAGE_PTS = 0.25        # per side


def load_days(folder):
    days = []
    for path in sorted(glob.glob(os.path.join(folder, "*.csv"))):
        with open(path, newline="") as f:
            rows = list(csv.DictReader(f))
        bars = []
        for r in rows:
            try:
                bars.append({
                    "date": r["date"], "time": r["time"],
                    "open": float(r["open"]), "high": float(r["high"]), "low": float(r["low"]), "close": float(r["close"]),
                    "vwap": float(r["vwap"]), "sd": float(r["sd"]), "ema": float(r["ema"]),
                    "ema_state": int(r["ema_state"]), "hl_state": int(r["hl_state"]), "div": int(r["divergence"]),
                    "price_hl": int(r.get("price_hl_state") or 0), "delta_vs_ema": int(r.get("delta_vs_ema") or 0),
                })
            except (KeyError, ValueError):
                continue
        if bars:
            days.append((os.path.basename(path), bars))
    return days


def session_of(time_pt):
    """Session by Pacific time of day (the chart's time zone)."""
    h, m = int(time_pt[:2]), int(time_pt[3:5])
    t = h * 60 + m
    if t >= 15 * 60 or t < 0:
        return "asia"            # 15:00 PT Globex open -> midnight
    if t < 6 * 60 + 30:
        return "london"          # 00:00 -> 06:30
    if t < 7 * 60 + 30:
        return "rth_open"        # 06:30 -> 07:30
    if t < 11 * 60:
        return "midday"
    return "close"               # 11:00 -> 14:00


# --- entry rules: return +1 (long), -1 (short) or 0 on closed bar i ---------------------------------------

def price_agrees(b, d):
    return b["price_hl"] == d and d * (b["close"] - b["ema"]) > 0


def delta_agrees(b, d):
    return b["hl_state"] == d and b["delta_vs_ema"] == d


def rule_agree(bars, i, chase):
    """Price and delta agree (the EmaRejectionExecution 'Required' check), on the bar agreement starts."""
    b, p = bars[i], bars[i - 1]
    for d in (1, -1):
        now = price_agrees(b, d) and delta_agrees(b, d)
        before = price_agrees(p, d) and delta_agrees(p, d)
        if now and not before and d * (b["close"] - b["ema"]) <= chase:
            return d
    return 0


def rule_price_only(bars, i, chase):
    """Same, without delta: does delta add anything over price structure alone?"""
    b, p = bars[i], bars[i - 1]
    for d in (1, -1):
        if price_agrees(b, d) and not price_agrees(p, d) and d * (b["close"] - b["ema"]) <= chase:
            return d
    return 0


def rule_divergence(bars, i, chase):
    """The cyan / magenta triangles: enter on the bar a divergence is confirmed."""
    return bars[i]["div"]


def rule_divergence_trend(bars, i, chase):
    """Divergence only when delta's EMAs agree with it."""
    d = bars[i]["div"]
    return d if d != 0 and bars[i]["ema_state"] == d else 0


def rule_agree_vwap(bars, i, chase):
    """Agreement, only on the VWAP side of the trade (longs above VWAP, shorts below)."""
    d = rule_agree(bars, i, chase)
    return d if d != 0 and d * (bars[i]["close"] - bars[i]["vwap"]) > 0 else 0


def rule_agree_any(bars, i, chase):
    """Agreement started (no chase limit): used with a limit at the EMA instead of a market entry."""
    return rule_agree(bars, i, 1e9)


RULES = {
    "agree (price + delta)": rule_agree,
    "agree + VWAP side": rule_agree_vwap,
    "price only": rule_price_only,
    "divergence": rule_divergence,
    "divergence + delta EMAs": rule_divergence_trend,
}


# --- simulation --------------------------------------------------------------------------------------------

def simulate(days, rule, stop_pts, target_r, chase, limit_at_ema=0.0, limit_bars=10):
    """limit_at_ema > 0: after a signal, a limit rests at EMA +/- limit_at_ema (long: EMA + x) for limit_bars bars."""
    trades = []
    for name, bars in days:
        pos = None
        pending = None   # (dir, bars left)
        for i in range(1, len(bars)):
            b = bars[i]
            if pos is None and pending is not None:
                d, left = pending
                px = bars[i - 1]["ema"] + d * limit_at_ema     # the limit set on the previous close
                filled = (b["low"] <= px) if d > 0 else (b["high"] >= px)
                if filled:
                    entry = min(px, b["open"]) if d > 0 else max(px, b["open"])
                    pos = {"day": name, "time": b["time"], "session": session_of(b["time"]), "dir": d, "entry": entry,
                           "stop": entry - d * stop_pts, "target": entry + d * stop_pts * target_r, "mae": 0.0, "mfe": 0.0}
                    pending = None
                    # the fill bar can also hit the stop / target: checked below as a normal bar
                else:
                    pending = (d, left - 1) if left > 1 else None
            if pos is not None:
                d = pos["dir"]
                hit_stop = (b["low"] <= pos["stop"]) if d > 0 else (b["high"] >= pos["stop"])
                hit_tgt = (b["high"] >= pos["target"]) if d > 0 else (b["low"] <= pos["target"])
                pos["mae"] = max(pos["mae"], d * (pos["entry"] - (b["low"] if d > 0 else b["high"])))
                pos["mfe"] = max(pos["mfe"], d * ((b["high"] if d > 0 else b["low"]) - pos["entry"]))
                exit_px = None
                if hit_stop:
                    exit_px, why = pos["stop"], "stop"
                elif hit_tgt:
                    exit_px, why = pos["target"], "target"
                elif i == len(bars) - 1:
                    exit_px, why = b["close"], "session end"
                if exit_px is not None:
                    pts = d * (exit_px - pos["entry"]) - SLIPPAGE_PTS
                    trades.append({**pos, "exit": exit_px, "why": why, "pts": pts,
                                   "usd": pts * POINT_VALUE - COMMISSION, "r": pts / stop_pts})
                    pos = None
                continue
            if i == len(bars) - 1:
                break
            d = rule(bars, i, chase)
            if d == 0:
                continue
            if limit_at_ema > 0:
                pending = (d, limit_bars)
                continue
            entry = b["close"] + d * SLIPPAGE_PTS
            pos = {"day": name, "time": b["time"], "session": session_of(b["time"]), "dir": d, "entry": entry,
                   "stop": entry - d * stop_pts, "target": entry + d * stop_pts * target_r, "mae": 0.0, "mfe": 0.0}
    return trades


def stats(trades):
    n = len(trades)
    if n == 0:
        return None
    wins = [t for t in trades if t["pts"] > 0]
    losses = [t for t in trades if t["pts"] <= 0]
    gross_win = sum(t["usd"] for t in wins)
    gross_loss = -sum(t["usd"] for t in losses)
    equity = peak = dd = 0.0
    for t in trades:
        equity += t["usd"]
        peak = max(peak, equity)
        dd = max(dd, peak - equity)
    usd = sorted(t["usd"] for t in trades)
    trimmed = usd[: max(1, int(n * 0.95))]   # drop the top 5% of trades: is one big winner carrying it?
    return {
        "n": n, "win": len(wins) / n, "exp_usd": sum(usd) / n, "exp_r": sum(t["r"] for t in trades) / n,
        "total": sum(usd), "pf": gross_win / gross_loss if gross_loss > 0 else float("inf"), "maxdd": dd,
        "trim_exp": sum(trimmed) / len(trimmed),
        "mae": sum(t["mae"] for t in trades) / n, "mfe": sum(t["mfe"] for t in trades) / n,
    }


def fmt_row(label, s):
    if s is None:
        return "| %s | 0 | | | | | | | |" % label
    return "| %s | %d | %.0f%% | %+.2f | $%+.2f | $%+.0f | %.2f | $%.0f | $%+.2f |" % (
        label, s["n"], s["win"] * 100, s["exp_r"], s["exp_usd"], s["total"], s["pf"], s["maxdd"], s["trim_exp"])


HEADER = ("| Rule | Trades | Win | Exp R | Exp $/trade | Total $ | PF | Max DD | Exp $ w/o top 5% |\n"
          "|---|---|---|---|---|---|---|---|---|")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("folder", nargs="?", default=os.path.join(os.path.expanduser("~"), "OneDrive", "Documents",
                                                             "NinjaTrader 8", "analysis", "bars"))
    ap.add_argument("--stop", type=float, default=25.0)
    ap.add_argument("--target-r", type=float, nargs="+", default=[1.0, 2.0])
    ap.add_argument("--chase", type=float, default=8.0)
    ap.add_argument("--out", default="")
    a = ap.parse_args()

    days = load_days(a.folder)
    nbars = sum(len(b) for _, b in days)
    out = ["# Backtest: DeltaConfluence bar logs", "",
           "%d days (%s to %s), %d bars. 1 MNQ, stop %.0f pts, slippage %.2f pts per side, $%.2f commission. "
           "Entry on a closed bar at its close; stop first when both are hit in one bar." % (
               len(days), days[0][0] if days else "-", days[-1][0] if days else "-", nbars, a.stop, SLIPPAGE_PTS,
               COMMISSION), ""]
    for tr in a.target_r:
        out += ["## Target %.1fR (%.0f pts)" % (tr, a.stop * tr), "", HEADER]
        by_session = {}
        for label, rule in RULES.items():
            trades = simulate(days, rule, a.stop, tr, a.chase)
            out.append(fmt_row(label, stats(trades)))
            if rule is rule_agree:
                lt = simulate(days, rule_agree_any, a.stop, tr, a.chase, limit_at_ema=2.0)
                out.append(fmt_row("agree, then limit at EMA+2 (10 bars)", stats(lt)))
                g = defaultdict(list)
                for t2 in lt:
                    g[t2["session"]].append(t2)
                by_session["agree, limit at EMA"] = g
            groups = defaultdict(list)
            for t in trades:
                groups[t["session"]].append(t)
            by_session[label] = groups
        out += ["", "By session (exp $/trade, trades):", "",
                "| Rule | " + " | ".join(["asia", "london", "rth_open", "midday", "close"]) + " |", "|---|---|---|---|---|---|"]
        for label, groups in by_session.items():
            cells = []
            for sname in ["asia", "london", "rth_open", "midday", "close"]:
                s = stats(groups.get(sname, []))
                cells.append("$%+.1f (%d)" % (s["exp_usd"], s["n"]) if s else "-")
            out.append("| %s | %s |" % (label, " | ".join(cells)))
        out.append("")
    out += ["Fewer than ~100 trades per rule is a hint, not a result. Same data for every rule; nothing tuned."]
    text = "\n".join(out)
    print(text)
    if a.out:
        with open(a.out, "w", encoding="utf-8") as f:
            f.write(text + "\n")


if __name__ == "__main__":
    main()
