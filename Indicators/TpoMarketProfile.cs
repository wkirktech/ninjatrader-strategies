//
// TpoMarketProfile.cs
// TPO (Time Price Opportunity) market profile for NinjaTrader 8.
//
// Install: copy this file to  Documents\NinjaTrader 8\bin\Custom\Indicators\
// then open the NinjaScript Editor and press F5 to compile.
//
// History is read from a hidden 1-minute data series, so the block size (5m / 30m / 1h / 4h) is
// independent of the chart's own bar type. In real time the current minute is updated on every tick,
// so the developing profile follows the market tick by tick.
//
// Written against the C# 5 language level so it compiles on every NinjaTrader 8 release.
//
#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.SuperDom;
using NinjaTrader.Gui.Tools;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.Core.FloatingPoint;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

namespace TpoMarketProfileTypes
{
	public enum TpoSessionType
	{
		Daily,
		Weekly,
		Monthly
	}

	public enum TpoBlockSize
	{
		Minutes5,
		Minutes30,
		Hour1,
		Hours4
	}

	public enum TpoSessionHours
	{
		RTH,
		ETH
	}

	public enum TpoBlockDisplay
	{
		BlocksWithLetters,
		BlocksOnly,
		LettersOnly,
		Histogram
	}

	public enum TpoBlockColorMode
	{
		Period,
		Heat,
		Session,
		Solid
	}

	public enum TpoPalette
	{
		BySession,
		DodgerBlue,
		Classic,
		BlueRed,
		Ocean,
		Sunset,
		Forest,
		Mono
	}

	public enum TpoLineStyle
	{
		Solid,
		Dashed,
		Dotted
	}

	public enum TpoVolumePosition
	{
		Left,
		Right,
		OverTpo
	}
}

namespace NinjaTrader.NinjaScript.Indicators
{
	public class TpoMarketProfile : Indicator
	{
		#region Internal types

		private struct MinuteRec
		{
			public DateTime Time;
			public double Low;
			public double High;
			public double Volume;
		}

		// Price range of one minute bar: all that is needed to tell whether a level has been traded again.
		private struct RangeRec
		{
			public DateTime Time;
			public double Low;
			public double High;
		}

		// One time slice (block) of a session: the price range traded during that slice.
		private sealed class TpoBlock
		{
			public int DayKey;
			public int Slot;
			public double Low;
			public double High;
			public DateTime Start;
			public DateTime LastClose;
		}

		// A time-of-day window in New York time. DayOffset moves an evening start onto the trading day it belongs to.
		private sealed class SessionWindow
		{
			public TimeSpan Start;
			public TimeSpan End;
			public int DayOffset;
			public int Slots = 1;
		}

		// Raw data of one session (day / week / month / custom session). Never merged; merging happens at profile level.
		private sealed class TpoSession
		{
			public string Key;
			public int Index;
			public DateTime OrderKey;
			public DateTime PeriodDate;
			public DateTime FirstBarOpen;
			public DateTime LastBarClose;
			public DateTime Anchor;
			public DateTime DayAnchor;
			public int DayKey = int.MinValue;
			public int ExpectedSlots = 1;
			public double High = double.MinValue;
			public double Low = double.MaxValue;
			public double Open;
			public double Close;
			public bool HasIb;
			public double IbHigh = double.MinValue;
			public double IbLow = double.MaxValue;
			public bool HasOpening;
			public double OpeningHigh = double.MinValue;
			public double OpeningLow = double.MaxValue;
			public double OpeningClose;
			public int LastMinuteBar = -1;
			public int MinuteCount;
			public int Version;
			public int EndPos;
			public readonly List<TpoBlock> Blocks = new List<TpoBlock>();
			public readonly List<MinuteRec> Minutes = new List<MinuteRec>();

			// Chart bar index caches (render thread only).
			public int StartIdx = -1;
			public int EndIdx = -1;
		}

		// Result of building a profile. The data part is never changed after Build; it is replaced as a whole.
		private sealed class TpoBuilt
		{
			public long Signature;
			public int RequestedRowTicks;
			public long RowTicks = 1;
			public long RowMin;
			public int RowCount;
			public int[] Counts;
			public int[] RowStart;
			public char[] CellLetter;
			public byte[] CellShade;
			public double[] Vol;
			public double High;
			public double Low;
			public int Poc;
			public int Vah;
			public int Val;
			public int MaxCount;
			public int TotalTpo;
			public int TpoAbove;
			public int TpoBelow;
			public int VPoc;
			public int VVah;
			public int VVal;
			public double MaxVol;
			public double TotalVol;
			public int TopRun;
			public int BottomRun;
			public int TopTail;
			public int BottomTail;
			public bool PoorHigh;
			public bool PoorLow;
			public bool HasIb;
			public double IbHigh;
			public double IbLow;
			public double ExtensionUp;
			public double ExtensionDown;
			public int OpenRow;
			public int CloseRow;
			public string DayTypeText;
			public string OpenTypeText;

			// Text under the profile, composed on the first redraw that needs it (render thread only).
			public string Annotation;

			// Per block, in time order.
			public DateTime[] BlockStart;
			public DateTime[] BlockEnd;
			public int[] BlockRow0;
			public int[] BlockRow1;
			public char[] BlockLetter;
			public byte[] BlockShade;
			public sbyte[] BlockOtf;
			public int OtfDirection;
			public int OtfRun;

			// Developing levels: one point per block (or per group of blocks on very long profiles).
			public int DevCount;
			public int[] DevFrom;
			public int[] DevTo;
			public int[] DevPoc;
			public int[] DevVah;
			public int[] DevVal;

			// Chart bar index of each block (render thread only).
			public int[] BlockIdx0;
			public int[] BlockIdx1;
			public bool BlockIdxDone;
		}

		// A level that is extended to the right until price trades at it again.
		private sealed class NakedLevel
		{
			public bool Active;
			public double Price;
			public int Start;
			public int Pos;
			public bool HasAfter;
			public bool Touched;
			public DateTime TouchTime;
			public int TouchBarIdx = -1;
		}

		// An opening gap: the New York session opened above the prior session's high or below its low.
		// Guarded by sync, except the chart bar caches, which belong to the render thread.
		private sealed class GapRec
		{
			public bool Up;
			public double Level;        // the prior high (gap up) or prior low (gap down): where the line is, and what fills the gap
			public double Edge;         // how far New York trading has come back towards the level: the other side of what is left
			public DateTime StartClose; // chart time: close of the opening minute
			public bool Filled;
			public DateTime FillClose;  // chart time: close of the minute that traded at the level
			public bool SessionOver;    // its own New York session ended without filling it
			public DateTime AreaFrom;   // chart time: close of that session's last minute
			public int StartIdx = -1;
			public int FillIdx = -1;
			public int AreaIdx = -1;
		}

		// Copy of a gap for one redraw.
		private struct GapView
		{
			public GapRec Rec;
			public bool Up;
			public double Level;
			public double Edge;
			public DateTime StartClose;
			public bool Filled;
			public DateTime FillClose;
			public bool SessionOver;
			public DateTime AreaFrom;
		}

		// What is drawn: one session, or several sessions combined.
		private sealed class TpoProfile
		{
			public readonly List<TpoSession> Sessions = new List<TpoSession>();
			public TpoSession SpanStart;
			public TpoBuilt Built;
			public TpoBuilt NakedFor;
			public NakedLevel[] Naked;
			public long NakedStamp = -1;
			public int GeoStart;
			public int GeoEnd;
			public bool Visible;
			public bool HitValid;
			public float HitLeft;
			public float HitRight;
		}

		private sealed class Glyph
		{
			public SharpDX.DirectWrite.TextLayout Layout;
			public float W;
			public float H;
		}

		#endregion

		#region Fields

		private const int GradientSteps = 16;
		private const int MaxRowsPerProfile = 1500;
		private const int MinLetterPixels = 7;
		private const int MaxLetterFont = 16;
		private const int MaxDevPoints = 800;
		private const int ChunkSize = 256;
		private const int OpeningMinutes = 30;

		private const int LvPoc = 0;
		private const int LvVah = 1;
		private const int LvVal = 2;
		private const int LvPoorHigh = 3;
		private const int LvPoorLow = 4;
		private const int LvVolPoc = 5;
		private const int LvVolVah = 6;
		private const int LvVolVal = 7;
		private const int LevelCount = 8;

		private readonly object sync = new object();
		private readonly List<TpoSession> sessions = new List<TpoSession>();
		private List<TpoProfile> profiles = new List<TpoProfile>();
		private readonly HashSet<string> mergedKeys = new HashSet<string>();
		private readonly HashSet<string> expandedKeys = new HashSet<string>();
		private readonly List<object> addedMenuItems = new List<object>();
		private bool groupingDirty = true;
		private int groupedSessionCount = -1;

		// Every minute bar, inside or outside the session windows: used to find when a level is traded again.
		// Every minute bar's range, kept only while a naked level is being followed (see RecordMinute).
		private readonly List<RangeRec> allMinutes = new List<RangeRec>();
		private long dataStamp;

		// Initial balance of the latest New York open (09:30 New York time), whatever trading hours the profiles use.
		// Guarded by sync. The times are chart times: the close of the first minute of the initial balance, and the
		// close of the first minute after it (the moment the initial balance is complete).
		private static readonly TimeSpan NewYorkOpen = new TimeSpan(9, 30, 0);
		private static readonly TimeSpan NewYorkClose = new TimeSpan(16, 0, 0);
		private DateTime ibDate = DateTime.MinValue;
		private DateTime ibFirstClose = DateTime.MinValue;
		private DateTime ibMarkClose = DateTime.MinValue;
		private double ibHigh = double.MinValue;
		private double ibLow = double.MaxValue;
		private bool ibComplete;

		// Opening gaps. Guarded by sync. Only New York session minutes (09:30-16:00) are looked at, so a gap
		// can only be filled, or eaten into, while New York trades.
		private readonly List<GapRec> gaps = new List<GapRec>();
		private readonly List<GapRec> openGaps = new List<GapRec>();
		private DateTime gapDay = DateTime.MinValue;
		private double gapHigh = double.MinValue;
		private double gapLow = double.MaxValue;
		private bool gapSessionOpen;
		private DateTime gapLastClose = DateTime.MinValue;
		private readonly List<double> chunkLow = new List<double>();
		private readonly List<double> chunkHigh = new List<double>();
		private int allLastBar = -1;

		// Bar update thread only. The last values handed on, so that ticks which change nothing are dropped early.
		private int seenBar = -1;
		private double seenHigh = double.NaN;
		private double seenLow = double.NaN;
		private double seenClose = double.NaN;
		private double seenVolume = double.NaN;
		private long tzCacheHour = long.MinValue;
		private TimeSpan tzCacheOffset = TimeSpan.Zero;

		// Settings resolved once the data is loaded.
		private double tickSize = 0.01;
		private int priceDecimals = 2;
		private int blockMinutes = 30;
		private string keyPrefix = "D";
		private string zoneProblem;
		private List<SessionWindow> windows = new List<SessionWindow>();
		private TimeZoneInfo srcZone;
		private TimeZoneInfo dstZone;
		private bool nakedEnabled;
		private bool needVolume;

		// Rendering resources (UI thread only).
		private bool terminated;
		private bool mouseAttached;
		private bool reachedRealtime;
		private bool dxReady;
		private SharpDX.Direct2D1.RenderTarget dxTarget;

		// Problems are shown on the chart instead of failing silently.
		private string barError;
		private string renderError;
		private string lastPrintedError;
		private int lastSessionCount = -1;
		private SharpDX.Direct2D1.SolidColorBrush[] dxPeriod;
		private SharpDX.Direct2D1.SolidColorBrush[] dxHeat;
		private SharpDX.Direct2D1.SolidColorBrush[] dxVolGrade;
		private SharpDX.Direct2D1.SolidColorBrush[] dxLevel;
		private bool[] levelIsLight = new bool[LevelCount];
		private SharpDX.Direct2D1.SolidColorBrush dxSolid;
		private SharpDX.Direct2D1.SolidColorBrush dxOutside;
		private SharpDX.Direct2D1.SolidColorBrush dxPocRow;
		private SharpDX.Direct2D1.SolidColorBrush dxLetter;
		private SharpDX.Direct2D1.SolidColorBrush dxOpenBlock;
		private SharpDX.Direct2D1.SolidColorBrush dxCloseBlock;
		private SharpDX.Direct2D1.SolidColorBrush dxVaFill;
		private SharpDX.Direct2D1.SolidColorBrush dxIb;
		private SharpDX.Direct2D1.SolidColorBrush dxRangeExt;
		private SharpDX.Direct2D1.SolidColorBrush dxSingle;
		private SharpDX.Direct2D1.SolidColorBrush dxBuyTail;
		private SharpDX.Direct2D1.SolidColorBrush dxSellTail;
		private SharpDX.Direct2D1.SolidColorBrush dxDevPoc;
		private SharpDX.Direct2D1.SolidColorBrush dxDevVa;
		private SharpDX.Direct2D1.SolidColorBrush dxOtfUp;
		private SharpDX.Direct2D1.SolidColorBrush dxOtfDown;
		private SharpDX.Direct2D1.SolidColorBrush dxVolIn;
		private SharpDX.Direct2D1.SolidColorBrush dxVolOut;
		private SharpDX.Direct2D1.SolidColorBrush dxText;
		private SharpDX.Direct2D1.SolidColorBrush dxPriorHigh;
		private SharpDX.Direct2D1.SolidColorBrush dxGapLine;
		private SharpDX.Direct2D1.SolidColorBrush dxGapArea;
		private SharpDX.Direct2D1.SolidColorBrush dxPriorLow;
		private SharpDX.Direct2D1.SolidColorBrush dxDayType;
		private SharpDX.Direct2D1.SolidColorBrush dxOpenType;
		private SharpDX.Direct2D1.SolidColorBrush dxStatsBack;
		private SharpDX.Direct2D1.SolidColorBrush dxTagDark;
		private SharpDX.Direct2D1.SolidColorBrush dxTagLight;
		private SharpDX.Direct2D1.StrokeStyle dashStyle;
		private SharpDX.Direct2D1.StrokeStyle dotStyle;
		private SharpDX.DirectWrite.TextFormat labelFormat;
		private SharpDX.DirectWrite.TextFormat[] letterFormats = new SharpDX.DirectWrite.TextFormat[MaxLetterFont + 1];
		private readonly Dictionary<int, Glyph> glyphs = new Dictionary<int, Glyph>();

		// Finished text layouts of labels, kept from one redraw to the next: slot 0 is the label font,
		// slot n + 1 the small font of size n. Emptied at the start of a redraw once it holds too many.
		private const int MaxCachedLabels = 1500;
		private readonly Dictionary<string, Glyph>[] labelCache = new Dictionary<string, Glyph>[MaxLetterFont + 2];
		private int labelCacheCount;

		// Reused from redraw to redraw (render thread only).
		private GapView[] gapViews = new GapView[0];
		private TpoProfile[] snapshotBuffer = new TpoProfile[0];
		private DateTime[] snapshotEndBuffer = new DateTime[0];
		private float[] edgeBuffer = new float[256];
		private float clipLeft;
		private float clipTop;
		private float clipRight;
		private float clipBottom;

		#endregion

		#region State handling

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description = "TPO market profile: time spent at each price inside a session, with point of control, value area, initial balance, structure markers, annotations, volume profile, and a profile menu to expand or merge sessions.";
				Name = "TPO Market Profile";
				Calculate = Calculate.OnEachTick;
				IsOverlay = true;
				DisplayInDataBox = false;
				DrawOnPricePanel = true;
				PaintPriceMarkers = false;
				IsAutoScale = false;
				IsSuspendedWhileInactive = true;
				ScaleJustification = NinjaTrader.Gui.Chart.ScaleJustification.Right;

				// 1. General
				SessionType = TpoMarketProfileTypes.TpoSessionType.Daily;
				SessionHours = TpoMarketProfileTypes.TpoSessionHours.RTH;
				BlockSize = TpoMarketProfileTypes.TpoBlockSize.Minutes30;
				TicksPerRow = 10;
				ProfileSpacing = 0;
				RollingCompositeSessions = 1;

				// 2. Color
				Palette = TpoMarketProfileTypes.TpoPalette.BySession;
				CustomColors = false;
				BlockDisplay = TpoMarketProfileTypes.TpoBlockDisplay.BlocksWithLetters;
				BlockColorMode = TpoMarketProfileTypes.TpoBlockColorMode.Period;
				LineStyle = TpoMarketProfileTypes.TpoLineStyle.Dashed;
				HighlightStartEnd = false;
				OutsideVaColors = true;
				HighlightPocRow = true;
				BlockWidth = 0;
				MaxBlockWidth = 8;
				ProfileWidthPercent = 90;
				BlockOpacity = 75;
				LabelFontSize = 11;
				DrawBehindBars = true;

				// 3. Profile: core levels
				PocShow = true;
				PocLabel = true;
				PocNaked = false;
				PocScale = false;
				PocThickness = 1;
				ValueAreaPercent = 70;
				VahShow = true;
				VahLabel = true;
				VahNaked = false;
				VahScale = false;
				ValShow = true;
				ValLabel = true;
				ValNaked = false;
				ValScale = false;
				LineWidth = 1;
				FillValueArea = false;
				ValueAreaFillOpacity = 12;
				LevelPriceTags = true;
				PriorPocExtend = true;
				PriorVahExtend = true;
				PriorValExtend = true;
				PriorHighExtend = false;
				PriorLowExtend = false;
				PriorSessionLevels = false;
				NakedPocExtend = true;

				// 4. Profile: structure markers
				ShowInitialBalance = true;
				InitialBalanceMinutes = 60;
				IbLineWidth = 2;
				PoorHighShow = true;
				PoorHighLabel = true;
				PoorHighNaked = false;
				PoorHighScale = false;
				PoorLowShow = true;
				PoorLowLabel = true;
				PoorLowNaked = false;
				PoorLowScale = false;
				ShowSinglePrints = true;
				ShowTails = false;
				MinTailRows = 2;
				ShowGaps = true;
				GapLineWidth = 2;
				GapAreaOpacity = 25;

				// 5. Profile: annotations
				PocSummary = false;
				StatisticsBox = false;
				ShowTpoCounts = false;
				OneTimeFraming = true;
				RangeExtension = false;
				DevelopingPoc = false;
				DevelopingVa = false;
				DayType = true;
				OpenType = true;

				// 6. Volume
				ShowVolumeProfile = false;
				VolumePosition = TpoMarketProfileTypes.TpoVolumePosition.Left;
				VolumeValues = false;
				VolumeWidthPercent = 30;
				VolumeColorGrade = false;
				VolumeThreshold = 0;
				VolumeOpacity = 45;
				VolPocShow = true;
				VolPocExtend = false;
				VolPocScale = false;
				VolVahShow = false;
				VolVahExtend = false;
				VolVahScale = false;
				VolValShow = false;
				VolValExtend = false;
				VolValScale = false;
				ShowVolumeSummary = false;

				// 7. Merge
				EnableClickMerge = true;
				MergeLatestCount = 0;
				MergedSessions = string.Empty;
				ExpandedSessions = string.Empty;

				// 8. Colors
				RthBlockBrush = Brushes.DodgerBlue;
				EthBlockBrush = Brushes.LimeGreen;
				PeriodEarlyBrush = Brushes.DodgerBlue;
				PeriodMidBrush = Brushes.DodgerBlue;
				PeriodLateBrush = Brushes.DodgerBlue;
				HeatLowBrush = Brushes.SteelBlue;
				HeatMidBrush = Brushes.Khaki;
				HeatHighBrush = Brushes.Crimson;
				SolidBrush = Brushes.DodgerBlue;
				OutsideValueAreaBrush = Brushes.Gray;
				PocRowBrush = Brushes.White;
				LetterBrush = Brushes.Black;
				OpenBlockBrush = Brushes.DodgerBlue;
				CloseBlockBrush = Brushes.Orange;
				PocLineBrush = Brushes.Orange;
				VahBrush = Brushes.DodgerBlue;
				ValBrush = Brushes.DodgerBlue;
				PriorHighBrush = Brushes.Orange;
				PriorLowBrush = Brushes.Orange;
				GapLineBrush = Brushes.Yellow;
				GapAreaBrush = Brushes.Yellow;
				ValueAreaFillBrush = Brushes.LightGray;
				InitialBalanceBrush = Brushes.White;
				RangeExtensionBrush = Brushes.DeepSkyBlue;
				PoorHighBrush = Brushes.DarkBlue;
				PoorLowBrush = Brushes.DarkBlue;
				SinglePrintBrush = Brushes.Magenta;
				BuyingTailBrush = Brushes.LimeGreen;
				SellingTailBrush = Brushes.OrangeRed;
				DevelopingPocBrush = Brushes.Gold;
				DevelopingVaBrush = Brushes.Silver;
				OtfUpBrush = Brushes.LimeGreen;
				OtfDownBrush = Brushes.OrangeRed;
				VolumeBrush = Brushes.CornflowerBlue;
				VolumeOutsideBrush = Brushes.SlateGray;
				VolumePocBrush = Brushes.White;
				VolumeVahBrush = Brushes.MediumPurple;
				VolumeValBrush = Brushes.MediumPurple;
				TextBrush = Brushes.LightGray;
				DayTypeBrush = Brushes.Gold;
				OpenTypeBrush = Brushes.DeepSkyBlue;
				StatisticsBackgroundBrush = Brushes.Black;
			}
			else if (State == State.Configure)
			{
				// The developing profile must follow every tick, whatever was chosen for Calculate in the dialog.
				Calculate = Calculate.OnEachTick;

				// BarsArray[1]: the 1-minute series every profile is built from.
				AddDataSeries(BarsPeriodType.Minute, 1);
			}
			else if (State == State.DataLoaded)
			{
				InitializeSettings();
			}
			else if (State == State.Historical)
			{
				// Each step is optional for the profile itself, so none of them may stop the indicator.
				if (ChartControl != null)
				{
					try
					{
						if (DrawBehindBars)
							SetZOrder(-1);
					}
					catch (Exception)
					{
					}

					try
					{
						if (EnableClickMerge)
						{
							ChartControl attachTo = ChartControl;
							attachTo.Dispatcher.InvokeAsync(new Action(() =>
							{
								try
								{
									AttachMouse(attachTo);
								}
								catch (Exception)
								{
								}
							}));
						}
					}
					catch (Exception)
					{
					}
				}
			}
			else if (State == State.Realtime)
			{
				reachedRealtime = true;
			}
			else if (State == State.Terminated)
			{
				terminated = true;
				ChartControl cc = ChartControl;
				try
				{
					if (cc != null && cc.Dispatcher != null && !cc.Dispatcher.CheckAccess())
					{
						cc.Dispatcher.InvokeAsync(new Action(() =>
						{
							try
							{
								DetachMouse(cc);
								DisposeAllResources();
							}
							catch (Exception)
							{
							}
						}));
					}
					else
					{
						if (cc != null)
							DetachMouse(cc);
						DisposeAllResources();
					}
				}
				catch (Exception)
				{
				}
			}
		}

		public override string DisplayName
		{
			get { return "TPO Market Profile (" + SessionType + ", " + SessionHours + ", " + BlockLabel() + ")"; }
		}

		private string BlockLabel()
		{
			switch (BlockSize)
			{
				case TpoMarketProfileTypes.TpoBlockSize.Minutes5: return "5m";
				case TpoMarketProfileTypes.TpoBlockSize.Hour1: return "1h";
				case TpoMarketProfileTypes.TpoBlockSize.Hours4: return "4h";
				default: return "30m";
			}
		}

		private void InitializeSettings()
		{
			tickSize = TickSize > 0 ? TickSize : 0.01;

			priceDecimals = 0;
			double t = tickSize;
			while (priceDecimals < 8 && Math.Abs(t - Math.Round(t)) > 1e-9)
			{
				t *= 10.0;
				priceDecimals++;
			}

			switch (BlockSize)
			{
				case TpoMarketProfileTypes.TpoBlockSize.Minutes5: blockMinutes = 5; break;
				case TpoMarketProfileTypes.TpoBlockSize.Hour1: blockMinutes = 60; break;
				case TpoMarketProfileTypes.TpoBlockSize.Hours4: blockMinutes = 240; break;
				default: blockMinutes = 30; break;
			}

			switch (SessionType)
			{
				case TpoMarketProfileTypes.TpoSessionType.Weekly: keyPrefix = "W"; break;
				case TpoMarketProfileTypes.TpoSessionType.Monthly: keyPrefix = "M"; break;
				default: keyPrefix = "D"; break;
			}

			// Sessions are defined in New York time. Chart times are converted from the time zone
			// NinjaTrader itself is set to, so nothing has to be chosen by hand.
			zoneProblem = null;
			srcZone = PlatformZone();
			dstZone = EasternZone();
			if (srcZone == null || dstZone == null)
				zoneProblem = "TPO Market Profile: could not work out New York time on this computer, so sessions may be shifted.";

			SessionWindow window = new SessionWindow();
			if (SessionHours == TpoMarketProfileTypes.TpoSessionHours.ETH)
			{
				// Overnight: Asia and London, from the evening open to the New York cash open. Belongs to the next day.
				window.Start = new TimeSpan(18, 0, 0);
				window.End = new TimeSpan(9, 30, 0);
				window.DayOffset = 1;
			}
			else
			{
				// Regular trading hours: the New York session.
				window.Start = new TimeSpan(9, 30, 0);
				window.End = new TimeSpan(16, 0, 0);
				window.DayOffset = 0;
			}

			double length = (window.End - window.Start).TotalMinutes;
			if (length <= 0)
				length += 1440.0;
			window.Slots = Math.Max(1, (int)Math.Ceiling(length / blockMinutes));

			windows = new List<SessionWindow>();
			windows.Add(window);

			needVolume = ShowVolumeProfile || ShowVolumeSummary || StatisticsBox;

			nakedEnabled = false;
			for (int lv = 0; lv < LevelCount; lv++)
			{
				if (NakedTracked(lv))
					nakedEnabled = true;
			}

			lock (sync)
			{
				ParseKeys(MergedSessions, mergedKeys);
				ParseKeys(ExpandedSessions, expandedKeys);
				groupingDirty = true;
			}
		}

		// The time zone NinjaTrader shows its bars in (Tools > Options > General), which defaults to the computer's own.
		private static TimeZoneInfo PlatformZone()
		{
			try
			{
				TimeZoneInfo zone = NinjaTrader.Core.Globals.GeneralOptions.TimeZoneInfo;
				if (zone != null)
					return zone;
			}
			catch (Exception)
			{
			}

			try
			{
				return TimeZoneInfo.Local;
			}
			catch (Exception)
			{
				return null;
			}
		}

		// New York time, daylight saving included. Windows knows it; the rule is built by hand if it somehow does not.
		private static TimeZoneInfo EasternZone()
		{
			try
			{
				return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
			}
			catch (Exception)
			{
			}

			try
			{
				return TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
			}
			catch (Exception)
			{
			}

			return BuildNewYorkZone();
		}

		private static TimeZoneInfo BuildNewYorkZone()
		{
			try
			{
				// Daylight time runs from the second Sunday of March to the first Sunday of November, changing at 02:00.
				TimeZoneInfo.TransitionTime start = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday);
				TimeZoneInfo.TransitionTime end = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday);
				TimeZoneInfo.AdjustmentRule rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
					DateTime.MinValue.Date, DateTime.MaxValue.Date, TimeSpan.FromHours(1), start, end);
				return TimeZoneInfo.CreateCustomTimeZone("TpoNewYork", TimeSpan.FromHours(-5), "New York", "Eastern Standard Time",
					"Eastern Daylight Time", new TimeZoneInfo.AdjustmentRule[] { rule });
			}
			catch (Exception)
			{
				return null;
			}
		}

		#endregion

		#region Level settings

		private bool LevelShown(int level)
		{
			switch (level)
			{
				case LvPoc: return PocShow;
				case LvVah: return VahShow;
				case LvVal: return ValShow;
				case LvPoorHigh: return PoorHighShow;
				case LvPoorLow: return PoorLowShow;
				case LvVolPoc: return ShowVolumeProfile && VolPocShow;
				case LvVolVah: return ShowVolumeProfile && VolVahShow;
				case LvVolVal: return ShowVolumeProfile && VolValShow;
				default: return false;
			}
		}

		private bool LevelNaked(int level)
		{
			switch (level)
			{
				case LvPoc: return PocNaked;
				case LvVah: return VahNaked;
				case LvVal: return ValNaked;
				case LvPoorHigh: return PoorHighNaked;
				case LvPoorLow: return PoorLowNaked;
				case LvVolPoc: return VolPocExtend;
				case LvVolVah: return VolVahExtend;
				case LvVolVal: return VolValExtend;
				default: return false;
			}
		}

		private bool LevelScale(int level)
		{
			switch (level)
			{
				case LvPoc: return PocScale;
				case LvVah: return VahScale;
				case LvVal: return ValScale;
				case LvPoorHigh: return PoorHighScale;
				case LvPoorLow: return PoorLowScale;
				case LvVolPoc: return VolPocScale;
				case LvVolVah: return VolVahScale;
				case LvVolVal: return VolValScale;
				default: return false;
			}
		}

		private bool LevelLabel(int level)
		{
			switch (level)
			{
				case LvPoc: return PocLabel;
				case LvVah: return VahLabel;
				case LvVal: return ValLabel;
				case LvPoorHigh: return PoorHighLabel;
				case LvPoorLow: return PoorLowLabel;
				default: return LevelPriceTags;
			}
		}

		private static string LevelName(int level)
		{
			switch (level)
			{
				case LvPoc: return "POC";
				case LvVah: return "VAH";
				case LvVal: return "VAL";
				case LvPoorHigh: return "Poor high";
				case LvPoorLow: return "Poor low";
				case LvVolPoc: return "vPOC";
				case LvVolVah: return "vVAH";
				case LvVolVal: return "vVAL";
				default: return string.Empty;
			}
		}

		private string LevelText(int level, double price, string prefix)
		{
			string text = prefix + LevelName(level);
			if (LevelPriceTags)
				text += " " + FormatPrice(price);
			return text;
		}

		// Is this level followed after its profile ends, to find out whether price has traded at it again?
		private bool NakedTracked(int level)
		{
			if (level == LvPoc && NakedPocExtend)
				return true;
			return LevelShown(level) && LevelNaked(level);
		}

		// Is a level that has been traded again still drawn, from its profile up to the bar that traded it?
		// "Extend All Naked POCs" alone draws only the ones that are still untouched.
		private bool NakedTrail(int level)
		{
			return LevelShown(level) && LevelNaked(level);
		}

		// Is this profile's point of control still untouched and drawn to the right edge by "Extend All Naked POCs"?
		private bool NakedPocOpen(TpoProfile p, TpoBuilt b)
		{
			if (!NakedPocExtend || p == null || p.Naked == null || !object.ReferenceEquals(p.NakedFor, b))
				return false;
			NakedLevel level = p.Naked[LvPoc];
			return level.Active && level.HasAfter && !level.Touched;
		}

		// Is this level extended from the prior session? Only the point of control and the value area edges can be;
		// the range high and low have their own switches.
		private bool PriorChosen(int level)
		{
			switch (level)
			{
				case LvPoc: return PriorPocExtend;
				case LvVah: return PriorVahExtend;
				case LvVal: return PriorValExtend;
				default: return false;
			}
		}

		// "pRTH VAH 31234.50" or "pETH POC 31180.25": which kind of session the extended level comes from.
		private string PriorText(string name, double price)
		{
			string text = (SessionHours == TpoMarketProfileTypes.TpoSessionHours.ETH ? "pETH " : "pRTH ") + name;
			if (LevelPriceTags)
				text += " " + FormatPrice(price);
			return text;
		}

		// lineValue: where the line is drawn on the price axis. price: the price shown and tested for touches.
		private bool LevelValues(TpoBuilt b, int level, out double lineValue, out double price)
		{
			lineValue = 0;
			price = 0;
			if (b == null || b.RowCount == 0)
				return false;

			double rowSize = b.RowTicks * tickSize;
			double baseEdge = b.RowMin * rowSize - tickSize * 0.5;

			switch (level)
			{
				case LvPoc:
					lineValue = baseEdge + (b.Poc + 0.5) * rowSize;
					price = RowMidPrice(b, b.Poc);
					return true;
				case LvVah:
					lineValue = baseEdge + (b.Vah + 1) * rowSize;
					price = RowTopPrice(b, b.Vah);
					return true;
				case LvVal:
					lineValue = baseEdge + b.Val * rowSize;
					price = RowBottomPrice(b, b.Val);
					return true;
				case LvPoorHigh:
					lineValue = b.High;
					price = b.High;
					return b.PoorHigh;
				case LvPoorLow:
					lineValue = b.Low;
					price = b.Low;
					return b.PoorLow;
				case LvVolPoc:
					lineValue = baseEdge + (b.VPoc + 0.5) * rowSize;
					price = RowMidPrice(b, b.VPoc);
					return b.Vol != null && b.MaxVol > 0;
				case LvVolVah:
					lineValue = baseEdge + (b.VVah + 1) * rowSize;
					price = RowTopPrice(b, b.VVah);
					return b.Vol != null && b.MaxVol > 0;
				case LvVolVal:
					lineValue = baseEdge + b.VVal * rowSize;
					price = RowBottomPrice(b, b.VVal);
					return b.Vol != null && b.MaxVol > 0;
				default:
					return false;
			}
		}

		private SharpDX.Direct2D1.StrokeStyle LevelStyle(int level)
		{
			if (level == LvVolPoc)
				return null;
			if (level == LvVolVah || level == LvVolVal)
				return dotStyle;

			switch (LineStyle)
			{
				case TpoMarketProfileTypes.TpoLineStyle.Dashed: return dashStyle;
				case TpoMarketProfileTypes.TpoLineStyle.Dotted: return dotStyle;
				default: return null;
			}
		}

		private float LevelWidth(int level)
		{
			return level == LvPoc ? Math.Max(1, PocThickness) : Math.Max(1, LineWidth);
		}

		// Thickness of every line that is not a level line of its own: follows "Line Thickness".
		private float OtherWidth()
		{
			return Math.Max(1, LineWidth);
		}

		#endregion

		#region Data collection

		protected override void OnBarUpdate()
		{
			try
			{
				CollectBar();
			}
			catch (Exception ex)
			{
				if (barError == null)
				{
					barError = "TPO Market Profile: data error: " + Describe(ex);
					Print(barError);
				}
			}
		}

		private static string Describe(Exception ex)
		{
			string text = ex.GetType().Name + ": " + ex.Message;
			try
			{
				if (ex.TargetSite != null)
					text += " (in " + ex.TargetSite.Name + ")";
			}
			catch (Exception)
			{
			}
			return text;
		}

		private void CollectBar()
		{
			if (BarsInProgress != 1)
				return;
			int barIndex = CurrentBars[1];
			if (barIndex < 0)
				return;

			double high = Highs[1][0];
			double low = Lows[1][0];
			double close = Closes[1][0];
			double volume = needVolume ? Volumes[1][0] : 0;

			if (double.IsNaN(high) || double.IsNaN(low) || high < low)
				return;

			// Most ticks trade inside the minute's range at the last price: nothing that is drawn changes, so
			// they stop here. (Volume only counts while something that shows volume is switched on.)
			if (barIndex == seenBar && high == seenHigh && low == seenLow && close == seenClose && volume == seenVolume)
				return;
			seenBar = barIndex;
			seenHigh = high;
			seenLow = low;
			seenClose = close;
			seenVolume = volume;

			DateTime barClose = Times[1][0];
			double open = Opens[1][0];

			// Minute bars are stamped with their closing time.
			DateTime barOpen = barClose.AddMinutes(-1);

			DateTime dayDate;
			DateTime dayAnchor;
			DateTime orderKey;
			int expectedSlots;
			int windowIndex = -1;
			bool inSession;

			inSession = ResolveWindowBar(barOpen, out dayDate, out dayAnchor, out orderKey, out windowIndex);
			expectedSlots = inSession ? windows[windowIndex].Slots : 1;

			DateTime newYorkOpenTime = ToWindowZone(barOpen);

			// Is this minute part of the New York session, and of which day? With RTH profiles that is exactly
			// what the session window just answered; otherwise the New York clock time says so.
			bool newYorkSession;
			DateTime newYorkDay;
			if (SessionHours == TpoMarketProfileTypes.TpoSessionHours.RTH)
			{
				newYorkSession = inSession;
				newYorkDay = dayDate;
			}
			else
			{
				TimeSpan newYorkClock = newYorkOpenTime.TimeOfDay;
				newYorkSession = newYorkClock >= NewYorkOpen && newYorkClock < NewYorkClose;
				newYorkDay = newYorkOpenTime.Date;
			}

			lock (sync)
			{
				if (nakedEnabled)
					RecordMinute(barIndex, barClose, high, low);
				FollowInitialBalance(newYorkOpenTime, barClose, high, low);
				if (ShowGaps)
					FollowGaps(newYorkSession, newYorkDay, barClose, open, high, low);
				if (inSession)
					Ingest(barIndex, barOpen, barClose, open, high, low, close, volume, dayDate, dayAnchor, orderKey, expectedSlots, windowIndex);
			}
		}

		// Called under lock(sync). Follows the initial balance of the latest New York open: it starts again at
		// every 09:30 New York time, which drops the one from the day before, and it is complete with the first
		// minute that starts after its last minute. newYorkTime is the opening time of the minute bar.
		private void FollowInitialBalance(DateTime newYorkTime, DateTime barClose, double high, double low)
		{
			DateTime date = newYorkTime.Date;
			TimeSpan timeOfDay = newYorkTime.TimeOfDay;

			if (date > ibDate && timeOfDay >= NewYorkOpen && timeOfDay < NewYorkClose)
			{
				ibDate = date;
				ibFirstClose = barClose;
				ibMarkClose = DateTime.MinValue;
				ibHigh = double.MinValue;
				ibLow = double.MaxValue;
				ibComplete = false;
			}

			if (ibComplete || date < ibDate || ibDate == DateTime.MinValue)
				return;

			if (date == ibDate && newYorkTime < ibDate + NewYorkOpen + TimeSpan.FromMinutes(InitialBalanceMinutes))
			{
				if (high > ibHigh) ibHigh = high;
				if (low < ibLow) ibLow = low;
			}
			else
			{
				ibComplete = true;
				ibMarkClose = barClose;
			}
		}

		// Called under lock(sync), once per changed minute bar. Opening gaps:
		// - at the first minute of a New York session, an open above the prior session's high or below its low
		//   starts a gap whose line sits at that prior high or low;
		// - a New York minute that trades at the line fills the gap; until then the gap shrinks to wherever
		//   New York trading has come closest;
		// - the first minute after a New York session marks the gaps still open as left over by their session.
		// Minutes outside the New York session never fill or shrink a gap.
		private void FollowGaps(bool newYorkSession, DateTime day, DateTime barClose, double open, double high, double low)
		{
			if (!newYorkSession)
			{
				if (gapSessionOpen)
					EndGapSession();
				return;
			}

			if (day < gapDay)
				return;

			double eps = tickSize * 0.01;

			if (day > gapDay)
			{
				if (gapSessionOpen)
					EndGapSession();

				bool hasPrior = gapDay != DateTime.MinValue && gapHigh >= gapLow;
				double priorHigh = gapHigh;
				double priorLow = gapLow;

				gapDay = day;
				gapHigh = high;
				gapLow = low;
				gapSessionOpen = true;

				if (hasPrior && (open > priorHigh + eps || open < priorLow - eps))
				{
					GapRec gap = new GapRec();
					gap.Up = open > priorHigh;
					gap.Level = gap.Up ? priorHigh : priorLow;
					gap.Edge = open;
					gap.StartClose = barClose;
					gaps.Add(gap);
					openGaps.Add(gap);
				}
			}
			else
			{
				if (high > gapHigh) gapHigh = high;
				if (low < gapLow) gapLow = low;
			}

			gapLastClose = barClose;

			for (int i = openGaps.Count - 1; i >= 0; i--)
			{
				GapRec gap = openGaps[i];
				bool touched = gap.Up ? low <= gap.Level + eps : high >= gap.Level - eps;
				if (touched)
				{
					gap.Filled = true;
					gap.FillClose = barClose;
					gap.Edge = gap.Level;
					openGaps.RemoveAt(i);
				}
				else if (gap.Up)
				{
					if (low < gap.Edge) gap.Edge = low;
				}
				else
				{
					if (high > gap.Edge) gap.Edge = high;
				}
			}
		}

		// Called under lock(sync). The New York session is over: what it did not fill is highlighted from here on.
		private void EndGapSession()
		{
			gapSessionOpen = false;
			for (int i = 0; i < openGaps.Count; i++)
			{
				GapRec gap = openGaps[i];
				if (!gap.SessionOver)
				{
					gap.SessionOver = true;
					gap.AreaFrom = gapLastClose;
				}
			}
		}

		// Only minutes inside the RTH or ETH window are counted. The bar time is turned into New York time first.
		private bool ResolveWindowBar(DateTime barOpen, out DateTime dayDate, out DateTime dayAnchor, out DateTime orderKey, out int windowIndex)
		{
			dayDate = DateTime.MinValue;
			dayAnchor = DateTime.MinValue;
			orderKey = DateTime.MinValue;
			windowIndex = -1;

			DateTime z = ToWindowZone(barOpen);
			TimeSpan tod = z.TimeOfDay;

			for (int w = 0; w < windows.Count; w++)
			{
				SessionWindow window = windows[w];
				DateTime windowDate;

				if (window.Start == window.End)
				{
					windowDate = tod >= window.Start ? z.Date : z.Date.AddDays(-1);
				}
				else if (window.Start < window.End)
				{
					if (tod < window.Start || tod >= window.End)
						continue;
					windowDate = z.Date;
				}
				else
				{
					if (tod >= window.Start)
						windowDate = z.Date;
					else if (tod < window.End)
						windowDate = z.Date.AddDays(-1);
					else
						continue;
				}

				double minutesFromStart = (z - (windowDate + window.Start)).TotalMinutes;
				if (minutesFromStart < 0)
					minutesFromStart = 0;

				dayDate = windowDate.AddDays(window.DayOffset);
				dayAnchor = barOpen.AddMinutes(-minutesFromStart);
				orderKey = windowDate + window.Start;
				windowIndex = w;
				return true;
			}
			return false;
		}

		private DateTime ToWindowZone(DateTime time)
		{
			if (srcZone == null || dstZone == null)
				return time;

			long hour = time.Ticks / TimeSpan.TicksPerHour;
			if (hour != tzCacheHour)
			{
				TimeSpan offset;
				try
				{
					DateTime unspecified = DateTime.SpecifyKind(time, DateTimeKind.Unspecified);
					DateTime converted = TimeZoneInfo.ConvertTime(unspecified, srcZone, dstZone);
					offset = converted - unspecified;
				}
				catch (Exception)
				{
					offset = dstZone.BaseUtcOffset - srcZone.BaseUtcOffset;
				}
				tzCacheHour = hour;
				tzCacheOffset = offset;
			}
			return time + tzCacheOffset;
		}

		private DateTime PeriodOf(DateTime day)
		{
			switch (SessionType)
			{
				case TpoMarketProfileTypes.TpoSessionType.Weekly:
					return day.Date.AddDays(-(((int)day.DayOfWeek + 6) % 7));
				case TpoMarketProfileTypes.TpoSessionType.Monthly:
					return new DateTime(day.Year, day.Month, 1);
				default:
					return day.Date;
			}
		}

		// Called under lock(sync), and only while a naked level is followed. Keeps every minute bar's range,
		// with a low/high summary per chunk for fast scanning. dataStamp changes whenever a range does.
		private void RecordMinute(int barIndex, DateTime barClose, double high, double low)
		{
			RangeRec rec;
			rec.Time = barClose;
			rec.Low = low;
			rec.High = high;

			if (allLastBar == barIndex && allMinutes.Count > 0)
			{
				RangeRec old = allMinutes[allMinutes.Count - 1];
				if (old.Low == low && old.High == high)
					return;
				allMinutes[allMinutes.Count - 1] = rec;
			}
			else
			{
				allMinutes.Add(rec);
				allLastBar = barIndex;
			}
			dataStamp++;

			int chunk = (allMinutes.Count - 1) / ChunkSize;
			if (chunk >= chunkLow.Count)
			{
				chunkLow.Add(low);
				chunkHigh.Add(high);
			}
			else
			{
				if (low < chunkLow[chunk]) chunkLow[chunk] = low;
				if (high > chunkHigh[chunk]) chunkHigh[chunk] = high;
			}
		}

		// Called under lock(sync). Touches no NinjaTrader objects.
		private void Ingest(int barIndex, DateTime barOpen, DateTime barClose, double open, double high, double low, double close, double volume,
			DateTime dayDate, DateTime dayAnchor, DateTime orderKey, int expectedSlots, int windowIndex)
		{
			DateTime periodDate = PeriodOf(dayDate);
			DateTime order = SessionType == TpoMarketProfileTypes.TpoSessionType.Daily ? orderKey : periodDate;
			TpoSession s = sessions.Count > 0 ? sessions[sessions.Count - 1] : null;

			if (s == null || order > s.OrderKey)
			{
				s = new TpoSession();
				s.Index = sessions.Count;
				s.OrderKey = order;
				s.PeriodDate = periodDate;
				s.Key = keyPrefix + periodDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
				s.Anchor = dayAnchor;
				s.FirstBarOpen = barOpen;
				s.LastBarClose = barClose;
				s.Open = open;
				s.Close = close;
				sessions.Add(s);
				groupingDirty = true;
			}
			else if (order < s.OrderKey)
			{
				return;
			}

			int dayKey = dayDate.Year * 10000 + dayDate.Month * 100 + dayDate.Day;
			if (s.DayKey != dayKey)
			{
				s.DayKey = dayKey;
				s.DayAnchor = dayAnchor;
			}

			int slot = (int)Math.Floor((barOpen - s.DayAnchor).TotalMinutes / blockMinutes);
			if (slot < 0)
				slot = 0;
			if (expectedSlots > s.ExpectedSlots)
				s.ExpectedSlots = expectedSlots;
			if (slot + 1 > s.ExpectedSlots)
				s.ExpectedSlots = slot + 1;

			bool changed = false;

			TpoBlock block = s.Blocks.Count > 0 ? s.Blocks[s.Blocks.Count - 1] : null;
			if (block == null || block.DayKey != dayKey || block.Slot != slot)
			{
				block = new TpoBlock();
				block.DayKey = dayKey;
				block.Slot = slot;
				block.Low = low;
				block.High = high;
				block.Start = barOpen;
				block.LastClose = barClose;
				s.Blocks.Add(block);
				changed = true;
			}
			else
			{
				if (low < block.Low) { block.Low = low; changed = true; }
				if (high > block.High) { block.High = high; changed = true; }
				if (barClose > block.LastClose) { block.LastClose = barClose; changed = true; }
			}

			bool newMinute = s.LastMinuteBar != barIndex;
			if (newMinute)
			{
				s.LastMinuteBar = barIndex;
				s.MinuteCount++;
				changed = true;
			}

			// The minute bars themselves are only kept for the volume profile and the volume figures.
			if (needVolume)
			{
				MinuteRec rec;
				rec.Time = barClose;
				rec.Low = low;
				rec.High = high;
				rec.Volume = volume;

				if (newMinute || s.Minutes.Count == 0)
				{
					s.Minutes.Add(rec);
				}
				else
				{
					MinuteRec old = s.Minutes[s.Minutes.Count - 1];
					if (old.Low != low || old.High != high || old.Volume != volume)
					{
						s.Minutes[s.Minutes.Count - 1] = rec;
						changed = true;
					}
				}
			}

			if (high > s.High) s.High = high;
			if (low < s.Low) s.Low = low;
			if (s.Close != close) { s.Close = close; changed = true; }

			if (barOpen < s.Anchor.AddMinutes(InitialBalanceMinutes))
			{
				s.HasIb = true;
				if (high > s.IbHigh) { s.IbHigh = high; changed = true; }
				if (low < s.IbLow) { s.IbLow = low; changed = true; }
			}

			if (barOpen < s.Anchor.AddMinutes(OpeningMinutes))
			{
				s.HasOpening = true;
				if (high > s.OpeningHigh) s.OpeningHigh = high;
				if (low < s.OpeningLow) s.OpeningLow = low;
				s.OpeningClose = close;
			}

			s.LastBarClose = barClose;
			s.EndPos = allMinutes.Count;
			if (changed)
				s.Version++;
		}

		#endregion

		#region Grouping and profile building

		private static void ParseKeys(string text, HashSet<string> keys)
		{
			keys.Clear();
			if (string.IsNullOrEmpty(text))
				return;

			string[] parts = text.Split(new char[] { ';', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
			for (int i = 0; i < parts.Length; i++)
			{
				string key = parts[i].Trim().ToUpperInvariant();
				if (key.Length > 0)
					keys.Add(key);
			}
		}

		private static string JoinKeys(HashSet<string> keys)
		{
			List<string> sorted = new List<string>(keys);
			sorted.Sort(StringComparer.Ordinal);
			return string.Join(";", sorted.ToArray());
		}

		// Called under lock(sync).
		// Rolling Composite Sessions > 1: every profile is its own session plus the sessions before it.
		// Otherwise a session listed in mergedKeys is drawn as part of the profile before it.
		private void Regroup()
		{
			Dictionary<TpoSession, TpoProfile> previous = new Dictionary<TpoSession, TpoProfile>();
			for (int i = 0; i < profiles.Count; i++)
			{
				List<TpoSession> old = profiles[i].Sessions;
				if (old.Count > 0)
					previous[old[old.Count - 1]] = profiles[i];
			}

			List<TpoProfile> list = new List<TpoProfile>();
			int n = sessions.Count;
			int rolling = Math.Max(1, RollingCompositeSessions);

			if (rolling > 1)
			{
				for (int i = 0; i < n; i++)
				{
					TpoProfile profile = new TpoProfile();
					for (int k = Math.Max(0, i - rolling + 1); k <= i; k++)
						profile.Sessions.Add(sessions[k]);
					profile.SpanStart = sessions[i];
					list.Add(profile);
				}
			}
			else
			{
				int latest = Math.Max(0, MergeLatestCount);
				TpoProfile current = null;

				for (int i = 0; i < n; i++)
				{
					TpoSession s = sessions[i];
					bool join = i > 0 && current != null && (mergedKeys.Contains(s.Key) || (latest > 0 && i >= n - latest));
					if (!join)
					{
						current = new TpoProfile();
						current.SpanStart = s;
						list.Add(current);
					}
					current.Sessions.Add(s);
				}
			}

			// Keep the built data and level state of profiles whose composition did not change.
			for (int i = 0; i < list.Count; i++)
			{
				List<TpoSession> now = list[i].Sessions;
				TpoProfile old;
				if (previous.TryGetValue(now[now.Count - 1], out old)
					&& old.Sessions.Count == now.Count
					&& object.ReferenceEquals(old.Sessions[0], now[0]))
				{
					list[i].Built = old.Built;
					list[i].NakedFor = old.NakedFor;
					list[i].Naked = old.Naked;
					list[i].NakedStamp = old.NakedStamp;
				}
			}

			profiles = list;
			groupedSessionCount = n;
			groupingDirty = false;
		}

		private int ComputeRowTicks()
		{
			return Math.Max(1, TicksPerRow);
		}

		private long ToTick(double price)
		{
			return (long)Math.Round(price / tickSize, MidpointRounding.AwayFromZero);
		}

		private long TicksBetween(double a, double b)
		{
			return (long)Math.Round(Math.Abs(a - b) / tickSize, MidpointRounding.AwayFromZero);
		}

		private static long FloorDiv(long a, long b)
		{
			long q = a / b;
			if ((a % b != 0) && ((a < 0) != (b < 0)))
				q--;
			return q;
		}

		private static int ClampRow(long row, int rowCount)
		{
			if (row < 0)
				return 0;
			if (row > rowCount - 1)
				return rowCount - 1;
			return (int)row;
		}

		private static char LetterOf(int slot)
		{
			int i = slot % 52;
			if (i < 0)
				i += 52;
			return i < 26 ? (char)('A' + i) : (char)('a' + (i - 26));
		}

		private static long SignatureOf(TpoProfile p)
		{
			unchecked
			{
				long sig = 17;
				for (int i = 0; i < p.Sessions.Count; i++)
					sig = sig * 31 + p.Sessions[i].Version;
				sig = sig * 31 + p.Sessions.Count;
				return sig;
			}
		}

		// Called under lock(sync).
		private void EnsureBuilt(TpoProfile p, int rowTicks)
		{
			long sig = SignatureOf(p);
			if (p.Built != null && p.Built.Signature == sig && p.Built.RequestedRowTicks == rowTicks)
				return;
			p.Built = Build(p, rowTicks, sig);
		}

		private TpoBuilt Build(TpoProfile p, int rowTicks, long sig)
		{
			TpoBuilt b = new TpoBuilt();
			b.Signature = sig;
			b.RequestedRowTicks = rowTicks;
			b.DayTypeText = string.Empty;
			b.OpenTypeText = string.Empty;

			int sessionCount = p.Sessions.Count;
			int blockCount = 0;
			double lo = double.MaxValue;
			double hi = double.MinValue;

			for (int si = 0; si < sessionCount; si++)
			{
				TpoSession s = p.Sessions[si];
				if (s.Blocks.Count == 0)
					continue;
				blockCount += s.Blocks.Count;
				if (s.Low < lo) lo = s.Low;
				if (s.High > hi) hi = s.High;
			}

			if (blockCount == 0 || hi < lo)
				return b;

			long loTick = ToTick(lo);
			long hiTick = ToTick(hi);
			long span = hiTick - loTick + 1;
			long eff = Math.Max(1, rowTicks);
			if (span / eff > MaxRowsPerProfile)
				eff = (span + MaxRowsPerProfile - 1) / MaxRowsPerProfile;

			long rowMin = FloorDiv(loTick, eff);
			long rowMax = FloorDiv(hiTick, eff);
			int n = (int)(rowMax - rowMin + 1);

			// Pass 1: how many blocks touched each row.
			int[] counts = new int[n];
			for (int si = 0; si < sessionCount; si++)
			{
				List<TpoBlock> blocks = p.Sessions[si].Blocks;
				for (int bi = 0; bi < blocks.Count; bi++)
				{
					int r0 = ClampRow(FloorDiv(ToTick(blocks[bi].Low), eff) - rowMin, n);
					int r1 = ClampRow(FloorDiv(ToTick(blocks[bi].High), eff) - rowMin, n);
					for (int r = r0; r <= r1; r++)
						counts[r]++;
				}
			}

			int[] rowStart = new int[n + 1];
			int maxCount = 0;
			for (int r = 0; r < n; r++)
			{
				rowStart[r + 1] = rowStart[r] + counts[r];
				if (counts[r] > maxCount)
					maxCount = counts[r];
			}
			int total = rowStart[n];

			// Pass 2: letter and colour shade of every cell, row by row, in time order.
			// The same pass records each block's time span, its one-time-framing state and the developing levels.
			char[] letters = new char[total];
			byte[] shades = new byte[total];
			int[] cursor = new int[n];
			Array.Copy(rowStart, cursor, n);

			bool bySession = BlockColorMode == TpoMarketProfileTypes.TpoBlockColorMode.Session;
			bool wantDeveloping = DevelopingPoc || DevelopingVa;
			double valueFraction = ValueAreaPercent / 100.0;

			DateTime[] blockStart = new DateTime[blockCount];
			DateTime[] blockEnd = new DateTime[blockCount];
			int[] blockRow0 = new int[blockCount];
			int[] blockRow1 = new int[blockCount];
			char[] blockLetter = new char[blockCount];
			byte[] blockShade = new byte[blockCount];
			sbyte[] blockOtf = new sbyte[blockCount];

			int stride = 1;
			int devCapacity = 0;
			double[] live = null;
			if (wantDeveloping)
			{
				stride = (blockCount + MaxDevPoints - 1) / MaxDevPoints;
				if (stride < 1)
					stride = 1;
				devCapacity = (blockCount + stride - 1) / stride;
				live = new double[n];
				b.DevFrom = new int[devCapacity];
				b.DevTo = new int[devCapacity];
				b.DevPoc = new int[devCapacity];
				b.DevVah = new int[devCapacity];
				b.DevVal = new int[devCapacity];
			}

			int g = 0;
			int devFrom = 0;
			int liveFirst = n - 1;
			int liveLast = 0;

			for (int si = 0; si < sessionCount; si++)
			{
				TpoSession s = p.Sessions[si];
				byte sessionShade = sessionCount > 1 ? ShadeOf((double)si / (sessionCount - 1)) : (byte)0;
				int denominator = Math.Max(1, s.ExpectedSlots - 1);
				List<TpoBlock> blocks = s.Blocks;
				TpoBlock priorBlock = null;
				sbyte priorState = 0;

				for (int bi = 0; bi < blocks.Count; bi++)
				{
					TpoBlock block = blocks[bi];
					char letter = LetterOf(block.Slot);
					byte shade = bySession ? sessionShade : ShadeOf((double)block.Slot / denominator);
					int r0 = ClampRow(FloorDiv(ToTick(block.Low), eff) - rowMin, n);
					int r1 = ClampRow(FloorDiv(ToTick(block.High), eff) - rowMin, n);
					if (r0 < liveFirst) liveFirst = r0;
					if (r1 > liveLast) liveLast = r1;
					for (int r = r0; r <= r1; r++)
					{
						int c = cursor[r]++;
						letters[c] = letter;
						shades[c] = shade;
						if (live != null)
							live[r] += 1.0;
					}

					blockStart[g] = block.Start;
					blockEnd[g] = block.LastClose;
					blockRow0[g] = r0;
					blockRow1[g] = r1;
					blockLetter[g] = letter;
					blockShade[g] = shade;

					// One-time framing: up while each block holds the prior block's low, down while it holds the prior high.
					sbyte state = 0;
					if (priorBlock != null && priorBlock.DayKey == block.DayKey)
					{
						bool holdsLow = block.Low >= priorBlock.Low;
						bool holdsHigh = block.High <= priorBlock.High;
						if (holdsLow && !holdsHigh)
							state = 1;
						else if (holdsHigh && !holdsLow)
							state = -1;
						else if (holdsLow && holdsHigh)
							state = priorState;
					}
					blockOtf[g] = state;
					priorState = state;
					priorBlock = block;

					if (live != null && ((g + 1) % stride == 0 || g == blockCount - 1) && b.DevCount < devCapacity)
					{
						int d = b.DevCount++;
						b.DevFrom[d] = devFrom;
						b.DevTo[d] = g;
						ValueArea(live, liveFirst, liveLast, valueFraction, out b.DevPoc[d], out b.DevVah[d], out b.DevVal[d]);
						devFrom = g + 1;
					}

					g++;
				}
			}

			b.RowTicks = eff;
			b.RowMin = rowMin;
			b.RowCount = n;
			b.Counts = counts;
			b.RowStart = rowStart;
			b.CellLetter = letters;
			b.CellShade = shades;
			b.MaxCount = maxCount;
			b.TotalTpo = total;
			b.High = hi;
			b.Low = lo;
			b.BlockStart = blockStart;
			b.BlockEnd = blockEnd;
			b.BlockRow0 = blockRow0;
			b.BlockRow1 = blockRow1;
			b.BlockLetter = blockLetter;
			b.BlockShade = blockShade;
			b.BlockOtf = blockOtf;

			// Current one-time-framing state: the run of equal, non-neutral states at the end.
			if (blockCount > 0 && blockOtf[blockCount - 1] != 0)
			{
				b.OtfDirection = blockOtf[blockCount - 1];
				for (int k = blockCount - 1; k >= 0 && blockOtf[k] == b.OtfDirection; k--)
					b.OtfRun++;
			}

			// Time point of control and value area.
			double[] asDouble = new double[n];
			for (int r = 0; r < n; r++)
				asDouble[r] = counts[r];
			ValueArea(asDouble, 0, n - 1, valueFraction, out b.Poc, out b.Vah, out b.Val);

			for (int r = 0; r < n; r++)
			{
				if (r > b.Poc) b.TpoAbove += counts[r];
				else if (r < b.Poc) b.TpoBelow += counts[r];
			}

			// Volume distribution: each minute's volume is spread evenly over the ticks it traded.
			if (needVolume)
			{
				double[] vol = new double[n];
				double totalVol = 0;

				for (int si = 0; si < sessionCount; si++)
				{
					List<MinuteRec> minutes = p.Sessions[si].Minutes;
					for (int mi = 0; mi < minutes.Count; mi++)
					{
						MinuteRec m = minutes[mi];
						if (m.Volume <= 0)
							continue;

						long t0 = ToTick(m.Low);
						long t1 = ToTick(m.High);
						if (t1 < t0)
						{
							long swap = t0;
							t0 = t1;
							t1 = swap;
						}

						int ra = ClampRow(FloorDiv(t0, eff) - rowMin, n);
						int rb = ClampRow(FloorDiv(t1, eff) - rowMin, n);
						totalVol += m.Volume;

						if (ra == rb)
						{
							vol[ra] += m.Volume;
						}
						else
						{
							double ticks = t1 - t0 + 1;
							for (int r = ra; r <= rb; r++)
							{
								long bottom = (rowMin + r) * eff;
								long top = bottom + eff - 1;
								long overlap = Math.Min(t1, top) - Math.Max(t0, bottom) + 1;
								if (overlap > 0)
									vol[r] += m.Volume * overlap / ticks;
							}
						}
					}
				}

				double maxVol = 0;
				for (int r = 0; r < n; r++)
				{
					if (vol[r] > maxVol)
						maxVol = vol[r];
				}

				b.Vol = vol;
				b.MaxVol = maxVol;
				b.TotalVol = totalVol;
				ValueArea(vol, 0, n - 1, valueFraction, out b.VPoc, out b.VVah, out b.VVal);
			}

			// Single-width runs at the extremes: tails when long enough.
			int topRun = 0;
			for (int r = n - 1; r >= 0 && counts[r] == 1; r--)
				topRun++;
			int bottomRun = 0;
			for (int r = 0; r < n && counts[r] == 1; r++)
				bottomRun++;
			if (topRun >= n || bottomRun >= n)
			{
				topRun = 0;
				bottomRun = 0;
			}

			int minTail = Math.Max(1, MinTailRows);
			b.TopRun = topRun;
			b.BottomRun = bottomRun;
			b.TopTail = topRun >= minTail ? topRun : 0;
			b.BottomTail = bottomRun >= minTail ? bottomRun : 0;
			b.PoorHigh = n > 1 && counts[n - 1] >= 2;
			b.PoorLow = n > 1 && counts[0] >= 2;

			// Double distribution: two bodies of trade separated by a run of single prints inside the profile.
			bool doubleDistribution = false;
			int singleRun = 0;
			for (int r = bottomRun; r <= n - topRun; r++)
			{
				if (r < n - topRun && counts[r] == 1)
				{
					singleRun++;
					continue;
				}
				if (singleRun >= 2)
				{
					int below = rowStart[r - singleRun];
					int above = total - rowStart[r];
					if (below >= total * 0.2 && above >= total * 0.2)
						doubleDistribution = true;
				}
				singleRun = 0;
			}

			// Open of the first session and close of the last one.
			TpoSession firstSession = p.Sessions[0];
			TpoSession lastSession = p.Sessions[sessionCount - 1];
			b.OpenRow = ClampRow(FloorDiv(ToTick(firstSession.Open), eff) - rowMin, n);
			b.CloseRow = ClampRow(FloorDiv(ToTick(lastSession.Close), eff) - rowMin, n);

			// Initial balance, range extension, day type and open type describe the most recent session of the profile.
			if (lastSession.HasIb && lastSession.IbHigh >= lastSession.IbLow)
			{
				b.HasIb = true;
				b.IbHigh = lastSession.IbHigh;
				b.IbLow = lastSession.IbLow;
				b.ExtensionUp = Math.Max(0, lastSession.High - lastSession.IbHigh);
				b.ExtensionDown = Math.Max(0, lastSession.IbLow - lastSession.Low);

				if (DayType)
				{
					double sum = 0;
					int used = 0;
					for (int k = Math.Max(0, lastSession.Index - 20); k < lastSession.Index && k < sessions.Count; k++)
					{
						if (sessions[k].High >= sessions[k].Low)
						{
							sum += sessions[k].High - sessions[k].Low;
							used++;
						}
					}
					double averageRange = used > 0 ? sum / used : 0;
					b.DayTypeText = ClassifyDay(lastSession.High, lastSession.Low, lastSession.Close, lastSession.IbHigh, lastSession.IbLow,
						tickSize, averageRange, doubleDistribution);
				}
			}

			if (OpenType && lastSession.HasOpening)
			{
				bool hasPrior = lastSession.Index > 0 && lastSession.Index - 1 < sessions.Count;
				double priorHigh = hasPrior ? sessions[lastSession.Index - 1].High : 0;
				double priorLow = hasPrior ? sessions[lastSession.Index - 1].Low : 0;
				b.OpenTypeText = ClassifyOpen(lastSession.Open, lastSession.OpeningHigh, lastSession.OpeningLow, lastSession.OpeningClose,
					hasPrior, priorHigh, priorLow);
			}

			return b;
		}

		// Rule-of-thumb day type from the initial balance, the range extension and where the session closed.
		private static string ClassifyDay(double high, double low, double close, double ibHigh, double ibLow,
			double tick, double averageRange, bool doubleDistribution)
		{
			double range = high - low;
			double ib = ibHigh - ibLow;
			if (range <= 0 || ib <= 0)
				return string.Empty;

			double threshold = Math.Max(tick, ib * 0.05);
			bool up = high - ibHigh > threshold;
			bool down = ibLow - low > threshold;
			double closePosition = (close - low) / range;

			if (up && down)
				return (closePosition >= 0.75 || closePosition <= 0.25) ? "Neutral (extreme)" : "Neutral (center)";

			if (!up && !down)
				return (averageRange > 0 && range < averageRange * 0.6) ? "Non-trend" : "Normal";

			string direction = up ? " up" : " down";
			double ratio = range / ib;
			if (ratio >= 2.0)
			{
				bool closesAtExtreme = up ? closePosition >= 0.75 : closePosition <= 0.25;
				if (closesAtExtreme)
					return (doubleDistribution ? "Double distribution trend" : "Trend") + direction;
				return "Normal variation" + direction;
			}
			if (ratio <= 1.15)
				return "Normal";
			return "Normal variation" + direction;
		}

		// Rule-of-thumb open type from the first 30 minutes: where the open sits in that range and where the 30 minutes ended.
		private static string ClassifyOpen(double open, double openingHigh, double openingLow, double openingClose,
			bool hasPrior, double priorHigh, double priorLow)
		{
			double range = openingHigh - openingLow;
			if (range <= 0)
				return string.Empty;

			double openPosition = (open - openingLow) / range;
			double closePosition = (openingClose - openingLow) / range;
			string type;

			if (openPosition <= 0.10 && closePosition >= 0.70)
				type = "Open-Drive up";
			else if (openPosition >= 0.90 && closePosition <= 0.30)
				type = "Open-Drive down";
			else if (openPosition <= 0.30 && closePosition >= 0.70)
				type = "Open-Test-Drive up";
			else if (openPosition >= 0.70 && closePosition <= 0.30)
				type = "Open-Test-Drive down";
			else if (closePosition >= 0.70)
				type = "Open-Rejection-Reverse up";
			else if (closePosition <= 0.30)
				type = "Open-Rejection-Reverse down";
			else
				type = "Open-Auction";

			// In range / out of range belongs to the Open-Auction: did the session open inside the prior session's range?
			if (type == "Open-Auction" && hasPrior && priorHigh >= priorLow)
				type += (open <= priorHigh && open >= priorLow) ? " (in range)" : " (out of range)";
			return type;
		}

		private static byte ShadeOf(double fraction)
		{
			if (double.IsNaN(fraction) || fraction < 0)
				fraction = 0;
			if (fraction > 1)
				fraction = 1;
			return (byte)Math.Round(fraction * (GradientSteps - 1));
		}

		// Point of control = widest row (ties go to the row nearest the middle).
		// Value area grows from the point of control two rows at a time, taking the heavier side.
		// Only rows first..last are considered: the rows traded so far.
		private static void ValueArea(double[] values, int first, int last, double fraction, out int poc, out int vah, out int val)
		{
			poc = first;
			vah = first;
			val = first;
			if (values.Length == 0 || last < first)
				return;

			double middle = (first + last) / 2.0;
			double best = double.MinValue;
			double total = 0;

			for (int i = first; i <= last; i++)
			{
				total += values[i];
				if (values[i] > best || (values[i] == best && Math.Abs(i - middle) < Math.Abs(poc - middle)))
				{
					best = values[i];
					poc = i;
				}
			}

			int up = poc;
			int down = poc;
			double target = total * fraction;
			double accumulated = values[poc];

			while (accumulated < target && (up < last || down > first))
			{
				double upSum = 0;
				int upRows = 0;
				for (int k = 1; k <= 2 && up + k <= last; k++)
				{
					upSum += values[up + k];
					upRows++;
				}

				double downSum = 0;
				int downRows = 0;
				for (int k = 1; k <= 2 && down - k >= first; k++)
				{
					downSum += values[down - k];
					downRows++;
				}

				if (upRows == 0)
				{
					accumulated += downSum;
					down -= downRows;
				}
				else if (downRows == 0)
				{
					accumulated += upSum;
					up += upRows;
				}
				else if (upSum > downSum)
				{
					accumulated += upSum;
					up += upRows;
				}
				else if (downSum > upSum)
				{
					accumulated += downSum;
					down -= downRows;
				}
				else
				{
					accumulated += upSum + downSum;
					up += upRows;
					down -= downRows;
				}
			}

			vah = up;
			val = down;
		}

		#endregion

		#region Naked levels

		// Called under lock(sync). Looks for the first minute after the profile that trades at each naked level.
		private void UpdateNaked(TpoProfile p)
		{
			TpoBuilt b = p.Built;
			if (b == null || b.RowCount == 0)
				return;

			// Nothing has traded and the profile is the same since the last look: the answer is the same too.
			if (p.Naked != null && p.NakedStamp == dataStamp && object.ReferenceEquals(p.NakedFor, b))
				return;
			p.NakedStamp = dataStamp;

			if (p.Naked == null)
			{
				p.Naked = new NakedLevel[LevelCount];
				for (int i = 0; i < LevelCount; i++)
					p.Naked[i] = new NakedLevel();
			}

			if (!object.ReferenceEquals(p.NakedFor, b))
			{
				for (int i = 0; i < LevelCount; i++)
					p.Naked[i].Active = false;
				p.NakedFor = b;
			}

			int start = p.Sessions[p.Sessions.Count - 1].EndPos;
			int count = allMinutes.Count;

			for (int lv = 0; lv < LevelCount; lv++)
			{
				NakedLevel level = p.Naked[lv];
				double lineValue;
				double price;

				if (!NakedTracked(lv) || !LevelValues(b, lv, out lineValue, out price))
				{
					level.Active = false;
					continue;
				}

				if (!level.Active || level.Price != price || level.Start != start)
				{
					level.Active = true;
					level.Price = price;
					level.Start = start;
					level.Pos = start;
					level.Touched = false;
					level.TouchBarIdx = -1;
				}

				level.HasAfter = count > start;
				if (!level.Touched)
					ScanNaked(level);
			}
		}

		private void ScanNaked(NakedLevel level)
		{
			int count = allMinutes.Count;
			double eps = tickSize * 0.01;
			int i = level.Pos;

			while (i < count)
			{
				// Skip a whole chunk when the level lies outside everything traded in it.
				if (i % ChunkSize == 0 && i + ChunkSize <= count)
				{
					int chunk = i / ChunkSize;
					if (level.Price > chunkHigh[chunk] + eps || level.Price < chunkLow[chunk] - eps)
					{
						i += ChunkSize;
						continue;
					}
				}

				RangeRec m = allMinutes[i];
				if (m.Low - eps <= level.Price && level.Price <= m.High + eps)
				{
					level.Touched = true;
					level.TouchTime = m.Time;
					level.Pos = i;
					return;
				}
				i++;
			}

			// The newest minute may still be forming: look at it again next time.
			level.Pos = Math.Max(level.Start, count - 1);
		}

		#endregion

		#region Mouse: profile menu on double-click or right-click, Shift+Click merges, Ctrl+Shift+Click restores

		// What the menu needs to know about the profile under the mouse.
		private sealed class ProfileRef
		{
			public string IdentityKey;
			public string FirstKey;
			public bool CanMerge;
			public bool IsMerged;
			public bool IsExpanded;
			public readonly List<string> JoinedKeys = new List<string>();
		}

		private void AttachMouse(ChartControl cc)
		{
			if (cc == null || mouseAttached)
				return;
			cc.PreviewMouseLeftButtonDown += OnChartMouseDown;
			cc.ContextMenuOpening += OnChartContextMenuOpening;
			cc.ContextMenuClosing += OnChartContextMenuClosing;
			mouseAttached = true;
		}

		private void DetachMouse(ChartControl cc)
		{
			if (cc == null || !mouseAttached)
				return;
			cc.PreviewMouseLeftButtonDown -= OnChartMouseDown;
			cc.ContextMenuOpening -= OnChartContextMenuOpening;
			cc.ContextMenuClosing -= OnChartContextMenuClosing;
			RemoveAddedMenuItems(cc);
			mouseAttached = false;
		}

		// position is in WPF units relative to the chart; rendering uses device pixels.
		private ProfileRef ProfileAt(ChartControl cc, System.Windows.Point position)
		{
			double scaleX = 1.0;
			double scaleY = 1.0;
			PresentationSource source = PresentationSource.FromVisual(cc);
			if (source != null && source.CompositionTarget != null)
			{
				scaleX = source.CompositionTarget.TransformToDevice.M11;
				scaleY = source.CompositionTarget.TransformToDevice.M22;
			}
			float x = (float)(position.X * scaleX);
			float y = (float)(position.Y * scaleY);

			if (y < clipTop || y > clipBottom)
				return null;

			lock (sync)
			{
				TpoProfile hit = null;
				for (int i = 0; i < profiles.Count; i++)
				{
					TpoProfile p = profiles[i];
					if (p.HitValid && x >= p.HitLeft && x <= p.HitRight)
						hit = p;
				}

				if (hit == null || hit.Sessions.Count == 0 || hit.SpanStart == null)
					return null;

				bool rolling = RollingCompositeSessions > 1;
				ProfileRef target = new ProfileRef();
				target.IdentityKey = hit.SpanStart.Key;
				target.FirstKey = hit.Sessions[0].Key;
				target.CanMerge = !rolling && hit.Sessions[0].Index > 0;
				if (!rolling)
				{
					for (int k = 1; k < hit.Sessions.Count; k++)
					{
						string key = hit.Sessions[k].Key;
						target.JoinedKeys.Add(key);
						if (mergedKeys.Contains(key))
							target.IsMerged = true;
					}
				}
				target.IsExpanded = expandedKeys.Contains(target.IdentityKey);
				return target;
			}
		}

		private bool MergeProfile(ProfileRef target)
		{
			if (!target.CanMerge)
				return false;

			lock (sync)
			{
				if (!mergedKeys.Add(target.FirstKey))
					return false;
				MergedSessions = JoinKeys(mergedKeys);
				groupingDirty = true;
				return true;
			}
		}

		private bool ExpandProfile(ProfileRef target)
		{
			lock (sync)
			{
				if (!expandedKeys.Add(target.IdentityKey))
					return false;
				ExpandedSessions = JoinKeys(expandedKeys);
				return true;
			}
		}

		// Undoes both: the profile is collapsed again and the sessions merged into it are drawn on their own.
		private bool RestoreProfile(ProfileRef target)
		{
			lock (sync)
			{
				bool changed = expandedKeys.Remove(target.IdentityKey);
				for (int k = 0; k < target.JoinedKeys.Count; k++)
				{
					if (mergedKeys.Remove(target.JoinedKeys[k]))
						changed = true;
					if (expandedKeys.Remove(target.JoinedKeys[k]))
						changed = true;
				}

				if (changed)
				{
					MergedSessions = JoinKeys(mergedKeys);
					ExpandedSessions = JoinKeys(expandedKeys);
					groupingDirty = true;
				}
				return changed;
			}
		}

		private System.Windows.Controls.MenuItem MakeMenuItem(string header, Func<bool> action)
		{
			System.Windows.Controls.MenuItem item = new System.Windows.Controls.MenuItem();
			item.Header = header;
			item.Click += (sender, args) =>
			{
				try
				{
					if (action())
						ForceRefresh();
				}
				catch (Exception)
				{
				}
			};
			return item;
		}

		private List<System.Windows.Controls.MenuItem> BuildMenuItems(ProfileRef target)
		{
			List<System.Windows.Controls.MenuItem> items = new List<System.Windows.Controls.MenuItem>();
			if (!target.IsExpanded)
				items.Add(MakeMenuItem("Expand this profile", () => ExpandProfile(target)));
			if (target.CanMerge)
				items.Add(MakeMenuItem("Merge with previous profile", () => MergeProfile(target)));
			if (target.IsExpanded || target.IsMerged)
				items.Add(MakeMenuItem("Return profile to original", () => RestoreProfile(target)));
			return items;
		}

		private void OnChartMouseDown(object sender, MouseButtonEventArgs e)
		{
			try
			{
				if (!EnableClickMerge)
					return;

				ModifierKeys modifiers = Keyboard.Modifiers;
				bool shift = (modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
				bool control = (modifiers & ModifierKeys.Control) == ModifierKeys.Control;
				if (!shift && e.ClickCount != 2)
					return;

				ChartControl cc = ChartControl;
				if (cc == null)
					return;

				ProfileRef target = ProfileAt(cc, e.GetPosition(cc));
				if (target == null)
					return;

				if (shift)
				{
					bool changed = control ? RestoreProfile(target) : MergeProfile(target);
					if (changed)
					{
						e.Handled = true;
						ForceRefresh();
					}
					return;
				}

				// Double-click on a profile: open the profile menu at the mouse.
				List<System.Windows.Controls.MenuItem> items = BuildMenuItems(target);
				if (items.Count == 0)
					return;

				System.Windows.Controls.ContextMenu menu = new System.Windows.Controls.ContextMenu();
				for (int i = 0; i < items.Count; i++)
					menu.Items.Add(items[i]);
				menu.PlacementTarget = cc;
				menu.IsOpen = true;
				e.Handled = true;
			}
			catch (Exception)
			{
			}
		}

		// Right-click on a profile: the same entries are added to the chart's own menu while it is open.
		private void OnChartContextMenuOpening(object sender, System.Windows.Controls.ContextMenuEventArgs e)
		{
			try
			{
				ChartControl cc = ChartControl;
				if (!EnableClickMerge || cc == null || cc.ContextMenu == null)
					return;

				RemoveAddedMenuItems(cc);

				ProfileRef target = ProfileAt(cc, Mouse.GetPosition(cc));
				if (target == null)
					return;

				List<System.Windows.Controls.MenuItem> items = BuildMenuItems(target);
				if (items.Count == 0)
					return;

				System.Windows.Controls.Separator separator = new System.Windows.Controls.Separator();
				cc.ContextMenu.Items.Add(separator);
				addedMenuItems.Add(separator);
				for (int i = 0; i < items.Count; i++)
				{
					cc.ContextMenu.Items.Add(items[i]);
					addedMenuItems.Add(items[i]);
				}
			}
			catch (Exception)
			{
			}
		}

		private void OnChartContextMenuClosing(object sender, System.Windows.Controls.ContextMenuEventArgs e)
		{
			try
			{
				RemoveAddedMenuItems(ChartControl);
			}
			catch (Exception)
			{
			}
		}

		private void RemoveAddedMenuItems(ChartControl cc)
		{
			if (addedMenuItems.Count == 0)
				return;

			if (cc != null && cc.ContextMenu != null)
			{
				for (int i = 0; i < addedMenuItems.Count; i++)
				{
					if (cc.ContextMenu.Items.Contains(addedMenuItems[i]))
						cc.ContextMenu.Items.Remove(addedMenuItems[i]);
				}
			}
			addedMenuItems.Clear();
		}

		#endregion

		#region Rendering resources

		public override void OnRenderTargetChanged()
		{
			DisposeDeviceResources();
		}

		private static Color ColorOf(Brush brush, Color fallback, out double opacity)
		{
			opacity = 1.0;
			try
			{
				SolidColorBrush solid = brush as SolidColorBrush;
				if (solid == null)
					return fallback;
				opacity = solid.Opacity;
				return solid.Color;
			}
			catch (Exception)
			{
				opacity = 1.0;
				return fallback;
			}
		}

		private static float[] Channels(Brush brush, Color fallback)
		{
			double opacity;
			Color c = ColorOf(brush, fallback, out opacity);
			return new float[] { c.R / 255f, c.G / 255f, c.B / 255f, (float)(c.A / 255.0 * opacity) };
		}

		private static float[] Channels(byte r, byte g, byte b)
		{
			return new float[] { r / 255f, g / 255f, b / 255f, 1f };
		}

		private SharpDX.Direct2D1.SolidColorBrush MakeBrush(float[] channels, float opacity)
		{
			float alpha = channels[3] * opacity;
			if (alpha < 0f) alpha = 0f;
			if (alpha > 1f) alpha = 1f;
			return new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color4(channels[0], channels[1], channels[2], alpha));
		}

		private SharpDX.Direct2D1.SolidColorBrush MakeBrush(Brush brush, float opacity)
		{
			return MakeBrush(Channels(brush, Colors.Gray), opacity);
		}

		// Evenly spaced colour stops blended into GradientSteps brushes.
		private SharpDX.Direct2D1.SolidColorBrush[] MakeGradient(float[][] stops, float opacity)
		{
			SharpDX.Direct2D1.SolidColorBrush[] result = new SharpDX.Direct2D1.SolidColorBrush[GradientSteps];
			int segments = Math.Max(1, stops.Length - 1);

			for (int i = 0; i < GradientSteps; i++)
			{
				float position = (float)i / (GradientSteps - 1) * segments;
				int index = (int)Math.Floor(position);
				if (index > segments - 1)
					index = segments - 1;
				float u = position - index;

				float[] lower = stops[index];
				float[] upper = stops[Math.Min(index + 1, stops.Length - 1)];
				float[] mixed = new float[4];
				for (int k = 0; k < 4; k++)
					mixed[k] = lower[k] + (upper[k] - lower[k]) * u;
				result[i] = MakeBrush(mixed, opacity);
			}
			return result;
		}

		// Block colours of the built-in palettes: period (early, middle, late), heat (narrow, medium, wide) and solid.
		private void PaletteChannels(out float[][] period, out float[][] heat, out float[] solid)
		{
			switch (Palette)
			{
				case TpoMarketProfileTypes.TpoPalette.DodgerBlue:
					// One colour for every block; Heat mode shades it from dark to light.
					period = new float[][] { Channels(30, 144, 255), Channels(30, 144, 255) };
					heat = new float[][] { Channels(14, 62, 120), Channels(30, 144, 255), Channels(170, 215, 255) };
					solid = Channels(30, 144, 255);
					break;
				case TpoMarketProfileTypes.TpoPalette.BlueRed:
					period = new float[][] { Channels(58, 94, 168), Channels(64, 128, 236), Channels(160, 56, 68), Channels(220, 72, 84) };
					heat = period;
					solid = Channels(64, 128, 236);
					break;
				case TpoMarketProfileTypes.TpoPalette.Ocean:
					period = new float[][] { Channels(10, 80, 120), Channels(60, 160, 185), Channels(180, 230, 220) };
					heat = period;
					solid = Channels(60, 160, 185);
					break;
				case TpoMarketProfileTypes.TpoPalette.Sunset:
					period = new float[][] { Channels(120, 20, 100), Channels(235, 100, 50), Channels(255, 210, 100) };
					heat = new float[][] { Channels(255, 210, 100), Channels(235, 100, 50), Channels(120, 20, 100) };
					solid = Channels(235, 100, 50);
					break;
				case TpoMarketProfileTypes.TpoPalette.Forest:
					period = new float[][] { Channels(25, 85, 50), Channels(95, 155, 75), Channels(215, 200, 110) };
					heat = period;
					solid = Channels(95, 155, 75);
					break;
				case TpoMarketProfileTypes.TpoPalette.Mono:
					period = new float[][] { Channels(95, 95, 95), Channels(155, 155, 155), Channels(225, 225, 225) };
					heat = period;
					solid = Channels(155, 155, 155);
					break;
				default:
					period = new float[][] { Channels(220, 20, 60), Channels(240, 230, 140), Channels(60, 179, 113) };
					heat = new float[][] { Channels(70, 130, 180), Channels(240, 230, 140), Channels(220, 20, 60) };
					solid = Channels(70, 130, 180);
					break;
			}
		}

		private void CreateDeviceResources()
		{
			// Brushes belong to one render target: start clean and remember which target they were made for.
			DisposeDeviceResources();
			dxTarget = RenderTarget;

			float blockOpacity = Math.Max(0, Math.Min(100, BlockOpacity)) / 100f;
			float volumeOpacity = Math.Max(0, Math.Min(100, VolumeOpacity)) / 100f;
			float fillOpacity = Math.Max(0, Math.Min(100, ValueAreaFillOpacity)) / 100f;

			float[][] period;
			float[][] heat;
			float[] solid;
			if (CustomColors)
			{
				period = new float[][] { Channels(PeriodEarlyBrush, Colors.Crimson), Channels(PeriodMidBrush, Colors.Khaki), Channels(PeriodLateBrush, Colors.SeaGreen) };
				heat = new float[][] { Channels(HeatLowBrush, Colors.SteelBlue), Channels(HeatMidBrush, Colors.Khaki), Channels(HeatHighBrush, Colors.Crimson) };
				solid = Channels(SolidBrush, Colors.SteelBlue);
			}
			else if (Palette == TpoMarketProfileTypes.TpoPalette.BySession)
			{
				// One colour per kind of session: dodger blue for RTH, green for ETH, unless changed in the Colors group.
				float[] own = SessionHours == TpoMarketProfileTypes.TpoSessionHours.ETH
					? Channels(EthBlockBrush, Colors.LimeGreen)
					: Channels(RthBlockBrush, Colors.DodgerBlue);
				float[] dark = new float[] { own[0] * 0.4f, own[1] * 0.4f, own[2] * 0.4f, own[3] };
				float[] light = new float[] { own[0] + (1f - own[0]) * 0.6f, own[1] + (1f - own[1]) * 0.6f, own[2] + (1f - own[2]) * 0.6f, own[3] };
				period = new float[][] { own, own };
				heat = new float[][] { dark, own, light };
				solid = own;
			}
			else
			{
				PaletteChannels(out period, out heat, out solid);
			}

			dxPeriod = MakeGradient(period, blockOpacity);
			dxHeat = MakeGradient(heat, blockOpacity);
			dxSolid = MakeBrush(solid, blockOpacity);

			float[] volumeLow = Channels(VolumeOutsideBrush, Colors.Gray);
			float[] volumeHigh = Channels(VolumeBrush, Colors.SteelBlue);
			dxVolGrade = MakeGradient(new float[][] { volumeLow, volumeHigh }, volumeOpacity);

			Brush[] levelBrushes = new Brush[] { PocLineBrush, VahBrush, ValBrush, PoorHighBrush, PoorLowBrush, VolumePocBrush, VolumeVahBrush, VolumeValBrush };
			dxLevel = new SharpDX.Direct2D1.SolidColorBrush[LevelCount];
			for (int lv = 0; lv < LevelCount; lv++)
			{
				float[] c = Channels(levelBrushes[lv], Colors.Gray);
				dxLevel[lv] = MakeBrush(c, 1f);
				levelIsLight[lv] = 0.299f * c[0] + 0.587f * c[1] + 0.114f * c[2] > 0.55f;
			}

			dxOutside = MakeBrush(OutsideValueAreaBrush, blockOpacity);
			dxPocRow = MakeBrush(PocRowBrush, blockOpacity);
			dxSingle = MakeBrush(SinglePrintBrush, blockOpacity);
			dxBuyTail = MakeBrush(BuyingTailBrush, blockOpacity);
			dxSellTail = MakeBrush(SellingTailBrush, blockOpacity);
			dxLetter = MakeBrush(LetterBrush, 1f);
			dxOpenBlock = MakeBrush(OpenBlockBrush, 1f);
			dxCloseBlock = MakeBrush(CloseBlockBrush, 1f);
			dxVaFill = MakeBrush(ValueAreaFillBrush, fillOpacity);
			dxIb = MakeBrush(InitialBalanceBrush, 1f);
			dxRangeExt = MakeBrush(RangeExtensionBrush, 1f);
			dxDevPoc = MakeBrush(DevelopingPocBrush, 1f);
			dxDevVa = MakeBrush(DevelopingVaBrush, 1f);
			dxOtfUp = MakeBrush(OtfUpBrush, 1f);
			dxOtfDown = MakeBrush(OtfDownBrush, 1f);
			dxVolIn = MakeBrush(VolumeBrush, volumeOpacity);
			dxVolOut = MakeBrush(VolumeOutsideBrush, volumeOpacity);
			dxText = MakeBrush(TextBrush, 1f);
			dxPriorHigh = MakeBrush(PriorHighBrush, 1f);
			dxGapLine = MakeBrush(GapLineBrush, 1f);
			dxGapArea = MakeBrush(GapAreaBrush, Math.Max(0, Math.Min(100, GapAreaOpacity)) / 100f);
			dxPriorLow = MakeBrush(PriorLowBrush, 1f);
			dxDayType = MakeBrush(DayTypeBrush, 1f);
			dxOpenType = MakeBrush(OpenTypeBrush, 1f);
			dxStatsBack = MakeBrush(StatisticsBackgroundBrush, 0.75f);
			dxTagDark = MakeBrush(Channels(0, 0, 0), 1f);
			dxTagLight = MakeBrush(Channels(255, 255, 255), 1f);

			if (dashStyle == null)
			{
				SharpDX.Direct2D1.StrokeStyleProperties dashProperties = new SharpDX.Direct2D1.StrokeStyleProperties();
				dashProperties.DashStyle = SharpDX.Direct2D1.DashStyle.Dash;
				dashStyle = new SharpDX.Direct2D1.StrokeStyle(NinjaTrader.Core.Globals.D2DFactory, dashProperties);
			}
			if (dotStyle == null)
			{
				SharpDX.Direct2D1.StrokeStyleProperties dotProperties = new SharpDX.Direct2D1.StrokeStyleProperties();
				dotProperties.DashStyle = SharpDX.Direct2D1.DashStyle.Dot;
				dotStyle = new SharpDX.Direct2D1.StrokeStyle(NinjaTrader.Core.Globals.D2DFactory, dotProperties);
			}
			if (labelFormat == null)
				labelFormat = new SharpDX.DirectWrite.TextFormat(NinjaTrader.Core.Globals.DirectWriteFactory, "Arial", (float)Math.Max(6, LabelFontSize));

			dxReady = true;
		}

		private static void DisposeBrush(ref SharpDX.Direct2D1.SolidColorBrush brush)
		{
			if (brush != null)
			{
				try
				{
					if (!brush.IsDisposed)
						brush.Dispose();
				}
				catch (Exception)
				{
				}
				brush = null;
			}
		}

		private static void DisposeBrushes(ref SharpDX.Direct2D1.SolidColorBrush[] brushes)
		{
			if (brushes != null)
			{
				for (int i = 0; i < brushes.Length; i++)
					DisposeBrush(ref brushes[i]);
				brushes = null;
			}
		}

		private void DisposeDeviceResources()
		{
			dxReady = false;
			dxTarget = null;
			DisposeBrushes(ref dxPeriod);
			DisposeBrushes(ref dxHeat);
			DisposeBrushes(ref dxVolGrade);
			DisposeBrushes(ref dxLevel);
			DisposeBrush(ref dxSolid);
			DisposeBrush(ref dxOutside);
			DisposeBrush(ref dxPocRow);
			DisposeBrush(ref dxSingle);
			DisposeBrush(ref dxBuyTail);
			DisposeBrush(ref dxSellTail);
			DisposeBrush(ref dxLetter);
			DisposeBrush(ref dxOpenBlock);
			DisposeBrush(ref dxCloseBlock);
			DisposeBrush(ref dxVaFill);
			DisposeBrush(ref dxIb);
			DisposeBrush(ref dxRangeExt);
			DisposeBrush(ref dxDevPoc);
			DisposeBrush(ref dxDevVa);
			DisposeBrush(ref dxOtfUp);
			DisposeBrush(ref dxOtfDown);
			DisposeBrush(ref dxVolIn);
			DisposeBrush(ref dxVolOut);
			DisposeBrush(ref dxText);
			DisposeBrush(ref dxPriorHigh);
			DisposeBrush(ref dxGapLine);
			DisposeBrush(ref dxGapArea);
			DisposeBrush(ref dxPriorLow);
			DisposeBrush(ref dxDayType);
			DisposeBrush(ref dxOpenType);
			DisposeBrush(ref dxStatsBack);
			DisposeBrush(ref dxTagDark);
			DisposeBrush(ref dxTagLight);
		}

		private void DisposeAllResources()
		{
			DisposeDeviceResources();

			try
			{
				foreach (KeyValuePair<int, Glyph> pair in glyphs)
				{
					if (pair.Value != null && pair.Value.Layout != null && !pair.Value.Layout.IsDisposed)
						pair.Value.Layout.Dispose();
				}
				glyphs.Clear();
				ClearLabelCache();

				for (int i = 0; i < letterFormats.Length; i++)
				{
					if (letterFormats[i] != null)
					{
						if (!letterFormats[i].IsDisposed)
							letterFormats[i].Dispose();
						letterFormats[i] = null;
					}
				}

				if (labelFormat != null)
				{
					if (!labelFormat.IsDisposed)
						labelFormat.Dispose();
					labelFormat = null;
				}
				if (dashStyle != null)
				{
					if (!dashStyle.IsDisposed)
						dashStyle.Dispose();
					dashStyle = null;
				}
				if (dotStyle != null)
				{
					if (!dotStyle.IsDisposed)
						dotStyle.Dispose();
					dotStyle = null;
				}
			}
			catch (Exception)
			{
			}
		}

		private SharpDX.DirectWrite.TextFormat SmallFormat(int size)
		{
			if (size < 6) size = 6;
			if (size > MaxLetterFont) size = MaxLetterFont;
			if (letterFormats[size] == null)
				letterFormats[size] = new SharpDX.DirectWrite.TextFormat(NinjaTrader.Core.Globals.DirectWriteFactory, "Arial", (float)size);
			return letterFormats[size];
		}

		// The finished layout of a label, made once and kept. Returns null for a font that is not one of ours.
		private Glyph GetLabel(string text, SharpDX.DirectWrite.TextFormat format)
		{
			if (format == null)
				return null;

			int slot = -1;
			if (object.ReferenceEquals(format, labelFormat))
			{
				slot = 0;
			}
			else
			{
				for (int i = 0; i < letterFormats.Length; i++)
				{
					if (object.ReferenceEquals(format, letterFormats[i]))
					{
						slot = i + 1;
						break;
					}
				}
			}
			if (slot < 0)
				return null;

			Dictionary<string, Glyph> cache = labelCache[slot];
			if (cache == null)
			{
				cache = new Dictionary<string, Glyph>(StringComparer.Ordinal);
				labelCache[slot] = cache;
			}

			Glyph label;
			if (cache.TryGetValue(text, out label))
				return label;

			label = new Glyph();
			label.Layout = new SharpDX.DirectWrite.TextLayout(NinjaTrader.Core.Globals.DirectWriteFactory, text, format, 2000f, 600f);
			label.W = label.Layout.Metrics.Width;
			label.H = label.Layout.Metrics.Height;
			cache[text] = label;
			labelCacheCount++;
			return label;
		}

		private void ClearLabelCache()
		{
			for (int i = 0; i < labelCache.Length; i++)
			{
				Dictionary<string, Glyph> cache = labelCache[i];
				if (cache == null)
					continue;
				foreach (KeyValuePair<string, Glyph> pair in cache)
				{
					try
					{
						if (pair.Value != null && pair.Value.Layout != null && !pair.Value.Layout.IsDisposed)
							pair.Value.Layout.Dispose();
					}
					catch (Exception)
					{
					}
				}
				cache.Clear();
			}
			labelCacheCount = 0;
		}

		private Glyph GetGlyph(int size, char letter)
		{
			int key = size * 1000 + (int)letter;
			Glyph glyph;
			if (glyphs.TryGetValue(key, out glyph))
				return glyph;

			glyph = new Glyph();
			glyph.Layout = new SharpDX.DirectWrite.TextLayout(NinjaTrader.Core.Globals.DirectWriteFactory, letter.ToString(), SmallFormat(size), 200f, 200f);
			glyph.W = glyph.Layout.Metrics.Width;
			glyph.H = glyph.Layout.Metrics.Height;
			glyphs[key] = glyph;
			return glyph;
		}

		#endregion

		#region Drawing helpers (everything is kept inside the chart panel)

		private void FillRect(float x, float y, float w, float h, SharpDX.Direct2D1.Brush brush)
		{
			float x0 = x < clipLeft ? clipLeft : x;
			float y0 = y < clipTop ? clipTop : y;
			float x1 = x + w > clipRight ? clipRight : x + w;
			float y1 = y + h > clipBottom ? clipBottom : y + h;
			if (x1 <= x0 || y1 <= y0)
				return;
			RenderTarget.FillRectangle(new SharpDX.RectangleF(x0, y0, x1 - x0, y1 - y0), brush);
		}

		private void HLine(float x0, float x1, float y, SharpDX.Direct2D1.Brush brush, float width, SharpDX.Direct2D1.StrokeStyle style)
		{
			if (y < clipTop || y > clipBottom)
				return;
			if (x0 < clipLeft) x0 = clipLeft;
			if (x1 > clipRight) x1 = clipRight;
			if (x1 <= x0)
				return;

			if (style == null)
				RenderTarget.DrawLine(new SharpDX.Vector2(x0, y), new SharpDX.Vector2(x1, y), brush, width);
			else
				RenderTarget.DrawLine(new SharpDX.Vector2(x0, y), new SharpDX.Vector2(x1, y), brush, width, style);
		}

		private void VLine(float x, float y0, float y1, SharpDX.Direct2D1.Brush brush, float width)
		{
			VLine(x, y0, y1, brush, width, null);
		}

		private void VLine(float x, float y0, float y1, SharpDX.Direct2D1.Brush brush, float width, SharpDX.Direct2D1.StrokeStyle style)
		{
			if (x < clipLeft || x > clipRight)
				return;
			if (y1 < y0)
			{
				float swap = y0;
				y0 = y1;
				y1 = swap;
			}
			if (y0 < clipTop) y0 = clipTop;
			if (y1 > clipBottom) y1 = clipBottom;
			if (y1 <= y0)
				return;
			if (style == null)
				RenderTarget.DrawLine(new SharpDX.Vector2(x, y0), new SharpDX.Vector2(x, y1), brush, width);
			else
				RenderTarget.DrawLine(new SharpDX.Vector2(x, y0), new SharpDX.Vector2(x, y1), brush, width, style);
		}

		private bool TextFits(float x, float y, float w, float h)
		{
			return x >= clipLeft && y >= clipTop && x + w <= clipRight && y + h <= clipBottom;
		}

		// alignRight: text ends at x instead of starting there.
		// vertical: 0 = centred on y, -1 = above y, 1 = below y.
		private void DrawLabel(string text, float x, float y, SharpDX.Direct2D1.Brush brush, bool alignRight, int vertical, SharpDX.DirectWrite.TextFormat format)
		{
			if (string.IsNullOrEmpty(text))
				return;

			Glyph label = GetLabel(text, format);
			if (label == null)
				return;

			float w = label.W;
			float h = label.H;
			float tx = alignRight ? x - w : x;
			float ty;
			if (vertical < 0)
				ty = y - h - 1f;
			else if (vertical > 0)
				ty = y + 1f;
			else
				ty = y - h * 0.5f;

			if (TextFits(tx, ty, w, h))
				RenderTarget.DrawTextLayout(new SharpDX.Vector2(tx, ty), label.Layout, brush);
		}

		// A filled price tag against the right edge of the panel, next to the price scale.
		private void DrawScaleTag(int level, double price, float y)
		{
			Glyph label = GetLabel(FormatPrice(price), labelFormat);
			if (label == null)
				return;

			float w = label.W + 8f;
			float h = label.H + 2f;
			float x = clipRight - w;
			float top = y - h * 0.5f;

			if (!TextFits(x, top, w, h))
				return;

			RenderTarget.FillRectangle(new SharpDX.RectangleF(x, top, w, h), dxLevel[level]);
			RenderTarget.DrawTextLayout(new SharpDX.Vector2(x + 4f, top + 1f), label.Layout, levelIsLight[level] ? dxTagDark : dxTagLight);
		}

		#endregion

		#region Rendering

		protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
		{
			if (chartControl == null || chartScale == null || RenderTarget == null
				|| ChartBars == null || ChartPanel == null || ChartSeries() == null)
				return;

			clipLeft = ChartPanel.X;
			clipTop = ChartPanel.Y;
			clipRight = ChartPanel.X + ChartPanel.W;
			clipBottom = ChartPanel.Y + ChartPanel.H;

			SharpDX.Direct2D1.AntialiasMode oldMode = RenderTarget.AntialiasMode;
			try
			{
				if (EnableClickMerge && !mouseAttached && !terminated)
					AttachMouse(chartControl);

				RenderTarget.AntialiasMode = SharpDX.Direct2D1.AntialiasMode.Aliased;
				RenderProfiles(chartControl, chartScale);
				renderError = null;
			}
			catch (Exception ex)
			{
				renderError = "TPO Market Profile: drawing error: " + Describe(ex);
				dxReady = false;
			}
			finally
			{
				RenderTarget.AntialiasMode = oldMode;
			}

			// Anything that went wrong is written on the chart, so an empty chart always has an explanation.
			string problem = renderError != null ? renderError : (barError != null ? barError : zoneProblem);
			if (problem == null && reachedRealtime && lastSessionCount == 0)
				problem = "TPO Market Profile: no 1-minute bars found inside " + SessionHours
					+ ". The chart needs 1-minute history for the days it shows.";

			if (problem != null)
			{
				if (problem != lastPrintedError)
				{
					lastPrintedError = problem;
					Print(problem);
				}
				DrawProblem(problem);
			}
		}

		private void DrawProblem(string text)
		{
			try
			{
				using (SharpDX.Direct2D1.SolidColorBrush back = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color4(0f, 0f, 0f, 0.8f)))
				using (SharpDX.Direct2D1.SolidColorBrush fore = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color4(1f, 0.45f, 0.4f, 1f)))
				using (SharpDX.DirectWrite.TextFormat format = new SharpDX.DirectWrite.TextFormat(NinjaTrader.Core.Globals.DirectWriteFactory, "Arial", 12f))
				using (SharpDX.DirectWrite.TextLayout layout = new SharpDX.DirectWrite.TextLayout(
					NinjaTrader.Core.Globals.DirectWriteFactory, text, format, Math.Max(120f, clipRight - clipLeft - 40f), 300f))
				{
					float x = clipLeft + 12f;
					float y = clipTop + 12f;
					float w = Math.Min(layout.Metrics.Width + 12f, clipRight - x);
					float h = Math.Min(layout.Metrics.Height + 8f, clipBottom - y);
					if (w > 0f && h > 0f)
					{
						RenderTarget.FillRectangle(new SharpDX.RectangleF(x, y, w, h), back);
						RenderTarget.DrawTextLayout(new SharpDX.Vector2(x + 6f, y + 4f), layout, fore);
					}
				}
			}
			catch (Exception)
			{
			}
		}

		private void RenderProfiles(ChartControl chartControl, ChartScale chartScale)
		{
			int barCount = ChartSeries().Count;
			if (barCount <= 0)
				return;

			int lastIdx = barCount - 1;
			int firstVisible = ChartBars.FromIndex;
			int lastVisible = ChartBars.ToIndex;
			if (firstVisible < 0 || lastVisible < 0 || lastVisible < firstVisible)
				return;
			if (lastVisible > lastIdx)
				lastVisible = lastIdx;
			if (firstVisible > lastVisible)
				firstVisible = lastVisible;

			float barDistance = 10f;
			if (lastVisible > firstVisible)
				barDistance = (chartControl.GetXByBarIndex(ChartBars, lastVisible) - chartControl.GetXByBarIndex(ChartBars, firstVisible)) / (float)(lastVisible - firstVisible);
			if (barDistance < 0.5f)
				barDistance = 0.5f;

			// Labels of prices that are no longer shown pile up slowly; start again once there are too many.
			// Done here, before anything is drawn, so that no layout handed out during this redraw is released.
			if (labelCacheCount > MaxCachedLabels)
				ClearLabelCache();

			// Step 1 (locked, data only): refresh the grouping and take a snapshot.
			TpoProfile[] snapshot;
			DateTime[] snapshotEnd;
			TpoSession lastSession = null;
			TpoSession priorSession = null;
			DateTime priorEnd = DateTime.MinValue;
			int rowTicks;
			int gapCount;
			bool ibReady;
			double ibTop;
			double ibBottom;
			DateTime ibFrom;
			DateTime ibMark;

			lock (sync)
			{
				ibReady = false;
				ibTop = 0;
				ibBottom = 0;
				ibFrom = DateTime.MinValue;
				ibMark = DateTime.MinValue;

				gapCount = ShowGaps ? gaps.Count : 0;
				if (gapViews.Length < gapCount)
					gapViews = new GapView[gapCount + 16];
				for (int i = 0; i < gapCount; i++)
				{
					GapRec gap = gaps[i];
					gapViews[i].Rec = gap;
					gapViews[i].Up = gap.Up;
					gapViews[i].Level = gap.Level;
					gapViews[i].Edge = gap.Edge;
					gapViews[i].StartClose = gap.StartClose;
					gapViews[i].Filled = gap.Filled;
					gapViews[i].FillClose = gap.FillClose;
					gapViews[i].SessionOver = gap.SessionOver;
					gapViews[i].AreaFrom = gap.AreaFrom;
				}

				if (ShowInitialBalance)
				{
					if (SessionHours == TpoMarketProfileTypes.TpoSessionHours.RTH
						&& SessionType == TpoMarketProfileTypes.TpoSessionType.Daily)
					{
						// Daily New York sessions: the latest session is the current one, and its own first
						// minutes are the initial balance. A new session at the open has none until they are over.
						TpoSession current = sessions.Count > 0 ? sessions[sessions.Count - 1] : null;
						if (current != null && current.HasIb && current.IbHigh >= current.IbLow)
						{
							DateTime ibOver = current.Anchor.AddMinutes(InitialBalanceMinutes);
							if (current.LastBarClose > ibOver)
							{
								ibReady = true;
								ibTop = current.IbHigh;
								ibBottom = current.IbLow;
								ibFrom = current.FirstBarOpen.AddMinutes(1);
								ibMark = ibOver.AddMinutes(1);
							}
						}
					}
					else if (ibComplete && ibHigh >= ibLow)
					{
						// Any other profiles (ETH, weekly, monthly): followed separately from the 09:30 New York open.
						ibReady = true;
						ibTop = ibHigh;
						ibBottom = ibLow;
						ibFrom = ibFirstClose;
						ibMark = ibMarkClose;
					}
				}

				if (groupingDirty || groupedSessionCount != sessions.Count)
					Regroup();

				rowTicks = ComputeRowTicks();
				if (snapshotBuffer.Length != profiles.Count)
				{
					snapshotBuffer = new TpoProfile[profiles.Count];
					snapshotEndBuffer = new DateTime[profiles.Count];
				}
				profiles.CopyTo(snapshotBuffer);
				snapshot = snapshotBuffer;
				snapshotEnd = snapshotEndBuffer;
				for (int i = 0; i < snapshot.Length; i++)
				{
					List<TpoSession> list = snapshot[i].Sessions;
					snapshotEnd[i] = list[list.Count - 1].LastBarClose;
				}

				lastSessionCount = sessions.Count;
				if (sessions.Count > 0)
					lastSession = sessions[sessions.Count - 1];
				if (sessions.Count > 1)
				{
					priorSession = sessions[sessions.Count - 2];
					priorEnd = priorSession.LastBarClose;
				}
			}

			if (snapshot.Length == 0)
				return;

			// Step 2 (unlocked, chart access): where each profile sits on the bar axis.
			// The developing session is given at least the width of the session before it.
			int typicalBars = 0;
			if (priorSession != null)
				typicalBars = Math.Max(1, EndIndexOf(priorSession, priorEnd, barCount) - StartIndexOf(priorSession, barCount));

			for (int i = 0; i < snapshot.Length; i++)
			{
				TpoProfile p = snapshot[i];
				p.HitValid = false;

				TpoSession lastOfProfile = p.Sessions[p.Sessions.Count - 1];
				bool developing = lastOfProfile == lastSession;

				int start = StartIndexOf(p.SpanStart, barCount);
				int end;

				if (!developing)
				{
					end = EndIndexOf(lastOfProfile, snapshotEnd[i], barCount);
				}
				else
				{
					end = FirstBarAfter(snapshotEnd[i], barCount);
					if (end >= barCount - 1)
						end = barCount;
					if (typicalBars > 0)
					{
						int expectedEnd = StartIndexOf(lastOfProfile, barCount) + typicalBars;
						if (expectedEnd > end)
							end = expectedEnd;
					}
				}

				if (end <= start)
					end = start + 1;

				p.GeoStart = start;
				p.GeoEnd = end;
				p.Visible = start <= lastVisible + 1 && end >= firstVisible - 1;
			}

			// Step 3 (locked, data only): build what is going to be drawn and follow the naked levels.
			lock (sync)
			{
				for (int i = 0; i < snapshot.Length; i++)
				{
					bool needed = nakedEnabled || snapshot[i].Visible
						|| (CarriesLevels(i, snapshot.Length) && snapshot[i + 1].Visible);
					if (!needed)
						continue;

					EnsureBuilt(snapshot[i], rowTicks);
					if (nakedEnabled)
						UpdateNaked(snapshot[i]);
				}
			}

			// Step 4 (unlocked): draw. Brushes are rebuilt whenever they are missing or belong to another render target.
			if (!dxReady || !object.ReferenceEquals(dxTarget, RenderTarget))
				CreateDeviceResources();

			for (int i = 0; i < snapshot.Length; i++)
			{
				TpoProfile p = snapshot[i];
				TpoProfile next = i + 1 < snapshot.Length ? snapshot[i + 1] : null;
				bool carries = CarriesLevels(i, snapshot.Length);
				bool levelsOnly = !p.Visible && carries && next.Visible;

				if (!p.Visible && !levelsOnly)
					continue;

				TpoBuilt b = p.Built;
				if (b == null || b.RowCount == 0)
					continue;

				float xLeft = XOfIndex(chartControl, p.GeoStart, lastIdx, barDistance) - barDistance * 0.5f;
				float xRight = XOfIndex(chartControl, p.GeoEnd, lastIdx, barDistance) - barDistance * 0.5f;
				if (xRight < xLeft + 1f)
					xRight = xLeft + 1f;

				float xPriorEnd = xRight;
				bool hasNext = false;
				if (carries)
				{
					float nextRight = XOfIndex(chartControl, next.GeoEnd, lastIdx, barDistance) - barDistance * 0.5f;
					if (nextRight > xPriorEnd)
					{
						xPriorEnd = nextRight;
						hasNext = true;
					}
				}

				DrawProfile(chartControl, chartScale, p, b, i == snapshot.Length - 1, xLeft, xRight, xPriorEnd, hasNext, levelsOnly, barCount, lastIdx, barDistance);
			}

			// Naked levels run from the end of their profile to the bar that traded them again, or to the right edge.
			// With "Extend All Naked POCs" every profile's untouched point of control runs to the right edge.
			if (nakedEnabled)
			{
				for (int i = 0; i < snapshot.Length; i++)
				{
					TpoProfile p = snapshot[i];
					TpoBuilt b = p.Built;
					if (b == null || b.RowCount == 0 || p.Naked == null || !object.ReferenceEquals(p.NakedFor, b))
						continue;

					float xStart = XOfIndex(chartControl, p.GeoEnd, lastIdx, barDistance) - barDistance * 0.5f;
					if (xStart > clipRight)
						continue;

					for (int lv = 0; lv < LevelCount; lv++)
					{
						NakedLevel level = p.Naked[lv];
						if (!level.Active || !level.HasAfter)
							continue;
						if (level.Touched && !NakedTrail(lv))
							continue;

						float xEnd = clipRight;
						if (level.Touched)
						{
							int index = level.TouchBarIdx;
							if (index < 0)
							{
								index = FirstBarAfter(level.TouchTime.AddMinutes(-1), barCount);
								if (index < barCount)
									level.TouchBarIdx = index;
							}
							xEnd = XOfIndex(chartControl, index, lastIdx, barDistance);
						}
						if (xEnd <= xStart || xEnd < clipLeft)
							continue;

						double lineValue;
						double price;
						if (!LevelValues(b, lv, out lineValue, out price))
							continue;

						float y = chartScale.GetYByValue(lineValue);
						HLine(xStart, xEnd, y, dxLevel[lv], LevelWidth(lv), LevelStyle(lv));

						if (level.Touched)
							continue;

						// Every untouched point of control is named after its kind of session: "pRTH nPOC" or "pETH nPOC".
						if (lv == LvPoc && NakedPocExtend)
							DrawLabel(PriorText("nPOC", price), clipRight - 4f, y, dxLevel[lv], true, -1, labelFormat);
						else if (LevelLabel(lv))
							DrawLabel(LevelText(lv, price, "n"), clipRight - 4f, y, dxLevel[lv], true, -1, labelFormat);
					}
				}
			}

			// Gap and initial balance lines stop where the current profile's own level lines stop: at its right
			// end, not at the price scale. Once the newest bar is past that end (after the session), gaps run on
			// to the newest bar, so that what is left of them stays in sight.
			TpoProfile newest = snapshot[snapshot.Length - 1];
			float xProfileEnd = XOfIndex(chartControl, newest.GeoEnd, lastIdx, barDistance) - barDistance * 0.5f;
			float xNewestBar = XOfIndex(chartControl, lastIdx, lastIdx, barDistance) + barDistance * 0.5f;

			if (gapCount > 0)
				DrawGaps(chartControl, chartScale, gapCount, barCount, lastIdx, barDistance, Math.Max(xProfileEnd, xNewestBar));

			if (ibReady)
				DrawInitialBalance(chartControl, chartScale, ibTop, ibBottom, ibFrom, ibMark, barCount, lastIdx, barDistance, xProfileEnd, xNewestBar);
		}

		// Opening gaps. Each one is a line at the prior session's high or low, from the New York open that
		// left it behind to the bar that traded back to it, or to the right edge while it is still open.
		// A gap its own session did not fill also gets a highlighted band over what is left of it.
		private void DrawGaps(ChartControl chartControl, ChartScale chartScale, int count, int barCount, int lastIdx, float barDistance, float xStop)
		{
			float width = Math.Max(1, GapLineWidth);
			SharpDX.Direct2D1.StrokeStyle style = LevelStyle(LvPoc);
			if (xStop > clipRight)
				xStop = clipRight;

			for (int i = 0; i < count; i++)
			{
				GapRec rec = gapViews[i].Rec;

				int startIdx = rec.StartIdx;
				if (startIdx < 0)
				{
					startIdx = FirstBarAfter(gapViews[i].StartClose.AddMinutes(-1), barCount);
					if (startIdx < barCount)
						rec.StartIdx = startIdx;
				}
				if (startIdx >= barCount)
					continue;

				float xStart = XOfIndex(chartControl, startIdx, lastIdx, barDistance) - barDistance * 0.5f;
				if (xStart >= clipRight)
					continue;

				float xEnd = Math.Max(xStop, xStart + barDistance * 0.5f);
				if (gapViews[i].Filled)
				{
					int fillIdx = rec.FillIdx;
					if (fillIdx < 0)
					{
						fillIdx = FirstBarAfter(gapViews[i].FillClose.AddMinutes(-1), barCount);
						if (fillIdx < barCount)
							rec.FillIdx = fillIdx;
					}
					xEnd = XOfIndex(chartControl, fillIdx, lastIdx, barDistance);
					if (xEnd < xStart + barDistance * 0.5f)
						xEnd = xStart + barDistance * 0.5f;
				}
				if (xEnd < clipLeft)
					continue;

				float y = chartScale.GetYByValue(gapViews[i].Level);

				if (!gapViews[i].Filled && gapViews[i].SessionOver)
				{
					int areaIdx = rec.AreaIdx;
					if (areaIdx < 0)
					{
						areaIdx = FirstBarAfter(gapViews[i].AreaFrom, barCount);
						if (areaIdx < barCount)
							rec.AreaIdx = areaIdx;
					}

					float xArea = XOfIndex(chartControl, areaIdx, lastIdx, barDistance) - barDistance * 0.5f;
					float yEdge = chartScale.GetYByValue(gapViews[i].Edge);
					float top = Math.Min(y, yEdge);
					if (xEnd > xArea && yEdge != y)
					{
						// The band, closed off by a line on the side price has to come back from.
						FillRect(xArea, top, xEnd - xArea, Math.Abs(yEdge - y), dxGapArea);
						HLine(xArea, xEnd, yEdge, dxGapLine, OtherWidth(), style);
					}
				}

				HLine(xStart, xEnd, y, dxGapLine, width, style);

				// The label sits inside the gap: above the line of a gap up, below the line of a gap down.
				string text = "GAP";
				if (LevelPriceTags)
					text += " " + FormatPrice(gapViews[i].Level);
				DrawLabel(text, Math.Max(xStart, clipLeft) + 4f, y, dxGapLine, false, gapViews[i].Up ? -1 : 1, labelFormat);
			}
		}

		// The initial balance of the current New York session only. Nothing is drawn while it is still forming;
		// once its last minute is over, its high and low run from the open to the right edge of the chart, with
		// an upright line at the bar where it was completed. The next New York open removes it.
		private void DrawInitialBalance(ChartControl chartControl, ChartScale chartScale, double high, double low,
			DateTime firstClose, DateTime markClose, int barCount, int lastIdx, float barDistance, float xProfileEnd, float xNewestBar)
		{
			if (firstClose == DateTime.MinValue || markClose == DateTime.MinValue)
				return;

			// Minute bars are stamped with their closing time: the chart bar holding a minute is the first one after its opening time.
			int startIdx = FirstBarAfter(firstClose.AddMinutes(-1), barCount);
			if (startIdx >= barCount)
				return;
			int markIdx = FirstBarAfter(markClose.AddMinutes(-1), barCount);
			if (markIdx < startIdx)
				markIdx = startIdx;

			float xStart = XOfIndex(chartControl, startIdx, lastIdx, barDistance) - barDistance * 0.5f;
			float xMark = XOfIndex(chartControl, markIdx, lastIdx, barDistance) - barDistance * 0.5f;
			if (xStart >= clipRight)
				return;

			float yHigh = chartScale.GetYByValue(high);
			float yLow = chartScale.GetYByValue(low);
			float width = Math.Max(1, IbLineWidth);
			SharpDX.Direct2D1.StrokeStyle style = LevelStyle(LvPoc);

			// The lines end with the current profile. If that profile does not reach past the initial balance
			// (profiles that are not New York sessions), they end at the newest bar instead.
			float xEnd = xProfileEnd > xMark + 1f ? xProfileEnd : xNewestBar;
			if (xEnd < xMark + barDistance)
				xEnd = xMark + barDistance;
			if (xEnd > clipRight)
				xEnd = clipRight;

			HLine(xStart, xEnd, yHigh, dxIb, width, style);
			HLine(xStart, xEnd, yLow, dxIb, width, style);
			VLine(xMark, yHigh, yLow, dxIb, width, style);

			string highText = "IBH";
			string lowText = "IBL";
			if (LevelPriceTags)
			{
				highText += " " + FormatPrice(high);
				lowText += " " + FormatPrice(low);
			}
			DrawLabel(highText, xEnd - 4f, yHigh, dxIb, true, -1, labelFormat);
			DrawLabel(lowText, xEnd - 4f, yLow, dxIb, true, 1, labelFormat);
		}

		// Does profile 'index' carry its chosen levels across the profile after it?
		// Normally only the profile just before the current (last) one does; optionally every profile.
		private bool CarriesLevels(int index, int count)
		{
			if (index + 1 >= count)
				return false;
			if (!PriorPocExtend && !PriorVahExtend && !PriorValExtend && !PriorHighExtend && !PriorLowExtend)
				return false;
			return PriorSessionLevels || index == count - 2;
		}

		private void DrawProfile(ChartControl chartControl, ChartScale chartScale, TpoProfile p, TpoBuilt b, bool isLatest,
			float xLeft, float xRight, float xPriorEnd, bool hasNext, bool levelsOnly, int barCount, int lastIdx, float barDistance)
		{
			int n = b.RowCount;
			float xUse = xRight - Math.Max(0, ProfileSpacing);
			if (xUse < xLeft + 1f)
				xUse = xLeft + 1f;
			float available = xUse - xLeft;

			// Row edges in pixels: edge[r] is the bottom of row r, edge[r + 1] its top.
			if (edgeBuffer.Length < n + 1)
				edgeBuffer = new float[n + 64];
			float[] edge = edgeBuffer;

			double rowSize = b.RowTicks * tickSize;
			double basePrice = b.RowMin * rowSize - tickSize * 0.5;
			for (int k = 0; k <= n; k++)
				edge[k] = chartScale.GetYByValue(basePrice + k * rowSize);

			float rowPixels = Math.Abs(edge[0] - edge[n]) / n;

			bool expanded = p.SpanStart != null && expandedKeys.Contains(p.SpanStart.Key);
			bool volumeOn = ShowVolumeProfile && b.Vol != null && b.MaxVol > 0;
			float volumeWidth = volumeOn ? available * (VolumeWidthPercent / 100f) : 0f;
			float xTpo = xLeft;
			float tpoAvailable = available;
			if (volumeOn && !expanded && VolumePosition == TpoMarketProfileTypes.TpoVolumePosition.Left)
			{
				xTpo = xLeft + volumeWidth + 3f;
				tpoAvailable = available - volumeWidth - 3f;
				if (tpoAvailable < 1f)
					tpoAvailable = 1f;
			}

			float cellWidth;
			if (BlockWidth > 0)
			{
				cellWidth = BlockWidth;
			}
			else
			{
				cellWidth = tpoAvailable * (ProfileWidthPercent / 100f) / Math.Max(1, b.MaxCount);
				if (cellWidth > MaxBlockWidth)
					cellWidth = MaxBlockWidth;
				if (cellWidth < 1f)
					cellWidth = 1f;
			}

			bool histogram = BlockDisplay == TpoMarketProfileTypes.TpoBlockDisplay.Histogram;
			bool wantsLetters = BlockDisplay == TpoMarketProfileTypes.TpoBlockDisplay.BlocksWithLetters
				|| BlockDisplay == TpoMarketProfileTypes.TpoBlockDisplay.LettersOnly;
			bool letters = wantsLetters && cellWidth >= MinLetterPixels && rowPixels >= MinLetterPixels;
			bool fill = !(letters && BlockDisplay == TpoMarketProfileTypes.TpoBlockDisplay.LettersOnly);
			float gap = (!histogram && cellWidth >= 4f && rowPixels >= 4f) ? 1f : 0f;
			bool mergeRuns = gap == 0f && !letters;

			int fontSize = (int)Math.Floor(Math.Min(cellWidth, rowPixels) * 0.72f);
			if (fontSize < 6) fontSize = 6;
			if (fontSize > MaxLetterFont) fontSize = MaxLetterFont;

			int rowFont = (int)Math.Floor(rowPixels * 0.8f);
			if (rowFont > 11) rowFont = 11;
			bool rowTextFits = rowPixels >= 8f;

			float annotationTop = edge[0] + 6f;

			if (!levelsOnly)
			{
				if (FillValueArea)
					FillRect(xLeft, edge[b.Vah + 1], available, edge[b.Val] - edge[b.Vah + 1], dxVaFill);

				if (volumeOn && VolumePosition == TpoMarketProfileTypes.TpoVolumePosition.Left)
					DrawVolume(b, edge, xLeft, 1, volumeWidth, rowTextFits, rowFont);
				else if (volumeOn && VolumePosition == TpoMarketProfileTypes.TpoVolumePosition.Right)
					DrawVolume(b, edge, xUse, -1, volumeWidth, rowTextFits, rowFont);

				// TPO blocks: collapsed against the left edge, or expanded with every block in its own time column.
				if (expanded)
				{
					ResolveBlockBars(b, barCount);
					DrawExpanded(chartControl, b, edge, lastIdx, barDistance, rowPixels, histogram, wantsLetters);
				}
				else
				{
					for (int r = 0; r < n; r++)
					{
						int c0 = b.RowStart[r];
						int c1 = b.RowStart[r + 1];
						if (c1 == c0)
							continue;

						float top = edge[r + 1];
						float height = edge[r] - top;
						if (height < 1f) height = 1f;
						if (top > clipBottom || top + height < clipTop)
							continue;

						SharpDX.Direct2D1.SolidColorBrush rowBrush = RowBrush(b, r);

						float cellHeight = height - gap;
						if (cellHeight < 1f) cellHeight = 1f;
						float cellDrawWidth = cellWidth - gap;
						if (cellDrawWidth < 1f) cellDrawWidth = 1f;

						if (mergeRuns)
						{
							// No gaps and no letters: draw each run of same-coloured cells as one rectangle.
							int runStart = c0;
							SharpDX.Direct2D1.SolidColorBrush runBrush = rowBrush != null ? rowBrush : dxPeriod[b.CellShade[c0]];

							for (int c = c0 + 1; c <= c1; c++)
							{
								SharpDX.Direct2D1.SolidColorBrush brush = null;
								if (c < c1)
									brush = rowBrush != null ? rowBrush : dxPeriod[b.CellShade[c]];

								if (c == c1 || !object.ReferenceEquals(brush, runBrush))
								{
									float x0 = xTpo + (runStart - c0) * cellWidth;
									float x1 = xTpo + (c - c0) * cellWidth;
									FillRect(x0, top, x1 - x0, cellHeight, runBrush);
									runStart = c;
									runBrush = brush;
								}
							}
						}
						else
						{
							for (int c = c0; c < c1; c++)
							{
								float x = xTpo + (c - c0) * cellWidth;
								if (x > clipRight)
									break;
								if (x + cellWidth < clipLeft)
									continue;

								SharpDX.Direct2D1.SolidColorBrush brush = rowBrush != null ? rowBrush : dxPeriod[b.CellShade[c]];

								if (fill)
									FillRect(x, top, cellDrawWidth, cellHeight, brush);

								if (letters)
									DrawLetter(fontSize, b.CellLetter[c], x, top, cellDrawWidth, cellHeight, fill ? dxLetter : brush);
							}
						}
					}

					// Start (open) and end (close) blocks.
					if (HighlightStartEnd)
					{
						DrawMarkedCell(b, edge, b.OpenRow, 0, xTpo, cellWidth, gap, letters, fontSize, dxOpenBlock);
						DrawMarkedCell(b, edge, b.CloseRow, b.Counts[b.CloseRow] - 1, xTpo, cellWidth, gap, letters, fontSize, dxCloseBlock);
					}
				}

				if (volumeOn && VolumePosition == TpoMarketProfileTypes.TpoVolumePosition.OverTpo)
					DrawVolume(b, edge, xLeft, 1, volumeWidth, rowTextFits, rowFont);

				// Number of TPOs at the end of each row.
				if (ShowTpoCounts && rowTextFits && !expanded)
				{
					SharpDX.DirectWrite.TextFormat format = SmallFormat(rowFont);
					for (int r = 0; r < n; r++)
					{
						float middle = (edge[r] + edge[r + 1]) * 0.5f;
						if (middle < clipTop || middle > clipBottom)
							continue;
						DrawLabel(b.Counts[r].ToString(CultureInfo.InvariantCulture), xTpo + b.Counts[r] * cellWidth + 3f, middle, dxText, false, 0, format);
					}
				}

				// Poor high / poor low: a short marker over the extreme row.
				for (int lv = LvPoorHigh; lv <= LvPoorLow; lv++)
				{
					double lineValue;
					double price;
					if (!LevelShown(lv) || !LevelValues(b, lv, out lineValue, out price))
						continue;

					bool isHigh = lv == LvPoorHigh;
					float y = isHigh ? edge[n] - 2f : edge[0] + 2f;
					float w = expanded ? Math.Max(xUse - xTpo, 6f) : Math.Max(cellWidth * b.Counts[isHigh ? n - 1 : 0], 6f);
					HLine(xTpo, xTpo + w, y, dxLevel[lv], Math.Max(2f, OtherWidth()), null);
					if (LevelLabel(lv))
						DrawLabel(LevelText(lv, price, string.Empty), xTpo + w + 4f, y, dxLevel[lv], false, 0, labelFormat);
					if (isLatest && LevelScale(lv))
						DrawScaleTag(lv, price, chartScale.GetYByValue(lineValue));
					if (!isHigh)
						annotationTop += 4f;
				}

				// Range extension beyond the session's initial balance, as brackets on the left edge.
				// (The initial balance itself is drawn once, for the current New York session: DrawInitialBalance.)
				if (b.HasIb && RangeExtension)
				{
					float yHigh = chartScale.GetYByValue(b.IbHigh + tickSize * 0.5);
					float yLow = chartScale.GetYByValue(b.IbLow - tickSize * 0.5);
					float x = xLeft - 3f;

					if (b.ExtensionUp > 0)
					{
						float yTop = chartScale.GetYByValue(b.IbHigh + b.ExtensionUp + tickSize * 0.5);
						VLine(x, yTop, yHigh, dxRangeExt, OtherWidth());
						HLine(x - 3f, x + 2f, yTop, dxRangeExt, OtherWidth(), null);
					}
					if (b.ExtensionDown > 0)
					{
						float yBottom = chartScale.GetYByValue(b.IbLow - b.ExtensionDown - tickSize * 0.5);
						VLine(x, yLow, yBottom, dxRangeExt, OtherWidth());
						HLine(x - 3f, x + 2f, yBottom, dxRangeExt, OtherWidth(), null);
					}
				}

				// Developing levels and the one-time-framing ribbon follow the time axis.
				if (DevelopingPoc || DevelopingVa || OneTimeFraming)
				{
					ResolveBlockBars(b, barCount);

					if (DevelopingVa)
					{
						DrawDeveloping(chartControl, b, edge, 1, lastIdx, barDistance, dxDevVa);
						DrawDeveloping(chartControl, b, edge, 2, lastIdx, barDistance, dxDevVa);
					}
					if (DevelopingPoc)
						DrawDeveloping(chartControl, b, edge, 0, lastIdx, barDistance, dxDevPoc);

					if (OneTimeFraming)
					{
						for (int k = 0; k < b.BlockOtf.Length; k++)
						{
							if (b.BlockOtf[k] == 0)
								continue;
							float x0 = XOfIndex(chartControl, b.BlockIdx0[k], lastIdx, barDistance) - barDistance * 0.5f;
							float x1 = XOfIndex(chartControl, b.BlockIdx1[k], lastIdx, barDistance) + barDistance * 0.5f;
							if (x1 < x0 + 1f)
								x1 = x0 + 1f;
							FillRect(x0, annotationTop - 2f, Math.Max(1f, x1 - x0 - 1f), 4f, b.BlockOtf[k] > 0 ? dxOtfUp : dxOtfDown);
						}
						annotationTop += 8f;
					}
				}

				// Above the profile: day type and open type.
				DrawHeadLabels(xLeft, xRight, edge[n] - 6f,
					DayType ? b.DayTypeText : null,
					OpenType ? b.OpenTypeText : null);

				p.HitValid = true;
				p.HitLeft = xLeft;
				p.HitRight = expanded ? xRight : Math.Max(xRight, xTpo + b.MaxCount * cellWidth);
			}

			// Levels: point of control, value area and the volume levels. A level chosen under "Extend Prior ..."
			// runs on across the following profile even when its own line is switched off.
			for (int lv = 0; lv < LevelCount; lv++)
			{
				if (lv == LvPoorHigh || lv == LvPoorLow)
					continue;

				bool shown = LevelShown(lv) && !levelsOnly;
				bool carried = hasNext && PriorChosen(lv);
				if (!shown && !carried)
					continue;

				double lineValue;
				double price;
				if (!LevelValues(b, lv, out lineValue, out price))
					continue;

				bool isVolume = lv >= LvVolPoc;
				float y = chartScale.GetYByValue(lineValue);
				HLine(shown ? xLeft : xRight, carried ? xPriorEnd : xUse, y, dxLevel[lv], LevelWidth(lv), LevelStyle(lv));

				if (shown && LevelLabel(lv))
				{
					if (isVolume)
						DrawLabel(LevelText(lv, price, string.Empty), xUse - 4f, y, dxLevel[lv], true, lv == LvVolVal ? 1 : -1, labelFormat);
					else
						DrawLabel(LevelText(lv, price, string.Empty), xLeft - 8f, y, dxLevel[lv], true, 0, labelFormat);
				}

				// An untouched prior POC is labelled once, as "nPOC" at the right edge, by the naked pass.
				if (carried && !(lv == LvPoc && NakedPocOpen(p, b)))
					DrawLabel(PriorText(LevelName(lv), price), Math.Min(xPriorEnd, clipRight) - 4f, y, dxLevel[lv], true, -1, labelFormat);

				if (isLatest && shown && LevelScale(lv))
					DrawScaleTag(lv, price, y);
			}

			// The session's range high and low can be extended across the following profile as well.
			if (hasNext && (PriorHighExtend || PriorLowExtend))
			{
				SharpDX.Direct2D1.StrokeStyle style = LevelStyle(LvPoc);
				float width = Math.Max(1, LineWidth);
				float labelX = Math.Min(xPriorEnd, clipRight) - 4f;

				if (PriorHighExtend)
				{
					float y = chartScale.GetYByValue(b.High);
					HLine(xRight, xPriorEnd, y, dxPriorHigh, width, style);
					DrawLabel(PriorText("High", b.High), labelX, y, dxPriorHigh, true, -1, labelFormat);
				}
				if (PriorLowExtend)
				{
					float y = chartScale.GetYByValue(b.Low);
					HLine(xRight, xPriorEnd, y, dxPriorLow, width, style);
					DrawLabel(PriorText("Low", b.Low), labelX, y, dxPriorLow, true, 1, labelFormat);
				}
			}

			// Annotation block under the profile.
			if (!levelsOnly)
			{
				if (b.Annotation == null)
					b.Annotation = BuildAnnotation(b);
				string text = b.Annotation;
				Glyph block = text.Length > 0 ? GetLabel(text, labelFormat) : null;
				if (block != null)
				{
					float w = block.W;
					float h = block.H;
					if (TextFits(xLeft - 4f, annotationTop - 3f, w + 8f, h + 6f))
					{
						if (StatisticsBox)
						{
							SharpDX.RectangleF box = new SharpDX.RectangleF(xLeft - 4f, annotationTop - 3f, w + 8f, h + 6f);
							RenderTarget.FillRectangle(box, dxStatsBack);
							RenderTarget.DrawRectangle(box, dxText, 1f);
						}
						RenderTarget.DrawTextLayout(new SharpDX.Vector2(xLeft, annotationTop), block.Layout, dxText);
					}
				}
			}
		}

		// Colour that applies to a whole row, or null when each block keeps its own period colour.
		private SharpDX.Direct2D1.SolidColorBrush RowBrush(TpoBuilt b, int r)
		{
			int n = b.RowCount;
			if (HighlightPocRow && r == b.Poc)
				return dxPocRow;
			if (ShowTails && b.TopTail > 0 && r >= n - b.TopTail)
				return dxSellTail;
			if (ShowTails && b.BottomTail > 0 && r < b.BottomTail)
				return dxBuyTail;
			if (ShowSinglePrints && b.Counts[r] == 1 && r >= b.BottomRun && r < n - b.TopRun)
				return dxSingle;
			if (OutsideVaColors && (r < b.Val || r > b.Vah))
				return dxOutside;
			if (BlockColorMode == TpoMarketProfileTypes.TpoBlockColorMode.Heat)
				return dxHeat[ShadeOf(b.MaxCount > 1 ? (double)(b.Counts[r] - 1) / (b.MaxCount - 1) : 1.0)];
			if (BlockColorMode == TpoMarketProfileTypes.TpoBlockColorMode.Solid)
				return dxSolid;
			return null;
		}

		// Expanded view: every block is drawn as a column over the bars of its own time slice.
		private void DrawExpanded(ChartControl chartControl, TpoBuilt b, float[] edge, int lastIdx, float barDistance, float rowPixels,
			bool histogram, bool wantsLetters)
		{
			if (b.BlockIdx0 == null)
				return;

			int blocks = b.BlockRow0.Length;
			for (int k = 0; k < blocks; k++)
			{
				float x0 = XOfIndex(chartControl, b.BlockIdx0[k], lastIdx, barDistance) - barDistance * 0.5f;
				float x1 = XOfIndex(chartControl, b.BlockIdx1[k], lastIdx, barDistance) + barDistance * 0.5f;
				if (x1 < clipLeft || x0 > clipRight)
					continue;

				float width = x1 - x0;
				if (width < 1f) width = 1f;

				float gap = (!histogram && width >= 4f && rowPixels >= 4f) ? 1f : 0f;
				bool letters = wantsLetters && width >= MinLetterPixels && rowPixels >= MinLetterPixels;
				bool fill = !(letters && BlockDisplay == TpoMarketProfileTypes.TpoBlockDisplay.LettersOnly);
				bool mergeRuns = gap == 0f && !letters;
				float drawWidth = width - gap;
				if (drawWidth < 1f) drawWidth = 1f;

				int fontSize = (int)Math.Floor(Math.Min(width, rowPixels) * 0.72f);
				if (fontSize < 6) fontSize = 6;
				if (fontSize > MaxLetterFont) fontSize = MaxLetterFont;

				SharpDX.Direct2D1.SolidColorBrush own = dxPeriod[b.BlockShade[k]];
				int r0 = b.BlockRow0[k];
				int r1 = b.BlockRow1[k];

				if (mergeRuns)
				{
					// No gaps and no letters: one rectangle per run of same-coloured rows.
					int runStart = r0;
					SharpDX.Direct2D1.SolidColorBrush runBrush = RowBrush(b, r0);
					if (runBrush == null) runBrush = own;

					for (int r = r0 + 1; r <= r1 + 1; r++)
					{
						SharpDX.Direct2D1.SolidColorBrush brush = null;
						if (r <= r1)
						{
							brush = RowBrush(b, r);
							if (brush == null) brush = own;
						}

						if (r > r1 || !object.ReferenceEquals(brush, runBrush))
						{
							float top = edge[r];
							float bottom = edge[runStart];
							if (bottom - top < 1f) bottom = top + 1f;
							FillRect(x0, top, drawWidth, bottom - top, runBrush);
							runStart = r;
							runBrush = brush;
						}
					}
				}
				else
				{
					for (int r = r0; r <= r1; r++)
					{
						float top = edge[r + 1];
						float height = edge[r] - top - gap;
						if (height < 1f) height = 1f;
						if (top > clipBottom || top + height < clipTop)
							continue;

						SharpDX.Direct2D1.SolidColorBrush brush = RowBrush(b, r);
						if (brush == null) brush = own;

						if (fill)
							FillRect(x0, top, drawWidth, height, brush);
						if (letters)
							DrawLetter(fontSize, b.BlockLetter[k], x0, top, drawWidth, height, fill ? dxLetter : brush);
					}
				}

				// Start (open) and end (close) blocks.
				if (HighlightStartEnd && (k == 0 || k == blocks - 1))
				{
					for (int pass = 0; pass < 2; pass++)
					{
						bool isOpen = pass == 0;
						if ((isOpen && k != 0) || (!isOpen && k != blocks - 1))
							continue;
						int row = isOpen ? b.OpenRow : b.CloseRow;
						if (row < r0 || row > r1)
							continue;

						float top = edge[row + 1];
						float height = edge[row] - top - gap;
						if (height < 1f) height = 1f;
						FillRect(x0, top, drawWidth, height, isOpen ? dxOpenBlock : dxCloseBlock);
						if (letters && top >= clipTop && top + height <= clipBottom)
							DrawLetter(fontSize, b.BlockLetter[k], x0, top, drawWidth, height, dxLetter);
					}
				}
			}
		}

		// Stacks the label lines just above the top of a profile. When the profile touches the top of the
		// panel the lines move down inside it, and when its left edge is scrolled away they stay at the panel edge.
		private void DrawHeadLabels(float xLeft, float xRight, float bottom, string dayType, string openType)
		{
			Glyph day = string.IsNullOrEmpty(dayType) ? null : GetLabel(dayType, labelFormat);
			Glyph open = string.IsNullOrEmpty(openType) ? null : GetLabel(openType, labelFormat);

			float total = 0f;
			if (day != null)
				total += day.H + 1f;
			if (open != null)
				total += open.H + 1f;
			if (total == 0f || xRight < clipLeft)
				return;

			float x = xLeft < clipLeft + 2f ? clipLeft + 2f : xLeft;
			float y = bottom - total;
			if (y < clipTop + 1f)
				y = clipTop + 1f;

			if (day != null)
			{
				if (TextFits(x, y, day.W, day.H))
					RenderTarget.DrawTextLayout(new SharpDX.Vector2(x, y), day.Layout, dxDayType);
				y += day.H + 1f;
			}
			if (open != null)
			{
				if (TextFits(x, y, open.W, open.H))
					RenderTarget.DrawTextLayout(new SharpDX.Vector2(x, y), open.Layout, dxOpenType);
			}
		}

		private void DrawLetter(int fontSize, char letter, float x, float top, float width, float height, SharpDX.Direct2D1.Brush brush)
		{
			Glyph glyph = GetGlyph(fontSize, letter);
			float tx = x + (width - glyph.W) * 0.5f;
			float ty = top + (height - glyph.H) * 0.5f;
			if (TextFits(tx, ty, glyph.W, glyph.H))
				RenderTarget.DrawTextLayout(new SharpDX.Vector2(tx, ty), glyph.Layout, brush);
		}

		private void DrawMarkedCell(TpoBuilt b, float[] edge, int row, int cellInRow, float xTpo, float cellWidth, float gap,
			bool letters, int fontSize, SharpDX.Direct2D1.Brush brush)
		{
			if (row < 0 || row >= b.RowCount || cellInRow < 0 || cellInRow >= b.Counts[row])
				return;

			float top = edge[row + 1];
			float height = edge[row] - top - gap;
			if (height < 1f) height = 1f;
			float width = cellWidth - gap;
			if (width < 1f) width = 1f;
			float x = xTpo + cellInRow * cellWidth;

			FillRect(x, top, width, height, brush);
			if (letters && top >= clipTop && top + height <= clipBottom)
				DrawLetter(fontSize, b.CellLetter[b.RowStart[row] + cellInRow], x, top, width, height, dxLetter);
		}

		// direction 1: bars grow to the right from origin. direction -1: bars grow to the left from origin.
		private void DrawVolume(TpoBuilt b, float[] edge, float origin, int direction, float maxWidth, bool rowTextFits, int rowFont)
		{
			int n = b.RowCount;
			SharpDX.DirectWrite.TextFormat format = (VolumeValues && rowTextFits) ? SmallFormat(rowFont) : null;

			for (int r = 0; r < n; r++)
			{
				double volume = b.Vol[r];
				if (volume <= 0 || volume < VolumeThreshold)
					continue;

				float top = edge[r + 1];
				float height = edge[r] - top;
				if (height < 1f) height = 1f;
				if (top > clipBottom || top + height < clipTop)
					continue;

				double share = volume / b.MaxVol;
				float w = (float)share * maxWidth;
				if (w < 0.5f)
					w = 0.5f;

				SharpDX.Direct2D1.SolidColorBrush brush;
				if (VolumeColorGrade)
					brush = dxVolGrade[ShadeOf(share)];
				else
					brush = (r >= b.VVal && r <= b.VVah) ? dxVolIn : dxVolOut;

				float drawn = height > 3f ? height - 1f : height;
				FillRect(direction > 0 ? origin : origin - w, top, w, drawn, brush);

				if (format != null)
					DrawLabel(FormatVolume(volume), direction > 0 ? origin + 2f : origin - 2f, top + drawn * 0.5f, dxText, direction < 0, 0, format);
			}
		}

		// which: 0 = point of control, 1 = value area high, 2 = value area low.
		private void DrawDeveloping(ChartControl chartControl, TpoBuilt b, float[] edge, int which, int lastIdx, float barDistance, SharpDX.Direct2D1.Brush brush)
		{
			if (b.DevCount == 0 || b.BlockIdx0 == null)
				return;

			float previousY = 0f;
			for (int d = 0; d < b.DevCount; d++)
			{
				float y;
				if (which == 0)
					y = (edge[b.DevPoc[d]] + edge[b.DevPoc[d] + 1]) * 0.5f;
				else if (which == 1)
					y = edge[b.DevVah[d] + 1];
				else
					y = edge[b.DevVal[d]];

				float x0 = XOfIndex(chartControl, b.BlockIdx0[b.DevFrom[d]], lastIdx, barDistance) - barDistance * 0.5f;
				float x1;
				if (d + 1 < b.DevCount)
					x1 = XOfIndex(chartControl, b.BlockIdx0[b.DevFrom[d + 1]], lastIdx, barDistance) - barDistance * 0.5f;
				else
					x1 = XOfIndex(chartControl, b.BlockIdx1[b.DevTo[d]], lastIdx, barDistance) + barDistance * 0.5f;

				HLine(x0, x1, y, brush, OtherWidth(), null);
				if (d > 0 && y != previousY)
					VLine(x0, previousY, y, brush, OtherWidth());
				previousY = y;
			}
		}

		// Chart bar holding the first and the last minute of every block. Blocks are in time order, so one forward walk is enough.
		private void ResolveBlockBars(TpoBuilt b, int barCount)
		{
			if (b.BlockIdxDone || b.BlockStart == null)
				return;

			int count = b.BlockStart.Length;
			if (b.BlockIdx0 == null)
			{
				b.BlockIdx0 = new int[count];
				b.BlockIdx1 = new int[count];
			}

			bool complete = true;
			int index = count > 0 ? FirstBarAfter(b.BlockStart[0], barCount) : 0;

			for (int k = 0; k < count; k++)
			{
				while (index < barCount && ChartSeries().GetTime(index) <= b.BlockStart[k])
					index++;
				b.BlockIdx0[k] = index;

				DateTime lastMinuteOpen = b.BlockEnd[k].AddMinutes(-1);
				int end = index;
				while (end < barCount && ChartSeries().GetTime(end) <= lastMinuteOpen)
					end++;
				b.BlockIdx1[k] = end;

				if (index >= barCount || end >= barCount)
					complete = false;
			}

			b.BlockIdxDone = complete;
		}

		private string BuildAnnotation(TpoBuilt b)
		{
			StringBuilder text = new StringBuilder();

			if (PocSummary)
			{
				text.Append("POC ").Append(FormatPrice(RowMidPrice(b, b.Poc)));
				text.Append("  VAH ").Append(FormatPrice(RowTopPrice(b, b.Vah)));
				text.Append("  VAL ").Append(FormatPrice(RowBottomPrice(b, b.Val)));
				text.Append("\n");
			}

			if (StatisticsBox)
			{
				text.Append("Range ").Append(FormatPrice(b.Low)).Append(" - ").Append(FormatPrice(b.High));
				text.Append(" (").Append(TicksBetween(b.High, b.Low).ToString(CultureInfo.CurrentCulture)).Append(" ticks)\n");
				if (b.HasIb)
				{
					text.Append("IB ").Append(FormatPrice(b.IbLow)).Append(" - ").Append(FormatPrice(b.IbHigh));
					text.Append(" (").Append(TicksBetween(b.IbHigh, b.IbLow).ToString(CultureInfo.CurrentCulture)).Append(" ticks)\n");
				}
				text.Append("Value area ").Append(FormatPrice(RowBottomPrice(b, b.Val))).Append(" - ").Append(FormatPrice(RowTopPrice(b, b.Vah))).Append("\n");
				text.Append("TPO ").Append(b.TotalTpo.ToString(CultureInfo.CurrentCulture));
				text.Append("  above POC ").Append(b.TpoAbove.ToString(CultureInfo.CurrentCulture));
				text.Append("  below POC ").Append(b.TpoBelow.ToString(CultureInfo.CurrentCulture)).Append("\n");
				if (b.Vol != null)
					text.Append("Volume ").Append(FormatVolume(b.TotalVol)).Append("\n");
			}

			if (RangeExtension && b.HasIb)
			{
				text.Append("Range extension: up ").Append(TicksBetween(b.ExtensionUp, 0).ToString(CultureInfo.CurrentCulture));
				text.Append(", down ").Append(TicksBetween(b.ExtensionDown, 0).ToString(CultureInfo.CurrentCulture)).Append(" ticks\n");
			}

			if (OneTimeFraming)
			{
				text.Append("One-time framing: ");
				if (b.OtfDirection == 0)
					text.Append("none");
				else
					text.Append(b.OtfDirection > 0 ? "up, " : "down, ").Append(b.OtfRun.ToString(CultureInfo.CurrentCulture)).Append(b.OtfRun == 1 ? " block" : " blocks");
				text.Append("\n");
			}

			if (ShowVolumeSummary && b.Vol != null)
			{
				double pocPrice = RowMidPrice(b, b.Poc);
				double volPocPrice = RowMidPrice(b, b.VPoc);
				long difference = TicksBetween(pocPrice, volPocPrice);

				text.Append("TPO ").Append(b.TotalTpo.ToString(CultureInfo.CurrentCulture));
				text.Append("  POC ").Append(FormatPrice(pocPrice));
				text.Append("  VA ").Append(FormatPrice(RowBottomPrice(b, b.Val))).Append(" - ").Append(FormatPrice(RowTopPrice(b, b.Vah))).Append("\n");
				text.Append("VOL ").Append(FormatVolume(b.TotalVol));
				text.Append("  POC ").Append(FormatPrice(volPocPrice));
				text.Append("  VA ").Append(FormatPrice(RowBottomPrice(b, b.VVal))).Append(" - ").Append(FormatPrice(RowTopPrice(b, b.VVah))).Append("\n");
				text.Append("Time vs volume POC: ").Append(difference.ToString(CultureInfo.CurrentCulture)).Append(difference == 1 ? " tick apart" : " ticks apart").Append("\n");
			}

			return text.ToString().TrimEnd('\n');
		}

		private float XOfIndex(ChartControl chartControl, int index, int lastIdx, float barDistance)
		{
			if (index < 0)
				index = 0;
			if (index <= lastIdx)
				return chartControl.GetXByBarIndex(ChartBars, index);
			return chartControl.GetXByBarIndex(ChartBars, lastIdx) + (index - lastIdx) * barDistance;
		}

		// The chart's own bars. This indicator also loads a 1-minute series, and while drawing, the plain
		// "Bars" property points at whichever series updated last, usually the 1-minute one. Bar positions
		// on the chart must always be looked up in the chart's series, BarsArray[0].
		private Bars ChartSeries()
		{
			return (BarsArray != null && BarsArray.Length > 0) ? BarsArray[0] : null;
		}

		// First chart bar whose time stamp is later than 'time'; barCount when there is none yet.
		private int FirstBarAfter(DateTime time, int barCount)
		{
			int lo = 0;
			int hi = barCount;
			while (lo < hi)
			{
				int mid = lo + (hi - lo) / 2;
				if (ChartSeries().GetTime(mid) > time)
					hi = mid;
				else
					lo = mid + 1;
			}
			return lo;
		}

		private int StartIndexOf(TpoSession s, int barCount)
		{
			if (s.StartIdx >= 0)
				return s.StartIdx;
			int index = FirstBarAfter(s.FirstBarOpen, barCount);
			if (index < barCount)
				s.StartIdx = index;
			return index;
		}

		// First chart bar after a finished session. Cached once it exists.
		private int EndIndexOf(TpoSession s, DateTime lastBarClose, int barCount)
		{
			if (s.EndIdx >= 0)
				return s.EndIdx;
			int index = FirstBarAfter(lastBarClose, barCount);
			if (index < barCount)
				s.EndIdx = index;
			return index;
		}

		private double RowBottomPrice(TpoBuilt b, int row)
		{
			return (b.RowMin + row) * b.RowTicks * tickSize;
		}

		private double RowTopPrice(TpoBuilt b, int row)
		{
			return ((b.RowMin + row + 1) * b.RowTicks - 1) * tickSize;
		}

		private double RowMidPrice(TpoBuilt b, int row)
		{
			return ((b.RowMin + row) * b.RowTicks + (b.RowTicks - 1) / 2) * tickSize;
		}

		private string FormatPrice(double price)
		{
			return price.ToString("F" + priceDecimals.ToString(CultureInfo.InvariantCulture), CultureInfo.CurrentCulture);
		}

		private static string FormatVolume(double volume)
		{
			if (volume >= 1e9)
				return (volume / 1e9).ToString("0.00", CultureInfo.CurrentCulture) + "B";
			if (volume >= 1e6)
				return (volume / 1e6).ToString("0.00", CultureInfo.CurrentCulture) + "M";
			if (volume >= 1e4)
				return (volume / 1e3).ToString("0.0", CultureInfo.CurrentCulture) + "K";
			return volume.ToString("0.##", CultureInfo.CurrentCulture);
		}

		#endregion

		#region Properties: 1. General

		[Display(Name = "Session Type", Description = "What one profile covers: a day, a week or a month.", GroupName = "1. General", Order = 1)]
		public TpoMarketProfileTypes.TpoSessionType SessionType { get; set; }

		[Display(Name = "Trading Hours", Description = "RTH: regular trading hours, the New York session, 09:30 to 16:00 New York time. ETH: the overnight session, Asia and London, 18:00 to 09:30 New York time. The indicator converts from the time zone NinjaTrader is set to, so the sessions land at the right local time on your chart.", GroupName = "1. General", Order = 2)]
		public TpoMarketProfileTypes.TpoSessionHours SessionHours { get; set; }

		[Display(Name = "Block Size", Description = "The time slice each block represents: 5 minutes, 30 minutes, 1 hour or 4 hours.", GroupName = "1. General", Order = 3)]
		public TpoMarketProfileTypes.TpoBlockSize BlockSize { get; set; }

		[Range(1, 1000000)]
		[Display(Name = "Ticks Per Bar", Description = "Price height of one profile row in ticks. 10 ticks is 2.50 points on NQ.", GroupName = "1. General", Order = 4)]
		public int TicksPerRow { get; set; }

		[Range(0, 2000)]
		[Display(Name = "Profile Spacing", Description = "Empty space in pixels kept at the right end of every profile.", GroupName = "1. General", Order = 5)]
		public int ProfileSpacing { get; set; }

		[Range(1, 500)]
		[Display(Name = "Rolling Composite Sessions", Description = "Number of sessions combined in every profile: each session is drawn together with the sessions before it. 1 draws every session on its own. Merging is switched off above 1.", GroupName = "1. General", Order = 6)]
		public int RollingCompositeSessions { get; set; }

		#endregion

		#region Properties: 2. Color

		[Display(Name = "Color Scheme", Description = "Colours of the blocks. BySession: the RTH Blocks colour (dodger blue) when Trading Hours is RTH and the ETH Blocks colour (green) when it is ETH. DodgerBlue: always dodger blue. The others shade the blocks through the session. Used while Custom Colors is off; every other colour is set in the Colors group.", GroupName = "2. Color", Order = 1)]
		public TpoMarketProfileTypes.TpoPalette Palette { get; set; }

		[Display(Name = "Custom Colors", Description = "On: the blocks use the seven 'Custom:' colours from the Colors group, for RTH and ETH alike. Off: the blocks use the Color Scheme and the 'Custom:' colours are ignored.", GroupName = "2. Color", Order = 2)]
		public bool CustomColors { get; set; }

		[Display(Name = "Block Display", Description = "How blocks are drawn: with letters, plain blocks, letters only, or a solid histogram.", GroupName = "2. Color", Order = 3)]
		public TpoMarketProfileTypes.TpoBlockDisplay BlockDisplay { get; set; }

		[Display(Name = "Block Color Mode", Description = "Period: colour follows the time of the block. Heat: colour follows the row width. Session: colour follows the session inside a combined profile. Solid: one colour.", GroupName = "2. Color", Order = 4)]
		public TpoMarketProfileTypes.TpoBlockColorMode BlockColorMode { get; set; }

		[Display(Name = "TPO Line Style", Description = "Style of the point of control and value area lines.", GroupName = "2. Color", Order = 5)]
		public TpoMarketProfileTypes.TpoLineStyle LineStyle { get; set; }

		[Display(Name = "Highlight Start/End", Description = "Colour the block where the session opened and the block where it last traded.", GroupName = "2. Color", Order = 6)]
		public bool HighlightStartEnd { get; set; }

		[Display(Name = "Outside VA Colors", Description = "Draw rows outside the value area in the Outside VA colour.", GroupName = "2. Color", Order = 7)]
		public bool OutsideVaColors { get; set; }

		[Display(Name = "Highlight POC Row", Description = "Draw the blocks of the point of control row in the POC Row colour.", GroupName = "2. Color", Order = 8)]
		public bool HighlightPocRow { get; set; }

		[Range(0, 200)]
		[Display(Name = "Block Width (px)", Description = "Fixed block width in pixels. 0 fits the profile to the width of its session.", GroupName = "2. Color", Order = 9)]
		public int BlockWidth { get; set; }

		[Range(1, 200)]
		[Display(Name = "Max Block Width (px)", Description = "Upper limit for the automatic block width.", GroupName = "2. Color", Order = 10)]
		public int MaxBlockWidth { get; set; }

		[Range(5, 100)]
		[Display(Name = "Profile Width %", Description = "Share of the session's width the widest row may use when the block width is automatic.", GroupName = "2. Color", Order = 11)]
		public int ProfileWidthPercent { get; set; }

		[Range(0, 100)]
		[Display(Name = "Block Opacity %", Description = "Opacity of the blocks.", GroupName = "2. Color", Order = 12)]
		public int BlockOpacity { get; set; }

		[Range(6, 40)]
		[Display(Name = "Label Font Size", Description = "Font size of labels, tags and annotations.", GroupName = "2. Color", Order = 13)]
		public int LabelFontSize { get; set; }

		[Display(Name = "Draw Behind Bars", Description = "Draw the profile behind the price bars.", GroupName = "2. Color", Order = 14)]
		public bool DrawBehindBars { get; set; }

		#endregion

		#region Properties: 3. Profile - Core Levels

		[Display(Name = "POC", Description = "Draw the point of control line.", GroupName = "3. Profile - Core Levels", Order = 1)]
		public bool PocShow { get; set; }

		[Display(Name = "POC: Label", Description = "Write the level name next to the line.", GroupName = "3. Profile - Core Levels", Order = 2)]
		public bool PocLabel { get; set; }

		[Display(Name = "POC: Naked", Description = "Extend the line to the right until price trades at it again.", GroupName = "3. Profile - Core Levels", Order = 3)]
		public bool PocNaked { get; set; }

		[Display(Name = "POC: Scale", Description = "Show a price tag for the latest profile's level at the right edge, next to the price scale.", GroupName = "3. Profile - Core Levels", Order = 4)]
		public bool PocScale { get; set; }

		[Range(1, 10)]
		[Display(Name = "POC: Line Thickness", Description = "Thickness in pixels of the point of control line, including where it is extended as a prior or naked POC.", GroupName = "3. Profile - Core Levels", Order = 5)]
		public int PocThickness { get; set; }

		[Range(1, 100)]
		[Display(Name = "Value Area %", Description = "Share of the session's time (TPO count) the value area covers.", GroupName = "3. Profile - Core Levels", Order = 6)]
		public int ValueAreaPercent { get; set; }

		[Display(Name = "VAH", Description = "Draw the value area high line.", GroupName = "3. Profile - Core Levels", Order = 7)]
		public bool VahShow { get; set; }

		[Display(Name = "VAH: Label", Description = "Write the level name next to the line.", GroupName = "3. Profile - Core Levels", Order = 8)]
		public bool VahLabel { get; set; }

		[Display(Name = "VAH: Naked", Description = "Extend the line to the right until price trades at it again.", GroupName = "3. Profile - Core Levels", Order = 9)]
		public bool VahNaked { get; set; }

		[Display(Name = "VAH: Scale", Description = "Show a price tag for the latest profile's level at the right edge, next to the price scale.", GroupName = "3. Profile - Core Levels", Order = 10)]
		public bool VahScale { get; set; }

		[Display(Name = "VAL", Description = "Draw the value area low line.", GroupName = "3. Profile - Core Levels", Order = 11)]
		public bool ValShow { get; set; }

		[Display(Name = "VAL: Label", Description = "Write the level name next to the line.", GroupName = "3. Profile - Core Levels", Order = 12)]
		public bool ValLabel { get; set; }

		[Display(Name = "VAL: Naked", Description = "Extend the line to the right until price trades at it again.", GroupName = "3. Profile - Core Levels", Order = 13)]
		public bool ValNaked { get; set; }

		[Display(Name = "VAL: Scale", Description = "Show a price tag for the latest profile's level at the right edge, next to the price scale.", GroupName = "3. Profile - Core Levels", Order = 14)]
		public bool ValScale { get; set; }

		[Range(1, 10)]
		[Display(Name = "Line Thickness", Description = "Thickness in pixels of every other line: value area high and low, their extensions into the next session, prior high and low, naked levels, volume levels, poor high and low markers, developing lines and range extension. The point of control and the initial balance have their own thickness.", GroupName = "3. Profile - Core Levels", Order = 15)]
		public int LineWidth { get; set; }

		[Display(Name = "Fill Value Area", Description = "Shade the area between value area low and value area high.", GroupName = "3. Profile - Core Levels", Order = 16)]
		public bool FillValueArea { get; set; }

		[Range(0, 100)]
		[Display(Name = "Value Area Fill Opacity %", Description = "Opacity of the value area shading.", GroupName = "3. Profile - Core Levels", Order = 17)]
		public int ValueAreaFillOpacity { get; set; }

		[Display(Name = "Level Price Tags", Description = "Add the price to every level label.", GroupName = "3. Profile - Core Levels", Order = 18)]
		public bool LevelPriceTags { get; set; }

		[Display(Name = "Extend Prior POC", Description = "Extend the previous session's point of control across the current session, labelled pRTH POC or pETH POC.", GroupName = "3. Profile - Core Levels", Order = 19)]
		public bool PriorPocExtend { get; set; }

		[Display(Name = "Extend Prior VAH", Description = "Extend the previous session's value area high across the current session, labelled pRTH VAH or pETH VAH.", GroupName = "3. Profile - Core Levels", Order = 20)]
		public bool PriorVahExtend { get; set; }

		[Display(Name = "Extend Prior VAL", Description = "Extend the previous session's value area low across the current session, labelled pRTH VAL or pETH VAL.", GroupName = "3. Profile - Core Levels", Order = 21)]
		public bool PriorValExtend { get; set; }

		[Display(Name = "Extend Prior High", Description = "Extend the previous session's range high across the current session, labelled pRTH High or pETH High.", GroupName = "3. Profile - Core Levels", Order = 22)]
		public bool PriorHighExtend { get; set; }

		[Display(Name = "Extend Prior Low", Description = "Extend the previous session's range low across the current session, labelled pRTH Low or pETH Low.", GroupName = "3. Profile - Core Levels", Order = 23)]
		public bool PriorLowExtend { get; set; }

		[Display(Name = "Extend From Every Profile", Description = "Off: only the session just before the current one is extended. On: every profile's chosen levels are carried across the profile after it, through the whole chart.", GroupName = "3. Profile - Core Levels", Order = 24)]
		public bool PriorSessionLevels { get; set; }

		[Display(Name = "Extend All Naked POCs", Description = "Extend the point of control of every earlier session that price has not traded at since, all the way to the right edge of the chart, labelled pRTH nPOC or pETH nPOC. A line disappears as soon as price trades at it. Works with the POC line itself switched off.", GroupName = "3. Profile - Core Levels", Order = 25)]
		public bool NakedPocExtend { get; set; }

		#endregion

		#region Properties: 4. Profile - Structure Markers

		[Display(Name = "Initial Balance Range (IBR)", Description = "Mark the initial balance of the current New York session: the high and low of the first minutes after the 09:30 New York open. The lines appear when those minutes are over, use the TPO Line Style, and end where the current profile's level lines end. The next New York open removes them. Earlier sessions are not marked.", GroupName = "4. Profile - Structure Markers", Order = 1)]
		public bool ShowInitialBalance { get; set; }

		[Range(1, 100000)]
		[Display(Name = "IBR Minutes", Description = "Length of the initial balance in minutes. The marked initial balance is measured from the 09:30 New York open; day type, open type and range extension use the first minutes of each profile's own session.", GroupName = "4. Profile - Structure Markers", Order = 2)]
		public int InitialBalanceMinutes { get; set; }

		[Range(1, 10)]
		[Display(Name = "IBR Line Thickness", Description = "Thickness in pixels of the initial balance high and low lines.", GroupName = "4. Profile - Structure Markers", Order = 3)]
		public int IbLineWidth { get; set; }

		[Display(Name = "Poor Highs", Description = "Mark a session high that two or more blocks traded.", GroupName = "4. Profile - Structure Markers", Order = 4)]
		public bool PoorHighShow { get; set; }

		[Display(Name = "Poor Highs: Label", Description = "Write the marker name next to it.", GroupName = "4. Profile - Structure Markers", Order = 5)]
		public bool PoorHighLabel { get; set; }

		[Display(Name = "Poor Highs: Naked", Description = "Extend the level to the right until price trades at it again.", GroupName = "4. Profile - Structure Markers", Order = 6)]
		public bool PoorHighNaked { get; set; }

		[Display(Name = "Poor Highs: Scale", Description = "Show a price tag for the latest profile's poor high at the right edge, next to the price scale.", GroupName = "4. Profile - Structure Markers", Order = 7)]
		public bool PoorHighScale { get; set; }

		[Display(Name = "Poor Lows", Description = "Mark a session low that two or more blocks traded.", GroupName = "4. Profile - Structure Markers", Order = 8)]
		public bool PoorLowShow { get; set; }

		[Display(Name = "Poor Lows: Label", Description = "Write the marker name next to it.", GroupName = "4. Profile - Structure Markers", Order = 9)]
		public bool PoorLowLabel { get; set; }

		[Display(Name = "Poor Lows: Naked", Description = "Extend the level to the right until price trades at it again.", GroupName = "4. Profile - Structure Markers", Order = 10)]
		public bool PoorLowNaked { get; set; }

		[Display(Name = "Poor Lows: Scale", Description = "Show a price tag for the latest profile's poor low at the right edge, next to the price scale.", GroupName = "4. Profile - Structure Markers", Order = 11)]
		public bool PoorLowScale { get; set; }

		[Display(Name = "Single Prints", Description = "Highlight rows inside the profile that only one block traded.", GroupName = "4. Profile - Structure Markers", Order = 12)]
		public bool ShowSinglePrints { get; set; }

		[Display(Name = "Buying / Selling Tails", Description = "Highlight a single-width run at the low (buying tail) or at the high (selling tail).", GroupName = "4. Profile - Structure Markers", Order = 13)]
		public bool ShowTails { get; set; }

		[Range(1, 100)]
		[Display(Name = "Minimum Tail Rows", Description = "Number of single-width rows at an extreme needed to call it a tail.", GroupName = "4. Profile - Structure Markers", Order = 14)]
		public int MinTailRows { get; set; }

		[Display(Name = "Opening Gap", Description = "Mark a New York open (09:30 New York time) above the prior session's high or below its low: a line labelled GAP at that prior high or low, which stops at the bar that trades back to it. A gap its own session does not fill gets a highlighted band over what is left of it, until it is filled. Only New York session trading (09:30-16:00) can fill a gap. Lines use the TPO Line Style and end where the current profile's level lines end. The band's colour is Gap Area under Colors; its strength is Gap Area Opacity %.", GroupName = "4. Profile - Structure Markers", Order = 15)]
		public bool ShowGaps { get; set; }

		[Range(1, 10)]
		[Display(Name = "Gap Line Thickness", Description = "Thickness in pixels of the opening gap line.", GroupName = "4. Profile - Structure Markers", Order = 16)]
		public int GapLineWidth { get; set; }

		[Range(0, 100)]
		[Display(Name = "Gap Area Opacity %", Description = "Opacity of the highlighted band over an unfilled gap.", GroupName = "4. Profile - Structure Markers", Order = 17)]
		public int GapAreaOpacity { get; set; }

		#endregion

		#region Properties: 5. Profile - Annotations

		[Display(Name = "POC Summary", Description = "Print the point of control and value area prices under the profile.", GroupName = "5. Profile - Annotations", Order = 1)]
		public bool PocSummary { get; set; }

		[Display(Name = "Statistics Box", Description = "Print range, initial balance, value area, TPO counts above and below the point of control, and volume in a box under the profile.", GroupName = "5. Profile - Annotations", Order = 2)]
		public bool StatisticsBox { get; set; }

		[Display(Name = "Show TPO Counts", Description = "Write the number of blocks at the end of each row.", GroupName = "5. Profile - Annotations", Order = 3)]
		public bool ShowTpoCounts { get; set; }

		[Display(Name = "One-Time Framing", Description = "Draw a ribbon under the profile: up while each block holds the prior block's low, down while each block holds the prior block's high.", GroupName = "5. Profile - Annotations", Order = 4)]
		public bool OneTimeFraming { get; set; }

		[Display(Name = "Range Extension", Description = "Mark how far the session traded beyond the initial balance range.", GroupName = "5. Profile - Annotations", Order = 5)]
		public bool RangeExtension { get; set; }

		[Display(Name = "Developing POC", Description = "Draw how the point of control moved through the session, block by block.", GroupName = "5. Profile - Annotations", Order = 6)]
		public bool DevelopingPoc { get; set; }

		[Display(Name = "Developing VA", Description = "Draw how the value area high and low moved through the session, block by block.", GroupName = "5. Profile - Annotations", Order = 7)]
		public bool DevelopingVa { get; set; }

		[Display(Name = "Day Type", Description = "Label each profile above its high with its day type: Normal, Normal variation, Trend, Double distribution trend, Neutral (center), Neutral (extreme) or Non-trend.", GroupName = "5. Profile - Annotations", Order = 8)]
		public bool DayType { get; set; }

		[Display(Name = "Open Type", Description = "Label each profile above its high with its open type, read from the first 30 minutes: Open-Drive, Open-Test-Drive, Open-Rejection-Reverse or Open-Auction (in range / out of range).", GroupName = "5. Profile - Annotations", Order = 9)]
		public bool OpenType { get; set; }

		#endregion

		#region Properties: 6. Volume

		[Display(Name = "Enabled", Description = "Draw a volume profile alongside the time profile.", GroupName = "6. Volume", Order = 1)]
		public bool ShowVolumeProfile { get; set; }

		[Display(Name = "Position", Description = "Left: before the blocks. Right: at the right end of the session. OverTpo: on top of the blocks.", GroupName = "6. Volume", Order = 2)]
		public TpoMarketProfileTypes.TpoVolumePosition VolumePosition { get; set; }

		[Display(Name = "Value", Description = "Write the volume of each row on its bar.", GroupName = "6. Volume", Order = 3)]
		public bool VolumeValues { get; set; }

		[Range(5, 100)]
		[Display(Name = "Width %", Description = "Share of the session's width the widest volume bar may use.", GroupName = "6. Volume", Order = 4)]
		public int VolumeWidthPercent { get; set; }

		[Display(Name = "Volume Color Grade", Description = "Shade each bar by its volume, from the Volume: Outside Value Area colour to the Volume: Inside Value Area colour.", GroupName = "6. Volume", Order = 5)]
		public bool VolumeColorGrade { get; set; }

		[Range(0, int.MaxValue)]
		[Display(Name = "Volume Threshold", Description = "Hide rows with less volume than this.", GroupName = "6. Volume", Order = 6)]
		public int VolumeThreshold { get; set; }

		[Range(0, 100)]
		[Display(Name = "Volume Opacity %", Description = "Opacity of the volume bars.", GroupName = "6. Volume", Order = 7)]
		public int VolumeOpacity { get; set; }

		[Display(Name = "Volume POC", Description = "Draw the volume point of control line.", GroupName = "6. Volume", Order = 8)]
		public bool VolPocShow { get; set; }

		[Display(Name = "Volume POC: Extend", Description = "Extend the line to the right until price trades at it again.", GroupName = "6. Volume", Order = 9)]
		public bool VolPocExtend { get; set; }

		[Display(Name = "Volume POC: Scale", Description = "Show a price tag for the latest profile's level at the right edge, next to the price scale.", GroupName = "6. Volume", Order = 10)]
		public bool VolPocScale { get; set; }

		[Display(Name = "Volume VAH", Description = "Draw the volume value area high line.", GroupName = "6. Volume", Order = 11)]
		public bool VolVahShow { get; set; }

		[Display(Name = "Volume VAH: Extend", Description = "Extend the line to the right until price trades at it again.", GroupName = "6. Volume", Order = 12)]
		public bool VolVahExtend { get; set; }

		[Display(Name = "Volume VAH: Scale", Description = "Show a price tag for the latest profile's level at the right edge, next to the price scale.", GroupName = "6. Volume", Order = 13)]
		public bool VolVahScale { get; set; }

		[Display(Name = "Volume VAL", Description = "Draw the volume value area low line.", GroupName = "6. Volume", Order = 14)]
		public bool VolValShow { get; set; }

		[Display(Name = "Volume VAL: Extend", Description = "Extend the line to the right until price trades at it again.", GroupName = "6. Volume", Order = 15)]
		public bool VolValExtend { get; set; }

		[Display(Name = "Volume VAL: Scale", Description = "Show a price tag for the latest profile's level at the right edge, next to the price scale.", GroupName = "6. Volume", Order = 16)]
		public bool VolValScale { get; set; }

		[Display(Name = "Volume Summary", Description = "Print time and volume point of control and value area under each profile, and how far apart the two points of control are.", GroupName = "6. Volume", Order = 17)]
		public bool ShowVolumeSummary { get; set; }

		#endregion

		#region Properties: 7. Merge and Expand

		[Display(Name = "Profile Menu", Description = "Double-click or right-click a profile for a menu: Expand this profile, Merge with previous profile, Return profile to original. Shift+Click also merges, Ctrl+Shift+Click also returns a profile to original.", GroupName = "7. Merge and Expand", Order = 1)]
		public bool EnableClickMerge { get; set; }

		[Range(0, 1000)]
		[Display(Name = "Merge Latest With Prior", Description = "Always merge the current session with this many prior sessions. 0 turns it off.", GroupName = "7. Merge and Expand", Order = 2)]
		public int MergeLatestCount { get; set; }

		[Display(Name = "Merged Sessions", Description = "Sessions merged into the session before them, separated by semicolons. D20261002 is the day 2 Oct 2026, W and M prefixes are weeks (Monday date) and months. Filled in by the profile menu; clear it to undo every merge.", GroupName = "7. Merge and Expand", Order = 3)]
		public string MergedSessions { get; set; }

		[Display(Name = "Expanded Sessions", Description = "Profiles drawn expanded, with every block in its own time column, separated by semicolons. Filled in by the profile menu; clear it to collapse every profile.", GroupName = "7. Merge and Expand", Order = 4)]
		public string ExpandedSessions { get; set; }

		#endregion

		#region Properties: 8. Colors

		[XmlIgnore]
		[Display(Name = "RTH Blocks", Description = "Color Scheme BySession: colour of the blocks when Trading Hours is RTH.", GroupName = "8. Colors", Order = 1)]
		public Brush RthBlockBrush { get; set; }

		[Browsable(false)]
		public string RthBlockBrushSerialize
		{
			get { return Serialize.BrushToString(RthBlockBrush); }
			set { RthBlockBrush = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "ETH Blocks", Description = "Color Scheme BySession: colour of the blocks when Trading Hours is ETH.", GroupName = "8. Colors", Order = 2)]
		public Brush EthBlockBrush { get; set; }

		[Browsable(false)]
		public string EthBlockBrushSerialize
		{
			get { return Serialize.BrushToString(EthBlockBrush); }
			set { EthBlockBrush = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Custom: Period Early", Description = "Custom Colors on, Period and Session modes: colour of the first blocks.", GroupName = "8. Colors", Order = 3)]
		public Brush PeriodEarlyBrush { get; set; }

		[Browsable(false)]
		public string PeriodEarlyBrushSerialize
		{
			get { return Serialize.BrushToString(PeriodEarlyBrush); }
			set { PeriodEarlyBrush = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Custom: Period Middle", Description = "Custom Colors on, Period and Session modes: colour of the middle blocks.", GroupName = "8. Colors", Order = 4)]
		public Brush PeriodMidBrush { get; set; }

		[Browsable(false)]
		public string PeriodMidBrushSerialize
		{
			get { return Serialize.BrushToString(PeriodMidBrush); }
			set { PeriodMidBrush = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Custom: Period Late", Description = "Custom Colors on, Period and Session modes: colour of the last blocks.", GroupName = "8. Colors", Order = 5)]
		public Brush PeriodLateBrush { get; set; }

		[Browsable(false)]
		public string PeriodLateBrushSerialize
		{
			get { return Serialize.BrushToString(PeriodLateBrush); }
			set { PeriodLateBrush = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Custom: Heat Narrow Rows", Description = "Custom Colors on, Heat mode: colour of the narrowest rows.", GroupName = "8. Colors", Order = 6)]
		public Brush HeatLowBrush { get; set; }

		[Browsable(false)]
		public string HeatLowBrushSerialize
		{
			get { return Serialize.BrushToString(HeatLowBrush); }
			set { HeatLowBrush = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Custom: Heat Medium Rows", Description = "Custom Colors on, Heat mode: colour of medium-width rows.", GroupName = "8. Colors", Order = 7)]
		public Brush HeatMidBrush { get; set; }

		[Browsable(false)]
		public string HeatMidBrushSerialize
		{
			get { return Serialize.BrushToString(HeatMidBrush); }
			set { HeatMidBrush = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Custom: Heat Wide Rows", Description = "Custom Colors on, Heat mode: colour of the widest rows.", GroupName = "8. Colors", Order = 8)]
		public Brush HeatHighBrush { get; set; }

		[Browsable(false)]
		public string HeatHighBrushSerialize
		{
			get { return Serialize.BrushToString(HeatHighBrush); }
			set { HeatHighBrush = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Custom: Solid", Description = "Custom Colors on, Solid mode: colour of every block.", GroupName = "8. Colors", Order = 9)]
		public Brush SolidBrush { get; set; }

		[Browsable(false)]
		public string SolidBrushSerialize
		{
			get { return Serialize.BrushToString(SolidBrush); }
			set { SolidBrush = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Outside VA", Description = "Colour of rows outside the value area when Outside VA Colors is on.", GroupName = "8. Colors", Order = 10)]
		public Brush OutsideValueAreaBrush { get; set; }

		[Browsable(false)]
		public string OutsideValueAreaBrushSerialize
		{
			get { return Serialize.BrushToString(OutsideValueAreaBrush); }
			set { OutsideValueAreaBrush = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "POC Row", Description = "Colour of the blocks in the point of control row.", GroupName = "8. Colors", Order = 11)]
		public Brush PocRowBrush { get; set; }

		[Browsable(false)]
		public string PocRowBrushSerialize
		{
			get { return Serialize.BrushToString(PocRowBrush); }
			set { PocRowBrush = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Letters", Description = "Colour of the block letters.", GroupName = "8. Colors", Order = 12)]
		public Brush LetterBrush { get; set; }

		[Browsable(false)]
		public string LetterBrushSerialize
		{
			get { return Serialize.BrushToString(LetterBrush); }
			set { LetterBrush = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Start Block", Description = "Highlight Start/End: colour of the block where the session opened.", GroupName = "8. Colors", Order = 13)]
		public Brush OpenBlockBrush { get; set; }

		[Browsable(false)]
		public string OpenBlockBrushSerialize
		{
			get { return Serialize.BrushToString(OpenBlockBrush); }
			set { OpenBlockBrush = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "End Block", Description = "Highlight Start/End: colour of the block where the session last traded.", GroupName = "8. Colors", Order = 14)]
		public Brush CloseBlockBrush { get; set; }

		[Browsable(false)]
		public string CloseBlockBrushSerialize
		{
			get { return Serialize.BrushToString(CloseBlockBrush); }
			set { CloseBlockBrush = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Point of Control (POC)", Description = "Colour of the point of control line, label and tag.", GroupName = "8. Colors", Order = 15)]
		public Brush PocLineBrush { get; set; }

		[Browsable(false)]
		public string PocLineBrushSerialize
		{
			get { return Serialize.BrushToString(PocLineBrush); }
			set { PocLineBrush = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Value Area High (VAH)", Description = "Colour of the value area high line, label and tag.", GroupName = "8. Colors", Order = 16)]
		public Brush VahBrush { get; set; }

		[Browsable(false)]
		public string VahBrushSerialize
		{
			get { return Serialize.BrushToString(VahBrush); }
			set { VahBrush = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Value Area Low (VAL)", Description = "Colour of the value area low line, label and tag.", GroupName = "8. Colors", Order = 17)]
		public Brush ValBrush { get; set; }

		[Browsable(false)]
		public string ValBrushSerialize
		{
			get { return Serialize.BrushToString(ValBrush); }
			set { ValBrush = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Prior High", Description = "Colour of the extended prior range high line and label.", GroupName = "8. Colors", Order = 18)]
		public Brush PriorHighBrush { get; set; }

		[Browsable(false)]
		public string PriorHighBrushSerialize
		{
			get { return Serialize.BrushToString(PriorHighBrush); }
			set { PriorHighBrush = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Prior Low", Description = "Colour of the extended prior range low line and label.", GroupName = "8. Colors", Order = 19)]
		public Brush PriorLowBrush { get; set; }

		[Browsable(false)]
		public string PriorLowBrushSerialize
		{
			get { return Serialize.BrushToString(PriorLowBrush); }
			set { PriorLowBrush = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Gap Line", Description = "Colour of the opening gap line and its GAP label.", GroupName = "8. Colors", Order = 20)]
		public Brush GapLineBrush { get; set; }

		[Browsable(false)]
		public string GapLineBrushSerialize
		{
			get { return Serialize.BrushToString(GapLineBrush); }
			set { GapLineBrush = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Gap Area", Description = "Colour of the highlighted band over an unfilled gap. Its strength is set by Gap Area Opacity %.", GroupName = "8. Colors", Order = 21)]
		public Brush GapAreaBrush { get; set; }

		[Browsable(false)]
		public string GapAreaBrushSerialize
		{
			get { return Serialize.BrushToString(GapAreaBrush); }
			set { GapAreaBrush = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Value Area Fill", Description = "Colour of the value area shading.", GroupName = "8. Colors", Order = 22)]
		public Brush ValueAreaFillBrush { get; set; }

		[Browsable(false)]
		public string ValueAreaFillBrushSerialize
		{
			get { return Serialize.BrushToString(ValueAreaFillBrush); }
			set { ValueAreaFillBrush = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Initial Balance Range (IBR)", Description = "Colour of the initial balance high and low lines.", GroupName = "8. Colors", Order = 23)]
		public Brush InitialBalanceBrush { get; set; }

		[Browsable(false)]
		public string InitialBalanceBrushSerialize
		{
			get { return Serialize.BrushToString(InitialBalanceBrush); }
			set { InitialBalanceBrush = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Range Extension", Description = "Colour of the range extension marks.", GroupName = "8. Colors", Order = 24)]
		public Brush RangeExtensionBrush { get; set; }

		[Browsable(false)]
		public string RangeExtensionBrushSerialize
		{
			get { return Serialize.BrushToString(RangeExtensionBrush); }
			set { RangeExtensionBrush = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Poor Highs", Description = "Colour of the poor high marker.", GroupName = "8. Colors", Order = 25)]
		public Brush PoorHighBrush { get; set; }

		[Browsable(false)]
		public string PoorHighBrushSerialize
		{
			get { return Serialize.BrushToString(PoorHighBrush); }
			set { PoorHighBrush = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Poor Lows", Description = "Colour of the poor low marker.", GroupName = "8. Colors", Order = 26)]
		public Brush PoorLowBrush { get; set; }

		[Browsable(false)]
		public string PoorLowBrushSerialize
		{
			get { return Serialize.BrushToString(PoorLowBrush); }
			set { PoorLowBrush = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Single Prints", Description = "Colour of single print rows.", GroupName = "8. Colors", Order = 27)]
		public Brush SinglePrintBrush { get; set; }

		[Browsable(false)]
		public string SinglePrintBrushSerialize
		{
			get { return Serialize.BrushToString(SinglePrintBrush); }
			set { SinglePrintBrush = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Buying Tail", Description = "Colour of buying tail rows.", GroupName = "8. Colors", Order = 28)]
		public Brush BuyingTailBrush { get; set; }

		[Browsable(false)]
		public string BuyingTailBrushSerialize
		{
			get { return Serialize.BrushToString(BuyingTailBrush); }
			set { BuyingTailBrush = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Selling Tail", Description = "Colour of selling tail rows.", GroupName = "8. Colors", Order = 29)]
		public Brush SellingTailBrush { get; set; }

		[Browsable(false)]
		public string SellingTailBrushSerialize
		{
			get { return Serialize.BrushToString(SellingTailBrush); }
			set { SellingTailBrush = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Developing POC", Description = "Colour of the developing point of control line.", GroupName = "8. Colors", Order = 30)]
		public Brush DevelopingPocBrush { get; set; }

		[Browsable(false)]
		public string DevelopingPocBrushSerialize
		{
			get { return Serialize.BrushToString(DevelopingPocBrush); }
			set { DevelopingPocBrush = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Developing VA", Description = "Colour of the developing value area lines.", GroupName = "8. Colors", Order = 31)]
		public Brush DevelopingVaBrush { get; set; }

		[Browsable(false)]
		public string DevelopingVaBrushSerialize
		{
			get { return Serialize.BrushToString(DevelopingVaBrush); }
			set { DevelopingVaBrush = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "One-Time Framing Up", Description = "Colour of the one-time-framing ribbon while framing up.", GroupName = "8. Colors", Order = 32)]
		public Brush OtfUpBrush { get; set; }

		[Browsable(false)]
		public string OtfUpBrushSerialize
		{
			get { return Serialize.BrushToString(OtfUpBrush); }
			set { OtfUpBrush = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "One-Time Framing Down", Description = "Colour of the one-time-framing ribbon while framing down.", GroupName = "8. Colors", Order = 33)]
		public Brush OtfDownBrush { get; set; }

		[Browsable(false)]
		public string OtfDownBrushSerialize
		{
			get { return Serialize.BrushToString(OtfDownBrush); }
			set { OtfDownBrush = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Volume: Inside Value Area", Description = "Colour of volume bars inside the volume value area, and the high end of the colour grade.", GroupName = "8. Colors", Order = 34)]
		public Brush VolumeBrush { get; set; }

		[Browsable(false)]
		public string VolumeBrushSerialize
		{
			get { return Serialize.BrushToString(VolumeBrush); }
			set { VolumeBrush = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Volume: Outside Value Area", Description = "Colour of volume bars outside the volume value area, and the low end of the colour grade.", GroupName = "8. Colors", Order = 35)]
		public Brush VolumeOutsideBrush { get; set; }

		[Browsable(false)]
		public string VolumeOutsideBrushSerialize
		{
			get { return Serialize.BrushToString(VolumeOutsideBrush); }
			set { VolumeOutsideBrush = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Volume: Point of Control", Description = "Colour of the volume point of control line, label and tag.", GroupName = "8. Colors", Order = 36)]
		public Brush VolumePocBrush { get; set; }

		[Browsable(false)]
		public string VolumePocBrushSerialize
		{
			get { return Serialize.BrushToString(VolumePocBrush); }
			set { VolumePocBrush = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Volume: Value Area High", Description = "Colour of the volume value area high line, label and tag.", GroupName = "8. Colors", Order = 37)]
		public Brush VolumeVahBrush { get; set; }

		[Browsable(false)]
		public string VolumeVahBrushSerialize
		{
			get { return Serialize.BrushToString(VolumeVahBrush); }
			set { VolumeVahBrush = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Volume: Value Area Low", Description = "Colour of the volume value area low line, label and tag.", GroupName = "8. Colors", Order = 38)]
		public Brush VolumeValBrush { get; set; }

		[Browsable(false)]
		public string VolumeValBrushSerialize
		{
			get { return Serialize.BrushToString(VolumeValBrush); }
			set { VolumeValBrush = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Text", Description = "Colour of annotations, row counts and volume numbers.", GroupName = "8. Colors", Order = 39)]
		public Brush TextBrush { get; set; }

		[Browsable(false)]
		public string TextBrushSerialize
		{
			get { return Serialize.BrushToString(TextBrush); }
			set { TextBrush = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Day Type Label", Description = "Colour of the day type label above the profile.", GroupName = "8. Colors", Order = 40)]
		public Brush DayTypeBrush { get; set; }

		[Browsable(false)]
		public string DayTypeBrushSerialize
		{
			get { return Serialize.BrushToString(DayTypeBrush); }
			set { DayTypeBrush = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Open Type Label", Description = "Colour of the open type label above the profile.", GroupName = "8. Colors", Order = 41)]
		public Brush OpenTypeBrush { get; set; }

		[Browsable(false)]
		public string OpenTypeBrushSerialize
		{
			get { return Serialize.BrushToString(OpenTypeBrush); }
			set { OpenTypeBrush = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Statistics Box Background", Description = "Background colour of the statistics box.", GroupName = "8. Colors", Order = 42)]
		public Brush StatisticsBackgroundBrush { get; set; }

		[Browsable(false)]
		public string StatisticsBackgroundBrushSerialize
		{
			get { return Serialize.BrushToString(StatisticsBackgroundBrush); }
			set { StatisticsBackgroundBrush = Serialize.StringToBrush(value); }
		}

		#endregion
	}
}
