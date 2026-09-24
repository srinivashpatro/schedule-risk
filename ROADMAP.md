# Roadmap

The source of truth for progress (see CLAUDE.md). Work on one item at a time, and tick it only
when it is done and build-and-test passes. The order below is a draft: reorder as priorities change.

## Engine accuracy

These come first because "the CPM engine must match P6" is a non-negotiable.

- [x] `synth_5000.xer`: `sra verify` found 9 of 19,774 fields that differ from the stored values
      and printed float values as if they were dates. The engine was right: the fixture generator
      wrote total float to 6 significant digits. It now writes 4 decimals, the file is regenerated
      (seed 11, now in tools_make_golden.py), and the CLI prints float differences in hours.
- [x] `sra verify`: report "no P6 dates to compare" for files without P6-calculated dates
      (`hand_*`, `parallel_*`) instead of "differences found", so every fixture in testdata/ can pass.
      The verifier now has three outcomes (matches / differences / nothing to compare, exit 0), and a
      test runs it over every fixture in testdata/.
- [x] `sra verify`: a stored P6 date outside the calendar horizon stops the whole check with an
      error (C#: `outside calendar horizon`, exit 4; Python: traceback) instead of being reported.
      The horizon (ScheduleBuilder, ~400 days before the earliest date to 30 years after the
      latest) is built from start, constraint and data dates, not from the P6 finish and late dates
      that verify compares. Seen with a hand-edited late date (2099); a real export with a distant
      late date or constraint could hit it too. In the web app the file failed to open.
      Now reported as a difference in elapsed time, marked "outside the calendar range".
- [x] CPM: a project "must finish by" date (`scd_end_date`) outside the calendar horizon crashes
      the scheduling itself (`outside calendar horizon`): the backward pass starts from it
      (CpmEngine.cs:380) but ScheduleBuilder does not include it when building the horizon. On
      synth_200 (data date 2026-05-04), must-finish-by 2100, 2020 or 2015 fails `sra cpm` and the
      web app cannot open the file; 2045 works. Adding the date to the horizon is not enough on its
      own: with large negative float, late dates computed backwards can reach further back than the
      fixed 400-day margin. Scheduling code, so it must keep `sra verify` passing on every fixture.
      Fixed: the horizon now includes the must-finish-by and, when one is set, starts early enough
      for any finish before the horizon end. This also fixed Monte Carlo runs whose iterations
      overran an in-horizon must-finish-by (they failed with "work before calendar horizon").
- [x] Confirm which PROJECT column holds P6's "Must Finish By". The engine reads `scd_end_date`; it
      may be `plan_end_date`, with `scd_end_date` being P6's calculated Scheduled Finish, which P6
      writes into every scheduled export. If so, real files anchor every backward pass to P6's
      deterministic finish, including each Monte Carlo iteration, which can inflate the criticality
      index in iterations that finish later. Waiting on a real P6 export with Must Finish By set.
      Confirmed from a real export: `plan_end_date`. The engine now reads it and ignores
      `scd_end_date`. On synth_500 with `scd_end_date` at its deterministic finish (as in a real
      export), activities critical in at least half the iterations fell from 357 to 6.

## Scheduling features not yet supported

Each is flagged by `sra validate` where relevant.

- [ ] ALAP (as late as possible) constraints. P6 moves an ALAP activity later into its free float
      without delaying its successors. Still to confirm from P6: whether a chain of ALAP activities
      all move late, whether an open-ended one moves to the project early finish or the Must Finish
      By, and whether total float falls by the delay. Waiting on two P6 exports of a toy project,
      which become verify fixtures in testdata/ (toy data only; testdata/ is public):
      1. Import `testdata/hand_24h_lag.xer` into P6, or rebuild it: 5x8 calendar (08-12, 13-17,
         1 Jan 2026 holiday), data date Mon 2026-01-05 08:00; S start milestone, A 5d, B 3d, C 10d,
         D 2d, M finish milestone; S->A FS, A->B FS +3d, A->C SS +2d, B->D FF +1d, C->M FS, D->M FS;
         options: lag calendar 24-Hour, retained logic, total float = finish float, critical if
         float <= 0.
      2. Add E: 1 day, FS from A, no successors.
      3. `p6_alap_1.xer`: primary constraint "As Late As Possible" on D and E; schedule (F9), export.
      4. `p6_alap_2.xer`: also ALAP on B, project Must Finish By 2026-01-30 17:00; schedule, export.
- [ ] Expected finish constraint
- [ ] "Make open-ended activities critical" project option
- [ ] P6 XML import
- [ ] MS Project import
- [ ] Resource levelling

## Shipped

- [x] v0.3: engine core (P6-rules CPM, calendars, constraints, risk model, Monte Carlo with
      Latin Hypercube, seed-reproducible), `sra` CLI, Blazor WebAssembly app on GitHub Pages
- [x] Interactive finish-date chart (hover and keyboard readout)
- [x] Modernist redesign of the browser app, with a sample project
