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
- [ ] `sra verify`: a stored P6 date outside the calendar horizon stops the whole check with an
      error (C#: `outside calendar horizon`, exit 4; Python: traceback) instead of being reported.
      The horizon (ScheduleBuilder, ~400 days before the earliest date to 30 years after the
      latest) is built from start, constraint and data dates, not from the P6 finish and late dates
      that verify compares. Seen with a hand-edited late date (2099); a real export with a distant
      late date or constraint could hit it too.

## Scheduling features not yet supported

Each is flagged by `sra validate` where relevant.

- [ ] ALAP (as late as possible) constraints
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
