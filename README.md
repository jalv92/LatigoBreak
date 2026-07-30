# LatigoBreak

Opening-range breakout strategy for the **18:00 ET Globex reopen** (NQ/MNQ, NinjaTrader 8), built around one core problem: detecting **whipsaws** — breaks of the first 30-second candle that snap back inside the range — and refusing to trade them.

## Status

**Research phase.** No strategy code exists yet, by design. The whipsaw detector is being validated offline in Python against ~200+ sessions of real tick data with pre-registered kill gates. If the signal can't beat its costs even with a perfect filter (the "oracle test"), the project gets archived honestly — see the Pullback project for the precedent.

- Design: [`docs/specs/2026-07-30-whipsaw-detector-research-design.md`](docs/specs/2026-07-30-whipsaw-detector-research-design.md)
- Research pipeline: `research/`

## Method rules inherited from previous projects

1. Python-first: no NinjaScript before the signal proves itself on data.
2. Thresholds in fractions of the candle range, never fixed ticks.
3. Pre-registered gates and trial budgets; all results reported, not just winners.
4. Risk management cannot manufacture expectancy.
