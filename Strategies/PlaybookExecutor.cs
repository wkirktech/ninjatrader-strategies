#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
using NinjaTrader.NinjaScript.Indicators;
#endregion

// Manual executor for the "Reset at a Level" playbook (analysis\playbooks\reset-at-level.md).
// Run it on the NQ 377-tick chart: LONG / SHORT buttons send market orders on the micro (MNQ, same expiry) with a
// stop and target worked out like the RL ATM templates. It never enters on its own.
//   Stop:   past the last N bars' extreme + 1 tick, rounded up to 5-pt steps (min 15). Blocked over the ceiling (40)
//           or when even 1 MNQ would risk more than the per-trade $ cap ($170 default, never more than what is left of
//           today's loss limit).
//           If the ladder's 2 MNQ doesn't fit the cap, it trades 1.
//   Vol:    stop never tighter than VolStopPct of the 10-day range (10% ~ 30 pts). HIGH volatility (largest 1-min
//           candle over 2h >= threshold): always 1 MNQ and the ceiling rises to HighVolMaxStopPts (60).
//   Size:   David's ladder from the account P&L: 1 MNQ (all out at 1R) until +$1,000, then 2 MNQ (half at 1R, runner
//           to 3R; runner stop to breakeven at RunnerBreakevenR, default 1R, 0 = off), back to 1 below +$700.
//   Gate:   blocks entries after the daily trade / loss / $ limits, the same side within N minutes of a loss, or
//           when already in a trade.
//   Risk:   the stop may only move closer. A stop dragged closer by hand (MNQ chart / DOM) or TIGHTEN (5 pts) is kept;
//           a stop dragged wider is put back.
//   Manage: BE moves the stop to entry + 1 tick (only once price is past it), TGT->LVL moves the target once to the
//           nearest plan level at least 1R away, FLAT closes everything. No other edits.
// Draws the MNQ trade's entry / stop / target on this chart and appends each closed trade to
// analysis\playbooks\reset_journal.csv (outside the repo). Hosts TradePlanPanel for context and levels.
// Order calls never run inside the order / execution / position callbacks: those only set flags for the next update.
namespace NinjaTrader.NinjaScript.Strategies
{
	public class PlaybookExecutor : Strategy
	{
		private const string Leg1 = "RL1", Leg2 = "RL2";
		private static readonly Dictionary<string, string> MicroRoots = new Dictionary<string, string>
		{
			{ "NQ", "MNQ" }, { "ES", "MES" }, { "YM", "MYM" }, { "RTY", "M2K" },
		};

		private int execIdx;
		private TimeZoneInfo eastern, pacific;
		private TradePlanPanel panel;

		// trade in progress (strategy thread)
		private int tDir, tQty, tStopPts;
		private double tEntry, tMae, tMfe;
		private string tAtm = "", tCtx = "", tDayType = "", tLevel = "", tZc = "", tTrap = "";
		private DateTime tStartLocal, tStartEt;
		private bool inTrade;
		// target moves are free (they don't change risk) but logged: count, first and final target
		private int tgtMoves;
		private bool runnerBe;
		private double tgtFirst = double.NaN, tgtFinal = double.NaN;

		// set in callbacks, applied in ProcessPending
		private volatile bool pendingBe, pendingClose;
		private volatile string lastExitKind = "";
		private readonly object pxLock = new object();
		private readonly Dictionary<string, double> stopPx = new Dictionary<string, double>(), tgtPx = new Dictionary<string, double>();
		// the stop the strategy wants per leg, and stops dragged by hand (applied or reverted in ProcessPending)
		private readonly Dictionary<string, double> setStop = new Dictionary<string, double>(), dragged = new Dictionary<string, double>();
		private readonly Dictionary<string, double> setTgt = new Dictionary<string, double>(), tdragged = new Dictionary<string, double>();
		private double fillPx = double.NaN;
		// price extremes since the fill (MFE / MAE and runner BE ignore ticks before it)
		private double hiSince = double.NaN, loSince = double.NaN;
		// open contracts per leg (entry fills minus exits), and when each open leg was first seen without a stop
		private readonly Dictionary<string, int> legQty = new Dictionary<string, int>();
		private readonly Dictionary<string, DateTime> noStopSince = new Dictionary<string, DateTime>();

		// day limits
		private DateTime tradeDay = DateTime.MinValue;
		private int tradesToday, lossesToday, lastLossDir;
		private double pnlToday;
		private DateTime lastLossTime = DateTime.MinValue;
		private int ladderSize = 1;
		private string statusMsg = "";

		// UI
		private ChartControl chartControlRef;
		private StackPanel uiPanel, body;
		private Button headerButton;
		private bool collapsed;
		private TextBlock statusText, infoText;
		private Button longButton, shortButton, beButton, tgtButton, flatButton, tightenButton;
		private DateTime lastUi = DateTime.MinValue;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description = "Manual NQ-chart / MNQ-execution buttons for the Reset at a Level playbook, with David's size ladder, daily limits, trade lines and an auto journal.";
				Name = "PlaybookExecutor";
				Calculate = Calculate.OnPriceChange;
				EntriesPerDirection = 2;
				EntryHandling = EntryHandling.UniqueEntries;
				StopTargetHandling = StopTargetHandling.PerEntryExecution;
				IsExitOnSessionCloseStrategy = true;
				ExitOnSessionCloseSeconds = 30;
				StartBehavior = StartBehavior.WaitUntilFlat;
				RealtimeErrorHandling = RealtimeErrorHandling.StopCancelClose;
				TimeInForce = TimeInForce.Gtc;
				BarsRequiredToTrade = 20;
				TraceOrders = false;

				ExecutionInstrument = "";
				StartingBalance = 100000;
				LadderUpAt = 1000;
				LadderDownBelow = 700;
				MaxTradesPerDay = 3;
				MaxLossesPerDay = 2;
				DailyLossUsd = 300;
				SameSideLockoutMin = 15;
				MinStopPts = 15;
				StopLookbackBars = 10;
				MaxStopPts = 40;
				HighVolMaxStopPts = 60;
				HighVolCandlePts = 70;
				VolStopPct = 10;
				MaxRiskPerTradeUsd = 170;
				CostPerContractUsd = 3;
				ShowPlan = true;
				WriteJournal = true;
				RunnerBreakevenR = 1;
				StartCollapsed = true;
			}
			else if (State == State.Configure)
			{
				execIdx = 0;
				string exec = ExecutionInstrument == null ? "" : ExecutionInstrument.Trim();
				if (exec.Length == 0)
					exec = MicroOf(Instrument);
				if (exec.Length > 0 && !string.Equals(exec, Instrument.FullName, StringComparison.OrdinalIgnoreCase))
				{
					AddDataSeries(exec);
					execIdx = 1;
				}
			}
			else if (State == State.DataLoaded)
			{
				eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
				pacific = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
				panel = TradePlanPanel(Closes[0], 5, 3, 30, true, 11, Account != null ? Account.Name : "", StartingBalance, LadderUpAt, LadderDownBelow);
				panel.HighVolCandlePts = HighVolCandlePts;
				panel.VolStopPct = VolStopPct;
				if (ShowPlan)
					AddChartIndicator(panel);
			}
			else if (State == State.Historical)
			{
				if (ChartControl != null)
				{
					chartControlRef = ChartControl;
					chartControlRef.Dispatcher.InvokeAsync(new Action(BuildUi));
				}
			}
			else if (State == State.Terminated)
			{
				if (chartControlRef != null)
					chartControlRef.Dispatcher.InvokeAsync(new Action(RemoveUi));
			}
		}

		// NQ 12-26 -> MNQ 12-26. Blank when the chart is already a micro or has no known micro.
		private static string MicroOf(Instrument ins)
		{
			if (ins == null) return "";
			string root = ins.MasterInstrument.Name, micro;
			if (!MicroRoots.TryGetValue(root, out micro) || !ins.FullName.StartsWith(root)) return "";
			return micro + ins.FullName.Substring(root.Length);
		}

		protected override void OnBarUpdate()
		{
			if (BarsInProgress != 0 || CurrentBars[0] < StopLookbackBars + 1 || (execIdx > 0 && CurrentBars[execIdx] < 0))
				return;
			// panel needs updating on every primary bar even when hidden, so the levels exist for TGT->LVL
			panel.Update();
			if (State != State.Realtime)
				return;

			DayRoll();
			ProcessPending();
			if (inTrade && !double.IsNaN(fillPx))
			{
				hiSince = double.IsNaN(hiSince) ? Close[0] : Math.Max(hiSince, Close[0]);
				loSince = double.IsNaN(loSince) ? Close[0] : Math.Min(loSince, Close[0]);
				double up = hiSince - fillPx, dn = fillPx - loSince;
				tMfe = Math.Max(tMfe, tDir > 0 ? up : dn);
				tMae = Math.Max(tMae, tDir > 0 ? dn : up);
			}
			DrawTrade();
			if ((DateTime.Now - lastUi).TotalMilliseconds >= 500)
				RefreshUi();
		}

		#region Gate, size, stop
		private void DayRoll()
		{
			DateTime et = TimeZoneInfo.ConvertTime(Times[0][0], Core.Globals.GeneralOptions.TimeZoneInfo, eastern);
			DateTime d = et.TimeOfDay >= new TimeSpan(18, 0, 0) ? et.Date.AddDays(1) : et.Date;
			if (d != tradeDay)
			{
				tradeDay = d; tradesToday = 0; lossesToday = 0; pnlToday = 0;
				LoadDay();
			}
		}

		// Day limits survive a restart / reload: saved after each closed trade, read back for the same account and trade day
		private string DayFile()
		{
			string acct = Account != null ? Account.Name : "none";
			foreach (char ch in Path.GetInvalidFileNameChars()) acct = acct.Replace(ch, '_');
			return Path.Combine(Core.Globals.UserDataDir, "analysis", "playbooks", "executor_day_" + acct + ".txt");
		}

		private void LoadDay()
		{
			try
			{
				string path = DayFile();
				if (!File.Exists(path)) return;
				CultureInfo ci = CultureInfo.InvariantCulture;
				string[] f = File.ReadAllText(path).Trim().Split(',');
				if (f.Length < 6 || f[0] != tradeDay.ToString("yyyy-MM-dd", ci)) return;
				tradesToday = int.Parse(f[1], ci); lossesToday = int.Parse(f[2], ci); pnlToday = double.Parse(f[3], ci);
				lastLossDir = int.Parse(f[4], ci); lastLossTime = new DateTime(long.Parse(f[5], ci));
				statusMsg = string.Format("today so far: {0} trades, {1} losses, {2}{3:N0}", tradesToday, lossesToday, pnlToday >= 0 ? "+$" : "-$", Math.Abs(pnlToday));
			}
			catch (Exception ex) { Print("PlaybookExecutor day file: " + ex.Message); }
		}

		private void SaveDay()
		{
			CultureInfo ci = CultureInfo.InvariantCulture;
			string line = string.Join(",", tradeDay.ToString("yyyy-MM-dd", ci), tradesToday.ToString(ci), lossesToday.ToString(ci),
				pnlToday.ToString("0.00", ci), lastLossDir.ToString(ci), lastLossTime.Ticks.ToString(ci));
			try { File.WriteAllText(DayFile(), line); }
			catch (Exception ex) { Print("PlaybookExecutor day file: " + ex.Message); }
		}

		private int Ladder()
		{
			if (Account == null) return 1;
			double pnl = Account.Get(AccountItem.NetLiquidation, Currency.UsDollar) - StartingBalance;
			if (pnl >= LadderUpAt) ladderSize = 2;
			else if (pnl < LadderDownBelow) ladderSize = 1;
			return ladderSize;
		}

		// Stop past the last N bars' extreme + 1 tick, rounded up to 5-pt steps (min MinStopPts). 0 = over the ceiling.
		private int TemplateStop(int dir, out double raw)
		{
			double ext = dir > 0 ? MIN(Lows[0], StopLookbackBars)[0] - TickSize : MAX(Highs[0], StopLookbackBars)[0] + TickSize;
			double volFloor = VolStopPct > 0 ? panel.TenDayRange * VolStopPct / 100.0 : 0;
			raw = Math.Max(Math.Max(MinStopPts, volFloor), Math.Abs(Closes[0][0] - ext));
			int pts = (int)(Math.Ceiling(raw / 5.0) * 5);
			return pts > Ceiling() ? 0 : pts;
		}

		// high vol (big candle or a floor above the normal ceiling): wider ceiling, never below the vol floor + 5
		private double Ceiling()
		{
			if (!panel.HighVol) return MaxStopPts;
			double floor = VolStopPct > 0 ? panel.TenDayRange * VolStopPct / 100.0 : 0;
			return Math.Max(Math.Max(MaxStopPts, HighVolMaxStopPts), Math.Ceiling(floor / 5.0) * 5 + 5);
		}

		// Per-trade $ cap: the set cap (or daily limit / max losses), never more than what is left of today's loss limit.
		// Today's profits don't add room. 0 = no cap at all (no limits set).
		private double RiskCap()
		{
			double cap = MaxRiskPerTradeUsd > 0 ? MaxRiskPerTradeUsd : DailyLossUsd > 0 ? DailyLossUsd / Math.Max(1, MaxLossesPerDay) : 0;
			if (DailyLossUsd > 0)
			{
				double left = Math.Max(0.01, DailyLossUsd + Math.Min(0, pnlToday));
				cap = cap > 0 ? Math.Min(cap, left) : left;
			}
			return cap;
		}

		private double RiskUsd(int stopPts, int qty)
		{
			return qty * (stopPts * Instruments[execIdx].MasterInstrument.PointValue + CostPerContractUsd);
		}

		// Ladder size, cut to what fits the $ cap; 0 = not even 1 contract fits
		private int SizeFor(int stopPts)
		{
			int qty = panel.HighVol ? 1 : Ladder();   // high volatility: 1 MNQ only
			double cap = RiskCap();
			while (qty > 0 && cap > 0 && RiskUsd(stopPts, qty) > cap) qty--;
			return qty;
		}

		// Any open position on the account in this chart's or the execution instrument's root (e.g. a DOM trade, or one
		// left from before a restart); null = none
		private string AccountPosition()
		{
			if (Account == null) return null;
			string a = Instrument.MasterInstrument.Name, b = Instruments[execIdx].MasterInstrument.Name;
			lock (Account.Positions)
				foreach (Position p in Account.Positions)
					if (p.MarketPosition != MarketPosition.Flat && (p.Instrument.MasterInstrument.Name == a || p.Instrument.MasterInstrument.Name == b))
						return string.Format("{0} {1} {2}", p.MarketPosition == MarketPosition.Long ? "long" : "short", p.Quantity, p.Instrument.FullName);
			return null;
		}

		private string Blocked(int dir)
		{
			if (State != State.Realtime) return "not live yet";
			if (inTrade || Positions[execIdx].MarketPosition != MarketPosition.Flat) return "already in a trade";
			string held = AccountPosition();
			if (held != null) return "account already holds " + held;
			if (tradesToday >= MaxTradesPerDay) return tradesToday + " trades today: done for the day";
			if (lossesToday >= MaxLossesPerDay) return lossesToday + " losses today: done for the day";
			if (DailyLossUsd > 0 && pnlToday <= -DailyLossUsd) return "daily loss limit hit";
			if (dir == lastLossDir && (DateTime.Now - lastLossTime).TotalMinutes < SameSideLockoutMin)
				return "same side locked until " + lastLossTime.AddMinutes(SameSideLockoutMin).ToString("H:mm");
			double raw;
			int sp = TemplateStop(dir, out raw);
			if (sp == 0) return string.Format("stop {0:0.0} pts > {1:0} ceiling, skip", raw, Ceiling());
			if (SizeFor(sp) == 0) return string.Format("{0} pts = ${1:0} at 1 MNQ > ${2:0} per-trade cap, skip", sp, RiskUsd(sp, 1), RiskCap());
			return null;
		}
		#endregion

		#region Orders (strategy thread only)
		private void Enter(int dir)
		{
			string why = Blocked(dir);
			if (why != null) { statusMsg = "BLOCKED: " + why; RefreshUi(); return; }

			double raw;
			int stopPts = TemplateStop(dir, out raw);
			int qty = SizeFor(stopPts);
			int ticks = (int)Math.Round(stopPts / Instruments[execIdx].MasterInstrument.TickSize);
			SetStopLoss(Leg1, CalculationMode.Ticks, ticks, false);
			SetProfitTarget(Leg1, CalculationMode.Ticks, ticks);
			if (qty >= 2)
			{
				SetStopLoss(Leg2, CalculationMode.Ticks, ticks, false);
				SetProfitTarget(Leg2, CalculationMode.Ticks, 3 * ticks);
			}
			if (dir > 0) { EnterLong(execIdx, 1, Leg1); if (qty >= 2) EnterLong(execIdx, 1, Leg2); }
			else { EnterShort(execIdx, 1, Leg1); if (qty >= 2) EnterShort(execIdx, 1, Leg2); }

			inTrade = true; tDir = dir; tQty = qty; tStopPts = stopPts; tEntry = Closes[0][0]; tMae = tMfe = 0; tgtMoves = 0; tgtFirst = tgtFinal = double.NaN; runnerBe = false;
			tAtm = "RL" + stopPts + (qty == 1 ? "x1" : "");
			tStartLocal = Times[0][0];
			tStartEt = TimeZoneInfo.ConvertTime(tStartLocal, Core.Globals.GeneralOptions.TimeZoneInfo, eastern);
			tCtx = panel.ContextText + (panel.Bias > 0 ? " / long bias" : panel.Bias < 0 ? " / short bias" : " / no bias");
			tDayType = panel.DayTypeText; tLevel = panel.NearestLevelText; tZc = panel.ZCubedText; tTrap = panel.TrapText;
			fillPx = double.NaN; hiSince = loSince = double.NaN; lastExitKind = "";
			lock (pxLock) { legQty.Clear(); noStopSince.Clear(); }
			statusMsg = string.Format("{0} {1} {2} sent, {3} ({4:0.0} pts to the {5}-bar extreme)", dir > 0 ? "LONG" : "SHORT", qty,
				Instruments[execIdx].FullName, tAtm, raw, StopLookbackBars);
			RefreshUi();
		}

		private void SetTarget(string leg, double price)
		{
			lock (pxLock) setTgt[leg] = price;
			SetProfitTarget(leg, CalculationMode.Price, price);
			tgtMoves++; tgtFinal = price;
		}

		// Targets dragged by hand are kept (any direction) as long as they stay on the profitable side of price
		private void ApplyTargetDrags()
		{
			List<KeyValuePair<string, double>> drags;
			lock (pxLock) { drags = tdragged.ToList(); tdragged.Clear(); }
			foreach (var d in drags)
			{
				double cur;
				lock (pxLock) if (!inTrade || !setTgt.TryGetValue(d.Key, out cur)) continue;
				if (tDir * (d.Value - Closes[execIdx][0]) >= 2 * TickSize)
				{
					SetTarget(d.Key, d.Value);
					statusMsg = string.Format("{0} target -> {1} ({2} moves)", d.Key, d.Value.ToString("0.00", CultureInfo.InvariantCulture), tgtMoves);
				}
				else { SetProfitTarget(d.Key, CalculationMode.Price, cur); statusMsg = "target on the wrong side of price: put back"; }
			}
		}

		// Every stop change goes through here so a later hand drag can be judged against it
		private void SetStop(string leg, double price)
		{
			lock (pxLock) setStop[leg] = price;
			SetStopLoss(leg, CalculationMode.Price, price, false);
		}

		// Risk may only go down: a stop moved closer (by hand or TIGHTEN) is kept, a wider one is put back
		private void ApplyDrags()
		{
			List<KeyValuePair<string, double>> drags;
			lock (pxLock) { drags = dragged.ToList(); dragged.Clear(); }
			foreach (var d in drags)
			{
				double cur;
				lock (pxLock) if (!inTrade || !setStop.TryGetValue(d.Key, out cur)) continue;
				bool tighter = tDir * (d.Value - cur) > 0;
				bool valid = tDir * (Closes[execIdx][0] - d.Value) >= 2 * TickSize;
				if (tighter && valid)
				{
					SetStop(d.Key, d.Value);
					statusMsg = "stop tightened to " + d.Value.ToString("0.00", CultureInfo.InvariantCulture) + " (risk down)";
				}
				else
				{
					SetStop(d.Key, cur);
					statusMsg = tighter ? "stop too close to price: put back" : "wider stop refused: risk can only go down";
				}
			}
		}

		private void Tighten()
		{
			if (!inTrade || Positions[execIdx].MarketPosition == MarketPosition.Flat) { statusMsg = "no open trade"; return; }
			int moved = 0;
			foreach (string leg in OpenLegs())
			{
				double cur;
				lock (pxLock) if (!setStop.TryGetValue(leg, out cur)) continue;
				double nw = cur + tDir * 5;
				if (tDir * (Closes[execIdx][0] - nw) < 2 * TickSize) continue;
				SetStop(leg, nw); moved++;
			}
			statusMsg = moved > 0 ? "stop 5 pts closer" : "TIGHTEN refused: price too close to the stop";
		}

		private void MoveToBreakeven()
		{
			if (!inTrade || Positions[execIdx].MarketPosition == MarketPosition.Flat) { statusMsg = "no open trade"; return; }
			double avg = Positions[execIdx].AveragePrice, be = avg + tDir * Instruments[execIdx].MasterInstrument.TickSize;
			double last = Closes[execIdx][0];
			if (tDir * (last - be) < 2 * TickSize) { statusMsg = "BE refused: price not past entry + 1 tick yet"; return; }
			int moved = 0;
			foreach (string leg in OpenLegs())
			{
				double cur;
				lock (pxLock) if (setStop.TryGetValue(leg, out cur) && tDir * (be - cur) <= 0) continue;   // already at or past BE
				SetStop(leg, be); moved++;
			}
			statusMsg = moved > 0 ? "stop moved to breakeven " + be.ToString("0.00", CultureInfo.InvariantCulture) : "stop already at or past breakeven";
		}

		private void MoveTargetToLevel()
		{
			if (!inTrade || Positions[execIdx].MarketPosition == MarketPosition.Flat) { statusMsg = "no open trade"; return; }
			double entry = Positions[execIdx].AveragePrice;
			double[] lv = panel.LevelPrices;
			double last = Closes[execIdx][0];
			// at least 1R from entry and still ahead of price (a level already passed would fill the target at once)
			var c = lv.Where(p => tDir * (p - entry) >= tStopPts && tDir * (p - tDir * TickSize - last) >= 2 * TickSize).OrderBy(p => Math.Abs(p - entry)).ToList();
			if (c.Count == 0) { statusMsg = "no plan level at least 1R away and ahead of price"; return; }
			double px = c[0] - tDir * TickSize;   // one tick in front of the level
			string leg = tQty >= 2 ? Leg2 : Leg1;
			if (!OpenLegs().Contains(leg)) { statusMsg = leg + " already closed"; return; }
			SetTarget(leg, px);
			statusMsg = string.Format("{0} target -> {1} ({2:0.0} pts, {3:0.0}R)", leg, px.ToString("0.00", CultureInfo.InvariantCulture), Math.Abs(px - entry), Math.Abs(px - entry) / tStopPts);
		}

		private void Flatten()
		{
			Position p = Positions[execIdx];
			if (p.MarketPosition == MarketPosition.Long) ExitLong(execIdx, p.Quantity, "Flat", "");
			else if (p.MarketPosition == MarketPosition.Short) ExitShort(execIdx, p.Quantity, "Flat", "");
			statusMsg = "flatten sent";
		}

		private List<string> OpenLegs()
		{
			lock (pxLock)
				return stopPx.Keys.ToList();
		}

		private void ProcessPending()
		{
			pendingBe = false;
			// Runner to breakeven once price reaches RunnerBreakevenR (0 = never). Backtest 2026-11-08: BE at 1R vs none
			// = -0.005R +/- 0.011 per trade, i.e. no difference in R; it trades fewer full losses for fewer 3R runners.
			if (inTrade && !runnerBe && RunnerBreakevenR > 0 && OpenLegs().Contains(Leg2))
			{
				double avg = Positions[execIdx].AveragePrice, trig = avg + tDir * RunnerBreakevenR * tStopPts;
				double be = avg + tDir * Instruments[execIdx].MasterInstrument.TickSize;
				bool reached = tDir > 0 ? hiSince >= trig : loSince <= trig;   // false while NaN (no fill yet)
				if (reached && tDir * (Closes[execIdx][0] - be) >= 2 * TickSize)
				{
					double cur;
					bool tighter;
					lock (pxLock) tighter = !setStop.TryGetValue(Leg2, out cur) || tDir * (be - cur) > 0;
					if (tighter) SetStop(Leg2, be);   // never loosens a stop you already tightened past BE
					runnerBe = true;
					statusMsg = string.Format("runner stop to breakeven at {0:0.#}R", RunnerBreakevenR);
				}
			}
			ApplyDrags();
			ApplyTargetDrags();
			GuardStops();
			if (pendingClose)
			{
				pendingClose = false;
				if (inTrade) CloseTrade();
			}
		}

		// Every open leg must have a working stop. Missing for 2 s (e.g. cancelled in Chart Trader / DOM): put it back;
		// still missing 2 s later: close that leg.
		private void GuardStops()
		{
			if (!inTrade || pendingClose || Positions[execIdx].MarketPosition == MarketPosition.Flat) return;
			List<string> legs;
			lock (pxLock) legs = legQty.Where(kv => kv.Value > 0).Select(kv => kv.Key).ToList();
			foreach (string leg in legs)
			{
				double want;
				bool known;
				DateTime since;
				lock (pxLock)
				{
					if (stopPx.ContainsKey(leg)) { noStopSince.Remove(leg); continue; }
					known = setStop.TryGetValue(leg, out want);
					if (!noStopSince.TryGetValue(leg, out since)) { noStopSince[leg] = DateTime.Now; continue; }
				}
				double secs = (DateTime.Now - since).TotalSeconds;
				if (secs < 2) continue;
				if (secs < 4 && known)
				{
					SetStopLoss(leg, CalculationMode.Price, want, false);
					statusMsg = leg + " had no stop: stop put back at " + want.ToString("0.00", CultureInfo.InvariantCulture);
					continue;
				}
				int q;
				lock (pxLock) { q = legQty[leg]; noStopSince[leg] = DateTime.Now; }
				if (tDir > 0) ExitLong(execIdx, q, "NoStop", leg); else ExitShort(execIdx, q, "NoStop", leg);
				statusMsg = leg + " still had no stop: leg closed";
				panel.Announce("No stop on the trade. Leg closed.");
			}
		}

		private void CloseTrade()
		{
			var trades = SystemPerformance.AllTrades.Where(t => t.Entry.Time >= tStartLocal.AddSeconds(-1)).ToList();
			double pnl = trades.Sum(t => t.ProfitCurrency - t.Commission);
			bool loss = trades.Sum(t => t.ProfitCurrency) < 0;   // before commission: a breakeven exit is not a loss
			double qty = trades.Sum(t => (double)t.Quantity);
			double exit = qty > 0 ? trades.Sum(t => t.Exit.Price * t.Quantity) / qty : Closes[execIdx][0];
			double entry = !double.IsNaN(fillPx) ? fillPx : tEntry;
			double pv = Instruments[execIdx].MasterInstrument.PointValue;
			double r = tStopPts > 0 && tQty > 0 ? pnl / (tStopPts * pv * tQty) : 0;

			tradesToday++;
			pnlToday += pnl;
			if (loss) { lossesToday++; lastLossDir = tDir; lastLossTime = DateTime.Now; }
			SaveDay();
			string done = tradesToday >= MaxTradesPerDay ? tradesToday + " trades today." : lossesToday >= MaxLossesPerDay ? lossesToday + " losses today."
				: DailyLossUsd > 0 && pnlToday <= -DailyLossUsd ? "Daily loss limit hit." : null;
			if (done != null) panel.Announce(done + " Done for the day.");
			else if (loss && SameSideLockoutMin > 0) panel.Announce(string.Format("{0} side locked for {1} minutes.", tDir > 0 ? "Long" : "Short", SameSideLockoutMin));
			if (WriteJournal)
				AppendJournal(entry, exit, pnl, r);
			statusMsg = string.Format("closed {0}: {1}{2:N0} ({3:+0.00;-0.00}R), {4}", tDir > 0 ? "long" : "short", pnl >= 0 ? "+$" : "-$", Math.Abs(pnl), r, lastExitKind);
			inTrade = false;
			lock (pxLock) { stopPx.Clear(); tgtPx.Clear(); setStop.Clear(); dragged.Clear(); setTgt.Clear(); tdragged.Clear(); legQty.Clear(); noStopSince.Clear(); }
			RemoveDrawObject("PE_entry"); RemoveDrawObject("PE_stop"); RemoveDrawObject("PE_tgt1"); RemoveDrawObject("PE_tgt2"); RemoveDrawObject("PE_lbl");
		}
		#endregion

		#region Callbacks: record only, no order calls
		protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice, int quantity, int filled,
			double averageFillPrice, OrderState orderState, DateTime time, ErrorCode error, string comment)
		{
			if (order.Instrument != Instruments[execIdx]) return;
			string leg = order.FromEntrySignal;
			bool live = orderState == OrderState.Working || orderState == OrderState.Accepted || orderState == OrderState.ChangeSubmitted || orderState == OrderState.Submitted;
			lock (pxLock)
			{
				if (order.Name == "Stop loss")
				{
					if (live) stopPx[leg] = stopPrice; else if (orderState != OrderState.ChangePending) stopPx.Remove(leg);
					// first live report = the strategy's own stop; any later different price = a hand drag (judged in ApplyDrags)
					double want, px = orderState == OrderState.ChangePending && order.StopPriceChanged > 0 ? order.StopPriceChanged : stopPrice;
					if (live || orderState == OrderState.ChangePending)
					{
						if (!setStop.TryGetValue(leg, out want)) setStop[leg] = px;
						else if (Math.Abs(px - want) > TickSize / 2) dragged[leg] = px;
					}
				}
				else if (order.Name == "Profit target")
				{
					if (live) tgtPx[leg] = limitPrice; else if (orderState != OrderState.ChangePending) tgtPx.Remove(leg);
					double wantT, tp = orderState == OrderState.ChangePending && order.LimitPriceChanged > 0 ? order.LimitPriceChanged : limitPrice;
					if (live || orderState == OrderState.ChangePending)
					{
						if (!setTgt.TryGetValue(leg, out wantT)) { setTgt[leg] = tp; if (double.IsNaN(tgtFirst) || leg == Leg2) { tgtFirst = tp; tgtFinal = tp; } }
						else if (Math.Abs(tp - wantT) > TickSize / 2) tdragged[leg] = tp;
					}
				}
			}
		}

		protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity,
			MarketPosition marketPosition, string orderId, DateTime time)
		{
			Order o = execution.Order;
			if (o == null || execution.Instrument != Instruments[execIdx]) return;
			if ((o.Name == Leg1 || o.Name == Leg2) && double.IsNaN(fillPx)) fillPx = price;
			lock (pxLock)
			{
				int q;
				if (o.Name == Leg1 || o.Name == Leg2) { legQty.TryGetValue(o.Name, out q); legQty[o.Name] = q + quantity; }
				else if (!string.IsNullOrEmpty(o.FromEntrySignal) && legQty.TryGetValue(o.FromEntrySignal, out q)) legQty[o.FromEntrySignal] = Math.Max(0, q - quantity);
			}
			if (o.Name == "Profit target" && o.FromEntrySignal == Leg1) { pendingBe = true; lastExitKind = "target"; }
			else if (o.Name == "Profit target") lastExitKind = "target";
			else if (o.Name == "Stop loss") lastExitKind = Math.Abs(price - (double.IsNaN(fillPx) ? price : fillPx)) <= 2 * TickSize ? "breakeven" : "stop";
			else if (o.Name == "Flat") lastExitKind = "flat (manual)";
			else if (o.Name == "NoStop") lastExitKind = "closed: no stop";
			else if (o.Name == "Exit on session close") lastExitKind = "session close";
		}

		protected override void OnPositionUpdate(Position position, double averagePrice, int quantity, MarketPosition marketPosition)
		{
			if (position.Instrument == Instruments[execIdx] && marketPosition == MarketPosition.Flat)
				pendingClose = true;
		}
		#endregion

		#region Journal
		private void AppendJournal(double entry, double exit, double pnl, double r)
		{
			CultureInfo ci = CultureInfo.InvariantCulture;
			Func<string, string> c = s => (s ?? "").Replace(",", ";");
			Func<double, string> p = v => v.ToString("0.00", ci);
			DateTime pt = TimeZoneInfo.ConvertTime(tStartEt, eastern, pacific);
			double stop = entry - tDir * tStopPts, tp1 = entry + tDir * tStopPts;
			string runner = tQty >= 2 ? lastExitKind : "";
			string[] row =
			{
				tradeDay.ToString("yyyy-MM-dd", ci), pt.ToString("HH:mm", ci), c(Account != null ? Account.Name : ""), tDir > 0 ? "long" : "short",
				"", "", c(tCtx), c(tDayType), c(tLevel), c(tTrap), c(tZc), "", "", "",
				tQty.ToString(ci), tAtm, p(entry), p(stop), tStopPts.ToString(ci), p(tp1), p(exit), c(runner),
				tMae.ToString("0.00", ci), tMfe.ToString("0.00", ci), r.ToString("0.00", ci), pnl.ToString("0.00", ci),
				"yes", "", "", c("auto: PlaybookExecutor, exit " + lastExitKind),
				tgtMoves.ToString(ci), double.IsNaN(tgtFirst) ? "" : p(tgtFirst), double.IsNaN(tgtFinal) ? "" : p(tgtFinal),
				double.IsNaN(tgtFinal) ? "" : (tDir * (tgtFinal - entry) / tStopPts).ToString("0.00", ci)
			};
			string line = string.Join(",", row) + Environment.NewLine;
			string path = Path.Combine(Core.Globals.UserDataDir, "analysis", "playbooks", "reset_journal.csv");
			// file I/O off the strategy thread
			Task.Run(() =>
			{
				try
				{
					lock (MicroRoots)
					{
						if (!File.Exists(path))
							File.WriteAllText(path, JournalHeader + Environment.NewLine);
						File.AppendAllText(path, line);
					}
				}
				catch (Exception ex) { Print("PlaybookExecutor journal: " + ex.Message); }
			});
		}

		private const string JournalHeader = "date,time_pt,mode,dir,dir_preplay,htf_draw,context,day_type,levels,trap,zc_reset,confirm,pmarp5_confirm,coded_rule_fired,contracts,atm,entry,stop,stop_pts,tp1,exit,runner_exit,mae_pts,mfe_pts,r_result,pnl_usd,taken,boxes_missed,screenshot,notes,tgt_moves,tgt_first,tgt_final,tgt_final_r";
		#endregion

		#region Chart lines and UI
		// The MNQ orders don't show on an NQ chart, so draw them. NQ and MNQ trade at the same price.
		private void DrawTrade()
		{
			if (!inTrade) return;
			double entry = !double.IsNaN(fillPx) ? fillPx : tEntry;
			double sp = double.NaN, t1 = double.NaN, t2 = double.NaN;
			lock (pxLock)
			{
				if (stopPx.Count > 0) sp = tDir > 0 ? stopPx.Values.Min() : stopPx.Values.Max();
				double v;
				if (tgtPx.TryGetValue(Leg1, out v)) t1 = v;
				if (tgtPx.TryGetValue(Leg2, out v)) t2 = v;
			}
			Draw.HorizontalLine(this, "PE_entry", entry, Brushes.White).IsLocked = true;
			if (!double.IsNaN(sp)) Draw.HorizontalLine(this, "PE_stop", sp, Brushes.Tomato).IsLocked = true; else RemoveDrawObject("PE_stop");
			if (!double.IsNaN(t1)) Draw.HorizontalLine(this, "PE_tgt1", t1, Brushes.LimeGreen).IsLocked = true; else RemoveDrawObject("PE_tgt1");
			if (!double.IsNaN(t2)) Draw.HorizontalLine(this, "PE_tgt2", t2, Brushes.SeaGreen).IsLocked = true; else RemoveDrawObject("PE_tgt2");
			string lbl = string.Format("MNQ {0} {1} @ {2:0.00}", tDir > 0 ? "LONG" : "SHORT", Positions[execIdx].Quantity, entry);
			Draw.Text(this, "PE_lbl", lbl, 0, entry, Brushes.White);
		}

		private void RefreshUi()
		{
			lastUi = DateTime.Now;
			if (chartControlRef == null) return;
			CultureInfo ci = CultureInfo.InvariantCulture;
			int size = Ladder();
			double pnlAcct = Account != null ? Account.Get(AccountItem.NetLiquidation, Currency.UsDollar) - StartingBalance : 0;
			var sb = new System.Text.StringBuilder();
			sb.AppendLine(string.Format("{0} | {1}", Instruments[execIdx].FullName, Account != null ? Account.Name : "-"));
			sb.AppendLine(string.Format("ladder {0} MNQ (P&L {1}{2:N0})", size, pnlAcct >= 0 ? "+$" : "-$", Math.Abs(pnlAcct)));
			sb.AppendLine(string.Format("today {0}/{1} trades  {2}/{3} losses  {4}{5:N0}", tradesToday, MaxTradesPerDay, lossesToday, MaxLossesPerDay, pnlToday >= 0 ? "+$" : "-$", Math.Abs(pnlToday)));
			// spoken at the end of the panel's 🔊 Plan read
			int microsAllowed = panel.HighVol ? Math.Min(1, size) : size;
			panel.RiskText = string.Format("Size allowed: {0} micro{1}{2}. Risk cap {3:0} dollars this trade, {4:0} dollars left today. {5} trades and {6} losses left.",
				microsAllowed, microsAllowed == 1 ? "" : "s", panel.HighVol ? ", high volatility" : "", RiskCap(),
				DailyLossUsd > 0 ? DailyLossUsd + Math.Min(0, pnlToday) : 0, Math.Max(0, MaxTradesPerDay - tradesToday), Math.Max(0, MaxLossesPerDay - lossesToday));
			if (!inTrade)
			{
				double rl, rs;
				int sl = TemplateStop(1, out rl), ss = TemplateStop(-1, out rs);
				Func<int, string> plan = sp => { if (sp == 0) return "skip (ceiling)"; int q = SizeFor(sp); return q == 0 ? "skip ($ cap)" : string.Format("{0} pts x {1} MNQ = ${2:0}", sp, q, RiskUsd(sp, q)); };
				sb.AppendLine(string.Format("cap ${0:0} this trade (day left ${1:0})  ceiling {2:0} pts", RiskCap(), DailyLossUsd > 0 ? DailyLossUsd + Math.Min(0, pnlToday) : 0, Ceiling()));
				sb.AppendLine(string.Format("vol: 1-min candle {0:0} (2h){1}  floor {2:0} pts", panel.LargestCandlePts, panel.HighVol ? " HIGH -> 1 MNQ" : "", Math.Max(MinStopPts, VolStopPct > 0 ? panel.TenDayRange * VolStopPct / 100.0 : 0)));
				sb.AppendLine(string.Format("long now:  {0:0.0} -> {1}", rl, plan(sl)));
				sb.Append(string.Format("short now: {0:0.0} -> {1}", rs, plan(ss)));
			}
			else
			{
				double entry = !double.IsNaN(fillPx) ? fillPx : tEntry;
				double open = tDir * (Closes[execIdx][0] - entry) * Instruments[execIdx].MasterInstrument.PointValue * Positions[execIdx].Quantity;
				sb.AppendLine(string.Format("IN {0} {1} @ {2} {3}", tDir > 0 ? "LONG" : "SHORT", Positions[execIdx].Quantity, entry.ToString("0.00", ci), tAtm));
				sb.Append(string.Format("open {0}{1:N0}  MAE {2:0.0}  MFE {3:0.0}", open >= 0 ? "+$" : "-$", Math.Abs(open), tMae, tMfe));
			}
			string info = sb.ToString();
			string why = inTrade ? null : Blocked(panel.Bias != 0 ? panel.Bias : 1);
			string status = statusMsg.Length > 0 ? statusMsg : why == null ? "READY" : "BLOCKED: " + why;
			Brush sBrush = status.StartsWith("BLOCKED") || status.Contains("refused") ? Brushes.Tomato : status == "READY" ? Brushes.LimeGreen : Brushes.Gold;
			string head = inTrade
				? string.Format("EXEC  IN {0} {1} MNQ", tDir > 0 ? "LONG" : "SHORT", Positions[execIdx].Quantity)
				: string.Format("EXEC  {0} | {1} MNQ", why == null ? "READY" : "BLOCKED", size);
			Brush hBrush = inTrade ? Brushes.Gold : why == null ? Brushes.LimeGreen : Brushes.Tomato;
			chartControlRef.Dispatcher.InvokeAsync(new Action(() =>
			{
				if (infoText == null) return;
				headerButton.Content = (collapsed ? "\u25B8 " : "\u25BE ") + head;
				headerButton.Foreground = hBrush;
				infoText.Text = info;
				statusText.Text = status;
				statusText.Foreground = sBrush;
			}));
		}

		private Button MakeButton(string text, double width, Brush accent)
		{
			return new Button
			{
				Content = text, Width = width, Height = 24, FontSize = 11, Foreground = accent,
				Background = new SolidColorBrush(Color.FromRgb(45, 45, 50)), BorderBrush = accent,
				BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 4, 4), Cursor = System.Windows.Input.Cursors.Hand
			};
		}

		private void BuildUi()
		{
			if (chartControlRef == null || uiPanel != null) return;
			uiPanel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 16, 70, 0) };
			var border = new Border
			{
				Background = new SolidColorBrush(Color.FromArgb(215, 15, 15, 20)), BorderBrush = Brushes.DimGray,
				BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(6), MinWidth = 180
			};
			var inner = new StackPanel();
			collapsed = StartCollapsed;
			headerButton = new Button
			{
				Content = (collapsed ? "\u25B8 " : "\u25BE ") + "EXEC  loading...", FontWeight = FontWeights.Bold, FontSize = 12, Foreground = Brushes.Gold,
				Background = Brushes.Transparent, BorderThickness = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Left,
				Cursor = System.Windows.Input.Cursors.Hand, ToolTip = "Show / hide the PlaybookExecutor controls (MNQ)"
			};
			inner.Children.Add(headerButton);
			body = new StackPanel { Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible };
			inner.Children.Add(body);
			statusText = new TextBlock { Foreground = Brushes.Gray, FontFamily = new FontFamily("Consolas"), FontSize = 11, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = 300 };
			infoText = new TextBlock { Foreground = Brushes.Gainsboro, FontFamily = new FontFamily("Consolas"), FontSize = 11, Margin = new Thickness(0, 4, 0, 6), Text = "loading..." };
			body.Children.Add(statusText);
			body.Children.Add(infoText);

			var row1 = new StackPanel { Orientation = Orientation.Horizontal };
			longButton = MakeButton("LONG", 80, Brushes.DeepSkyBlue);
			shortButton = MakeButton("SHORT", 80, Brushes.OrangeRed);
			row1.Children.Add(longButton); row1.Children.Add(shortButton);
			var row2 = new StackPanel { Orientation = Orientation.Horizontal };
			beButton = MakeButton("BE", 50, Brushes.Gold);
			tightenButton = MakeButton("TIGHTEN", 65, Brushes.Gold);
			tgtButton = MakeButton("TGT->LVL", 75, Brushes.LimeGreen);
			flatButton = MakeButton("FLAT", 55, Brushes.Tomato);
			row2.Children.Add(beButton); row2.Children.Add(tightenButton); row2.Children.Add(tgtButton); row2.Children.Add(flatButton);
			body.Children.Add(row1); body.Children.Add(row2);
			border.Child = inner;
			uiPanel.Children.Add(border);

			headerButton.Click += OnHeaderClick;
			longButton.Click += OnLongClick; shortButton.Click += OnShortClick;
			beButton.Click += OnBeClick; tightenButton.Click += OnTightenClick; tgtButton.Click += OnTgtClick; flatButton.Click += OnFlatClick;
			UserControlCollection.Add(uiPanel);
		}

		private void RemoveUi()
		{
			if (headerButton != null) headerButton.Click -= OnHeaderClick;
			if (longButton != null) longButton.Click -= OnLongClick;
			if (shortButton != null) shortButton.Click -= OnShortClick;
			if (beButton != null) beButton.Click -= OnBeClick;
			if (tightenButton != null) tightenButton.Click -= OnTightenClick;
			if (tgtButton != null) tgtButton.Click -= OnTgtClick;
			if (flatButton != null) flatButton.Click -= OnFlatClick;
			if (uiPanel != null && UserControlCollection.Contains(uiPanel)) UserControlCollection.Remove(uiPanel);
			uiPanel = null; body = null; headerButton = null; infoText = null; statusText = null;
		}

		// Buttons run on the UI thread; hand the work to the strategy thread, primary series context
		private void OnUi(Action a)
		{
			TriggerCustomEvent(o => { ProcessPending(); a(); RefreshUi(); }, 0, null);
		}
		// UI thread only: show / hide the controls, no strategy work
		private void OnHeaderClick(object sender, RoutedEventArgs e)
		{
			collapsed = !collapsed;
			body.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
			string txt = headerButton.Content as string ?? "";
			headerButton.Content = (collapsed ? "\u25B8 " : "\u25BE ") + (txt.Length > 2 ? txt.Substring(2) : "EXEC");
		}
		private void OnLongClick(object sender, RoutedEventArgs e) { OnUi(() => Enter(1)); }
		private void OnShortClick(object sender, RoutedEventArgs e) { OnUi(() => Enter(-1)); }
		private void OnBeClick(object sender, RoutedEventArgs e) { OnUi(MoveToBreakeven); }
		private void OnTightenClick(object sender, RoutedEventArgs e) { OnUi(Tighten); }
		private void OnTgtClick(object sender, RoutedEventArgs e) { OnUi(MoveTargetToLevel); }
		private void OnFlatClick(object sender, RoutedEventArgs e) { OnUi(Flatten); }
		#endregion

		#region Properties
		[NinjaScriptProperty]
		[Display(Name = "Execution instrument", Description = "Blank = the chart's micro (NQ -> MNQ, same expiry).", Order = 1, GroupName = "Execution")]
		public string ExecutionInstrument { get; set; }

		[NinjaScriptProperty]
		[Range(1, 60)]
		[Display(Name = "Min stop (pts)", Order = 2, GroupName = "Execution")]
		public double MinStopPts { get; set; }

		[NinjaScriptProperty]
		[Range(3, 50)]
		[Display(Name = "Stop lookback (bars)", Description = "Stop goes 1 tick past the extreme of this many bars, rounded up to 5-pt steps.", Order = 3, GroupName = "Execution")]
		public int StopLookbackBars { get; set; }

		[NinjaScriptProperty]
		[Range(15, 200)]
		[Display(Name = "Max stop (pts)", Description = "Ceiling: wider stops are blocked.", Order = 4, GroupName = "Execution")]
		public double MaxStopPts { get; set; }

		[NinjaScriptProperty]
		[Range(15, 300)]
		[Display(Name = "High-vol max stop (pts)", Description = "Ceiling when volatility is HIGH (1 MNQ only then).", Order = 7, GroupName = "Execution")]
		public double HighVolMaxStopPts { get; set; }

		[NinjaScriptProperty]
		[Range(1, 1000)]
		[Display(Name = "High-vol candle (pts)", Description = "Largest 1-min candle over the last 2 hours at or above this = HIGH volatility.", Order = 8, GroupName = "Execution")]
		public double HighVolCandlePts { get; set; }

		[NinjaScriptProperty]
		[Range(0, 50)]
		[Display(Name = "Vol stop floor (% of 10-day range)", Description = "Stops never tighter than this % of the 10-day average RTH range. 0 = off.", Order = 9, GroupName = "Execution")]
		public double VolStopPct { get; set; }

		[NinjaScriptProperty]
		[Range(0, 100000)]
		[Display(Name = "Max risk per trade ($)", Description = "Default $170 (2 MNQ x 40 pts = $166). 0 = daily loss limit / max losses. Always capped by what is left of today's loss limit. Keep at or under the firm / TradeSpacePro per-trade cap.", Order = 5, GroupName = "Execution")]
		public double MaxRiskPerTradeUsd { get; set; }

		[NinjaScriptProperty]
		[Range(0, 50)]
		[Display(Name = "Cost per contract ($)", Description = "Commission + slippage per MNQ round trip, counted in the $ risk.", Order = 6, GroupName = "Execution")]
		public double CostPerContractUsd { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Starting balance ($)", Description = "Account balance at the start; ladder P&L = net liquidation minus this.", Order = 1, GroupName = "Size ladder")]
		public double StartingBalance { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "2 MNQ at P&L ($)", Order = 2, GroupName = "Size ladder")]
		public double LadderUpAt { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Back to 1 MNQ below P&L ($)", Order = 3, GroupName = "Size ladder")]
		public double LadderDownBelow { get; set; }

		[NinjaScriptProperty]
		[Range(1, 20)]
		[Display(Name = "Max trades per day", Order = 1, GroupName = "Daily limits")]
		public int MaxTradesPerDay { get; set; }

		[NinjaScriptProperty]
		[Range(1, 20)]
		[Display(Name = "Max losses per day", Order = 2, GroupName = "Daily limits")]
		public int MaxLossesPerDay { get; set; }

		[NinjaScriptProperty]
		[Range(0, 100000)]
		[Display(Name = "Daily loss limit ($)", Description = "0 = off. Each trade is sized to fit what is left of this today.", Order = 3, GroupName = "Daily limits")]
		public double DailyLossUsd { get; set; }

		[NinjaScriptProperty]
		[Range(0, 240)]
		[Display(Name = "Same-side lockout after a loss (min)", Order = 4, GroupName = "Daily limits")]
		public int SameSideLockoutMin { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show trade plan panel", Order = 1, GroupName = "Display")]
		public bool ShowPlan { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Write journal", Description = "Append each closed trade to analysis\\playbooks\\reset_journal.csv.", Order = 2, GroupName = "Display")]
		public bool WriteJournal { get; set; }

		[Display(Name = "Start collapsed", Description = "Show only the one-line EXEC header until you click it.", Order = 3, GroupName = "Display")]
		public bool StartCollapsed { get; set; }

		[NinjaScriptProperty]
		[Range(0, 5)]
		[Display(Name = "Runner to breakeven at (R)", Description = "2 MNQ only: move the runner's stop to entry + 1 tick once price reaches this many R. 0 = never. Backtest: no difference in average R (it trades fewer full losses for fewer 3R runners).", Order = 1, GroupName = "Management")]
		public double RunnerBreakevenR { get; set; }
		#endregion
	}
}
