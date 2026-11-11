#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

// Trade-plan dashboard for the "Reset at a Level" playbook (analysis\playbooks\reset-at-level.md).
// Top-left panel: market-profile context (prior value, held outside value, day type), ICT liquidity above / below
// (PDH/PDL, ONH/ONL, IB, hourly swing and equal highs/lows, swept or not), the draw, nearest level, ZCubed reset /
// trigger, the stop a trade would need right now with its ATM template, and the size from David's P&L ladder.
// Also draws the levels on this chart. A dropdown switches the view: Full, Context (David's read: trend, range,
// value, buy-side / sell-side, draw), Execution (level, trigger, stop, gates, size) or Hidden.
// Display only: no orders, no files. All session times are New York.
namespace NinjaTrader.NinjaScript.Indicators
{
	public enum PlanView { Full, Context, Execution, Hidden }

	public class TradePlanPanel : Indicator
	{
		private class Level { public string Name; public double Price; public int Kind; }   // Kind: 0 liquidity, 1 profile

		private TimeZoneInfo eastern;
		private NinjaScriptBase zc;
		private Account account;

		// current RTH day and the last completed one
		private DateTime rthDate = DateTime.MinValue;
		private Dictionary<int, double> dayVol = new Dictionary<int, double>();
		private double dayHi, dayLo, dayClose, dayOpen;
		private bool haveCur, havePrior;
		private double pdh, pdl, pdc, vah, val, poc, tpoc = double.NaN;
		// time POC (TPO style): price touched in the most 30-min periods; ties -> nearest the middle of the range
		private readonly Dictionary<int, int> tpoCount = new Dictionary<int, int>();
		private int tpoBracket = -1; private double bHi, bLo;
		private readonly List<double> nakedTpocs = new List<double>();
		private readonly List<double> ranges = new List<double>();        // completed RTH ranges, newest last
		private readonly List<double> openRanges = new List<double>();    // completed first-30-min ranges
		private readonly List<KeyValuePair<DateTime, double>> nakedPocs = new List<KeyValuePair<DateTime, double>>();

		// overnight, IB, first 30 minutes
		private double onh = double.NaN, onl = double.NaN, onhFinal = double.NaN, onlFinal = double.NaN;
		private double ibh = double.NaN, ibl = double.NaN; private bool ibDone;
		private double o30Hi, o30Lo, o30Close; private int o30HiIdx, o30LoIdx, o30Count; private string dayType = "-"; private int dayDir;
		private bool sweptPdh, sweptPdl, sweptOnh, sweptOnl;

		// hourly swings (liquidity)
		private DateTime hourKey = DateTime.MinValue;
		private readonly List<double[]> hourly = new List<double[]>();    // {high, low}
		private readonly List<double> swingHighs = new List<double>(), swingLows = new List<double>();   // unswept
		private readonly List<double> lastHighs = new List<double>(), lastLows = new List<double>();     // last confirmed, swept or not
		private readonly List<double> dayHighs = new List<double>(), dayLows = new List<double>();       // completed RTH days

		// traps: a push >= 2 pts through a liquidity level, then a close back inside within 5 minutes
		private class Watch { public string Name; public double P; public int Side; public DateTime Start; }
		private readonly List<Watch> watches = new List<Watch>();
		private readonly HashSet<string> trapped = new HashSet<string>();
		private string trapName = ""; private double trapLevel; private int trapSide; private DateTime trapTime = DateTime.MinValue;

		// held outside value
		private DateTime aboveSince = DateTime.MinValue, belowSince = DateTime.MinValue;

		// ZCubed reset / trigger memory
		private readonly List<bool> resetUp = new List<bool>(), resetDn = new List<bool>();
		private int ladderSize = 1;

		// volatility: 1-min ranges built from the chart's bars, last 120 minutes (largest-candle read, research\2026-11-08_largest_candle.md)
		private readonly Queue<KeyValuePair<DateTime, double>> minuteRanges = new Queue<KeyValuePair<DateTime, double>>();
		private DateTime minuteKey = DateTime.MinValue;
		private double minHi, minLo;
		private volatile bool highVol;
		private double lastLc, lastR10 = 300;

		private volatile string[] lines = new string[0];
		private volatile int[] colors = new int[0];   // 0 white, 1 green, 2 red, 3 grey, 4 yellow
		private volatile int[] rowKind = new int[0];   // 0 context, 1 execution, 2 coach (every view)
		private ComboBox viewBox;
		private StackPanel uiRow;
		private Button speakBtn, alertBtn;
		private const string SpeakLabel = "🔊 Plan", AlertsOnLabel = "🔔 On", AlertsOffLabel = "🔕 Off";
		private object voice;   // SAPI.SpVoice
		private System.Windows.Threading.DispatcherTimer speechTimer;
		private volatile string riskText = "";
		private volatile string[] spoken = new string[0];   // per row: what 🔊 Plan says instead of the row text (null = the row text)
		private ChartControl chartControlRef;
		private volatile Level[] drawLevels = new Level[0];

		private SharpDX.Direct2D1.Brush dxWhite, dxGreen, dxRed, dxGrey, dxYellow, dxBack, dxLiq, dxProf;
		private SharpDX.DirectWrite.TextFormat textFormat, smallFormat;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description = "Reset-at-a-Level trade plan: profile context, ICT liquidity, ZCubed trigger, stop/ATM and ladder size. Display only.";
				Name = "TradePlanPanel";
				Calculate = Calculate.OnBarClose;
				IsOverlay = true;
				DisplayInDataBox = false;
				PaintPriceMarkers = false;
				IsSuspendedWhileInactive = true;

				LevelTolPct = 5;
				EqualTolPts = 3;
				HeldMinutes = 30;
				ShowLevels = true;
				FontSize = 11;
				AccountName = "Playback101";
				StartingBalance = 100000;
				LadderUpAt = 1000;
				LadderDownBelow = 700;
				View = PlanView.Full;
				ShowSessionWindow = false;
				ShowCoach = true;
				HighVolCandlePts = 70;
				LevelFontSize = 14;
				VolStopPct = 10;
				ShowLabel = false;
				SpeechRate = 1;
				VoiceAlerts = true;
				AlertCooldownSec = 120;
				HighAlertVoice = "Microsoft Zira Desktop";
				PmarpMinutes = 2;
				PmarpVoice = true;
				ZcAllResets = true;
				ContinuationMin = 10;
				PmarpTurnPts = 8;
			}
			else if (State == State.DataLoaded)
			{
				eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
				zc = ZCubed(6, 6, 3, 8, 6, 3, 6, 14, 80, 20, 25, 25, 10);
				lock (Account.All)
					account = Account.All.FirstOrDefault(a => a.Name == AccountName);
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
				DisposeResources();
			}
		}

		// Each bar is processed once, after it closes. Hosted by PlaybookExecutor (Calculate on price change) this runs on
		// every tick live, so the closed bar is [1] on the first tick of the next bar; on its own (on bar close) it is [0].
		private int b, lastDoneBar = -1;
		private bool sawRepeat;

		protected override void OnBarUpdate()
		{
			if (State == State.Realtime && b == 0 && CurrentBar == lastDoneBar) sawRepeat = true;   // called per tick despite the setting
			b = State == State.Realtime && (Calculate != Calculate.OnBarClose || sawRepeat) ? 1 : 0;
			if (b == 1 && !IsFirstTickOfBar) return;
			if (CurrentBar < b || CurrentBar - b <= lastDoneBar) return;
			lastDoneBar = CurrentBar - b;

			DateTime et = TimeZoneInfo.ConvertTime(Time[b], Core.Globals.GeneralOptions.TimeZoneInfo, eastern);
			TimeSpan tod = et.TimeOfDay;
			DateTime prevEt = CurrentBar > b ? TimeZoneInfo.ConvertTime(Time[b + 1], Core.Globals.GeneralOptions.TimeZoneInfo, eastern) : et;
			bool rth = tod > new TimeSpan(9, 30, 0) && tod <= new TimeSpan(16, 0, 0);

			// new Globex session: reset the overnight range
			if (CurrentBar == b || (prevEt.TimeOfDay <= new TimeSpan(18, 0, 0) && tod > new TimeSpan(18, 0, 0)))
			{
				onh = High[b]; onl = Low[b];
			}
			else if (!rth && (tod > new TimeSpan(18, 0, 0) || tod <= new TimeSpan(9, 30, 0)))
			{
				onh = Math.Max(onh, High[b]); onl = Math.Min(onl, Low[b]);
			}

			if (rth)
			{
				if (et.Date != rthDate)
					StartRthDay(et.Date);
				AddToDay(et);
			}
			if (rth && havePrior) DetectTraps(et);
			UpdateHourly(et);
			UpdateSwept();
			UpdateZc();
			UpdateMinuteRanges();
			UpdatePmarp();

			if (havePrior && (State == State.Realtime || CurrentBar >= Count - 2 - b))
				Compose(et, rth);
		}

		#region Session bookkeeping
		private void StartRthDay(DateTime date)
		{
			if (haveCur)
			{
				pdh = dayHi; pdl = dayLo; pdc = dayClose;
				ComputeValueArea();
				if (havePrior) nakedPocs.Add(new KeyValuePair<DateTime, double>(rthDate, poc));
				if (havePrior && !double.IsNaN(tpoc)) nakedTpocs.Add(tpoc);
				FlushBracket(); tpoc = TimePoc();
				ranges.Add(dayHi - dayLo); if (ranges.Count > 10) ranges.RemoveAt(0);
				dayHighs.Add(dayHi); dayLows.Add(dayLo); if (dayHighs.Count > 3) { dayHighs.RemoveAt(0); dayLows.RemoveAt(0); }
				if (o30Count > 0) { openRanges.Add(o30Hi - o30Lo); if (openRanges.Count > 10) openRanges.RemoveAt(0); }
				havePrior = true;
			}
			rthDate = date; haveCur = true;
			dayVol.Clear(); dayHi = High[b]; dayLo = Low[b]; dayOpen = Open[b];
			tpoCount.Clear(); tpoBracket = -1;
			while (nakedTpocs.Count > 10) nakedTpocs.RemoveAt(0);
			onhFinal = onh; onlFinal = onl;
			ibh = High[b]; ibl = Low[b]; ibDone = false;
			o30Count = 0; dayType = "-"; dayDir = 0;
			sweptPdh = sweptPdl = sweptOnh = sweptOnl = false;
			aboveSince = belowSince = DateTime.MinValue;
			while (nakedPocs.Count > 10) nakedPocs.RemoveAt(0);
			watches.Clear(); trapped.Clear();
		}

		private void AddToDay(DateTime et)
		{
			dayHi = Math.Max(dayHi, High[b]); dayLo = Math.Min(dayLo, Low[b]); dayClose = Close[b];
			int lo = (int)Math.Floor(Low[b]), hi = (int)Math.Floor(High[b]);
			double share = Volume[b] / (hi - lo + 1);
			for (int p = lo; p <= hi; p++)
			{
				double v; dayVol.TryGetValue(p, out v); dayVol[p] = v + share;
			}
			TimeSpan tod = et.TimeOfDay;
			int br = (int)((tod.TotalMinutes - 570 - 0.001) / 30);   // 30-min periods from 09:30 ET
			if (br != tpoBracket) { FlushBracket(); tpoBracket = br; bHi = High[b]; bLo = Low[b]; }
			else { bHi = Math.Max(bHi, High[b]); bLo = Math.Min(bLo, Low[b]); }
			if (tod <= new TimeSpan(10, 30, 0)) { ibh = Math.Max(ibh, High[b]); ibl = Math.Min(ibl, Low[b]); }
			else ibDone = true;

			if (tod <= new TimeSpan(10, 0, 0))
			{
				if (o30Count == 0) { o30Hi = High[b]; o30Lo = Low[b]; o30HiIdx = o30LoIdx = 0; }
				if (High[b] > o30Hi) { o30Hi = High[b]; o30HiIdx = o30Count; }
				if (Low[b] < o30Lo) { o30Lo = Low[b]; o30LoIdx = o30Count; }
				o30Close = Close[b]; o30Count++;
			}
			else if (dayType == "-" && o30Count > 0)
				ClassifyOpen();

			nakedTpocs.RemoveAll(p => Low[b] <= p && High[b] >= p);
			for (int i = nakedPocs.Count - 1; i >= 0; i--)
				if (Low[b] <= nakedPocs[i].Value && High[b] >= nakedPocs[i].Value) nakedPocs.RemoveAt(i);

			// held outside prior value: start of the current unbroken run of closes beyond VAH / VAL
			if (havePrior)
			{
				if (Close[b] > vah) { if (aboveSince == DateTime.MinValue) aboveSince = et; } else aboveSince = DateTime.MinValue;
				if (Close[b] < val) { if (belowSince == DateTime.MinValue) belowSince = et; } else belowSince = DateTime.MinValue;
			}
		}

		// Dalton open types from the first 30 minutes (same rules as research\scripts\daytype_levels_traps.py)
		private void ClassifyOpen()
		{
			double r = o30Hi - o30Lo;
			if (r <= 0) { dayType = "OA"; return; }
			double po = (dayOpen - o30Lo) / r, pc = (o30Close - o30Lo) / r;
			bool hiFirst = o30HiIdx < o30LoIdx;
			bool big = openRanges.Count > 0 && r >= openRanges.Average();
			if (big && po <= 0.1 && pc >= 0.75) { dayType = "OD"; dayDir = 1; }
			else if (big && po >= 0.9 && pc <= 0.25) { dayType = "OD"; dayDir = -1; }
			else if (po > 0.1 && po < 0.9 && pc >= 0.75 && !hiFirst) { dayType = "OTD"; dayDir = 1; }
			else if (po > 0.1 && po < 0.9 && pc <= 0.25 && hiFirst) { dayType = "OTD"; dayDir = -1; }
			else if (po >= 0.7 && hiFirst && pc < 0.5) { dayType = "ORR"; dayDir = -1; }
			else if (po <= 0.3 && !hiFirst && pc > 0.5) { dayType = "ORR"; dayDir = 1; }
			else { dayType = "OA"; dayDir = 0; }
		}

		private void FlushBracket()
		{
			if (tpoBracket < 0) return;
			for (int p = (int)Math.Floor(bLo); p <= (int)Math.Floor(bHi); p++)
			{
				int n; tpoCount.TryGetValue(p, out n); tpoCount[p] = n + 1;
			}
			tpoBracket = -1;
		}

		private double TimePoc()
		{
			if (tpoCount.Count == 0) return double.NaN;
			int max = tpoCount.Values.Max();
			double mid = (dayHi + dayLo) / 2;
			return tpoCount.Where(kv => kv.Value == max).Select(kv => kv.Key + 0.5).OrderBy(p => Math.Abs(p - mid)).First();
		}

		private void ComputeValueArea()
		{
			if (dayVol.Count == 0) return;
			int[] prices = dayVol.Keys.OrderBy(p => p).ToArray();
			double total = dayVol.Values.Sum();
			int i = 0;
			for (int k = 1; k < prices.Length; k++) if (dayVol[prices[k]] > dayVol[prices[i]]) i = k;
			int j = i; double got = dayVol[prices[i]];
			poc = prices[i] + 0.5;
			while (got < 0.7 * total)
			{
				double up = j + 1 < prices.Length ? dayVol[prices[j + 1]] : -1, dn = i > 0 ? dayVol[prices[i - 1]] : -1;
				if (up < 0 && dn < 0) break;
				if (up >= dn) { j++; got += up; } else { i--; got += dn; }
			}
			vah = prices[j] + 1; val = prices[i];
		}

		// Hourly bars (New York hour) and confirmed swing highs / lows, 2 bars each side, unswept, last 5 days
		private void UpdateHourly(DateTime et)
		{
			DateTime key = new DateTime(et.Year, et.Month, et.Day, et.Hour, 0, 0);
			if (et.Minute == 0 && et.Second == 0) key = key.AddHours(-1);   // a bar ending on the hour belongs to that hour
			if (key != hourKey)
			{
				hourKey = key;
				int n = hourly.Count;
				if (n >= 5)
				{
					int k = n - 3;
					double[] c = hourly[k];
					if (c[0] > hourly[k - 1][0] && c[0] > hourly[k - 2][0] && c[0] >= hourly[k + 1][0] && c[0] >= hourly[k + 2][0]) { swingHighs.Add(c[0]); lastHighs.Add(c[0]); if (lastHighs.Count > 2) lastHighs.RemoveAt(0); }
					if (c[1] < hourly[k - 1][1] && c[1] < hourly[k - 2][1] && c[1] <= hourly[k + 1][1] && c[1] <= hourly[k + 2][1]) { swingLows.Add(c[1]); lastLows.Add(c[1]); if (lastLows.Count > 2) lastLows.RemoveAt(0); }
				}
				hourly.Add(new[] { High[b], Low[b] });
				if (hourly.Count > 120) hourly.RemoveAt(0);
				while (swingHighs.Count > 12) swingHighs.RemoveAt(0);
				while (swingLows.Count > 12) swingLows.RemoveAt(0);
			}
			else
			{
				double[] h = hourly[hourly.Count - 1];
				h[0] = Math.Max(h[0], High[b]); h[1] = Math.Min(h[1], Low[b]);
			}
			swingHighs.RemoveAll(p => High[b] > p);
			swingLows.RemoveAll(p => Low[b] < p);
		}

		private void DetectTraps(DateTime et)
		{
			const double sweep = 2.0;
			var lv = new List<KeyValuePair<string, double>> { new KeyValuePair<string, double>("PDH", pdh), new KeyValuePair<string, double>("PDL", pdl) };
			if (!double.IsNaN(onhFinal)) { lv.Add(new KeyValuePair<string, double>("ONH", onhFinal)); lv.Add(new KeyValuePair<string, double>("ONL", onlFinal)); }
			if (ibDone) { lv.Add(new KeyValuePair<string, double>("IBH", ibh)); lv.Add(new KeyValuePair<string, double>("IBL", ibl)); }
			foreach (double h in swingHighs) lv.Add(new KeyValuePair<string, double>("1h high", h));
			foreach (double l in swingLows) lv.Add(new KeyValuePair<string, double>("1h low", l));
			double prevClose = Close[b + 1];
			foreach (var kv in lv)
			{
				string key = kv.Key + kv.Value.ToString(CultureInfo.InvariantCulture);
				if (trapped.Contains(key) || watches.Any(w => w.Name == kv.Key && w.P == kv.Value)) continue;
				if (prevClose < kv.Value && High[b] >= kv.Value + sweep) watches.Add(new Watch { Name = kv.Key, P = kv.Value, Side = 1, Start = et });
				else if (prevClose > kv.Value && Low[b] <= kv.Value - sweep) watches.Add(new Watch { Name = kv.Key, P = kv.Value, Side = -1, Start = et });
			}
			for (int i = watches.Count - 1; i >= 0; i--)
			{
				Watch w = watches[i];
				bool back = w.Side > 0 ? Close[b] < w.P : Close[b] > w.P;
				if (back && (et - w.Start).TotalMinutes <= 5)
				{
					trapped.Add(w.Name + w.P.ToString(CultureInfo.InvariantCulture));
					trapName = w.Name; trapLevel = w.P; trapSide = w.Side; trapTime = et;
					if (ShowLevels)
					{
						string tag = "TPP_trap" + CurrentBar;
						if (w.Side > 0) Draw.ArrowDown(this, tag, true, b, High[b] + 4 * TickSize, Brushes.Orange);
						else Draw.ArrowUp(this, tag, true, b, Low[b] - 4 * TickSize, Brushes.Orange);
						Draw.Text(this, tag + "t", "trap " + w.Name, b, w.Side > 0 ? High[b] + 12 * TickSize : Low[b] - 12 * TickSize, Brushes.Orange);
					}
					watches.RemoveAt(i);
				}
				else if ((et - w.Start).TotalMinutes > 5)
					watches.RemoveAt(i);   // held beyond: a breakout, not a trap
			}
		}

		// 1-min high-low from the chart's bars (bar close time decides the minute); keeps the last 120 minutes
		private void UpdateMinuteRanges()
		{
			DateTime key = new DateTime(Time[b].Year, Time[b].Month, Time[b].Day, Time[b].Hour, Time[b].Minute, 0);
			if (key != minuteKey)
			{
				if (minuteKey != DateTime.MinValue) minuteRanges.Enqueue(new KeyValuePair<DateTime, double>(minuteKey, minHi - minLo));
				minuteKey = key; minHi = High[b]; minLo = Low[b];
				while (minuteRanges.Count > 0 && (key - minuteRanges.Peek().Key).TotalMinutes > 120) minuteRanges.Dequeue();
			}
			else { minHi = Math.Max(minHi, High[b]); minLo = Math.Min(minLo, Low[b]); }
		}

		private double LargestCandle()
		{
			double m = minuteKey == DateTime.MinValue ? 0 : minHi - minLo;
			foreach (var kv in minuteRanges) m = Math.Max(m, kv.Value);
			return m;
		}

		private void UpdateSwept()
		{
			if (!havePrior || !haveCur) return;
			if (High[b] > pdh) sweptPdh = true;
			if (Low[b] < pdl) sweptPdl = true;
			if (!double.IsNaN(onhFinal) && High[b] > onhFinal) sweptOnh = true;
			if (!double.IsNaN(onlFinal) && Low[b] < onlFinal) sweptOnl = true;
		}

		// ZCubed reset: fast / med / slow %K all <= 20 (>= 80) and within 15 of each other on this bar
		private double Zv(int plot, int ago) { return zc.Values[plot][ago + b]; }
		// PMARP on PmarpMinutes bars (default 2) built from this chart's bars, same formula as PMARP_V2 (research corr 1.000):
		// PMAR = close / VWMA(20); PMARP = % of the previous 350 PMAR values <= now; signal = SMA(PMARP, 20).
		// Voice: slope turn (3-bar change past +/-5 -> up / down, else flat) = warning; signal-line cross = high alert.
		private long pmKey = -1;
		private double pmC, pmV;
		private readonly Queue<KeyValuePair<double, double>> pmBars = new Queue<KeyValuePair<double, double>>();   // close, volume
		private readonly Queue<double> pmarHist = new Queue<double>();
		private readonly List<double> pmarp = new List<double>();
		private int pmState, pmLastDir;
		private double pmRunExt;
		private int pmTurnBars;
		private volatile string pmText = "";

		private void UpdatePmarp()
		{
			long key = Time[b].AddTicks(-1).Ticks / TimeSpan.FromMinutes(PmarpMinutes).Ticks;   // a bar stamped 10:02:00 belongs to 10:00-10:02
			if (key != pmKey)
			{
				if (pmKey >= 0) ClosePmarpBar();
				pmKey = key; pmV = 0;
			}
			pmC = Close[b]; pmV += Volume[b];
		}

		private void ClosePmarpBar()
		{
			pmBars.Enqueue(new KeyValuePair<double, double>(pmC, pmV));
			if (pmBars.Count > 20) pmBars.Dequeue();
			double sv = pmBars.Sum(b => b.Value);
			if (pmBars.Count < 20 || sv <= 0) return;
			double x = pmC / (pmBars.Sum(b => b.Key * b.Value) / sv);
			if (pmarHist.Count == 350)
			{
				pmarp.Add(100.0 * pmarHist.Count(q => q <= x) / 350);
				if (pmarp.Count > 40) pmarp.RemoveAt(0);
				OnPmarp();
			}
			pmarHist.Enqueue(x);
			if (pmarHist.Count > 350) pmarHist.Dequeue();
		}

		private void OnPmarp()
		{
			int n = pmarp.Count;
			if (n < 21) return;
			Func<int, double> sig = i => pmarp.Skip(i - 19).Take(20).Average();
			double pm = pmarp[n - 1], pm1 = pmarp[n - 2], s0 = sig(n - 1), s1 = sig(n - 2), slope = pm - pmarp[n - 4];
			// A turn needs PmarpTurnPts off the run's low (high) on 2 closes in a row, so a one-bar pause near 90+ (where PMARP
			// moves in big steps) isn't called a reversal. Inside a run the opposite slope only counts as flat; otherwise the 3-bar slope decides.
			bool offUp = pmLastDir < 0 && pm - pmRunExt >= PmarpTurnPts, offDn = pmLastDir > 0 && pmRunExt - pm >= PmarpTurnPts;
			pmTurnBars = offUp || offDn ? pmTurnBars + 1 : 0;
			int slopeState = slope > 5 ? 1 : slope < -5 ? -1 : 0;
			int state = pmLastDir > 0 ? (offDn && pmTurnBars >= 2 ? -1 : Math.Max(0, slopeState))
				: pmLastDir < 0 ? (offUp && pmTurnBars >= 2 ? 1 : Math.Min(0, slopeState)) : slopeState;
			int cross = pm > s0 && pm1 <= s1 ? 1 : pm < s0 && pm1 >= s1 ? -1 : 0;
			double turnedFrom = pmRunExt;
			if (state != 0 && state != pmLastDir) pmRunExt = pm;
			else if (pmLastDir > 0) pmRunExt = Math.Max(pmRunExt, pm);
			else if (pmLastDir < 0) pmRunExt = Math.Min(pmRunExt, pm);
			pmText = string.Format("{0}m {1:0} (signal {2:0}) {3}", PmarpMinutes, pm, s0, state > 0 ? "rising" : state < 0 ? "falling" : "flat");
			bool live = State == State.Realtime && PmarpVoice;
			string tf = "P MARP " + PmarpMinutes + " minute";
			DateTime now = Time[b];
			if (state != pmState) { pmRunSince = now; pmExtremeSaid = false; }   // a new run: continuation timer restarts
			if (live && cross != 0 && Fresh("pm cross " + cross))
				Announce(string.Format("High alert. {0} crossed {1} its signal line, at {2:0}.", tf, cross > 0 ? "above" : "below", pm), true);
			else if (live && state == 0 && pmState != 0 && Fresh("pm flat " + pmState))
				Announce(string.Format("Warning. {0} flattening after a {1}, at {2:0}.", tf, pmState > 0 ? "rise" : "drop", pm));
			else if (live && state != 0 && pmLastDir != 0 && state != pmLastDir && Fresh("pm turn " + state))
				Announce(string.Format("Warning. {0} turning {1} from {2:0}, now {3:0}.", tf, state > 0 ? "up" : "down", turnedFrom, pm));
			else if (live && state != 0 && state == pmState)
			{
				// continuation: the move is holding. Once when it reaches the extreme, then every ContinuationMin minutes.
				bool extreme = state > 0 ? pm >= 80 : pm <= 20;
				if (extreme && !pmExtremeSaid)
				{
					pmExtremeSaid = true; pmRunSince = now;
					Announce(string.Format("{0} still {1}, {2} {3}. Strong move.", tf, state > 0 ? "rising" : "falling", state > 0 ? "above" : "below", state > 0 ? 80 : 20));
				}
				else if (ContinuationMin > 0 && (now - pmRunSince).TotalMinutes >= ContinuationMin)
				{
					pmRunSince = now;
					Announce(string.Format("{0} still {1}, now {2:0}.", tf, state > 0 ? "rising" : "falling", pm));
				}
			}
			pmState = state;
			if (state != 0) pmLastDir = state;
		}

		private DateTime pmRunSince = DateTime.MinValue;
		private bool pmExtremeSaid;

		private void UpdateZc()
		{
			zc.Update();
			if (CurrentBar < b + 2) { resetUp.Add(false); resetDn.Add(false); return; }
			double f = Zv(0, 0), m = Zv(1, 0), s = Zv(3, 0);
			double mx = Math.Max(f, Math.Max(m, s)), mn = Math.Min(f, Math.Min(m, s));
			resetUp.Add(mx <= 20 && mx - mn <= 15);
			resetDn.Add(mn >= 80 && mx - mn <= 15);
			if (resetUp.Count > 10) { resetUp.RemoveAt(0); resetDn.RemoveAt(0); }
		}
		#endregion

		#region Panel text
		private void Compose(DateTime et, bool rth)
		{
			var L = new List<string>(); var C = new List<int>(); var X = new List<int>(); var S = new List<string>();
			bool ctxRow = true;   // rows before the LEVEL row are context, the rest execution
			Action<string, int> add = (s, c) => { L.Add(s); C.Add(c); X.Add(ctxRow ? 0 : 1); S.Add(null); };
			Action<string> spokenAs = s => S[S.Count - 1] = s;   // what 🔊 Plan says for the row just added (null = the row text)
			CultureInfo ci = CultureInfo.InvariantCulture;
			Func<double, string> px = p => p.ToString("0.##", ci);
			double close = Close[b];
			// median of the last 10 RTH ranges: one freak day (e.g. 1,510 pts on 2026-11-05) can't inflate it
			double r10 = ranges.Count > 0 ? ranges.OrderBy(x => x).ElementAt(ranges.Count / 2) : 300;
			double tol = r10 * LevelTolPct / 100.0;
			DateTime pt = TimeZoneInfo.ConvertTime(et, eastern, TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time"));

			// trend (David: higher-timeframe direction first) and range
			int dTrend = Structure(dayHighs, dayLows), hTrend = Structure(lastHighs, lastLows);
			Func<int, string> tw = t => t > 0 ? "up (HH+HL)" : t < 0 ? "down (LH+LL)" : "range";
			string align = dTrend != 0 && dTrend == hTrend ? (dTrend > 0 ? "ALIGNED UP" : "ALIGNED DOWN") : dTrend != 0 && hTrend != 0 ? "MIXED: wait for clarity" : "unclear";
			add(string.Format("TREND     daily {0} | 1h {1} -> {2}", tw(dTrend), tw(hTrend), align), dTrend != 0 && dTrend == hTrend ? (dTrend > 0 ? 1 : 2) : 3);
			if (rth)
			{
				double rng = dayHi - dayLo, at = rng > 0 ? 100 * (close - dayLo) / rng : 50;
				string ibTxt = ibDone ? string.Format("   IB {0:0}", ibh - ibl) : "";
				add(string.Format("RANGE     today {0:0} pts = {1:0}% of 10d median {2:0}   price at {3:0}% of day{4}", rng, 100 * rng / r10, r10, at, ibTxt), rng >= r10 ? 4 : 0);
			}
			double lc = LargestCandle(), volFloor = VolStopPct > 0 ? r10 * VolStopPct / 100.0 : 0;
			// high volatility: a big 1-min candle in the last 2h, or the normal-noise floor itself is wider than the 40-pt ceiling
			highVol = lc >= HighVolCandlePts || volFloor > 40; lastLc = lc; lastR10 = r10;
			add(string.Format("VOL       largest 1-min candle (2h) {0:0} pts | stop floor {1:0} pts ({2:0}% of 10d range){3}", lc, Math.Max(15, volFloor), VolStopPct,
				highVol ? string.Format("  HIGH ({0}): 1 MNQ only, stops up to {1:0}", lc >= HighVolCandlePts ? "big candle" : "wide days", HighVolCeiling(volFloor)) : ""), highVol ? 4 : 0);
			// context
			int heldAbove = aboveSince == DateTime.MinValue ? 0 : (int)(et - aboveSince).TotalMinutes;
			int heldBelow = belowSince == DateTime.MinValue ? 0 : (int)(et - belowSince).TotalMinutes;
			int bias = !rth ? 0 : heldAbove >= HeldMinutes ? 1 : heldBelow >= HeldMinutes ? -1 : 0;
			string where = close > vah ? "above value" : close < val ? "below value" : "inside value";
			int held = Math.Max(heldAbove, heldBelow);
			string biasTxt = bias > 0 ? "LONG bias" : bias < 0 ? "SHORT bias" : "no bias yet";
			add(string.Format("CONTEXT  {0}{1} -> {2}   Day type: {3}{4}", where, held > 0 ? " " + held + "m" : "", biasTxt,
				dayType, dayDir > 0 ? " up" : dayDir < 0 ? " down" : ""), bias > 0 ? 1 : bias < 0 ? 2 : 3);
			string naked = nakedPocs.Count > 0 ? "   nPOC " + px(nakedPocs.OrderBy(k => Math.Abs(k.Value - close)).First().Value) : "";
			add(string.Format("VALUE    VAH {0}  POC {1}  tPOC {2}  VAL {3}{4}", px(vah), px(poc), double.IsNaN(tpoc) ? "-" : px(tpoc), px(val), naked), 0);

			// liquidity
			var liq = new List<Level>();
			liq.Add(new Level { Name = sweptPdh ? "PDH x" : "PDH", Price = pdh });
			liq.Add(new Level { Name = sweptPdl ? "PDL x" : "PDL", Price = pdl });
			if (!double.IsNaN(onhFinal) && rth) { liq.Add(new Level { Name = sweptOnh ? "ONH x" : "ONH", Price = onhFinal }); liq.Add(new Level { Name = sweptOnl ? "ONL x" : "ONL", Price = onlFinal }); }
			if (ibDone) { liq.Add(new Level { Name = "IBH", Price = ibh }); liq.Add(new Level { Name = "IBL", Price = ibl }); }
			foreach (double h in swingHighs) liq.Add(new Level { Name = IsEqual(h, swingHighs) ? "EQH" : "1h SH", Price = h });
			foreach (double l in swingLows) liq.Add(new Level { Name = IsEqual(l, swingLows) ? "EQL" : "1h SL", Price = l });
			Func<Level, string> fmt = lv => string.Format("{0} {1} ({2}{3:0})", lv.Name, px(lv.Price), lv.Price >= close ? "+" : "", lv.Price - close);
			var above = liq.Where(l => l.Price > close && !l.Name.EndsWith(" x")).OrderBy(l => l.Price).Take(3).ToList();
			var below = liq.Where(l => l.Price < close && !l.Name.EndsWith(" x")).OrderByDescending(l => l.Price).Take(3).ToList();
			add("BUY-SIDE  " + (above.Count > 0 ? string.Join("  ", above.Select(fmt)) : "none above"), 0);
			spokenAs(SpokenPools("Buy side", liq.Where(l => l.Price > close && !l.Name.EndsWith(" x")), close, "above"));
			add("SELL-SIDE " + (below.Count > 0 ? string.Join("  ", below.Select(fmt)) : "none below"), 0);
			spokenAs(SpokenPools("Sell side", liq.Where(l => l.Price < close && !l.Name.EndsWith(" x")), close, "below"));
			string swept = string.Join(" ", new[] { sweptPdh ? "PDH" : null, sweptPdl ? "PDL" : null, sweptOnh ? "ONH" : null, sweptOnl ? "ONL" : null }.Where(s => s != null));
			Level draw = bias > 0 ? above.FirstOrDefault() : bias < 0 ? below.FirstOrDefault() : null;
			add(string.Format("DRAW      {0}   swept today: {1}", draw != null ? fmt(draw) : "pick from the pools above / below", swept.Length > 0 ? swept : "none"), draw != null ? 4 : 3);
			spokenAs(string.Format("Draw: {0}. Swept today: {1}", draw != null ? Speakable(draw.Name) + " " + PtsAway(draw.Price - close) : "pick from the pools above or below",
				swept.Length > 0 ? string.Join(", ", swept.Split(' ').Select(Speakable)) : "none"));

			// location
			ctxRow = false;
			var all = new List<Level>(liq.Where(l => !l.Name.EndsWith(" x")));
			all.Add(new Level { Name = "VAH", Price = vah, Kind = 1 }); all.Add(new Level { Name = "VAL", Price = val, Kind = 1 });
			all.Add(new Level { Name = "POC", Price = poc, Kind = 1 }); all.Add(new Level { Name = "PDC", Price = pdc, Kind = 1 });
			if (!double.IsNaN(tpoc)) all.Add(new Level { Name = "tPOC", Price = tpoc, Kind = 1 });
			foreach (double t in nakedTpocs) all.Add(new Level { Name = "ntPOC", Price = t, Kind = 1 });
			foreach (var k in nakedPocs) all.Add(new Level { Name = "nPOC", Price = k.Value, Kind = 1 });
			Level near = all.OrderBy(l => Math.Abs(l.Price - close)).FirstOrDefault();
			bool atLevel = near != null && Math.Abs(near.Price - close) <= tol;
			int cluster = near == null ? 0 : all.Count(l => Math.Abs(l.Price - near.Price) <= 10);
			add(string.Format("LEVEL     nearest {0} ({1:0.0} pts){2}{3}", near != null ? near.Name + " " + px(near.Price) : "-",
				near != null ? Math.Abs(near.Price - close) : 0, atLevel ? "  AT LEVEL" : "", cluster >= 2 ? "  [" + cluster + " levels within 10]" : ""), atLevel ? 4 : 3);

			// trap (David: liquidity run against the higher-timeframe direction that fails = enter with the trend)
			int trend = dTrend != 0 && dTrend == hTrend ? dTrend : bias;
			if (trapTime != DateTime.MinValue && (et - trapTime).TotalMinutes <= 30)
			{
				int tradeDir = -trapSide;   // failed breakout up -> short, failed breakdown -> long
				bool withTrend = trend != 0 && tradeDir == trend;
				string kind = trapSide > 0 ? "failed breakout (bull trap) -> short" : "failed breakdown (bear trap) -> long";
				lastTrap = string.Format("{0} {1} {2} at {3}", trapName, px(trapLevel), trapSide > 0 ? "bull trap" : "bear trap", TimeZoneInfo.ConvertTime(trapTime, eastern, TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time")).ToString("H:mm", ci));
				add(string.Format("TRAP      {0} {1}: {2}, {3:0}m ago  {4}", trapName, px(trapLevel), kind, (et - trapTime).TotalMinutes,
					withTrend ? "WITH TREND (David)" : trend != 0 ? "against trend: careful" : "no trend read"), withTrend ? 1 : 4);
			}
			else { lastTrap = ""; add("TRAP      none in the last 30 min", 3); }

			// trigger
			double f = Zv(0, 0), m = Zv(1, 0), s = Zv(3, 0);
			bool armedUp = resetUp.Skip(Math.Max(0, resetUp.Count - 5)).Any(x => x), armedDn = resetDn.Skip(Math.Max(0, resetDn.Count - 5)).Any(x => x);
			bool turnUp = Zv(0, 0) > Zv(0, 1) && Zv(0, 1) <= Zv(0, 2), turnDn = Zv(0, 0) < Zv(0, 1) && Zv(0, 1) >= Zv(0, 2);
			string trig = armedUp && turnUp ? "TRIGGER LONG" : armedDn && turnDn ? "TRIGGER SHORT" : armedUp ? "reset low, wait for the turn up" : armedDn ? "reset high, wait for the turn down" : "no reset";
			int trigDir = armedUp && turnUp ? 1 : armedDn && turnDn ? -1 : 0;
			add(string.Format("ZCUBED    F {0:0} M {1:0} S {2:0}  {3}", f, m, s, trig), trigDir == 0 ? 3 : (trigDir == bias && atLevel ? 1 : 4));
			if (pmText.Length > 0)
			{
				add("PMARP     " + pmText, 3);
				spokenAs(string.Format("P MARP, {0} minute, {1}", PmarpMinutes, pmText.Substring(pmText.IndexOf(' ') + 1).Replace("(", ", ").Replace(")", ",")));
			}

			// stop and ATM if entered now in the bias (or trigger) direction
			int dir = trigDir != 0 ? trigDir : bias;
			if (dir != 0)
			{
				double ext = dir > 0 ? MIN(Low, 10)[b] - TickSize : MAX(High, 10)[b] + TickSize;
				double stop = Math.Max(Math.Max(15, VolStopPct > 0 ? lastR10 * VolStopPct / 100.0 : 0), Math.Abs(close - ext));
				int pts = (int)(Math.Ceiling(stop / 5.0) * 5);   // same rule as PlaybookExecutor's defaults; $ caps are applied there
				double ceiling = highVol ? HighVolCeiling(VolStopPct > 0 ? lastR10 * VolStopPct / 100.0 : 0) : 40;
				string tmpl = pts > ceiling ? "over " + ceiling + ": skip" : string.Format("{0} pts = ${1:0} per MNQ{2}", pts, pts * Instrument.MasterInstrument.PointValue / 10 + 3, highVol ? " (high vol: 1 MNQ)" : "");
				add(string.Format("STOP      {0} now: {1:0.0} pts (structure or vol floor) -> {2}", dir > 0 ? "long" : "short", stop, tmpl), pts > ceiling ? 2 : 0);
			}

			// session window (optional, off by default: the journal's entry times decide later) and the open-drive warning
			bool trendFight = dayType == "OD" && dir != 0 && dir != dayDir;
			if (ShowSessionWindow)
			{
				bool window = (pt.TimeOfDay >= new TimeSpan(6, 30, 0) && pt.TimeOfDay <= new TimeSpan(8, 30, 0)) || (pt.TimeOfDay >= new TimeSpan(10, 0, 0) && pt.TimeOfDay <= new TimeSpan(12, 30, 0));
				add(string.Format("SESSION   {0} PT {1}{2}", pt.ToString("H:mm", ci), window ? "in David's first 2h / PM window" : "outside 6:30-8:30 / 10:00-12:30", trendFight ? "   OD day: no countertrend" : ""), window && !trendFight ? 1 : 2);
			}
			else if (trendFight)
				add("DAY TYPE  open drive " + (dayDir > 0 ? "up" : "down") + ": no countertrend trades", 2);

			// size from David's ladder
			string sizeTxt = "account '" + AccountName + "' not found";
			if (account != null)
			{
				double pnl = account.Get(AccountItem.NetLiquidation, Currency.UsDollar) - StartingBalance;
				if (pnl >= LadderUpAt) ladderSize = 2; else if (pnl < LadderDownBelow) ladderSize = 1;
				sizeTxt = string.Format("{0} P&L {1}{2:N0} -> {3} MNQ ({4})", AccountName, pnl >= 0 ? "+$" : "-$", Math.Abs(pnl), ladderSize,
					ladderSize == 1 ? "2 at +$" + LadderUpAt.ToString("N0", ci) : "back to 1 below +$" + LadderDownBelow.ToString("N0", ci));
			}
			add("SIZE      " + sizeTxt, account != null ? 0 : 2);

			var lv = new List<Level>(all);
			lv.Add(new Level { Name = sweptPdh ? "PDH (swept)" : "PDH", Price = pdh });
			lv.Add(new Level { Name = sweptPdl ? "PDL (swept)" : "PDL", Price = pdl });
			drawLevels = lv.GroupBy(l => l.Name + l.Price).Select(g => g.First()).ToArray();
			bool trapLive = trapTime != DateTime.MinValue && (et - trapTime).TotalMinutes <= 30;
			string step = "";
			if (ShowCoach)
			{
				var coach = Coach(pt, rth, dTrend, hTrend, align, bias, held, atLevel, near, all, close, trigDir, trapLive && -trapSide == trend);
				for (int i = coach.Count - 1; i >= 0; i--) { L.Insert(0, coach[i].Key); C.Insert(0, coach[i].Value); X.Insert(0, 2); S.Insert(0, null); }
				if (coach.Count > 0) step = coach[0].Key;
			}
			if (State == State.Realtime)
			{
				CheckAlerts(step, bias, trapLive ? trapTime : DateTime.MinValue, -trapSide, trend != 0 && -trapSide == trend, near,
					highVol, HighVolCeiling(volFloor));
				int htf = dTrend != 0 && dTrend == hTrend ? dTrend : 0;
				ZcAlerts(f, m, s, Zv(0, 1), armedUp, armedDn, trigDir, htf != 0 ? htf : bias, atLevel, near);
			}
			spoken = S.ToArray(); rowKind = X.ToArray(); colors = C.ToArray(); lines = L.ToArray();
			lastBias = bias; lastDayType = dayType + (dayDir > 0 ? " up" : dayDir < 0 ? " down" : "");
			lastNear = near != null ? near.Name + " " + px(near.Price) : ""; lastZc = trig; lastContext = where + (held > 0 ? " " + held + "m" : "");
		}

		// Step-by-step guide for the playbook order (reset-at-level_workspace.md): which step you are on and what to do next
		private List<KeyValuePair<string, int>> Coach(DateTime pt, bool rth, int dTrend, int hTrend, string align, int bias, int held,
			bool atLevel, Level near, List<Level> all, double close, int trigDir, bool trapWithTrend)
		{
			var o = new List<KeyValuePair<string, int>>();
			Action<string, int> say = (t, c) => o.Add(new KeyValuePair<string, int>(t, c));
			TimeSpan t0 = pt.TimeOfDay;
			int htf = dTrend != 0 && dTrend == hTrend ? dTrend : 0;
			Func<int, string> side = d => d > 0 ? "LONG" : "SHORT";

			bool inTrade = false;
			if (account != null)
				lock (account.Positions)
					inTrade = account.Positions.Any(q => q.MarketPosition != MarketPosition.Flat && (q.Instrument.MasterInstrument.Name == Instrument.MasterInstrument.Name
						|| q.Instrument.MasterInstrument.Name == "M" + Instrument.MasterInstrument.Name));

			if (inTrade)
			{
				say("STEP 4  IN TRADE: hands off. Runner goes to BE by itself; TIGHTEN or TGT->LVL if needed, never widen.", 4);
				say("        FLAT only if ZCubed is at the opposite extreme and price stalls at a level, or it comes back hard.", 3);
			}
			else if (t0 < new TimeSpan(6, 30, 0) && t0 >= new TimeSpan(3, 0, 0))
			{
				say("STEP 1  PRE-OPEN (by 6:20): Daily: name the draw > 1h: heading to it? > WRITE direction + draw in the journal", 4);
				say("        then TPO levels > 5m FVGs.  Panel read: " + align + (htf == 0 ? " -> likely no trade unless it clears" : " -> look for " + side(htf) + "S"), htf != 0 ? 0 : 3);
			}
			else if (rth && t0 >= new TimeSpan(6, 30, 0) && t0 < new TimeSpan(7, 0, 0))
				say(string.Format("STEP 2  OPEN: watch only until 7:00. Held outside value {0}m of {1}. Day type shows at 7:00.", held, HeldMinutes), 4);
			else if (!rth || t0 >= new TimeSpan(13, 0, 0))
				say("DONE    session over: finish the journal notes and review the screenshots.", 3);
			else
			{
				int want = htf != 0 ? htf : bias;
				bool odAgainst = dayType == "OD" && want != 0 && want != dayDir;
				if (want == 0)
					say("STEP 3  WAIT: no context (TREND " + align + ", no acceptance outside value). No trade until one clears.", 2);
				else if (htf != 0 && bias != 0 && htf != bias)
					say(string.Format("STEP 3  WAIT: HTF {0} but price accepted {1} value. Conflicting: stand aside.", htf > 0 ? "up" : "down", bias > 0 ? "above" : "below"), 2);
				else if (odAgainst)
					say("STEP 3  WAIT: open-drive day the other way. No countertrend trades today.", 2);
				else if (bias == 0)
					say(string.Format("STEP 3  WAIT for acceptance: HTF {0}; need {1}m held {2} (now {3}m). Inside value only with a clear draw.",
						htf > 0 ? "up" : "down", HeldMinutes, htf > 0 ? "above VAH" : "below VAL", held), 4);
				else if (!atLevel)
				{
					Level pb = all.Where(l => want > 0 ? l.Price < close : l.Price > close).OrderBy(l => Math.Abs(l.Price - close)).FirstOrDefault();
					say(string.Format("STEP 3  HUNT {0}S: wait for a pullback into a level{1}. Don't chase.", side(want),
						pb != null ? string.Format(" (next: {0} {1:0.##}, {2:0} pts away)", pb.Name, pb.Price, Math.Abs(pb.Price - close)) : ""), 0);
				}
				else if (trigDir == want)
				{
					say(string.Format("STEP 4  ENTER {0}: at {1}, ZCubed triggered{2}. Check 1-min {3} + 1000t, then click {0}.", side(want),
						near != null ? near.Name : "level", trapWithTrend ? ", trap WITH TREND" : "", want > 0 ? "higher low" : "lower high"), 1);
					say("        After the fill: TGT->LVL once if a level is 1R+ away, then hands off.", 1);
				}
				else if (trigDir == -want)
					say("STEP 3  AT LEVEL but ZCubed triggered the other way: no trade.", 2);
				else
					say(string.Format("STEP 3  AT LEVEL ({0}): wait for ZCubed full reset ({1}) and the fast line to turn {2}.{3}",
						near != null ? near.Name : "-", want > 0 ? "all 3 at 20 or below" : "all 3 at 80 or above", want > 0 ? "up" : "down", trapWithTrend ? " Trap WITH TREND: good." : ""), 4);
			}
			return o;
		}

		// high-vol ceiling: 60, but always at least 5 pts above the vol floor (rounded up to 5)
		private static double HighVolCeiling(double floor) { return Math.Max(60, Math.Ceiling(floor / 5.0) * 5 + 5); }

		private static bool KeyLevel(string n)
		{
			return n.StartsWith("PDH") || n.StartsWith("PDL") || n == "PDC" || n.StartsWith("ONH") || n.StartsWith("ONL") || n == "VAH" || n == "VAL" || n == "POC" || n == "tPOC" || n == "IBH" || n == "IBL";
		}

		// +1 when the last two highs and lows are both higher, -1 when both lower, else 0
		private static int Structure(List<double> highs, List<double> lows)
		{
			if (highs.Count < 2 || lows.Count < 2) return 0;
			bool hh = highs[highs.Count - 1] > highs[highs.Count - 2], hl = lows[lows.Count - 1] > lows[lows.Count - 2];
			bool lh = highs[highs.Count - 1] < highs[highs.Count - 2], ll = lows[lows.Count - 1] < lows[lows.Count - 2];
			return hh && hl ? 1 : lh && ll ? -1 : 0;
		}

		private bool IsEqual(double p, List<double> list)
		{
			return list.Count(q => Math.Abs(q - p) <= EqualTolPts) >= 2;
		}
		#endregion

		#region View dropdown and speaker buttons
		private void BuildUi()
		{
			if (chartControlRef == null || viewBox != null) return;
			uiRow = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left,
				VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(6, 4, 0, 0) };
			viewBox = new ComboBox { Width = 120, Height = 22, FontSize = 11, ToolTip = "Trade plan view" };
			foreach (PlanView v in Enum.GetValues(typeof(PlanView))) viewBox.Items.Add("Plan: " + v);
			viewBox.SelectedIndex = (int)View;
			viewBox.SelectionChanged += OnViewChanged;
			speakBtn = MakeButton(SpeakLabel, "Read the plan aloud, top to bottom (the rows this view shows; Hidden reads Full), then the size allowed. Click again to stop.");
			speakBtn.Click += OnSpeakClick;
			alertBtn = MakeButton(VoiceAlerts ? AlertsOnLabel : AlertsOffLabel, "Voice alerts: step changes, bias, traps, ZCubed at a level, high vol, size and daily limits. Click to turn on / off.");
			alertBtn.Click += OnAlertClick;
			uiRow.Children.Add(viewBox); uiRow.Children.Add(speakBtn); uiRow.Children.Add(alertBtn);
			UserControlCollection.Add(uiRow);
		}

		private static Button MakeButton(string text, string tip)
		{
			return new Button { Content = text, ToolTip = tip, Height = 22, FontSize = 11, Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(4, 0, 0, 0) };
		}

		private void RemoveUi()
		{
			if (viewBox == null) return;
			StopSpeech();
			if (speechTimer != null) { speechTimer.Stop(); speechTimer = null; }
			if (voice != null) { try { Marshal.FinalReleaseComObject(voice); } catch { } voice = null; }
			viewBox.SelectionChanged -= OnViewChanged;
			speakBtn.Click -= OnSpeakClick;
			alertBtn.Click -= OnAlertClick;
			if (UserControlCollection.Contains(uiRow)) UserControlCollection.Remove(uiRow);
			viewBox = null; speakBtn = alertBtn = null; uiRow = null;
		}

		private int[] VisibleRows(PlanView v, string[] all, int[] kind)
		{
			if (kind.Length != all.Length) return new int[0];
			return Enumerable.Range(0, all.Length).Where(i => kind[i] == 2 || v == PlanView.Full
				|| (v == PlanView.Context && kind[i] == 0) || (v == PlanView.Execution && kind[i] == 1)).ToArray();
		}

		// Live read: Windows' built-in voice (SAPI.SpVoice over COM, so no extra references to add in NinjaScript)
		private void OnSpeakClick(object sender, RoutedEventArgs e)
		{
			try
			{
				if (voice != null && !(bool)Com(voice, "WaitUntilDone", BindingFlags.InvokeMethod, 0)) { StopSpeech(); return; }   // second click stops
				string[] all = lines, said = spoken; int[] kind = rowKind;
				string text = string.Join(". ", VisibleRows(View == PlanView.Hidden ? PlanView.Full : View, all, kind)
					.Select(i => i < said.Length && said[i] != null ? said[i] : Speakable(all[i])));
				if (text.Length == 0) return;
				string risk = RiskText;
				Speak(string.IsNullOrEmpty(risk) ? text : text + ". " + risk);
			}
			catch (Exception ex) { Print("TradePlanPanel speech: " + ex.Message); }
		}

		private void OnAlertClick(object sender, RoutedEventArgs e)
		{
			VoiceAlerts = !VoiceAlerts;
			alertBtn.Content = VoiceAlerts ? AlertsOnLabel : AlertsOffLabel;
			if (!VoiceAlerts) StopSpeech();
		}

		// Short spoken alert from any thread (the panel's own checks, or PlaybookExecutor). Queues behind anything already speaking.
		// high = a high alert: clears anything queued or playing and speaks in the HighAlertVoice
		public void Announce(string text, bool high = false)
		{
			if (!VoiceAlerts || View == PlanView.Hidden || chartControlRef == null || string.IsNullOrEmpty(text)) return;
			Print(string.Format("{0:HH:mm:ss} TradePlanPanel {1}: {2}", DateTime.Now, high ? "HIGH" : "say", text));   // log of every alert
			chartControlRef.Dispatcher.InvokeAsync(new Action(() =>
			{
				try { if (speakBtn != null) Speak(text, high); } catch (Exception ex) { Print("TradePlanPanel alert: " + ex.Message); }
			}));
		}

		// UI thread only (the COM voice lives there)
		private void Speak(string text, bool high = false)
		{
			{
				if (voice == null) voice = Activator.CreateInstance(Type.GetTypeFromProgID("SAPI.SpVoice"));
				Com(voice, "Rate", BindingFlags.SetProperty, SpeechRate);
				if (high)   // SAPI XML picks the voice per sentence (falls back to the default if it isn't installed); 1 async + 2 purge + 8 XML
					Com(voice, "Speak", BindingFlags.InvokeMethod, string.Format("<voice optional=\"Name={0}\">{1}</voice>",
						System.Security.SecurityElement.Escape(HighAlertVoice), System.Security.SecurityElement.Escape(text)), 1 | 2 | 8);
				else
					Com(voice, "Speak", BindingFlags.InvokeMethod, text, 1);   // 1 = async, queued after what's playing
				speakBtn.Content = "■ Stop";
				if (speechTimer == null)
				{
					// put the label back when the voice finishes on its own
					speechTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
					speechTimer.Tick += (s, a) =>
					{
						bool done = true;
						try { done = voice == null || (bool)Com(voice, "WaitUntilDone", BindingFlags.InvokeMethod, 0); } catch { }
						if (done) { speechTimer.Stop(); if (speakBtn != null) speakBtn.Content = SpeakLabel; }
					};
				}
				speechTimer.Start();
			}
		}

		private void StopSpeech()
		{
			if (voice != null) try { Com(voice, "Speak", BindingFlags.InvokeMethod, "", 3); } catch { }   // 3 = async + purge
			if (speakBtn != null) speakBtn.Content = SpeakLabel;
		}

		private static object Com(object o, string name, BindingFlags kind, params object[] args)
		{
			return o.GetType().InvokeMember(name, kind, null, o, args);
		}

		// Voice alerts: each state is spoken once when it changes (live bars only), and the same alert not again within the cooldown
		private bool alertsReady, aHighVol;
		private string aStep = "", aTrap = "";
		private int aBias, aLadder;
		private readonly Dictionary<string, DateTime> alertSpoke = new Dictionary<string, DateTime>();

		// ZCubed voice (377t closed bars). Every full reset is spoken with its context (ZcAllResets); the setup stages only at a
		// level in the trade direction: heads-up (all < 35 / > 65 and still falling / rising into it) -> full reset ->
		// trigger (high alert, the entry moment) or "setup gone" (reset expired without the turn, or price left the level).
		private bool zReady, zArmUpPrev, zArmDnPrev, zNearSaid, zSetupLive;
		private int zSetupDir;

		private void ZcAlerts(double f, double m, double s, double f1, bool armedUp, bool armedDn, int trigDir, int want, bool atLevel, Level near)
		{
			bool newUp = armedUp && !zArmUpPrev, newDn = armedDn && !zArmDnPrev;
			zArmUpPrev = armedUp; zArmDnPrev = armedDn;
			if (!atLevel) zNearSaid = false;
			if (!zReady) { zReady = true; return; }   // first live bar: just learn the state

			string lvl = near != null ? Speakable(near.Name) : "the level";
			Func<int, string> lowHigh = d => d > 0 ? "low" : "high";
			Func<int, string> upDown = d => d > 0 ? "up" : "down";
			Func<int, string> side = d => d > 0 ? "longs" : "shorts";
			double mx = Math.Max(f, Math.Max(m, s)), mn = Math.Min(f, Math.Min(m, s));
			var say = new List<string>();

			// 1. heads-up: at a level in the trade direction, oscillators nearly reset and still heading there
			bool armedWant = want > 0 ? armedUp : want < 0 && armedDn;
			if (atLevel && want != 0 && !zNearSaid && !armedWant && (want > 0 ? mx <= 35 && f < f1 : mn >= 65 && f > f1) && Fresh("zc near " + want + lvl))
			{
				zNearSaid = true;
				say.Add(string.Format("Heads up. Z cubed getting {0} at {1}, {2}.", lowHigh(want), lvl, side(want)));
			}

			// 2. full reset: always spoken, with what it means here
			int dir = newUp ? 1 : newDn ? -1 : 0;
			if (dir != 0 && Fresh("zc reset " + dir))
			{
				if (atLevel && want == dir)
				{
					zSetupLive = true; zSetupDir = dir;
					say.Add(string.Format("Z cubed full reset {0} at {1}, with the trend. Wait for the fast line to turn {2}.", lowHigh(dir), lvl, upDown(dir)));
				}
				else if (ZcAllResets)
					say.Add(string.Format("Z cubed full reset {0}, {1}.", lowHigh(dir),
						want == -dir ? "against the trend, no trade" : !atLevel ? "not at a level" : "at " + lvl + ", no trend read yet"));
			}

			// 3. trigger at the level in the trade direction = the entry moment
			if (trigDir != 0 && atLevel && trigDir == want && Fresh("zc trigger " + trigDir))
			{
				zSetupLive = false;
				Announce(string.Format("Trigger {0}. Z cubed turned {1} at {2}. Check the 1 minute and 1000 tick.", trigDir > 0 ? "long" : "short", upDown(trigDir), lvl), true);
			}
			// 4. the setup went away without a trigger
			else if (zSetupLive && (!(zSetupDir > 0 ? armedUp : armedDn) || !atLevel))
			{
				zSetupLive = false;
				say.Add(!atLevel ? "Setup gone. Price left the level." : "Setup gone. Z cubed reset faded without the turn.");
			}
			if (say.Count > 0) Announce(string.Join(" ", say));
		}

		private bool Fresh(string key)
		{
			DateTime t;
			if (alertSpoke.TryGetValue(key, out t) && (DateTime.Now - t).TotalSeconds < AlertCooldownSec) return false;
			alertSpoke[key] = DateTime.Now; return true;
		}

		private void CheckAlerts(string step, int bias, DateTime trapAt, int trapDir, bool trapWithTrend, Level near, bool hv, double hvCeiling)
		{
			string stepKey = step.Length == 0 || step.Contains("IN TRADE") ? aStep : step.Split(':')[0];
			string trapKey = trapAt == DateTime.MinValue ? "" : trapName + trapAt.Ticks;
			string lvl = near != null ? Speakable(near.Name) : "the level";
			var say = new List<string>();
			Func<string, bool> fresh = Fresh;
			if (alertsReady)
			{
				// the ENTER step is said by the ZCubed trigger high alert instead
				if (stepKey != aStep && !stepKey.Contains("ENTER") && fresh("step " + stepKey)) say.Add(Speakable(step) + ".");
				if (bias != aBias && bias != 0 && fresh("bias " + bias))
					say.Add(string.Format("Held {0} value {1} minutes. {2} bias.", bias > 0 ? "above" : "below", HeldMinutes, bias > 0 ? "Long" : "Short"));
				if (trapKey != aTrap && trapKey.Length > 0 && fresh("trap " + trapKey))
					say.Add(string.Format("{0} at {1}. {2} side, {3}.", trapDir > 0 ? "Bear trap" : "Bull trap", Speakable(trapName), trapDir > 0 ? "Long" : "Short",
						trapWithTrend ? "with the trend" : "against the trend, careful"));
				if (hv != aHighVol && fresh("vol " + hv))
					say.Add(hv ? string.Format("High volatility. One micro only, stops up to {0:0} points.", hvCeiling) : "Volatility back to normal.");
				if (account != null && ladderSize != aLadder && fresh("size " + ladderSize))
					say.Add(string.Format("Size now {0} micro{1}.", ladderSize, ladderSize == 1 ? "" : "s"));
			}
			aStep = stepKey; aBias = bias; aTrap = trapKey; aHighVol = hv; aLadder = ladderSize;
			alertsReady = true;
			if (say.Count > 0) Announce(string.Join(" ", say));
		}

		// Liquidity pools for the voice: no prices, just names, how many and points away, nearest first
		// e.g. "Buy side: three 1 hour swing highs, nearest plus 12 points; prior day high plus 62 points"
		private static readonly string[] CountWord = { "", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten" };

		private static string PtsAway(double d)
		{
			return string.Format("{0} {1:0} points", d >= 0 ? "plus" : "minus", Math.Abs(d));
		}

		private static string SpokenPools(string side, IEnumerable<Level> pools, double close, string none)
		{
			var groups = pools.GroupBy(l => l.Name)
				.Select(g => new { Name = Speakable(g.Key), D = g.Select(l => l.Price - close).OrderBy(d => Math.Abs(d)).ToList() })
				.OrderBy(g => Math.Abs(g.D[0])).ToList();
			if (groups.Count == 0) return side + ": none " + none;
			return side + ": " + string.Join("; ", groups.Select(g => g.D.Count == 1 ? g.Name + " " + PtsAway(g.D[0])
				: string.Format("{0} {1}{2}, nearest {3}", g.D.Count < CountWord.Length ? CountWord[g.D.Count] : g.D.Count.ToString(), g.Name,
					g.Name.EndsWith("s") ? "" : "s", PtsAway(g.D[0]))));
		}

		// Panel text -> something the voice says naturally (abbreviations spelled out, column gaps become pauses)
		private static readonly string[,] SayAs = {
			{ @"TGT->LVL", "target to level" }, { @"->", ", so " }, { @"\s>\s", ", then " }, { @"\|", ", " }, { @"\s=\s", ", " },
			{ @"[()]", ", " }, { @"(?<=\w)/(?=\w)", " or " }, { @"(?<=\d)-(?=\d)", " to " },
			{ @"\bntPOC\b", "naked time P O C" }, { @"\bnPOC\b", "naked P O C" }, { @"\btPOC\b", "time P O C" }, { @"\bPOC(s?)\b", "P O C$1" },
			{ @"\bVAH\b", "value area high" }, { @"\bVAL\b", "value area low" },
			{ @"\bPDH\b", "prior day high" }, { @"\bPDL\b", "prior day low" }, { @"\bPDC\b", "prior day close" },
			{ @"\bONH\b", "overnight high" }, { @"\bONL\b", "overnight low" }, { @"\bIBH\b", "I B high" }, { @"\bIBL\b", "I B low" }, { @"\bIB\b", "I B" },
			{ @"\bHTF\b", "higher time frame" }, { @"\bFVG(s?)\b", "F V G$1" }, { @"\bATM\b", "A T M" }, { @"\bMNQ\b", "M N Q" }, { @"\bNQ\b", "N Q" },
			{ @"\bEQH\b", "equal highs" }, { @"\bEQL\b", "equal lows" }, { @"\bSH\b", "swing high" }, { @"\bSL\b", "swing low" },
			{ @"\bOD\b", "open drive" }, { @"\bBE\b", "breakeven" }, { @"\bPT\b", "Pacific" }, { @"^VOL\b", "volatility" },
			{ @"\bRL(\d+)x1\b", "R L $1, one micro" }, { @"\bRL(\d+)\b", "R L $1" }, { @"\bZ[Cc][Uu][Bb][Ee][Dd]\b", "Z cubed" },
			{ @"\bF (\d+) M (\d+) S (\d+)", "fast $1, medium $2, slow $3" },
			{ @"(\d)-min\b", "$1 minute" }, { @"(\d)t\b", "$1 tick" }, { @"(\d)h\b", "$1 hour" }, { @"(\d)d\b", "$1 day" }, { @"(\d)m\b", "$1 minutes" }, { @"(\d)R\b", "$1 R" },
			{ @"\bpts\b", "points" },
		};

		private static string Speakable(string row)
		{
			string s = Regex.Replace(row.Trim(), @"^([A-Z][A-Z\-]*(?: \d+| TYPE)?)\s+", "$1. ");   // row name, then a pause
			for (int i = 0; i < SayAs.GetLength(0); i++) s = Regex.Replace(s, SayAs[i, 0], SayAs[i, 1]);
			s = Regex.Replace(s, @"\b[A-Z]{2,}\b", m => m.Value.ToLowerInvariant());   // LONGS, WAIT ... are words, not letters
			s = Regex.Replace(s, @"\s{2,}", ", ");
			s = Regex.Replace(s, @"\s*,(?:\s*,)*\s*", ", ");
			return Regex.Replace(s, @"[,\s]+(?=[.:]|$)", "");
		}

		private void OnViewChanged(object sender, SelectionChangedEventArgs e)
		{
			View = (PlanView)viewBox.SelectedIndex;
			if (chartControlRef != null) chartControlRef.InvalidateVisual();
		}
		#endregion

		#region Read by PlaybookExecutor (journal context and target levels)
		private volatile string lastDayType = "", lastNear = "", lastZc = "", lastContext = "", lastTrap = "";
		[Browsable(false)] [XmlIgnore] public string TrapText { get { return lastTrap; } }
		private volatile int lastBias;
		[Browsable(false)] [XmlIgnore] public int Bias { get { return lastBias; } }
		[Browsable(false)] [XmlIgnore] public string DayTypeText { get { return lastDayType; } }
		[Browsable(false)] [XmlIgnore] public string NearestLevelText { get { return lastNear; } }
		[Browsable(false)] [XmlIgnore] public string ZCubedText { get { return lastZc; } }
		[Browsable(false)] [XmlIgnore] public string ContextText { get { return lastContext; } }
		[Browsable(false)] [XmlIgnore] public double[] LevelPrices { get { return drawLevels.Select(l => l.Price).ToArray(); } }
		[Browsable(false)] [XmlIgnore] public bool HighVol { get { return highVol; } }
		[Browsable(false)] [XmlIgnore] public double LargestCandlePts { get { return lastLc; } }
		[Browsable(false)] [XmlIgnore] public double TenDayRange { get { return lastR10; } }
		#endregion

		#region Rendering
		public override void OnRenderTargetChanged()
		{
			DisposeResources();
			if (RenderTarget != null)
			{
				dxWhite = Brushes.White.ToDxBrush(RenderTarget);
				dxGreen = Brushes.LimeGreen.ToDxBrush(RenderTarget);
				dxRed = Brushes.Tomato.ToDxBrush(RenderTarget);
				dxGrey = Brushes.Gray.ToDxBrush(RenderTarget);
				dxYellow = Brushes.Gold.ToDxBrush(RenderTarget);
				dxLiq = Brushes.IndianRed.ToDxBrush(RenderTarget);
				dxProf = Brushes.DodgerBlue.ToDxBrush(RenderTarget);
				dxBack = Brushes.Black.ToDxBrush(RenderTarget);
				dxBack.Opacity = 0.7f;
			}
		}

		private void DisposeResources()
		{
			foreach (var b in new[] { dxWhite, dxGreen, dxRed, dxGrey, dxYellow, dxBack, dxLiq, dxProf })
				if (b != null) b.Dispose();
			dxWhite = dxGreen = dxRed = dxGrey = dxYellow = dxBack = dxLiq = dxProf = null;
			if (textFormat != null) { textFormat.Dispose(); textFormat = null; }
			if (smallFormat != null) { smallFormat.Dispose(); smallFormat = null; }
		}

		protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
		{
			if (dxWhite == null || ChartPanel == null)
				return;
			if (textFormat == null)
			{
				textFormat = new SharpDX.DirectWrite.TextFormat(Core.Globals.DirectWriteFactory, "Consolas", FontSize);
				smallFormat = new SharpDX.DirectWrite.TextFormat(Core.Globals.DirectWriteFactory, "Arial", SharpDX.DirectWrite.FontWeight.Bold, SharpDX.DirectWrite.FontStyle.Normal, (float)LevelFontSize);
			}

			if (ShowLevels && View != PlanView.Hidden)
			{
				float x0 = ChartPanel.X, x1 = ChartPanel.X + ChartPanel.W;
				float lh2 = (float)LevelFontSize + 4;
				double last = Close.IsValidDataPointAt(CurrentBar) ? Close.GetValueAt(CurrentBar) : (chartScale.MaxValue + chartScale.MinValue) / 2;
				int up = 0, dn = 0;
				var onScreen = new List<KeyValuePair<float, Level>>();
				foreach (Level lv in drawLevels)
				{
					if (lv.Price > chartScale.MaxValue || lv.Price < chartScale.MinValue)
					{
						// key levels off-screen: pin a label to the top / bottom edge so you still know where they are
						if (!KeyLevel(lv.Name)) continue;
						bool above = lv.Price > chartScale.MaxValue;
						float ey = above ? ChartPanel.Y + 32 + lh2 * up++ : ChartPanel.Y + ChartPanel.H - lh2 - 4 - lh2 * dn++;
						string t = string.Format("{0} {1} {2:0.##} ({3:+0;-0})", above ? "▲" : "▼", lv.Name, lv.Price, lv.Price - last);
						RenderTarget.DrawText(t, smallFormat, new SharpDX.RectangleF(x1 - 16 * (float)LevelFontSize, ey, 16 * (float)LevelFontSize - 5, lh2), lv.Kind == 1 ? dxProf : dxLiq);
						continue;
					}
					float y = chartScale.GetYByValue(lv.Price);
					RenderTarget.DrawLine(new SharpDX.Vector2(x0, y), new SharpDX.Vector2(x1, y), lv.Kind == 1 ? dxProf : dxLiq, 1f);
					onScreen.Add(new KeyValuePair<float, Level>(y, lv));
				}

				// labels: levels closer than one label height share a row, names side by side from the right edge
				onScreen.Sort((p1, p2) => p1.Key.CompareTo(p2.Key));
				int k0 = 0;
				while (k0 < onScreen.Count)
				{
					int k1 = k0 + 1;
					while (k1 < onScreen.Count && onScreen[k1].Key - onScreen[k0].Key < lh2) k1++;
					float x = x1 - 5, ly = onScreen[k0].Key - lh2;
					for (int k = k1 - 1; k >= k0; k--)
					{
						Level lv = onScreen[k].Value;
						string name = k > k0 ? " · " + lv.Name : lv.Name;
						float wdt;
						using (var layout = new SharpDX.DirectWrite.TextLayout(Core.Globals.DirectWriteFactory, name, smallFormat, 1000, lh2))
							wdt = layout.Metrics.WidthIncludingTrailingWhitespace;
						x -= wdt;
						RenderTarget.DrawText(name, smallFormat, new SharpDX.RectangleF(x, ly, wdt + 2, lh2), lv.Kind == 1 ? dxProf : dxLiq);
					}
					k0 = k1;
				}
			}

			string[] all = lines; int[] allC = colors; int[] kind = rowKind;
			if (View == PlanView.Hidden || allC.Length != all.Length) return;
			int[] keep = VisibleRows(View, all, kind);
			string[] l = keep.Select(i => all[i]).ToArray(); int[] c = keep.Select(i => allC[i]).ToArray();
			if (l.Length == 0) return;
			float lh = FontSize + 5, w = 0;
			foreach (string s in l)
				using (var layout = new SharpDX.DirectWrite.TextLayout(Core.Globals.DirectWriteFactory, s, textFormat, 2000, lh))
					w = Math.Max(w, layout.Metrics.Width);
			float px0 = ChartPanel.X + 6, py0 = ChartPanel.Y + 32;   // below the view dropdown
			RenderTarget.FillRectangle(new SharpDX.RectangleF(px0, py0, w + 12, lh * l.Length + 8), dxBack);
			for (int i = 0; i < l.Length && i < c.Length; i++)
			{
				var b = c[i] == 1 ? dxGreen : c[i] == 2 ? dxRed : c[i] == 3 ? dxGrey : c[i] == 4 ? dxYellow : dxWhite;
				RenderTarget.DrawText(l[i], textFormat, new SharpDX.RectangleF(px0 + 6, py0 + 4 + i * lh, w + 4, lh), b);
			}
		}
		#endregion

		// Hide the name in the chart's top-left label area unless asked; the Indicators dialog still shows it
		public override string DisplayName { get { return ShowLabel || State == State.SetDefaults ? Name : string.Empty; } }

		#region Properties
		[Range(-5, 5)]
		[Display(Name = "Speech rate", Description = "Speed of the 🔊 Plan voice: -5 slow, 0 normal, 5 fast.", Order = 97, GroupName = "Plan")]
		public int SpeechRate { get; set; }

		[Display(Name = "Voice alerts", Description = "Speak when the step, bias, a trap, ZCubed at a level, high vol or size changes (live bars only). Also the 🔔 button.", Order = 95, GroupName = "Plan")]
		public bool VoiceAlerts { get; set; }

		[Range(0, 3600)]
		[Display(Name = "Alert cooldown (sec)", Description = "The same alert is not spoken again within this many seconds.", Order = 96, GroupName = "Plan")]
		public int AlertCooldownSec { get; set; }

		[Display(Name = "ZCubed: say every full reset", Description = "Also speak full resets away from a level or against the trend (with that context). Off = only resets at a level in the trade direction.", Order = 91, GroupName = "Plan")]
		public bool ZcAllResets { get; set; }

		[Display(Name = "PMARP voice", Description = "Speak the panel's PMARP warnings / high alerts / continuation. Turn off if PMARPv3 on another chart is doing the talking.", Order = 92, GroupName = "Plan")]
		public bool PmarpVoice { get; set; }

		[Range(1, 50)]
		[Display(Name = "PMARP turn points", Description = "A turn = PMARP this far off the run's low (high) on 2 closes in a row.", Order = 92, GroupName = "Plan")]
		public double PmarpTurnPts { get; set; }

		[Range(0, 120)]
		[Display(Name = "PMARP continuation (min)", Description = "While PMARP keeps rising / falling, say so every this many minutes (0 = off). Also once when it reaches 80 / 20.", Order = 93, GroupName = "Plan")]
		public int ContinuationMin { get; set; }

		[Range(1, 15)]
		[Display(Name = "PMARP bar minutes", Description = "PMARP is built on bars of this many minutes from the chart's data (2 = updates every 2 min; research used 5).", Order = 94, GroupName = "Plan")]
		public int PmarpMinutes { get; set; }

		[Display(Name = "High alert voice", Description = "Windows voice for high alerts (PMARP signal cross), e.g. Microsoft Zira Desktop or Microsoft David Desktop.", Order = 97, GroupName = "Plan")]
		public string HighAlertVoice { get; set; }

		// Set by PlaybookExecutor: size allowed and risk left, read at the end of 🔊 Plan
		[Browsable(false)] [XmlIgnore] public string RiskText { get { return riskText; } set { riskText = value ?? ""; } }

		[Display(Name = "Show label", Description = "Show the indicator's name in the chart's top-left label area.", Order = 99, GroupName = "Plan")]
		public bool ShowLabel { get; set; }

		[NinjaScriptProperty]
		[Range(1, 20)]
		[Display(Name = "At-level tolerance (% of 10-day range)", Description = "Price counts as 'at a level' within this % of the average RTH range (5% ~ 15 pts).", Order = 1, GroupName = "Plan")]
		public double LevelTolPct { get; set; }

		[NinjaScriptProperty]
		[Range(0.25, 20)]
		[Display(Name = "Equal highs/lows tolerance (pts)", Description = "Two unswept hourly swing highs (lows) this close count as equal highs (lows).", Order = 2, GroupName = "Plan")]
		public double EqualTolPts { get; set; }

		[NinjaScriptProperty]
		[Range(1, 240)]
		[Display(Name = "Held outside value (minutes)", Description = "Unbroken closes beyond prior VAH / VAL for this long set the bias.", Order = 3, GroupName = "Plan")]
		public int HeldMinutes { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show levels", Description = "Draw the levels on this chart.", Order = 4, GroupName = "Plan")]
		public bool ShowLevels { get; set; }

		[NinjaScriptProperty]
		[Range(8, 20)]
		[Display(Name = "Font size", Order = 5, GroupName = "Plan")]
		public int FontSize { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Account", Description = "Account whose P&L drives David's size ladder (e.g. Playback101, Sim101 or a prop account).", Order = 1, GroupName = "Size ladder")]
		public string AccountName { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Starting balance ($)", Description = "Account balance at the start; P&L = net liquidation minus this.", Order = 2, GroupName = "Size ladder")]
		public double StartingBalance { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "2 MNQ at P&L ($)", Order = 3, GroupName = "Size ladder")]
		public double LadderUpAt { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Back to 1 MNQ below P&L ($)", Order = 4, GroupName = "Size ladder")]
		public double LadderDownBelow { get; set; }

		[Display(Name = "Plan view", Description = "Full, Context (trend, range, value, liquidity, draw), Execution (level, trigger, stop, gates, size) or Hidden. Also on the chart dropdown.", Order = 6, GroupName = "Plan")]
		public PlanView View { get; set; }

		[Display(Name = "Show session window", Description = "Shows whether the time is in David's first 2h / PM window (6:30-8:30 / 10:00-12:30 PT). Off by default: the journal's entry times decide whether it matters.", Order = 7, GroupName = "Plan")]
		public bool ShowSessionWindow { get; set; }

		[Display(Name = "Show step coach", Description = "Top rows: which playbook step you are on and what to do next (all views).", Order = 8, GroupName = "Plan")]
		public bool ShowCoach { get; set; }

		[Display(Name = "High-vol candle (pts)", Description = "Largest 1-min candle over the last 2 hours at or above this = HIGH volatility (1 MNQ, wider stops). ~top quarter of days.", Order = 9, GroupName = "Plan")]
		public double HighVolCandlePts { get; set; }

		[Display(Name = "Vol stop floor (% of 10-day range)", Description = "Stops never tighter than this % of the median RTH range of the last 10 days (10% = most even stop-out rate in testing). 0 = off.", Order = 10, GroupName = "Plan")]
		public double VolStopPct { get; set; }

		[Range(8, 30)]
		[Display(Name = "Level label font size", Description = "Size of the PDH / PDL / VAH ... labels on the lines and at the chart edges.", Order = 11, GroupName = "Plan")]
		public int LevelFontSize { get; set; }
		#endregion
	}
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private TradePlanPanel[] cacheTradePlanPanel;
		public TradePlanPanel TradePlanPanel(double levelTolPct, double equalTolPts, int heldMinutes, bool showLevels, int fontSize, string accountName, double startingBalance, double ladderUpAt, double ladderDownBelow)
		{
			return TradePlanPanel(Input, levelTolPct, equalTolPts, heldMinutes, showLevels, fontSize, accountName, startingBalance, ladderUpAt, ladderDownBelow);
		}

		public TradePlanPanel TradePlanPanel(ISeries<double> input, double levelTolPct, double equalTolPts, int heldMinutes, bool showLevels, int fontSize, string accountName, double startingBalance, double ladderUpAt, double ladderDownBelow)
		{
			if (cacheTradePlanPanel != null)
				for (int idx = 0; idx < cacheTradePlanPanel.Length; idx++)
					if (cacheTradePlanPanel[idx] != null && cacheTradePlanPanel[idx].LevelTolPct == levelTolPct && cacheTradePlanPanel[idx].EqualTolPts == equalTolPts && cacheTradePlanPanel[idx].HeldMinutes == heldMinutes && cacheTradePlanPanel[idx].ShowLevels == showLevels && cacheTradePlanPanel[idx].FontSize == fontSize && cacheTradePlanPanel[idx].AccountName == accountName && cacheTradePlanPanel[idx].StartingBalance == startingBalance && cacheTradePlanPanel[idx].LadderUpAt == ladderUpAt && cacheTradePlanPanel[idx].LadderDownBelow == ladderDownBelow && cacheTradePlanPanel[idx].EqualsInput(input))
						return cacheTradePlanPanel[idx];
			return CacheIndicator<TradePlanPanel>(new TradePlanPanel(){ LevelTolPct = levelTolPct, EqualTolPts = equalTolPts, HeldMinutes = heldMinutes, ShowLevels = showLevels, FontSize = fontSize, AccountName = accountName, StartingBalance = startingBalance, LadderUpAt = ladderUpAt, LadderDownBelow = ladderDownBelow }, input, ref cacheTradePlanPanel);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.TradePlanPanel TradePlanPanel(double levelTolPct, double equalTolPts, int heldMinutes, bool showLevels, int fontSize, string accountName, double startingBalance, double ladderUpAt, double ladderDownBelow)
		{
			return indicator.TradePlanPanel(Input, levelTolPct, equalTolPts, heldMinutes, showLevels, fontSize, accountName, startingBalance, ladderUpAt, ladderDownBelow);
		}

		public Indicators.TradePlanPanel TradePlanPanel(ISeries<double> input , double levelTolPct, double equalTolPts, int heldMinutes, bool showLevels, int fontSize, string accountName, double startingBalance, double ladderUpAt, double ladderDownBelow)
		{
			return indicator.TradePlanPanel(input, levelTolPct, equalTolPts, heldMinutes, showLevels, fontSize, accountName, startingBalance, ladderUpAt, ladderDownBelow);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.TradePlanPanel TradePlanPanel(double levelTolPct, double equalTolPts, int heldMinutes, bool showLevels, int fontSize, string accountName, double startingBalance, double ladderUpAt, double ladderDownBelow)
		{
			return indicator.TradePlanPanel(Input, levelTolPct, equalTolPts, heldMinutes, showLevels, fontSize, accountName, startingBalance, ladderUpAt, ladderDownBelow);
		}

		public Indicators.TradePlanPanel TradePlanPanel(ISeries<double> input , double levelTolPct, double equalTolPts, int heldMinutes, bool showLevels, int fontSize, string accountName, double startingBalance, double ladderUpAt, double ladderDownBelow)
		{
			return indicator.TradePlanPanel(input, levelTolPct, equalTolPts, heldMinutes, showLevels, fontSize, accountName, startingBalance, ladderUpAt, ladderDownBelow);
		}
	}
}

#endregion
