#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
#endregion

// Hourly wick rejections at the prior TPO levels, for the 60-min chart.
//   Levels: POC / VAH / VAL / single prints of the last SessionsBack completed RTH sessions, built like TpoMarketProfile
//   (Daily RTH 09:30-16:00 ET, 30-min blocks, TicksPerRow rows, 70% value area). A session's levels count from its close.
//   Long : the hour's low trades >= ThroughPts below a level and closes back above it, lower wick >= MinWickPct of the range.
//   Short: the mirror with the upper wick (also the "sell the long" mark). A bar with both is skipped.
//   Trend: close vs SMA(TrendPeriod). Backtest 2021-2026 (analysis/research/2026-11-09_hour_wick_levels.md): no edge
//   overall (-0.04R, no better than placebo levels); against-trend wicks were worst (-0.10R both halves), so they are dimmed.
// Drawn as rings around the wick (BigTrades style): solid = closed bar with the trend, dashed = against the trend,
// dotted = still forming inside the current hour. The latest signal shows its next level and the points to it.
// Voice (live only): forming wick (once per hour per side), confirmed wick on the close (with the trend: high alert voice).
namespace NinjaTrader.NinjaScript.Indicators
{
	public class HourWickLevels : Indicator
	{
		private class Level { public string Kind; public double Price; }
		private class Session { public DateTime EndUtc; public List<Level> Levels; }
		private class Wick
		{
			public int Bar, Dir; public bool WithTrend, Forming;
			public double Tip, Body, LevelPrice, Target, Close; public string Kind, TargetKind;
		}

		private TimeZoneInfo eastern;
		private SMA sma;
		private readonly List<Session> sessions = new List<Session>();
		private readonly SortedDictionary<int, double[]> blocks = new SortedDictionary<int, double[]>();   // slot -> hi, lo
		private DateTime blockDate = DateTime.MinValue;
		private int lastBlockBar = -1, lastHourBar = -1;
		private readonly object sync = new object();
		private readonly List<Wick> wicks = new List<Wick>();
		private Wick forming;
		private readonly HashSet<string> saidThisBar = new HashSet<string>();
		private readonly Dictionary<string, DateTime> spoke = new Dictionary<string, DateTime>();
		private bool startSaid;
		private ChartControl chartControlRef;
		private object voice;   // SAPI.SpVoice (COM), created and used on the chart's UI thread
		private SharpDX.Direct2D1.Brush dxUp, dxDn, dxLevel;
		private SharpDX.Direct2D1.StrokeStyle dashed, dotted;
		private SharpDX.DirectWrite.TextFormat textFormat;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description = "Hourly wick rejections at the prior TPO levels (POC, VAH, VAL, single prints), with voice alerts.";
				Name = "HourWickLevels";
				Calculate = Calculate.OnPriceChange;
				IsOverlay = true;
				DisplayInDataBox = false;
				DrawOnPricePanel = true;
				IsSuspendedWhileInactive = false;

				ShowLongs = true;
				ShowShorts = true;
				ShowAgainstTrend = true;
				ShowForming = true;
				ThroughPts = 2;
				MinWickPct = 40;
				TrendPeriod = 50;
				SessionsBack = 2;
				TicksPerRow = 10;
				ValueAreaPct = 70;
				MinTargetPts = 20;
				TargetBars = 6;
				DrawLevels = false;
				LongBrush = Brushes.LimeGreen;
				ShortBrush = Brushes.Red;
				VoiceAlerts = true;
				FormingVoice = true;
				FormingMinMinutes = 20;
				CooldownSec = 120;
				SpeechRate = 1;
				HighAlertVoice = "Microsoft Zira Desktop";
			}
			else if (State == State.Configure)
				AddDataSeries(BarsPeriodType.Minute, 30);
			else if (State == State.DataLoaded)
			{
				eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
				sma = SMA(Close, TrendPeriod);
			}
			else if (State == State.Historical)
				chartControlRef = ChartControl;
			else if (State == State.Terminated)
			{
				DisposeResources();
				if (chartControlRef != null && voice != null)
				{
					object v = voice; voice = null;
					chartControlRef.Dispatcher.InvokeAsync(new Action(() => { try { Marshal.FinalReleaseComObject(v); } catch { } }));
				}
			}
		}

		protected override void OnBarUpdate()
		{
			bool live = State == State.Realtime;
			// history: every call is a closed bar. Live: the bar that just closed is [1] on the first tick of the next one.
			if (BarsInProgress == 1)
			{
				if (!live) AddBlock(0);
				else if (IsFirstTickOfBar && CurrentBars[1] > 0) AddBlock(1);
				return;
			}
			if (CurrentBar < TrendPeriod + 1) return;
			if (live && !startSaid) { startSaid = true; if (VoiceAlerts) Say("Hour wick voice alerts on.", false); }

			if (!live) CloseHour(0);
			else if (IsFirstTickOfBar) { CloseHour(1); saidThisBar.Clear(); }
			if (!live) return;

			// the forming hour: a ring once its wick qualifies, gone again if it stops qualifying
			Wick w = ShowForming ? Find(0) : null;
			if (w != null) w.Forming = true;
			lock (sync) forming = w;
			if (w == null || !FormingVoice) return;
			double minutesIn = BarsPeriod.Value - (Time[0] - Core.Globals.Now).TotalMinutes;
			if (minutesIn >= FormingMinMinutes && Once("forming " + w.Dir))
				Say(string.Format("Hourly {0} wick forming at {1} {2:0}{3}.", w.Dir > 0 ? "long" : "short", Words(w.Kind), w.LevelPrice,
					w.WithTrend ? "" : ", against the trend"), false);
		}

		#region Levels
		// One closed 30-min bar: a TPO block when it closes 10:00-16:00 ET. The 16:00 block (or the first bar of a new
		// date) completes the session's profile.
		private void AddBlock(int ago)
		{
			int bar = CurrentBars[1] - ago;
			if (bar <= lastBlockBar) return;
			lastBlockBar = bar;
			DateTime et = TimeZoneInfo.ConvertTime(Times[1][ago], Core.Globals.GeneralOptions.TimeZoneInfo, eastern);
			int mins = et.Hour * 60 + et.Minute;
			if (blocks.Count > 0 && et.Date != blockDate) Complete(blockDate.AddHours(16));
			if (mins <= 9 * 60 + 30 || mins > 16 * 60) return;
			blockDate = et.Date;
			blocks[(mins - 9 * 60 - 31) / 30] = new[] { Highs[1][ago], Lows[1][ago] };
			if (mins == 16 * 60) Complete(et);
		}

		private void Complete(DateTime endEt)
		{
			if (blocks.Count >= 6)
			{
				List<Level> lv = Profile();
				lock (sync)
				{
					sessions.Add(new Session { EndUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(endEt, DateTimeKind.Unspecified), eastern), Levels = lv });
					if (sessions.Count > 20) sessions.RemoveAt(0);
				}
			}
			blocks.Clear();
		}

		// TpoMarketProfile's build: rows of TicksPerRow ticks, count of blocks per row, POC = most blocks (ties: nearest
		// the middle), value area grown two rows at a time from the POC; single prints = count 1 outside the end runs.
		private List<Level> Profile()
		{
			double row = TicksPerRow * TickSize;
			long lo = long.MaxValue, hi = long.MinValue;
			foreach (double[] b in blocks.Values) { lo = Math.Min(lo, RowOf(b[1], row)); hi = Math.Max(hi, RowOf(b[0], row)); }
			int n = (int)(hi - lo + 1);
			int[] cnt = new int[n];
			foreach (double[] b in blocks.Values)
				for (long r = RowOf(b[1], row); r <= RowOf(b[0], row); r++) cnt[r - lo]++;

			double middle = (n - 1) / 2.0, total = 0;
			int poc = 0;
			for (int i = 0; i < n; i++)
			{
				total += cnt[i];
				if (cnt[i] > cnt[poc] || (cnt[i] == cnt[poc] && Math.Abs(i - middle) < Math.Abs(poc - middle))) poc = i;
			}
			int up = poc, down = poc;
			double got = cnt[poc];
			while (got < total * ValueAreaPct / 100.0 && (up < n - 1 || down > 0))
			{
				int us = 0, ur = 0, ds = 0, dr = 0;
				for (int k = 1; k <= 2 && up + k <= n - 1; k++) { us += cnt[up + k]; ur++; }
				for (int k = 1; k <= 2 && down - k >= 0; k++) { ds += cnt[down - k]; dr++; }
				if (ur == 0) { got += ds; down -= dr; }
				else if (dr == 0) { got += us; up += ur; }
				else if (us > ds) { got += us; up += ur; }
				else if (ds > us) { got += ds; down -= dr; }
				else { got += us + ds; up += ur; down -= dr; }
			}
			var lv = new List<Level>
			{
				new Level { Kind = "POC", Price = Instrument.MasterInstrument.RoundToTickSize((lo + poc + 0.5) * row) },
				new Level { Kind = "VAH", Price = (lo + up + 1) * row },
				new Level { Kind = "VAL", Price = (lo + down) * row }
			};
			int top = 0, bottom = 0;
			while (top < n && cnt[n - 1 - top] == 1) top++;
			while (bottom < n && cnt[bottom] == 1) bottom++;
			if (top < n)
				for (int r = bottom; r < n - top; r++)
				{
					if (cnt[r] != 1) continue;
					int first = r;
					while (r + 1 < n - top && cnt[r + 1] == 1) r++;
					lv.Add(new Level { Kind = "SP", Price = Instrument.MasterInstrument.RoundToTickSize(((lo + first) * row + (lo + r + 1) * row) / 2) });
				}
			return lv;
		}

		private static long RowOf(double price, double row) { return (long)Math.Floor(price / row + 1e-9); }

		// Levels of the last SessionsBack sessions that had closed when this hour started
		private List<Level> LevelsAt(DateTime barStart)
		{
			DateTime startUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(barStart, DateTimeKind.Unspecified), Core.Globals.GeneralOptions.TimeZoneInfo);
			var lv = new List<Level>();
			lock (sync)
			{
				int k = sessions.Count;
				while (k > 0 && sessions[k - 1].EndUtc > startUtc) k--;
				for (int i = Math.Max(0, k - SessionsBack); i < k; i++) lv.AddRange(sessions[i].Levels);
			}
			return lv;
		}
		#endregion

		#region Signals
		private void CloseHour(int ago)
		{
			int bar = CurrentBar - ago;
			if (bar <= lastHourBar) return;
			lastHourBar = bar;
			Wick w = Find(ago);
			if (w == null) return;
			lock (sync) { wicks.Add(w); if (wicks.Count > 2000) wicks.RemoveAt(0); }
			if (State != State.Realtime || !VoiceAlerts) return;
			string next = w.TargetKind != null ? string.Format(" Next level {0}, {1:0} points.", Words(w.TargetKind), Math.Abs(w.Target - w.Close)) : "";
			if (w.WithTrend)
				Say(string.Format("Hourly {0} wick confirmed at {1} {2:0}, with the trend.{3}", w.Dir > 0 ? "long" : "short", Words(w.Kind), w.LevelPrice, next), true);
			else
				Say(string.Format("Hourly {0} wick at {1} {2:0}, against the trend.{3}", w.Dir > 0 ? "long" : "short", Words(w.Kind), w.LevelPrice, next), false);
		}

		// The wick signal of bar `ago` (null if none, both sides, or filtered out)
		private Wick Find(int ago)
		{
			double o = Open[ago], h = High[ago], l = Low[ago], c = Close[ago], range = h - l;
			if (range <= 0) return null;
			List<Level> lv = LevelsAt(Time[ago].AddMinutes(-BarsPeriod.Value));
			if (lv.Count == 0) return null;
			double lw = Math.Min(o, c) - l, uw = h - Math.Max(o, c), minWick = MinWickPct / 100.0 * range;
			Level bestL = null, bestS = null;
			foreach (Level x in lv)
			{
				if (l <= x.Price - ThroughPts && c > x.Price && lw >= minWick && (bestL == null || x.Price - l < bestL.Price - l)) bestL = x;
				if (h >= x.Price + ThroughPts && c < x.Price && uw >= minWick && (bestS == null || h - x.Price < h - bestS.Price)) bestS = x;
			}
			if ((bestL == null) == (bestS == null)) return null;
			int dir = bestL != null ? 1 : -1;
			if ((dir > 0 && !ShowLongs) || (dir < 0 && !ShowShorts)) return null;
			bool withTrend = dir > 0 ? c > sma[ago] : c < sma[ago];
			if (!withTrend && !ShowAgainstTrend) return null;
			Level lvl = dir > 0 ? bestL : bestS, target = null;
			foreach (Level x in lv)
				if (dir * (x.Price - c) >= MinTargetPts && (target == null || dir * (x.Price - target.Price) < 0)) target = x;
			return new Wick
			{
				Bar = CurrentBar - ago, Dir = dir, WithTrend = withTrend, Kind = lvl.Kind, LevelPrice = lvl.Price, Close = c,
				Tip = dir > 0 ? l : h, Body = dir > 0 ? Math.Min(o, c) : Math.Max(o, c),
				Target = target != null ? target.Price : 0, TargetKind = target != null ? target.Kind : null
			};
		}

		private static string Words(string kind)
		{
			switch (kind)
			{
				case "VAH": return "value area high";
				case "VAL": return "value area low";
				case "POC": return "P O C";
				default: return "single prints";
			}
		}
		#endregion

		#region Voice
		// once per bar per alert, and not again within the cooldown
		private bool Once(string key)
		{
			if (saidThisBar.Contains(key)) return false;
			saidThisBar.Add(key);
			DateTime t;
			if (spoke.TryGetValue(key, out t) && (DateTime.Now - t).TotalSeconds < CooldownSec) return false;
			spoke[key] = DateTime.Now; return true;
		}

		// high = clear anything queued or playing and use HighAlertVoice (SAPI XML; falls back to the default voice)
		private void Say(string text, bool high)
		{
			Print(string.Format("{0:HH:mm:ss} HourWickLevels {1}: {2}", DateTime.Now, high ? "HIGH" : "say", text));   // log of every alert
			if (chartControlRef == null) { Print("HourWickLevels: no chart, can't speak"); return; }
			chartControlRef.Dispatcher.InvokeAsync(new Action(() =>
			{
				try
				{
					if (voice == null) voice = Activator.CreateInstance(Type.GetTypeFromProgID("SAPI.SpVoice"));
					Com("Rate", BindingFlags.SetProperty, SpeechRate);
					if (high)
						Com("Speak", BindingFlags.InvokeMethod, string.Format("<voice optional=\"Name={0}\">{1}</voice>",
							System.Security.SecurityElement.Escape(HighAlertVoice), System.Security.SecurityElement.Escape(text)), 1 | 2 | 8);
					else
						Com("Speak", BindingFlags.InvokeMethod, text, 1);
				}
				catch (Exception ex) { Print("HourWickLevels voice: " + ex.Message); }
			}));
		}

		private object Com(string name, BindingFlags kind, params object[] args)
		{
			return voice.GetType().InvokeMember(name, kind, null, voice, args);
		}
		#endregion

		#region Rendering
		public override void OnRenderTargetChanged()
		{
			DisposeResources();
			if (RenderTarget != null)
			{
				dxUp = LongBrush.ToDxBrush(RenderTarget);
				dxDn = ShortBrush.ToDxBrush(RenderTarget);
				dxLevel = Brushes.Silver.ToDxBrush(RenderTarget);
				dashed = new SharpDX.Direct2D1.StrokeStyle(Core.Globals.D2DFactory, new SharpDX.Direct2D1.StrokeStyleProperties { DashStyle = SharpDX.Direct2D1.DashStyle.Dash });
				dotted = new SharpDX.Direct2D1.StrokeStyle(Core.Globals.D2DFactory, new SharpDX.Direct2D1.StrokeStyleProperties { DashStyle = SharpDX.Direct2D1.DashStyle.Dot });
			}
		}

		private void DisposeResources()
		{
			if (dxUp != null) { dxUp.Dispose(); dxUp = null; }
			if (dxDn != null) { dxDn.Dispose(); dxDn = null; }
			if (dxLevel != null) { dxLevel.Dispose(); dxLevel = null; }
			if (dashed != null) { dashed.Dispose(); dashed = null; }
			if (dotted != null) { dotted.Dispose(); dotted = null; }
			if (textFormat != null) { textFormat.Dispose(); textFormat = null; }
		}

		protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
		{
			if (dxUp == null || ChartBars == null || ChartBars.Count == 0) return;
			if (textFormat == null)
				textFormat = new SharpDX.DirectWrite.TextFormat(Core.Globals.DirectWriteFactory, "Arial", SharpDX.DirectWrite.FontWeight.Bold, SharpDX.DirectWrite.FontStyle.Normal, 10f);
			List<Wick> snap;
			lock (sync) { snap = new List<Wick>(wicks); if (forming != null) snap.Add(forming); }

			var oldAa = RenderTarget.AntialiasMode;
			RenderTarget.AntialiasMode = SharpDX.Direct2D1.AntialiasMode.PerPrimitive;
			float half = Math.Max(4f, (float)chartControl.BarWidth + 4f);

			if (DrawLevels && ChartBars.ToIndex >= 0)
			{
				float xr = (float)(ChartPanel.X + ChartPanel.W);
				dxLevel.Opacity = 0.5f;
				foreach (Level x in LevelsAt(Time.GetValueAt(Math.Min(CurrentBar, ChartBars.ToIndex)).AddMinutes(-BarsPeriod.Value)))
				{
					float y = chartScale.GetYByValue(x.Price);
					RenderTarget.DrawLine(new SharpDX.Vector2(ChartPanel.X, y), new SharpDX.Vector2(xr, y), dxLevel, 1f, dashed);
				}
				dxLevel.Opacity = 1f;
			}

			Wick latest = null;
			foreach (Wick w in snap)
			{
				if (w.Bar < ChartBars.FromIndex - 1 || w.Bar > ChartBars.ToIndex) continue;
				if (!w.Forming) latest = w;
				float x = chartControl.GetXByBarIndex(ChartBars, w.Bar);
				float yTip = chartScale.GetYByValue(w.Tip), yBody = chartScale.GetYByValue(w.Body);
				float ry = Math.Max(8f, Math.Abs(yBody - yTip) / 2 + 6f);
				var ring = new SharpDX.Direct2D1.Ellipse(new SharpDX.Vector2(x, (yTip + yBody) / 2), half + 4f, ry);
				SharpDX.Direct2D1.Brush brush = w.Dir > 0 ? dxUp : dxDn;
				brush.Opacity = w.Forming ? 0.8f : w.WithTrend ? 1f : 0.45f;
				if (w.Forming) RenderTarget.DrawEllipse(ring, brush, 2f, dotted);
				else if (w.WithTrend) RenderTarget.DrawEllipse(ring, brush, 2.5f);
				else RenderTarget.DrawEllipse(ring, brush, 1.5f, dashed);
				// level name just beyond the wick tip
				float ty = w.Dir > 0 ? yTip + 4f : yTip - 16f;
				RenderTarget.DrawText(w.Kind, textFormat, new SharpDX.RectangleF(x - 20f, ty, 40f, 14f), brush);
				brush.Opacity = 1f;
			}

			// the latest closed signal: dashed line to the next level with the points to it
			if (latest != null && latest.TargetKind != null && CurrentBar - latest.Bar <= TargetBars)
			{
				SharpDX.Direct2D1.Brush brush = latest.Dir > 0 ? dxUp : dxDn;
				float x0 = chartControl.GetXByBarIndex(ChartBars, latest.Bar), x1 = chartControl.GetXByBarIndex(ChartBars, latest.Bar + TargetBars);
				float y = chartScale.GetYByValue(latest.Target);
				brush.Opacity = latest.WithTrend ? 0.9f : 0.5f;
				RenderTarget.DrawLine(new SharpDX.Vector2(x0, y), new SharpDX.Vector2(x1, y), brush, 1.5f, dashed);
				string label = string.Format(CultureInfo.InvariantCulture, "{0} {1:0} ({2:+0;-0})", latest.TargetKind, latest.Target, latest.Dir * Math.Abs(latest.Target - latest.Close));
				RenderTarget.DrawText(label, textFormat, new SharpDX.RectangleF(x1 + 4f, y - 7f, 160f, 14f), brush);
				brush.Opacity = 1f;
			}
			RenderTarget.AntialiasMode = oldAa;
		}
		#endregion

		#region Properties
		[Display(Name = "Longs (green)", Order = 1, GroupName = "1. Signals")]
		public bool ShowLongs { get; set; }

		[Display(Name = "Shorts / sell longs (red)", Order = 2, GroupName = "1. Signals")]
		public bool ShowShorts { get; set; }

		[Display(Name = "Show against-trend wicks", Description = "Dashed and dim. Backtest: worst group (-0.10R in both halves).", Order = 3, GroupName = "1. Signals")]
		public bool ShowAgainstTrend { get; set; }

		[Display(Name = "Show forming wick", Description = "Dotted ring on the current hour while its wick qualifies; it can still disappear before the close.", Order = 4, GroupName = "1. Signals")]
		public bool ShowForming { get; set; }

		[Range(0, 100)]
		[Display(Name = "Through level (pts)", Description = "The wick must trade at least this far past the level.", Order = 5, GroupName = "1. Signals")]
		public double ThroughPts { get; set; }

		[Range(10, 90)]
		[Display(Name = "Min wick % of range", Order = 6, GroupName = "1. Signals")]
		public double MinWickPct { get; set; }

		[Range(2, 500)]
		[Display(Name = "Trend SMA (bars)", Description = "With the trend = long above / short below this SMA of the close.", Order = 7, GroupName = "1. Signals")]
		public int TrendPeriod { get; set; }

		[Range(1, 50)]
		[Display(Name = "Min points to next level", Order = 8, GroupName = "1. Signals")]
		public double MinTargetPts { get; set; }

		[Range(1, 48)]
		[Display(Name = "Target line bars", Description = "How long the latest signal's next-level line stays.", Order = 9, GroupName = "1. Signals")]
		public int TargetBars { get; set; }

		[Range(1, 10)]
		[Display(Name = "Sessions back", Description = "Like TpoMarketProfile's Highlight Sessions Back.", Order = 1, GroupName = "2. TPO levels")]
		public int SessionsBack { get; set; }

		[Range(1, 100)]
		[Display(Name = "Ticks per row", Description = "Match TpoMarketProfile (10).", Order = 2, GroupName = "2. TPO levels")]
		public int TicksPerRow { get; set; }

		[Range(50, 95)]
		[Display(Name = "Value area %", Order = 3, GroupName = "2. TPO levels")]
		public double ValueAreaPct { get; set; }

		[Display(Name = "Draw levels", Description = "Thin dashed lines at the levels in use (to check them against the TPO bands).", Order = 4, GroupName = "2. TPO levels")]
		public bool DrawLevels { get; set; }

		[XmlIgnore]
		[Display(Name = "Long color", Order = 1, GroupName = "3. Colors")]
		public Brush LongBrush { get; set; }
		[Browsable(false)]
		public string LongBrushSerializable { get { return Serialize.BrushToString(LongBrush); } set { LongBrush = Serialize.StringToBrush(value); } }

		[XmlIgnore]
		[Display(Name = "Short color", Order = 2, GroupName = "3. Colors")]
		public Brush ShortBrush { get; set; }
		[Browsable(false)]
		public string ShortBrushSerializable { get { return Serialize.BrushToString(ShortBrush); } set { ShortBrush = Serialize.StringToBrush(value); } }

		[Display(Name = "Voice alerts", Order = 1, GroupName = "4. Voice")]
		public bool VoiceAlerts { get; set; }

		[Display(Name = "Forming wick voice", Description = "Also speak when a wick is forming (once per hour per side).", Order = 2, GroupName = "4. Voice")]
		public bool FormingVoice { get; set; }

		[Range(0, 59)]
		[Display(Name = "Forming: min minutes into hour", Description = "Don't speak a forming wick before this many minutes of the hour have passed.", Order = 3, GroupName = "4. Voice")]
		public int FormingMinMinutes { get; set; }

		[Range(0, 3600)]
		[Display(Name = "Cooldown (sec)", Order = 4, GroupName = "4. Voice")]
		public int CooldownSec { get; set; }

		[Range(-5, 5)]
		[Display(Name = "Speech rate", Order = 5, GroupName = "4. Voice")]
		public int SpeechRate { get; set; }

		[Display(Name = "High alert voice", Description = "Used for confirmed with-trend wicks. e.g. Microsoft Zira Desktop.", Order = 6, GroupName = "4. Voice")]
		public string HighAlertVoice { get; set; }
		#endregion
	}
}
