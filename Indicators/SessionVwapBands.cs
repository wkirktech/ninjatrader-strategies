#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
#endregion

// Session VWAP with volume-weighted standard deviation bands, half-band midlines and range midpoints, for mean reversion.
//   Anchor TradingHours: resets on the chart's trading-hours session (Globex, 17:00 CT for CME index futures).
//   Anchor Custom: bars whose close time (exchange time zone, CT for CME) is after SessionStart and at or before SessionEnd.
//   Defaults (TradingHours, OHLC/4, 1/2/3 SD) match TTN ZWAPBandos on the 377 chart: checked 2026-11-10, VWAP exact,
//   SD within 0.002 pt (its Full_Session mode ignores the custom times).
//   VWAP = sum(v*p) / sum(v); SD = sqrt(sum(v*p^2) / sum(v) - VWAP^2), with p measured from the session's first open so
//   the sums keep their precision. Bands = VWAP +/- Sd1/Sd2/Sd3 x SD; midlines sit halfway between neighbouring lines.
//   Z = (close - VWAP) / SD, for strategies and the Python twin (analysis\research\scripts\session_vwap_bands.py).
// Range midpoints on the SessionStart-SessionEnd window whatever the anchor (plotted inside that window, once fully known):
//   prior session mid  (high + low) / 2 of the previous session window
//   overnight mid      (high + low) / 2 of the bars between the previous session end and this session start
//   IB mid             (high + low) / 2 of the first IbMinutes of the session, from the IB's end
// Every value is per bar and rebuilt from the bar before, so the forming bar can be recalculated on each tick.
public enum SessionVwapPrice { Close, Typical, Ohlc4 }
public enum SessionVwapAnchor { TradingHours, Custom }

namespace NinjaTrader.NinjaScript.Indicators
{
	public class SessionVwapBands : Indicator
	{
		// Running sums and range tracking, one value per bar (NaN = none yet)
		private Series<double> sumV, sumPV, sumP2V, refPrice, sigma, zScore;
		private Series<double> sessHigh, sessLow, onHigh, onLow, ibHigh, ibLow, priorMid, onMid;
		private TimeZoneInfo platformZone, exchangeZone;
		private TimeSpan start, end;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description = "Session VWAP with volume-weighted SD bands, half-band midlines and prior / overnight / IB midpoints.";
				Name = "SessionVwapBands";
				Calculate = Calculate.OnPriceChange;
				IsOverlay = true;
				DisplayInDataBox = true;
				DrawOnPricePanel = true;
				PaintPriceMarkers = true;
				IsSuspendedWhileInactive = true;

				Anchor = SessionVwapAnchor.TradingHours;
				PriceSource = SessionVwapPrice.Ohlc4;
				SessionStart = "08:30";
				SessionEnd = "15:15";
				Sd1 = 1;
				Sd2 = 2;
				Sd3 = 3;
				IbMinutes = 60;
				ShowMidlines = true;
				ShowRangeMids = true;

				AddPlot(new Stroke(Brushes.Gold, DashStyleHelper.DashDot, 3), PlotStyle.Line, "VWAP");
				AddPlot(new Stroke(Brushes.SkyBlue, 1), PlotStyle.Line, "Upper1");
				AddPlot(new Stroke(Brushes.DodgerBlue, 1), PlotStyle.Line, "Upper2");
				AddPlot(new Stroke(Brushes.RoyalBlue, 1), PlotStyle.Line, "Upper3");
				AddPlot(new Stroke(Brushes.SkyBlue, 1), PlotStyle.Line, "Lower1");
				AddPlot(new Stroke(Brushes.DodgerBlue, 1), PlotStyle.Line, "Lower2");
				AddPlot(new Stroke(Brushes.RoyalBlue, 1), PlotStyle.Line, "Lower3");
				AddPlot(new Stroke(Brushes.Gray, DashStyleHelper.Dot, 1), PlotStyle.Line, "UpperMid0");
				AddPlot(new Stroke(Brushes.Gray, DashStyleHelper.Dot, 1), PlotStyle.Line, "UpperMid1");
				AddPlot(new Stroke(Brushes.Gray, DashStyleHelper.Dot, 1), PlotStyle.Line, "UpperMid2");
				AddPlot(new Stroke(Brushes.Gray, DashStyleHelper.Dot, 1), PlotStyle.Line, "LowerMid0");
				AddPlot(new Stroke(Brushes.Gray, DashStyleHelper.Dot, 1), PlotStyle.Line, "LowerMid1");
				AddPlot(new Stroke(Brushes.Gray, DashStyleHelper.Dot, 1), PlotStyle.Line, "LowerMid2");
				AddPlot(new Stroke(Brushes.Orchid, DashStyleHelper.Dash, 1), PlotStyle.Line, "PriorMid");
				AddPlot(new Stroke(Brushes.MediumPurple, DashStyleHelper.Dash, 1), PlotStyle.Line, "OvernightMid");
				AddPlot(new Stroke(Brushes.Khaki, DashStyleHelper.Dash, 1), PlotStyle.Line, "IbMid");
			}
			else if (State == State.Configure)
			{
				start = ParseTime(SessionStart, new TimeSpan(8, 30, 0));
				end = ParseTime(SessionEnd, new TimeSpan(15, 15, 0));
			}
			else if (State == State.DataLoaded)
			{
				sumV = new Series<double>(this); sumPV = new Series<double>(this); sumP2V = new Series<double>(this);
				refPrice = new Series<double>(this); sigma = new Series<double>(this); zScore = new Series<double>(this);
				sessHigh = new Series<double>(this); sessLow = new Series<double>(this);
				onHigh = new Series<double>(this); onLow = new Series<double>(this);
				ibHigh = new Series<double>(this); ibLow = new Series<double>(this);
				priorMid = new Series<double>(this); onMid = new Series<double>(this);
				platformZone = Core.Globals.GeneralOptions.TimeZoneInfo;
				exchangeZone = Bars.TradingHours != null ? Bars.TradingHours.TimeZoneInfo : platformZone;
			}
		}

		protected override void OnBarUpdate()
		{
			DateTime t0 = Exchange(Time[0]);
			bool rth0 = InSession(t0);
			bool rth1 = CurrentBar > 0 && InSession(Exchange(Time[1]));
			bool newRth = rth0 && !(rth1 && Exchange(Time[1]).Date == t0.Date);
			bool custom = Anchor == SessionVwapAnchor.Custom;
			bool in0 = custom ? rth0 : true;
			bool newSession = custom ? newRth : CurrentBar == 0 || Bars.IsFirstBarOfSession;

			// Carry the range trackers forward (session high/low lives on through the overnight as the prior session)
			double sh = CurrentBar > 0 ? sessHigh[1] : double.NaN, sl = CurrentBar > 0 ? sessLow[1] : double.NaN;
			double oh = CurrentBar > 0 ? onHigh[1] : double.NaN, ol = CurrentBar > 0 ? onLow[1] : double.NaN;
			double pm = CurrentBar > 0 ? priorMid[1] : double.NaN, om = CurrentBar > 0 ? onMid[1] : double.NaN;
			double ih = CurrentBar > 0 ? ibHigh[1] : double.NaN, il = CurrentBar > 0 ? ibLow[1] : double.NaN;

			if (newRth)
			{
				pm = Mid(sh, sl);          // last session's range is complete
				om = Mid(oh, ol);          // so is the overnight
				sh = High[0]; sl = Low[0];
				oh = ol = double.NaN;
				ih = High[0]; il = Low[0];
			}
			else if (rth0)
			{
				sh = Math.Max(sh, High[0]); sl = Math.Min(sl, Low[0]);
				if (t0.TimeOfDay <= start.Add(TimeSpan.FromMinutes(IbMinutes))) { ih = Math.Max(ih, High[0]); il = Math.Min(il, Low[0]); }
			}
			else
			{
				oh = double.IsNaN(oh) ? High[0] : Math.Max(oh, High[0]);
				ol = double.IsNaN(ol) ? Low[0] : Math.Min(ol, Low[0]);
			}
			sessHigh[0] = sh; sessLow[0] = sl; onHigh[0] = oh; onLow[0] = ol;
			priorMid[0] = pm; onMid[0] = om; ibHigh[0] = ih; ibLow[0] = il;

			if (!in0) return;

			double rp = newSession ? Open[0] : refPrice[1];
			double p = Price(0) - rp, v = Volume[0];
			double sv = (newSession ? 0 : sumV[1]) + v;
			double spv = (newSession ? 0 : sumPV[1]) + v * p;
			double sp2v = (newSession ? 0 : sumP2V[1]) + v * p * p;
			refPrice[0] = rp; sumV[0] = sv; sumPV[0] = spv; sumP2V[0] = sp2v;
			if (sv <= 0) return;

			double mean = spv / sv;
			double sd = Math.Sqrt(Math.Max(0, sp2v / sv - mean * mean));
			double vwap = rp + mean;
			sigma[0] = sd;
			zScore[0] = sd > 0 ? (Close[0] - vwap) / sd : 0;

			Values[0][0] = vwap;
			Values[1][0] = vwap + Sd1 * sd; Values[2][0] = vwap + Sd2 * sd; Values[3][0] = vwap + Sd3 * sd;
			Values[4][0] = vwap - Sd1 * sd; Values[5][0] = vwap - Sd2 * sd; Values[6][0] = vwap - Sd3 * sd;
			if (ShowMidlines)
			{
				Values[7][0] = vwap + Sd1 * sd / 2; Values[8][0] = vwap + (Sd1 + Sd2) * sd / 2; Values[9][0] = vwap + (Sd2 + Sd3) * sd / 2;
				Values[10][0] = vwap - Sd1 * sd / 2; Values[11][0] = vwap - (Sd1 + Sd2) * sd / 2; Values[12][0] = vwap - (Sd2 + Sd3) * sd / 2;
			}
			if (ShowRangeMids && rth0)
			{
				if (!double.IsNaN(pm)) Values[13][0] = pm;
				if (!double.IsNaN(om)) Values[14][0] = om;
				if (t0.TimeOfDay >= start.Add(TimeSpan.FromMinutes(IbMinutes))) Values[15][0] = Mid(ih, il);
			}
		}

		private double Price(int ago)
		{
			switch (PriceSource)
			{
				case SessionVwapPrice.Typical: return (High[ago] + Low[ago] + Close[ago]) / 3;
				case SessionVwapPrice.Ohlc4: return (Open[ago] + High[ago] + Low[ago] + Close[ago]) / 4;
				default: return Close[ago];
			}
		}

		private DateTime Exchange(DateTime platformTime)
		{
			return TimeZoneInfo.ConvertTime(DateTime.SpecifyKind(platformTime, DateTimeKind.Unspecified), platformZone, exchangeZone);
		}

		private bool InSession(DateTime t)
		{
			return t.TimeOfDay > start && t.TimeOfDay <= end;
		}

		private static double Mid(double hi, double lo)
		{
			return double.IsNaN(hi) || double.IsNaN(lo) ? double.NaN : (hi + lo) / 2;
		}

		private static TimeSpan ParseTime(string s, TimeSpan fallback)
		{
			TimeSpan t;
			return TimeSpan.TryParseExact(s ?? "", @"hh\:mm", CultureInfo.InvariantCulture, out t) ? t : fallback;
		}

		#region Properties
		[Browsable(false)] [XmlIgnore] public Series<double> VWAP { get { return Values[0]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> Upper1 { get { return Values[1]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> Upper2 { get { return Values[2]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> Upper3 { get { return Values[3]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> Lower1 { get { return Values[4]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> Lower2 { get { return Values[5]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> Lower3 { get { return Values[6]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> PriorMid { get { return Values[13]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> OvernightMid { get { return Values[14]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> IbMid { get { return Values[15]; } }
		/// <summary>Session SD in points (set on session bars only).</summary>
		[Browsable(false)] [XmlIgnore] public Series<double> Sigma { get { return sigma; } }
		/// <summary>(close - VWAP) / SD (set on session bars only).</summary>
		[Browsable(false)] [XmlIgnore] public Series<double> Z { get { return zScore; } }

		[NinjaScriptProperty]
		[Display(Name = "Anchor", Description = "TradingHours = reset with the chart's session (Globex, like ZWAPBandos Full_Session). Custom = the session window below.", Order = 0, GroupName = "VWAP")]
		public SessionVwapAnchor Anchor { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Price", Description = "Price weighted into the VWAP. OHLC/4 matches ZWAPBandos on the 377 chart.", Order = 1, GroupName = "VWAP")]
		public SessionVwapPrice PriceSource { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Session start (HH:mm)", Description = "Exchange time zone (CT for CME). Custom anchor: bars closing after this start the session. Also the range-midpoint window.", Order = 2, GroupName = "VWAP")]
		public string SessionStart { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Session end (HH:mm)", Description = "Exchange time zone. Bars closing after this are overnight.", Order = 3, GroupName = "VWAP")]
		public string SessionEnd { get; set; }

		[NinjaScriptProperty]
		[Range(0.1, 10)]
		[Display(Name = "SD 1", Order = 4, GroupName = "Bands")]
		public double Sd1 { get; set; }

		[NinjaScriptProperty]
		[Range(0.1, 10)]
		[Display(Name = "SD 2", Order = 5, GroupName = "Bands")]
		public double Sd2 { get; set; }

		[NinjaScriptProperty]
		[Range(0.1, 10)]
		[Display(Name = "SD 3", Order = 6, GroupName = "Bands")]
		public double Sd3 { get; set; }

		[Display(Name = "Show midlines", Description = "Halfway lines between VWAP / SD 1 / SD 2 / SD 3.", Order = 7, GroupName = "Bands")]
		public bool ShowMidlines { get; set; }

		[NinjaScriptProperty]
		[Range(5, 240)]
		[Display(Name = "IB minutes", Description = "Initial balance length from the session start.", Order = 1, GroupName = "Range midpoints")]
		public int IbMinutes { get; set; }

		[Display(Name = "Show range midpoints", Description = "Prior session, overnight and IB midpoints.", Order = 2, GroupName = "Range midpoints")]
		public bool ShowRangeMids { get; set; }
		#endregion
	}
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private SessionVwapBands[] cacheSessionVwapBands;
		public SessionVwapBands SessionVwapBands(SessionVwapAnchor anchor, SessionVwapPrice priceSource, string sessionStart, string sessionEnd, double sd1, double sd2, double sd3, int ibMinutes)
		{
			return SessionVwapBands(Input, anchor, priceSource, sessionStart, sessionEnd, sd1, sd2, sd3, ibMinutes);
		}

		public SessionVwapBands SessionVwapBands(ISeries<double> input, SessionVwapAnchor anchor, SessionVwapPrice priceSource, string sessionStart, string sessionEnd, double sd1, double sd2, double sd3, int ibMinutes)
		{
			if (cacheSessionVwapBands != null)
				for (int idx = 0; idx < cacheSessionVwapBands.Length; idx++)
					if (cacheSessionVwapBands[idx] != null && cacheSessionVwapBands[idx].Anchor == anchor && cacheSessionVwapBands[idx].PriceSource == priceSource && cacheSessionVwapBands[idx].SessionStart == sessionStart && cacheSessionVwapBands[idx].SessionEnd == sessionEnd && cacheSessionVwapBands[idx].Sd1 == sd1 && cacheSessionVwapBands[idx].Sd2 == sd2 && cacheSessionVwapBands[idx].Sd3 == sd3 && cacheSessionVwapBands[idx].IbMinutes == ibMinutes && cacheSessionVwapBands[idx].EqualsInput(input))
						return cacheSessionVwapBands[idx];
			return CacheIndicator<SessionVwapBands>(new SessionVwapBands(){ Anchor = anchor, PriceSource = priceSource, SessionStart = sessionStart, SessionEnd = sessionEnd, Sd1 = sd1, Sd2 = sd2, Sd3 = sd3, IbMinutes = ibMinutes }, input, ref cacheSessionVwapBands);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.SessionVwapBands SessionVwapBands(SessionVwapAnchor anchor, SessionVwapPrice priceSource, string sessionStart, string sessionEnd, double sd1, double sd2, double sd3, int ibMinutes)
		{
			return indicator.SessionVwapBands(Input, anchor, priceSource, sessionStart, sessionEnd, sd1, sd2, sd3, ibMinutes);
		}

		public Indicators.SessionVwapBands SessionVwapBands(ISeries<double> input , SessionVwapAnchor anchor, SessionVwapPrice priceSource, string sessionStart, string sessionEnd, double sd1, double sd2, double sd3, int ibMinutes)
		{
			return indicator.SessionVwapBands(input, anchor, priceSource, sessionStart, sessionEnd, sd1, sd2, sd3, ibMinutes);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.SessionVwapBands SessionVwapBands(SessionVwapAnchor anchor, SessionVwapPrice priceSource, string sessionStart, string sessionEnd, double sd1, double sd2, double sd3, int ibMinutes)
		{
			return indicator.SessionVwapBands(Input, anchor, priceSource, sessionStart, sessionEnd, sd1, sd2, sd3, ibMinutes);
		}

		public Indicators.SessionVwapBands SessionVwapBands(ISeries<double> input , SessionVwapAnchor anchor, SessionVwapPrice priceSource, string sessionStart, string sessionEnd, double sd1, double sd2, double sd3, int ibMinutes)
		{
			return indicator.SessionVwapBands(input, anchor, priceSource, sessionStart, sessionEnd, sd1, sd2, sd3, ibMinutes);
		}
	}
}

#endregion
