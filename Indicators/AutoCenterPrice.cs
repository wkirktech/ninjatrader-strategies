#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Threading;
using System.Xml.Serialization;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
#endregion

// Every few seconds, moves the price scale so the last price is back in the middle, keeping the current zoom.
// Not a lock: between moves the scale can be dragged and zoomed as usual, and the next move keeps that zoom.
// It only moves once price is in the outer edge zone of the panel. Display only: no orders, no files.
namespace NinjaTrader.NinjaScript.Indicators
{
	public class AutoCenterPrice : Indicator
	{
		private DispatcherTimer timer;
		private ChartScale scale;   // the price scale, cached in OnRender (UI thread, like the timer)
		private double lastPrice = double.NaN;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description = "Re-centres the price scale on the last price every few seconds once price nears the edge, keeping the zoom. Does not lock the scale.";
				Name = "AutoCenterPrice";
				Calculate = Calculate.OnPriceChange;
				IsOverlay = true;
				DisplayInDataBox = false;
				PaintPriceMarkers = false;
				IsSuspendedWhileInactive = false;

				Enabled = true;
				IntervalSeconds = 30;
				EdgeZonePercent = 20;
				PauseWhileMouseOver = true;
				ShowLabel = false;
			}
			else if (State == State.Historical)
			{
				if (ChartControl != null)
					ChartControl.Dispatcher.InvokeAsync(StartTimer);
			}
			else if (State == State.Terminated)
			{
				if (ChartControl != null)
					ChartControl.Dispatcher.InvokeAsync(StopTimer);
				else
					StopTimer();
			}
		}

		protected override void OnBarUpdate()
		{
			lastPrice = Close[0];
		}

		protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
		{
			scale = chartScale;
		}

		private void StartTimer()
		{
			if (timer != null)
				return;
			timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Max(1, IntervalSeconds)) };
			timer.Tick += OnTimer;
			timer.Start();
		}

		private void StopTimer()
		{
			if (timer == null)
				return;
			timer.Stop();
			timer.Tick -= OnTimer;
			timer = null;
		}

		private void OnTimer(object sender, EventArgs e)
		{
			try
			{
				ChartControl cc = ChartControl;
				ChartScale s = scale;
				double price = lastPrice;
				if (!Enabled || cc == null || s == null || double.IsNaN(price) || !cc.IsVisible)
					return;
				// Never move the scale under the cursor, e.g. while placing or dragging an order
				if (PauseWhileMouseOver && cc.IsMouseOver)
					return;

				double max = s.MaxValue, min = s.MinValue, range = max - min;
				if (range <= 0)
					return;
				double pos = (price - min) / range;   // 0 = bottom of the panel, 1 = top
				double edge = Math.Max(0, Math.Min(49, EdgeZonePercent)) / 100.0;
				if (pos > edge && pos < 1 - edge)
					return;

				s.Properties.YAxisRangeType = YAxisRangeType.Fixed;
				s.Properties.FixedScaleMax = price + range / 2;
				s.Properties.FixedScaleMin = price - range / 2;
				cc.InvalidateVisual();
			}
			catch (Exception ex)
			{
				// A display helper must never take the chart down
				Print("AutoCenterPrice: " + ex.Message);
				StopTimer();
			}
		}

		// Hide the name in the chart's top-left label area unless asked; the Indicators dialog still shows it
		public override string DisplayName { get { return ShowLabel || State == State.SetDefaults ? Name : string.Empty; } }

		#region Properties
		[Display(Name = "Show label", Description = "Show the indicator's name in the chart's top-left label area.", Order = 99, GroupName = "Parameters")]
		public bool ShowLabel { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Enabled", Description = "Turn the re-centring on or off.", GroupName = "Parameters", Order = 0)]
		public bool Enabled { get; set; }

		[NinjaScriptProperty]
		[Range(1, 3600)]
		[Display(Name = "Interval (seconds)", Description = "How often price is checked.", GroupName = "Parameters", Order = 1)]
		public int IntervalSeconds { get; set; }

		[NinjaScriptProperty]
		[Range(0, 49)]
		[Display(Name = "Edge zone %", Description = "Only re-centre when price is this close to the top or bottom of the panel. 0 = re-centre every interval.", GroupName = "Parameters", Order = 2)]
		public int EdgeZonePercent { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Pause while mouse over chart", Description = "Skip a re-centre while the mouse is over the chart.", GroupName = "Parameters", Order = 3)]
		public bool PauseWhileMouseOver { get; set; }
		#endregion
	}
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private AutoCenterPrice[] cacheAutoCenterPrice;
		public AutoCenterPrice AutoCenterPrice(bool enabled, int intervalSeconds, int edgeZonePercent, bool pauseWhileMouseOver)
		{
			return AutoCenterPrice(Input, enabled, intervalSeconds, edgeZonePercent, pauseWhileMouseOver);
		}

		public AutoCenterPrice AutoCenterPrice(ISeries<double> input, bool enabled, int intervalSeconds, int edgeZonePercent, bool pauseWhileMouseOver)
		{
			if (cacheAutoCenterPrice != null)
				for (int idx = 0; idx < cacheAutoCenterPrice.Length; idx++)
					if (cacheAutoCenterPrice[idx] != null && cacheAutoCenterPrice[idx].Enabled == enabled && cacheAutoCenterPrice[idx].IntervalSeconds == intervalSeconds && cacheAutoCenterPrice[idx].EdgeZonePercent == edgeZonePercent && cacheAutoCenterPrice[idx].PauseWhileMouseOver == pauseWhileMouseOver && cacheAutoCenterPrice[idx].EqualsInput(input))
						return cacheAutoCenterPrice[idx];
			return CacheIndicator<AutoCenterPrice>(new AutoCenterPrice(){ Enabled = enabled, IntervalSeconds = intervalSeconds, EdgeZonePercent = edgeZonePercent, PauseWhileMouseOver = pauseWhileMouseOver }, input, ref cacheAutoCenterPrice);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.AutoCenterPrice AutoCenterPrice(bool enabled, int intervalSeconds, int edgeZonePercent, bool pauseWhileMouseOver)
		{
			return indicator.AutoCenterPrice(Input, enabled, intervalSeconds, edgeZonePercent, pauseWhileMouseOver);
		}

		public Indicators.AutoCenterPrice AutoCenterPrice(ISeries<double> input , bool enabled, int intervalSeconds, int edgeZonePercent, bool pauseWhileMouseOver)
		{
			return indicator.AutoCenterPrice(input, enabled, intervalSeconds, edgeZonePercent, pauseWhileMouseOver);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.AutoCenterPrice AutoCenterPrice(bool enabled, int intervalSeconds, int edgeZonePercent, bool pauseWhileMouseOver)
		{
			return indicator.AutoCenterPrice(Input, enabled, intervalSeconds, edgeZonePercent, pauseWhileMouseOver);
		}

		public Indicators.AutoCenterPrice AutoCenterPrice(ISeries<double> input , bool enabled, int intervalSeconds, int edgeZonePercent, bool pauseWhileMouseOver)
		{
			return indicator.AutoCenterPrice(input, enabled, intervalSeconds, edgeZonePercent, pauseWhileMouseOver);
		}
	}
}

#endregion
