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
#endregion

// Big trades: same-side market orders grouped over a short window (default 500 ms) and drawn as bubbles at their
// average price, green = market buys, red = market sells, size ~ contracts. After the bubble each one is graded:
//   solid  = aggression, price ran PushPoints past the bubble within OutcomeSeconds
//   hollow = absorption, no push and price traded back through the bubble price within BackSeconds
//   faint  = neither
// Works on any chart: trades come from a 1-tick series (bid/ask per trade), in history and live.
// Research (2026-11-07, 44 NQ sessions): no mechanical edge from the bubbles alone; this is a discretionary aid.
public enum BigTradesSizeMode { Fixed, Auto }

namespace NinjaTrader.NinjaScript.Indicators
{
	public class BigTrades : Indicator
	{
		private class Bubble
		{
			public DateTime Time;       // last trade of the group
			public double Price;        // volume-weighted price of the group
			public double Lo, Hi;
			public long Size;
			public int Side;            // 1 buy, -1 sell
			public int Outcome;         // 0 pending, 1 aggression, 2 absorption, 3 neither
		}

		// Group being built for each side (index 0 = buys, 1 = sells)
		private readonly DateTime[] grpStart = new DateTime[2];
		private readonly DateTime[] grpLast = new DateTime[2];
		private readonly long[] grpVol = new long[2];
		private readonly double[] grpPV = new double[2], grpLo = new double[2], grpHi = new double[2];

		private readonly List<Bubble> bubbles = new List<Bubble>();    // drawn; under bubbleLock
		private readonly object bubbleLock = new object();
		private readonly List<Bubble> pending = new List<Bubble>();    // waiting for their outcome (data thread only)
		private double lastPrice;
		private int lastSide = 1;

		// Bar coloring: the chart bar holding the latest bubble is the anchor for the next ColorBarsCount bars
		private int newAnchorBar = -1;     // set by the tick series, picked up when that chart bar closes
		private int anchorBar = -1;
		private double anchorHigh, anchorLow;

		// Auto size: the AutoRank-th largest group of 09:30-10:00 ET sets the minimum for the rest of the day
		private TimeZoneInfo eastern;
		private DateTime autoDay = DateTime.MinValue;
		private readonly List<long> openingSizes = new List<long>();
		private long autoMin;

		// Logger (live bubbles only): rows queued here, written by a timer on a pool thread
		private readonly object logLock = new object();
		private List<string> logQueue = new List<string>();
		private Timer logTimer;
		private string instName;    // read on the data thread; the timer thread only uses this copy

		private SharpDX.Direct2D1.Brush dxBuy, dxSell, dxText;
		private SharpDX.DirectWrite.TextFormat textFormat;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description = "Big trades: same-side market orders grouped over a short window, drawn as bubbles (green buys, red sells). Solid = pushed price (aggression), hollow = failed and price came back (absorption).";
				Name = "BigTrades";
				Calculate = Calculate.OnBarClose;
				IsOverlay = true;
				DisplayInDataBox = false;
				PaintPriceMarkers = false;
				IsSuspendedWhileInactive = false;

				MinSize = 250;
				SizeMode = BigTradesSizeMode.Fixed;
				AutoRank = 10;
				GroupMs = 500;
				PushPoints = 8;
				OutcomeSeconds = 60;
				BackSeconds = 300;
				BubbleScale = 1.0;
				ShowLabels = true;
				AlertSize = 0;
				ColorBars = true;
				ColorBarsCount = 20;
				UpBarBrush = Brushes.LimeGreen;
				DownBarBrush = Brushes.Red;
				LogBubbles = false;
				LogFolder = Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "analysis", "bubbles");
				BuyBrush = Brushes.LimeGreen;
				SellBrush = Brushes.Red;
			}
			else if (State == State.Configure)
			{
				AddDataSeries(BarsPeriodType.Tick, 1);
			}
			else if (State == State.DataLoaded)
			{
				eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");   // handles DST
				instName = Instrument.MasterInstrument.Name;
				if (LogBubbles)
					logTimer = new Timer(o => FlushLog(), null, 2000, 2000);
			}
			else if (State == State.Terminated)
			{
				if (logTimer != null)
				{
					ManualResetEvent done = new ManualResetEvent(false);
					logTimer.Dispose(done);
					done.WaitOne(5000);
					logTimer = null;
					FlushLog();
				}
				DisposeResources();
			}
		}

		protected override void OnBarUpdate()
		{
			if (BarsInProgress == 0)
			{
				ColorBar();
				return;
			}
			if (BarsInProgress != 1)
				return;
			int b = CurrentBars[1];
			if (b < 0 || Volumes[1][0] <= 0)
				return;

			// At/above the ask = market buy, at/below the bid = market sell, inside the spread = skipped.
			// Without quotes: the tick rule (up = buy, down = sell, same = as before).
			double price = Closes[1][0];
			double ask = BarsArray[1].GetAsk(b), bid = BarsArray[1].GetBid(b);
			int side;
			if (ask > 0 && bid > 0)
				side = price >= ask ? 1 : price <= bid ? -1 : 0;
			else
				side = price > lastPrice ? 1 : price < lastPrice ? -1 : lastSide;
			lastPrice = price;
			if (side != 0)
				lastSide = side;

			DateTime t = Times[1][0];
			UpdateOutcomes(t, price);

			// Close groups whose window has passed, on either side
			for (int i = 0; i < 2; i++)
				if (grpVol[i] > 0 && (t - grpStart[i]).TotalMilliseconds > GroupMs)
					CloseGroup(i);
			if (side == 0)
				return;

			int g = side > 0 ? 0 : 1;
			long vol = (long)Volumes[1][0];
			if (grpVol[g] == 0)
			{
				grpStart[g] = t;
				grpPV[g] = 0;
				grpLo[g] = grpHi[g] = price;
			}
			grpVol[g] += vol;
			grpPV[g] += price * vol;
			grpLo[g] = Math.Min(grpLo[g], price);
			grpHi[g] = Math.Max(grpHi[g], price);
			grpLast[g] = t;
		}

		private void CloseGroup(int g)
		{
			long size = grpVol[g];
			grpVol[g] = 0;
			DateTime et = TimeZoneInfo.ConvertTime(DateTime.SpecifyKind(grpLast[g], DateTimeKind.Unspecified), NinjaTrader.Core.Globals.GeneralOptions.TimeZoneInfo, eastern);
			if (size >= Threshold(et, size))
			{
				Bubble bb = new Bubble { Time = grpLast[g], Price = grpPV[g] / size, Lo = grpLo[g], Hi = grpHi[g], Size = size, Side = g == 0 ? 1 : -1 };
				lock (bubbleLock)
					bubbles.Add(bb);
				pending.Add(bb);
				if (ColorBars)
					newAnchorBar = BarsArray[0].GetBar(bb.Time);   // the chart bar that holds the bubble
				if (State == State.Realtime && AlertSize > 0 && size >= AlertSize)
					Alert("BigTrades" + grpLast[g].Ticks, Priority.Medium, (bb.Side > 0 ? "Big BUY " : "Big SELL ") + size + " @ " + Instrument.MasterInstrument.FormatPrice(bb.Price),
						NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert2.wav", 5, bb.Side > 0 ? Brushes.DarkGreen : Brushes.DarkRed, Brushes.White);
			}
		}

		// Chart bar closed: the next ColorBarsCount bars after the latest bubble's bar are painted green when both their high
		// and low are above that bar's (higher high, higher low), red when both are below, and left alone when mixed.
		private void ColorBar()
		{
			if (!ColorBars)
				return;
			if (newAnchorBar >= 0 && newAnchorBar <= CurrentBar)
			{
				int ago = CurrentBar - newAnchorBar;
				anchorBar = newAnchorBar;
				anchorHigh = High[ago];
				anchorLow = Low[ago];
				newAnchorBar = -1;
			}
			int n = CurrentBar - anchorBar;
			if (anchorBar < 0 || n < 1 || n > ColorBarsCount)
				return;
			if (High[0] > anchorHigh && Low[0] > anchorLow)
				BarBrush = UpBarBrush;
			else if (High[0] < anchorHigh && Low[0] < anchorLow)
				BarBrush = DownBarBrush;
		}

		// Minimum size for a bubble at this US Eastern time
		private long Threshold(DateTime et, long size)
		{
			if (SizeMode == BigTradesSizeMode.Fixed)
				return MinSize;
			if (et.Date != autoDay)
			{
				autoDay = et.Date;
				openingSizes.Clear();
				autoMin = 0;
			}
			TimeSpan tod = et.TimeOfDay;
			if (tod >= new TimeSpan(9, 30, 0) && tod < new TimeSpan(10, 0, 0))
			{
				openingSizes.Add(size);
				return MinSize;
			}
			if (tod >= new TimeSpan(10, 0, 0) && autoMin == 0 && openingSizes.Count > 0)
			{
				openingSizes.Sort();
				autoMin = openingSizes[Math.Max(0, openingSizes.Count - AutoRank)];
			}
			return autoMin > 0 && tod >= new TimeSpan(10, 0, 0) ? autoMin : MinSize;
		}

		private void UpdateOutcomes(DateTime t, double price)
		{
			double tick = TickSize;
			for (int i = pending.Count - 1; i >= 0; i--)
			{
				Bubble bb = pending[i];
				double secs = (t - bb.Time).TotalSeconds;
				double extreme = bb.Side > 0 ? bb.Hi : bb.Lo;
				int outcome = 0;
				if (secs <= OutcomeSeconds && bb.Side * (price - extreme) >= PushPoints)
					outcome = 1;
				else if (-bb.Side * (price - bb.Price) >= 2 * tick)
					outcome = 2;
				else if (secs > BackSeconds)
					outcome = 3;
				if (outcome == 0)
					continue;
				bb.Outcome = outcome;
				pending.RemoveAt(i);
				if (LogBubbles && State == State.Realtime)
					QueueLog(bb);
			}
		}

		#region Logging
		private void QueueLog(Bubble bb)
		{
			DateTime et = TimeZoneInfo.ConvertTime(DateTime.SpecifyKind(bb.Time, DateTimeKind.Unspecified), NinjaTrader.Core.Globals.GeneralOptions.TimeZoneInfo, eastern);
			CultureInfo ci = CultureInfo.InvariantCulture;
			string line = et.ToString("yyyy-MM-dd", ci) + "," + et.ToString("HH:mm:ss.fff", ci) + "," + bb.Side + "," + bb.Size + ","
				+ bb.Price.ToString("0.00", ci) + "," + bb.Lo.ToString(ci) + "," + bb.Hi.ToString(ci) + "," + (bb.Outcome == 1 ? "aggression" : bb.Outcome == 2 ? "absorption" : "none");
			lock (logLock)
				logQueue.Add(line);
		}

		// Pool thread (timer) and Terminated: one file per instrument and day, appended
		private void FlushLog()
		{
			List<string> batch;
			lock (logLock)
			{
				if (logQueue.Count == 0)
					return;
				batch = logQueue;
				logQueue = new List<string>();
			}
			try
			{
				Directory.CreateDirectory(LogFolder);
				foreach (string line in batch)
				{
					string path = Path.Combine(LogFolder, instName + "_bubbles_" + line.Substring(0, 10).Replace("-", "") + ".csv");
					bool isNew = !File.Exists(path);
					using (StreamWriter w = new StreamWriter(path, true))
					{
						if (isNew)
							w.Write("date_et,time_et,side,size,price,low,high,outcome\r\n");
						w.Write(line + "\r\n");
					}
				}
			}
			catch (Exception ex)
			{
				// File locked (OneDrive, Excel): keep the rows and retry on the next tick
				lock (logLock)
				{
					batch.AddRange(logQueue);
					logQueue = batch;
				}
				Print("BigTrades log: " + ex.Message + " (will retry)");
			}
		}
		#endregion

		#region Rendering
		public override void OnRenderTargetChanged()
		{
			DisposeResources();
			if (RenderTarget != null)
			{
				dxBuy = BuyBrush.ToDxBrush(RenderTarget);
				dxSell = SellBrush.ToDxBrush(RenderTarget);
				dxText = Brushes.White.ToDxBrush(RenderTarget);
			}
		}

		private void DisposeResources()
		{
			if (dxBuy != null) { dxBuy.Dispose(); dxBuy = null; }
			if (dxSell != null) { dxSell.Dispose(); dxSell = null; }
			if (dxText != null) { dxText.Dispose(); dxText = null; }
			if (textFormat != null) { textFormat.Dispose(); textFormat = null; }
		}

		protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
		{
			if (dxBuy == null || ChartBars == null || ChartBars.Count == 0)
				return;
			if (textFormat == null)
				textFormat = new SharpDX.DirectWrite.TextFormat(NinjaTrader.Core.Globals.DirectWriteFactory, "Arial", SharpDX.DirectWrite.FontWeight.Bold, SharpDX.DirectWrite.FontStyle.Normal, 10f)
				{ TextAlignment = SharpDX.DirectWrite.TextAlignment.Center, ParagraphAlignment = SharpDX.DirectWrite.ParagraphAlignment.Center };

			DateTime from = ChartBars.GetTimeByBarIdx(chartControl, Math.Max(0, ChartBars.FromIndex - 1));
			DateTime to = ChartBars.GetTimeByBarIdx(chartControl, ChartBars.ToIndex);
			Bubble[] snap;
			lock (bubbleLock)
				snap = bubbles.ToArray();

			var oldAa = RenderTarget.AntialiasMode;
			RenderTarget.AntialiasMode = SharpDX.Direct2D1.AntialiasMode.PerPrimitive;
			foreach (Bubble bb in snap)
			{
				if (bb.Time < from || bb.Time > to)
					continue;
				float x = chartControl.GetXByTime(bb.Time);
				float y = chartScale.GetYByValue(bb.Price);
				float r = (float)Math.Min(40, Math.Max(4, 7 * BubbleScale * Math.Sqrt((double)bb.Size / Math.Max(1, MinSize))));
				var ellipse = new SharpDX.Direct2D1.Ellipse(new SharpDX.Vector2(x, y), r, r);
				SharpDX.Direct2D1.Brush brush = bb.Side > 0 ? dxBuy : dxSell;

				// solid = aggression, hollow ring = absorption, faint = neither, half = still pending
				if (bb.Outcome == 2)
					RenderTarget.DrawEllipse(ellipse, brush, 2.5f);
				else
				{
					brush.Opacity = bb.Outcome == 1 ? 0.9f : bb.Outcome == 3 ? 0.3f : 0.6f;
					RenderTarget.FillEllipse(ellipse, brush);
					brush.Opacity = 1f;
				}
				if (ShowLabels && r >= 9)
					RenderTarget.DrawText(bb.Size.ToString(CultureInfo.InvariantCulture), textFormat, new SharpDX.RectangleF(x - 30, y - r, 60, 2 * r), dxText);
			}
			RenderTarget.AntialiasMode = oldAa;
		}
		#endregion

		#region Properties
		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Min size (contracts)", Description = "Smallest group drawn. NQ: 250 ~ 10 a day (Sep-Nov 2026). In Auto mode used until 10:00 ET.", Order = 1, GroupName = "Big trades")]
		public int MinSize { get; set; }

		[Display(Name = "Size mode", Description = "Fixed: always Min size. Auto: from 10:00 ET, the Nth largest group of 09:30-10:00 ET.", Order = 2, GroupName = "Big trades")]
		public BigTradesSizeMode SizeMode { get; set; }

		[Range(1, 100)]
		[Display(Name = "Auto rank", Description = "Auto mode: the minimum is the Nth largest group of the first 30 minutes.", Order = 3, GroupName = "Big trades")]
		public int AutoRank { get; set; }

		[Range(1, 10000)]
		[Display(Name = "Group window (ms)", Description = "Same-side trades within this time of the group's first trade are added together.", Order = 4, GroupName = "Big trades")]
		public int GroupMs { get; set; }

		[Range(0.25, 1000)]
		[Display(Name = "Push (points)", Description = "Aggression: price runs this far past the bubble within the outcome window.", Order = 5, GroupName = "Outcome")]
		public double PushPoints { get; set; }

		[Range(1, 3600)]
		[Display(Name = "Outcome window (s)", Order = 6, GroupName = "Outcome")]
		public int OutcomeSeconds { get; set; }

		[Range(1, 3600)]
		[Display(Name = "Absorption window (s)", Description = "Absorption: price trades 2 ticks back through the bubble price within this time.", Order = 7, GroupName = "Outcome")]
		public int BackSeconds { get; set; }

		[Range(0.1, 10)]
		[Display(Name = "Bubble scale", Order = 8, GroupName = "Display")]
		public double BubbleScale { get; set; }

		[Display(Name = "Show size labels", Order = 9, GroupName = "Display")]
		public bool ShowLabels { get; set; }

		[XmlIgnore]
		[Display(Name = "Buy color", Order = 10, GroupName = "Display")]
		public Brush BuyBrush { get; set; }

		[Browsable(false)]
		public string BuyBrushSerializable { get { return Serialize.BrushToString(BuyBrush); } set { BuyBrush = Serialize.StringToBrush(value); } }

		[XmlIgnore]
		[Display(Name = "Sell color", Order = 11, GroupName = "Display")]
		public Brush SellBrush { get; set; }

		[Browsable(false)]
		public string SellBrushSerializable { get { return Serialize.BrushToString(SellBrush); } set { SellBrush = Serialize.StringToBrush(value); } }

		[Range(0, int.MaxValue)]
		[Display(Name = "Alert size (0 = off)", Description = "Live only: sound and Alerts window entry for groups at least this big.", Order = 12, GroupName = "Alerts and log")]
		public int AlertSize { get; set; }

		[Display(Name = "Log live bubbles", Description = "Appends each live bubble and its outcome to one CSV per day, for forward testing.", Order = 13, GroupName = "Alerts and log")]
		public bool LogBubbles { get; set; }

		[Display(Name = "Log folder", Order = 14, GroupName = "Alerts and log")]
		public string LogFolder { get; set; }

		[Display(Name = "Color bars after bubble", Description = "Paint the next bars green when high and low are both above the bubble's bar, red when both below.", Order = 15, GroupName = "Bar colors")]
		public bool ColorBars { get; set; }

		[Range(1, 500)]
		[Display(Name = "Bars to color", Order = 16, GroupName = "Bar colors")]
		public int ColorBarsCount { get; set; }

		[XmlIgnore]
		[Display(Name = "Higher high / higher low color", Order = 17, GroupName = "Bar colors")]
		public Brush UpBarBrush { get; set; }

		[Browsable(false)]
		public string UpBarBrushSerializable { get { return Serialize.BrushToString(UpBarBrush); } set { UpBarBrush = Serialize.StringToBrush(value); } }

		[XmlIgnore]
		[Display(Name = "Lower low / lower high color", Order = 18, GroupName = "Bar colors")]
		public Brush DownBarBrush { get; set; }

		[Browsable(false)]
		public string DownBarBrushSerializable { get { return Serialize.BrushToString(DownBarBrush); } set { DownBarBrush = Serialize.StringToBrush(value); } }
		#endregion
	}
}

