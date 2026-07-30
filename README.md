# LatigoBreak

Opening-range breakout strategy for the **18:00 ET Globex reopen** (NQ/MNQ, NinjaTrader 8), built around one core problem: detecting **whipsaws** — breaks of the first 30-second candle that snap back inside the range — and refusing to trade them.

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
2. Apply to an NQ/MNQ chart whose trading-hours template is **ETH/24-7** (the session must BEGIN at the 18:00 ET reopen; an RTH template silently makes it trade the 9:30 open). A 30-second chart is recommended for the visuals; the logic runs on its own 1-tick series.
3. Strategy Analyzer runs require Order Fill Resolution **High / Tick / 1**.
4. Defaults = best research cell (hold 30 s, extension 0.25×R30, 1 trade/session, 1R bracket, time stop 18:30). `Use whipsaw filter = false` reproduces the naive chase — useful in Playback to watch the filter earn its keep.

### Playback checklist

1. Candle 18:00:00–18:00:30 drawn with H/L rays and `R30=Nt` label.
2. First break beyond H/L marks a dot (blue up / magenta down).
3. Snap-back inside prints a red `✕ n.ns` and the strategy re-arms (both sides).
4. Confirmed break (30 s outside + 0.25×R30 extension) enters market with stop at the opposite extreme and 1R target; triangle at entry.
5. No new breaks after 18:15:30; no entries after 18:20; open position flattens at 18:30.
6. Rewind mid-session and replay: old drawings of the discarded pass are wiped, state resets cleanly, the session re-detects from scratch.
7. Second session in the same Playback run resets counters (fresh candle, fresh trade budget).

## Method rules inherited from previous projects

1. Python-first: no NinjaScript before the signal proves itself on data.
2. Thresholds in fractions of the candle range, never fixed ticks.
3. Pre-registered gates and trial budgets; all results reported, not just winners.
4. Risk management cannot manufacture expectancy.
