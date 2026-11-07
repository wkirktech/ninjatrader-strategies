"""Morning context sheet for NQ: overnight / IB levels plus the market profile stats that held up on 2021-2026 data.

Reads the BarLogger export (analysis/history/NQ_1Minute.csv, bar close times in US Eastern) and writes
analysis/briefs/<date>-context.md. Before 10:31 ET it fills the pre-open part; after, it adds the IB part.
Plain Python, no network, no Claude call. Scheduled by register-agent-tasks.ps1 (6:25 and 7:31 PT on weekdays).
Usage: python context_sheet.py [--csv PATH] [--out DIR]
"""
import argparse, csv, io, os, sys
from collections import defaultdict
from datetime import datetime, timedelta
from zoneinfo import ZoneInfo

ANALYSIS = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "..", "analysis"))
TAIL_BYTES = 4_000_000          # ~5 sessions of 1-min bars, enough for the prior day after a long weekend
STALE_MIN = 15                  # newest bar older than this (during the session) = data not live

# Hit rates measured on NQ Dec 2021 - Nov 2026 (analysis/research/2026-11-06_mp_stats_report.pdf,
# 2026-11-06_ib25_step1.md). (all years, 2026 so far)
STAT_ON_MID = (79, 73)          # open between ON mid and ON high -> ON high trades in RTH (mirror low)
STAT_ANY_ON = (96, 92)          # RTH trades at least one ON extreme
STAT_IB_HI_LAST = (82, 82)      # IB high formed after the IB low -> IB high breaks later
STAT_IB_LO_LAST = (73, 63)
STAT_IB_ZONE = (90, 86)         # 10:30 close in the top (bottom) 25% of the IB -> IB high (low) breaks later
BASE_IB_HI, BASE_IB_LO = 61, 56 # any day, no condition


def read_tail(path):
    size = os.path.getsize(path)
    with open(path, "rb") as f:
        f.seek(max(0, size - TAIL_BYTES))
        data = f.read().decode("utf-8", "replace")
    lines = data.splitlines()[1:]     # first line is partial (or the header)
    bars = []
    for row in csv.reader(io.StringIO("\n".join(lines))):
        if len(row) != 7 or not row[1]:
            continue
        try:
            bars.append((row[0], row[1], float(row[2]), float(row[3]), float(row[4]), float(row[5]), float(row[6])))
        except ValueError:
            continue
    return bars


def value_area(bars):
    vol = defaultdict(float)
    for b in bars:
        lo, hi = int(b[4]), int(b[3])
        for p in range(lo, hi + 1):
            vol[p] += b[6] / (hi - lo + 1)
    prices = sorted(vol)
    total = sum(vol.values())
    i = j = poc = max(range(len(prices)), key=lambda k: vol[prices[k]])
    got = vol[prices[i]]
    while got < 0.7 * total:
        up = vol[prices[j + 1]] if j + 1 < len(prices) else -1
        dn = vol[prices[i - 1]] if i > 0 else -1
        if up >= dn:
            j += 1; got += up
        else:
            i -= 1; got += dn
    return prices[j] + 1, prices[poc], prices[i]


def rth(bars, day):
    return [b for b in bars if b[0] == day and "09:31:00" <= b[1] <= "16:00:00"]


def fmt(x):
    return f"{x:,.2f}"


def build(bars, now_et):
    days = sorted({b[0] for b in bars})
    today = now_et.strftime("%Y-%m-%d")
    prior = [d for d in days if d < today and rth(bars, d)]
    if not prior:
        return None, "No prior session in the export."
    pday = prior[-1]
    pb = rth(bars, pday)
    ph, pl, pc = max(b[3] for b in pb), min(b[4] for b in pb), pb[-1][5]
    vah, poc, val = value_area(pb)
    on = [b for b in bars if (b[0], b[1]) > (pday, "18:00:00") and (b[0], b[1]) <= (today, "09:30:00")]
    last = bars[-1]
    out, warn = [], []

    last_dt = datetime.strptime(f"{last[0]} {last[1]}", "%Y-%m-%d %H:%M:%S")
    age = (now_et.replace(tzinfo=None) - last_dt).total_seconds() / 60
    if age > STALE_MIN:
        warn.append(f"DATA NOT LIVE: newest bar is {last[0]} {last[1]} ET ({age:.0f} min old). Is NinjaTrader open with "
                    "BarLogger on the 1-min NQ chart? Levels below may be incomplete.")
    if not on:
        return None, "No overnight bars for today in the export yet."

    onh, onl = max(b[3] for b in on), min(b[4] for b in on)
    onm = (onh + onl) / 2
    today_rth = rth(bars, today)
    started = bool(today_rth)
    ref = today_rth[0][2] if started else last[5]
    ref_txt = "6:30 PT open" if started else "last price (before the open; recheck at 6:30)"

    lean = []
    out.append("## Pre-open")
    out.append(f"Prior session {pday}:  H {fmt(ph)}   L {fmt(pl)}   C {fmt(pc)}")
    out.append(f"Prior value area:       VAH {fmt(vah)}   POC {fmt(poc)}   VAL {fmt(val)}")
    out.append(f"Overnight:              H {fmt(onh)}   L {fmt(onl)}   mid {fmt(onm)}")
    out.append(f"Reference:              {fmt(ref)}  ({ref_txt})")
    out.append("")
    if onm < ref < onh:
        lean.append(1)
        out.append(f"- Above the ON mid -> ON HIGH {fmt(onh)} is the magnet: it trades on {STAT_ON_MID[0]}% of days "
                   f"({STAT_ON_MID[1]}% in 2026). Target for longs; poor place to short into before it trades.")
    elif onl < ref < onm:
        lean.append(-1)
        out.append(f"- Below the ON mid -> ON LOW {fmt(onl)} is the magnet: it trades on {STAT_ON_MID[0]}% of days "
                   f"({STAT_ON_MID[1]}% in 2026). Target for shorts; poor place to buy into before it trades.")
    elif ref >= onh:
        out.append("- Already above the overnight high: the ON-mid stat does not apply. Watch ON high as support.")
    elif ref <= onl:
        out.append("- Already below the overnight low: the ON-mid stat does not apply. Watch ON low as resistance.")
    else:
        out.append("- Right on the ON mid: no lean from the overnight range.")
    out.append(f"- At least one ON extreme trades in RTH on {STAT_ANY_ON[0]}% of days ({STAT_ANY_ON[1]}% in 2026).")
    out.append("- These moves often go against you first (median drawdown about the size of the distance to the level),"
               " so use them as targets and bias, not as a reason to enter at the open.")

    ib = [b for b in today_rth if b[1] <= "10:30:00"]
    if ib and ib[-1][1] == "10:30:00" and len(ib) >= 58:
        hi, lo = max(b[3] for b in ib), min(b[4] for b in ib)
        rng = hi - lo
        hi_i = max(i for i, b in enumerate(ib) if b[3] == hi)
        lo_i = max(i for i, b in enumerate(ib) if b[4] == lo)
        close = ib[-1][5]
        pos = (close - lo) / rng if rng > 0 else 0.5
        out.append("")
        out.append("## Initial balance (6:30-7:30 PT)")
        out.append(f"IB high {fmt(hi)}   IB low {fmt(lo)}   range {rng:.2f} pts   7:30 close {fmt(close)} ({pos:.0%} of the IB)")
        out.append(f"Extensions: high +0.5 IB {fmt(hi + 0.5 * rng)}, +1 IB {fmt(hi + rng)} | low -0.5 IB {fmt(lo - 0.5 * rng)}, -1 IB {fmt(lo - rng)}")
        out.append("")
        if hi_i > lo_i:
            lean.append(1)
            out.append(f"- IB HIGH formed last -> IB high breaks later on {STAT_IB_HI_LAST[0]}% of days "
                       f"({STAT_IB_HI_LAST[1]}% in 2026; any day {BASE_IB_HI}%).")
        else:
            lean.append(-1)
            out.append(f"- IB LOW formed last -> IB low breaks later on {STAT_IB_LO_LAST[0]}% of days "
                       f"({STAT_IB_LO_LAST[1]}% in 2026; any day {BASE_IB_LO}%).")
        if pos >= 0.75:
            lean.append(1)
            out.append(f"- 7:30 close in the TOP 25% -> IB high breaks on {STAT_IB_ZONE[0]}% of days ({STAT_IB_ZONE[1]}% in 2026).")
        elif pos <= 0.25:
            lean.append(-1)
            out.append(f"- 7:30 close in the BOTTOM 25% -> IB low breaks on {STAT_IB_ZONE[0]}% of days ({STAT_IB_ZONE[1]}% in 2026).")
        else:
            out.append("- 7:30 close in the middle half: no IB-zone signal. Touching the zone later does NOT carry the 90% "
                       "(later touches broke 75% and lost money).")
    elif started:
        out.append("")
        out.append("## Initial balance")
        out.append("IB not complete yet (rerun after 7:30 PT).")

    s = sum(lean)
    if lean and all(x == lean[0] for x in lean):
        verdict = f"LEAN {'LONG' if lean[0] == 1 else 'SHORT'} ({len(lean)} of {len(lean)} signals agree)"
    elif s:
        verdict = f"WEAK {'LONG' if s > 0 else 'SHORT'} lean ({len(lean)} signals, mixed)"
    else:
        verdict = "NO LEAN (signals conflict or none apply)"
    head = [f"# NQ context {today}  (updated {now_et:%H:%M} ET / {(now_et - timedelta(hours=3)):%H:%M} PT)", "",
            f"**{verdict}**", ""]
    tail = ["", "---",
            "Context only. Tested as stand-alone trades on 2021-2026 data, none of these beat costs: they tell you",
            "where price likely goes, not where to get in. Levels from BarLogger's NQ 1-min export."]
    return "\n".join(head + [f"> {w}" for w in warn] + ([""] if warn else []) + out + tail), None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--csv", default=os.path.join(ANALYSIS, "history", "NQ_1Minute.csv"))
    ap.add_argument("--out", default=os.path.join(ANALYSIS, "briefs"))
    ap.add_argument("--at", help="replay as of 'YYYY-MM-DD HH:MM' ET (testing); bars after it are ignored")
    args = ap.parse_args()
    now_et = datetime.now(ZoneInfo("America/New_York"))
    if args.at:
        now_et = datetime.strptime(args.at, "%Y-%m-%d %H:%M").replace(tzinfo=ZoneInfo("America/New_York"))
    if not os.path.exists(args.csv):
        sys.exit(f"Missing {args.csv}: add BarLogger to the 1-min NQ chart.")
    bars = read_tail(args.csv)
    if args.at:
        cut = now_et.strftime("%Y-%m-%d %H:%M:%S")
        bars = [b for b in bars if f"{b[0]} {b[1]}" <= cut]
    text, err = build(bars, now_et)
    if err:
        text = f"# NQ context {now_et:%Y-%m-%d}\n\n> {err}\n"
    os.makedirs(args.out, exist_ok=True)
    path = os.path.join(args.out, f"{now_et:%Y-%m-%d}-context.md")
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(text + "\n")
    print(path)


if __name__ == "__main__":
    main()
