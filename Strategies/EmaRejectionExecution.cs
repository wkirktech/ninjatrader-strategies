#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public enum EmaRejDirection { Both, LongOnly, ShortOnly }
    public enum EmaRejSetupMode { Rejection, TrendPullback, Either }
    public enum EmaRejPullbackEntry { OnReclaim, LimitAtEma }
    public enum EmaRejDeltaMode { Off, LogOnly, Required }

    // EMA rejection / bounce execution.
    // Short: price tests EMA from below (wick can pierce, close can be a tick or two over), then closes back under.
    //        N confirmed rejections making lower highs = setup. A sell limit rides the EMA (minus offset) for the next test.
    // Long:  mirror (bounces off EMA from above making higher lows, buy limit rides EMA plus offset).
    // Stop = fixed points, target = beyond the most recent swing low (short) / swing high (long).
    // AUTO button arms it; it stays armed and takes every new setup until a full stop-loss hit or you click it off.
    // Trend pullback (Setup mode): price closes back over EMA and VWAP (the reclaim), then each pullback into the EMA that
    // holds a higher low (short: lower high) is an entry, from the first bounce.
    public class EmaRejectionExecution : Strategy
    {
        private const string SigLong  = "EMAR_L";
        private const string SigShort = "EMAR_S";
        // Split targets: one entry; once price reaches TP1 the strategy sends a market exit for half from OnBarUpdate.
        // (The old two-entry split with per-entry stops was in every NinjaTrader lock-up: 2026-10-26 21:56, 22:17, 2026-10-27 07:33.)
        private const string Tp1Name = "TP1";

        private class SideState
        {
            public int Dir;                                   // +1 long (bounce), -1 short (rejection)
            public List<double> Extremes = new List<double>(); // rejection highs (short) / bounce lows (long)
            public List<int> ExtremeBars = new List<int>();
            public bool TestActive;
            public int TestStartBar;
            public double TestExtreme;
            public int TestExtremeBar;
            public bool LeftZone = true;                      // price moved away from EMA since the last test
            public int ClosesBeyond;                          // consecutive closes on the wrong side of EMA
            public bool Forced;
            public int ForcedBar;
            public int ForcedAnchor = -1;
            public string Status = "";
            // Trend pullback: kept through Reset (a trade or a broken rejection count doesn't end the trend)
            public bool TrendOn;
            public bool TrendArmed = true;                    // a new trend needs a fresh reclaim: price on the other side first
            public int TrendAnchor = -1;                      // the reclaim bar: where the target leg starts
            public int TrendBar = -1;                         // the reclaim or the newest higher low
            public double TrendFloor;                         // newest higher low (short: lower high); a break ends the trend
            public int TrendEntries;
            public bool TrendWaitPb;                          // traded this pullback: the next entry needs the next one
            public int CloseThrough;                          // consecutive closes through the EMA
            public bool PbActive, PbTriggered;
            public bool PbLeft;                               // price was clear of the EMA zone since the reclaim / last pullback
            public int OnSideCount;                           // closes in a row over the EMA (and VWAP) before a trend starts
            public bool DeltaAgree, AgreeLast, ReadyLast;      // price and delta agree on this side (this bar / last bar), setup ready last bar
            public bool DeltaTrigger;                         // Required mode: agreement started on this bar with the setup ready
            public int DeltaDivAgo = -1;                      // bars since a delta divergence on this side (-1 = none in the lookback)
            public double PbExtreme;
            public int PbBar, TriggerBar = -1;                // TriggerBar: the bar that bounced back over the EMA

            public int LastBar
            {
                get
                {
                    if (Forced) return ForcedBar;
                    int last = ExtremeBars.Count > 0 ? ExtremeBars[ExtremeBars.Count - 1] : -1;
                    return TrendOn ? Math.Max(last, TrendBar) : last;
                }
            }

            public void Reset(bool leftZone)
            {
                Extremes.Clear();
                ExtremeBars.Clear();
                TestActive = false;
                ClosesBeyond = 0;
                Forced = false;
                LeftZone = leftZone;
            }
        }

        private EMA ema;
        private SideState longSide, shortSide;
        private int execIdx;
        private double execTick;
        private bool isChartHosted;
        private bool armed;
        private int lastProcessedBar = -1;
        private int tradesThisSession;

        // Day limits (reset with tradesThisSession) and the trade being journaled
        private double dayPnl;
        private int dayLosses;
        private string dayStopReason = "", dayBlock = "";
        private int tradeDir, tradeQty, exitQty;
        private double entryValue, exitValue, tradeCommission;
        private bool costEstimated;   // commission in the journal came from Cost per contract, not the fills
        private double tradeStopInit, tradeTpInit, tradeHigh, tradeLow;
        private DateTime tradeOpenTime, tradeCloseTime;
        private int tradeTrend;
        private bool tradeChop, tradeForced;
        private string exitReason = "";

        // Levels for the market-regime agent: prior Globex session, overnight, and the current session so far
        private double sessHigh, sessLow, sessClose, onHigh, onLow;
        private double priorHigh = double.NaN, priorLow = double.NaN, priorClose = double.NaN;
        // Trade context for the journal: prior RTH value area, opening range / initial balance, 15-min trend
        private readonly Dictionary<double, double> rthVolume = new Dictionary<double, double>();
        private DateTime rthDay = DateTime.MinValue;
        private bool rthValueDone = true;
        private double vah, val, poc, orHigh, orLow, ibHigh, ibLow;
        // 15-min trend built from the chart's own bars (an extra 15-min data series was involved in an NT lock-up)
        private long m15Bucket = -1;
        private double m15LastClose, m15Closed, m15Ema;
        private int m15Count;
        private string ctxSession = "", ctxTf15 = "", ctxValue = "", ctxLevel = "";
        private double ctxDist;
        private DateTime lastLevelsWrite = DateTime.MinValue;

        private double lastSwingHigh, lastSwingLow;
        private bool hasSwingHigh, hasSwingLow;
        private int lastSwingHighBar = -1, lastSwingLowBar = -1;

        private Order entryOrder;
        private int entryDir;
        private string entrySignal = "";
        private int entrySubmitBar;

        private bool tradeOpen;
        private bool profitLocked;
        private string activeSignal = "";

        // Globex VWAP (resets 18:00 ET), chop and trend
        private TimeZoneInfo easternTz;
        private DateTime vwapSession = DateTime.MinValue;
        private double vwapPV, vwapVol, lastVwap;
        private int vwapStartBar = -1;
        private const int VwapWarmupBars = 10;   // VWAP hugs price right after the 18:00 reset; ignore its crosses there
        private bool inChop, chopRisk;
        private int chopVwapX, trendDir;
        private double chopEfficiency = 1;
        private string pauseReason = "";

        private bool planValid;
        private int planDir;
        private double planEntry, planStop, planTarget;
        private bool planFallback;
        private bool planTooClose;
        private bool planViaTrend;   // the plan comes from the trend pullback (not a rejection count or a button)
        private Indicators.DeltaConfluence delta;   // null when Delta confirmation is Off
        private int deltaIdx = -1;                   // its 1-tick series
        private string entryDeltaCols = ",,,,,", tradeDeltaCols = ",,,,,";
        private string tradeSetup = "";
        private double planLeg = double.NaN;
        private int planAnchor = -1;

        // Open trade target tracking
        private int tradeAnchor = -1;
        private double tradeLeg = double.NaN;
        private double lastSetTarget;
        private bool targetManual;
        // Drag rules: stop may only be tightened (a looser stop snaps back); TP can be dragged anywhere
        private double lastSetStop, tradeEntry;
        private bool stopKnown, syncStop, stopFallback;
        private DateTime lastDragTime = DateTime.MinValue;
        private bool ownTargetPending;   // our own TP change in flight: echoes at the old price aren't manual drags
        private double preExtendTarget;
        // Some live connections (Tradovate) report a dragged exit at its old price; the price asked for is in the change request
        private bool changeReqSeen;
        private double looseStop;        // the looser stop being put back, so its own echo isn't a second drag
        private double pendingTighten;   // a tighter stop asked for, taken once the broker accepts it (0 = none)
        private Order pendingTightenOrder, looseOrder;
        // Split targets: half exits at market at TP1 (Tp1R from the fill), the runner keeps the leg target
        private bool splitActive, tp1Sent, tp1Done, stopHandled;
        private double tp1Price;
        private int tp1Qty;
        private Order tp1Order;   // the TP1 market exit, until it is done (FLAT must not exit those contracts twice)
        private string tp1Note = "";
        private bool cancelLeftover;
        private Order leftoverEntry;   // entry order still open when a trade went flat
        // Work found in the fill callbacks and done on the next update of the execution series (ProcessPending): order
        // methods there deadlocked NinjaTrader twice on 2026-10-26 ("Write lock may not be acquired with read lock held")
        private bool fillPending, bePending;
        private double fillPrice, beStop;
        private DateTime fillTime;
        private string pendingDisarm = "";
        private readonly List<ClosedTrade> pendingCloses = new List<ClosedTrade>();
        private bool entryCancelRequested;   // our own CancelEntry; any other cancel of an entry came from outside
        private string accountBlock = "";
        private bool entryIgnoredLogged;
        private DateTime realtimeSince = DateTime.MaxValue, lastFillWall = DateTime.MinValue;
        private SessionIterator sessionIt;
        private DateTime sessionEnd = DateTime.MinValue;   // chart time zone, for NinjaTrader's exit on session close

        private class ClosedTrade
        {
            public double Pnl;
            public string Message;
            public string Row;
        }
        private double planEma;
        private string planNote = "";
        private string lastEvent = "";

        // UI
        private ChartControl chartControlRef;
        private StackPanel uiPanel;
        private TextBlock infoText, statusText, titleText;
        private Button autoButton, dirButton, flatButton, buyButton, sellButton, collapseButton;
        private Border uiBorder;
        private StackPanel uiRow2;
        private bool uiCollapsed;   // UI thread only
        private string uiPosText = "Flat";   // UI thread only

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description                                 = "EMA rejection / bounce execution with on-chart AUTO switch. Rides a limit order on the EMA after confirmed rejections (lower highs) or bounces (higher lows).";
                Name                                        = "EmaRejectionExecution";
                Calculate                                   = Calculate.OnPriceChange;
                EntriesPerDirection                         = 1;
                EntryHandling                               = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy                = true;
                ExitOnSessionCloseSeconds                   = 30;
                IsFillLimitOnTouch                          = false;
                MaximumBarsLookBack                         = MaximumBarsLookBack.TwoHundredFiftySix;
                OrderFillResolution                         = OrderFillResolution.Standard;
                Slippage                                    = 0;
                StartBehavior                               = StartBehavior.WaitUntilFlat;
                TimeInForce                                 = TimeInForce.Gtc;
                TraceOrders                                 = false;
                RealtimeErrorHandling                       = RealtimeErrorHandling.StopCancelClose;
                // One stop + one target for the whole position (partial fills don't create extra stops the drag rules can't see)
                StopTargetHandling                          = StopTargetHandling.ByStrategyPosition;
                BarsRequiredToTrade                         = 20;
                IsInstantiatedOnEachOptimizationIteration   = true;

                EmaPeriod               = 55;
                TouchZonePts            = 3;
                MaxPiercePts            = 8;
                CloseOverTicks          = 2;
                RejectCloseTicks        = 2;
                RejectBars              = 3;
                RequiredRejections      = 2;
                RequireStructure        = true;
                MinStructureGapPts      = 1;
                SetupLookbackBars       = 80;
                MaxClosesBeyond         = 2;
                UseSlopeFilter          = false;
                SlopeBars               = 10;
                SwingStrength           = 3;
                SetupMode               = EmaRejSetupMode.Rejection;
                TrendNeedsVwap          = true;
                TrendBreakPts           = 5;
                TrendBreakBars          = 3;
                TrendMaxEntries         = 2;
                PullbackEntry           = EmaRejPullbackEntry.OnReclaim;
                TrendHoldBars           = 2;
                DeltaMode               = EmaRejDeltaMode.LogOnly;
                DeltaDivBars            = 10;
                PullbackOffsetPts       = 2;
                ReclaimMaxChasePts      = 8;

                LongOffsetPts           = 5;
                ShortOffsetPts          = 5;
                TrackEma                = true;
                OrderExpiryBars         = 0;
                MinDistFromPricePts     = 3;
                Direction               = EmaRejDirection.Both;

                StopLossPts             = 25;
                TargetBeyondSwingPts    = 2;
                MinTargetR              = 1;
                UseProfitLock           = false;
                ProfitLockTriggerPts    = 30;
                ProfitLockPts           = 12;

                StartArmed              = false;
                DisarmOnStopLoss        = true;
                DisarmOnProfitLockStop  = false;
                Contracts               = 1;
                MaxTradesPerSession     = 0;
                ExecutionInstrument     = "";
                ExecuteOnMicro          = true;

                UseTimeFilter           = false;
                StartTime               = 93000;
                EndTime                 = 155500;

                ShowEma                 = true;
                ShowMarkers             = true;
                ShowPlanLines           = true;
                ShowDashboard           = true;
                DashboardCollapsed      = false;

                UseChopFilter           = true;
                ChopLookbackBars        = 84;
                ChopMinCrosses          = 5;
                ChopMaxEfficiency       = 0.25;
                ChopRecentBars          = 20;
                TradeWithTrend          = true;

                DailyStopUsd            = 0;
                MaxLossesPerDay         = 3;
                CostPerContractUsd      = 2.5;

                SplitTargets            = false;   // off until the market TP1 has passed Sim
                TraceOrdersToOutput     = false;
                Tp1R                    = 1;
                RunnerToBreakeven       = true;
                Tp1MinGapPts            = 5;

                AddPlot(Brushes.Orange, "VWAP");
            }
            else if (State == State.Configure)
            {
                execIdx = 0;
                string exec = ExecutionInstrument == null ? "" : ExecutionInstrument.Trim();
                if (exec.Length == 0 && ExecuteOnMicro)
                    exec = MicroOf(Instrument);
                // A bare root ("NQ", "MNQ") takes the chart's contract month, so "NQ" on an NQ chart trades the chart itself
                if (exec.Length > 0 && exec.IndexOf(' ') < 0 && Instrument != null
                    && Instrument.FullName.StartsWith(Instrument.MasterInstrument.Name))
                    exec = exec.ToUpper() + Instrument.FullName.Substring(Instrument.MasterInstrument.Name.Length);
                if (exec.Length > 0 && (Instrument == null || !string.Equals(exec, Instrument.FullName, StringComparison.OrdinalIgnoreCase)))
                {
                    AddDataSeries(exec);
                    execIdx = 1;
                }
                // DeltaConfluence reads every trade from a 1-tick series of the chart's instrument; a hosted indicator's
                // series must be added by the strategy too
                deltaIdx = -1;
                if (DeltaMode != EmaRejDeltaMode.Off)
                {
                    AddDataSeries(BarsPeriodType.Tick, 1);
                    deltaIdx = execIdx + 1;
                }
                TraceOrders = TraceOrdersToOutput;
            }
            else if (State == State.DataLoaded)
            {
                ema = EMA(Closes[0], EmaPeriod);
                delta = deltaIdx > 0 ? DeltaConfluence(DeltaResetMode.Session, 21, 55, 3, 20, EmaPeriod, 0.15, 5, 2) : null;
                if (ShowEma)
                    AddChartIndicator(ema);

                longSide  = new SideState { Dir = 1 };
                shortSide = new SideState { Dir = -1 };
                execTick  = Instruments[execIdx].MasterInstrument.TickSize;
                easternTz = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

                sessionIt     = new SessionIterator(Bars);
                isChartHosted = ChartControl != null;
                armed = isChartHosted ? StartArmed : true;   // Strategy Analyzer / no chart: always armed
            }
            else if (State == State.Historical)
            {
                if (ChartControl != null && ShowDashboard)
                {
                    chartControlRef = ChartControl;
                    chartControlRef.Dispatcher.InvokeAsync(new Action(BuildUi));
                }
            }
            else if (State == State.Realtime)
            {
                realtimeSince = DateTime.Now;
            }
            else if (State == State.Terminated)
            {
                // Trades booked in a fill callback but not yet applied still reach the journal
                foreach (ClosedTrade ct in pendingCloses)
                    if (ct.Row != null)
                        QueueFile(new FileWrite { Path = AnalysisPath("trades.csv"), Text = ct.Row + Environment.NewLine, Append = true, Header = JournalHeader });
                pendingCloses.Clear();
                if (chartControlRef != null)
                    chartControlRef.Dispatcher.InvokeAsync(new Action(RemoveUi));
            }
        }

        // NQ 12-26 -> MNQ 12-26 (same expiry). Blank when the chart is already a micro or has no known micro.
        private static readonly Dictionary<string, string> MicroRoots = new Dictionary<string, string>
        {
            { "NQ", "MNQ" }, { "ES", "MES" }, { "YM", "MYM" }, { "RTY", "M2K" },
            { "GC", "MGC" }, { "CL", "MCL" }, { "SI", "SIL" }
        };

        private static string MicroOf(Instrument instrument)
        {
            if (instrument == null)
                return "";
            string root = instrument.MasterInstrument.Name;
            string micro;
            if (!MicroRoots.TryGetValue(root, out micro))
                return "";
            string full = instrument.FullName;
            return full.StartsWith(root) ? micro + full.Substring(root.Length) : "";
        }

        protected override void OnBarUpdate()
        {
            if (BarsInProgress == deltaIdx)
                return;   // every trade: only the indicator uses it
            if (CurrentBars[0] < Math.Max(EmaPeriod, SlopeBars + 2 * SwingStrength + 2) + 2)
                return;
            if (execIdx > 0 && CurrentBars[execIdx] < 1)
                return;

            // Before anything that can send an entry or reset the day (on either series): AUTO OFF and the day limits from
            // a fill must take effect first
            ProcessPending();

            if (BarsInProgress == execIdx)
            {
                EnforceExitDrags();
                ManageProfitLock();
                ManageTp1();
                if (tradeOpen)
                {
                    tradeHigh = Math.Max(tradeHigh, Closes[execIdx][0]);
                    tradeLow  = Math.Min(tradeLow, Closes[execIdx][0]);
                }
            }

            if (BarsInProgress != 0 || !IsFirstTickOfBar)
                return;

            // End of the chart's current trading session (its own template), read here so callbacks never touch the bars
            if (sessionIt != null && (sessionEnd == DateTime.MinValue || Time[0] >= sessionEnd))
            {
                sessionIt.GetNextSession(Time[0], true);
                sessionEnd = sessionIt.ActualSessionEnd;
            }

            // Historical (no tick replay) calls once per closed bar -> bar 0 is closed.
            // Realtime first tick of a new bar -> bar 1 is the one that just closed.
            int c = (State == State.Historical && !Bars.IsTickReplay) ? 0 : 1;
            int cb = CurrentBar - c;
            if (cb <= lastProcessedBar)
                return;
            lastProcessedBar = cb;

            if (Bars.IsFirstBarOfSession)
            {
                tradesThisSession = 0;
                dayPnl        = 0;
                dayLosses     = 0;
                dayStopReason = "";
            }

            UpdateSwings(c);
            UpdateVwapAndChop(c);
            UpdateSide(shortSide, c);
            UpdateSide(longSide, c);
            UpdateDeltaState(c);
            UpdateTradeTarget(c);
            ManageEntry(c);
            DrawPlan();
            RefreshUi();
        }

        #region VWAP / chop / trend
        private void UpdateVwapAndChop(int c)
        {
            // Globex trading day rolls at 18:00 ET: shift ET time by 6h and take the date
            DateTime et = TimeZoneInfo.ConvertTime(Time[c], NinjaTrader.Core.Globals.GeneralOptions.TimeZoneInfo, easternTz);
            DateTime session = et.AddHours(6).Date;
            if (session != vwapSession)
            {
                if (vwapSession != DateTime.MinValue)
                {
                    priorHigh  = sessHigh;
                    priorLow   = sessLow;
                    priorClose = sessClose;
                    Log0("Session start: prior H " + Fmt(priorHigh) + " L " + Fmt(priorLow) + " C " + Fmt(priorClose));
                }
                sessHigh = onHigh = double.MinValue;
                sessLow  = onLow  = double.MaxValue;
                vwapSession  = session;
                vwapStartBar = CurrentBar - c;
                // A trend doesn't carry into a new session: VWAP starts over, so a fresh reclaim is needed
                foreach (SideState s in new[] { longSide, shortSide })
                    if (s.TrendOn)
                        EndTrend(s, "new session");
                vwapPV  = 0;
                vwapVol = 0;
            }
            sessHigh  = Math.Max(sessHigh, High[c]);
            sessLow   = Math.Min(sessLow, Low[c]);
            sessClose = Close[c];
            if (et.TimeOfDay < new TimeSpan(9, 30, 0) || et.TimeOfDay >= new TimeSpan(18, 0, 0))
            {
                onHigh = Math.Max(onHigh, High[c]);
                onLow  = Math.Min(onLow, Low[c]);
            }
            UpdateRthProfile(et, c);
            Update15MinTrend(et, Close[c]);

            double vol = Volume[c];
            if (vol > 0)
            {
                vwapPV  += (High[c] + Low[c] + Close[c]) / 3.0 * vol;
                vwapVol += vol;
            }
            if (vwapVol <= 0)
                return;
            lastVwap = vwapPV / vwapVol;
            Values[0][c] = lastVwap;

            double e = ema[c];
            trendDir = Close[c] > e && Close[c] > lastVwap ? 1 : (Close[c] < e && Close[c] < lastVwap ? -1 : 0);

            // Crosses: closes flipping sides of the Globex VWAP. EMA crosses don't count: pullbacks to the EMA are the setup itself.
            // Efficiency: net move / bar-by-bar distance travelled. Trend ~0.5+, chop goes nowhere (~0.2 or less).
            chopVwapX = 0;
            int lastCrossAgo = int.MaxValue;   // bars since the newest VWAP cross
            double path = 0;
            int look = Math.Min(ChopLookbackBars, CurrentBar - c - 1);
            for (int i = c; i < c + look; i++)
            {
                // VWAP crosses only within this session, after the warm-up (no cross across the reset)
                if (CurrentBar - (i + 1) >= vwapStartBar + VwapWarmupBars && Values[0].IsValidDataPoint(i + 1) && (Close[i] > Values[0][i]) != (Close[i + 1] > Values[0][i + 1]))
                {
                    chopVwapX++;
                    lastCrossAgo = Math.Min(lastCrossAgo, i - c);
                }
                path += Math.Abs(Close[i] - Close[i + 1]);
            }
            chopEfficiency = path > 0 ? Math.Abs(Close[c] - Close[c + look]) / path : 1;

            // Crosses alone = early "risk" heads-up; crosses + low efficiency = CHOP (pauses entries)
            // ...and price is still rotating around VWAP: once it has held one side for ChopRecentBars, old crosses don't count
            bool crossesHit = chopVwapX >= ChopMinCrosses && (ChopRecentBars <= 0 || lastCrossAgo < ChopRecentBars);
            bool wasChop = inChop, wasRisk = chopRisk;
            inChop   = crossesHit && chopEfficiency <= ChopMaxEfficiency;
            chopRisk = crossesHit && !inChop;
            if (inChop != wasChop || chopRisk != wasRisk)
                Log0(inChop ? "CHOP detected" + (UseChopFilter ? ", entries paused" : "") : chopRisk ? "Chop risk" : "Chop cleared");
            WriteLevels(et, ema[c]);
        }

        // RTH (09:30-16:00 ET): volume by 1-pt price bin for the value area, opening range (first 15 min), initial balance (first hour).
        // The value area is computed once RTH has closed, so during a session it is always the prior RTH day's.
        private void UpdateRthProfile(DateTime et, int c)
        {
            // Bars are stamped at their close: on minute bars use the bar's start, so the pre-open bar stays out
            if (BarsPeriod.BarsPeriodType == BarsPeriodType.Minute)
                et = et.AddMinutes(-BarsPeriod.Value);
            else if (BarsPeriod.BarsPeriodType == BarsPeriodType.Second)
                et = et.AddSeconds(-BarsPeriod.Value);
            TimeSpan tod = et.TimeOfDay;
            if (tod >= new TimeSpan(9, 30, 0) && tod < new TimeSpan(16, 0, 0))
            {
                if (et.Date != rthDay)
                {
                    rthDay = et.Date;
                    rthVolume.Clear();
                    rthValueDone = false;
                    orHigh = ibHigh = double.MinValue;
                    orLow  = ibLow  = double.MaxValue;
                }
                double bin = Math.Round(Close[c], MidpointRounding.AwayFromZero);   // default rounding favours even bins
                double v;
                rthVolume.TryGetValue(bin, out v);
                rthVolume[bin] = v + Volume[c];
                if (tod < new TimeSpan(9, 45, 0))
                {
                    orHigh = Math.Max(orHigh, High[c]);
                    orLow  = Math.Min(orLow, Low[c]);
                }
                if (tod < new TimeSpan(10, 30, 0))
                {
                    ibHigh = Math.Max(ibHigh, High[c]);
                    ibLow  = Math.Min(ibLow, Low[c]);
                }
            }
            else if (!rthValueDone && rthVolume.Count > 0)
            {
                rthValueDone = true;
                ComputeValueArea();
            }
        }

        // 15-min closes and their EMA 12, from the chart's own closed bars. A bar stamped exactly on a 15-min mark closes the
        // block it ends, so the time is nudged back a tick before bucketing.
        private void Update15MinTrend(DateTime et, double close)
        {
            long span   = TimeSpan.FromMinutes(15).Ticks;
            long bucket = et.AddTicks(-1).Ticks / span;
            if (bucket != m15Bucket)
            {
                if (m15Bucket >= 0)
                    Close15Min();
                m15Bucket = bucket;
            }
            m15LastClose = close;
            // Minute / second bars ending exactly on a 15-min mark close the block now, not when the next block starts
            bool timeBars = BarsPeriod.BarsPeriodType == BarsPeriodType.Minute || BarsPeriod.BarsPeriodType == BarsPeriodType.Second;
            if (timeBars && et.Ticks % span == 0)
            {
                Close15Min();
                m15Bucket = -2;   // closed: the next bar starts a new block without closing this one again
            }
        }

        private void Close15Min()
        {
            m15Closed = m15LastClose;
            m15Ema    = m15Count == 0 ? m15Closed : m15Ema + 2.0 / 13 * (m15Closed - m15Ema);
            m15Count++;
        }

        // POC = busiest bin; the value area grows from it, taking the busier neighbour each step, until it holds 70% of the volume
        private void ComputeValueArea()
        {
            List<double> bins = new List<double>(rthVolume.Keys);
            bins.Sort();
            double total = 0;
            int p = 0;
            for (int i = 0; i < bins.Count; i++)
            {
                total += rthVolume[bins[i]];
                if (rthVolume[bins[i]] > rthVolume[bins[p]])
                    p = i;
            }
            int lo = p, hi = p;
            double inside = rthVolume[bins[p]];
            while (inside < total * 0.7 && (lo > 0 || hi < bins.Count - 1))
            {
                double below = lo > 0 ? rthVolume[bins[lo - 1]] : -1;
                double above = hi < bins.Count - 1 ? rthVolume[bins[hi + 1]] : -1;
                if (above >= below)
                    inside += rthVolume[bins[++hi]];
                else
                    inside += rthVolume[bins[--lo]];
            }
            poc = bins[p];
            vah = bins[hi];
            val = bins[lo];
        }

        // Where the trade started, from data known at the fill (no look-ahead)
        private void SnapshotContext(double price, DateTime time)
        {
            DateTime et = ToEt(time);
            TimeSpan tod = et.TimeOfDay;
            ctxSession = tod >= new TimeSpan(18, 0, 0) || tod < new TimeSpan(3, 0, 0) ? "asia"
                : tod < new TimeSpan(9, 30, 0) ? "london"
                : tod < new TimeSpan(10, 30, 0) ? "rth_open"
                : tod < new TimeSpan(14, 0, 0) ? "midday"
                : tod < new TimeSpan(16, 0, 0) ? "close" : "post";

            // Last closed 15-min bar vs its EMA 12
            ctxTf15 = m15Count >= 12 ? (m15Closed > m15Ema ? "up" : m15Closed < m15Ema ? "down" : "flat") : "";

            ctxValue = vah > 0 ? (price > vah ? "above" : price < val ? "below" : "inside") : "";

            // Nearest key level; opening range / IB only once complete today
            bool today = et.Date == rthDay;
            List<KeyValuePair<string, double>> levels = new List<KeyValuePair<string, double>>
            {
                new KeyValuePair<string, double>("prior_high", priorHigh), new KeyValuePair<string, double>("prior_low", priorLow),
                new KeyValuePair<string, double>("prior_close", priorClose), new KeyValuePair<string, double>("on_high", onHigh),
                new KeyValuePair<string, double>("on_low", onLow), new KeyValuePair<string, double>("vwap", lastVwap),
                new KeyValuePair<string, double>("vah", vah), new KeyValuePair<string, double>("val", val),
                new KeyValuePair<string, double>("poc", poc)
            };
            if (today && tod >= new TimeSpan(9, 45, 0))
            {
                levels.Add(new KeyValuePair<string, double>("or_high", orHigh));
                levels.Add(new KeyValuePair<string, double>("or_low", orLow));
            }
            if (today && tod >= new TimeSpan(10, 30, 0))
            {
                levels.Add(new KeyValuePair<string, double>("ib_high", ibHigh));
                levels.Add(new KeyValuePair<string, double>("ib_low", ibLow));
            }
            ctxLevel = "";
            ctxDist  = 0;
            foreach (KeyValuePair<string, double> l in levels)
            {
                if (double.IsNaN(l.Value) || l.Value <= 0 || l.Value == double.MaxValue)
                    continue;
                if (ctxLevel.Length == 0 || Math.Abs(price - l.Value) < Math.Abs(ctxDist))
                {
                    ctxLevel = l.Key;
                    ctxDist  = price - l.Value;
                }
            }
        }

        // Current levels for the market-regime agent: Documents\NinjaTrader 8\analysis\levels.txt, rewritten about once a minute
        private void WriteLevels(DateTime et, double e)
        {
            if (State != State.Realtime || (DateTime.Now - lastLevelsWrite).TotalSeconds < 60)
                return;
            lastLevelsWrite = DateTime.Now;
            string text = "updated " + et.ToString("yyyy-MM-dd HH:mm") + " ET  " + Instrument.FullName + Environment.NewLine
                + "prior session   H " + Lvl(priorHigh) + "  L " + Lvl(priorLow) + "  C " + Lvl(priorClose) + Environment.NewLine
                + "overnight       H " + Lvl(onHigh) + "  L " + Lvl(onLow) + Environment.NewLine
                + "session so far  H " + Lvl(sessHigh) + "  L " + Lvl(sessLow) + "  last " + Lvl(sessClose) + Environment.NewLine
                + "prior RTH value VAH " + Lvl(vah) + "  POC " + Lvl(poc) + "  VAL " + Lvl(val) + Environment.NewLine
                + "opening range   H " + Lvl(orHigh) + "  L " + Lvl(orLow) + "   initial balance H " + Lvl(ibHigh) + "  L " + Lvl(ibLow) + Environment.NewLine
                + "VWAP " + Lvl(lastVwap) + "  EMA" + EmaPeriod + " " + Lvl(e) + Environment.NewLine
                + "trend " + (trendDir > 0 ? "BULLISH" : trendDir < 0 ? "BEARISH" : "MIXED")
                + "  chop " + (inChop ? "CHOP" : chopRisk ? "risk" : "none") + "  efficiency " + chopEfficiency.ToString("0.00") + Environment.NewLine;
            WriteAnalysisFile("levels.txt", text, false);
        }

        private string Lvl(double x)
        {
            return double.IsNaN(x) || x <= 0 || x == double.MaxValue || x == double.MinValue ? "-" : Fmt(x);
        }

        private string StatusText()
        {
            if (lastVwap <= 0)
                return "Trend --";
            string trend = trendDir > 0 ? "BULLISH" : trendDir < 0 ? "BEARISH" : "MIXED";
            string t = "Trend " + trend + "  VWAP " + Fmt(lastVwap) + "\n";
            t += inChop ? (UseChopFilter ? "⚠ CHOP – NO ENTRIES" : "CHOP (filter off)") : chopRisk ? "Chop risk" : "Chop: none";
            return t;
        }
        #endregion

        #region Setup detection
        private void UpdateSwings(int c)
        {
            int p = c + SwingStrength;
            if (CurrentBar < p + SwingStrength)
                return;

            bool isHigh = true, isLow = true;
            for (int k = 1; k <= SwingStrength; k++)
            {
                if (!(High[p] > High[p - k] && High[p] >= High[p + k])) isHigh = false;
                if (!(Low[p] < Low[p - k] && Low[p] <= Low[p + k])) isLow = false;
            }
            if (isHigh) { lastSwingHigh = High[p]; hasSwingHigh = true; lastSwingHighBar = CurrentBar - p; }
            if (isLow)  { lastSwingLow  = Low[p];  hasSwingLow  = true; lastSwingLowBar  = CurrentBar - p; }
        }

        // Trend pullback. On: a close back over the EMA (and VWAP) after being on the other side; the floor is the low of
        // the move that reclaimed. Each pullback into the EMA touch zone that bounces (closes RejectClose over the EMA) is a
        // trigger; when it leaves the zone its low is the new higher low. Off: a close more than TrendBreakPts through the
        // EMA (or VWAP), TrendBreakBars closes in a row through the EMA, a lower low, or a stop-out in this leg.
        private void UpdateTrend(SideState s, int c)
        {
            if (SetupMode == EmaRejSetupMode.Rejection || lastVwap <= 0)
                return;
            double eps  = TickSize * 0.01;
            double e    = ema[c];
            int cb      = CurrentBar - c;
            double ext  = s.Dir > 0 ? Low[c] : High[c];
            string name = s.Dir > 0 ? "Long" : "Short";

            if (!s.TrendOn)
            {
                bool onSide = s.Dir * (Close[c] - e) > eps && (!TrendNeedsVwap || s.Dir * (Close[c] - lastVwap) > eps);
                if (!onSide)
                {
                    s.TrendArmed  = true;
                    s.OnSideCount = 0;
                    return;
                }
                if (!s.TrendArmed)
                    return;
                // A real reclaim holds: TrendHoldBars closes in a row on this side, outside chop
                s.OnSideCount++;
                if (s.OnSideCount < TrendHoldBars || (UseChopFilter && inChop))
                    return;
                // The reclaim. Floor: the low (short: high) back to the last bar that closed fully on this side (max 30 bars)
                double floor = ext;
                int look = Math.Min(30, CurrentBar - c - 1);
                for (int i = c + 1; i <= c + look; i++)
                {
                    floor = s.Dir > 0 ? Math.Min(floor, Low[i]) : Math.Max(floor, High[i]);
                    bool vwapSide = !TrendNeedsVwap || (Values[0].IsValidDataPoint(i) && s.Dir * (Close[i] - Values[0][i]) > eps);
                    if (i >= c + TrendHoldBars && s.Dir * (Close[i] - ema[i]) > eps && vwapSide)
                        break;
                }
                s.TrendOn      = true;
                s.TrendArmed   = false;
                s.TrendAnchor  = s.TrendBar = cb;
                s.TrendFloor   = floor;
                s.TrendEntries = 0;
                s.TrendWaitPb  = s.PbActive = s.PbTriggered = s.PbLeft = false;
                s.CloseThrough = 0;
                s.TriggerBar   = -1;
                Log0(name + " trend on: reclaimed EMA" + (TrendNeedsVwap ? " + VWAP" : "") + ", " + (s.Dir > 0 ? "low " : "high ") + Fmt(floor));
                if (ShowMarkers)
                    Draw.Text(this, "EMAR_TO" + cb, "T", c, s.Dir > 0 ? Low[c] - 8 * TickSize : High[c] + 8 * TickSize, Brushes.Gold);
                return;
            }

            double through = -s.Dir * (Close[c] - e);   // > 0: closed on the wrong side of the EMA
            s.CloseThrough = through > eps ? s.CloseThrough + 1 : 0;
            string off = "";
            if (TrendNeedsVwap && -s.Dir * (Close[c] - lastVwap) > TrendBreakPts + eps)
                off = "closed more than " + TrendBreakPts.ToString("0.##") + " pts through VWAP";
            else if (through > TrendBreakPts + eps)
                off = "closed " + through.ToString("0.##") + " pts through EMA";
            else if (TrendBreakBars > 0 && s.CloseThrough >= TrendBreakBars)
                off = s.CloseThrough + " closes through EMA";
            else if (s.Dir * (ext - s.TrendFloor) < -eps)
                off = s.Dir > 0 ? "lower low" : "higher high";
            if (off.Length > 0)
            {
                EndTrend(s, off);
                return;
            }

            // Pullbacks into the EMA zone. One starts only after price was clear of the zone (not the bars right after the reclaim).
            bool inZone = -s.Dir * (ext - e) >= -TouchZonePts - eps;
            if (!inZone)
                s.PbLeft = true;
            if (inZone && (s.PbActive || s.PbLeft))
            {
                if (!s.PbActive)
                {
                    s.PbActive    = true;
                    s.PbTriggered = false;
                    s.PbExtreme   = ext;
                    s.PbBar       = cb;
                }
                else if (s.Dir * (ext - s.PbExtreme) < 0)
                {
                    s.PbExtreme = ext;
                    s.PbBar     = cb;
                }
                // The bounce: a close back over the EMA (once per pullback)
                if (!s.PbTriggered && s.Dir * (Close[c] - e) >= RejectCloseTicks * TickSize - eps)
                {
                    s.PbTriggered = true;
                    s.TriggerBar  = cb;
                    Log0(name + " pullback bounced off EMA, " + (s.Dir > 0 ? "low " : "high ") + Fmt(s.PbExtreme));
                }
            }
            else if (s.PbActive)
            {
                // Left the zone: that pullback's extreme is the new higher low (short: lower high)
                s.PbActive    = false;
                s.PbLeft      = true;
                s.TrendFloor  = s.PbExtreme;
                s.TrendBar    = s.PbBar;
                s.TrendWaitPb = false;
                if (ShowMarkers)
                {
                    int ago = CurrentBar - s.PbBar;
                    if (s.Dir > 0)
                        Draw.TriangleUp(this, "EMAR_TH" + s.PbBar, false, ago, s.PbExtreme - 2 * TickSize, Brushes.Gold);
                    else
                        Draw.TriangleDown(this, "EMAR_TH" + s.PbBar, false, ago, s.PbExtreme + 2 * TickSize, Brushes.Gold);
                }
            }
        }

        private void EndTrend(SideState s, string why)
        {
            s.TrendOn = s.TrendArmed = s.PbActive = false;
            s.TriggerBar = -1;
            Log0((s.Dir > 0 ? "Long" : "Short") + " trend off: " + why);
        }

        // An entry can come from the rejection count or from the trend pullback (Setup mode)
        private bool RejectionReady(SideState s)
        {
            return SetupMode != EmaRejSetupMode.TrendPullback && s.Extremes.Count >= RequiredRejections;
        }

        private bool TrendReady(SideState s)
        {
            return SetupMode != EmaRejSetupMode.Rejection && s.TrendOn && !s.TrendWaitPb
                && (TrendMaxEntries <= 0 || s.TrendEntries < TrendMaxEntries);
        }

        private bool ViaTrend(SideState s)
        {
            return !s.Forced && !RejectionReady(s) && TrendReady(s);
        }

        private void UpdateSide(SideState s, int c)
        {
            UpdateTrend(s, c);
            double eps = TickSize * 0.01;
            double e = ema[c];
            int cb = CurrentBar - c;
            // pen > 0: wick went through the EMA. closeBeyond > 0: closed on the wrong side.
            double pen         = s.Dir < 0 ? High[c] - e : e - Low[c];
            double ext         = s.Dir < 0 ? High[c] : Low[c];
            double closeBeyond = s.Dir < 0 ? Close[c] - e : e - Close[c];

            if (closeBeyond > CloseOverTicks * TickSize + eps || pen > MaxPiercePts + eps)
            {
                if (s.Extremes.Count > 0 || s.Forced || s.TestActive)
                    Invalidate(s, "broke EMA");
                else
                    s.Reset(false);
                return;
            }
            s.ClosesBeyond = closeBeyond > eps ? s.ClosesBeyond + 1 : 0;
            if (s.ClosesBeyond >= MaxClosesBeyond)
            {
                Invalidate(s, MaxClosesBeyond + " closes over EMA");
                return;
            }

            while (s.ExtremeBars.Count > 0 && cb - s.ExtremeBars[0] > SetupLookbackBars)
            {
                s.ExtremeBars.RemoveAt(0);
                s.Extremes.RemoveAt(0);
            }
            if (s.Forced && cb - s.ForcedBar > SetupLookbackBars)
                s.Forced = false;

            if (pen < -TouchZonePts)
                s.LeftZone = true;

            bool touch = pen >= -TouchZonePts;
            if (touch && (s.LeftZone || s.TestActive))
            {
                if (!s.TestActive)
                {
                    s.TestActive     = true;
                    s.TestStartBar   = cb;
                    s.TestExtreme    = ext;
                    s.TestExtremeBar = cb;
                }
                else if (s.Dir < 0 ? ext > s.TestExtreme : ext < s.TestExtreme)
                {
                    s.TestExtreme    = ext;
                    s.TestExtremeBar = cb;
                }
            }

            if (s.TestActive)
            {
                double rejectDist = s.Dir < 0 ? e - Close[c] : Close[c] - e;
                if (rejectDist >= RejectCloseTicks * TickSize - eps)
                    ConfirmRejection(s, cb);
                else if (cb - s.TestStartBar >= RejectBars)
                {
                    s.TestActive = false;
                    s.LeftZone   = false;
                }
            }

            s.Status = s.Forced ? "FORCED" : (RejectionReady(s) ? "CONFIRMED" : (s.TestActive ? "testing" : ""));
        }

        private void ConfirmRejection(SideState s, int cb)
        {
            double x = s.TestExtreme;
            int xb   = s.TestExtremeBar;
            s.TestActive = false;
            s.LeftZone   = false;

            string word = s.Dir < 0 ? "lower high" : "higher low";
            if (s.Extremes.Count > 0 && RequireStructure)
            {
                double prev    = s.Extremes[s.Extremes.Count - 1];
                double improve = s.Dir < 0 ? prev - x : x - prev;
                if (improve < Math.Max(MinStructureGapPts, TickSize) - TickSize * 0.01)
                {
                    s.Extremes.Clear();
                    s.ExtremeBars.Clear();
                    Log0((s.Dir < 0 ? "Short" : "Long") + ": not a " + word + ", count restarted");
                }
            }

            s.Extremes.Add(x);
            s.ExtremeBars.Add(xb);

            if (ShowMarkers)
            {
                int ago = CurrentBar - xb;
                if (s.Dir < 0)
                    Draw.TriangleDown(this, "EMAR_R" + xb, false, ago, x + 2 * TickSize, Brushes.OrangeRed);
                else
                    Draw.TriangleUp(this, "EMAR_B" + xb, false, ago, x - 2 * TickSize, Brushes.DeepSkyBlue);

                int n = s.Extremes.Count;
                if (n >= 2)
                {
                    int pb = s.ExtremeBars[n - 2];
                    Draw.Line(this, "EMAR_S" + s.Dir + "_" + xb, false, CurrentBar - pb, s.Extremes[n - 2], ago, x,
                        s.Dir < 0 ? Brushes.OrangeRed : Brushes.DeepSkyBlue, DashStyleHelper.Dash, 1);
                }
                if (n == RequiredRejections)
                    Draw.Text(this, "EMAR_T" + s.Dir + "_" + xb, "x" + n, ago, s.Dir < 0 ? x + 6 * TickSize : x - 6 * TickSize,
                        s.Dir < 0 ? Brushes.OrangeRed : Brushes.DeepSkyBlue);
            }

            if (s.Extremes.Count == RequiredRejections)
                Log0((s.Dir < 0 ? "SHORT rejection" : "LONG bounce") + " confirmed x" + RequiredRejections);
        }

        private void Invalidate(SideState s, string reason)
        {
            bool had = RejectionReady(s) || s.Forced;
            s.Reset(false);
            // A trend pullback entry stays: the trend has its own break rules
            if (entryOrder != null && entryDir == s.Dir && !TrendReady(s))
                CancelEntry();
            if (had)
                Log0((s.Dir < 0 ? "Short" : "Long") + " setup cancelled: " + reason);
        }
        #endregion

        #region Orders
        private bool CanTradeNow()
        {
            return State == State.Realtime || !isChartHosted;
        }

        private bool InSession(int c)
        {
            if (!UseTimeFilter)
                return true;
            int t = ToTime(Time[c]);
            return StartTime <= EndTime ? (t >= StartTime && t < EndTime) : (t >= StartTime || t < EndTime);
        }

        // Price and delta doing the same thing on bar c (long; short mirrors): a price higher low with the same swing method
        // as the indicator, a close over the EMA, a delta higher low and delta over its fast EMA.
        private bool DeltaAgrees(int dir, int c)
        {
            return delta.PriceHlState[c] == dir && dir * (Close[c] - ema[c]) > 0
                && delta.HlState[c] == dir && delta.DeltaVsEma[c] == dir;
        }

        private int DeltaDivAgo(int dir, int c)
        {
            for (int i = c; i < c + DeltaDivBars && i <= CurrentBar; i++)
                if (delta.Divergence[i] == dir)
                    return i - c;
            return -1;
        }

        private string DeltaWhy(int dir, int c)
        {
            return "price " + (delta.PriceHlState[c] == dir ? "ok" : "no") + (dir > 0 ? " HL" : " LH")
                + ", " + (dir * (Close[c] - ema[c]) > 0 ? "ok" : "no") + (dir > 0 ? " over" : " under") + " EMA"
                + "; delta " + (delta.HlState[c] == dir ? "ok" : "no") + (dir > 0 ? " HL" : " LH")
                + ", " + (delta.DeltaVsEma[c] == dir ? "ok" : "no") + (dir > 0 ? " over" : " under") + " its EMA";
        }

        // trades.csv: delta_agree, delta_price_hl, delta_hl, delta_vs_ema, delta_ema_state, delta_div_ago
        private string DeltaCols(int dir, int c)
        {
            if (delta == null)
                return ",,,,,";
            int div = DeltaDivAgo(dir, c);
            return (DeltaAgrees(dir, c) ? "1" : "0") + "," + delta.PriceHlState[c] + "," + delta.HlState[c] + ","
                + delta.DeltaVsEma[c] + "," + delta.EmaState[c] + "," + (div >= 0 ? div.ToString() : "");
        }

        private void UpdateDeltaState(int c)
        {
            if (delta == null)
                return;
            delta.Update();
            foreach (SideState s in new[] { longSide, shortSide })
            {
                bool ready = RejectionReady(s) || TrendReady(s);
                bool agree = DeltaAgrees(s.Dir, c);
                s.DeltaTrigger = ready && agree && (!s.AgreeLast || !s.ReadyLast);
                if (DeltaMode == EmaRejDeltaMode.Required && ready && !s.ReadyLast && !agree)
                    Log0((s.Dir > 0 ? "Long" : "Short") + " setup ready, waiting for delta: " + DeltaWhy(s.Dir, c));
                s.DeltaAgree  = agree;
                s.AgreeLast   = agree;
                s.ReadyLast   = ready;
                s.DeltaDivAgo = DeltaDivAgo(s.Dir, c);
            }
        }

        // At market on a bar close instead of a resting limit: the trend bounce, or delta agreement when it is required
        private bool MarketEntry(SideState s)
        {
            return (planViaTrend && PullbackEntry == EmaRejPullbackEntry.OnReclaim)
                || (DeltaMode == EmaRejDeltaMode.Required && !s.Forced && delta != null);
        }

        // Newest side that passes the chop / trend filters. If none passes, the newest blocked one, with pauseReason set.
        private SideState PickSide(int c)
        {
            SideState best = null, blocked = null;
            foreach (SideState s in new[] { longSide, shortSide })
            {
                if (Direction == EmaRejDirection.LongOnly && s.Dir < 0) continue;
                if (Direction == EmaRejDirection.ShortOnly && s.Dir > 0) continue;
                if (!s.Forced)
                {
                    if (!RejectionReady(s) && !TrendReady(s)) continue;
                    if (UseSlopeFilter && s.Dir * (ema[c] - ema[c + SlopeBars]) <= 0) continue;
                }
                if (FilterReason(s).Length > 0)
                {
                    if (blocked == null || s.LastBar > blocked.LastBar)
                        blocked = s;
                    continue;
                }
                if (best == null || s.LastBar > best.LastBar)
                    best = s;
            }
            pauseReason = best == null && blocked != null ? FilterReason(blocked) : "";
            return best ?? blocked;
        }

        // Chop / trend pause automatic setups only; BUY/SELL @EMA (forced) always go through
        private string FilterReason(SideState s)
        {
            if (s.Forced)
                return "";
            if (UseChopFilter && inChop)
                return "chop";
            // The trend pullback has its own trend rules (a brief close through the EMA is part of it)
            if (TradeWithTrend && trendDir != s.Dir && !ViaTrend(s))
                return "trend not " + (s.Dir < 0 ? "bearish" : "bullish");
            return "";
        }

        private double Rnd(double price)
        {
            return Instruments[execIdx].MasterInstrument.RoundToTickSize(price);
        }

        private bool BuildPlan(SideState s, int c)
        {
            double e = ema[c];
            double target;
            planViaTrend = ViaTrend(s);
            double entry = Rnd(e + s.Dir * PlanOffset(s.Dir));
            if (MarketEntry(s))
                entry = Rnd(Close[c]);   // at market on the bar close
            else if (!TrackEma && entryOrder != null && entryDir == s.Dir)
                entry = entryOrder.LimitPrice;

            // Target = the leg's lower low (short) / higher high (long) + buffer; Minimum target is only a floor
            planAnchor = LegAnchor(s);
            double leg = LegExtreme(s.Dir, planAnchor, c);
            planLeg = leg;
            planFallback = !PickTarget(s.Dir, entry, leg, out target);

            planDir    = s.Dir;
            planEma    = e;
            planEntry  = entry;
            planTarget = target;
            planStop   = Rnd(entry - s.Dir * StopLossPts);
            planNote   = "";
            return true;
        }

        private double PlanOffset(int dir)
        {
            return planViaTrend ? PullbackOffsetPts : dir > 0 ? LongOffsetPts : ShortOffsetPts;
        }

        // Where the target leg starts: the setup's first rejection; a forced setup uses the top (short) / bottom (long)
        // of the move, frozen when the button was clicked; a trend pullback uses the reclaim bar.
        private int LegAnchor(SideState s)
        {
            if (planViaTrend)
                return s.TrendAnchor;
            if (s.ExtremeBars.Count > 0)
                return s.ExtremeBars[0];
            if (s.Forced)
                return s.ForcedAnchor;
            return s.Dir < 0 ? lastSwingHighBar : lastSwingLowBar;
        }

        // Lowest low (short) / highest high (long) from the anchor bar up to bar c.
        private double LegExtreme(int dir, int anchor, int c)
        {
            if (anchor < 0)
                return double.NaN;
            int from = Math.Min(CurrentBar - anchor, 250);
            if (from < c)
                return double.NaN;
            double x = dir < 0 ? double.MaxValue : double.MinValue;
            for (int i = c; i <= from; i++)
                x = dir < 0 ? Math.Min(x, Low[i]) : Math.Max(x, High[i]);
            return x;
        }

        // Leg target, but never closer than Minimum target (x stop). Returns false when the minimum was used.
        private bool PickTarget(int dir, double entry, double leg, out double target)
        {
            double minPts = StopLossPts * MinTargetR;
            double floor  = Rnd(entry + dir * minPts);
            target = double.IsNaN(leg) ? floor : Rnd(leg + dir * TargetBeyondSwingPts);
            if (dir * (target - entry) < minPts - execTick * 0.01)
            {
                target = floor;
                return false;
            }
            return true;
        }

        // In a trade: keep following the leg, only ever moving the target further. A TP dragged on the chart is left alone.
        private void UpdateTradeTarget(int c)
        {
            Position p = Positions[execIdx];
            if (!tradeOpen || targetManual || p.MarketPosition == MarketPosition.Flat)
                return;
            int dir = p.MarketPosition == MarketPosition.Long ? 1 : -1;
            double target;
            tradeLeg = LegExtreme(dir, tradeAnchor, c);
            PickTarget(dir, p.AveragePrice, tradeLeg, out target);
            if (dir * (target - lastSetTarget) > execTick * 0.5)
            {
                if (!ownTargetPending)
                    preExtendTarget = lastSetTarget;   // keep the oldest pending price
                ownTargetPending = true;
                SetTarget(activeSignal, target);
                Log0("TP extended -> " + Fmt(target));
            }
        }

        private void SetTarget(string sig, double price)
        {
            lastSetTarget = price;
            SetProfitTarget(sig, CalculationMode.Price, price);
        }

        private double Tp1Price(int dir, double entry)
        {
            return Rnd(entry + dir * Tp1R * StopLossPts);
        }

        // Split targets: once price reaches TP1, half the position (the runner keeps at least 1) exits at market. Sent from
        // OnBarUpdate, never from a fill callback, and as a market order: NinjaTrader ignores non-market exits while the
        // stop / target are working. The stop and target follow the smaller position by themselves (ByStrategyPosition).
        private void ManageTp1()
        {
            if (!splitActive || tp1Sent || !tradeOpen || fillPending)
                return;
            Position p = Positions[execIdx];
            if (p.MarketPosition == MarketPosition.Flat)
                return;
            int qty = Math.Min(Contracts / 2, p.Quantity - 1);
            if (qty <= 0)
                return;   // only part of the entry filled so far: wait for more, or the target takes it all
            bool isLong = p.MarketPosition == MarketPosition.Long;
            int dir = isLong ? 1 : -1;
            // History: the close (a bar's high / low may come from before the fill, and the exit fills on the next open)
            double px = State == State.Realtime ? (isLong ? GetCurrentBid(execIdx) : GetCurrentAsk(execIdx)) : Closes[execIdx][0];
            if (dir * (px - tp1Price) < -execTick * 0.01)
                return;

            tp1Sent = true;
            // Target at (or next to) TP1: a market exit racing the target could overfill, so the target takes the whole position
            if (dir * (lastSetTarget - tp1Price) < Tp1MinGapPts - execTick * 0.01)
            {
                tp1Note = "skipped, TP near TP1";
                Log0("TP1 skipped: target " + Fmt(lastSetTarget) + " is within " + Tp1MinGapPts.ToString("0.##") + " pts of TP1");
                return;
            }
            Order o = isLong ? ExitLong(execIdx, qty, Tp1Name, activeSignal) : ExitShort(execIdx, qty, Tp1Name, activeSignal);
            tp1Order = o;
            tp1Qty   = qty;
            tp1Note  = o == null ? "ignored by NinjaTrader" : "sent";
            Log0(o == null ? "TP1 exit ignored by NinjaTrader (see Output with Trace orders on)"
                           : "TP1 reached " + Fmt(tp1Price) + ": " + (isLong ? "selling " : "buying ") + qty + " at market");
        }

        private void SetStops(double price)
        {
            SetStopLoss(activeSignal, CalculationMode.Price, price, false);
        }

        // Market exit of the whole position, less what a TP1 exit still in flight takes out
        private void ExitAll(string name)
        {
            Position p = Positions[execIdx];
            int qty = p.Quantity;
            if (tp1Order != null && !Order.IsTerminalState(tp1Order.OrderState))
                qty -= tp1Order.Quantity - tp1Order.Filled;
            if (qty <= 0)
                return;
            if (p.MarketPosition == MarketPosition.Long) ExitLong(execIdx, qty, name, "");
            else                                          ExitShort(execIdx, qty, name, "");
        }

        private static bool IsEntrySignal(string name)
        {
            return name == SigLong || name == SigShort;
        }

        private void ManageEntry(int c)
        {
            int cb = CurrentBar - c;
            if (Positions[execIdx].MarketPosition != MarketPosition.Flat)
            {
                planValid = false;
                return;
            }

            SideState s = PickSide(c);
            planValid = s != null && BuildPlan(s, c);

            string block = DayBlockReason();
            if (block != dayBlock && block.Length > 0 && planValid && armed && CanTradeNow())
                Log0("Entries blocked: " + block);
            dayBlock = block;

            // A position on this account the strategy didn't open (manual / ATM) would mix with ours: no entries until it's flat
            // The broker's account position can lag our own fills by a moment, so it is only judged once fills have been quiet
            string acct = CanTradeNow() && State == State.Realtime && (DateTime.Now - lastFillWall).TotalSeconds > 3
                && PositionsAccount[execIdx].MarketPosition != MarketPosition.Flat
                ? "account has a position the strategy doesn't own (" + PositionsAccount[execIdx].MarketPosition + " "
                  + PositionsAccount[execIdx].Quantity + ")" : "";
            if (acct != accountBlock && acct.Length > 0 && planValid && armed)
                Log0("Entries blocked: " + acct);
            accountBlock = acct;

            DropFinishedEntries();
            bool allowed = planValid && pauseReason.Length == 0 && dayBlock.Length == 0 && accountBlock.Length == 0
                && armed && CanTradeNow() && InSession(c);
            if (!allowed)
            {
                CancelEntry();
                return;
            }

            if (entryOrder != null && entryDir != s.Dir)
            {
                CancelEntry();
                return;
            }

            // At market on a bar close, no resting limit:
            // - trend pullback "On reclaim": the bar that bounced back over the EMA (and, if required, with delta agreeing)
            // - delta Required: the bar where price and delta start agreeing while the setup is ready
            if (MarketEntry(s))
            {
                planTooClose = false;
                if (entryOrder != null)
                {
                    if (entryOrder.OrderType == OrderType.Limit)
                        CancelEntry();
                    return;
                }
                bool reclaim = planViaTrend && PullbackEntry == EmaRejPullbackEntry.OnReclaim;
                if (reclaim ? s.TriggerBar != cb : !s.DeltaTrigger)
                    return;
                s.TriggerBar   = -1;
                s.DeltaTrigger = false;
                if (DeltaMode == EmaRejDeltaMode.Required && !s.Forced && !s.DeltaAgree)
                {
                    Log0((s.Dir > 0 ? "BUY" : "SELL") + " skipped: delta not confirming (" + DeltaWhy(s.Dir, c) + ")");
                    return;
                }
                double chase = s.Dir * (Close[c] - ema[c]);
                if (chase > ReclaimMaxChasePts + execTick * 0.01)
                {
                    Log0((s.Dir > 0 ? "BUY" : "SELL") + " skipped: closed " + chase.ToString("0.##") + " pts from EMA (max "
                        + ReclaimMaxChasePts.ToString("0.##") + ")");
                    return;
                }
                SubmitEntry(s, cb, true);
                return;
            }

            // The limit must rest at least MinDistFromPricePts away from the execution instrument's market,
            // waiting for price to come back to the EMA. Closer than that = wait (new) or leave it where it is (working).
            double bid = State == State.Realtime ? GetCurrentBid(execIdx) : Closes[execIdx][0];
            double ask = State == State.Realtime ? GetCurrentAsk(execIdx) : Closes[execIdx][0];
            double away = s.Dir > 0 ? bid - planEntry : planEntry - ask;
            bool farEnough = away > execTick * 0.01 && away >= MinDistFromPricePts - execTick * 0.01;
            planTooClose = !farEnough;

            if (entryOrder == null)
            {
                if (farEnough)
                    SubmitEntry(s, cb, false);
                return;
            }
            if (entryOrder.OrderType != OrderType.Limit)
                return;   // a market entry on its way

            if (OrderExpiryBars > 0 && cb - entrySubmitBar >= OrderExpiryBars)
            {
                Invalidate(s, "limit expired");
                CancelEntry();   // a trend limit too (Invalidate leaves it to the trend rules)
                return;
            }

            double restingAt = entryOrder.LimitPrice;
            if (TrackEma && farEnough
                && (entryOrder.OrderState == OrderState.Working || entryOrder.OrderState == OrderState.Accepted)
                && Math.Abs(entryOrder.LimitPrice - planEntry) > execTick * 0.5)
            {
                ChangeOrder(entryOrder, entryOrder.Quantity, planEntry, 0);
                restingAt = planEntry;
            }

            SetTarget(entrySignal, planTarget);
            if (splitActive)
                tp1Price = Tp1Price(entryDir, restingAt);
        }

        // Day limits: "" = new entries allowed. A trade is only taken if its full stop keeps the day inside the daily stop.
        private string DayBlockReason()
        {
            if (dayStopReason.Length > 0)
                return dayStopReason;
            if (MaxTradesPerSession > 0 && tradesThisSession >= MaxTradesPerSession)
                return "max trades";
            double risk = (StopLossPts * Instruments[execIdx].MasterInstrument.PointValue + CostPerContractUsd) * Contracts;
            if (DailyStopUsd > 0 && dayPnl - risk < -DailyStopUsd)
                return "daily stop: a full stop (" + Usd(-risk) + ") would pass it";
            return "";
        }

        private void CheckDayLimits()
        {
            if (dayStopReason.Length > 0)
                return;
            if (DailyStopUsd > 0 && dayPnl <= -DailyStopUsd)
                dayStopReason = "daily stop";
            else if (MaxLossesPerDay > 0 && dayLosses >= MaxLossesPerDay)
                dayStopReason = "max losses";
            else
                return;
            Log0("Day limit hit (" + dayStopReason + "): day " + Usd(dayPnl) + ", losses " + dayLosses);
            Disarm(dayStopReason);
        }

        // All contracts are out: work out the trade now (pure math, no I/O or order calls inside the fill callback),
        // and book it for the day limits and the journal on the next update (ApplyClosedTrades)
        private void CloseTrade()
        {
            if (tradeQty <= 0 || exitQty <= 0)
                return;
            double entryPx = entryValue / tradeQty;
            double exitPx  = exitValue / exitQty;
            double pts     = tradeDir * (exitPx - entryPx);
            // Some connections (Tradovate) report no commission on fills: estimate it so P&L and the day limits aren't flattered
            costEstimated = tradeCommission <= 0 && CostPerContractUsd > 0;
            if (costEstimated)
                tradeCommission = CostPerContractUsd * exitQty;
            double pnl = pts * Instruments[execIdx].MasterInstrument.PointValue * exitQty - tradeCommission;
            if (fillPending)
                SnapshotContext(fillPrice, fillTime);   // opened and closed before the next update: context for the row (pure math)
            pendingCloses.Add(new ClosedTrade
            {
                Pnl     = pnl,
                Message = "Trade closed " + Usd(pnl) + " (" + pts.ToString("0.##") + " pts)",
                Row     = State == State.Realtime ? JournalRow(entryPx, exitPx, pts, pnl) : null
            });
            // Booked: a late entry partial after this starts a fresh tally
            tradeQty   = exitQty = 0;
            entryValue = exitValue = tradeCommission = 0;
        }

        private void ApplyClosedTrades()
        {
            foreach (ClosedTrade ct in pendingCloses)
            {
                dayPnl += ct.Pnl;
                if (ct.Pnl < 0)
                    dayLosses++;
                Log0(ct.Message + ", day " + Usd(dayPnl));
                CheckDayLimits();
                if (ct.Row != null)
                    QueueFile(new FileWrite { Path = AnalysisPath("trades.csv"), Text = ct.Row + Environment.NewLine, Append = true, Header = JournalHeader });
            }
            pendingCloses.Clear();
        }

        private const string JournalHeader = "date,open_et,close_et,account,instrument,side,contracts,entry,stop_initial,tp_initial,"
            + "stop_final,tp_final,exit,points,pnl_usd,commission_usd,r,mae_pts,mfe_pts,exit_reason,trend,chop,forced,grade,cost_estimated,"
            + "session,tf15_trend,value_loc,nearest_level,level_dist_pts,setup,"
            + "delta_agree,delta_price_hl,delta_hl,delta_vs_ema,delta_ema_state,delta_div_ago";

        // One row per trade in Documents\NinjaTrader 8\analysis\trades.csv, for the strategy-analyst and risk-manager
        private string JournalRow(double entryPx, double exitPx, double pts, double pnl)
        {
            DateTime openEt  = ToEt(tradeOpenTime);
            DateTime closeEt = ToEt(tradeCloseTime);
            double mae = Math.Max(0, tradeDir > 0 ? entryPx - tradeLow : tradeHigh - entryPx);
            double mfe = Math.Max(0, tradeDir > 0 ? tradeHigh - entryPx : entryPx - tradeLow);
            return string.Join(",", new[]
            {
                openEt.AddHours(6).ToString("yyyy-MM-dd"), openEt.ToString("HH:mm:ss"), closeEt.ToString("HH:mm:ss"),
                Account.Name, Instruments[execIdx].FullName, tradeDir > 0 ? "long" : "short", exitQty.ToString(),
                Num(entryPx), Num(tradeStopInit), Num(tradeTpInit), Num(lastSetStop), Num(lastSetTarget), Num(exitPx),
                Num(pts), Num(pnl), Num(tradeCommission), Num(StopLossPts > 0 ? pts / StopLossPts : 0), Num(mae), Num(mfe),
                exitReason.Replace(",", " "), tradeTrend > 0 ? "bull" : tradeTrend < 0 ? "bear" : "mixed",
                tradeChop ? "chop" : "", tradeForced ? "forced" : "", "", costEstimated ? "1" : "",
                ctxSession, ctxTf15, ctxValue, ctxLevel, ctxLevel.Length > 0 ? Num(ctxDist) : "", tradeSetup, tradeDeltaCols
            });
        }

        // Fill work deferred out of NinjaTrader's order / execution callbacks, done on the execution series' next update
        private void ProcessPending()
        {
            if (fillPending)
            {
                fillPending = false;
                int fdir = activeSignal == SigLong ? 1 : -1;
                // TP was planned from the limit price: re-floor it from the actual fill (only ever further, never over a drag)
                double fillTarget;
                PickTarget(fdir, fillPrice, tradeLeg, out fillTarget);
                if (tradeOpen && !targetManual && fdir * (fillTarget - lastSetTarget) > execTick * 0.5)
                {
                    if (!ownTargetPending)
                        preExtendTarget = lastSetTarget;   // keep the oldest pending price
                    ownTargetPending = true;
                    SetTarget(activeSignal, fillTarget);
                    Log0("TP re-set from fill -> " + Fmt(fillTarget));
                }
                if (splitActive && !tp1Sent)
                    tp1Price = Tp1Price(fdir, fillPrice);   // exactly Tp1R from the fill
                tradeTpInit = lastSetTarget;
                SnapshotContext(fillPrice, fillTime);
                Log0((fdir > 0 ? "LONG" : "SHORT") + " filled @ " + Fmt(fillPrice) + (splitActive ? ", TP1 " + Fmt(tp1Price) : ""));
            }

            if (bePending)
            {
                bePending = false;
                int dir = activeSignal == SigLong ? 1 : -1;
                if (tradeOpen && Positions[execIdx].MarketPosition != MarketPosition.Flat && dir * (beStop - lastSetStop) > execTick * 0.5)
                {
                    lastSetStop = beStop;
                    SetStopLoss(activeSignal, CalculationMode.Price, beStop, false);
                    Log0("Runner stop -> breakeven " + Fmt(beStop));
                }
            }

            if (pendingCloses.Count > 0)
                ApplyClosedTrades();

            if (lastFileError.Length > 0)
            {
                Print(Name + ": could not write " + lastFileError);
                lastFileError = "";
            }

            if (pendingDisarm.Length > 0)
            {
                string why = pendingDisarm;
                pendingDisarm = "";
                Disarm(why);
            }

            if (cancelLeftover)
            {
                cancelLeftover = false;
                // Only the order left over from that trade, never a fresh one sent since
                Order lo = leftoverEntry;
                if (lo != null && !Order.IsTerminalState(lo.OrderState)
                    && lo.OrderState != OrderState.CancelPending && lo.OrderState != OrderState.CancelSubmitted)
                {
                    entryCancelRequested = true;
                    CancelOrder(lo);
                }
                leftoverEntry = null;
            }
        }

        private DateTime ToEt(DateTime t)
        {
            return TimeZoneInfo.ConvertTime(t, NinjaTrader.Core.Globals.GeneralOptions.TimeZoneInfo, easternTz);
        }

        private static string Num(double x)
        {
            return x.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
        }

        private static string Usd(double x)
        {
            return (x < 0 ? "-$" : "$") + Math.Abs(x).ToString("0");
        }

        private void SubmitEntry(SideState s, int cb, bool atMarket)
        {
            string sig = s.Dir > 0 ? SigLong : SigShort;
            int stopTicks = Math.Max(1, (int)Math.Round(StopLossPts / execTick));
            // Split: half exits at TP1 (odd counts give the runner the extra contract)
            splitActive = SplitTargets && Contracts >= 2;
            tp1Price    = Tp1Price(s.Dir, planEntry);
            SetStopLoss(sig, CalculationMode.Ticks, stopTicks, false);
            SetTarget(sig, planTarget);

            entryCancelRequested = false;
            Order o = atMarket
                ? (s.Dir > 0 ? EnterLong(execIdx, Contracts, sig) : EnterShort(execIdx, Contracts, sig))
                : s.Dir > 0
                    ? EnterLongLimit(execIdx, true, Contracts, planEntry, sig)
                    : EnterShortLimit(execIdx, true, Contracts, planEntry, sig);
            if (o == null)
            {
                // NinjaTrader ignored it (its order handling rules); turn on "Trace orders" to see why in the Output window
                if (!entryIgnoredLogged)
                    Log0((s.Dir > 0 ? "BUY" : "SELL") + (atMarket ? " MKT" : " LMT") + " ignored by NinjaTrader (see Output with Trace orders on)");
                entryIgnoredLogged = true;
                return;
            }
            entryIgnoredLogged = false;

            entryOrder     = o;
            entryDir       = s.Dir;
            entrySignal    = sig;
            entrySubmitBar = cb;
            int c = CurrentBar - cb;
            entryDeltaCols = DeltaCols(s.Dir, c);
            string deltaNote = delta == null || s.Forced ? "" : DeltaAgrees(s.Dir, c) ? " | delta agrees" : " | delta not confirming";
            Log0((s.Dir > 0 ? "BUY" : "SELL") + (atMarket ? " MKT (" + (planViaTrend ? "trend pullback bounce" : "delta agreement") + ", close " + Fmt(planEntry) + ")" : " LMT " + Fmt(planEntry))
                + " sent" + (planViaTrend ? " [trend " + (s.TrendEntries + 1) + "/" + (TrendMaxEntries > 0 ? TrendMaxEntries.ToString() : "-") + "]" : "")
                + (splitActive ? " (" + (Contracts / 2) + " at TP1 " + Fmt(tp1Price) + ", " + (Contracts - Contracts / 2) + " runner)" : "")
                + deltaNote);
        }

        private void CancelEntry()
        {
            DropFinishedEntries();
            Order o = entryOrder;
            if (o != null && o.OrderState != OrderState.CancelPending && o.OrderState != OrderState.CancelSubmitted)
            {
                entryCancelRequested = true;
                CancelOrder(o);
            }
        }

        // An entry order that is already filled, cancelled or rejected is forgotten, so a stale reference can never block
        // the next entry
        private void DropFinishedEntries()
        {
            if (entryOrder != null && Order.IsTerminalState(entryOrder.OrderState))
                entryOrder = null;
        }

        // Put back a stop dragged further away (flagged in OnOrderUpdate). Waits until the drag has settled for a
        // second and the order has no change in flight, so it never fights the user.
        private void EnforceExitDrags()
        {
            if (!syncStop || (DateTime.Now - lastDragTime).TotalSeconds < 1 || !Settled(looseOrder))
                return;
            Position p = Positions[execIdx];
            if (p.MarketPosition == MarketPosition.Flat)
            {
                syncStop = stopFallback = false;
                return;
            }

            // Price already through the allowed stop: a stop there would be rejected (and the strategy shut down), so exit now
            bool isLong = p.MarketPosition == MarketPosition.Long;
            double px = State == State.Realtime ? (isLong ? GetCurrentBid(execIdx) : GetCurrentAsk(execIdx)) : Closes[execIdx][0];
            if (isLong ? px <= lastSetStop : px >= lastSetStop)
            {
                syncStop = stopFallback = false;
                Log0("Price through allowed stop " + Fmt(lastSetStop) + ", exiting at market");
                ExitAll("Stop put-back");
                return;
            }

            if (!stopFallback)
            {
                SetStops(lastSetStop);
                stopFallback = true;   // check again on a later tick, once this change has had time to land
                return;
            }
            // Engine already held this price and left the order alone: put a looser stop back directly. Sent without checking
            // the order's StopPrice, which can show the old price on some connections; at worst one redundant modify.
            ChangeOrder(looseOrder, looseOrder.Quantity, 0, lastSetStop);
            syncStop = stopFallback = false;
        }

        private static bool Settled(Order o)
        {
            return o != null && (o.OrderState == OrderState.Working || o.OrderState == OrderState.Accepted);
        }

        private void ManageProfitLock()
        {
            if (!UseProfitLock || profitLocked || !tradeOpen)
                return;
            Position p = Positions[execIdx];
            if (p.MarketPosition == MarketPosition.Flat)
                return;

            double px   = Closes[execIdx][0];
            bool isLong = p.MarketPosition == MarketPosition.Long;
            double gain = isLong ? px - p.AveragePrice : p.AveragePrice - px;
            if (gain < ProfitLockTriggerPts)
                return;

            double lockPx = Rnd(isLong ? p.AveragePrice + ProfitLockPts : p.AveragePrice - ProfitLockPts);
            profitLocked = true;
            // Never loosen a stop that was already tightened by hand past the lock level
            if (stopKnown && (isLong ? 1 : -1) * (lockPx - lastSetStop) <= execTick * 0.5)
                return;
            lastSetStop = lockPx;
            SetStops(lockPx);
            Log0("Profit lock: stop -> " + Fmt(lockPx));
            RefreshUi();
        }

        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice, int quantity, int filled,
            double averageFillPrice, OrderState orderState, DateTime time, ErrorCode error, string nativeError)
        {
            // Exits dragged on the chart: a tighter stop is kept, a looser one is put back; the TP is kept wherever it is dropped.
            // A drag is read from its change request (ChangePending: the price asked for). Working / Accepted prices are only
            // used until a change request has been seen, because some connections echo the old price there.
            bool isRequest = orderState == OrderState.ChangePending;
            if (tradeOpen && (isRequest || orderState == OrderState.Working || orderState == OrderState.Accepted))
            {
                int dir = activeSignal == SigLong ? 1 : -1;
                if (order.Name == "Stop loss")
                {
                    // A tighter stop asked for is only taken once the broker accepts the change (a stop past the market is rejected)
                    if (!isRequest && pendingTighten > 0 && order == pendingTightenOrder)
                    {
                        // Skipped if the stop moved past it meanwhile (e.g. the profit lock)
                        if (error == ErrorCode.NoError && dir * (pendingTighten - lastSetStop) > execTick * 0.5)
                        {
                            lastSetStop = pendingTighten;
                            // Tell the managed engine right away, or it puts the stop back on the next tick
                            SetStops(pendingTighten);
                            Log0("SL tightened manually -> " + Fmt(pendingTighten));
                        }
                        pendingTighten      = 0;
                        pendingTightenOrder = null;
                    }
                    double px = isRequest ? order.StopPriceChanged : stopPrice;
                    if (isRequest && px > 0)
                        changeReqSeen = true;
                    if (px > 0 && (isRequest || !changeReqSeen))
                    {
                        double d = dir * (px - lastSetStop);
                        if (stopKnown && isRequest && d > execTick * 0.5)
                        {
                            pendingTighten      = px;
                            pendingTightenOrder = order;
                        }
                        else if (!stopKnown || d > execTick * 0.5)
                        {
                            lastSetStop = px;
                            if (stopKnown)
                            {
                                SetStops(px);
                                Log0("SL tightened manually -> " + Fmt(px));
                            }
                            stopKnown = true;
                        }
                        else if (d < -execTick * 0.5 && !(syncStop && Math.Abs(px - looseStop) <= execTick * 0.5))
                        {
                            // Hold the dragged price first so the engine's copy matches the broker, then put it back
                            looseStop  = px;
                            looseOrder = order;
                            if (isRequest)
                                SetStopLoss(order.FromEntrySignal, CalculationMode.Price, px, false);
                            Log0("SL can only be tightened, back to " + Fmt(lastSetStop));
                            lastDragTime = DateTime.Now;
                            syncStop     = true;
                            stopFallback = false;
                        }
                    }
                }
                else if (order.Name == "Profit target")
                {
                    double px = isRequest ? order.LimitPriceChanged : limitPrice;
                    if (isRequest && px > 0)
                        changeReqSeen = true;
                    if (px > 0 && (isRequest || !changeReqSeen))
                    {
                        bool matchesOurs = Math.Abs(px - lastSetTarget) <= execTick * 0.5;
                        if (matchesOurs)
                            ownTargetPending = false;   // our auto-extend has landed
                        // Until our own change lands, a price between the oldest pending target and ours is a stale echo, not a drag
                        else if (!ownTargetPending || dir * (px - preExtendTarget) < -execTick * 0.5
                                                   || dir * (lastSetTarget - px) < -execTick * 0.5)
                        {
                            targetManual = true;
                            // Tell the managed engine right away, or it puts the TP back on the next tick
                            SetTarget(activeSignal, px);
                            Log0("TP moved manually -> " + Fmt(px));
                        }
                    }
                }
            }

            if (!IsEntrySignal(order.Name))
                return;
            if (Order.IsTerminalState(orderState))
            {
                bool ours = entryOrder == order;
                if (ours)
                    entryOrder = null;
                // Cancelled from outside the strategy (chart / Orders tab): treat it as "stop", or AUTO would send it again.
                // AUTO OFF is handled on the next update, not inside this callback.
                // Not counted: NinjaTrader's own cancels in the first seconds of realtime (historical orders) and around
                // the 17:00 ET close (exit on session close)
                if (ours && orderState == OrderState.Cancelled && !entryCancelRequested && armed && State == State.Realtime
                    && !NtOwnCancel())
                {
                    Log0("Entry cancelled outside the strategy");
                    pendingDisarm = "entry cancelled on the chart";
                }
            }
            else
                entryOrder = order;
        }

        protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity,
            MarketPosition marketPosition, string orderId, DateTime time)
        {
            Order o = execution.Order;
            if (o == null)
                return;
            lastFillWall = DateTime.Now;

            if (IsEntrySignal(o.Name) && !tradeOpen)
            {
                tradeOpen    = true;
                profitLocked = false;
                activeSignal = o.Name;
                tradeAnchor  = planAnchor;
                tradeLeg     = planLeg;
                targetManual = false;
                tradeEntry   = price;
                // Stop was sent as StopLossPts from the fill: know it now, so even the first drag is checked
                lastSetStop  = Rnd(price - (activeSignal == SigLong ? 1 : -1) * StopLossPts);
                stopKnown     = true;
                syncStop      = false;
                stopFallback  = false;
                looseOrder    = pendingTightenOrder = null;
                pendingTighten = 0;
                changeReqSeen  = false;   // each trade re-learns whether this connection sends change requests
                ownTargetPending = false;
                int fdir = activeSignal == SigLong ? 1 : -1;
                tp1Sent     = tp1Done = stopHandled = false;
                tp1Note     = "";
                tp1Order    = null;
                bePending   = false;
                // TP re-set from the fill, TP1, trade context and the fill log line: next update (ProcessPending)
                fillPending = true;
                fillPrice   = price;
                fillTime    = time;
                tradesThisSession++;
                // Journal: what the trade started with
                tradeDir        = fdir;
                tradeQty        = exitQty = 0;
                entryValue      = exitValue = tradeCommission = 0;
                tradeHigh       = tradeLow = price;
                tradeOpenTime   = time;
                tradeStopInit   = lastSetStop;
                tradeTpInit     = lastSetTarget;
                tradeTrend      = trendDir;
                tradeChop       = inChop;
                exitReason      = "";
                // Setup is used up: a new one has to form after this trade
                SideState s = activeSignal == SigLong ? longSide : shortSide;
                tradeForced = s.Forced;
                tradeSetup  = s.Forced ? "forced" : planViaTrend ? "trend" : "rejection";
                tradeDeltaCols = entryDeltaCols;
                if (s.TrendOn && planViaTrend && !s.Forced)
                {
                    s.TrendEntries++;
                    s.TrendWaitPb = true;   // the next trend entry needs the next pullback
                }
                s.Reset(false);
            }
            else if ((o.Name == "Stop loss" || o.Name == "Stop put-back") && o.OrderState == OrderState.Filled && !stopHandled)
            {
                stopHandled = true;   // count the stop-out once, even when it fills in pieces
                // A stop at or past entry (profit lock, tightened by hand) or a runner after TP1 banked is not a full stop-loss
                int dir = activeSignal == SigLong ? 1 : -1;
                if (profitLocked || tp1Done || (stopKnown && dir * (lastSetStop - tradeEntry) >= 0))
                {
                    Log0("Profit-lock stop hit, setup ended");
                    if (DisarmOnProfitLockStop) pendingDisarm = "profit-lock stop";
                }
                else
                {
                    Log0("STOPPED OUT");
                    if (DisarmOnStopLoss) pendingDisarm = "stop loss";
                    // A full stop-out means the higher-low idea failed: that trend leg is done
                    SideState ts = activeSignal == SigLong ? longSide : shortSide;
                    if (ts.TrendOn)
                        EndTrend(ts, "stopped out");
                }
            }
            else if (o.Name == Tp1Name && o.OrderState == OrderState.Filled)
            {
                tp1Done = true;
                tp1Note = "hit";
                Log0("TP1 hit @ " + Fmt(price));
                // Runner still open: its stop goes to breakeven on the next update (never loosened)
                double be = tradeQty > 0 ? Rnd(entryValue / tradeQty) : tradeEntry;
                if (RunnerToBreakeven && tradeQty - exitQty - quantity > 0)
                {
                    bePending = true;
                    beStop    = be;
                }
            }
            else if (o.Name == "Profit target" && o.OrderState == OrderState.Filled)
                Log0("TARGET hit @ " + Fmt(price));

            // Every fill of the open trade (partials included), for the journal and the day P&L.
            // tradeQty > 0 keeps exits counted even if the Flat position update arrives before the last exit fill.
            if ((tradeOpen || tradeQty > 0) && execution.Instrument == Instruments[execIdx])
            {
                tradeCommission += execution.Commission;
                if (IsEntrySignal(o.Name))
                {
                    tradeQty   += quantity;
                    entryValue += price * quantity;
                }
                else
                {
                    exitQty        += quantity;
                    exitValue      += price * quantity;
                    exitReason      = (tp1Done && o.Name != Tp1Name ? "TP1+" : "") + o.Name;
                    tradeCloseTime  = time;
                    if (exitQty >= tradeQty)
                        CloseTrade();
                }
            }

            RefreshUi();
        }

        protected override void OnPositionUpdate(Position position, double averagePrice, int quantity, MarketPosition marketPosition)
        {
            if (position.Instrument == Instruments[execIdx] && marketPosition == MarketPosition.Flat)
            {
                // The unfilled rest of a partly filled entry is cancelled on the next update, not inside NinjaTrader's position update
                if (tradeOpen)
                {
                    leftoverEntry  = entryOrder;
                    cancelLeftover = true;
                }
                tradeOpen = false;
            }
        }

        // NinjaTrader cancels entries itself in the first seconds of realtime (historical orders) and at exit on session
        // close: within 5 min of the chart's session end (any trading-hours template) or of the 17:00 ET Globex close.
        // Uses the PC clock only, so it can't throw inside a callback.
        private bool NtOwnCancel()
        {
            if ((DateTime.Now - realtimeSince).TotalSeconds < 10)
                return true;
            DateTime chartNow = TimeZoneInfo.ConvertTime(DateTime.Now, NinjaTrader.Core.Globals.GeneralOptions.TimeZoneInfo);
            if (sessionEnd != DateTime.MinValue && Math.Abs((chartNow - sessionEnd).TotalMinutes) <= 5)
                return true;
            TimeSpan et = TimeZoneInfo.ConvertTime(DateTime.Now, easternTz).TimeOfDay;
            return et >= new TimeSpan(16, 55, 0) && et < new TimeSpan(17, 5, 0);
        }

        private void Disarm(string why)
        {
            // Backtests keep running: only a chart in realtime turns itself off
            if (!isChartHosted || State != State.Realtime)
                return;
            armed = false;
            CancelEntry();
            Log0("AUTO OFF (" + why + ")");
        }
        #endregion

        #region Drawing / dashboard
        private void DrawPlan()
        {
            if (!ShowPlanLines || CurrentBar < 15)
                return;
            if (splitActive && tradeOpen && !tp1Sent)
                Draw.Line(this, "EMAR_TP1", false, 12, tp1Price, 0, tp1Price, Brushes.Gold, DashStyleHelper.Dash, 2);
            else
                RemoveDrawObject("EMAR_TP1");
            if (!planValid)
            {
                RemoveDrawObject("EMAR_PE");
                RemoveDrawObject("EMAR_PS");
                RemoveDrawObject("EMAR_PT");
                return;
            }
            Brush entryBrush = planDir > 0 ? Brushes.DeepSkyBlue : Brushes.OrangeRed;
            Draw.Line(this, "EMAR_PE", false, 12, planEntry, 0, planEntry, entryBrush, DashStyleHelper.Dash, 2);
            Draw.Line(this, "EMAR_PS", false, 12, planStop, 0, planStop, Brushes.Red, DashStyleHelper.Dot, 1);
            Draw.Line(this, "EMAR_PT", false, 12, planTarget, 0, planTarget, Brushes.LimeGreen, DashStyleHelper.Dot, 1);
        }

        private string Fmt(double price)
        {
            return Instruments[execIdx].MasterInstrument.FormatPrice(price);
        }

        private void Log0(string msg)
        {
            lastEvent = (CurrentBars[0] >= 0 ? Times[0][0].ToString("HH:mm:ss") + " " : "") + msg;
            if (State != State.Realtime)
                return;
            string line = Name + " " + Instruments[execIdx].FullName + ": " + lastEvent;
            Print(line);
            WriteLogFile(line);
        }

        // Same lines as the Output window, also kept in Documents\NinjaTrader 8\log\EmaRejection.yyyyMMdd.txt
        // (prefixed with the PC clock) so they survive a restart and can be read without copy-pasting.
        // All file writes go through one background writer, so NinjaTrader's order / execution threads never wait on the
        // disk (or OneDrive). Lines keep their order because one writer drains one queue.
        private class FileWrite
        {
            public string Path;
            public string Text;
            public bool Append;
            public string Header;   // csv header: written first on a new file; a file with another header is set aside
        }

        private static readonly System.Collections.Concurrent.ConcurrentQueue<FileWrite> fileQueue =
            new System.Collections.Concurrent.ConcurrentQueue<FileWrite>();
        private static int fileWriterBusy;
        private static string lastFileError = "";

        private static void QueueFile(FileWrite w)
        {
            fileQueue.Enqueue(w);
            if (System.Threading.Interlocked.CompareExchange(ref fileWriterBusy, 1, 0) == 0)
                System.Threading.ThreadPool.QueueUserWorkItem(_ => DrainFiles());
        }

        private static void DrainFiles()
        {
            while (true)
            {
                FileWrite w;
                while (fileQueue.TryDequeue(out w))
                {
                    try
                    {
                        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(w.Path));
                        if (w.Header != null)
                        {
                            if (System.IO.File.Exists(w.Path))
                            {
                                string first;
                                using (System.IO.StreamReader r = new System.IO.StreamReader(w.Path))
                                    first = r.ReadLine();
                                string old = first == null ? null : first.Trim().TrimStart('﻿');
                                if (old != null && old.Length > 0 && old != w.Header && w.Header.StartsWith(old + ","))
                                {
                                    // Columns added at the end: the old rows get empty ones, the journal stays one file
                                    string pad = new string(',', w.Header.Substring(old.Length).Split(',').Length - 1);
                                    string[] lines = System.IO.File.ReadAllLines(w.Path);
                                    lines[0] = w.Header;
                                    for (int i = 1; i < lines.Length; i++)
                                        if (lines[i].Length > 0)
                                            lines[i] += pad;
                                    System.IO.File.WriteAllLines(w.Path + ".tmp", lines);   // swapped in whole: a crash never leaves half a journal
                                    System.IO.File.Replace(w.Path + ".tmp", w.Path, null);
                                }
                                else if (old != null && old != w.Header)
                                    System.IO.File.Move(w.Path, System.IO.Path.ChangeExtension(w.Path, null)
                                        + "." + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".csv");
                            }
                            if (!System.IO.File.Exists(w.Path))
                                System.IO.File.WriteAllText(w.Path, w.Header + Environment.NewLine);
                        }
                        if (w.Append)
                            System.IO.File.AppendAllText(w.Path, w.Text);
                        else
                            System.IO.File.WriteAllText(w.Path, w.Text);
                    }
                    catch (Exception ex) { lastFileError = System.IO.Path.GetFileName(w.Path) + ": " + ex.Message; }   // never stop the strategy
                }
                System.Threading.Interlocked.Exchange(ref fileWriterBusy, 0);
                // A write queued between the last dequeue and the reset is picked up here
                if (fileQueue.IsEmpty || System.Threading.Interlocked.CompareExchange(ref fileWriterBusy, 1, 0) != 0)
                    return;
            }
        }

        private void WriteLogFile(string line)
        {
            QueueFile(new FileWrite
            {
                Path   = System.IO.Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "log", "EmaRejection." + DateTime.Now.ToString("yyyyMMdd") + ".txt"),
                Text   = DateTime.Now.ToString("HH:mm:ss.fff") + " " + Account.Name + " " + line + Environment.NewLine,
                Append = true
            });
        }

        // Files for the agents in Documents\NinjaTrader 8\analysis\ (outside the repo)
        private static string AnalysisPath(string name)
        {
            return System.IO.Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "analysis", name);
        }

        private void WriteAnalysisFile(string name, string text, bool append)
        {
            QueueFile(new FileWrite { Path = AnalysisPath(name), Text = text, Append = append });
        }

        private string SideLine(SideState s)
        {
            string label = s.Dir < 0 ? "SHORT rej " : "LONG bnc  ";
            if (s.Forced)
                return label + "FORCED";
            string list = "";
            foreach (double x in s.Extremes)
                list += (list.Length > 0 ? " > " : "") + Fmt(x);
            if (s.Dir > 0 && list.Length > 0)
                list = list.Replace(" > ", " < ");
            string trend = SetupMode == EmaRejSetupMode.Rejection ? ""
                : s.TrendOn ? "  | TREND " + (s.Dir > 0 ? "HL " : "LH ") + Fmt(s.TrendFloor) + " " + s.TrendEntries + "/"
                              + (TrendMaxEntries > 0 ? TrendMaxEntries.ToString() : "-") + (s.TrendWaitPb ? " (next pullback)" : "")
                : "  | trend off";
            string dlt = delta == null ? ""
                : "  | \u0394 " + (s.DeltaAgree ? "\u2713" : "\u2717") + (s.DeltaDivAgo >= 0 ? " div " + s.DeltaDivAgo + " bars ago" : "");
            return label + s.Extremes.Count + "/" + RequiredRejections + (s.Status.Length > 0 ? " " + s.Status : "") + (list.Length > 0 ? "  " + list : "") + trend + dlt;
        }

        private string BuildDashboardText()
        {
            string t = "";
            double e = ema[0];
            double slope = CurrentBar > SlopeBars ? ema[0] - ema[SlopeBars] : 0;
            t += "EMA" + EmaPeriod + " " + Fmt(e) + "  slope " + (slope > 0 ? "UP" : slope < 0 ? "DOWN" : "flat") + "\n";
            t += SideLine(shortSide) + "\n";
            t += SideLine(longSide) + "\n";
            t += "Swing H " + (hasSwingHigh ? Fmt(lastSwingHigh) : "-") + "  L " + (hasSwingLow ? Fmt(lastSwingLow) : "-") + "\n";

            Position p = Positions[execIdx];
            if (p.MarketPosition != MarketPosition.Flat)
            {
                t += "IN " + p.MarketPosition.ToString().ToUpper() + " " + p.Quantity + " @ " + Fmt(p.AveragePrice)
                    + (profitLocked ? "  LOCKED +" + ProfitLockPts : (UseProfitLock ? "  lock @+" + ProfitLockTriggerPts : "")) + "\n";
                string tradeLegName = p.MarketPosition == MarketPosition.Short ? "LL " : "HH ";
                if (splitActive)
                    t += "  TP1 " + Fmt(tp1Price) + " (" + Tp1R.ToString("0.##") + "R, " + (tp1Sent && tp1Qty > 0 ? tp1Qty : Contracts / 2) + " at market)"
                        + (tp1Note.Length > 0 ? " " + tp1Note : "") + (tp1Done && RunnerToBreakeven ? ", runner at BE" : "") + "\n";
                t += (splitActive ? "  TP2 " : "  TP ") + Fmt(lastSetTarget) + (targetManual ? " (manual)"
                    : double.IsNaN(tradeLeg) ? " (min)" : " (" + tradeLegName + Fmt(tradeLeg) + ")") + "\n";
            }
            else if (planValid)
            {
                double risk = Math.Abs(planEntry - planStop);
                double reward = Math.Abs(planTarget - planEntry);
                double offset = PlanOffset(planDir);
                bool onReclaim = planViaTrend && PullbackEntry == EmaRejPullbackEntry.OnReclaim;
                t += "Plan " + (planDir > 0 ? "BUY" : "SELL") + (onReclaim ? " MKT on the next bounce over EMA " + Fmt(planEma)
                    : " LMT " + Fmt(planEntry) + "  (EMA " + Fmt(planEma) + (planDir > 0 ? " + " : " - ") + offset.ToString("0.##") + ")")
                    + (planViaTrend ? " [trend]" : "") + "\n";
                string legName = planDir < 0 ? "LL " : "HH ";
                t += "  SL " + Fmt(planStop) + "  TP " + Fmt(planTarget)
                    + (planFallback ? " (min " + (StopLossPts * MinTargetR).ToString("0.##") +(double.IsNaN(planLeg) ? ", no leg)" : ", " + legName + Fmt(planLeg) + " too close)") : " (" + legName + Fmt(planLeg) + ")")
                    + "  " + (risk > 0 ? (reward / risk).ToString("0.00") : "-") + "R\n";
                if (SplitTargets && Contracts >= 2)
                    t += "  Split: " + (Contracts / 2) + " at TP1 " + Fmt(Tp1Price(planDir, planEntry)) + " (market), " + (Contracts - Contracts / 2) + " runner\n";
                string waitWhy = dayBlock.Length > 0 ? "DAY LIMIT (" + dayBlock + ")"
                    : accountBlock.Length > 0 ? "BLOCKED (" + accountBlock + ")" : pauseReason.Length > 0 ? "PAUSED (" + pauseReason + ")" : planTooClose ?"waiting: price within " + MinDistFromPricePts.ToString("0.##") + " pts of entry" : "waiting";
                t += "Order: " + (entryOrder != null ? entryOrder.OrderState + " @ " + Fmt(entryOrder.LimitPrice) + (planTooClose ? " (held, price close)" : "")
                    : (armed ? (CanTradeNow() ? waitWhy : "live only") : "AUTO is OFF")) + "\n";
            }
            else
                t += "Plan: none" + (planNote.Length > 0 ? " (" + planNote + ")" : "") + "\n";

            t += "Day " + Usd(dayPnl) + (DailyStopUsd > 0 ? " / -$" + DailyStopUsd.ToString("0") : "")
                + "  Losses " + dayLosses + (MaxLossesPerDay > 0 ? "/" + MaxLossesPerDay : "")
                + "  Trades " + tradesThisSession + (MaxTradesPerSession > 0 ? "/" + MaxTradesPerSession : "")
                + "  on " + Instruments[execIdx].FullName + "\n";
            t += lastEvent;
            return t;
        }

        private void RefreshUi()
        {
            if (chartControlRef == null || !ShowDashboard || ema == null || CurrentBars[0] < 1)
                return;
            string text   = BuildDashboardText();
            string status = StatusText();
            Brush statusBrush = inChop && UseChopFilter ? Brushes.Red : inChop || chopRisk ? Brushes.Yellow : trendDir > 0 ? Brushes.LimeGreen : trendDir < 0 ? Brushes.Orange : Brushes.Gray;
            bool isArmed  = armed;
            string dir    = Direction == EmaRejDirection.Both ? "BOTH" : Direction == EmaRejDirection.LongOnly ? "LONG" : "SHORT";
            Position pos  = Positions[execIdx];
            string posText = pos.MarketPosition == MarketPosition.Flat ? "Flat"
                : (pos.MarketPosition == MarketPosition.Long ? "Long " : "Short ") + pos.Quantity;
            chartControlRef.Dispatcher.InvokeAsync(new Action(() =>
            {
                if (infoText == null)
                    return;
                uiPosText = posText;
                titleText.Text = uiCollapsed ? "EMA REJ \u00B7 " + posText : "EMA REJECTION EXEC";
                infoText.Text = text;
                statusText.Text       = status;
                statusText.Foreground = statusBrush;
                autoButton.Content    = isArmed ? "AUTO: ON" : "AUTO: OFF";
                autoButton.Background = isArmed ? new SolidColorBrush(Color.FromRgb(20, 110, 40)) : new SolidColorBrush(Color.FromRgb(45, 45, 50));
                autoButton.Foreground = isArmed ? Brushes.White : Brushes.Gainsboro;
                dirButton.Content     = dir;
            }));
        }

        private Button MakeButton(string text, double width, Brush accent)
        {
            Button b = new Button
            {
                Content         = text,
                Width           = width,
                Height          = 24,
                FontSize        = 11,
                Foreground      = accent,
                Background      = new SolidColorBrush(Color.FromRgb(45, 45, 50)),
                BorderBrush     = accent,
                BorderThickness = new Thickness(1),
                Margin          = new Thickness(0, 0, 4, 4),
                Cursor          = System.Windows.Input.Cursors.Hand
            };
            return b;
        }

        private void BuildUi()
        {
            if (chartControlRef == null || uiPanel != null)
                return;

            uiPanel = new StackPanel
            {
                Orientation         = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment   = VerticalAlignment.Top,
                Margin              = new Thickness(0, 16, 70, 0)
            };

            Border border = new Border
            {
                Background      = new SolidColorBrush(Color.FromArgb(215, 15, 15, 20)),
                BorderBrush     = Brushes.DimGray,
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(6),
                Padding         = new Thickness(8),
                MinWidth        = 270
            };
            uiBorder = border;
            StackPanel inner = new StackPanel();
            StackPanel header = new StackPanel { Orientation = Orientation.Horizontal };
            collapseButton = MakeButton("\u25BE", 22, Brushes.Gainsboro);
            collapseButton.Height   = 22;
            collapseButton.MinWidth = 0;   // NinjaTrader's button style has a wide minimum
            collapseButton.Padding  = new Thickness(0);
            collapseButton.FontSize = 14;
            collapseButton.Margin = new Thickness(0, 0, 6, 0);
            collapseButton.ToolTip = "Hide / show the dashboard details";
            titleText = new TextBlock { Text = "EMA REJECTION EXEC", FontWeight = FontWeights.Bold, Foreground = Brushes.Gold, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            header.Children.Add(collapseButton);
            header.Children.Add(titleText);
            inner.Children.Add(header);
            infoText = new TextBlock
            {
                Foreground = Brushes.Gainsboro,
                FontFamily = new FontFamily("Consolas"),
                FontSize   = 11,
                Margin     = new Thickness(0, 4, 0, 6),
                Text       = "loading..."
            };
            statusText = new TextBlock
            {
                Foreground = Brushes.Gray,
                FontFamily = new FontFamily("Consolas"),
                FontSize   = 11,
                FontWeight = FontWeights.Bold,
                Margin     = new Thickness(0, 4, 0, 0)
            };
            inner.Children.Add(statusText);
            inner.Children.Add(infoText);

            StackPanel row1 = new StackPanel { Orientation = Orientation.Horizontal };
            autoButton = MakeButton("AUTO: OFF", 90, Brushes.LimeGreen);
            dirButton  = MakeButton("BOTH", 60, Brushes.Gold);
            flatButton = MakeButton("FLAT+OFF", 75, Brushes.Tomato);
            row1.Children.Add(autoButton);
            row1.Children.Add(dirButton);
            row1.Children.Add(flatButton);

            StackPanel row2 = new StackPanel { Orientation = Orientation.Horizontal };
            uiRow2 = row2;
            buyButton  = MakeButton("BUY @EMA", 90, Brushes.DeepSkyBlue);
            sellButton = MakeButton("SELL @EMA", 90, Brushes.OrangeRed);
            row2.Children.Add(buyButton);
            row2.Children.Add(sellButton);

            inner.Children.Add(row1);
            inner.Children.Add(row2);
            border.Child = inner;
            uiPanel.Children.Add(border);

            autoButton.Click += OnAutoClick;
            dirButton.Click  += OnDirClick;
            flatButton.Click += OnFlatClick;
            buyButton.Click  += OnBuyClick;
            sellButton.Click += OnSellClick;
            collapseButton.Click += OnCollapseClick;

            SetCollapsed(DashboardCollapsed);
            UserControlCollection.Add(uiPanel);
        }

        private void RemoveUi()
        {
            if (autoButton != null) autoButton.Click -= OnAutoClick;
            if (dirButton  != null) dirButton.Click  -= OnDirClick;
            if (flatButton != null) flatButton.Click -= OnFlatClick;
            if (buyButton  != null) buyButton.Click  -= OnBuyClick;
            if (sellButton != null) sellButton.Click -= OnSellClick;
            if (collapseButton != null) collapseButton.Click -= OnCollapseClick;
            if (uiPanel != null && UserControlCollection.Contains(uiPanel))
                UserControlCollection.Remove(uiPanel);
            uiPanel = null;
            infoText = null;
        }

        // Collapsed: title (with the position) and the AUTO / direction / FLAT row only. Pure UI, no strategy state.
        private void SetCollapsed(bool collapsed)
        {
            uiCollapsed = collapsed;
            Visibility v = collapsed ? Visibility.Collapsed : Visibility.Visible;
            statusText.Visibility = v;
            infoText.Visibility   = v;
            uiRow2.Visibility     = v;
            uiBorder.MinWidth     = collapsed ? 0 : 270;
            collapseButton.Content = collapsed ? "\u25B8" : "\u25BE";
            titleText.Text        = collapsed ? "EMA REJ \u00B7 " + uiPosText : "EMA REJECTION EXEC";
        }

        private void OnCollapseClick(object sender, RoutedEventArgs e)
        {
            SetCollapsed(!uiCollapsed);
        }

        private void OnAutoClick(object sender, RoutedEventArgs e)
        {
            TriggerCustomEvent(o =>
            {
                // The click means the opposite of what the button showed, even if a fill's pending AUTO OFF lands first
                bool want = !armed;
                ProcessPending();   // a fill's AUTO OFF / day limits first
                armed = want;
                Log0(armed ? "AUTO ON (manual)" : "AUTO OFF (manual)");
                if (armed) ManageEntry(0); else CancelEntry();
                DrawPlan();
                RefreshUi();
            }, 0, null);
        }

        private void OnDirClick(object sender, RoutedEventArgs e)
        {
            TriggerCustomEvent(o =>
            {
                ProcessPending();   // a fill's AUTO OFF / day limits first
                Direction = Direction == EmaRejDirection.Both ? EmaRejDirection.LongOnly
                          : Direction == EmaRejDirection.LongOnly ? EmaRejDirection.ShortOnly : EmaRejDirection.Both;
                ManageEntry(0);
                DrawPlan();
                RefreshUi();
            }, 0, null);
        }

        private void OnFlatClick(object sender, RoutedEventArgs e)
        {
            TriggerCustomEvent(o =>
            {
                ProcessPending();   // a fill's AUTO OFF / day limits first
                armed = false;
                CancelEntry();
                Position p = Positions[execIdx];
                if (p.MarketPosition != MarketPosition.Flat)
                    ExitAll("Flat");
                Log0("FLAT + AUTO OFF (manual)");
                RefreshUi();
            }, 0, null);
        }

        private void OnBuyClick(object sender, RoutedEventArgs e)  { ForceSide(longSide); }
        private void OnSellClick(object sender, RoutedEventArgs e) { ForceSide(shortSide); }

        // Manual override: treat this side as confirmed now and arm AUTO
        private void ForceSide(SideState s)
        {
            TriggerCustomEvent(o =>
            {
                ProcessPending();   // a fill's AUTO OFF / day limits first
                SideState other = s == longSide ? shortSide : longSide;
                other.Forced = false;
                s.Forced     = true;
                s.ForcedBar  = CurrentBar;
                // Leg starts at the highest high (short) / lowest low (long) of the lookback, so the target sees the whole move
                // Always the chart series: a custom event can run with the execution series as context
                int look = Math.Min(Math.Min(SetupLookbackBars, CurrentBars[0]), 250);
                int best = 0;
                for (int i = 1; i <= look; i++)
                    if (s.Dir < 0 ? Highs[0][i] > Highs[0][best] : Lows[0][i] < Lows[0][best])
                        best = i;
                s.ForcedAnchor = CurrentBars[0] - best;
                s.ClosesBeyond = 0;
                armed = true;
                Log0((s.Dir > 0 ? "BUY" : "SELL") + " @EMA forced, AUTO ON");
                ManageEntry(0);
                if (dayBlock.Length > 0)
                    Log0((s.Dir > 0 ? "BUY" : "SELL") + " blocked: " + dayBlock);
                else if (accountBlock.Length > 0)
                    Log0((s.Dir > 0 ? "BUY" : "SELL") + " blocked: " + accountBlock);
                else if (entryOrder == null && planValid && planTooClose)
                    Log0((s.Dir > 0 ? "BUY" : "SELL") + " waiting: needs price " + (s.Dir > 0 ? ">= " : "<= ")
                        + Fmt(planEntry + s.Dir * MinDistFromPricePts) + " (entry " + Fmt(planEntry) + ")");
                DrawPlan();
                RefreshUi();
            }, 0, null);
        }
        #endregion

        #region Properties
        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "EMA period", Order = 1, GroupName = "1. Setup")]
        public int EmaPeriod { get; set; }

        [NinjaScriptProperty]
        [Range(0, double.MaxValue)]
        [Display(Name = "Touch zone (pts)", Description = "A bar whose high (short) / low (long) comes within this distance of the EMA counts as a test.", Order = 2, GroupName = "1. Setup")]
        public double TouchZonePts { get; set; }

        [NinjaScriptProperty]
        [Range(0, double.MaxValue)]
        [Display(Name = "Max wick pierce (pts)", Description = "A wick through the EMA further than this is a break, not a rejection. Cancels the setup.", Order = 3, GroupName = "1. Setup")]
        public double MaxPiercePts { get; set; }

        [NinjaScriptProperty]
        [Range(0, int.MaxValue)]
        [Display(Name = "Close-over tolerance (ticks)", Description = "A bar can close this many ticks on the wrong side of the EMA and still be a test. More = break.", Order = 4, GroupName = "1. Setup")]
        public int CloseOverTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0, int.MaxValue)]
        [Display(Name = "Reject close (ticks)", Description = "A rejection is confirmed when a bar closes at least this many ticks back on the right side of the EMA.", Order = 5, GroupName = "1. Setup")]
        public int RejectCloseTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Reject within (bars)", Description = "Bars after the test starts to get the rejection close.", Order = 6, GroupName = "1. Setup")]
        public int RejectBars { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "Required rejections", Order = 7, GroupName = "1. Setup")]
        public int RequiredRejections { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Require lower highs / higher lows", Order = 8, GroupName = "1. Setup")]
        public bool RequireStructure { get; set; }

        [NinjaScriptProperty]
        [Range(0, double.MaxValue)]
        [Display(Name = "Min LH / HL gap (pts)", Description = "Each rejection high must be at least this much lower than the previous (mirror for longs).", Order = 9, GroupName = "1. Setup")]
        public double MinStructureGapPts { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Setup lookback (bars)", Description = "Rejections older than this drop out of the count.", Order = 10, GroupName = "1. Setup")]
        public int SetupLookbackBars { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Max closes over EMA", Description = "This many consecutive closes on the wrong side (even within tolerance) cancel the setup.", Order = 11, GroupName = "1. Setup")]
        public int MaxClosesBeyond { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use EMA slope filter", Description = "Shorts only when EMA is falling, longs only when rising.", Order = 12, GroupName = "1. Setup")]
        public bool UseSlopeFilter { get; set; }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name = "Slope bars", Order = 13, GroupName = "1. Setup")]
        public int SlopeBars { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Swing strength (bars each side)", Description = "Swing high / low used for the target.", Order = 14, GroupName = "1. Setup")]
        public int SwingStrength { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Setup mode", Description = "Rejection: N confirmed rejections / bounces (the original rule). Trend pullback: after price closes back over the EMA (and VWAP), each pullback into the EMA holding a higher low (short: lower high) is an entry, from the first bounce. Either: both.", Order = 15, GroupName = "1. Setup")]
        public EmaRejSetupMode SetupMode { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Trend: needs VWAP", Description = "The reclaim must close over VWAP as well as the EMA, and a close more than 'Trend: max close through' through VWAP ends the trend.", Order = 16, GroupName = "1. Setup")]
        public bool TrendNeedsVwap { get; set; }

        [NinjaScriptProperty]
        [Range(0, double.MaxValue)]
        [Display(Name = "Trend: max close through EMA (pts)", Description = "A pullback may close through the EMA by up to this much (break and reclaim). Further ends the trend.", Order = 17, GroupName = "1. Setup")]
        public double TrendBreakPts { get; set; }

        [NinjaScriptProperty]
        [Range(0, int.MaxValue)]
        [Display(Name = "Trend: closes through EMA to end it", Description = "This many closes in a row through the EMA end the trend. 0 = off.", Order = 18, GroupName = "1. Setup")]
        public int TrendBreakBars { get; set; }

        [NinjaScriptProperty]
        [Range(0, int.MaxValue)]
        [Display(Name = "Trend: max entries per leg", Description = "Pullback entries per trend leg (0 = no limit). A stop-out also ends the leg.", Order = 19, GroupName = "1. Setup")]
        public int TrendMaxEntries { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Trend: closes to confirm the reclaim", Description = "Closes in a row over the EMA (and VWAP) before a trend starts, outside chop. 1 = the old behaviour (one close).", Order = 20, GroupName = "1. Setup")]
        public int TrendHoldBars { get; set; }

        [NinjaScriptProperty]
        [Range(-50, 50)]
        [Display(Name = "Long offset (pts)", Description = "Buy limit = EMA + this. Positive fills just above the EMA, negative needs a poke through.", Order = 1, GroupName = "2. Entry")]
        public double LongOffsetPts { get; set; }

        [NinjaScriptProperty]
        [Range(-50, 50)]
        [Display(Name = "Short offset (pts)", Description = "Sell limit = EMA - this. Positive fills just below the EMA, negative needs a poke through.", Order = 2, GroupName = "2. Entry")]
        public double ShortOffsetPts { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Limit follows EMA", Description = "Move the working limit to the EMA every bar.", Order = 3, GroupName = "2. Entry")]
        public bool TrackEma { get; set; }

        [NinjaScriptProperty]
        [Range(0, int.MaxValue)]
        [Display(Name = "Limit expiry (bars, 0 = never)", Order = 4, GroupName = "2. Entry")]
        public int OrderExpiryBars { get; set; }

        [NinjaScriptProperty]
        [Range(0, double.MaxValue)]
        [Display(Name = "Min distance from price (pts)", Description = "The limit is only placed / moved when it rests at least this far from the market (bid/ask of the execution instrument). Stops it landing right on price.", Order = 6, GroupName = "2. Entry")]
        public double MinDistFromPricePts { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Trend pullback entry", Description = "On reclaim: at market on the close of the bar that bounces back over the EMA. Limit at EMA: a limit resting at EMA +/- Pullback offset while price is away.", Order = 7, GroupName = "2. Entry")]
        public EmaRejPullbackEntry PullbackEntry { get; set; }

        [NinjaScriptProperty]
        [Range(-50, 50)]
        [Display(Name = "Pullback offset (pts)", Description = "Trend pullback 'Limit at EMA': buy limit = EMA + this, sell limit = EMA - this.", Order = 8, GroupName = "2. Entry")]
        public double PullbackOffsetPts { get; set; }

        [NinjaScriptProperty]
        [Range(0, double.MaxValue)]
        [Display(Name = "Reclaim max chase (pts)", Description = "Trend pullback 'On reclaim': no entry when the bounce bar closes further than this from the EMA.", Order = 9, GroupName = "2. Entry")]
        public double ReclaimMaxChasePts { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Direction", Order = 5, GroupName = "2. Entry")]
        public EmaRejDirection Direction { get; set; }

        [NinjaScriptProperty]
        [Range(0.25, double.MaxValue)]
        [Display(Name = "Stop loss (pts)", Order = 1, GroupName = "3. Exits")]
        public double StopLossPts { get; set; }

        [NinjaScriptProperty]
        [Range(-50, 50)]
        [Display(Name = "Target beyond lower low (pts)", Description = "Short TP = the setup's lower low - this. Long TP = its higher high + this. Negative front-runs it.", Order = 2, GroupName = "3. Exits")]
        public double TargetBeyondSwingPts { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, double.MaxValue)]
        [Display(Name = "Minimum target (x stop)", Description = "Floor only: the TP is never closer than this many times the stop (1 = 1:1, 25 pts with a 25-pt stop). The lower low / higher high is used whenever it is further.", Order = 3, GroupName = "3. Exits")]
        public double MinTargetR { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use profit lock", Description = "Off by default. When on, the stop moves to entry +/- Profit lock once price reaches the trigger.", Order = 6, GroupName = "3. Exits")]
        public bool UseProfitLock { get; set; }

        [NinjaScriptProperty]
        [Range(0.25, double.MaxValue)]
        [Display(Name = "Profit lock trigger (pts)", Order = 7, GroupName = "3. Exits")]
        public double ProfitLockTriggerPts { get; set; }

        [NinjaScriptProperty]
        [Range(0, double.MaxValue)]
        [Display(Name = "Profit lock (pts)", Description = "Stop moves to entry +/- this once the trigger is reached.", Order = 8, GroupName = "3. Exits")]
        public double ProfitLockPts { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Split targets (2+ contracts)", Description = "One entry. When price reaches TP1 (gold dashed line), the strategy sends a market exit for half the contracts; the rest (the runner, which gets the extra contract on odd counts) keeps the leg target and the stop. TP1 is set here, not dragged, and needs NinjaTrader running. Skipped for a trade whose target is within 'TP1 min gap to target' of TP1. No effect with 1 contract.", Order = 9, GroupName = "3. Exits")]
        public bool SplitTargets { get; set; }

        [NinjaScriptProperty]
        [Range(0.25, 10)]
        [Display(Name = "TP1 at (x stop)", Description = "TP1 distance from the fill as a multiple of the stop (1 = 1:1). Below 1 needs a high hit rate to beat costs.", Order = 10, GroupName = "3. Exits")]
        public double Tp1R { get; set; }

        [NinjaScriptProperty]
        [Range(0, double.MaxValue)]
        [Display(Name = "TP1 min gap to target (pts)", Description = "TP1 is skipped for a trade whose target is closer than this to TP1: the target takes the whole position. A TP1 market exit racing a nearby target could overfill on a fast move.", Order = 12, GroupName = "3. Exits")]
        public double Tp1MinGapPts { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Runner stop to breakeven after TP1", Description = "Once the TP1 exit fills, the runner's stop moves to the average entry (never loosened).", Order = 11, GroupName = "3. Exits")]
        public bool RunnerToBreakeven { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Trace orders (Output window)", Description = "NinjaTrader prints every order the strategy sends, changes or has ignored to the NinjaScript Output window. For testing.", Order = 8, GroupName = "4. Automation")]
        public bool TraceOrdersToOutput { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Start with AUTO on", Order = 1, GroupName = "4. Automation")]
        public bool StartArmed { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "AUTO off after stop loss", Order = 2, GroupName = "4. Automation")]
        public bool DisarmOnStopLoss { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "AUTO off after profit-lock stop", Order = 3, GroupName = "4. Automation")]
        public bool DisarmOnProfitLockStop { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Contracts", Order = 4, GroupName = "4. Automation")]
        public int Contracts { get; set; }

        [NinjaScriptProperty]
        [Range(0, int.MaxValue)]
        [Display(Name = "Max trades per session (0 = no limit)", Order = 5, GroupName = "4. Automation")]
        public int MaxTradesPerSession { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Execution instrument", Description = "Overrides everything when filled, e.g. 'MNQ 12-26'. Blank = use 'Execute on micro' below.", Order = 6, GroupName = "4. Automation")]
        public string ExecutionInstrument { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Execute on micro", Description = "With Execution instrument blank: an NQ chart sends orders to MNQ of the same expiry (ES->MES, YM->MYM, RTY->M2K, GC->MGC, CL->MCL, SI->SIL). Off = trade the chart instrument.", Order = 7, GroupName = "4. Automation")]
        public bool ExecuteOnMicro { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use time filter", Order = 1, GroupName = "5. Session")]
        public bool UseTimeFilter { get; set; }

        [NinjaScriptProperty]
        [Range(0, 235959)]
        [Display(Name = "Start time (HHmmss)", Order = 2, GroupName = "5. Session")]
        public int StartTime { get; set; }

        [NinjaScriptProperty]
        [Range(0, 235959)]
        [Display(Name = "End time (HHmmss)", Order = 3, GroupName = "5. Session")]
        public int EndTime { get; set; }

        [Display(Name = "Show EMA", Order = 1, GroupName = "6. Display")]
        public bool ShowEma { get; set; }

        [Display(Name = "Show rejection markers", Order = 2, GroupName = "6. Display")]
        public bool ShowMarkers { get; set; }

        [Display(Name = "Show plan lines", Order = 3, GroupName = "6. Display")]
        public bool ShowPlanLines { get; set; }

        [Display(Name = "Show dashboard", Order = 4, GroupName = "6. Display")]
        public bool ShowDashboard { get; set; }

        [Display(Name = "Start dashboard collapsed", Description = "Start with only the title, position and the AUTO / direction / FLAT buttons. The arrow button toggles it on the chart.", Order = 5, GroupName = "6. Display")]
        public bool DashboardCollapsed { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Pause entries in chop", Description = "Chop = in the lookback, closes crossed the Globex VWAP at least 'Min crosses' times (EMA crosses don't count: they are the setup) and efficiency (net move / distance travelled) is at or below 'Chop max efficiency'. No new limits, waiting limit cancelled.", Order = 1, GroupName = "7. Chop / trend")]
        public bool UseChopFilter { get; set; }

        [NinjaScriptProperty]
        [Range(5, 250)]
        [Display(Name = "Chop lookback (bars)", Order = 2, GroupName = "7. Chop / trend")]
        public int ChopLookbackBars { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Chop min crosses (VWAP)", Order = 3, GroupName = "7. Chop / trend")]
        public int ChopMinCrosses { get; set; }

        [NinjaScriptProperty]
        [Range(0, 1)]
        [Display(Name = "Chop max efficiency", Description = "Net move / bar-by-bar distance over the lookback. Trend ~0.5+, chop ~0.2 or less. Crosses hit + efficiency at or below this = CHOP; crosses hit alone = Chop risk (warning only).", Order = 4, GroupName = "7. Chop / trend")]
        public double ChopMaxEfficiency { get; set; }

        [NinjaScriptProperty]
        [Range(0, int.MaxValue)]
        [Display(Name = "Chop needs a VWAP cross within (bars)", Description = "Chop only while price is still rotating around VWAP: the newest cross must be within this many bars. Once price holds one side of VWAP this long, older crosses no longer count. 0 = off.", Order = 6, GroupName = "7. Chop / trend")]
        public int ChopRecentBars { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Trade with trend only", Description = "Shorts only when close is below EMA and VWAP (BEARISH), longs only above both (BULLISH). BUY/SELL @EMA buttons bypass this.", Order = 5, GroupName = "7. Chop / trend")]
        public bool TradeWithTrend { get; set; }

        [NinjaScriptProperty]
        [Range(0, double.MaxValue)]
        [Display(Name = "Daily stop ($, 0 = off)", Description = "No new entry if a full stop on the next trade (plus costs) would take the day's closed P&L below -this. Set just inside the account's own daily limit (e.g. TradeSpacePro). Resets at the chart's session start, so use an ETH (18:00 ET) trading-hours template; with Max trades per session, these are the day limits.", Order = 1, GroupName = "8. Day limits")]
        public double DailyStopUsd { get; set; }

        [NinjaScriptProperty]
        [Range(0, int.MaxValue)]
        [Display(Name = "Max losing trades per day (0 = off)", Description = "A trade counts as a loss when its net P&L (after commission) is below 0, so a breakeven scratch counts.", Order = 2, GroupName = "8. Day limits")]
        public int MaxLossesPerDay { get; set; }

        [NinjaScriptProperty]
        [Range(0, double.MaxValue)]
        [Display(Name = "Cost per contract ($, round trip)", Description = "Commission plus expected slippage, added to the stop when checking the daily stop. Also used as the trade's commission in P&L and trades.csv when the connection reports none (cost_estimated = 1).", Order = 3, GroupName = "8. Day limits")]
        public double CostPerContractUsd { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Delta confirmation", Description = "Price and delta doing the same thing (long: price higher low + close over the EMA, delta higher low + delta over its fast EMA; short mirrors), from DeltaConfluence. Log only: trades.csv and the dashboard record it, trading unchanged. Required: setups enter at market on the bar where agreement starts (within 'Reclaim max chase' of the EMA). Off: no delta (no 1-tick series loaded).", Order = 1, GroupName = "9. Delta")]
        public EmaRejDeltaMode DeltaMode { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Divergence lookback (bars)", Description = "A delta divergence this many bars back or newer is shown on the dashboard and logged (delta_div_ago). Not required.", Order = 2, GroupName = "9. Delta")]
        public int DeltaDivBars { get; set; }
        #endregion
    }
}
