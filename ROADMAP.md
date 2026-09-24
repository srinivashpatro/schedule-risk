# Roadmap

The source of truth for progress (see CLAUDE.md). Work on one item at a time, and tick it only
when it is done and build-and-test passes. The order below is a draft: reorder as priorities change.

## Engine accuracy

These come first because "the CPM engine must match P6" is a non-negotiable.

- [ ] `synth_5000.xer`: `sra verify` finds 9 of 19,774 fields that differ from the stored values
      (total float about 19 minutes off on 8 activities, a sub-minute rounding difference on one),
      and prints float values as if they were dates.
- [ ] `sra verify`: report "no P6 dates to compare" for files without P6-calculated dates
      (`hand_*`, `parallel_*`) instead of "differences found", so every fixture in testdata/ can pass.

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
