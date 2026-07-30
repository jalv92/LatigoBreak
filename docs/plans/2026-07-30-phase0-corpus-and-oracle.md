# LatigoBreak Phase 0 — Corpus, Descriptives & Oracle Gate Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the offline Python pipeline that turns raw NT8 `.ncd` tick files into a labeled corpus of 18:00 ET reopen breakout events and answers Gate G0 (oracle test).

**Architecture:** Five flat scripts in `research/` sharing one vendored parser: `corpus.py` (session discovery + cached loading), `events.py` (pure event engine, anti-lookahead by construction), `phase0a.py` (label-free descriptives), `freeze_labels.py` (mechanical pre-registration), `phase0b.py` (base rates + oracle sim). Versioned reports in `docs/research/`; caches in gitignored `research/out/`.

**Tech Stack:** Python 3 (system), numpy, pandas, pytest. No new dependencies.

## Global Constraints

Copied from `docs/specs/2026-07-30-whipsaw-detector-research-design.md`:

- Reference candle 18:00:00.000–18:00:29.999 ET; watch window for NEW breaks 18:00:30–18:15:30 ET (active events may resolve later).
- Break = first print ≥ 1 tick beyond H (or ≤ 1 tick below L) on an inside→outside transition. Re-arm both sides until window end.
- Whipsaw = re-enters ≥1 tick inside within timeout X, or timeout expires with neither re-entry nor extension. Real = reaches E first.
- E = max(k×R30, 8 ticks) with **k = 1.0 frozen now**; the 8-tick floor is the economic anchor (2×(2-tick spread + 1-tick slip) + 2 margin). *Amendment vs spec §4: k is fixed up front rather than chosen post-0a — fewer human degrees of freedom, stricter than spec.*
- X frozen mechanically post-0a: X = 5·round(P90(re-entry times ≤120 s, NQ pooled)/5), clamped to [30, 90] s.
- Degenerate session exclusion: R30 < 4 ticks.
- Cost model: 2-tick spread + 1-tick slippage per entry (adverse), commission RT NQ $4.50 / MNQ $1.50. Tick 0.25 pts; point value NQ $20 / MNQ $2.
- Entry sim: break/confirmation print price + 3 ticks adverse. Stop = opposite candle extreme. Bracket views 1R and 2R; ties resolve to STOP. Time stop 18:30:00 ET.
- MFE/MAE horizons: 1, 2, 5, 15 min post-break.
- G0 verdict: PASS iff oracle net expectancy per trade > 0 in at least one bracket view (NQ primary); MNQ reported as replication.
- NQ is primary; every conclusion replicated on MNQ or flagged fragile.
- No tick data, caches, or pickles committed (`.gitignore` already covers `research/out/`, `*.pkl`).
- Anti-lookahead: engines consume the tape in one forward pass; monotonic-timestamp assert.
- All prose/code/comments in English.

**Machine-local paths:** NT8 tick db at `/mnt/c/Users/javlo/Documents/NinjaTrader 8/db/tick` (override via env `NT8_TICK_DB`). File naming is END-of-hour: `*1900.Last.ncd` = 18:00:00–18:59:59 ET.

**Run tests:** `cd "projects/Trading/LatigoBreak/research" && python3 -m pytest -q`

---

### Task 1: Vendored parser + corpus loader

**Files:**
- Create: `research/ncd_parse.py` (vendored copy of `projects/Trading/MFF-Sim/ncd_parse.py`, header comment added)
- Create: `research/corpus.py`
- Test: `research/test_corpus.py` (real-data smoke; auto-skips if db absent)

**Interfaces:**
- Consumes: `ncd_parse.read_ticks(path)` → yields `(tt: int .NET ticks, price: float, boff: int, aoff: int, vol: int)`. Aggressor rule (validated): `aoff == 0` → buy (+1), else `boff == 0` → sell (−1), else 0.
- Produces (used by Tasks 2–5):
  - `corpus.DTYPE` — numpy dtype `[("ts","i8"),("px","f8"),("side","i1"),("vol","i4")]`
  - `corpus.net_to_dt(tt:int) -> datetime` / `corpus.dt_to_net(dt:datetime) -> int`
  - `corpus.sessions(instrument:str) -> dict[date, Path]` — front-month `*1900.Last.ncd` per date (dominant volume), JSON-cached
  - `corpus.load_session(instrument:str, d:date) -> np.ndarray|None` — DTYPE array, ts-sorted, npz-cached
  - `corpus.prev_close(instrument:str, d:date) -> float|None` — last print of the 16:00–17:00 file (pre-halt reference)
  - Constants: `TICK_SIZE=0.25`, `POINT_VALUE={"NQ":20.0,"MNQ":2.0}`, `COMMISSION_RT={"NQ":4.50,"MNQ":1.50}`, `SPREAD_TICKS=2`, `SLIP_TICKS=1`, `NET_S=10_000_000` (.NET ticks per second)

- [ ] **Step 1: Vendor the parser**

```bash
cd "/home/javlo/Code Projects/main-project/projects/Trading/LatigoBreak"
cp "../MFF-Sim/ncd_parse.py" research/ncd_parse.py
```

Then edit the module docstring first line to add provenance: `Vendored 2026-07-30 from projects/Trading/MFF-Sim/ncd_parse.py (validated: price corr 0.9998 vs external ref; spread field NOT trustworthy — side only).`

- [ ] **Step 2: Write `research/corpus.py`**

```python
"""Session discovery + tick loading for the 18:00 ET Globex reopen study.

NT8 hourly .ncd files are named by END of hour: *1900.Last.ncd holds
18:00:00-18:59:59 ET (verified 2026-07-30; the near-empty *1800 bucket is
the 17:00-18:00 maintenance halt). Timestamps are naive ET wall clock.
"""
import json
import os
from datetime import date, datetime, timedelta
from pathlib import Path

import numpy as np

from ncd_parse import read_ticks

DB = Path(os.environ.get("NT8_TICK_DB",
                         "/mnt/c/Users/javlo/Documents/NinjaTrader 8/db/tick"))
OUT = Path(__file__).parent / "out"

NET_S = 10_000_000            # .NET ticks per second
TICK_SIZE = 0.25
POINT_VALUE = {"NQ": 20.0, "MNQ": 2.0}
COMMISSION_RT = {"NQ": 4.50, "MNQ": 1.50}
SPREAD_TICKS = 2
SLIP_TICKS = 1

DTYPE = np.dtype([("ts", "i8"), ("px", "f8"), ("side", "i1"), ("vol", "i4")])


def net_to_dt(tt):
    return datetime(1, 1, 1) + timedelta(microseconds=tt // 10)


def dt_to_net(dt):
    return int((dt - datetime(1, 1, 1)).total_seconds() * NET_S)


def _file_ticks(path):
    rows = [(tt, px, 1 if aoff == 0 else (-1 if boff == 0 else 0), vol)
            for tt, px, boff, aoff, vol in read_ticks(path)]
    return np.array(rows, dtype=DTYPE)


def sessions(instrument):
    """date -> front-month *1900.Last.ncd path (dominant volume per date)."""
    cache = OUT / f"sessions_{instrument}.json"
    if cache.exists():
        raw = json.loads(cache.read_text())
        return {date.fromisoformat(k): Path(v) for k, v in raw.items()}
    cand = {}
    for f in DB.glob(f"{instrument} */*1900.Last.ncd"):
        cand.setdefault(f.name[:8], []).append(f)
    out = {}
    for dstr, files in sorted(cand.items()):
        best = max(files, key=lambda f: int(_file_ticks(f)["vol"].sum()))
        d = date(int(dstr[:4]), int(dstr[4:6]), int(dstr[6:]))
        out[d] = best
    OUT.mkdir(exist_ok=True)
    cache.write_text(json.dumps({k.isoformat(): str(v) for k, v in out.items()},
                                indent=0))
    return out


def load_session(instrument, d):
    """DTYPE array for 18:00:00-18:59:59 ET of date d, ts-sorted. npz-cached."""
    cache = OUT / "cache" / f"{instrument}_{d.strftime('%Y%m%d')}.npz"
    if cache.exists():
        return np.load(cache)["a"]
    f = sessions(instrument).get(d)
    if f is None:
        return None
    a = _file_ticks(f)
    a = a[np.argsort(a["ts"], kind="stable")]
    cache.parent.mkdir(parents=True, exist_ok=True)
    np.savez_compressed(cache, a=a)
    return a


def prev_close(instrument, d):
    """Last print of the 16:00-17:00 file (pre-halt reference), or None."""
    f = sessions(instrument).get(d)
    if f is None:
        return None
    g = f.with_name(f.name.replace("1900.Last", "1700.Last"))
    if not g.exists():
        return None
    a = _file_ticks(g)
    return float(a["px"][np.argmax(a["ts"])]) if len(a) else None
```

- [ ] **Step 3: Write `research/test_corpus.py`**

```python
"""Real-data smoke tests. Skip everything if the NT8 tick db is absent."""
from datetime import time

import numpy as np
import pytest

import corpus

pytestmark = pytest.mark.skipif(not corpus.DB.exists(),
                                reason="NT8 tick db not on this machine")


def test_sessions_nq_nonempty_and_sane():
    s = corpus.sessions("NQ")
    assert len(s) > 100                       # ~200-230 expected
    assert all(p.name.endswith("1900.Last.ncd") for p in s.values())
    assert all(p.exists() for p in s.values())
    years = {d.year for d in s}
    assert years <= {2025, 2026}


def test_load_session_covers_reopen_hour():
    s = corpus.sessions("NQ")
    d = sorted(s)[len(s) // 2]                # a mid-corpus date
    a = corpus.load_session("NQ", d)
    assert a is not None and len(a) > 100
    assert np.all(np.diff(a["ts"]) >= 0)      # sorted
    first, last = corpus.net_to_dt(int(a["ts"][0])), corpus.net_to_dt(int(a["ts"][-1]))
    assert time(18, 0) <= first.time() <= time(18, 5)
    assert last.time() <= time(19, 0)
    assert 10_000 < float(np.median(a["px"])) < 40_000
    assert set(np.unique(a["side"])) <= {-1, 0, 1}


def test_mnq_mirror_exists():
    assert len(corpus.sessions("MNQ")) > 50
```

- [ ] **Step 4: Run the tests**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/LatigoBreak/research" && python3 -m pytest test_corpus.py -q`
Expected: 3 passed (first run is slow: builds the sessions cache by parsing every candidate file once). If the session count assert fails, print `len(s)` and adjust ONLY if the real corpus is legitimately smaller — record the true number in the commit message.

- [ ] **Step 5: Commit**

```bash
cd "/home/javlo/Code Projects/main-project/projects/Trading/LatigoBreak"
git add research/ && git commit -m "feat(research): vendored ncd parser + session corpus loader"
```

---

### Task 2: Event engine (TDD)

**Files:**
- Create: `research/events.py`
- Test: `research/test_events.py`

**Interfaces:**
- Consumes: `corpus.DTYPE`, `corpus.dt_to_net`, `corpus.NET_S`, `corpus.TICK_SIZE`.
- Produces (used by phase0a/phase0b):
  - `events.Event` dataclass: `side:int(+1/-1)`, `t_break:int`, `i_break:int` (tape index of break print), `break_px:float`, `level:float`, `label:str|None ('real'|'whipsaw')`, `t_resolve:int|None`, `max_ext:int` (ticks beyond level at resolution), `ret_s:float|None`
  - `events.first_candle(a, t0) -> (h:float, l:float, r30:int)|None`
  - `events.detect_events(a, t0, k:float, x_s:float, e_floor:int=8, r30_min:int=4) -> (list[Event]|None, str|None)` — `(None, reason)` when session unusable/excluded; `(events, None)` otherwise.

Engine semantics (frozen; the tests below are the executable spec):
1. Candle = prints with `t0 <= ts < t0+30s`. H/L from prints; `r30 = round((H-L)/tick)`.
2. Watch prints from `t0+30s` onward, one forward pass, monotonic-ts assert.
3. A side is "armed" via transition: previous print inside `[L,H]` (init True), current print ≥1 tick beyond a level, `ts <= t0+930s` → open event. Only one active event at a time.
4. Active event, per print, in this order: (a) if `ts > t_break + X` → resolve whipsaw at `t_resolve = t_break + X·NET_S` (timeout wins over anything the late print shows); (b) update `max_ext`; if `max_ext >= E` → resolve real; (c) if ≥1 tick inside → resolve whipsaw, `ret_s = (ts - t_break)/NET_S`; if that same print is also ≥1 tick beyond the OPPOSITE level and `ts <= t0+930s` → immediately open the opposite event at this print (gap-through rule).
5. Tape ends with an active event → resolve whipsaw at last ts (conservative).
6. `E = max(ceil(k*r30), e_floor)` ticks.

- [ ] **Step 1: Write the failing tests**

```python
"""Synthetic-tape tests: the executable spec of the event engine."""
from datetime import datetime

import numpy as np
import pytest

import corpus
import events

T0 = corpus.dt_to_net(datetime(2026, 3, 2, 18, 0, 0))
S = corpus.NET_S


def tape(*rows):
    """rows = (seconds_after_1800, px). Candle rows included by caller."""
    return np.array([(T0 + int(s * S), px, 0, 1) for s, px in rows],
                    dtype=corpus.DTYPE)

# Base candle: H=20002.0, L=20000.0 -> R30 = 8 ticks, E = max(8,8) = 8 ticks (2.0 pts)
CANDLE = [(0, 20000.0), (5, 20002.0), (12, 20000.5), (29, 20001.0)]


def run(*watch, k=1.0, x=60.0):
    evs, reason = events.detect_events(tape(*CANDLE, *watch), T0, k=k, x_s=x)
    assert reason is None
    return evs


def test_first_candle():
    h, l, r30 = events.first_candle(tape(*CANDLE, (40, 21000.0)), T0)
    assert (h, l, r30) == (20002.0, 20000.0, 8)


def test_real_break_up():
    evs = run((40, 20002.25), (45, 20003.0), (50, 20004.0))
    assert len(evs) == 1
    e = evs[0]
    assert e.side == 1 and e.level == 20002.0 and e.label == "real"
    assert e.max_ext == 8 and e.break_px == 20002.25


def test_whipsaw_reentry():
    evs = run((40, 20002.25), (43, 20001.75))
    assert evs[0].label == "whipsaw" and evs[0].ret_s == pytest.approx(3.0)


def test_whipsaw_timeout_without_extension():
    # stays 1-2 ticks outside, never reaches E=8, first print after X=60 resolves
    evs = run((40, 20002.25), (70, 20002.5), (105, 20003.0))
    e = evs[0]
    assert e.label == "whipsaw" and e.ret_s is None
    assert e.t_resolve == e.t_break + 60 * S


def test_rearm_after_whipsaw_and_down_break():
    evs = run((40, 20002.25), (43, 20001.75),        # whipsaw up
              (50, 19999.75), (55, 19997.75))        # real down (ext 9 >= 8)
    assert [e.side for e in evs] == [1, -1]
    assert [e.label for e in evs] == ["whipsaw", "real"]
    assert evs[1].level == 20000.0


def test_no_new_breaks_after_watch_end_but_active_resolves():
    evs = run((925, 20002.25), (945, 20004.25))      # opens at 925, real at 945
    assert len(evs) == 1 and evs[0].label == "real"
    evs2 = run((935, 20002.25), (940, 20004.25))     # break attempt after 930s
    assert evs2 == []


def test_gap_through_resolves_and_opens_opposite():
    evs = run((40, 20002.25), (42, 19999.5), (44, 19997.5))
    assert [e.side for e in evs] == [1, -1]
    assert evs[0].label == "whipsaw"
    assert evs[1].t_break == T0 + 42 * S             # same print opens the down event
    assert evs[1].label == "real"


def test_degenerate_candle_excluded():
    a = tape((0, 20000.0), (10, 20000.25), (29, 20000.0), (40, 20001.0))
    evs, reason = events.detect_events(a, T0, k=1.0, x_s=60.0)
    assert evs is None and "degenerate" in reason


def test_non_monotonic_tape_raises():
    a = tape(*CANDLE, (50, 20002.25), (45, 20003.0))
    with pytest.raises(AssertionError):
        events.detect_events(a, T0, k=1.0, x_s=60.0)


def test_tape_end_with_active_event_is_whipsaw():
    evs = run((40, 20002.25), (41, 20002.5))
    assert evs[0].label == "whipsaw"
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `python3 -m pytest test_events.py -q` (from `research/`)
Expected: collection error — `events` module not found.

- [ ] **Step 3: Implement `research/events.py`**

```python
"""Break/whipsaw event engine. One forward pass, no lookahead by construction.

Frozen definitions live in docs/specs/2026-07-30-whipsaw-detector-research-design.md
section 4; test_events.py is the executable spec.
"""
import math
from dataclasses import dataclass

import numpy as np

from corpus import NET_S, TICK_SIZE

WATCH_END_S = 930          # 18:15:30, seconds after 18:00:00
CANDLE_S = 30


@dataclass
class Event:
    side: int
    t_break: int
    i_break: int
    break_px: float
    level: float
    label: str | None = None
    t_resolve: int | None = None
    max_ext: int = 0
    ret_s: float | None = None


def first_candle(a, t0):
    m = (a["ts"] >= t0) & (a["ts"] < t0 + CANDLE_S * NET_S)
    if not m.any():
        return None
    px = a["px"][m]
    h, l = float(px.max()), float(px.min())
    return h, l, round((h - l) / TICK_SIZE)


def _ext_ticks(px, side, level):
    d = (px - level) if side > 0 else (level - px)
    return max(0, round(d / TICK_SIZE))


def detect_events(a, t0, k, x_s, e_floor=8, r30_min=4):
    fc = first_candle(a, t0)
    if fc is None:
        return None, "no prints in first candle"
    h, l, r30 = fc
    if r30 < r30_min:
        return None, f"degenerate candle R30={r30}"
    e_ticks = max(math.ceil(k * r30), e_floor)
    watch_end = t0 + WATCH_END_S * NET_S
    x_net = int(x_s * NET_S)

    evs, active = [], None
    inside_prev = True
    prev_ts = -1
    for i in range(len(a)):
        ts, px = int(a["ts"][i]), float(a["px"][i])
        assert ts >= prev_ts, "tape not time-ordered"
        prev_ts = ts
        if ts < t0 + CANDLE_S * NET_S:
            continue
        up = px >= h + TICK_SIZE - 1e-9
        dn = px <= l - TICK_SIZE + 1e-9
        inside = not (up or dn)

        if active is not None:
            if ts > active.t_break + x_net:                      # (a) timeout
                active.label = "whipsaw"
                active.t_resolve = active.t_break + x_net
                active = None
            else:
                ext = _ext_ticks(px, active.side, active.level)  # (b) extension
                if ext > active.max_ext:
                    active.max_ext = ext
                if active.max_ext >= e_ticks:
                    active.label, active.t_resolve = "real", ts
                    active = None
                else:                                            # (c) re-entry
                    re_in = (px <= h - TICK_SIZE + 1e-9) if active.side > 0 \
                        else (px >= l + TICK_SIZE - 1e-9)
                    if re_in:
                        active.label, active.t_resolve = "whipsaw", ts
                        active.ret_s = (ts - active.t_break) / NET_S
                        opp = -active.side
                        active = None
                        beyond_opp = dn if opp < 0 else up
                        if beyond_opp and ts <= watch_end:       # gap-through
                            level = l if opp < 0 else h
                            active = Event(opp, ts, i, px, level)
                            active.max_ext = _ext_ticks(px, opp, level)
                            evs.append(active)

        if active is None and inside_prev and (up or dn) and ts <= watch_end:
            side = 1 if up else -1
            level = h if up else l
            active = Event(side, ts, i, px, level)
            active.max_ext = _ext_ticks(px, side, level)
            if active.max_ext >= e_ticks:                        # giant first print
                active.label, active.t_resolve = "real", ts
                evs.append(active)
                active = None
            else:
                evs.append(active)
        inside_prev = inside

    if active is not None:                                       # tape ended
        active.label, active.t_resolve = "whipsaw", prev_ts
    return evs, None
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `python3 -m pytest test_events.py test_corpus.py -q`
Expected: all pass. Debug the ENGINE, never edit a test to make it pass — the tests are the frozen spec (only exception: a test that contradicts spec §4, which must be flagged to the user instead).

- [ ] **Step 5: Commit**

```bash
git add research/events.py research/test_events.py
git commit -m "feat(research): break/whipsaw event engine with frozen label semantics"
```

---

### Task 3: Phase 0a descriptive report

**Files:**
- Create: `research/phase0a.py`
- Create (output, versioned): `docs/research/phase0a-report.md`
- Output (gitignored): `research/out/phase0a_stats.json`

**Interfaces:**
- Consumes: `corpus.sessions/load_session/prev_close`, `events.detect_events` (descriptive mode: `k=1e9, x_s=120.0, e_floor=10**6, r30_min=0` → every break resolves by re-entry or 120 s timeout; `max_ext`/`ret_s` become the descriptive stats), `events.first_candle`.
- Produces: `out/phase0a_stats.json` = `{"NQ": [row...], "MNQ": [row...]}`; row = `{"date","n_prints_watch","n_prints_1st_min","r30_ticks","gap_ticks","n_breaks","roll_spread_ticks","breaks":[{"side","t_s","max_ext_120","ret_s"}...]}`. `freeze_labels.py` (Task 4) reads exactly this file.

- [ ] **Step 1: Write `research/phase0a.py`**

```python
"""Phase 0a: label-free descriptives of the 18:00 ET reopen corpus.

Writes out/phase0a_stats.json (per-session rows, consumed by freeze_labels.py)
and ../docs/research/phase0a-report.md (versioned).
"""
import json
from datetime import datetime
from pathlib import Path

import numpy as np

import corpus
import events

DOCS = Path(__file__).parent.parent / "docs" / "research"
MIN_PRINTS_WATCH = 200          # thin-holiday exclusion, frozen (spec section 4)


def session_row(inst, d):
    a = corpus.load_session(inst, d)
    if a is None or not len(a):
        return None
    t0 = corpus.dt_to_net(datetime(d.year, d.month, d.day, 18, 0))
    watch = a[(a["ts"] >= t0 + 30 * corpus.NET_S) &
              (a["ts"] <= t0 + 930 * corpus.NET_S)]
    fc = events.first_candle(a, t0)
    if fc is None:
        return None
    h, l, r30 = fc
    pc = corpus.prev_close(inst, d)
    first_px = float(a["px"][0])
    evs, _ = events.detect_events(a, t0, k=1e9, x_s=120.0,
                                  e_floor=10**6, r30_min=0)
    evs = evs or []
    # Roll effective-spread estimator on watch-window prints
    dp = np.diff(watch["px"])
    roll = np.nan
    if len(dp) > 10:
        cov = float(np.cov(dp[1:], dp[:-1])[0, 1])
        if cov < 0:
            roll = 2 * np.sqrt(-cov) / corpus.TICK_SIZE
    m1 = a[(a["ts"] >= t0) & (a["ts"] < t0 + 60 * corpus.NET_S)]
    return {
        "date": d.isoformat(),
        "n_prints_watch": int(len(watch)),
        "n_prints_1st_min": int(len(m1)),
        "r30_ticks": int(r30),
        "gap_ticks": None if pc is None else round((first_px - pc) / corpus.TICK_SIZE),
        "n_breaks": len(evs),
        "roll_spread_ticks": None if np.isnan(roll) else round(roll, 2),
        "breaks": [{"side": e.side, "t_s": round((e.t_break -
                    (t0 + 30 * corpus.NET_S)) / corpus.NET_S, 1),
                    "max_ext_120": e.max_ext, "ret_s": e.ret_s} for e in evs],
    }


def q(vals, ps=(10, 25, 50, 75, 90)):
    v = [x for x in vals if x is not None and not (isinstance(x, float) and np.isnan(x))]
    if not v:
        return {}
    return {f"p{p}": round(float(np.percentile(v, p)), 2) for p in ps}


def main():
    stats, lines = {}, ["# Phase 0a — Descriptive Report (label-free)", ""]
    for inst in ("NQ", "MNQ"):
        rows = [r for d in sorted(corpus.sessions(inst))
                if (r := session_row(inst, d))]
        thin = [r for r in rows if r["n_prints_watch"] < MIN_PRINTS_WATCH]
        degen = [r for r in rows if r["r30_ticks"] < 4]
        usable = [r for r in rows if r["n_prints_watch"] >= MIN_PRINTS_WATCH
                  and r["r30_ticks"] >= 4]
        allbr = [b for r in usable for b in r["breaks"]]
        rets = [b["ret_s"] for b in allbr if b["ret_s"] is not None]
        stats[inst] = rows
        lines += [f"## {inst}", "",
                  f"- Sessions with a 1900-file front-month: **{len(rows)}**",
                  f"- Thin (<{MIN_PRINTS_WATCH} watch prints): {len(thin)}"
                  f" | degenerate (R30<4t): {len(degen)} | **usable: {len(usable)}**",
                  f"- R30 ticks: {q([r['r30_ticks'] for r in usable])}",
                  f"- Watch prints: {q([r['n_prints_watch'] for r in usable])}",
                  f"- First-minute prints: {q([r['n_prints_1st_min'] for r in usable])}",
                  f"- |gap| ticks: {q([abs(r['gap_ticks']) for r in usable if r['gap_ticks'] is not None])}",
                  f"- Roll spread ticks: {q([r['roll_spread_ticks'] for r in usable])}",
                  f"- Breaks/session: {q([r['n_breaks'] for r in usable])}"
                  f" (total {len(allbr)})",
                  f"- Break max-ext@120s ticks: {q([b['max_ext_120'] for b in allbr])}",
                  f"- Re-entry time s (n={len(rets)}): {q(rets)}",
                  f"- Breaks re-entering <=120s: "
                  f"{(100 * len(rets) / max(1, len(allbr))):.0f}%", ""]
    corpus.OUT.mkdir(exist_ok=True)
    (corpus.OUT / "phase0a_stats.json").write_text(json.dumps(stats))
    DOCS.mkdir(parents=True, exist_ok=True)
    (DOCS / "phase0a-report.md").write_text("\n".join(lines))
    print("\n".join(lines))


if __name__ == "__main__":
    main()
```

- [ ] **Step 2: Run it**

Run: `python3 phase0a.py` (from `research/`; first full parse of ~460 session files takes minutes — cached afterwards)
Expected: report printed and written to `docs/research/phase0a-report.md`. Sanity checks before proceeding: usable sessions ≥ 150 (NQ); breaks/session P50 ≥ 1; Roll spread P50 in a plausible 1–4 tick band; re-entry-time P90 a real number. Anything wildly off → STOP and investigate before Task 4 (the freeze depends on these numbers).

- [ ] **Step 3: Commit**

```bash
git add research/phase0a.py docs/research/phase0a-report.md
git commit -m "feat(research): phase 0a descriptive report over the reopen corpus"
```

---

### Task 4: Mechanical label freeze

**Files:**
- Create: `research/freeze_labels.py`
- Test: `research/test_freeze.py`
- Create (output, VERSIONED + immutable): `research/preregistration.json`

**Interfaces:**
- Consumes: `out/phase0a_stats.json` (Task 3 format).
- Produces: `research/preregistration.json` — the single source of frozen parameters for phase0b and all later phases:

```json
{
  "frozen_at": "<iso timestamp>",
  "break": "first print >=1 tick beyond H/L, inside->outside transition, window 18:00:30-18:15:30 ET",
  "k": 1.0, "e_floor_ticks": 8, "r30_min_ticks": 4, "min_prints_watch": 200,
  "x_timeout_s": "<computed>",
  "x_formula": "clamp(5*round(P90(NQ pooled re-entry times <=120s)/5), 30, 90)",
  "entry": "break print px + 3 ticks adverse (2 spread + 1 slip)",
  "commission_rt": {"NQ": 4.5, "MNQ": 1.5},
  "stop": "opposite candle extreme", "brackets": [1, 2], "tie_rule": "stop wins",
  "time_stop": "18:30:00 ET",
  "horizons_min": [1, 2, 5, 15],
  "g0": "PASS iff oracle net expectancy/trade > 0 in >=1 bracket view on NQ",
  "phase1_grid": {"hold_s": [0, 2, 5, 10, 20, 30], "ext_r30": [0, 0.25, 0.5, 1.0]},
  "phase1_split": "chronological 60/40",
  "phase1_metric": "expectancy per session in R, 1R bracket view"
}
```

- [ ] **Step 1: Write the failing test `research/test_freeze.py`**

```python
import json

import pytest

import freeze_labels


def test_x_formula():
    # np.percentile([10,20,30,40,88], 90) = 68.8 -> 5*round(13.76) = 70
    assert freeze_labels.x_from_rets([10, 20, 30, 40, 88]) == 70.0
    assert freeze_labels.x_from_rets([1, 2, 3]) == 30.0          # clamp low
    assert freeze_labels.x_from_rets([200.0] * 10) == 90.0       # clamp high
    assert freeze_labels.x_from_rets([]) == 60.0                 # fallback, flagged


def test_refuses_overwrite(tmp_path, monkeypatch):
    tgt = tmp_path / "preregistration.json"
    tgt.write_text("{}")
    monkeypatch.setattr(freeze_labels, "TARGET", tgt)
    with pytest.raises(SystemExit):
        freeze_labels.main()
```

- [ ] **Step 2: Run to verify failure**

Run: `python3 -m pytest test_freeze.py -q` — Expected: import error (module missing).

- [ ] **Step 3: Implement `research/freeze_labels.py`**

```python
"""Mechanical pre-registration freeze. Runs ONCE; refuses to overwrite."""
import json
import sys
from datetime import datetime, timezone
from pathlib import Path

import numpy as np

TARGET = Path(__file__).parent / "preregistration.json"
STATS = Path(__file__).parent / "out" / "phase0a_stats.json"


def x_from_rets(rets):
    """clamp(5*round(P90/5), 30, 90); 60 s documented fallback if no data."""
    if not rets:
        return 60.0
    p90 = float(np.percentile(rets, 90))
    return float(min(90.0, max(30.0, 5 * round(p90 / 5))))


def main():
    if TARGET.exists():
        sys.exit("preregistration.json already exists — labels are FROZEN. "
                 "Amendments require a new dated file plus a spec note.")
    stats = json.loads(STATS.read_text())
    rets = [b["ret_s"] for r in stats["NQ"] for b in r["breaks"]
            if b["ret_s"] is not None
            and r["n_prints_watch"] >= 200 and r["r30_ticks"] >= 4]
    reg = {
        "frozen_at": datetime.now(timezone.utc).isoformat(timespec="seconds"),
        "break": "first print >=1 tick beyond H/L, inside->outside transition, "
                 "window 18:00:30-18:15:30 ET",
        "k": 1.0, "e_floor_ticks": 8, "r30_min_ticks": 4, "min_prints_watch": 200,
        "x_timeout_s": x_from_rets(rets),
        "x_formula": "clamp(5*round(P90(NQ pooled re-entry times <=120s)/5), 30, 90)",
        "x_n_reentries": len(rets),
        "entry": "break print px + 3 ticks adverse (2 spread + 1 slip)",
        "commission_rt": {"NQ": 4.5, "MNQ": 1.5},
        "stop": "opposite candle extreme", "brackets": [1, 2],
        "tie_rule": "stop wins", "time_stop": "18:30:00 ET",
        "horizons_min": [1, 2, 5, 15],
        "g0": "PASS iff oracle net expectancy/trade > 0 in >=1 bracket view on NQ",
        "phase1_grid": {"hold_s": [0, 2, 5, 10, 20, 30],
                        "ext_r30": [0, 0.25, 0.5, 1.0]},
        "phase1_split": "chronological 60/40",
        "phase1_metric": "expectancy per session in R, 1R bracket view",
    }
    TARGET.write_text(json.dumps(reg, indent=2))
    print(f"FROZEN: X={reg['x_timeout_s']}s from {len(rets)} re-entries")


if __name__ == "__main__":
    main()
```

- [ ] **Step 4: Run tests, then run the freeze**

Run: `python3 -m pytest test_freeze.py -q` — Expected: 2 passed.
Run: `python3 freeze_labels.py` — Expected: prints the frozen X. Run it a second time — Expected: refuses with the FROZEN message.

- [ ] **Step 5: Commit (preregistration.json IS versioned)**

```bash
git add research/freeze_labels.py research/test_freeze.py research/preregistration.json
git commit -m "feat(research): freeze label pre-registration (mechanical X from 0a)"
```

---

### Task 5: Phase 0b — base rates + oracle gate

**Files:**
- Create: `research/phase0b.py`
- Create (output, versioned): `docs/research/phase0b-report.md`

**Interfaces:**
- Consumes: `research/preregistration.json`, `corpus.*`, `events.detect_events` (with frozen `k`, `x_timeout_s`, `e_floor_ticks`, `r30_min_ticks`), Event fields `i_break`, `break_px`, `side`, `label`.
- Produces: the G0 verdict. No downstream code consumes this module.

- [ ] **Step 1: Write `research/phase0b.py`**

```python
"""Phase 0b: frozen labels -> base rates, MFE/MAE, naive vs ORACLE expectancy.

G0 (kill gate): if even perfect whipsaw foresight cannot beat costs, archive.
"""
import json
from datetime import datetime
from pathlib import Path

import numpy as np

import corpus
import events

REG = json.loads((Path(__file__).parent / "preregistration.json").read_text())
DOCS = Path(__file__).parent.parent / "docs" / "research"


def sim_bracket(a, ev, h, l, rr, inst, t0):
    """Enter at break print +3 ticks adverse; bracket rr*R; stop wins ties."""
    tick = corpus.TICK_SIZE
    entry = ev.break_px + ev.side * 3 * tick
    stop = l if ev.side > 0 else h
    r_pts = abs(entry - stop)
    if r_pts < tick:
        return None
    target = entry + ev.side * rr * r_pts
    t_end = t0 + 1800 * corpus.NET_S                     # 18:30:00 time stop
    exit_px = float(a["px"][min(len(a) - 1, ev.i_break)])
    for j in range(ev.i_break + 1, len(a)):
        px = float(a["px"][j])
        if int(a["ts"][j]) > t_end:
            break
        exit_px = px
        if (ev.side > 0 and px <= stop) or (ev.side < 0 and px >= stop):
            exit_px = stop
            break
        if (ev.side > 0 and px >= target) or (ev.side < 0 and px <= target):
            exit_px = target
            break
    gross = (exit_px - entry) * ev.side * corpus.POINT_VALUE[inst]
    net = gross - corpus.COMMISSION_RT[inst]
    return {"net": net, "r": net / (r_pts * corpus.POINT_VALUE[inst])}


def mfe_mae(a, ev, minutes):
    out = {}
    t1 = ev.t_break
    for m in minutes:
        w = a[(a["ts"] > t1) & (a["ts"] <= t1 + m * 60 * corpus.NET_S)]["px"]
        if not len(w):
            out[m] = (0, 0)
            continue
        fav = (w - ev.break_px) * ev.side / corpus.TICK_SIZE
        out[m] = (round(float(fav.max()), 1), round(float(fav.min()), 1))
    return out


def stat_block(trades):
    if not trades:
        return "n=0"
    net = [t["net"] for t in trades]
    r = [t["r"] for t in trades]
    wins = sum(1 for x in net if x > 0)
    return (f"n={len(net)} | win%={100 * wins / len(net):.0f} | "
            f"E[$/trade]={np.mean(net):+.2f} (t={np.mean(net) / (np.std(net) / np.sqrt(len(net)) + 1e-9):.2f}) | "
            f"E[R]={np.mean(r):+.3f} | total=${np.sum(net):+.0f}")


def main():
    lines = [f"# Phase 0b — Base Rates & Oracle Gate", "",
             f"Frozen params: X={REG['x_timeout_s']}s, k={REG['k']}, "
             f"E_floor={REG['e_floor_ticks']}t (preregistration.json "
             f"{REG['frozen_at']})", ""]
    verdicts = {}
    for inst in ("NQ", "MNQ"):
        n_sess = n_events = 0
        whips, first_naive, first_oracle = [], {1: [], 2: []}, {1: [], 2: []}
        mfe_rows = []
        for d in sorted(corpus.sessions(inst)):
            a = corpus.load_session(inst, d)
            if a is None or len(a) < REG["min_prints_watch"]:
                continue
            t0 = corpus.dt_to_net(datetime(d.year, d.month, d.day, 18, 0))
            evs, reason = events.detect_events(
                a, t0, k=REG["k"], x_s=REG["x_timeout_s"],
                e_floor=REG["e_floor_ticks"], r30_min=REG["r30_min_ticks"])
            if evs is None or not evs:
                continue
            watch_n = int(((a["ts"] >= t0 + 30 * corpus.NET_S) &
                           (a["ts"] <= t0 + 930 * corpus.NET_S)).sum())
            if watch_n < REG["min_prints_watch"]:
                continue
            n_sess += 1
            n_events += len(evs)
            whips += [e.label == "whipsaw" for e in evs]
            fc = events.first_candle(a, t0)
            h, l, _ = fc
            for e in evs:
                mfe_rows.append(mfe_mae(a, e, REG["horizons_min"]))
            reals = [e for e in evs if e.label == "real"]
            for rr in (1, 2):
                t = sim_bracket(a, evs[0], h, l, rr, inst, t0)
                if t:
                    first_naive[rr].append(t)
                if reals:
                    t = sim_bracket(a, reals[0], h, l, rr, inst, t0)
                    if t:
                        first_oracle[rr].append(t)
        wr = 100 * np.mean(whips) if whips else float("nan")
        lines += [f"## {inst}", "",
                  f"- Usable sessions: {n_sess} | events: {n_events} "
                  f"({n_events / max(1, n_sess):.2f}/session) | "
                  f"**whipsaw rate: {wr:.0f}%**", ""]
        for m in REG["horizons_min"]:
            fav = [r[m][0] for r in mfe_rows]
            adv = [r[m][1] for r in mfe_rows]
            lines.append(f"- MFE/MAE @{m}min (ticks, all events): "
                         f"+{np.median(fav):.0f}/{np.median(adv):.0f} (median)")
        lines.append("")
        for rr in (1, 2):
            lines.append(f"- NAIVE  {rr}R bracket: {stat_block(first_naive[rr])}")
            lines.append(f"- ORACLE {rr}R bracket: {stat_block(first_oracle[rr])}")
        best = max(np.mean([t['net'] for t in first_oracle[rr]])
                   if first_oracle[rr] else -1e9 for rr in (1, 2))
        verdicts[inst] = best > 0
        lines += ["", f"**Best oracle E[$/trade]: {best:+.2f} -> "
                  f"{'PASS' if best > 0 else 'FAIL'}**", ""]
    g0 = verdicts.get("NQ", False)
    lines += ["## G0 verdict", "",
              f"**G0 {'PASS' if g0 else 'FAIL'}** (NQ primary; MNQ replication "
              f"{'PASS' if verdicts.get('MNQ') else 'FAIL'}). " +
              ("Proceed to Phase 1." if g0 else
               "Per spec section 7: archive the project honestly.")]
    DOCS.mkdir(parents=True, exist_ok=True)
    (DOCS / "phase0b-report.md").write_text("\n".join(lines))
    print("\n".join(lines))


if __name__ == "__main__":
    main()
```

- [ ] **Step 2: Run it**

Run: `python3 phase0b.py` (from `research/`)
Expected: report with NQ + MNQ blocks and an explicit **G0 PASS/FAIL** line. Cross-checks before trusting: whipsaw rate should be strictly between 20% and 90% (outside → suspect labels); oracle ≥ naive in E[$/trade] by construction (oracle is a subset selection — if naive beats oracle, there is a bug); n oracle trades ≤ n naive trades.

- [ ] **Step 3: Commit**

```bash
git add research/phase0b.py docs/research/phase0b-report.md
git commit -m "feat(research): phase 0b base rates + oracle gate (G0 verdict)"
```

---

## Self-review checklist (done at plan-writing time)

- Spec coverage: §3 data/corpus → Task 1; §4 taxonomy 0a/freeze/0b → Tasks 3/4/5; §8 pipeline validation → Tasks 2 tests + anti-lookahead assert; §7 G0 → Task 5. Phase 1/2 are OUT of this plan (next plan, gated on G0).
- The k=1.0 fix-up-front amendment is flagged in Global Constraints and must be echoed in the spec on first commit that uses it.
- Type consistency: `Event.i_break/break_px` produced in Task 2, consumed in Task 5; `phase0a_stats.json` schema produced Task 3, consumed Task 4 (`n_prints_watch`, `r30_ticks`, `breaks[].ret_s`).
