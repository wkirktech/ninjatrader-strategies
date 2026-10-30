#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

// Cumulative delta (trades at the ask minus trades at the bid) drawn as candles, with two EMAs, delta swing
// structure and price/delta divergence. Flags are exposed for strategies; an optional logger writes one CSV row
// per closed bar (price, session VWAP and SD, EMA, delta, flags) for offline backtests.
namespace NinjaTrader.NinjaScript.Indicators
{
	public enum DeltaResetMode { Session, Never }

	public class DeltaConfluence : Indicator
	{
		// Swing structure: a swing is confirmed once price reverses by at least the threshold from its extreme
		private class SwingTracker
		{
			public int Dir = 1;            // 1: tracking a high, -1: tracking a low
			public double Extreme = double.NaN;
			public int ExtremeBar;
			public double LastHigh = double.NaN, PrevHigh = double.NaN, LastLow = double.NaN, PrevLow = double.NaN;
			public int LastHighBar = -1, LastLowBar = -1;

			// Returns 1 when a swing high was confirmed on this bar, -1 for a swing low, 0 otherwise
			public int Update(int bar, double high, double low, double threshold)
			{
				if (double.IsNaN(Extreme))
				{
					Extreme = high;
					ExtremeBar = bar;
					return 0;
				}
				if (Dir > 0)
				{
					if (high >= Extreme)
					{
						Extreme = high;
						ExtremeBar = bar;
					}
					else if (Extreme - low >= threshold)
					{
						PrevHigh = LastHigh;
						LastHigh = Extreme;
						LastHighBar = ExtremeBar;
						Dir = -1;
						Extreme = low;
						ExtremeBar = bar;
						return 1;
					}
				}
				else
				{
					if (low <= Extreme)
					{
						Extreme = low;
						ExtremeBar = bar;
					}
					else if (high - Extreme >= threshold)
					{
						PrevLow = LastLow;
						LastLow = Extreme;
						LastLowBar = ExtremeBar;
						Dir = 1;
						Extreme = high;
						ExtremeBar = bar;
						return -1;
					}
				}
				return 0;
			}
		}

		// Running totals, applied in OnBarUpdate (pending* are the ticks since the last call)
		private double cum, pendingDelta;
		private double sumV, sumPV, sumP2V, pendingV, pendingPV, pendingP2V;
		private double lastPrice;
		private int lastSide = 1;
		private double barHigh, barLow;
		private bool sawTick;

		private Series<double> vwapSeries, sdSeries, priceEma;
		private Series<int> emaState, hlState, divergence;
		private SwingTracker priceZz, deltaZz;
		private double prevDivPriceLow = double.NaN, prevDivDeltaLow = double.NaN, prevDivPriceHigh = double.NaN, prevDivDeltaHigh = double.NaN;
		private int lastDivergenceBar = -1;

		// Logger: lines are queued on the indicator thread and written by a timer on a pool thread
		private readonly object logLock = new object();
		private List<KeyValuePair<string, string>> logQueue = new List<KeyValuePair<string, string>>();
		private HashSet<string> logStarted = new HashSet<string>();   // files (re)created in this run; pool thread only
		private Timer logTimer;
		private bool logSession;   // log only sessions that started while loaded (no partial first day)
		private const string LogHeader = "date,time,open,high,low,close,volume,vwap,sd,ema,d_open,d_high,d_low,d_close,d_ema_fast,d_ema_slow,ema_state,hl_state,divergence";

		private SharpDX.Direct2D1.Brush dxUp, dxDown, dxWick;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description = "Cumulative delta candles (ask trades minus bid trades) with EMAs, delta swing structure and price/delta divergence. Needs Tick Replay for history.";
				Name = "DeltaConfluence";
				Calculate = Calculate.OnEachTick;
				IsOverlay = false;
				DrawOnPricePanel = false;
				DisplayInDataBox = true;
				PaintPriceMarkers = true;
				IsSuspendedWhileInactive = false;
				MaximumBarsLookBack = MaximumBarsLookBack.Infinite;

				ResetMode = DeltaResetMode.Session;
				EmaFast = 21;
				EmaSlow = 55;
				PriceEmaPeriod = 55;
				PriceSwingSd = 0.15;
				MinSwingPts = 5;
				DeltaSwingMult = 3;
				DeltaSwingBars = 20;
				DivToleranceTicks = 2;
				ShowMarkers = true;
				LogBars = false;
				LogFolder = Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "analysis", "bars");
				UpBrush = Brushes.WhiteSmoke;
				DownBrush = Brushes.Black;

				AddPlot(Brushes.Transparent, "DeltaOpen");
				AddPlot(Brushes.Transparent, "DeltaHigh");
				AddPlot(Brushes.Transparent, "DeltaLow");
				AddPlot(Brushes.Transparent, "DeltaClose");
				AddPlot(Brushes.Goldenrod, "DeltaEmaFast");
				AddPlot(Brushes.DarkGoldenrod, "DeltaEmaSlow");
			}
			else if (State == State.DataLoaded)
			{
				vwapSeries = new Series<double>(this);
				sdSeries = new Series<double>(this);
				priceEma = new Series<double>(this);
				emaState = new Series<int>(this);
				hlState = new Series<int>(this);
				divergence = new Series<int>(this);
				priceZz = new SwingTracker();
				deltaZz = new SwingTracker();
				if (LogBars)
					logTimer = new Timer(o => FlushLog(), null, 2000, 2000);
			}

			else if (State == State.Terminated)
			{
				if (logTimer != null)
				{
					logTimer.Dispose();
					logTimer = null;
					FlushLog();
				}
			}
		}

		protected override void OnMarketData(MarketDataEventArgs e)
		{
			if (e.MarketDataType != MarketDataType.Last || e.Volume <= 0)
				return;
			double price = e.Price;
			int side;
			if (e.Ask > 0 && price >= e.Ask)
				side = 1;
			else if (e.Bid > 0 && price <= e.Bid)
				side = -1;
			else
				side = price > lastPrice ? 1 : price < lastPrice ? -1 : lastSide;   // inside the spread: tick rule
			lastPrice = price;
			lastSide = side;

			double v = e.Volume;
			pendingDelta += side * v;
			pendingV += v;
			pendingPV += price * v;
			pendingP2V += price * price * v;
			sawTick = true;
		}

		protected override void OnBarUpdate()
		{
			if (CurrentBar == 0 && State == State.Historical && !Bars.IsTickReplay)
				Draw.TextFixed(this, "dcWarn", "DeltaConfluence: enable Tick Replay on this chart for historical delta", TextPosition.BottomLeft);

			bool first = IsFirstTickOfBar;
			bool reset = first && Bars.IsFirstBarOfSession && (ResetMode == DeltaResetMode.Session || CurrentBar == 0);
			if (first && Bars.IsFirstBarOfSession)
			{
				// VWAP always resets with the session; the delta only in Session mode
				sumV = sumPV = sumP2V = 0;
				logSession = LogBars && CurrentBar > 0;
			}
			if (reset)
				cum = 0;

			// A closed bar: structure and flags for bar 1 before this bar starts
			if (first && CurrentBar > 0)
				CloseBar();

			cum += pendingDelta;
			sumV += pendingV;
			sumPV += pendingPV;
			sumP2V += pendingP2V;
			pendingDelta = pendingV = pendingPV = pendingP2V = 0;

			if (first)
			{
				double open = reset || CurrentBar == 0 ? 0 : DeltaClose[1];
				barHigh = Math.Max(open, cum);
				barLow = Math.Min(open, cum);
				DeltaOpen[0] = open;
			}
			barHigh = Math.Max(barHigh, cum);
			barLow = Math.Min(barLow, cum);
			DeltaHigh[0] = barHigh;
			DeltaLow[0] = barLow;
			DeltaClose[0] = cum;

			double aF = 2.0 / (EmaFast + 1), aS = 2.0 / (EmaSlow + 1), aP = 2.0 / (PriceEmaPeriod + 1);
			DeltaEmaFast[0] = CurrentBar == 0 ? cum : DeltaEmaFast[1] + aF * (cum - DeltaEmaFast[1]);
			DeltaEmaSlow[0] = CurrentBar == 0 ? cum : DeltaEmaSlow[1] + aS * (cum - DeltaEmaSlow[1]);
			priceEma[0] = CurrentBar == 0 ? Close[0] : priceEma[1] + aP * (Close[0] - priceEma[1]);

			if (sumV > 0)
			{
				double vwap = sumPV / sumV;
				vwapSeries[0] = vwap;
				sdSeries[0] = Math.Sqrt(Math.Max(0, sumP2V / sumV - vwap * vwap));
			}
			else
			{
				vwapSeries[0] = Close[0];
				sdSeries[0] = 0;
			}
		}

		// Runs once per closed bar (index 1), on the first tick of the next bar
		private void CloseBar()
		{
			int bar = CurrentBar - 1;
			emaState[1] = DeltaEmaFast[1] > DeltaEmaSlow[1] ? 1 : DeltaEmaFast[1] < DeltaEmaSlow[1] ? -1 : 0;

			// Price swings: a share of the session SD, never under MinSwingPts
			double pThr = Math.Max(MinSwingPts, PriceSwingSd * sdSeries[1]);
			int pSwing = priceZz.Update(bar, High[1], Low[1], pThr);

			// Delta swings: DeltaSwingMult x the average delta bar range
			double avgRange = 0;
			int n = Math.Min(DeltaSwingBars, CurrentBar);
			for (int i = 1; i <= n; i++)
				avgRange += DeltaHigh[i] - DeltaLow[i];
			avgRange = n > 0 ? avgRange / n : 0;
			int dSwing = deltaZz.Update(bar, DeltaHigh[1], DeltaLow[1], Math.Max(1, DeltaSwingMult * avgRange));

			bool hl = !double.IsNaN(deltaZz.PrevLow) && deltaZz.LastLow > deltaZz.PrevLow;
			bool lh = !double.IsNaN(deltaZz.PrevHigh) && deltaZz.LastHigh < deltaZz.PrevHigh;
			hlState[1] = hl && !lh ? 1 : lh && !hl ? -1 : 0;

			if (ShowMarkers && dSwing != 0)
			{
				if (dSwing < 0 && hl)
					Draw.Dot(this, "dcHL" + deltaZz.LastLowBar, false, CurrentBar - deltaZz.LastLowBar, deltaZz.LastLow, Brushes.LimeGreen);
				else if (dSwing > 0 && lh)
					Draw.Dot(this, "dcLH" + deltaZz.LastHighBar, false, CurrentBar - deltaZz.LastHighBar, deltaZz.LastHigh, Brushes.OrangeRed);
			}

			// Divergence: price makes an equal or lower swing low while delta at that low is higher (mirror for highs)
			int div = 0;
			double tol = DivToleranceTicks * TickSize;
			if (pSwing < 0)
			{
				double dAt = DeltaLowAround(priceZz.LastLowBar);
				if (!double.IsNaN(prevDivPriceLow) && priceZz.LastLow <= prevDivPriceLow + tol && dAt > prevDivDeltaLow)
					div = 1;
				prevDivPriceLow = priceZz.LastLow;
				prevDivDeltaLow = dAt;
			}
			else if (pSwing > 0)
			{
				double dAt = DeltaHighAround(priceZz.LastHighBar);
				if (!double.IsNaN(prevDivPriceHigh) && priceZz.LastHigh >= prevDivPriceHigh - tol && dAt < prevDivDeltaHigh)
					div = -1;
				prevDivPriceHigh = priceZz.LastHigh;
				prevDivDeltaHigh = dAt;
			}
			divergence[1] = div;
			if (div != 0)
			{
				lastDivergenceBar = bar;
				if (ShowMarkers)
				{
					if (div > 0)
						Draw.TriangleUp(this, "dcDiv" + bar, false, 1, DeltaLow[1], Brushes.Cyan);
					else
						Draw.TriangleDown(this, "dcDiv" + bar, false, 1, DeltaHigh[1], Brushes.Magenta);
				}
			}

			if (logSession)
				QueueLogLine();
		}

		// Delta low (high) within a bar of the price swing bar: the delta extreme rarely lands on the exact same bar
		private double DeltaLowAround(int swingBar)
		{
			double v = double.MaxValue;
			for (int b = Math.Max(0, swingBar - 1); b <= Math.Min(CurrentBar - 1, swingBar + 1); b++)
				v = Math.Min(v, DeltaLow.GetValueAt(b));
			return v;
		}

		private double DeltaHighAround(int swingBar)
		{
			double v = double.MinValue;
			for (int b = Math.Max(0, swingBar - 1); b <= Math.Min(CurrentBar - 1, swingBar + 1); b++)
				v = Math.Max(v, DeltaHigh.GetValueAt(b));
			return v;
		}

		#region Logger
		private void QueueLogLine()
		{
			DateTime t = Time[1];
			string name = Instrument.MasterInstrument.Name + "_" + BarsPeriod.Value.ToString(CultureInfo.InvariantCulture)
				+ BarsPeriod.BarsPeriodType.ToString() + "_" + t.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".csv";
			CultureInfo ci = CultureInfo.InvariantCulture;
			StringBuilder sb = new StringBuilder(200);
			sb.Append(t.ToString("yyyy-MM-dd", ci)).Append(',').Append(t.ToString("HH:mm:ss", ci)).Append(',')
				.Append(Open[1].ToString(ci)).Append(',').Append(High[1].ToString(ci)).Append(',').Append(Low[1].ToString(ci)).Append(',').Append(Close[1].ToString(ci)).Append(',')
				.Append(Volume[1].ToString(ci)).Append(',')
				.Append(Math.Round(vwapSeries[1], 2).ToString(ci)).Append(',').Append(Math.Round(sdSeries[1], 2).ToString(ci)).Append(',').Append(Math.Round(priceEma[1], 2).ToString(ci)).Append(',')
				.Append(DeltaOpen[1].ToString(ci)).Append(',').Append(DeltaHigh[1].ToString(ci)).Append(',').Append(DeltaLow[1].ToString(ci)).Append(',').Append(DeltaClose[1].ToString(ci)).Append(',')
				.Append(Math.Round(DeltaEmaFast[1], 1).ToString(ci)).Append(',').Append(Math.Round(DeltaEmaSlow[1], 1).ToString(ci)).Append(',')
				.Append(emaState[1].ToString(ci)).Append(',').Append(hlState[1].ToString(ci)).Append(',').Append(divergence[1].ToString(ci));
			lock (logLock)
				logQueue.Add(new KeyValuePair<string, string>(Path.Combine(LogFolder, name), sb.ToString()));
		}

		// Pool thread (timer) and Terminated. A file is recreated with the header the first time this run writes it,
		// so reloading the chart replaces a day instead of duplicating it.
		private void FlushLog()
		{
			List<KeyValuePair<string, string>> batch;
			lock (logLock)
			{
				if (logQueue.Count == 0)
					return;
				batch = logQueue;
				logQueue = new List<KeyValuePair<string, string>>();
			}
			try
			{
				Directory.CreateDirectory(LogFolder);
				Dictionary<string, StringBuilder> byFile = new Dictionary<string, StringBuilder>();
				foreach (KeyValuePair<string, string> kv in batch)
				{
					StringBuilder sb;
					if (!byFile.TryGetValue(kv.Key, out sb))
						byFile[kv.Key] = sb = new StringBuilder();
					sb.Append(kv.Value).Append("\r\n");
				}
				lock (logStarted)
				{
					foreach (KeyValuePair<string, StringBuilder> f in byFile)
					{
						if (logStarted.Add(f.Key))
							File.WriteAllText(f.Key, LogHeader + "\r\n" + f.Value);
						else
							File.AppendAllText(f.Key, f.Value.ToString());
					}
				}
			}
			catch (Exception ex)
			{
				Print("DeltaConfluence log: " + ex.Message);
			}
		}
		#endregion

		#region Rendering
		public override void OnRenderTargetChanged()
		{
			DisposeBrushes();
			if (RenderTarget != null)
			{
				dxUp = UpBrush.ToDxBrush(RenderTarget);
				dxDown = DownBrush.ToDxBrush(RenderTarget);
				dxWick = UpBrush.ToDxBrush(RenderTarget);
			}
		}

		private void DisposeBrushes()
		{
			if (dxUp != null) { dxUp.Dispose(); dxUp = null; }
			if (dxDown != null) { dxDown.Dispose(); dxDown = null; }
			if (dxWick != null) { dxWick.Dispose(); dxWick = null; }
		}

		protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
		{
			if (dxUp == null || ChartBars == null)
				return;
			float w = Math.Max(1f, (float)chartControl.GetBarPaintWidth(ChartBars));
			for (int idx = ChartBars.FromIndex; idx <= ChartBars.ToIndex; idx++)
			{
				if (!DeltaClose.IsValidDataPointAt(idx) || !DeltaOpen.IsValidDataPointAt(idx))
					continue;
				double o = DeltaOpen.GetValueAt(idx), h = DeltaHigh.GetValueAt(idx), l = DeltaLow.GetValueAt(idx), c = DeltaClose.GetValueAt(idx);
				float x = chartControl.GetXByBarIndex(ChartBars, idx);
				float yO = chartScale.GetYByValue(o), yH = chartScale.GetYByValue(h), yL = chartScale.GetYByValue(l), yC = chartScale.GetYByValue(c);
				RenderTarget.DrawLine(new SharpDX.Vector2(x, yH), new SharpDX.Vector2(x, yL), dxWick, 1f);
				float top = Math.Min(yO, yC), height = Math.Max(1f, Math.Abs(yC - yO));
				SharpDX.RectangleF body = new SharpDX.RectangleF(x - w / 2f, top, w, height);
				RenderTarget.FillRectangle(body, c >= o ? dxUp : dxDown);
				RenderTarget.DrawRectangle(body, dxWick, 1f);
			}
			base.OnRender(chartControl, chartScale);   // the EMA plots on top
		}
		#endregion

		#region Properties
		[Browsable(false)] [XmlIgnore] public Series<double> DeltaOpen { get { return Values[0]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> DeltaHigh { get { return Values[1]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> DeltaLow { get { return Values[2]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> DeltaClose { get { return Values[3]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> DeltaEmaFast { get { return Values[4]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> DeltaEmaSlow { get { return Values[5]; } }

		// Flags, set when a bar closes: +1 bullish, -1 bearish, 0 none
		[Browsable(false)] [XmlIgnore] public Series<int> EmaState { get { return emaState; } }
		[Browsable(false)] [XmlIgnore] public Series<int> HlState { get { return hlState; } }
		[Browsable(false)] [XmlIgnore] public Series<int> Divergence { get { return divergence; } }
		[Browsable(false)] [XmlIgnore] public int LastDivergenceBar { get { return lastDivergenceBar; } }
		[Browsable(false)] [XmlIgnore] public bool HasTicks { get { return sawTick; } }

		[NinjaScriptProperty]
		[Display(Name = "Reset delta", Description = "Session: delta starts at 0 each session. Never: one running total.", Order = 1, GroupName = "1. Delta")]
		public DeltaResetMode ResetMode { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Delta EMA fast", Order = 2, GroupName = "1. Delta")]
		public int EmaFast { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Delta EMA slow", Order = 3, GroupName = "1. Delta")]
		public int EmaSlow { get; set; }

		[NinjaScriptProperty]
		[Range(0.1, double.MaxValue)]
		[Display(Name = "Delta swing size (x avg bar)", Description = "A delta swing is confirmed after a reversal of this many average delta bar ranges.", Order = 4, GroupName = "1. Delta")]
		public double DeltaSwingMult { get; set; }

		[NinjaScriptProperty]
		[Range(2, int.MaxValue)]
		[Display(Name = "Delta swing average bars", Order = 5, GroupName = "1. Delta")]
		public int DeltaSwingBars { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Price EMA (logged)", Order = 1, GroupName = "2. Price structure")]
		public int PriceEmaPeriod { get; set; }

		[NinjaScriptProperty]
		[Range(0.01, double.MaxValue)]
		[Display(Name = "Price swing size (x session SD)", Description = "A price swing is confirmed after a reversal of this share of the session VWAP standard deviation.", Order = 2, GroupName = "2. Price structure")]
		public double PriceSwingSd { get; set; }

		[NinjaScriptProperty]
		[Range(0.25, double.MaxValue)]
		[Display(Name = "Min price swing (pts)", Order = 3, GroupName = "2. Price structure")]
		public double MinSwingPts { get; set; }

		[NinjaScriptProperty]
		[Range(0, int.MaxValue)]
		[Display(Name = "Divergence equal-low tolerance (ticks)", Order = 4, GroupName = "2. Price structure")]
		public int DivToleranceTicks { get; set; }

		[Display(Name = "Show markers", Description = "Dots on delta higher lows / lower highs, triangles on divergences.", Order = 1, GroupName = "3. Display")]
		public bool ShowMarkers { get; set; }

		[XmlIgnore]
		[Display(Name = "Up candle", Order = 2, GroupName = "3. Display")]
		public Brush UpBrush { get; set; }

		[Browsable(false)]
		public string UpBrushSerialize
		{
			get { return Serialize.BrushToString(UpBrush); }
			set { UpBrush = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Down candle", Order = 3, GroupName = "3. Display")]
		public Brush DownBrush { get; set; }

		[Browsable(false)]
		public string DownBrushSerialize
		{
			get { return Serialize.BrushToString(DownBrush); }
			set { DownBrush = Serialize.StringToBrush(value); }
		}

		[Display(Name = "Log bars to CSV", Description = "One row per closed bar, one file per day, for offline backtests. Only sessions that start while loaded (no partial first day).", Order = 1, GroupName = "4. Logger")]
		public bool LogBars { get; set; }

		[Display(Name = "Log folder", Order = 2, GroupName = "4. Logger")]
		public string LogFolder { get; set; }
		#endregion
	}
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private DeltaConfluence[] cacheDeltaConfluence;
		public DeltaConfluence DeltaConfluence(DeltaResetMode resetMode, int emaFast, int emaSlow, double deltaSwingMult, int deltaSwingBars, int priceEmaPeriod, double priceSwingSd, double minSwingPts, int divToleranceTicks)
		{
			return DeltaConfluence(Input, resetMode, emaFast, emaSlow, deltaSwingMult, deltaSwingBars, priceEmaPeriod, priceSwingSd, minSwingPts, divToleranceTicks);
		}

		public DeltaConfluence DeltaConfluence(ISeries<double> input, DeltaResetMode resetMode, int emaFast, int emaSlow, double deltaSwingMult, int deltaSwingBars, int priceEmaPeriod, double priceSwingSd, double minSwingPts, int divToleranceTicks)
		{
			if (cacheDeltaConfluence != null)
				for (int idx = 0; idx < cacheDeltaConfluence.Length; idx++)
					if (cacheDeltaConfluence[idx] != null && cacheDeltaConfluence[idx].ResetMode == resetMode && cacheDeltaConfluence[idx].EmaFast == emaFast && cacheDeltaConfluence[idx].EmaSlow == emaSlow && cacheDeltaConfluence[idx].DeltaSwingMult == deltaSwingMult && cacheDeltaConfluence[idx].DeltaSwingBars == deltaSwingBars && cacheDeltaConfluence[idx].PriceEmaPeriod == priceEmaPeriod && cacheDeltaConfluence[idx].PriceSwingSd == priceSwingSd && cacheDeltaConfluence[idx].MinSwingPts == minSwingPts && cacheDeltaConfluence[idx].DivToleranceTicks == divToleranceTicks && cacheDeltaConfluence[idx].EqualsInput(input))
						return cacheDeltaConfluence[idx];
			return CacheIndicator<DeltaConfluence>(new DeltaConfluence(){ ResetMode = resetMode, EmaFast = emaFast, EmaSlow = emaSlow, DeltaSwingMult = deltaSwingMult, DeltaSwingBars = deltaSwingBars, PriceEmaPeriod = priceEmaPeriod, PriceSwingSd = priceSwingSd, MinSwingPts = minSwingPts, DivToleranceTicks = divToleranceTicks }, input, ref cacheDeltaConfluence);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.DeltaConfluence DeltaConfluence(DeltaResetMode resetMode, int emaFast, int emaSlow, double deltaSwingMult, int deltaSwingBars, int priceEmaPeriod, double priceSwingSd, double minSwingPts, int divToleranceTicks)
		{
			return indicator.DeltaConfluence(Input, resetMode, emaFast, emaSlow, deltaSwingMult, deltaSwingBars, priceEmaPeriod, priceSwingSd, minSwingPts, divToleranceTicks);
		}

		public Indicators.DeltaConfluence DeltaConfluence(ISeries<double> input , DeltaResetMode resetMode, int emaFast, int emaSlow, double deltaSwingMult, int deltaSwingBars, int priceEmaPeriod, double priceSwingSd, double minSwingPts, int divToleranceTicks)
		{
			return indicator.DeltaConfluence(input, resetMode, emaFast, emaSlow, deltaSwingMult, deltaSwingBars, priceEmaPeriod, priceSwingSd, minSwingPts, divToleranceTicks);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.DeltaConfluence DeltaConfluence(DeltaResetMode resetMode, int emaFast, int emaSlow, double deltaSwingMult, int deltaSwingBars, int priceEmaPeriod, double priceSwingSd, double minSwingPts, int divToleranceTicks)
		{
			return indicator.DeltaConfluence(Input, resetMode, emaFast, emaSlow, deltaSwingMult, deltaSwingBars, priceEmaPeriod, priceSwingSd, minSwingPts, divToleranceTicks);
		}

		public Indicators.DeltaConfluence DeltaConfluence(ISeries<double> input , DeltaResetMode resetMode, int emaFast, int emaSlow, double deltaSwingMult, int deltaSwingBars, int priceEmaPeriod, double priceSwingSd, double minSwingPts, int divToleranceTicks)
		{
			return indicator.DeltaConfluence(input, resetMode, emaFast, emaSlow, deltaSwingMult, deltaSwingBars, priceEmaPeriod, priceSwingSd, minSwingPts, divToleranceTicks);
		}
	}
}

#endregion
