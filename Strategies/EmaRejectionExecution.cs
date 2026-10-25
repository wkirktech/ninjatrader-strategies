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

    // EMA rejection / bounce execution.
    // Short: price tests EMA from below (wick can pierce, close can be a tick or two over), then closes back under.
    //        N confirmed rejections making lower highs = setup. A sell limit rides the EMA (minus offset) for the next test.
    // Long:  mirror (bounces off EMA from above making higher lows, buy limit rides EMA plus offset).
    // Stop = fixed points, target = beyond the most recent swing low (short) / swing high (long).
    // AUTO button arms it; it stays armed and takes every new setup until a full stop-loss hit or you click it off.
    public class EmaRejectionExecution : Strategy
    {
        private const string SigLong  = "EMAR_L";
        private const string SigShort = "EMAR_S";

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

            public int LastBar { get { return Forced ? ForcedBar : (ExtremeBars.Count > 0 ? ExtremeBars[ExtremeBars.Count - 1] : -1); } }

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
        private int chopEmaX, chopVwapX, trendDir;
        private double chopEfficiency = 1;
        private string pauseReason = "";

        private bool planValid;
        private int planDir;
        private double planEntry, planStop, planTarget;
        private bool planFallback;
        private bool planTooClose;
        private double planLeg = double.NaN;
        private int planAnchor = -1;

        // Open trade target tracking
        private int tradeAnchor = -1;
        private double tradeLeg = double.NaN;
        private double lastSetTarget;
        private bool targetManual;
        // Drag rules: stop may only be tightened (a looser stop snaps back); TP can be dragged anywhere
        private Order stopOrder;
        private double lastSetStop, tradeEntry;
        private bool stopKnown, syncStop, stopFallback;
        private DateTime lastDragTime = DateTime.MinValue;
        private DateTime ownTargetTime = DateTime.MinValue;   // our own TP change in flight: its echoes aren't manual drags
        private double preExtendTarget;
        private double planEma;
        private string planNote = "";
        private string lastEvent = "";

        // UI
        private ChartControl chartControlRef;
        private StackPanel uiPanel;
        private TextBlock infoText, statusText;
        private Button autoButton, dirButton, flatButton, buyButton, sellButton;

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

                LongOffsetPts           = 5;
                ShortOffsetPts          = 5;
                TrackEma                = true;
                OrderExpiryBars         = 0;
                MinDistFromPricePts     = 3;
                Direction               = EmaRejDirection.Both;

                StopLossPts             = 25;
                TargetBeyondSwingPts    = 2;
                MinTargetR              = 1;
                UseProfitLock           = true;
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

                UseChopFilter           = true;
                ChopLookbackBars        = 120;
                ChopMinCrosses          = 4;
                ChopMaxEfficiency       = 0.25;
                TradeWithTrend          = true;

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
            }
            else if (State == State.DataLoaded)
            {
                ema = EMA(Closes[0], EmaPeriod);
                if (ShowEma)
                    AddChartIndicator(ema);

                longSide  = new SideState { Dir = 1 };
                shortSide = new SideState { Dir = -1 };
                execTick  = Instruments[execIdx].MasterInstrument.TickSize;
                easternTz = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

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
            else if (State == State.Terminated)
            {
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
            if (CurrentBars[0] < Math.Max(EmaPeriod, SlopeBars + 2 * SwingStrength + 2) + 2)
                return;
            if (execIdx > 0 && CurrentBars[execIdx] < 1)
                return;

            if (BarsInProgress == execIdx)
            {
                EnforceExitDrags();
                ManageProfitLock();
            }

            if (BarsInProgress != 0 || !IsFirstTickOfBar)
                return;

            // Historical (no tick replay) calls once per closed bar -> bar 0 is closed.
            // Realtime first tick of a new bar -> bar 1 is the one that just closed.
            int c = (State == State.Historical && !Bars.IsTickReplay) ? 0 : 1;
            int cb = CurrentBar - c;
            if (cb <= lastProcessedBar)
                return;
            lastProcessedBar = cb;

            if (Bars.IsFirstBarOfSession)
                tradesThisSession = 0;

            UpdateSwings(c);
            UpdateVwapAndChop(c);
            UpdateSide(shortSide, c);
            UpdateSide(longSide, c);
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
                vwapSession  = session;
                vwapStartBar = CurrentBar - c;
                vwapPV  = 0;
                vwapVol = 0;
            }
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

            // Crosses: closes flipping sides of the EMA / VWAP (both counted together).
            // Efficiency: net move / bar-by-bar distance travelled. Trend ~0.5+, chop goes nowhere (~0.2 or less).
            chopEmaX  = 0;
            chopVwapX = 0;
            double path = 0;
            int look = Math.Min(ChopLookbackBars, CurrentBar - c - 1);
            for (int i = c; i < c + look; i++)
            {
                if ((Close[i] > ema[i]) != (Close[i + 1] > ema[i + 1]))
                    chopEmaX++;
                // VWAP crosses only within this session, after the warm-up (no cross across the reset)
                if (CurrentBar - (i + 1) >= vwapStartBar + VwapWarmupBars && Values[0].IsValidDataPoint(i + 1) && (Close[i] > Values[0][i]) != (Close[i + 1] > Values[0][i + 1]))
                    chopVwapX++;
                path += Math.Abs(Close[i] - Close[i + 1]);
            }
            chopEfficiency = path > 0 ? Math.Abs(Close[c] - Close[c + look]) / path : 1;

            // Crosses alone = early "risk" heads-up; crosses + low efficiency = CHOP (pauses entries)
            bool crossesHit = chopEmaX + chopVwapX >= ChopMinCrosses;
            bool wasChop = inChop, wasRisk = chopRisk;
            inChop   = crossesHit && chopEfficiency <= ChopMaxEfficiency;
            chopRisk = crossesHit && !inChop;
            if (inChop != wasChop || chopRisk != wasRisk)
                Log0(inChop ? "CHOP detected" + (UseChopFilter ? ", entries paused" : "") : chopRisk ? "Chop risk" : "Chop cleared");
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

        private void UpdateSide(SideState s, int c)
        {
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

            int n = s.Extremes.Count;
            s.Status = s.Forced ? "FORCED" : (n >= RequiredRejections ? "CONFIRMED" : (s.TestActive ? "testing" : ""));
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
            bool had = s.Extremes.Count >= RequiredRejections || s.Forced;
            s.Reset(false);
            if (entryOrder != null && entryDir == s.Dir)
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
                    if (s.Extremes.Count < RequiredRejections) continue;
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
            if (TradeWithTrend && trendDir != s.Dir)
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
            double entry = Rnd(s.Dir > 0 ? e + LongOffsetPts : e - ShortOffsetPts);
            if (!TrackEma && entryOrder != null && entryDir == s.Dir)
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

        // Where the target leg starts: the setup's first rejection; a forced setup uses the top (short) / bottom (long)
        // of the move, frozen when the button was clicked.
        private int LegAnchor(SideState s)
        {
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
                ownTargetTime   = DateTime.Now;
                preExtendTarget = lastSetTarget;
                SetTarget(activeSignal, target);
                Log0("TP extended -> " + Fmt(target));
            }
        }

        private void SetTarget(string sig, double price)
        {
            lastSetTarget = price;
            SetProfitTarget(sig, CalculationMode.Price, price);
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

            bool allowed = planValid && pauseReason.Length == 0 && armed && CanTradeNow() && InSession(c)
                && (MaxTradesPerSession <= 0 || tradesThisSession < MaxTradesPerSession);
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
                    SubmitEntry(s, cb);
                return;
            }

            if (OrderExpiryBars > 0 && cb - entrySubmitBar >= OrderExpiryBars)
            {
                Invalidate(s, "limit expired");
                return;
            }

            if (TrackEma && farEnough
                && (entryOrder.OrderState == OrderState.Working || entryOrder.OrderState == OrderState.Accepted)
                && Math.Abs(entryOrder.LimitPrice - planEntry) > execTick * 0.5)
                ChangeOrder(entryOrder, entryOrder.Quantity, planEntry, 0);

            SetTarget(entrySignal, planTarget);
        }

        private void SubmitEntry(SideState s, int cb)
        {
            string sig = s.Dir > 0 ? SigLong : SigShort;
            SetStopLoss(sig, CalculationMode.Ticks, Math.Max(1, (int)Math.Round(StopLossPts / execTick)), false);
            SetTarget(sig, planTarget);

            Order o = s.Dir > 0
                ? EnterLongLimit(execIdx, true, Contracts, planEntry, sig)
                : EnterShortLimit(execIdx, true, Contracts, planEntry, sig);
            if (o == null)
                return;

            entryOrder     = o;
            entryDir       = s.Dir;
            entrySignal    = sig;
            entrySubmitBar = cb;
            Log0((s.Dir > 0 ? "BUY" : "SELL") + " LMT " + Fmt(planEntry) + " sent");
        }

        private void CancelEntry()
        {
            if (entryOrder != null && !Order.IsTerminalState(entryOrder.OrderState)
                && entryOrder.OrderState != OrderState.CancelPending && entryOrder.OrderState != OrderState.CancelSubmitted)
                CancelOrder(entryOrder);
        }

        // Put back a stop dragged further away (flagged in OnOrderUpdate). Waits until the drag has settled for a
        // second and the order has no change in flight, so it never fights the user.
        private void EnforceExitDrags()
        {
            if (!syncStop || (DateTime.Now - lastDragTime).TotalSeconds < 1 || !Settled(stopOrder))
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
                if (isLong) ExitLong(execIdx, p.Quantity, "Stop put-back", activeSignal);
                else        ExitShort(execIdx, p.Quantity, "Stop put-back", activeSignal);
                return;
            }

            if (!stopFallback)
            {
                SetStopLoss(activeSignal, CalculationMode.Price, lastSetStop, false);
                stopFallback = true;   // check again on a later tick, once this change has had time to land
                return;
            }
            // Engine already held this price and left the order alone: put a looser stop back directly
            if (Math.Abs(stopOrder.StopPrice - lastSetStop) > execTick * 0.5)
                ChangeOrder(stopOrder, stopOrder.Quantity, 0, lastSetStop);
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
            SetStopLoss(activeSignal, CalculationMode.Price, lockPx, false);
            Log0("Profit lock: stop -> " + Fmt(lockPx));
            RefreshUi();
        }

        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice, int quantity, int filled,
            double averageFillPrice, OrderState orderState, DateTime time, ErrorCode error, string nativeError)
        {
            // Exits dragged on the chart: a tighter stop is kept, a looser one is put back; the TP is kept wherever it is dropped
            if (tradeOpen && (orderState == OrderState.Working || orderState == OrderState.Accepted))
            {
                int dir = activeSignal == SigLong ? 1 : -1;
                if (order.Name == "Stop loss")
                {
                    stopOrder = order;
                    double d = dir * (stopPrice - lastSetStop);
                    if (!stopKnown || d > execTick * 0.5)
                    {
                        lastSetStop = stopPrice;
                        if (stopKnown)
                        {
                            // Tell the managed engine right away, or it puts the stop back on the next tick
                            SetStopLoss(activeSignal, CalculationMode.Price, stopPrice, false);
                            Log0("SL tightened manually -> " + Fmt(stopPrice));
                        }
                        stopKnown = true;
                    }
                    else if (d < -execTick * 0.5)
                    {
                        Log0("SL can only be tightened, back to " + Fmt(lastSetStop));
                        lastDragTime = DateTime.Now;
                        syncStop     = true;
                        stopFallback = false;
                    }
                }
                else if (order.Name == "Profit target")
                {
                    bool matchesOurs = Math.Abs(limitPrice - lastSetTarget) <= execTick * 0.5;
                    if (matchesOurs)
                        ownTargetTime = DateTime.MinValue;   // our auto-extend has landed
                    // Within 3 s of our own auto-extend, an update still at the old target is a stale echo, not a drag
                    else if ((DateTime.Now - ownTargetTime).TotalSeconds >= 3 || Math.Abs(limitPrice - preExtendTarget) > execTick * 0.5)
                    {
                        targetManual = true;
                        // Tell the managed engine right away, or it puts the TP back on the next tick
                        SetTarget(activeSignal, limitPrice);
                        Log0("TP moved manually -> " + Fmt(limitPrice));
                    }
                }
            }

            if (order.Name != SigLong && order.Name != SigShort)
                return;
            if (Order.IsTerminalState(orderState))
            {
                if (entryOrder == order)
                    entryOrder = null;
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

            if ((o.Name == SigLong || o.Name == SigShort) && !tradeOpen)
            {
                tradeOpen    = true;
                profitLocked = false;
                activeSignal = o.Name;
                tradeAnchor  = planAnchor;
                tradeLeg     = planLeg;
                targetManual = false;
                tradeEntry   = price;
                // Stop was sent as StopLossPts from the fill: know it now, so even the first drag is checked
                lastSetStop  = Rnd(price - (o.Name == SigLong ? 1 : -1) * StopLossPts);
                stopKnown     = true;
                syncStop      = false;
                stopFallback  = false;
                stopOrder     = null;
                ownTargetTime = DateTime.MinValue;
                tradesThisSession++;
                // Setup is used up: a new one has to form after this trade
                SideState s = o.Name == SigLong ? longSide : shortSide;
                s.Reset(false);
                Log0((o.Name == SigLong ? "LONG" : "SHORT") + " filled @ " + Fmt(price));
            }
            else if ((o.Name == "Stop loss" || o.Name == "Stop put-back") && o.OrderState == OrderState.Filled)
            {
                // A stop at or past entry (profit lock, or tightened by hand) is not a full stop-loss
                int dir = activeSignal == SigLong ? 1 : -1;
                if (profitLocked || (stopKnown && dir * (lastSetStop - tradeEntry) >= 0))
                {
                    Log0("Profit-lock stop hit, setup ended");
                    if (DisarmOnProfitLockStop) Disarm("profit-lock stop");
                }
                else
                {
                    Log0("STOPPED OUT");
                    if (DisarmOnStopLoss) Disarm("stop loss");
                }
            }
            else if (o.Name == "Profit target" && o.OrderState == OrderState.Filled)
                Log0("TARGET hit @ " + Fmt(price));

            RefreshUi();
        }

        protected override void OnPositionUpdate(Position position, double averagePrice, int quantity, MarketPosition marketPosition)
        {
            if (position.Instrument == Instruments[execIdx] && marketPosition == MarketPosition.Flat)
                tradeOpen = false;
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
        private static readonly object logFileLock = new object();

        private void WriteLogFile(string line)
        {
            try
            {
                string path = System.IO.Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "log",
                    "EmaRejection." + DateTime.Now.ToString("yyyyMMdd") + ".txt");
                lock (logFileLock)
                    System.IO.File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss.fff") + " " + Account.Name + " " + line + Environment.NewLine);
            }
            catch (Exception) { }   // logging must never stop the strategy
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
            return label + s.Extremes.Count + "/" + RequiredRejections + (s.Status.Length > 0 ? " " + s.Status : "") + (list.Length > 0 ? "  " + list : "");
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
                t += "  TP " + Fmt(lastSetTarget) + (targetManual ? " (manual)"
                    : double.IsNaN(tradeLeg) ? " (min)" : " (" + tradeLegName + Fmt(tradeLeg) + ")") + "\n";
            }
            else if (planValid)
            {
                double risk = Math.Abs(planEntry - planStop);
                double reward = Math.Abs(planTarget - planEntry);
                double offset = planDir > 0 ? LongOffsetPts : ShortOffsetPts;
                t += "Plan " + (planDir > 0 ? "BUY" : "SELL") + " LMT " + Fmt(planEntry)
                    + "  (EMA " + Fmt(planEma) + (planDir > 0 ? " + " : " - ") + offset.ToString("0.##") + ")\n";
                string legName = planDir < 0 ? "LL " : "HH ";
                t += "  SL " + Fmt(planStop) + "  TP " + Fmt(planTarget)
                    + (planFallback ? " (min " + (StopLossPts * MinTargetR).ToString("0.##") +(double.IsNaN(planLeg) ? ", no leg)" : ", " + legName + Fmt(planLeg) + " too close)") : " (" + legName + Fmt(planLeg) + ")")
                    + "  " + (risk > 0 ? (reward / risk).ToString("0.00") : "-") + "R\n";
                string waitWhy = pauseReason.Length > 0 ? "PAUSED (" + pauseReason + ")" : planTooClose ?"waiting: price within " + MinDistFromPricePts.ToString("0.##") + " pts of entry" : "waiting";
                t += "Order: " + (entryOrder != null ? entryOrder.OrderState + " @ " + Fmt(entryOrder.LimitPrice) + (planTooClose ? " (held, price close)" : "")
                    : (armed ? (CanTradeNow() ? waitWhy : "live only") : "AUTO is OFF")) + "\n";
            }
            else
                t += "Plan: none" + (planNote.Length > 0 ? " (" + planNote + ")" : "") + "\n";

            t += "Trades " + tradesThisSession + (MaxTradesPerSession > 0 ? "/" + MaxTradesPerSession : "")
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
            chartControlRef.Dispatcher.InvokeAsync(new Action(() =>
            {
                if (infoText == null)
                    return;
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
            StackPanel inner = new StackPanel();
            inner.Children.Add(new TextBlock { Text = "EMA REJECTION EXEC", FontWeight = FontWeights.Bold, Foreground = Brushes.Gold, FontSize = 12 });
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

            UserControlCollection.Add(uiPanel);
        }

        private void RemoveUi()
        {
            if (autoButton != null) autoButton.Click -= OnAutoClick;
            if (dirButton  != null) dirButton.Click  -= OnDirClick;
            if (flatButton != null) flatButton.Click -= OnFlatClick;
            if (buyButton  != null) buyButton.Click  -= OnBuyClick;
            if (sellButton != null) sellButton.Click -= OnSellClick;
            if (uiPanel != null && UserControlCollection.Contains(uiPanel))
                UserControlCollection.Remove(uiPanel);
            uiPanel = null;
            infoText = null;
        }

        private void OnAutoClick(object sender, RoutedEventArgs e)
        {
            TriggerCustomEvent(o =>
            {
                armed = !armed;
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
                armed = false;
                CancelEntry();
                Position p = Positions[execIdx];
                if (p.MarketPosition == MarketPosition.Long)
                    ExitLong(execIdx, p.Quantity, "Flat", activeSignal);
                else if (p.MarketPosition == MarketPosition.Short)
                    ExitShort(execIdx, p.Quantity, "Flat", activeSignal);
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
                if (entryOrder == null && planValid && planTooClose)
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
        [Display(Name = "Use profit lock", Order = 6, GroupName = "3. Exits")]
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

        [NinjaScriptProperty]
        [Display(Name = "Pause entries in chop", Description = "Chop = in the lookback, closes crossed the EMA and Globex VWAP at least 'Min crosses' times (both counted together) and efficiency (net move / distance travelled) is at or below 'Chop max efficiency'. No new limits, waiting limit cancelled.", Order = 1, GroupName = "7. Chop / trend")]
        public bool UseChopFilter { get; set; }

        [NinjaScriptProperty]
        [Range(5, 250)]
        [Display(Name = "Chop lookback (bars)", Order = 2, GroupName = "7. Chop / trend")]
        public int ChopLookbackBars { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Chop min crosses (EMA + VWAP)", Order = 3, GroupName = "7. Chop / trend")]
        public int ChopMinCrosses { get; set; }

        [NinjaScriptProperty]
        [Range(0, 1)]
        [Display(Name = "Chop max efficiency", Description = "Net move / bar-by-bar distance over the lookback. Trend ~0.5+, chop ~0.2 or less. Crosses hit + efficiency at or below this = CHOP; crosses hit alone = Chop risk (warning only).", Order = 4, GroupName = "7. Chop / trend")]
        public double ChopMaxEfficiency { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Trade with trend only", Description = "Shorts only when close is below EMA and VWAP (BEARISH), longs only above both (BULLISH). BUY/SELL @EMA buttons bypass this.", Order = 5, GroupName = "7. Chop / trend")]
        public bool TradeWithTrend { get; set; }
        #endregion
    }
}
