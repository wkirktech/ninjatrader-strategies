#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.NinjaScript;
#endregion

// Writes every closed bar of the chart's series (OHLCV) to one CSV for offline research, e.g. daily-bias and
// session-level backtests. Intraday times are converted to US Eastern; daily bars keep their session date.
// Load the chart on a continuous, back-adjusted contract (NQ ##-##) or roll gaps show up as fake price gaps.
namespace NinjaTrader.NinjaScript.Indicators
{
	public class BarLogger : Indicator
	{
		private const string LogHeader = "date_et,time_et,open,high,low,close,volume";

		// Lines are queued on the indicator thread and written by a timer on a pool thread
		private readonly object logLock = new object();
		private readonly object writeLock = new object();   // one flush at a time, so rows stay in order
		private List<string> logQueue = new List<string>();
		private bool fileStarted;   // file recreated with the header in this run; under writeLock
		private Timer logTimer;
		private string logPath;
		private bool isDaily;
		private TimeZoneInfo eastern;
		private int rows;
		private DateTime firstTime, lastTime;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description = "Writes every closed bar (OHLCV, US Eastern time) to one CSV for offline backtests. Use a back-adjusted continuous contract.";
				Name = "BarLogger";
				Calculate = Calculate.OnBarClose;
				IsOverlay = true;
				DisplayInDataBox = false;
				IsSuspendedWhileInactive = false;
				LogFolder = Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "analysis", "history");
			}
			else if (State == State.DataLoaded)
			{
				isDaily = BarsPeriod.BarsPeriodType == BarsPeriodType.Day || BarsPeriod.BarsPeriodType == BarsPeriodType.Week
					|| BarsPeriod.BarsPeriodType == BarsPeriodType.Month || BarsPeriod.BarsPeriodType == BarsPeriodType.Year;
				eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");   // handles DST
				logPath = Path.Combine(LogFolder, Instrument.MasterInstrument.Name + "_"
					+ BarsPeriod.Value.ToString(CultureInfo.InvariantCulture) + BarsPeriod.BarsPeriodType.ToString() + ".csv");
				logTimer = new Timer(o => FlushLog(), null, 2000, 2000);
			}
			else if (State == State.Realtime)
			{
				if (rows > 0)
					Print("BarLogger " + Instrument.FullName + " " + BarsPeriod + ": " + rows + " bars, "
						+ firstTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " to " + lastTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " -> " + logPath);
			}
			else if (State == State.Terminated)
			{
				if (logTimer != null)
				{
					// Wait for a flush already running, then write the rest
					using (ManualResetEvent done = new ManualResetEvent(false))
					{
						logTimer.Dispose(done);
						done.WaitOne(2000);
					}
					logTimer = null;
					FlushLog();
				}
			}
		}

		protected override void OnBarUpdate()
		{
			// Bar times are bar close times in NinjaTrader's time zone
			DateTime t = isDaily ? Time[0].Date : TimeZoneInfo.ConvertTime(Time[0], NinjaTrader.Core.Globals.GeneralOptions.TimeZoneInfo, eastern);
			if (rows == 0)
				firstTime = t;
			lastTime = t;
			rows++;

			CultureInfo ci = CultureInfo.InvariantCulture;
			StringBuilder sb = new StringBuilder(80);
			sb.Append(t.ToString("yyyy-MM-dd", ci)).Append(',').Append(isDaily ? "" : t.ToString("HH:mm:ss", ci)).Append(',')
				.Append(Open[0].ToString(ci)).Append(',').Append(High[0].ToString(ci)).Append(',').Append(Low[0].ToString(ci)).Append(',').Append(Close[0].ToString(ci)).Append(',')
				.Append(Volume[0].ToString(ci));
			lock (logLock)
				logQueue.Add(sb.ToString());
		}

		// Pool thread (timer) and Terminated. The file is recreated with the header the first time this run writes it,
		// so reloading the chart replaces the file instead of duplicating rows.
		private void FlushLog()
		{
			lock (writeLock)
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
					StringBuilder sb = new StringBuilder(batch.Count * 60);
					foreach (string line in batch)
						sb.Append(line).Append("\r\n");
					if (!fileStarted)
					{
						File.WriteAllText(logPath, LogHeader + "\r\n" + sb);
						fileStarted = true;
					}
					else
						File.AppendAllText(logPath, sb.ToString());
				}
				catch (Exception ex)
				{
					Print("BarLogger: " + ex.Message);
				}
			}
		}

		#region Properties
		[Display(Name = "Log folder", Description = "One file per instrument and bar type, replaced on each load.", Order = 1, GroupName = "Logger")]
		public string LogFolder { get; set; }
		#endregion
	}
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private BarLogger[] cacheBarLogger;
		public BarLogger BarLogger()
		{
			return BarLogger(Input);
		}

		public BarLogger BarLogger(ISeries<double> input)
		{
			if (cacheBarLogger != null)
				for (int idx = 0; idx < cacheBarLogger.Length; idx++)
					if (cacheBarLogger[idx] != null &&  cacheBarLogger[idx].EqualsInput(input))
						return cacheBarLogger[idx];
			return CacheIndicator<BarLogger>(new BarLogger(), input, ref cacheBarLogger);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.BarLogger BarLogger()
		{
			return indicator.BarLogger(Input);
		}

		public Indicators.BarLogger BarLogger(ISeries<double> input )
		{
			return indicator.BarLogger(input);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.BarLogger BarLogger()
		{
			return indicator.BarLogger(Input);
		}

		public Indicators.BarLogger BarLogger(ISeries<double> input )
		{
			return indicator.BarLogger(input);
		}
	}
}

#endregion
