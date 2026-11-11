#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
#endregion

// PMARP (The_Caretaker's formula, matched to PMARP_V2: research corr 1.000) with voice alerts, for whatever chart it's on.
//   PMAR = close / VWMA(close, 20); PMARP = % of the previous 350 PMAR values <= now; signal = SMA(PMARP, 20).
//   Slope = PMARP change over SlopeBars bars: > +SlopeThreshold rising, < -SlopeThreshold falling, else flat (plot color);
//   a move of TurnPts off the current run's low (high) is a turn up (down) straight away, so V-turns aren't missed.
//   Updates on every price change like PMARP_V2; alerts fire inside the forming bar, at most once per bar each.
// Voice (live bars only, Windows built-in voice):
//   warning     slope flattens after a run, or turns the other way
//   high alert  PMARP crosses its signal line (second voice, interrupts anything queued)
//   continue    the run holds: once when it reaches 80 / 20, then every ContinuationMin minutes
namespace NinjaTrader.NinjaScript.Indicators
{
	public class PMARPv3 : Indicator
	{
		private Series<double> pmar;
		private int state, lastDir;
		private DateTime runSince = DateTime.MinValue;
		private bool extremeSaid;
		private readonly Dictionary<string, DateTime> spoke = new Dictionary<string, DateTime>();
		private ChartControl chartControlRef;
		private object voice;   // SAPI.SpVoice (COM), created and used on the chart's UI thread

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description = "PMARP with slope coloring and voice alerts: slope turns (warning), signal-line crosses (high alert) and continuation.";
				Name = "PMARPv3";
				Calculate = Calculate.OnPriceChange;   // live like PMARP_V2
				IsOverlay = false;
				DisplayInDataBox = true;
				IsSuspendedWhileInactive = false;   // keep talking when the chart tab isn't in front

				VwmaPeriod = 20;
				Lookback = 350;
				SignalPeriod = 20;
				SlopeBars = 3;
				SlopeThreshold = 5;
				TurnPts = 5;
				VoiceAlerts = true;
				ContinuationMin = 10;
				CooldownSec = 120;
				SpeechRate = 1;
				HighAlertVoice = "Microsoft Zira Desktop";

				AddPlot(new Stroke(Brushes.DodgerBlue, 2), PlotStyle.Line, "PMARP");
				AddPlot(new Stroke(Brushes.Orange, 1), PlotStyle.Line, "Signal");
				AddLine(Brushes.Gray, 80, "High");
				AddLine(Brushes.DimGray, 50, "Mid");
				AddLine(Brushes.Gray, 20, "Low");
			}
			else if (State == State.DataLoaded)
				pmar = new Series<double>(this, MaximumBarsLookBack.Infinite);
			else if (State == State.Historical)
				chartControlRef = ChartControl;
			else if (State == State.Terminated && chartControlRef != null && voice != null)
			{
				object v = voice; voice = null;
				chartControlRef.Dispatcher.InvokeAsync(new Action(() => { try { Marshal.FinalReleaseComObject(v); } catch { } }));
			}
		}

		// Live like PMARP_V2: the forming bar's PMARP / signal / color update on every price change and warnings / high alerts
		// fire as soon as the forming bar shows them (each at most once per bar). The run (direction, its extreme, the
		// continuation timer) only moves on closed bars, so a flicker inside one bar can't flip it.
		private int lastCommitted = -1;
		private bool startSaid;
		private double runExt;
		private readonly HashSet<string> saidThisBar = new HashSet<string>();

		protected override void OnBarUpdate()
		{
			if (CurrentBar < VwmaPeriod) return;
			double sv = 0, scv = 0;
			for (int i = 0; i < VwmaPeriod; i++) { sv += Volume[i]; scv += Close[i] * Volume[i]; }
			if (sv <= 0) return;
			pmar[0] = Close[0] / (scv / sv);
			if (CurrentBar < VwmaPeriod + Lookback) return;

			int le = 0;
			for (int i = 1; i <= Lookback; i++) if (pmar[i] <= pmar[0]) le++;
			Value[0] = 100.0 * le / Lookback;
			if (CurrentBar < VwmaPeriod + Lookback + SignalPeriod) return;
			double sig = 0;
			for (int i = 0; i < SignalPeriod; i++) sig += Value[i];
			Signal[0] = sig / SignalPeriod;
			if (CurrentBar < VwmaPeriod + Lookback + SignalPeriod + SlopeBars + 2) return;

			bool live = State == State.Realtime;
			if (live && !startSaid) { startSaid = true; if (VoiceAlerts) Say("P MARP " + TimeframeWords() + " voice alerts on.", false); }
			// history: every call is a closed bar. Live: the bar that just closed is [1] on the first tick of the next one.
			if (!live) Commit(0);
			else if (IsFirstTickOfBar) { Commit(1); saidThisBar.Clear(); }

			int now = Classify(0);
			PlotBrushes[0][0] = now > 0 ? Brushes.LimeGreen : now < 0 ? Brushes.Tomato : Brushes.Gray;
			if (!live || !VoiceAlerts) return;

			string tf = "P MARP " + TimeframeWords();
			double pm = Value[0];
			int cross = Value[0] > Signal[0] && Value[1] <= Signal[1] ? 1 : Value[0] < Signal[0] && Value[1] >= Signal[1] ? -1 : 0;
			if (cross != 0 && Once("cross " + cross))
				Say(string.Format("High alert. {0} crossed {1} its signal line, at {2:0}.", tf, cross > 0 ? "above" : "below", pm), true);
			else if (now != 0 && lastDir != 0 && now != lastDir && Once("turn " + now))
				Say(string.Format("Warning. {0} turning {1} from {2:0}, now {3:0}.", tf, now > 0 ? "up" : "down", runExt, pm), false);
			else if (now == 0 && state != 0 && Once("flat " + state))
				Say(string.Format("Warning. {0} flattening after a {1}, at {2:0}.", tf, state > 0 ? "rise" : "drop", pm), false);
		}

		// +1 rising / -1 falling / 0 flat for the bar `ago`, against the current run:
		// a move of TurnPts off the run's extreme is a turn; otherwise the SlopeBars change decides.
		private int Classify(int ago)
		{
			double v = Value[ago], slope = v - Value[ago + SlopeBars];
			if (lastDir < 0 && v - runExt >= TurnPts) return 1;
			if (lastDir > 0 && runExt - v >= TurnPts) return -1;
			return slope > SlopeThreshold ? 1 : slope < -SlopeThreshold ? -1 : 0;
		}

		// Closed bar: move the run, then the continuation reminders (live only)
		private void Commit(int ago)
		{
			int bar = CurrentBar - ago;
			if (bar <= lastCommitted) return;
			lastCommitted = bar;
			double v = Value[ago];
			int now = Classify(ago);
			if (now != 0 && now != lastDir) { lastDir = now; runExt = v; }
			else if (lastDir > 0) runExt = Math.Max(runExt, v);
			else if (lastDir < 0) runExt = Math.Min(runExt, v);
			if (now != state) { runSince = Time[ago]; extremeSaid = false; }

			if (State == State.Realtime && VoiceAlerts && now != 0 && now == state)
			{
				string tf = "P MARP " + TimeframeWords();
				bool extreme = now > 0 ? v >= 80 : v <= 20;
				if (extreme && !extremeSaid)
				{
					extremeSaid = true; runSince = Time[ago];
					Say(string.Format("{0} still {1}, {2} {3}. Strong move.", tf, now > 0 ? "rising" : "falling", now > 0 ? "above" : "below", now > 0 ? 80 : 20), false);
				}
				else if (ContinuationMin > 0 && (Time[ago] - runSince).TotalMinutes >= ContinuationMin)
				{
					runSince = Time[ago];
					Say(string.Format("{0} still {1}, now {2:0}.", tf, now > 0 ? "rising" : "falling", v), false);
				}
			}
			state = now;
		}

		// once per bar per alert, and not again within the cooldown
		private bool Once(string key)
		{
			if (saidThisBar.Contains(key)) return false;
			saidThisBar.Add(key);
			return Fresh(key);
		}

		private string TimeframeWords()
		{
			switch (BarsPeriod.BarsPeriodType)
			{
				case BarsPeriodType.Minute: return BarsPeriod.Value + " minute";
				case BarsPeriodType.Tick: return BarsPeriod.Value + " tick";
				case BarsPeriodType.Second: return BarsPeriod.Value + " second";
				default: return BarsPeriod.ToString();
			}
		}

		private bool Fresh(string key)
		{
			DateTime t;
			if (spoke.TryGetValue(key, out t) && (DateTime.Now - t).TotalSeconds < CooldownSec) return false;
			spoke[key] = DateTime.Now; return true;
		}

		// high = clear anything queued or playing and use HighAlertVoice (SAPI XML; falls back to the default voice)
		private void Say(string text, bool high)
		{
			Print(string.Format("{0:HH:mm:ss} PMARPv3 {1}: {2}", DateTime.Now, high ? "HIGH" : "say", text));   // log of every alert
			if (chartControlRef == null) { Print("PMARPv3: no chart, can't speak"); return; }
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
				catch (Exception ex) { Print("PMARPv3 voice: " + ex.Message); }
			}));
		}

		private object Com(string name, BindingFlags kind, params object[] args)
		{
			return voice.GetType().InvokeMember(name, kind, null, voice, args);
		}

		#region Properties
		[Browsable(false)] [XmlIgnore] public Series<double> PMARP { get { return Values[0]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> Signal { get { return Values[1]; } }

		[NinjaScriptProperty]
		[Range(1, 500)]
		[Display(Name = "VWMA period", Order = 1, GroupName = "PMARP")]
		public int VwmaPeriod { get; set; }

		[NinjaScriptProperty]
		[Range(10, 2000)]
		[Display(Name = "Lookback", Description = "PMARP = % of this many previous PMAR values at or below now.", Order = 2, GroupName = "PMARP")]
		public int Lookback { get; set; }

		[NinjaScriptProperty]
		[Range(1, 200)]
		[Display(Name = "Signal period", Order = 3, GroupName = "PMARP")]
		public int SignalPeriod { get; set; }

		[NinjaScriptProperty]
		[Range(1, 20)]
		[Display(Name = "Slope bars", Description = "Slope = PMARP change over this many bars.", Order = 4, GroupName = "PMARP")]
		public int SlopeBars { get; set; }

		[NinjaScriptProperty]
		[Range(0, 50)]
		[Display(Name = "Slope threshold", Description = "Change above +this = rising, below -this = falling, else flat.", Order = 5, GroupName = "PMARP")]
		public double SlopeThreshold { get; set; }

		[NinjaScriptProperty]
		[Range(1, 50)]
		[Display(Name = "Turn points", Description = "PMARP this far off the run's low (high) = turning up (down), even before the slope agrees.", Order = 6, GroupName = "PMARP")]
		public double TurnPts { get; set; }

		[Display(Name = "Voice alerts", Order = 1, GroupName = "Voice")]
		public bool VoiceAlerts { get; set; }

		[Range(0, 120)]
		[Display(Name = "Continuation (min)", Description = "While the slope holds, say so every this many minutes (0 = off). Also once at 80 / 20.", Order = 2, GroupName = "Voice")]
		public int ContinuationMin { get; set; }

		[Range(0, 3600)]
		[Display(Name = "Cooldown (sec)", Description = "The same warning / alert is not repeated within this many seconds.", Order = 3, GroupName = "Voice")]
		public int CooldownSec { get; set; }

		[Range(-5, 5)]
		[Display(Name = "Speech rate", Order = 4, GroupName = "Voice")]
		public int SpeechRate { get; set; }

		[Display(Name = "High alert voice", Description = "e.g. Microsoft Zira Desktop or Microsoft David Desktop.", Order = 5, GroupName = "Voice")]
		public string HighAlertVoice { get; set; }
		#endregion
	}
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private PMARPv3[] cachePMARPv3;
		public PMARPv3 PMARPv3(int vwmaPeriod, int lookback, int signalPeriod, int slopeBars, double slopeThreshold, double turnPts)
		{
			return PMARPv3(Input, vwmaPeriod, lookback, signalPeriod, slopeBars, slopeThreshold, turnPts);
		}

		public PMARPv3 PMARPv3(ISeries<double> input, int vwmaPeriod, int lookback, int signalPeriod, int slopeBars, double slopeThreshold, double turnPts)
		{
			if (cachePMARPv3 != null)
				for (int idx = 0; idx < cachePMARPv3.Length; idx++)
					if (cachePMARPv3[idx] != null && cachePMARPv3[idx].VwmaPeriod == vwmaPeriod && cachePMARPv3[idx].Lookback == lookback && cachePMARPv3[idx].SignalPeriod == signalPeriod && cachePMARPv3[idx].SlopeBars == slopeBars && cachePMARPv3[idx].SlopeThreshold == slopeThreshold && cachePMARPv3[idx].TurnPts == turnPts && cachePMARPv3[idx].EqualsInput(input))
						return cachePMARPv3[idx];
			return CacheIndicator<PMARPv3>(new PMARPv3(){ VwmaPeriod = vwmaPeriod, Lookback = lookback, SignalPeriod = signalPeriod, SlopeBars = slopeBars, SlopeThreshold = slopeThreshold, TurnPts = turnPts }, input, ref cachePMARPv3);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.PMARPv3 PMARPv3(int vwmaPeriod, int lookback, int signalPeriod, int slopeBars, double slopeThreshold, double turnPts)
		{
			return indicator.PMARPv3(Input, vwmaPeriod, lookback, signalPeriod, slopeBars, slopeThreshold, turnPts);
		}

		public Indicators.PMARPv3 PMARPv3(ISeries<double> input , int vwmaPeriod, int lookback, int signalPeriod, int slopeBars, double slopeThreshold, double turnPts)
		{
			return indicator.PMARPv3(input, vwmaPeriod, lookback, signalPeriod, slopeBars, slopeThreshold, turnPts);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.PMARPv3 PMARPv3(int vwmaPeriod, int lookback, int signalPeriod, int slopeBars, double slopeThreshold, double turnPts)
		{
			return indicator.PMARPv3(Input, vwmaPeriod, lookback, signalPeriod, slopeBars, slopeThreshold, turnPts);
		}

		public Indicators.PMARPv3 PMARPv3(ISeries<double> input , int vwmaPeriod, int lookback, int signalPeriod, int slopeBars, double slopeThreshold, double turnPts)
		{
			return indicator.PMARPv3(input, vwmaPeriod, lookback, signalPeriod, slopeBars, slopeThreshold, turnPts);
		}
	}
}

#endregion
