# LatigoBreak Phase 1 — Price/Time Detector Grid Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Calibrate the causal price/time confirmation detector on the frozen 24-cell grid and answer Gate G1 on the chronological validation tramo.

**Architecture:** One new module `research/phase1.py` (causal `confirm_index` detector + grid runner) reusing Phase 0 infrastructure: `corpus.py` loaders, `events.detect_events` as the break-event stream, `phase0b.sim_bracket` for identical trade simulation. Full-grid report on calibration AND validation tramos; verdict uses only the pre-selected best-calibration cell.

**Tech Stack:** Python 3, numpy, pytest (no new deps).

## Global Constraints

From `research/preregistration.json` (frozen) and spec §5:

- Grid: `hold_s ∈ {0,2,5,10,20,30}` × `ext_r30 ∈ {0,0.25,0.5,1.0}` = 24 cells; cell (0,0) IS the naive baseline.
- Confirmation (causal, label-blind): first print with `ts − t_break ≥ hold_s` AND cumulative `max_ext ≥ ceil(ext_r30·R30)` ticks, with NO re-entry (≥1 tick inside) since the break. Re-entry before confirmation → veto that event, wait for the next break. One entry per session (first confirmation).
- Entry: confirmation print px + 3 ticks adverse. Stop: opposite candle extreme. Brackets 1R/2R, stop wins ties, time stop 18:30:00 ET — identical sim to Phase 0b.
- New pre-registered detail (this plan, frozen before results): confirmation must fire by **18:20:00 ET** (`t0+1200 s`), else the entry is forfeited — prevents pathological late entries 10 min before the time stop.
- Primary metric (frozen): **expectancy per session in R, 1R bracket view** — sessions in the tramo with no entry count 0.
- Split (frozen): chronological 60/40 over usable NQ sessions. Cell eligibility for selection: ≥15 calibration entries. Best eligible calibration cell → evaluated once on validation.
- G1 (frozen): PASS iff chosen cell's validation E[R/session] > 0 AND > naive (0,0) validation E[R/session]. FAIL → archive or redesign signal; NEVER re-tune past the split.
- MNQ: replicate the chosen cell (all usable MNQ sessions, no split) — direction must agree or the result is flagged fragile.
- Report ALL 24 cells on both tramos (no winner-only reporting).

---

### Task 1: Causal confirmation detector (TDD)

**Files:**
- Create: `research/phase1.py` (detector core only in this task)
- Test: `research/test_phase1.py`

**Interfaces:**
- Consumes: `events.Event` fields (`i_break`, `t_break`, `level`, `side`), `events._ext_ticks`, `corpus.DTYPE/NET_S/TICK_SIZE`, `test_events.tape/CANDLE/T0` helpers.
- Produces: `phase1.confirm_index(a, ev, h, l, r30, hold_s, ext_r30, deadline_net) -> int|None` — tape index of the confirmation print, or None (vetoed by re-entry / deadline / tape end).

- [ ] **Step 1: Write the failing tests**

```python
"""Causal detector tests on synthetic tape (reuses test_events fixtures)."""
import corpus
import events
import phase1
from test_events import CANDLE, T0, tape

S = corpus.NET_S
DEADLINE = T0 + 1200 * S


def _first_event(a, **kw):
    evs, reason = events.detect_events(a, T0, k=1.0, x_s=30.0, **kw)
    assert reason is None and evs
    return evs[0]


def test_naive_cell_confirms_at_break_print():
    a = tape(*CANDLE, (40, 20002.25), (45, 20003.0))
    ev = _first_event(a)
    j = phase1.confirm_index(a, ev, 20002.0, 20000.0, 8, 0, 0.0, DEADLINE)
    assert j == ev.i_break


def test_hold_waits_and_confirms():
    a = tape(*CANDLE, (40, 20002.25), (43, 20002.5), (46, 20002.75))
    ev = _first_event(a)
    j = phase1.confirm_index(a, ev, 20002.0, 20000.0, 8, 5, 0.0, DEADLINE)
    assert j == ev.i_break + 2          # first print with ts-t_break >= 5s


def test_reentry_vetoes_before_hold():
    a = tape(*CANDLE, (40, 20002.25), (43, 20001.75), (50, 20002.5))
    ev = _first_event(a)
    assert phase1.confirm_index(a, ev, 20002.0, 20000.0, 8, 5, 0.0, DEADLINE) is None


def test_extension_requirement_uses_cumulative_max():
    # ext_r30=0.5 -> ceil(0.5*8)=4 ticks; reached at 44s, confirm there (hold=0)
    a = tape(*CANDLE, (40, 20002.25), (44, 20003.0), (48, 20002.25))
    ev = _first_event(a)
    j = phase1.confirm_index(a, ev, 20002.0, 20000.0, 8, 0, 0.5, DEADLINE)
    assert j == ev.i_break + 1


def test_deadline_forfeits():
    a = tape(*CANDLE, (40, 20002.25), (1250, 20002.5))
    ev = _first_event(a)
    assert phase1.confirm_index(a, ev, 20002.0, 20000.0, 8, 20, 0.0, DEADLINE) is None
```

- [ ] **Step 2: Run to verify failure**

Run: `python3 -m pytest test_phase1.py -q` (from `research/`) — Expected: import error (phase1 missing).

- [ ] **Step 3: Implement the detector core in `research/phase1.py`**

```python
"""Phase 1: causal price/time confirmation detector + 24-cell grid runner.

Detector is label-blind: it sees the tape after a break and confirms when
hold AND extension conditions are met before any re-entry (>=1 tick inside).
"""
import math

import corpus
import events

ENTRY_DEADLINE_S = 1200            # 18:20:00 ET, pre-registered in the plan
EPS = 1e-9


def confirm_index(a, ev, h, l, r30, hold_s, ext_r30, deadline_net):
    """Tape index of the confirmation print, or None (veto/deadline)."""
    need_ext = math.ceil(ext_r30 * r30)
    hold_net = int(hold_s * corpus.NET_S)
    max_ext = 0
    for j in range(ev.i_break, len(a)):
        ts, px = int(a["ts"][j]), float(a["px"][j])
        if ts > deadline_net:
            return None
        ext = events._ext_ticks(px, ev.side, ev.level)
        if ext > max_ext:
            max_ext = ext
        re_in = (px <= h - corpus.TICK_SIZE + EPS) if ev.side > 0 \
            else (px >= l + corpus.TICK_SIZE - EPS)
        if re_in:
            return None
        if ts - ev.t_break >= hold_net and max_ext >= need_ext:
            return j
    return None
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `python3 -m pytest test_phase1.py -q` — Expected: 5 passed. Debug the detector, never the tests.

- [ ] **Step 5: Commit**

```bash
git add research/phase1.py research/test_phase1.py
git commit -m "feat(research): causal price/time confirmation detector"
```

---

### Task 2: Grid runner, G1 verdict, MNQ replication

**Files:**
- Modify: `research/phase1.py` (append runner + main)
- Create (output, versioned): `docs/research/phase1-report.md`

**Interfaces:**
- Consumes: `phase0b.sim_bracket(a, ev, h, l, rr, inst, t0)` (shim `events.Event` built at the confirmation print), `phase0b.REG`, `corpus.sessions/load_session`.
- Produces: the G1 verdict; per-cell rows also dumped to `out/phase1_cells.json` for Phase 2 reuse.

- [ ] **Step 1: Append the runner to `research/phase1.py`**

```python
# --- grid runner -----------------------------------------------------------
import json
from datetime import datetime
from pathlib import Path

import numpy as np

import phase0b

DOCS = Path(__file__).parent.parent / "docs" / "research"
GRID_HOLD = phase0b.REG["phase1_grid"]["hold_s"]
GRID_EXT = phase0b.REG["phase1_grid"]["ext_r30"]
MIN_CAL_ENTRIES = 15


def usable_dates(inst):
    out = []
    for d in sorted(corpus.sessions(inst)):
        a = corpus.load_session(inst, d)
        if a is None or len(a) < phase0b.REG["min_prints_watch"]:
            continue
        t0 = corpus.dt_to_net(datetime(d.year, d.month, d.day, 18, 0))
        watch_n = int(((a["ts"] >= t0 + 30 * corpus.NET_S) &
                       (a["ts"] <= t0 + 930 * corpus.NET_S)).sum())
        if watch_n >= phase0b.REG["min_prints_watch"]:
            out.append(d)
    return out


def run_cell(inst, dates, hold_s, ext_r30):
    """One trade max per session; returns list of per-trade dicts + n_sessions."""
    trades = []
    for d in dates:
        a = corpus.load_session(inst, d)
        t0 = corpus.dt_to_net(datetime(d.year, d.month, d.day, 18, 0))
        evs, _ = events.detect_events(
            a, t0, k=phase0b.REG["k"], x_s=phase0b.REG["x_timeout_s"],
            e_floor=phase0b.REG["e_floor_ticks"],
            r30_min=phase0b.REG["r30_min_ticks"])
        if not evs:
            continue
        fc = events.first_candle(a, t0)
        h, l, r30 = fc
        deadline = t0 + ENTRY_DEADLINE_S * corpus.NET_S
        for ev in evs:
            j = confirm_index(a, ev, h, l, r30, hold_s, ext_r30, deadline)
            if j is None:
                continue
            shim = events.Event(ev.side, int(a["ts"][j]), j,
                                float(a["px"][j]), ev.level)
            res = {rr: phase0b.sim_bracket(a, shim, h, l, rr, inst, t0)
                   for rr in (1, 2)}
            if res[1] is None:
                break                          # stop already crossed: no trade
            res["chase_ticks"] = round((float(a["px"][j]) - ev.level)
                                       * ev.side / corpus.TICK_SIZE)
            res["date"] = d.isoformat()
            trades.append(res)
            break                              # one entry per session
    return trades


def cell_stats(trades, n_sessions):
    r1 = [t[1]["r"] for t in trades]
    return {"n": len(trades),
            "win": round(100 * np.mean([t[1]["net"] > 0 for t in trades]), 0) if trades else 0,
            "er_trade": round(float(np.mean(r1)), 3) if trades else 0.0,
            "er_sess": round(float(np.sum(r1)) / max(1, n_sessions), 4),
            "usd_trade": round(float(np.mean([t[1]["net"] for t in trades])), 2) if trades else 0.0,
            "er2_sess": round(float(np.sum([t[2]["r"] for t in trades if t[2]]))
                              / max(1, n_sessions), 4),
            "chase_med": float(np.median([t["chase_ticks"] for t in trades])) if trades else 0.0}


def main():
    dates = usable_dates("NQ")
    n_cal = int(len(dates) * 0.6)
    cal, val = dates[:n_cal], dates[n_cal:]
    rows = {}
    for hs in GRID_HOLD:
        for ex in GRID_EXT:
            key = f"h{hs}_x{ex}"
            rows[key] = {"hold": hs, "ext": ex,
                         "cal": cell_stats(run_cell("NQ", cal, hs, ex), len(cal)),
                         "val": cell_stats(run_cell("NQ", val, hs, ex), len(val))}
            print(key, "cal", rows[key]["cal"], "val", rows[key]["val"], flush=True)
    eligible = {k: v for k, v in rows.items() if v["cal"]["n"] >= MIN_CAL_ENTRIES}
    best_key = max(eligible, key=lambda k: eligible[k]["cal"]["er_sess"])
    best = rows[best_key]
    naive = rows["h0_x0"]
    g1 = (best["val"]["er_sess"] > 0
          and best["val"]["er_sess"] > naive["val"]["er_sess"])
    mnq_dates = usable_dates("MNQ")
    mnq = cell_stats(run_cell("MNQ", mnq_dates, best["hold"], best["ext"]),
                     len(mnq_dates))

    corpus.OUT.mkdir(exist_ok=True)
    (corpus.OUT / "phase1_cells.json").write_text(json.dumps(rows))
    hdr = ("| cell | cal n | cal E[R]/sess | val n | val E[R]/sess | "
           "val E[R]/trade | val win% | val $/trade | chase med (t) |")
    sep = "|" + "---|" * 9
    def fmt(k, v):
        return (f"| {k} | {v['cal']['n']} | {v['cal']['er_sess']:+.4f} | "
                f"{v['val']['n']} | {v['val']['er_sess']:+.4f} | "
                f"{v['val']['er_trade']:+.3f} | {v['val']['win']:.0f} | "
                f"{v['val']['usd_trade']:+.2f} | {v['val']['chase_med']:.0f} |")
    lines = ["# Phase 1 — Detector Grid & G1 Verdict", "",
             f"NQ usable sessions: {len(dates)} (cal {len(cal)} / val {len(val)}, "
             f"chronological; split date {val[0].isoformat()}). "
             f"Eligibility: >={MIN_CAL_ENTRIES} cal entries. "
             f"Entry deadline 18:20:00 ET (pre-registered).", "",
             hdr, sep] + [fmt(k, v) for k, v in rows.items()] + [
             "",
             f"**Selected on calibration: {best_key}** (hold={best['hold']}s, "
             f"ext={best['ext']}xR30) -> validation E[R]/session = "
             f"{best['val']['er_sess']:+.4f} vs naive {naive['val']['er_sess']:+.4f}.",
             "",
             f"MNQ replication ({len(mnq_dates)} sessions, no split): "
             f"n={mnq['n']}, E[R]/session={mnq['er_sess']:+.4f}, "
             f"E[$/trade]={mnq['usd_trade']:+.2f}.",
             "",
             "## G1 verdict", "",
             f"**G1 {'PASS' if g1 else 'FAIL'}** — chosen cell validation "
             f"E[R]/session {'>' if g1 else '<='} 0 and "
             f"{'beats' if g1 else 'does not beat'} naive. " +
             ("Proceed to Phase 2 (order-flow overlay)." if g1 else
              "Per spec section 7: archive or redesign the signal; no re-tuning "
              "past the split.")]
    DOCS.mkdir(parents=True, exist_ok=True)
    (DOCS / "phase1-report.md").write_text("\n".join(lines))
    print("\n".join(lines[-12:]))


if __name__ == "__main__":
    main()
```

- [ ] **Step 2: Run the full suite, then the grid**

Run: `python3 -m pytest -q` — Expected: all pass (detector tests included).
Run: `python3 phase1.py` (24 cells × 203 sessions; expect a few minutes — per-cell progress prints)
Cross-checks before trusting: cell h0_x0 must reproduce Phase 0b naive (n=203 total across tramos, E[$/trade] ≈ −110 pooled); `n` must be non-increasing as hold/ext grow (stricter confirmation ⇒ fewer entries — a violation means a detector bug); chase_med must be non-decreasing in ext.

- [ ] **Step 3: Commit**

```bash
git add research/phase1.py docs/research/phase1-report.md
git commit -m "feat(research): phase 1 detector grid + G1 verdict"
```

---

## Self-review (done at plan-writing time)

- Spec §5 coverage: grid ✓, chase entry ✓, fixed management via shared `sim_bracket` ✓, chronological 60/40 ✓, all-cells reporting ✓, naive baseline as cell (0,0) ✓, G1 wording matches spec §7 ✓, MNQ replication ✓.
- `res[1] is None` guard: `sim_bracket` returns None when entry is already at/through the stop (r_pts < tick) — the session is treated as no-trade (break, then continue scanning would allow later events… simplification: forfeit session; disclosed in code comment) — actually `break` exits the event loop, consistent with "first confirmation consumes the session".
- Type check: `confirm_index` signature matches Task 1 tests; `cell_stats` consumes `sim_bracket` dicts `{"net","r"}` keyed by rr int and `chase_ticks/date` extras.
