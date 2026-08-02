# LatigoBreak

Opening-range breakout strategy (NQ/MNQ, NinjaTrader 8) built around one core problem: detecting **whipsaws** — breaks of the first 30-second candle that snap back inside the range — and refusing to trade them. Originally researched on the **18:00 ET Globex reopen**; the NT8 lab (v3) can hunt the same setup at up to three session windows per trading day (18:00 / 20:00 / 09:30 ET).

## Status

**Research complete; NT8 sim/Playback laboratory shipped.** The whipsaw detector was validated offline in Python against 203 NQ sessions with pre-registered kill gates:

- **G0 (oracle) PASS** — perfect break/whipsaw foresight pays (+$236/trade at 1R). There is signal to separate.
- **G1 (causal price/time detector) FAIL** — all 24 grid cells negative; the filter recovers most of the naive chase's −$110/trade but never crosses zero. Diagnosis: by the time price *proves* the break, the chase entry is ~20 NQ points late. Entry-price problem, not a detection problem. Full tables in [`docs/research/`](docs/research/).

`LatigoBreakStrategy.cs` implements the detector in NT8 anyway — **as a Playback/sim laboratory** for iterating the redesign (order-flow trigger, entry style) with live visual feedback, not as a validated edge. Running it live real-money contradicts this repo's own research.

- Research design: [`docs/specs/2026-07-30-whipsaw-detector-research-design.md`](docs/specs/2026-07-30-whipsaw-detector-research-design.md)
- NT8 design: [`docs/specs/2026-07-30-nt8-strategy-design.md`](docs/specs/2026-07-30-nt8-strategy-design.md)
- Research pipeline: `research/`

## NT8 usage

1. Copy `LatigoBreakStrategy.cs` to `Documents/NinjaTrader 8/bin/Custom/Strategies/` and compile (F5).
2. Apply to an NQ/MNQ chart whose trading-hours template is **CME ETH** (the session must BEGIN at the 18:00 ET reopen — the three windows are fixed offsets from the session begin, so an RTH template silently misplaces every window). A 30-second chart is recommended for the visuals; the logic runs on its own 1-tick series.
3. Strategy Analyzer runs require Order Fill Resolution **High / Tick / 1**.
4. **Session windows (v3):** three per trading day, each with its own checkbox — 18:00 ET Globex reopen, 20:00 ET, 09:30 ET US open (all ON by default). Each window hunts breaks/entries for `Entry window (minutes)` (default 30) after it opens. **An open position is never closed by the clock** — it runs to stop/target (outer bound: exit-on-session-close at the 17:00 ET halt). A window is skipped unless the strategy was already flat and idle at its open (in a position or pending entry at that moment ⇒ skipped, even if the trade closes seconds later — this also guarantees the opening candle is never built from a truncated tick sample). The effective entry window is floored at `Candle seconds + 60 s` and capped at the next enabled window's open; any adjustment is disclosed in the Output window. `Max trades per window` (default 1) re-arms inside the window after a completed trade.
5. Signal defaults = best research cell (hold 30 s, extension 0.25×R30). `Use whipsaw filter = false` reproduces the naive chase — useful in Playback to watch the filter earn its keep.
6. **Trade management:** initial stop = 2×ATR and target = 2×ATR (Wilder ATR on the primary chart series — the chart timeframe defines the ATR's meaning; both multipliers adjustable), priced off the actual fill. **Brackets are hand-draggable (v3):** they are live-until-cancelled Exit orders (`LB_Stop` / `LB_Target`), not Set* orders, so moving them in Chart Trader sticks — the strategy adopts your move (Output window logs it) and never snaps them back. Cancelling one by hand leaves that side unprotected (logged, your call). Optional breakeven once price covers a % of the entry→target run (default 50%, off by default; when it triggers it overrides a hand-dragged stop once). Real-time daily profit target / loss limit in USD (realized + unrealized; hit ⇒ flatten + lockout until next session). `Account-wide (all markets)` = the BigPrints shared governor: every LatigoBreak instance on the account flattens together on a combined breach (default OFF).

### Playback checklist

1. Opening candle of each enabled window (first 30 s) drawn with H/L rays; `R30=Nt` in the Output window.
2. First break beyond H/L marks a dot (blue up / magenta down).
3. Snap-back inside prints a red `✕` and the strategy re-arms (both sides).
4. Confirmed break (30 s outside + 0.25×R30 extension) enters market; stop/target brackets appear priced off the fill; triangle at entry.
5. No new breaks or entries after `Entry window (minutes)`; an open position is NOT flattened by the clock.
6. Drag the stop or target in Chart Trader: it stays where you put it and the Output window logs "moved by hand — adopted".
7. Hold a position through the next window's open (e.g. entry 19:58, still open at 20:00): that window is skipped, the following one arms normally.
8. Rewind mid-session and replay: old drawings of the discarded pass are wiped, state resets cleanly, the day re-detects from scratch.
9. Second session in the same Playback run resets counters (fresh windows, fresh trade budgets).

## Method rules inherited from previous projects

1. Python-first: no NinjaScript before the signal proves itself on data.
2. Thresholds in fractions of the candle range, never fixed ticks.
3. Pre-registered gates and trial budgets; all results reported, not just winners.
4. Risk management cannot manufacture expectancy.
