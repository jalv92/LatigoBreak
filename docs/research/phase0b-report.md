# Phase 0b — Base Rates & Oracle Gate

Frozen params: X=30.0s, k=1.0, E_floor=8t (preregistration.json 2026-07-30T23:08:57+00:00)

## NQ

- Usable sessions: 203 | events: 2918 (14.37/session) | **whipsaw rate: 99%**

- MFE/MAE @1min (ticks, all events): +18/-20 (median)
- MFE/MAE @2min (ticks, all events): +25/-26 (median)
- MFE/MAE @5min (ticks, all events): +36/-38 (median)
- MFE/MAE @15min (ticks, all events): +50/-65 (median)

- NAIVE  1R bracket: n=203 | win%=43 | E[$/trade]=-110.34 (t=-2.86) | E[R]=-0.156 | total=$-22398
- ORACLE 1R bracket: n=19 | win%=79 | E[$/trade]=+236.29 (t=2.38) | E[R]=+0.528 | total=$+4490
- NAIVE  2R bracket: n=203 | win%=35 | E[$/trade]=-124.77 (t=-2.81) | E[R]=-0.207 | total=$-25328
- ORACLE 2R bracket: n=19 | win%=68 | E[$/trade]=+280.76 (t=1.88) | E[R]=+0.607 | total=$+5334

**Best oracle E[$/trade]: +280.76 -> PASS**

## MNQ

- Usable sessions: 67 | events: 2186 (32.63/session) | **whipsaw rate: 100%**

- MFE/MAE @1min (ticks, all events): +23/-22 (median)
- MFE/MAE @2min (ticks, all events): +34/-28 (median)
- MFE/MAE @5min (ticks, all events): +58/-40 (median)
- MFE/MAE @15min (ticks, all events): +67/-68 (median)

- NAIVE  1R bracket: n=67 | win%=60 | E[$/trade]=+10.75 (t=1.42) | E[R]=+0.163 | total=$+720
- ORACLE 1R bracket: n=5 | win%=100 | E[$/trade]=+39.10 (t=11.00) | E[R]=+0.961 | total=$+196
- NAIVE  2R bracket: n=67 | win%=45 | E[$/trade]=+2.71 (t=0.33) | E[R]=-0.004 | total=$+182
- ORACLE 2R bracket: n=5 | win%=60 | E[$/trade]=+41.60 (t=1.52) | E[R]=+0.761 | total=$+208

**Best oracle E[$/trade]: +41.60 -> PASS**

## G0 verdict

**G0 PASS** (NQ primary; MNQ replication PASS). Proceed to Phase 1.