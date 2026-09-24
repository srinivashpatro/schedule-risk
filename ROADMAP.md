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

## Risk analysis method

- [x] Criticality with a Must Finish By. Each Monte Carlo iteration measured float against the
      project's Must Finish By when one was set, not against that iteration's own finish. In an
      iteration that finishes early, even its longest path has positive float, so often nothing
      counted as critical; in one that finishes late, the longest path and every path within the
      overrun of it have negative float and all counted. The criticality index (AACE RP 57R-09: how
      often an activity is on the critical path) then reflected the deadline as much as the logic,
      and cruciality inherited it. Worse, the risk model's `critical` filter used P6's float too, so
      a risk filtered to critical activities (as in the example model) hit none or hundreds of them
      and the finish forecast moved with the deadline.
      Fixed: iterations measure float to their own finish, and the `critical` filter picks the
      activities that drive the deterministic finish; the deterministic schedule (CPM dates, float,
      verify, validate, the activity grid) keeps P6's float against the Must Finish By. On synth_500
      with the example model (500 iterations, seed 1), a Must Finish By of 28-Jun-2030, 15-Mar-2029 or
      30-Jun-2028 had given 0, 364 or 389 activities with any criticality, a `critical` filter of 0,
      0 or 315 activities, and P80 18-May-2029, 18-May-2029 or 27-Jul-2029; all three now give the
      no-deadline results: 49 activities, 6 in the filter, P80 14-Jun-2029.

## Scheduling features not yet supported

Only ALAP is flagged by `sra validate` today (unsupported constraints); files using the others are
not detected yet.

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
- [ ] Expected finish dates. With the project option "Use Expected Finish Dates" on, P6 recalculates
      an activity's remaining duration at each schedule so it finishes on its expected finish date.
      The engine reads neither column and uses the stored remaining duration, so Monte Carlo
      iterations let these activities finish late where P6 would hold the date. Decision: follow
      P6 in every run, including each iteration; sampled uncertainty on these activities then has
      no effect, and `validate` lists them. Still to confirm from P6: the column names (TASK
      `expect_end_date`, PROJECT `sched_use_expect_end_flag`?), whether the duration is measured
      from the data date (in progress) or early start (not started), what happens when the expected
      finish is before the activity can start, and which activity types it applies to. Waiting on
      two P6 exports of a toy project, which become verify fixtures in testdata/ (toy data only):
      1. Start from `testdata/hand_24h_lag.xer` (see the ALAP item for how to rebuild it).
      2. Data date Wed 2026-01-07 08:00; A in progress, actual start Mon 2026-01-05 08:00.
      3. Expected finish dates: A Tue 2026-01-13 17:00, B Fri 2026-01-09 17:00 (before it can start),
         C Wed 2026-01-21 17:00 (later than its natural finish), D Tue 2026-01-13 17:00 (earlier).
      4. `p6_expfin_1.xer`: "Use Expected Finish Dates" on; schedule (F9), export.
      5. `p6_expfin_2.xer`: the same with the option off; schedule, export.
- [ ] "Make open-ended activities critical" project option. With it on, P6 sets the late finish of an
      activity without successors to its early finish, so it and the chain driving it get zero
      float. The engine ignores the option and uses the project finish (or Must Finish By), so float
      and criticality differ from P6's. Decision: follow P6 in every run, including each Monte Carlo
      iteration (these activities then show near 100% criticality); `validate` lists the open ends
      made critical by the setting. Still to confirm from P6: the column name (PROJECT
      `sched_open_critical_flag`?), how it combines with a Must Finish By, what counts as open-ended
      (only complete, LOE or WBS-summary successors?), and whether it applies to in-progress
      activities. Waiting on two P6 exports of a toy project, which become verify fixtures in
      testdata/ (toy data only):
      1. Start from `testdata/hand_24h_lag.xer` (see the ALAP item for how to rebuild it) and add E:
         1 day, FS from A, no successors.
      2. `p6_opencrit_1.xer`: option on; schedule (F9), export.
      3. `p6_opencrit_2.xer`: option on and project Must Finish By Fri 2026-01-16 17:00; schedule,
         export.
- [ ] P6 XML import. Read P6 XML ("PMXML", root `<APIBusinessObjects>`) as well as XER, in the CLI and
      the web app. Plan: translate the XML into the same XER tables and columns ScheduleBuilder
      already reads, so the engine, verify and simulation are unchanged and an XML and an XER of the
      same project give identical results. DTDs and external entities are refused; still nothing is
      uploaded. Enum strings, units and the calendar format are to be confirmed from a paired export,
      which becomes verify and parity fixtures in testdata/ (toy/synthetic data only):
      1. Import `testdata/synth_200.xer` into P6 and schedule (F9).
      2. Export that project as P6 XML (`p6xml_synth_200.xml`) and as XER (`p6xml_synth_200.xer`).
      3. Optional: the same pair from `testdata/hand_holiday.xer`, for calendar holidays.
- [ ] MS Project import. Decisions: MS Project XML (MSPDI, File > Save As > XML) only, no `.mpp`; and
      MS Project data is scheduled with our P6 engine, not an emulation of MS Project's rules.
      MSPDI is translated into the same XER tables ScheduleBuilder reads, reusing the P6 XML item's
      format detection, safe XML settings and calendar writer, so it comes after that item. Mapping:
      status date -> data date; outline -> WBS; summary tasks -> WBS nodes (their links passed to
      subtasks and flagged); constraints SNET/SNLT/FNET/FNLT -> on-or-after/on-or-before, MSO/MFO ->
      mandatory start/finish, ALAP -> CS_ALAP (flagged until the ALAP item lands); deadline -> finish
      on or before; percentage lags -> hours of the predecessor's duration; total slack -> smallest
      float. `validate` lists what has no exact P6 equivalent (summary-task links, elapsed lags,
      manually scheduled tasks, scheduling from finish, constraint dates not honoured), and `sra
      verify` compares with MS Project's own stored dates. Still to confirm from MS Project: the lag
      calendar, how in-progress work is placed relative to the status date, and the units in the file.
      Waiting on a toy MS Project file saved as XML (`msp_toy.xml`, toy data only; testdata/ is public):
      1. Calendar 5 days x 8 hours (08-12, 13-17), 1 Jan 2026 holiday, plus one recurring holiday
         (e.g. first Monday of each month).
      2. Tasks and links as `hand_24h_lag` (see the ALAP item): S, A 5d, B 3d, C 10d, D 2d, M
         milestone; S->A FS, A->B FS +3d, A->C SS +2d, B->D FF +1d, C->M FS, D->M FS.
      3. Put A-D under a summary task and link another task to the summary.
      4. One task with each constraint type (SNET, SNLT, FNET, FNLT, MSO, MFO, ALAP), a 50% lag, a
         2-elapsed-day lag ("2ed"), a deadline, and one manually scheduled task.
      5. Mark A in progress with a status date of Wed 2026-01-07; save as XML.
- [ ] Resource levelling: detect and explain, not model. Decision: the engine and the Monte Carlo
      stay logic-only (common QSRA practice; P6's levelling heuristic cannot be verified exactly and
      levelling inside iterations is disputed). Read resource assignments; recognise a levelled
      file (P6's option flag, and stored dates later than the logic allows on activities with
      resources); `validate` gets a `resource_levelled` check listing delayed activities and their
      delay; `sra verify` marks those differences "levelling delay" instead of engine mismatches;
      the web app, report and README say the risk analysis uses logic only. Still to confirm from
      P6: which stored dates levelling changes (early, remaining early or both), the PROJECT column
      for "level resources during scheduling", and the resource table columns. Waiting on two P6
      exports, which become verify fixtures in testdata/ (toy data only):
      1. Start from `testdata/hand_24h_lag.xer` (see the ALAP item for how to rebuild it).
      2. Add one resource with a maximum of 1 unit, assigned to B and C (they overlap).
      3. `p6_level_1.xer`: level resources (with "level resources during scheduling" on); export.
      4. `p6_level_2.xer`: the same without levelling; schedule (F9), export.

## Shipped

- [x] v0.3: engine core (P6-rules CPM, calendars, constraints, risk model, Monte Carlo with
      Latin Hypercube, seed-reproducible), `sra` CLI, Blazor WebAssembly app on GitHub Pages
- [x] Interactive finish-date chart (hover and keyboard readout)
- [x] Modernist redesign of the browser app, with a sample project
