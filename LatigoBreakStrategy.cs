// LatigoBreakStrategy — opening-range breakout of the first 30s candle at the
// 18:00 ET Globex reopen, with whipsaw (fake-breakout) veto and re-arm.
//
// 1:1 port of the validated research engine (research/events.py +
// research/phase1.confirm_index in this repo; the research label timeout X is
// deliberately omitted live — the causal detector never used it). Signal
// defaults = best Phase-1 grid cell (hold 30s, extension 0.25xR30).
// HONEST-USE NOTE: research on 203 NQ sessions shows the naive chase loses
// ~$110/trade and the best filter cell stays slightly below zero — this
// strategy is a Playback/sim laboratory for iterating the redesign, not a
// validated edge (see docs/research/).
//
// v2 trade management (ported from the twice-audited BigPrintsStrategy.cs):
// ATR brackets (stop = AtrStopMult x Wilder ATR on the PRIMARY chart series —
// the chart timeframe defines the ATR's meaning), optional breakeven at a %
// of the entry->target run, real-time daily profit/loss lockout, and an
// optional account-wide shared governor so several instances (markets) all
// flatten together on a combined breach (default OFF).
//
// Requires an ETH/24-7 trading-hours template (session must BEGIN at the
// 18:00 ET reopen). Strategy Analyzer runs need Order Fill Resolution
// High / Tick / 1.
#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class LatigoBreakStrategy : Strategy
    {
        private enum Phase { WaitSession, Candle, Armed, Active, Pending, InPosition, Done }

        private const int TickIdx = 1;
        private const string SigLong = "LB_Long";
        private const string SigShort = "LB_Short";

        private Phase _phase = Phase.WaitSession;
        private SessionIterator _sess;
        private DateTime _t0 = DateTime.MinValue;   // session begin (18:00 ET reopen)
        private DateTime _lastTick = DateTime.MinValue;

        private double _h = double.MinValue, _l = double.MaxValue;
        private int _r30;

        private bool _insidePrev;
        private int _side;                          // +1 up-break, -1 down-break
        private double _level, _breakPx;
        private DateTime _tBreak;
        private int _maxExt;                        // ticks beyond level (cumulative)

        private int _trades, _tag;
        private bool _entryPending, _timeExitSent, _flattenPending;
        private readonly List<string> _drawTags = new List<string>();

        // ATR on the PRIMARY series (hand-rolled Wilder — nt8c cannot resolve the
        // ATR() system-indicator wrapper; same pattern as BigPrints/FVGFlow).
        private Series<double> _atrSeries;
        private double _atrNow;                     // latest primary ATR, read from the tick branch

        // Bracket/breakeven state for the open trade
        private double _targetPx, _stopPx;
        private bool _beApplied;

        // --- Daily risk governor (ported from BigPrintsStrategy.cs, 2026-07-29 audits) ---
        private double _dayStartRealized;
        private bool _dailyLockout;

        // Shared (account-wide) mode: STATIC registry = shared across every instance of THIS
        // strategy class in the NT8 process. One entry per account: trading day, day baseline
        // (first instance writes it, later ones ADOPT it), and the breach broadcast. Re-read
        // under lock on every tick — never cached — so a wipe-and-recreate can't split the
        // group, and a peak seen only by another instrument's ticks still locks this one out.
        private sealed class AcctDayGov
        {
            public DateTime Day;
            public double Baseline;
            public volatile bool Breached;
        }
        private static readonly object _acctGovLock = new object();
        private static readonly Dictionary<string, AcctDayGov> _acctGov = new Dictionary<string, AcctDayGov>();
        private DateTime _acctSessionDay = DateTime.MinValue;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "LatigoBreakStrategy";
                Description = "Opening 30s-candle breakout at the 18:00 ET reopen with whipsaw veto. Sim/Playback lab — see repo docs/research for the honest stats.";
                Calculate = Calculate.OnEachTick;
                EntriesPerDirection = 1;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds = 30;
                BarsRequiredToTrade = 0;
                IsInstantiatedOnEachOptimizationIteration = false;

                UseWhipsawFilter = true;
                HoldSeconds = 30;
                ExtensionR30 = 0.25;
                CandleSeconds = 30;
                WatchEndSeconds = 930;
                EntryDeadlineSeconds = 1200;
                MinR30Ticks = 4;

                Contracts = 1;
                AtrPeriod = 14;
                AtrStopMult = 2.0;
                AtrTargetMult = 2.0;
                MaxTradesPerSession = 1;
                TimeStopSeconds = 1800;

                UseBreakeven = false;
                BreakevenPercent = 50;
                BreakevenOffsetTicks = 0;

                DailyProfitTargetUSD = 500;
                DailyLossLimitUSD = 300;
                UseAccountDailyPnL = false;         // multi-market shared close OFF by default

                ShowDrawings = true;
            }
            else if (State == State.Configure)
            {
                AddDataSeries(BarsPeriodType.Tick, 1);
            }
            else if (State == State.DataLoaded)
            {
                _sess = new SessionIterator(BarsArray[TickIdx]);
                _atrSeries = new Series<double>(this);
                // Playback rewinds reset the account — start the shared governor clean
                if (Account != null)
                    lock (_acctGovLock) _acctGov.Remove(Account.Name);
                ResetSession(false);
            }
        }

        // removeDrawings: true only on Playback rewind — the discarded pass's
        // objects get wiped; on a normal session rollover history stays on chart.
        private void ResetSession(bool removeDrawings)
        {
            if (removeDrawings)
            {
                foreach (string tag in _drawTags)
                    RemoveDrawObject(tag);
                // Rewind also discards the pass that may have set the shared breach flag
                if (Account != null)
                    lock (_acctGovLock) _acctGov.Remove(Account.Name);
            }
            _drawTags.Clear();

            _phase = Phase.WaitSession;
            _h = double.MinValue; _l = double.MaxValue;
            _r30 = 0;
            _insidePrev = true;
            _side = 0; _level = 0; _breakPx = 0; _maxExt = 0;
            _tBreak = DateTime.MinValue;
            _trades = 0;
            _entryPending = false;
            _timeExitSent = false;
            _flattenPending = false;
            _targetPx = 0; _stopPx = 0;
            _beApplied = false;
            _dailyLockout = false;
            _dayStartRealized = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit;
        }

        private string Tag(string t)
        {
            _drawTags.Add(t);
            return t;
        }

        protected override void OnBarUpdate()
        {
            if (BarsInProgress == 0)                 // primary series: ATR bookkeeping only
            {
                if (CurrentBar >= 1)
                {
                    _atrSeries[0] = ComputeAtr();
                    _atrNow = _atrSeries[0];
                }
                return;
            }
            if (BarsInProgress != TickIdx || CurrentBars[TickIdx] < 0)
                return;

            DateTime t = Times[TickIdx][0];
            double px = Closes[TickIdx][0];

            if (t < _lastTick)                       // Playback rewind: hard reset
                ResetSession(true);
            _lastTick = t;

            if (BarsArray[TickIdx].IsFirstBarOfSession)
            {
                ResetSession(false);
                _sess.GetNextSession(t, true);
                _t0 = _sess.ActualSessionBegin;
                _tag++;
                _phase = Phase.Candle;
                _acctSessionDay = _t0.Date;
                if (UseAccountDailyPnL)
                    CurrentAccountDay();             // prime the shared baseline for this day
            }

            if (_t0 != DateTime.MinValue)
                CheckRiskGovernor();                 // real-time daily limits — runs in EVERY phase

            if (_phase == Phase.WaitSession || _phase == Phase.Done || _t0 == DateTime.MinValue)
            {
                CheckTimeStop(t);
                return;
            }

            double secs = (t - _t0).TotalSeconds;

            if (_phase == Phase.Candle)
            {
                if (secs < 0)
                    return;                          // pre-session jitter (parity with t0 <= ts window)
                if (secs < CandleSeconds)
                {
                    if (px > _h) _h = px;
                    if (px < _l) _l = px;
                    return;
                }
                FreezeCandle();
                if (_phase == Phase.Done)
                    return;                          // degenerate / empty candle
            }

            // Global session clocks
            if (_phase == Phase.Armed || _phase == Phase.Active)
            {
                if (secs > EntryDeadlineSeconds)     // no more entries this session
                {
                    _phase = Phase.Done;
                    return;
                }
            }
            CheckTimeStop(t);
            if (_phase == Phase.InPosition)
                ManagePosition(px);
            if (_phase == Phase.Pending || _phase == Phase.InPosition || _phase == Phase.Done)
                return;

            bool up = px >= _h + TickSize * 0.5;
            bool dn = px <= _l - TickSize * 0.5;
            bool inside = !up && !dn;

            if (_phase == Phase.Active)
            {
                int ext = ExtTicks(px);
                if (ext > _maxExt) _maxExt = ext;

                bool reIn = _side > 0 ? px <= _h - TickSize * 0.5
                                      : px >= _l + TickSize * 0.5;
                if (reIn)
                {
                    int oldSide = _side;
                    double retS = (t - _tBreak).TotalSeconds;
                    DrawWhipsaw(t, px, retS, oldSide);
                    _phase = Phase.Armed;
                    _side = 0;
                    // Gap-through: same print already beyond the OPPOSITE level
                    bool beyondOpp = oldSide > 0 ? dn : up;
                    if (beyondOpp && secs <= WatchEndSeconds)
                        OpenBreak(t, px, -oldSide);
                }
                else if (ConfirmReady(t))
                {
                    SubmitEntry(t, px);
                }
            }

            if (_phase == Phase.Armed && _insidePrev && (up || dn) && secs <= WatchEndSeconds)
                OpenBreak(t, px, up ? 1 : -1);

            _insidePrev = inside;
        }

        private void FreezeCandle()
        {
            if (_h == double.MinValue)               // no prints in the candle window
            {
                _phase = Phase.Done;
                return;
            }
            _r30 = (int)Math.Round((_h - _l) / TickSize);
            if (_r30 < MinR30Ticks)
            {
                DrawNote(_lastTick, _h + 4 * TickSize, $"LB degenerate R30={_r30}t — skipped");
                _phase = Phase.Done;
                return;
            }
            _phase = Phase.Armed;
            _insidePrev = true;
            if (ShowDrawings && ChartControl != null)
            {
                DateTime end = _t0.AddSeconds(WatchEndSeconds);
                Draw.Line(this, Tag($"LB_H_{_tag}"), false, _t0, _h, end, _h, Brushes.OrangeRed, DashStyleHelper.Solid, 2);
                Draw.Line(this, Tag($"LB_L_{_tag}"), false, _t0, _l, end, _l, Brushes.OrangeRed, DashStyleHelper.Solid, 2);
                Draw.Text(this, Tag($"LB_R_{_tag}"), $"R30={_r30}t", 0, _h + 4 * TickSize);
            }
        }

        private int ExtTicks(double px)
        {
            double d = _side > 0 ? px - _level : _level - px;
            int ticks = (int)Math.Round(d / TickSize);
            return ticks > 0 ? ticks : 0;
        }

        private bool ConfirmReady(DateTime t)
        {
            if (!UseWhipsawFilter)
                return true;
            int needExt = (int)Math.Ceiling(ExtensionR30 * _r30);
            return (t - _tBreak).TotalSeconds >= HoldSeconds && _maxExt >= needExt;
        }

        private void OpenBreak(DateTime t, double px, int side)
        {
            _side = side;
            _level = side > 0 ? _h : _l;
            _tBreak = t;
            _breakPx = px;
            _maxExt = ExtTicks(px);
            _phase = Phase.Active;
            if (ShowDrawings && ChartControl != null)
                Draw.Dot(this, Tag($"LB_B_{_tag}_{t.Ticks}"), false, t, px,
                         side > 0 ? Brushes.DodgerBlue : Brushes.Magenta);
            // hold=0 configs (or filter off) can confirm on the break print itself
            if (ConfirmReady(t))
                SubmitEntry(t, px);
        }

        private void SubmitEntry(DateTime t, double px)
        {
            if (_dailyLockout)
            {
                _phase = Phase.Done;
                return;
            }

            double atr = _atrNow;
            double stopPx, targetPx;
            if (atr > TickSize)
            {
                stopPx = Instrument.MasterInstrument.RoundToTickSize(px - _side * AtrStopMult * atr);
                targetPx = Instrument.MasterInstrument.RoundToTickSize(px + _side * AtrTargetMult * atr);
            }
            else
            {
                // ATR not formed yet (fresh chart) — structural fallback, disclosed
                stopPx = _side > 0 ? _l : _h;
                double rPts = Math.Abs(px - stopPx);
                targetPx = Instrument.MasterInstrument.RoundToTickSize(px + _side * rPts);
                Print($"{Name}: ATR not ready — structural fallback stop at the opposite extreme.");
            }
            if (Math.Abs(px - stopPx) < TickSize)
            {
                _phase = Phase.Done;                 // entry would sit on the stop
                return;
            }
            string sig = _side > 0 ? SigLong : SigShort;

            SetStopLoss(sig, CalculationMode.Price, stopPx, false);
            SetProfitTarget(sig, CalculationMode.Price, targetPx);
            _stopPx = stopPx;
            _targetPx = targetPx;
            _beApplied = false;

            _entryPending = true;                    // BEFORE Enter* — order-event race
            _phase = Phase.Pending;
            if (_side > 0)
                EnterLong(0, Contracts, SigLong);    // primary bars context (index 0)
            else
                EnterShort(0, Contracts, SigShort);

            if (ShowDrawings && ChartControl != null)
            {
                if (_side > 0)
                    Draw.TriangleUp(this, Tag($"LB_E_{_tag}_{t.Ticks}"), false, t, px - 8 * TickSize, Brushes.Lime);
                else
                    Draw.TriangleDown(this, Tag($"LB_E_{_tag}_{t.Ticks}"), false, t, px + 8 * TickSize, Brushes.Red);
            }
        }

        // Breakeven: one-shot stop raise once price covers BreakevenPercent% of the
        // entry->target run. Uses the ACTUAL average fill as entry.
        private void ManagePosition(double px)
        {
            if (!UseBreakeven || _beApplied || Position.MarketPosition == MarketPosition.Flat)
                return;
            double entry = Position.AveragePrice;
            double run = (_targetPx - entry) * _side;
            if (run < TickSize)
                return;
            double covered = (px - entry) * _side;
            if (covered / run * 100.0 < BreakevenPercent)
                return;
            double bePx = Instrument.MasterInstrument.RoundToTickSize(
                entry + _side * BreakevenOffsetTicks * TickSize);
            // Never move the stop backwards, and never at/past the working target —
            // a big offset on a tiny ATR run would otherwise invert the OCO bracket.
            if (_side > 0 ? (bePx <= _stopPx || bePx >= _targetPx)
                          : (bePx >= _stopPx || bePx <= _targetPx))
                return;
            string sig = _side > 0 ? SigLong : SigShort;
            SetStopLoss(sig, CalculationMode.Price, bePx, false);
            _stopPx = bePx;
            _beApplied = true;
            Print($"{Name}: breakeven armed at {bePx} ({BreakevenPercent}% of run covered).");
        }

        // --- Daily risk governor (BigPrints port) --------------------------------

        // Fetch-or-create the shared registry entry — called every governor tick,
        // never cached. Null when shared mode can't operate (no account/day, or a
        // NEWER day already registered by another instance).
        private AcctDayGov CurrentAccountDay()
        {
            if (Account == null || _acctSessionDay == DateTime.MinValue)
                return null;
            lock (_acctGovLock)
            {
                AcctDayGov g;
                _acctGov.TryGetValue(Account.Name, out g);
                if (g != null && g.Day > _acctSessionDay)
                    return null;
                if (g == null || g.Day < _acctSessionDay)
                {
                    g = new AcctDayGov
                    {
                        Day = _acctSessionDay,
                        Baseline = Account.Get(AccountItem.RealizedProfitLoss, Currency.UsDollar),
                        Breached = false,
                    };
                    _acctGov[Account.Name] = g;
                }
                return g;
            }
        }

        private void CheckRiskGovernor()
        {
            if (_dailyLockout)
            {
                // A single Exit call isn't guaranteed to fill — retry until flat.
                if (Position.MarketPosition != MarketPosition.Flat && !_entryPending && !_flattenPending && !_timeExitSent)
                    FlattenNow("LB_Flatten");
                return;
            }

            double dayPnL;
            bool sharedMode = false;
            AcctDayGov gov = UseAccountDailyPnL ? CurrentAccountDay() : null;
            if (gov != null && gov.Breached)         // honor the broadcast even with own limits off
            {                                        // (gap closed vs the BigPrints original)
                Lockout("account-wide breach broadcast received");
                return;
            }
            if (DailyProfitTargetUSD <= 0 && DailyLossLimitUSD <= 0)
                return;
            if (gov != null)
            {
                sharedMode = true;
                double realized = Account.Get(AccountItem.RealizedProfitLoss, Currency.UsDollar) - gov.Baseline;
                double unrealized = Account.Get(AccountItem.UnrealizedProfitLoss, Currency.UsDollar);
                dayPnL = realized + unrealized;
            }
            else
            {
                double realized = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit - _dayStartRealized;
                double unrealized = Position.MarketPosition != MarketPosition.Flat
                    ? Position.GetUnrealizedProfitLoss(PerformanceUnit.Currency)
                    : 0.0;
                dayPnL = realized + unrealized;
            }

            bool hitTarget = DailyProfitTargetUSD > 0 && dayPnL >= DailyProfitTargetUSD;
            bool hitLoss = DailyLossLimitUSD > 0 && dayPnL <= -DailyLossLimitUSD;
            if (!hitTarget && !hitLoss)
                return;

            if (sharedMode)
                gov.Breached = true;                 // broadcast to every other instance
            Lockout(string.Format("daily {0} hit ({1:F2} USD{2})",
                hitTarget ? "profit target" : "loss limit", dayPnL, sharedMode ? ", account-wide" : ""));
        }

        private void Lockout(string reason)
        {
            _dailyLockout = true;
            _phase = Phase.Done;                     // no re-arm until next session
            Print($"{Name}: {reason} — locked out until next session.");
            if (Position.MarketPosition != MarketPosition.Flat && !_entryPending && !_flattenPending && !_timeExitSent)
                FlattenNow("LB_Flatten");
        }

        private void FlattenNow(string signalName)
        {
            // Two-arg overload on purpose: ExitLong(string) alone is fromEntrySignal,
            // NOT a signal name (BigPrints bug 2026-07-29). Empty fromEntrySignal =
            // attach the exit to ALL entries.
            _flattenPending = true;                  // BEFORE Exit* — order-event race
            if (Position.MarketPosition == MarketPosition.Long)
                ExitLong(signalName, "");
            else if (Position.MarketPosition == MarketPosition.Short)
                ExitShort(signalName, "");
            else
                _flattenPending = false;
        }

        private void CheckTimeStop(DateTime t)
        {
            // _flattenPending: the governor's flatten and this time stop must never
            // both be in flight — two market exits would reverse the position naked.
            if (_timeExitSent || _flattenPending || _t0 == DateTime.MinValue || Position.MarketPosition == MarketPosition.Flat)
                return;
            if ((t - _t0).TotalSeconds < TimeStopSeconds)
                return;
            _timeExitSent = true;
            // Position.Quantity, not Contracts — a partial fill must not over-exit
            if (Position.MarketPosition == MarketPosition.Long)
                ExitLong(0, Position.Quantity, "LB_TimeExit", SigLong);
            else
                ExitShort(0, Position.Quantity, "LB_TimeExit", SigShort);
        }

        protected override void OnExecutionUpdate(Execution execution, string executionId,
            double price, int quantity, MarketPosition marketPosition, string orderId, DateTime time)
        {
            if (execution.Order == null)
                return;
            string n = execution.Order.Name;

            if (n == SigLong || n == SigShort)
            {
                // Any state that leaves us with filled contracts counts as "in
                // position" — full fill, partial fill, or cancel-after-partial.
                if (execution.Order.OrderState == OrderState.Filled
                    || execution.Order.OrderState == OrderState.PartFilled
                    || (execution.Order.OrderState == OrderState.Cancelled
                        && execution.Order.Filled > 0))
                {
                    _entryPending = false;           // name-gated clear
                    if (_phase == Phase.Pending)     // lockout may already have forced Done
                        _phase = Phase.InPosition;
                }
                return;
            }

            bool isExit = n == "Stop loss" || n == "Profit target" || n == "LB_TimeExit"
                          || n == "LB_Flatten" || n == "Exit on session close";
            if (isExit && Position.MarketPosition == MarketPosition.Flat)
            {
                _trades++;
                _timeExitSent = false;
                _flattenPending = false;
                if (_dailyLockout || _trades >= MaxTradesPerSession
                    || (time - _t0).TotalSeconds > EntryDeadlineSeconds)
                    _phase = Phase.Done;
                else
                {
                    _phase = Phase.Armed;            // re-arm for the next break
                    _side = 0;
                    _insidePrev = false;             // refreshed on the next tick
                }
            }
        }

        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice,
            int quantity, int filled, double averageFillPrice, OrderState orderState,
            DateTime time, ErrorCode error, string comment)
        {
            if (order == null)
                return;
            if (order.Name == "LB_Flatten"
                && (orderState == OrderState.Rejected || orderState == OrderState.Cancelled))
            {
                _flattenPending = false;             // governor retries next tick
                return;
            }
            if (order.Name != SigLong && order.Name != SigShort)
                return;
            if (!_entryPending
                || (orderState != OrderState.Rejected && orderState != OrderState.Cancelled))
                return;
            _entryPending = false;
            if (filled == 0)
            {
                // Entry died without a fill -> stand down for the session
                if (_phase == Phase.Pending)
                    _phase = Phase.Done;
                Print($"{Name}: entry {order.Name} {orderState} unfilled — session stood down.");
            }
            else
            {
                // Cancel after partial fill: a live position exists with brackets
                if (_phase == Phase.Pending)
                    _phase = Phase.InPosition;
                Print($"{Name}: entry {order.Name} {orderState} after partial fill ({filled}) — managing open position.");
            }
        }

        // Wilder ATR on the primary series, identical formula to NT8's system ATR
        // (hand-rolled: nt8c can't resolve the ATR() wrapper — workspace gotcha).
        private double ComputeAtr()
        {
            double tr = TrueRange(0);
            if (CurrentBar < AtrPeriod)
            {
                double sum = tr;
                for (int k = 1; k < CurrentBar; k++)
                    sum += TrueRange(k);
                return sum / CurrentBar;
            }
            double prevAtr = _atrSeries[1];
            return prevAtr + (tr - prevAtr) / AtrPeriod;
        }

        private double TrueRange(int barsAgo)
        {
            double hl = High[barsAgo] - Low[barsAgo];
            double hc = Math.Abs(High[barsAgo] - Close[barsAgo + 1]);
            double lc = Math.Abs(Low[barsAgo] - Close[barsAgo + 1]);
            return Math.Max(hl, Math.Max(hc, lc));
        }

        private void DrawWhipsaw(DateTime t, double px, double retS, int side)
        {
            if (!ShowDrawings || ChartControl == null)
                return;
            double y = side > 0 ? px + 6 * TickSize : px - 6 * TickSize;
            Draw.Text(this, Tag($"LB_W_{_tag}_{t.Ticks}"), $"✕ {retS:0.0}s", 0, y, Brushes.Red);
        }

        private void DrawNote(DateTime t, double y, string msg)
        {
            Print($"{Name}: {msg}");
            // ponytail: Draw.Text, not Draw.TextFixed — TextPosition trips the known
            // Vendor/Custom duplicate-type CS1503 under nt8c.
            if (ShowDrawings && ChartControl != null)
                Draw.Text(this, Tag($"LB_N_{_tag}"), msg, 0, y, Brushes.Gray);
        }

        #region Properties
        [NinjaScriptProperty]
        [Display(Name = "Use whipsaw filter", Description = "OFF = naive chase at the break print (research: -$110/trade avg). ON = hold+extension confirmation with re-entry veto.", GroupName = "01. Signal", Order = 0)]
        public bool UseWhipsawFilter { get; set; }

        [NinjaScriptProperty, Range(0, 300)]
        [Display(Name = "Hold seconds", Description = "Seconds the break must survive outside the range before entry. Best Phase-1 cell: 30.", GroupName = "01. Signal", Order = 1)]
        public int HoldSeconds { get; set; }

        [NinjaScriptProperty, Range(0, 3)]
        [Display(Name = "Extension (xR30)", Description = "Required max excursion beyond the level, as a fraction of the opening candle range. Best Phase-1 cell: 0.25.", GroupName = "01. Signal", Order = 2)]
        public double ExtensionR30 { get; set; }

        [NinjaScriptProperty, Range(5, 300)]
        [Display(Name = "Candle seconds", GroupName = "01. Signal", Order = 3)]
        public int CandleSeconds { get; set; }

        [NinjaScriptProperty, Range(60, 3600)]
        [Display(Name = "Watch end (s from open)", Description = "No NEW breaks after this many seconds from session begin (research: 930 = 18:15:30).", GroupName = "01. Signal", Order = 4)]
        public int WatchEndSeconds { get; set; }

        [NinjaScriptProperty, Range(60, 3600)]
        [Display(Name = "Entry deadline (s from open)", Description = "No entries after this (research: 1200 = 18:20).", GroupName = "01. Signal", Order = 5)]
        public int EntryDeadlineSeconds { get; set; }

        [NinjaScriptProperty, Range(0, 100)]
        [Display(Name = "Min R30 ticks", Description = "Skip the session if the opening candle range is smaller (degenerate).", GroupName = "01. Signal", Order = 6)]
        public int MinR30Ticks { get; set; }

        [NinjaScriptProperty, Range(1, 100)]
        [Display(Name = "Contracts", GroupName = "02. Trade", Order = 0)]
        public int Contracts { get; set; }

        [NinjaScriptProperty, Range(2, 100)]
        [Display(Name = "ATR period", Description = "Wilder ATR on the PRIMARY chart series — the chart timeframe defines what the ATR measures.", GroupName = "02. Trade", Order = 1)]
        public int AtrPeriod { get; set; }

        [NinjaScriptProperty, Range(0.25, 10)]
        [Display(Name = "Stop (x ATR)", Description = "Stop = entry -/+ this many ATRs.", GroupName = "02. Trade", Order = 2)]
        public double AtrStopMult { get; set; }

        [NinjaScriptProperty, Range(0.25, 10)]
        [Display(Name = "Target (x ATR)", Description = "Target = entry +/- this many ATRs.", GroupName = "02. Trade", Order = 3)]
        public double AtrTargetMult { get; set; }

        [NinjaScriptProperty, Range(1, 10)]
        [Display(Name = "Max trades per session", GroupName = "02. Trade", Order = 4)]
        public int MaxTradesPerSession { get; set; }

        [NinjaScriptProperty, Range(300, 7200)]
        [Display(Name = "Time stop (s from open)", Description = "Flatten if still in a position this many seconds after session begin (research: 1800 = 18:30).", GroupName = "02. Trade", Order = 5)]
        public int TimeStopSeconds { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use breakeven", Description = "Move the stop to entry (+offset) once price covers a % of the entry->target run.", GroupName = "03. Breakeven", Order = 0)]
        public bool UseBreakeven { get; set; }

        [NinjaScriptProperty, Range(1, 99)]
        [Display(Name = "Breakeven trigger (% of run)", Description = "Percent of the entry->target distance that must be covered before the stop moves to breakeven.", GroupName = "03. Breakeven", Order = 1)]
        public int BreakevenPercent { get; set; }

        [NinjaScriptProperty, Range(-20, 20)]
        [Display(Name = "Breakeven offset (ticks)", Description = "Breakeven stop = entry +/- this many ticks (positive covers commissions).", GroupName = "03. Breakeven", Order = 2)]
        public int BreakevenOffsetTicks { get; set; }

        [NinjaScriptProperty, Range(0, 100000)]
        [Display(Name = "Daily profit target (USD)", Description = "Real-time (realized + unrealized). On hit: flatten + no more entries until next session. 0 = off.", GroupName = "04. Daily limits", Order = 0)]
        public double DailyProfitTargetUSD { get; set; }

        [NinjaScriptProperty, Range(0, 100000)]
        [Display(Name = "Daily loss limit (USD)", Description = "Real-time (realized + unrealized). On hit: flatten + no more entries until next session. 0 = off.", GroupName = "04. Daily limits", Order = 1)]
        public double DailyLossLimitUSD { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Account-wide (all markets)", Description = "Watch the ACCOUNT's combined day PnL: every instance of this strategy on the account flattens together on a breach (BigPrints shared governor). OFF = this instance's own PnL only.", GroupName = "04. Daily limits", Order = 2)]
        public bool UseAccountDailyPnL { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show drawings", GroupName = "05. Visuals", Order = 0)]
        public bool ShowDrawings { get; set; }
        #endregion
    }
}
