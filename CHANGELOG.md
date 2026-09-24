# Changelog

## 0.4.0 - 2026-09-24

### Behaviour changes: re-run results for files with a Must Finish By

- **Must Finish By read from the right column.** P6's Must Finish By is `PROJECT.plan_end_date`.
  The engine used to read `scd_end_date`, which is P6's calculated scheduled finish and is present in
  every scheduled export. Float was then measured against P6's own finish date, which inflated
  criticality in the simulation.
- **A Must Finish By no longer changes the simulation.** Each iteration measured float against the
  deadline, and the risk model's `critical` filter used P6's float as well. A risk filtered to
  critical activities could hit none of them or hundreds, which moved the P80 finish by about a
  month either way. Now each iteration measures criticality to its own finish, and the `critical`
  filter picks the activities that drive the deterministic finish. The deterministic schedule (CPM
  dates, float, `verify`, `validate`, the activity grid) still uses P6's float against the Must
  Finish By.

### Added

- The chance of meeting the Must Finish By, and how far the chosen P-level finishes from it, when a
  file has one. It appears in a web app tile, as a line on the finish chart and in its hover readout,
  in the HTML report, in the summary JSON (`must_finish_by`, `prob_meet_must_finish_by`) and on the
  CLI's `simulate` line.
- An interactive finish-date chart in the browser app, with a hover and keyboard readout of the
  chance of finishing by each date.
- The Modernist redesign of the browser app, with a bundled sample project. The Archivo font is
  self-hosted, so the app still loads nothing from other sites.

### Fixed

- A Must Finish By outside the calendar range stopped the schedule from calculating, in the CLI and
  in the web app. Simulation iterations that overran an in-range Must Finish By failed too.
- `sra verify` stopped with an error on a P6 date outside the calendar range. It now reports that
  date as a difference.
- `sra verify` reported "differences found" for files without P6-calculated dates. It now reports
  "nothing to compare" and exits 0. Float differences are shown in hours.
- The `synth_5000` test fixture stored total float at too low a precision.

### Project

- `CLAUDE.md` (project rules) and `ROADMAP.md`, with plans for ALAP, expected finish dates,
  "make open-ended activities critical", resource levelling, P6 XML and MS Project import, and
  longest-path criticality.

## 0.3.0

- Engine core (P6-rules CPM, calendars, constraints, risk model, Monte Carlo with Latin Hypercube
  sampling, seed-reproducible), the `sra` CLI, and the Blazor WebAssembly browser app on GitHub
  Pages.
