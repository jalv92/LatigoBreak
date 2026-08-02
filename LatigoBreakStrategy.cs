// LatigoBreakStrategy — opening-range breakout with whipsaw (fake-breakout)
// veto, run at up to three session windows per trading day (v3):
// 18:00 ET Globex reopen, 20:00 ET evening, 09:30 ET US open — each with its
// own enable checkbox. Windows are OFFSETS from the ETH session begin, so the
// strategy needs no time-zone code but REQUIRES a trading-hours template whose
// session begins at the 18:00 ET reopen (CME ETH). An RTH template silently
// misplaces every window.
//
// Signal engine is the 1:1 port of the research detector (research/events.py +
// research/phase1.confirm_index). Signal defaults = best Phase-1 grid cell
// (hold 30s, extension 0.25xR30).
// HONEST-USE NOTE: research on 203 NQ sessions shows the naive chase loses
// ~$110/trade and the best filter cell stays slightly below zero — this
// strategy is a Playback/sim laboratory for iterating the redesign, not a
// validated edge (see docs/research/).
//
// v3 changes (2026-08-02):
// * Three windows with checkboxes; a window is skipped when a position (or a
//   pending entry) is still open when its opening candle elapses.
// * One EntryWindowMinutes clock (default 30) per window replaces watch-end /
//   entry-deadline / time-stop: breaks and entries die 30 min after the window
//   opens, but an OPEN POSITION IS NEVER CLOSED BY THE CLOCK — it runs to
//   stop/target (outer bound: exit-on-session-close at the 17:00 ET halt).
// * Brackets are live-until-cancelled Exit orders (LB_Stop / LB_Target)
//   submitted on the entry fill — NOT Set* orders, which the strategy engine
//   re-asserts. Result: a stop/target dragged by hand in Chart Trader STAYS
//   where you put it. OnOrderUpdate adopts hand-moves into the internal state
//   so the optional breakeven guards stay honest.
//
// v2 trade management retained: ATR brackets (Wilder ATR hand-rolled on the
// PRIMARY chart series — the chart timeframe defines the ATR's meaning),
// optional breakeven at a % of the entry->target run, real-time daily
// profit/loss lockout, and the account-wide shared governor (default OFF).
//
// Strategy Analyzer runs need Order Fill Resolution High / Tick / 1.
#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
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
    // v4 flow gate: Off = module dormant; LogOnly = log the support verdict on
    // every confirmed break, trade pure v3; Filter = enter only breaks with
    // big-print support; Trigger = Filter + a fresh supporting cluster fires
    // the entry immediately (no hold wait).
    public enum LatigoFlowGateMode { Off = 0, LogOnly = 1, Filter = 2, Trigger = 3 }

    public class LatigoBreakStrategy : Strategy
    {
        private enum Phase { Idle, Candle, Armed, Active, Pending, InPosition }

        private const int TickIdx = 1;
        private const string SigLong = "LB_Long";
        private const string SigShort = "LB_Short";
        private const string SigStop = "LB_Stop";
        private const string SigTarget = "LB_Target";

        // Session windows as seconds from the ETH session begin (18:00 ET):
        // +0 = 18:00 reopen, +7200 = 20:00, +55800 = 09:30 next morning (same
        // trading day). DST transitions always fall inside the closed weekend,
        // so fixed offsets are exact.
        private static readonly int[] WinOffsetSecs = { 0, 7200, 55800 };
        private static readonly string[] WinNames = { "18:00", "20:00", "09:30" };

        private Phase _phase = Phase.Idle;
        private SessionIterator _sess;
        private DateTime _t0 = DateTime.MinValue;   // trading-day session begin (18:00 ET)
        private DateTime _w0 = DateTime.MinValue;   // active window begin
        private double _wDeadlineSecs;              // effective entry window (capped, see TryArmWindow)
        private int _nextWin;                       // next window index to consider
        private DateTime _freeSince = DateTime.MinValue;    // when we last became flat+idle
        private DateTime _lastTick = DateTime.MinValue;

        private double _h = double.MinValue, _l = double.MaxValue;
        private int _r30;

        private bool _insidePrev;
        private int _side;                          // +1 up-break, -1 down-break
        private double _level, _breakPx;
        private DateTime _tBreak;
        private int _maxExt;                        // ticks beyond level (cumulative)

        private int _trades, _tag;                  // per-window trade count / drawing tag
        private int _lastXBar = -1, _lastDotBar = -1;   // one ✕ / one dot per primary bar
        private bool _entryPending, _flattenPending;
        private readonly List<string> _drawTags = new List<string>();

        // ATR on the PRIMARY series (hand-rolled Wilder — nt8c cannot resolve the
        // ATR() system-indicator wrapper; same pattern as BigPrints/FVGFlow).
        private Series<double> _atrSeries;
        private double _atrNow;                     // latest primary ATR, read from the tick branch

        // Bracket state for the open trade. _stopPx/_targetPx track the LIVE
        // order prices (synced in OnOrderUpdate), not just what we submitted —
        // hand-dragged orders update them too.
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

        // --- v4 flow gate: big-print support detection (BigPrints tape port) ---
        // OnMarketData (market-data thread) classifies aggressor prints and folds
        // them into sweep clusters; fired clusters land in _flowClusters under
        // _flowLock. The strategy thread ONLY reads the list — orders are never
        // touched from the market-data thread (BigPrints crash-dump rule).
        private const int FlowClusterSpanMs = 1500;  // hard wall on one sweep's duration
        private double _fBid, _fAsk;                 // market-data thread only
        private bool _fClusterOpen, _fClusterIsBuy;
        private long _fClusterVolume, _fClusterMaxPrint;
        private double _fClusterPrice;               // price of the largest print
        private DateTime _fClusterStart = DateTime.MinValue, _fClusterLast = DateTime.MinValue;
        private volatile bool _flowResetRequested;   // rewind fence — drained inside OnMarketData
        private volatile int _flowEpoch;             // bumped on every reset; stale in-flight clusters stamp the old value
        private int _fEpoch;                         // market-data thread's snapshot, taken at each OnMarketData entry
        private int _flowClustersEverFired;          // diagnostic only, tearing tolerated

        private sealed class FlowCluster
        {
            public DateTime Time;
            public bool IsBuy;
            public long Volume;
            public long MaxPrint;
            public double Price;
            public int Epoch;
        }
        private readonly object _flowLock = new object();
        private readonly List<FlowCluster> _flowClusters = new List<FlowCluster>();

        // Per-break-episode bookkeeping (strategy thread)
        private bool _confirmLogged;                 // one confirm record per episode
        private string _entryVia = "v3";             // confirm | trigger | v3
        private bool _noTapeWarned;
        private bool _expectTargetCancel;            // one-shot: BE's OCO cancel-replace of the target

        // Forward outcome tracker: multi-slot (the BigPrints single-slot lesson),
        // MFE/MAE checkpoints at 30/60/120 s, closed at 600 s or on session reset.
        private sealed class FlowTrack
        {
            public string Id;
            public DateTime T0;
            public int Side;
            public double Px0;
            public double MaxFav, MinFav;            // signed points from Px0
            public bool C30, C60, C120;
            public double Fav30, Adv30, Fav60, Adv60, Fav120, Adv120;
        }
        private readonly List<FlowTrack> _flowTracks = new List<FlowTrack>();

        private static readonly object _flowLogLock = new object();
        private string _flowLogPath;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "LatigoBreakStrategy";
                Description = "Opening 30s-candle breakout with whipsaw veto at up to three session windows (18:00 / 20:00 / 09:30 ET). Sim/Playback lab — see repo docs/research for the honest stats.";
                Calculate = Calculate.OnEachTick;
                EntriesPerDirection = 1;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds = 30;
                BarsRequiredToTrade = 0;
                IsInstantiatedOnEachOptimizationIteration = false;

                TradeGlobexReopen = true;
                TradeEvening = true;
                TradeUsOpen = true;
                EntryWindowMinutes = 30;

                UseWhipsawFilter = true;
                HoldSeconds = 30;
                ExtensionR30 = 0.25;
                CandleSeconds = 30;
                MinR30Ticks = 4;

                Contracts = 1;
                AtrPeriod = 14;
                AtrStopMult = 2.0;
                AtrTargetMult = 2.0;
                MaxTradesPerWindow = 1;

                UseBreakeven = false;
                BreakevenPercent = 50;
                BreakevenOffsetTicks = 0;

                DailyProfitTargetUSD = 500;
                DailyLossLimitUSD = 300;
                UseAccountDailyPnL = false;         // multi-market shared close OFF by default

                FlowGate = LatigoFlowGateMode.Filter;
                SupportMinVolume = 50;              // reopen-tape scale (Javier's cases: 50-93c) — NOT BigPrints' RTH 150
                SupportWindowSec = 120;
                ClusterMilliseconds = 150;
                SupportMinMaxPrint = 0;             // 0 = sweep-total semantics; >0 = require one strong print

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
            else if (State == State.Realtime)
            {
                if (FlowGate != LatigoFlowGateMode.Off)
                    Print($"{Name}: flow gate {FlowGate} ACTIVE (min {SupportMinVolume}c, window {SupportWindowSec}s). Needs live L1 tape — historical bars / Strategy Analyzer trade as pure v3.");
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
                // Only the rewind path clears the ledger: tags must accumulate
                // across session rollovers so a cross-day rewind can wipe the
                // WHOLE discarded pass, not just its last session.
                _drawTags.Clear();
                // Rewind also discards the pass that may have set the shared breach flag
                if (Account != null)
                    lock (_acctGovLock) _acctGov.Remove(Account.Name);
            }

            _phase = Phase.Idle;
            _nextWin = 0;
            _w0 = DateTime.MinValue;
            _freeSince = DateTime.MinValue;
            ResetWindowState();
            _entryPending = false;
            _flattenPending = false;
            _targetPx = 0; _stopPx = 0;
            _beApplied = false;
            _expectTargetCancel = false;
            _dailyLockout = false;
            _dayStartRealized = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit;

            // Flow gate: flush open forward-tracks as truncated, drop fired
            // clusters, and (on rewind) fence the market-data thread's builder.
            // The epoch bump makes the fence airtight: an in-flight OnMarketData
            // call that started before this reset stamps its cluster with the
            // OLD epoch, and the support verdict ignores it.
            FlowFlushTracks("reset");
            lock (_flowLock)
            {
                _flowEpoch++;
                _flowClusters.Clear();
            }
            if (removeDrawings)
                _flowResetRequested = true;          // drained inside OnMarketData
            _confirmLogged = false;
            _entryVia = "v3";
        }

        private void ResetWindowState()
        {
            _h = double.MinValue; _l = double.MaxValue;
            _r30 = 0;
            _insidePrev = true;
            _side = 0; _level = 0; _breakPx = 0; _maxExt = 0;
            _tBreak = DateTime.MinValue;
            _trades = 0;
            _lastXBar = -1; _lastDotBar = -1;
        }

        private string Tag(string t)
        {
            _drawTags.Add(t);
            return t;
        }

        private bool WinEnabled(int i)
        {
            if (i == 0) return TradeGlobexReopen;
            if (i == 1) return TradeEvening;
            return TradeUsOpen;
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
                _acctSessionDay = _t0.Date;
                if (UseAccountDailyPnL)
                    CurrentAccountDay();             // prime the shared baseline for this day
            }
            if (_t0 == DateTime.MinValue)
                return;

            CheckRiskGovernor();                     // real-time daily limits — runs in EVERY phase
            FlowTracksUpdate(t, px);                 // forward MFE/MAE for logged breaks

            // An open position is never closed by the clock (v3): it runs to
            // stop/target while later windows pass by unarmed.
            if (_phase == Phase.InPosition)
            {
                ManagePosition(px);
                return;
            }
            if (_phase == Phase.Pending)
                return;

            if (_phase == Phase.Idle)
            {
                TryArmWindow(t);
                if (_phase == Phase.Idle)
                    return;
            }

            double secs = (t - _w0).TotalSeconds;
            double deadline = _wDeadlineSecs;

            if (_phase == Phase.Candle)
            {
                if (secs < CandleSeconds)
                {
                    if (px > _h) _h = px;
                    if (px < _l) _l = px;
                    return;
                }
                FreezeCandle();
                if (_phase == Phase.Idle)
                    return;                          // degenerate / empty candle — window consumed
            }

            if (secs > deadline)                     // Armed/Active past the entry window
            {
                ConsumeWindow("entry window over");
                return;
            }

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
                    DrawWhipsaw(t, px, oldSide);
                    _phase = Phase.Armed;
                    _side = 0;
                    // Gap-through: same print already beyond the OPPOSITE level
                    bool beyondOpp = oldSide > 0 ? dn : up;
                    if (beyondOpp)
                        OpenBreak(t, px, -oldSide);
                }
                else
                {
                    bool confirmed = ConfirmReady(t);
                    if (confirmed)
                        FlowNoteConfirm(t, px);

                    // Trigger mode: a fresh supporting cluster since the break
                    // fires the entry at once — no hold wait, entry at the level.
                    if (!confirmed && FlowGate == LatigoFlowGateMode.Trigger && FlowActive()
                        && FlowTriggerReady(t))
                    {
                        _entryVia = "trigger";
                        SubmitEntry(t, px);
                    }
                    else if (confirmed && FlowEntryAllowed(t))
                    {
                        _entryVia = "confirm";
                        SubmitEntry(t, px);
                    }
                }
            }

            if (_phase == Phase.Armed && _insidePrev && (up || dn))
                OpenBreak(t, px, up ? 1 : -1);

            _insidePrev = inside;
        }

        // When idle and flat, skip disabled windows and windows we were not
        // already free for at their open (in a trade / pending entry = "skip
        // the next opening" — and it also prevents silently-truncated opening
        // candles), then arm the current one once its time comes.
        private void TryArmWindow(DateTime t)
        {
            if (_dailyLockout || _entryPending || Position.MarketPosition != MarketPosition.Flat)
                return;
            while (_nextWin < WinOffsetSecs.Length)
            {
                DateTime w0 = _t0.AddSeconds(WinOffsetSecs[_nextWin]);
                if (!WinEnabled(_nextWin) || t >= w0.AddSeconds(CandleSeconds) || _freeSince > w0)
                {
                    _nextWin++;
                    continue;
                }
                if (t < w0)
                    return;                          // upcoming — keep waiting
                _w0 = w0;
                _wDeadlineSecs = EffectiveDeadlineSecs();
                _tag++;
                ResetWindowState();
                _phase = Phase.Candle;
                Print($"{Name}: window {WinNames[_nextWin]} armed ({w0:HH:mm:ss} chart time).");
                // Only reachable when the strategy was ENABLED mid-candle (a
                // position never overlaps here — _freeSince gates that): the
                // opening range is built from a partial tick sample. Disclose.
                if ((t - w0).TotalSeconds > 1)
                    Print($"{Name}: armed {(t - w0).TotalSeconds:F0}s late — opening candle is PARTIAL.");
                return;
            }
        }

        // Configured entry window, floored so the candle can never starve the
        // hunt (CandleSeconds + 60s minimum) and capped at the next ENABLED
        // window's open so one window's hunt can never swallow the next.
        private double EffectiveDeadlineSecs()
        {
            double configured = EntryWindowMinutes * 60.0;
            double want = Math.Max(configured, CandleSeconds + 60.0);
            for (int j = _nextWin + 1; j < WinOffsetSecs.Length; j++)
            {
                if (!WinEnabled(j))
                    continue;
                want = Math.Min(want, WinOffsetSecs[j] - WinOffsetSecs[_nextWin]);
                break;
            }
            if (Math.Abs(want - configured) > 0.5)
                Print($"{Name}: effective entry window {want:F0}s (configured {configured:F0}s — floored by candle / capped by next window).");
            return want;
        }

        private void ConsumeWindow(string reason)
        {
            Print($"{Name}: window {WinNames[Math.Min(_nextWin, WinNames.Length - 1)]} closed — {reason}.");
            _nextWin++;
            _phase = Phase.Idle;
        }

        private void FreezeCandle()
        {
            if (_h == double.MinValue)               // no prints in the candle window
            {
                ConsumeWindow("empty candle");
                return;
            }
            _r30 = (int)Math.Round((_h - _l) / TickSize);
            if (_r30 < MinR30Ticks)
            {
                DrawNote(_lastTick, _h + 4 * TickSize, $"LB degenerate R30={_r30}t — skipped");
                ConsumeWindow("degenerate candle");
                return;
            }
            _phase = Phase.Armed;
            _insidePrev = true;
            Print($"{Name}: candle frozen H={_h} L={_l} R30={_r30}t");
            if (ShowDrawings && ChartControl != null)
            {
                DateTime end = _w0.AddSeconds(_wDeadlineSecs);
                Draw.Line(this, Tag($"LB_H_{_tag}"), false, _w0, _h, end, _h, Brushes.OrangeRed, DashStyleHelper.Solid, 2);
                Draw.Line(this, Tag($"LB_L_{_tag}"), false, _w0, _l, end, _l, Brushes.OrangeRed, DashStyleHelper.Solid, 2);
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
            _confirmLogged = false;                  // new episode, new confirm record
            if (ShowDrawings && ChartControl != null && CurrentBars[0] != _lastDotBar)
            {
                _lastDotBar = CurrentBars[0];        // one break dot per primary bar
                Draw.Dot(this, Tag($"LB_B_{_tag}_{_lastDotBar}"), false, t, px,
                         side > 0 ? Brushes.DodgerBlue : Brushes.Magenta);
            }
            // hold=0 configs (or filter off) can confirm on the break print itself.
            // FlowNoteConfirm BEFORE the entry decision — this shortcut bypassed
            // all corpus logging (review 2026-08-02, critical finding).
            if (ConfirmReady(t))
            {
                FlowNoteConfirm(t, px);
                if (FlowEntryAllowed(t))
                {
                    _entryVia = "confirm";
                    SubmitEntry(t, px);
                }
            }
        }

        // One confirm record + forward track per break episode, at the moment
        // v3 WOULD enter — the scoring anchor for supported vs unsupported.
        // Called from BOTH the Active-phase loop and OpenBreak's instant-confirm
        // shortcut (filter off / hold=0), which previously skipped it.
        private void FlowNoteConfirm(DateTime t, double px)
        {
            if (_confirmLogged || !FlowActive())
                return;
            _confirmLogged = true;
            long sv, ov; int sn;
            bool sup = FlowSupported(_side, t, out sv, out ov, out sn);
            FlowLogConfirm(t, px, sup, sv, ov, sn);
            FlowOpenTrack($"c{_tag}_{t.Ticks}", t, _side, px);
            if (!sup && _flowClustersEverFired == 0 && !_noTapeWarned)
            {
                _noTapeWarned = true;
                Print($"{Name}: flow gate has seen ZERO clusters this session — check that the feed carries L1 bid/ask.");
            }
        }

        private void SubmitEntry(DateTime t, double px)
        {
            if (_dailyLockout)
            {
                ConsumeWindow("daily lockout");
                return;
            }

            // Brackets are priced off the ACTUAL average fill and submitted in
            // OnExecutionUpdate (live-until-cancelled Exit orders, hand-draggable).
            _stopPx = 0; _targetPx = 0;
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

            if (FlowActive())
            {
                FlowLogEntry(t, px);
                FlowAnnounceSupport(t);              // Output + gold dot: WHY this entry was licensed
                if (_entryVia == "trigger")          // no confirm record exists for this episode
                    FlowOpenTrack($"e{_tag}_{t.Ticks}", t, _side, px);
            }
        }

        // Show what licensed the entry. Without this the gate's reasons are
        // invisible: the BigPrints indicator is usually NOT on this chart, and a
        // supporting "cluster" is a sweep SUM of small prints — nothing a 30 s
        // chart shows. Gold dot = the freshest same-side supporting sweep.
        private void FlowAnnounceSupport(DateTime t)
        {
            long sv, ov; int sn;
            FlowSupported(_side, t, out sv, out ov, out sn);
            FlowCluster latest = null;
            lock (_flowLock)
            {
                foreach (FlowCluster c in _flowClusters)
                    if (c.Epoch == _flowEpoch && c.IsBuy == (_side > 0) && c.Time <= t
                        && c.MaxPrint >= SupportMinMaxPrint
                        && (latest == null || c.Time > latest.Time))
                        latest = c;
            }
            string detail = latest == null ? "" : string.Format(
                CultureInfo.InvariantCulture,
                " — last sweep {0}c (max print {1}c @ {2})",
                latest.Volume, latest.MaxPrint, latest.Price);
            Print($"{Name}: entry via {_entryVia} — support {sv}c in {sn} sweep(s) vs {ov}c opposite{detail}.");
            if (ShowDrawings && ChartControl != null && latest != null)
                Draw.Dot(this, Tag($"LB_S_{_tag}_{latest.Time.Ticks}"), false, latest.Time, latest.Price, Brushes.Gold);
        }

        // Live-until-cancelled Exit brackets, submitted/resized on each entry
        // execution. The strategy never re-asserts them afterwards, so a stop or
        // target dragged by hand in Chart Trader STAYS where you put it (v3).
        private void SubmitBrackets(Order entry)
        {
            int qty = entry.Filled;
            if (qty <= 0 || _side == 0)
                return;
            if (_stopPx == 0)                        // first fill prices the bracket
            {
                double basePx = Position.AveragePrice > 0 ? Position.AveragePrice : entry.AverageFillPrice;
                double atr = _atrNow;
                if (atr > TickSize)
                {
                    _stopPx = Instrument.MasterInstrument.RoundToTickSize(basePx - _side * AtrStopMult * atr);
                    _targetPx = Instrument.MasterInstrument.RoundToTickSize(basePx + _side * AtrTargetMult * atr);
                }
                else
                {
                    // ATR not formed yet (fresh chart) — structural fallback, disclosed
                    _stopPx = _side > 0 ? _l : _h;
                    _targetPx = Instrument.MasterInstrument.RoundToTickSize(basePx + _side * Math.Abs(basePx - _stopPx));
                    Print($"{Name}: ATR not ready — structural fallback stop at the opposite extreme.");
                }
                // Rounding can land a level on the fill price — push it one tick out
                if (Math.Abs(basePx - _stopPx) < TickSize)
                    _stopPx = Instrument.MasterInstrument.RoundToTickSize(basePx - _side * TickSize);
                if (Math.Abs(_targetPx - basePx) < TickSize)
                    _targetPx = Instrument.MasterInstrument.RoundToTickSize(basePx + _side * TickSize);
            }
            if (_side > 0)
            {
                ExitLongStopMarket(0, true, qty, _stopPx, SigStop, SigLong);
                ExitLongLimit(0, true, qty, _targetPx, SigTarget, SigLong);
            }
            else
            {
                ExitShortStopMarket(0, true, qty, _stopPx, SigStop, SigShort);
                ExitShortLimit(0, true, qty, _targetPx, SigTarget, SigShort);
            }
        }

        // Breakeven: one-shot stop move once price covers BreakevenPercent% of the
        // entry->target run. When enabled it overrides a hand-dragged stop ONCE at
        // trigger time (guards use the live order prices, synced in OnOrderUpdate).
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
            // Trackers BEFORE the submits: Playback can invoke OnOrderUpdate
            // synchronously in-stack, and a stale tracker made the BE's own echo
            // print as "moved by hand" (observed 2026-08-02). The stop change is
            // also a cancel-replace under OCO, which killed the TARGET leg —
            // re-submit it in the same breath (one-shot expected-cancel flag
            // keeps the by-hand warning honest).
            _stopPx = bePx;
            _beApplied = true;
            _expectTargetCancel = _targetPx > 0;
            if (_side > 0)
            {
                ExitLongStopMarket(0, true, Position.Quantity, bePx, SigStop, SigLong);
                if (_targetPx > 0)
                    ExitLongLimit(0, true, Position.Quantity, _targetPx, SigTarget, SigLong);
            }
            else
            {
                ExitShortStopMarket(0, true, Position.Quantity, bePx, SigStop, SigShort);
                if (_targetPx > 0)
                    ExitShortLimit(0, true, Position.Quantity, _targetPx, SigTarget, SigShort);
            }
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
                if (Position.MarketPosition != MarketPosition.Flat && !_entryPending && !_flattenPending)
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
            Print($"{Name}: {reason} — locked out until next session.");
            if (Position.MarketPosition != MarketPosition.Flat && !_entryPending && !_flattenPending)
                FlattenNow("LB_Flatten");
            // Mid-hunt (candle/armed/active): abandon the window. In a position or
            // pending, the flat bookkeeping in OnExecutionUpdate finishes the job.
            if (_phase == Phase.Candle || _phase == Phase.Armed || _phase == Phase.Active)
                _phase = Phase.Idle;
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
                    if (_phase == Phase.Pending)     // lockout may already have forced flatten
                        _phase = Phase.InPosition;
                    if (_dailyLockout)
                    {
                        // Breach hit while this entry was in flight: close it on
                        // the fill event itself, no brackets, no extra tick open.
                        if (!_flattenPending)
                            FlattenNow("LB_Flatten");
                        return;
                    }
                    SubmitBrackets(execution.Order); // price on first fill, resize on later ones
                }
                return;
            }

            bool isExit = n == SigStop || n == SigTarget || n == "LB_Flatten"
                          || n == "Exit on session close";
            // Phase gate: a stale exit event (e.g. interleaved around a Playback
            // rewind) must not consume a window of the freshly reset day.
            if (isExit && Position.MarketPosition == MarketPosition.Flat
                && (_phase == Phase.InPosition || _phase == Phase.Pending))
            {
                _trades++;
                _flattenPending = false;
                _stopPx = 0; _targetPx = 0;
                _beApplied = false;
                _expectTargetCancel = false;
                _freeSince = time;                   // gates arming of any window already open
                if (FlowActive())
                    FlowLogTradeClose(time, n);
                _entryVia = "v3";
                double wsecs = _w0 == DateTime.MinValue ? double.MaxValue : (time - _w0).TotalSeconds;
                if (_dailyLockout || _trades >= MaxTradesPerWindow || wsecs > _wDeadlineSecs)
                {
                    ConsumeWindow("trade closed");   // next window (if any) arms when its time comes
                }
                else
                {
                    _phase = Phase.Armed;            // re-arm for the next break in this window
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

            // Bracket orders: adopt the LIVE price so a hand-dragged stop/target
            // keeps the breakeven guards honest; warn if one is cancelled by hand.
            if (order.Name == SigStop || order.Name == SigTarget)
            {
                if (orderState == OrderState.Working || orderState == OrderState.Accepted)
                {
                    double p = order.Name == SigStop ? stopPrice : limitPrice;
                    double tracked = order.Name == SigStop ? _stopPx : _targetPx;
                    if (p > 0 && Math.Abs(p - tracked) >= TickSize * 0.5)
                    {
                        Print($"{Name}: {order.Name} moved by hand to {p} — adopted.");
                        if (order.Name == SigStop) _stopPx = p; else _targetPx = p;
                    }
                }
                else if (orderState == OrderState.Cancelled
                         && Position.MarketPosition != MarketPosition.Flat
                         && !_flattenPending && !_dailyLockout)
                {
                    if (order.Name == SigTarget && _expectTargetCancel)
                    {
                        _expectTargetCancel = false; // BE's OCO cancel-replace — target re-submitted, not hand-cancelled
                    }
                    else
                    {
                        if (order.Name == SigTarget)
                            _targetPx = 0;           // respect the hand-cancel: BE must not resurrect it
                        Print($"{Name}: {order.Name} cancelled by hand — position no longer protected on that side.");
                    }
                }
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
                // Entry died without a fill -> stand down for this window
                _freeSince = time;
                if (FlowActive())                    // close the orphaned "entry" record in the corpus
                    FlowLogTradeClose(time, "entry_" + orderState + "_unfilled");
                if (_phase == Phase.Pending)
                    ConsumeWindow("entry " + orderState + " unfilled");
                Print($"{Name}: entry {order.Name} {orderState} unfilled — window stood down.");
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

        private void DrawWhipsaw(DateTime t, double px, int side)
        {
            // One ✕ per primary bar, no text — dozens of sub-second whipsaws can
            // land inside a single candle and pile into an unreadable smear.
            if (!ShowDrawings || ChartControl == null || CurrentBars[0] == _lastXBar)
                return;
            _lastXBar = CurrentBars[0];
            double y = side > 0 ? px + 6 * TickSize : px - 6 * TickSize;
            Draw.Text(this, Tag($"LB_W_{_tag}_{_lastXBar}"), "✕", 0, y, Brushes.Red);
        }

        private void DrawNote(DateTime t, double y, string msg)
        {
            Print($"{Name}: {msg}");
            // ponytail: Draw.Text, not Draw.TextFixed — TextPosition trips the known
            // Vendor/Custom duplicate-type CS1503 under nt8c.
            if (ShowDrawings && ChartControl != null)
                Draw.Text(this, Tag($"LB_N_{_tag}"), msg, 0, y, Brushes.Gray);
        }

        // --- v4 flow gate ---------------------------------------------------

        // OnMarketData never fires on historical bars, so the gate can only
        // exist on live/Playback tape — everywhere else FlowActive() is false
        // and the strategy behaves exactly like v3.
        private bool FlowActive()
        {
            return FlowGate != LatigoFlowGateMode.Off && State == State.Realtime;
        }

        // Market-data thread: classify aggressor prints (at/above ask = buy,
        // at/below bid = sell, inside spread = no aggressor) and fold them into
        // same-side sweep clusters (gap <= ClusterMilliseconds, span <= 1.5 s).
        // Compute-only — the strategy thread drains the results.
        protected override void OnMarketData(MarketDataEventArgs e)
        {
            if (FlowGate == LatigoFlowGateMode.Off || CurrentBar < 0 || State != State.Realtime)
                return;

            if (_flowResetRequested)                 // Playback rewind fence (DataLoaded does NOT re-run)
            {
                _flowResetRequested = false;
                _fClusterOpen = false;
                _fBid = 0; _fAsk = 0;
            }
            _fEpoch = _flowEpoch;                    // clusters finalized in THIS call carry this stamp

            DateTime t = e.Time;

            // Tape-clock timeout: ANY later event closes an open cluster at its
            // real end (lower latency than waiting for the next breaking print).
            if (_fClusterOpen && (t - _fClusterLast).TotalMilliseconds > ClusterMilliseconds)
                FlowFinalizeCluster();

            if (e.MarketDataType == MarketDataType.Bid) { _fBid = e.Price; return; }
            if (e.MarketDataType == MarketDataType.Ask) { _fAsk = e.Price; return; }
            if (e.MarketDataType != MarketDataType.Last || _fBid <= 0 || _fAsk <= 0)
                return;

            bool isBuy;
            if (e.Price >= _fAsk) isBuy = true;
            else if (e.Price <= _fBid) isBuy = false;
            else return;                             // inside the spread — no aggressor

            if (_fClusterOpen && isBuy == _fClusterIsBuy
                && (t - _fClusterLast).TotalMilliseconds <= ClusterMilliseconds
                && (t - _fClusterStart).TotalMilliseconds <= FlowClusterSpanMs)
            {
                _fClusterVolume += e.Volume;
                if (e.Volume > _fClusterMaxPrint)
                {
                    _fClusterMaxPrint = e.Volume;
                    _fClusterPrice = e.Price;
                }
                _fClusterLast = t;
                return;
            }

            FlowFinalizeCluster();
            _fClusterOpen = true;
            _fClusterIsBuy = isBuy;
            _fClusterVolume = e.Volume;
            _fClusterMaxPrint = e.Volume;
            _fClusterPrice = e.Price;
            _fClusterStart = t;
            _fClusterLast = t;
        }

        private void FlowFinalizeCluster()
        {
            if (!_fClusterOpen)
                return;
            _fClusterOpen = false;
            if (_fClusterVolume < SupportMinVolume)
                return;
            System.Threading.Interlocked.Increment(ref _flowClustersEverFired);
            lock (_flowLock)
            {
                _flowClusters.Add(new FlowCluster
                {
                    Time = _fClusterLast,
                    IsBuy = _fClusterIsBuy,
                    Volume = _fClusterVolume,
                    MaxPrint = _fClusterMaxPrint,
                    Price = _fClusterPrice,
                    Epoch = _fEpoch,
                });
                DateTime cutoff = _fClusterLast.AddSeconds(-SupportWindowSec);
                _flowClusters.RemoveAll(c => c.Time < cutoff);
                if (_flowClusters.Count > 64)        // hard cap — 64 big prints in one window is another regime
                    _flowClusters.RemoveAt(0);
            }
        }

        // Support verdict at decision time: same-side big-print volume inside
        // the rolling window must reach SupportMinVolume AND outweigh the
        // opposite side. Strategy thread only.
        private bool FlowSupported(int side, DateTime t, out long sameVol, out long oppVol, out int sameCount)
        {
            sameVol = 0; oppVol = 0; sameCount = 0;
            if (_flowResetRequested)
                return false;                        // mid-rewind: never trust the list
            DateTime cutoff = t.AddSeconds(-SupportWindowSec);
            lock (_flowLock)
            {
                foreach (FlowCluster c in _flowClusters)
                {
                    if (c.Epoch != _flowEpoch || c.Time < cutoff || c.Time > t)
                        continue;                    // stale epoch, out of window, or a future-stamped ghost
                    if (c.MaxPrint < SupportMinMaxPrint)
                        continue;                    // "one strong print" requirement (0 = off)
                    if (c.IsBuy == (side > 0)) { sameVol += c.Volume; sameCount++; }
                    else oppVol += c.Volume;
                }
            }
            return sameVol >= SupportMinVolume && sameVol >= oppVol;
        }

        // Trigger mode: a supporting cluster fired SINCE this break (and the
        // window balance agrees) fires the entry with no hold wait.
        private bool FlowTriggerReady(DateTime t)
        {
            long sv, ov; int sn;
            if (!FlowSupported(_side, t, out sv, out ov, out sn))
                return false;
            lock (_flowLock)
            {
                foreach (FlowCluster c in _flowClusters)
                    if (c.Epoch == _flowEpoch && c.IsBuy == (_side > 0)
                        && c.MaxPrint >= SupportMinMaxPrint
                        && c.Time >= _tBreak && c.Time <= t)
                        return true;
            }
            return false;
        }

        private bool FlowEntryAllowed(DateTime t)
        {
            if (!FlowActive() || FlowGate == LatigoFlowGateMode.LogOnly)
                return true;                         // Off / LogOnly / no live tape -> pure v3
            long sv, ov; int sn;
            return FlowSupported(_side, t, out sv, out ov, out sn);
        }

        // --- v4 corpus log + forward outcome tracks -------------------------

        private static string J(double v)
        {
            return v.ToString("0.####", CultureInfo.InvariantCulture);
        }

        private static string JT(DateTime t)
        {
            return t.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture);
        }

        private void FlowAppend(string json)
        {
            try                                      // logging must never break trading
            {
                if (_flowLogPath == null)
                    _flowLogPath = Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "BigPrintsAI", "latigo_flow_log.jsonl");
                lock (_flowLogLock)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_flowLogPath));
                    File.AppendAllText(_flowLogPath, json + Environment.NewLine);
                }
            }
            catch { }
        }

        private string FlowCommonJson(DateTime t, double px)
        {
            return "\"t\":\"" + JT(t) + "\",\"instrument\":\"" + Instrument.MasterInstrument.Name
                + "\",\"window\":\"" + WinNames[Math.Min(_nextWin, WinNames.Length - 1)]
                + "\",\"side\":" + _side + ",\"r30\":" + _r30 + ",\"level\":" + J(_level) + ",\"px\":" + J(px)
                + ",\"mode\":\"" + FlowGate + "\",\"min_vol\":" + SupportMinVolume
                + ",\"min_mp\":" + SupportMinMaxPrint + ",\"win_sec\":" + SupportWindowSec;
        }

        private void FlowLogConfirm(DateTime t, double px, bool supported, long sameVol, long oppVol, int sameCount)
        {
            FlowAppend("{\"v\":1,\"type\":\"confirm\"," + FlowCommonJson(t, px)
                + ",\"supported\":" + (supported ? "true" : "false")
                + ",\"same_vol\":" + sameVol + ",\"opp_vol\":" + oppVol + ",\"same_n\":" + sameCount + "}");
        }

        private void FlowLogEntry(DateTime t, double px)
        {
            long sv, ov; int sn;
            bool sup = FlowSupported(_side, t, out sv, out ov, out sn);
            FlowAppend("{\"v\":1,\"type\":\"entry\"," + FlowCommonJson(t, px)
                + ",\"via\":\"" + _entryVia + "\",\"supported\":" + (sup ? "true" : "false")
                + ",\"same_vol\":" + sv + ",\"opp_vol\":" + ov + ",\"same_n\":" + sn + "}");
        }

        private void FlowLogTradeClose(DateTime t, string exitName)
        {
            FlowAppend("{\"v\":1,\"type\":\"trade_close\",\"t\":\"" + JT(t)
                + "\",\"instrument\":\"" + Instrument.MasterInstrument.Name
                + "\",\"window\":\"" + WinNames[Math.Min(_nextWin, WinNames.Length - 1)]
                + "\",\"via\":\"" + _entryVia + "\",\"exit\":\"" + exitName + "\"}");
        }

        private void FlowOpenTrack(string id, DateTime t, int side, double px)
        {
            if (_flowTracks.Count >= 32)
            {
                FlowLogOutcome(_flowTracks[0], "overflow", (t - _flowTracks[0].T0).TotalSeconds);
                _flowTracks.RemoveAt(0);
            }
            _flowTracks.Add(new FlowTrack { Id = id, T0 = t, Side = side, Px0 = px });
        }

        private void FlowTracksUpdate(DateTime t, double px)
        {
            for (int i = _flowTracks.Count - 1; i >= 0; i--)
            {
                FlowTrack tr = _flowTracks[i];
                double fav = (px - tr.Px0) * tr.Side;
                if (fav > tr.MaxFav) tr.MaxFav = fav;
                if (fav < tr.MinFav) tr.MinFav = fav;
                double secs = (t - tr.T0).TotalSeconds;
                if (secs >= 30 && !tr.C30) { tr.C30 = true; tr.Fav30 = tr.MaxFav; tr.Adv30 = tr.MinFav; }
                if (secs >= 60 && !tr.C60) { tr.C60 = true; tr.Fav60 = tr.MaxFav; tr.Adv60 = tr.MinFav; }
                if (secs >= 120 && !tr.C120) { tr.C120 = true; tr.Fav120 = tr.MaxFav; tr.Adv120 = tr.MinFav; }
                if (secs >= 600)
                {
                    FlowLogOutcome(tr, "tracked", secs);
                    _flowTracks.RemoveAt(i);
                }
            }
        }

        private void FlowFlushTracks(string how)
        {
            foreach (FlowTrack tr in _flowTracks)
                FlowLogOutcome(tr, how,
                    _lastTick == DateTime.MinValue ? 0 : (_lastTick - tr.T0).TotalSeconds);
            _flowTracks.Clear();
        }

        // "secs" = track age at close: a checkpoint is only genuine when
        // secs >= its horizon (truncated tracks would otherwise fake flat
        // checkpoints — review 2026-08-02).
        private void FlowLogOutcome(FlowTrack tr, string how, double ageSecs)
        {
            FlowAppend("{\"v\":1,\"type\":\"outcome\",\"id\":\"" + tr.Id + "\",\"t0\":\"" + JT(tr.T0)
                + "\",\"side\":" + tr.Side + ",\"px0\":" + J(tr.Px0)
                + ",\"secs\":" + (int)Math.Max(0, ageSecs)
                + ",\"mfe30_t\":" + FlowTicks(tr.C30 ? tr.Fav30 : tr.MaxFav) + ",\"mae30_t\":" + FlowTicks(tr.C30 ? tr.Adv30 : tr.MinFav)
                + ",\"mfe60_t\":" + FlowTicks(tr.C60 ? tr.Fav60 : tr.MaxFav) + ",\"mae60_t\":" + FlowTicks(tr.C60 ? tr.Adv60 : tr.MinFav)
                + ",\"mfe120_t\":" + FlowTicks(tr.C120 ? tr.Fav120 : tr.MaxFav) + ",\"mae120_t\":" + FlowTicks(tr.C120 ? tr.Adv120 : tr.MinFav)
                + ",\"mfe600_t\":" + FlowTicks(tr.MaxFav) + ",\"mae600_t\":" + FlowTicks(tr.MinFav)
                + ",\"how\":\"" + how + "\"}");
        }

        private int FlowTicks(double points)
        {
            return (int)Math.Round(points / TickSize);
        }

        #region Properties
        [NinjaScriptProperty]
        [Display(Name = "Trade 18:00 ET (Globex reopen)", Description = "Hunt the opening-candle breakout at the 18:00 ET session begin.", GroupName = "01. Sessions", Order = 0)]
        public bool TradeGlobexReopen { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Trade 20:00 ET (evening)", Description = "Hunt the opening-candle breakout at 20:00 ET (session begin + 2h).", GroupName = "01. Sessions", Order = 1)]
        public bool TradeEvening { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Trade 09:30 ET (US open)", Description = "Hunt the opening-candle breakout at the 09:30 ET US equity open (session begin + 15h30m).", GroupName = "01. Sessions", Order = 2)]
        public bool TradeUsOpen { get; set; }

        [NinjaScriptProperty, Range(1, 480)]
        [Display(Name = "Entry window (minutes)", Description = "Hunt breaks/entries only this long after each window opens. An OPEN position is never closed by this clock — it runs to stop/target. A window that opens while a position is still on is skipped.", GroupName = "01. Sessions", Order = 3)]
        public int EntryWindowMinutes { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use whipsaw filter", Description = "OFF = naive chase at the break print (research: -$110/trade avg). ON = hold+extension confirmation with re-entry veto.", GroupName = "02. Signal", Order = 0)]
        public bool UseWhipsawFilter { get; set; }

        [NinjaScriptProperty, Range(0, 300)]
        [Display(Name = "Hold seconds", Description = "Seconds the break must survive outside the range before entry. Best Phase-1 cell: 30.", GroupName = "02. Signal", Order = 1)]
        public int HoldSeconds { get; set; }

        [NinjaScriptProperty, Range(0, 3)]
        [Display(Name = "Extension (xR30)", Description = "Required max excursion beyond the level, as a fraction of the opening candle range. Best Phase-1 cell: 0.25.", GroupName = "02. Signal", Order = 2)]
        public double ExtensionR30 { get; set; }

        [NinjaScriptProperty, Range(5, 300)]
        [Display(Name = "Candle seconds", GroupName = "02. Signal", Order = 3)]
        public int CandleSeconds { get; set; }

        [NinjaScriptProperty, Range(0, 100)]
        [Display(Name = "Min R30 ticks", Description = "Skip the window if the opening candle range is smaller (degenerate).", GroupName = "02. Signal", Order = 4)]
        public int MinR30Ticks { get; set; }

        [NinjaScriptProperty, Range(1, 100)]
        [Display(Name = "Contracts", GroupName = "03. Trade", Order = 0)]
        public int Contracts { get; set; }

        [NinjaScriptProperty, Range(2, 100)]
        [Display(Name = "ATR period", Description = "Wilder ATR on the PRIMARY chart series — the chart timeframe defines what the ATR measures.", GroupName = "03. Trade", Order = 1)]
        public int AtrPeriod { get; set; }

        [NinjaScriptProperty, Range(0.25, 10)]
        [Display(Name = "Stop (x ATR)", Description = "Initial stop = fill -/+ this many ATRs. Hand-draggable once working.", GroupName = "03. Trade", Order = 2)]
        public double AtrStopMult { get; set; }

        [NinjaScriptProperty, Range(0.25, 10)]
        [Display(Name = "Target (x ATR)", Description = "Initial target = fill +/- this many ATRs. Hand-draggable once working.", GroupName = "03. Trade", Order = 3)]
        public double AtrTargetMult { get; set; }

        [NinjaScriptProperty, Range(1, 10)]
        [Display(Name = "Max trades per window", GroupName = "03. Trade", Order = 4)]
        public int MaxTradesPerWindow { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use breakeven", Description = "Move the stop to entry (+offset) once price covers a % of the entry->target run. When it triggers it overrides a hand-dragged stop ONCE.", GroupName = "04. Breakeven", Order = 0)]
        public bool UseBreakeven { get; set; }

        [NinjaScriptProperty, Range(1, 99)]
        [Display(Name = "Breakeven trigger (% of run)", Description = "Percent of the entry->target distance that must be covered before the stop moves to breakeven.", GroupName = "04. Breakeven", Order = 1)]
        public int BreakevenPercent { get; set; }

        [NinjaScriptProperty, Range(-20, 20)]
        [Display(Name = "Breakeven offset (ticks)", Description = "Breakeven stop = entry +/- this many ticks (positive covers commissions).", GroupName = "04. Breakeven", Order = 2)]
        public int BreakevenOffsetTicks { get; set; }

        [NinjaScriptProperty, Range(0, 100000)]
        [Display(Name = "Daily profit target (USD)", Description = "Real-time (realized + unrealized). On hit: flatten + no more entries until next session. 0 = off.", GroupName = "05. Daily limits", Order = 0)]
        public double DailyProfitTargetUSD { get; set; }

        [NinjaScriptProperty, Range(0, 100000)]
        [Display(Name = "Daily loss limit (USD)", Description = "Real-time (realized + unrealized). On hit: flatten + no more entries until next session. 0 = off.", GroupName = "05. Daily limits", Order = 1)]
        public double DailyLossLimitUSD { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Account-wide (all markets)", Description = "Watch the ACCOUNT's combined day PnL: every instance of this strategy on the account flattens together on a breach (BigPrints shared governor). OFF = this instance's own PnL only.", GroupName = "05. Daily limits", Order = 2)]
        public bool UseAccountDailyPnL { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show drawings", GroupName = "06. Visuals", Order = 0)]
        public bool ShowDrawings { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Flow gate mode", Description = "Big-print support gate on the breakout (needs live/Playback L1 tape; historical bars trade as v3). Off = module dormant. LogOnly = log the verdict per confirmed break, trade pure v3. Filter = enter ONLY breaks supported by big prints. Trigger = Filter + a fresh supporting cluster since the break enters at once, no hold wait.", GroupName = "07. Flow gate", Order = 0)]
        public LatigoFlowGateMode FlowGate { get; set; }

        [NinjaScriptProperty, Range(1, 100000)]
        [Display(Name = "Support min volume (contracts)", Description = "A sweep cluster (same-side prints, <=150ms gaps) counts as big-print support at this total size. Reopen-tape scale, not RTH scale — the observed 18:00 NQ cases ran 50-93 contracts.", GroupName = "07. Flow gate", Order = 1)]
        public int SupportMinVolume { get; set; }

        [NinjaScriptProperty, Range(5, 900)]
        [Display(Name = "Support window (s)", Description = "Rolling window of fired clusters consulted at decision time. Same-side volume must reach the minimum AND outweigh the opposite side.", GroupName = "07. Flow gate", Order = 2)]
        public int SupportWindowSec { get; set; }

        [NinjaScriptProperty, Range(0, 5000)]
        [Display(Name = "Cluster gap (ms)", Description = "Max gap between same-side prints folded into one sweep cluster (BigPrints default 150).", GroupName = "07. Flow gate", Order = 3)]
        public int ClusterMilliseconds { get; set; }

        [NinjaScriptProperty, Range(0, 10000)]
        [Display(Name = "Support min MAX print (contracts)", Description = "0 = off. A sweep only counts as support if its largest SINGLE print reaches this size — 'one strong entry' semantics instead of many small prints accumulating to the total.", GroupName = "07. Flow gate", Order = 4)]
        public int SupportMinMaxPrint { get; set; }
        #endregion
    }
}
