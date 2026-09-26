# Changelog

## Unreleased

### Added

- Expand all and Collapse all for the Gantt chart's WBS bands, from buttons in the Gantt toolbar and from
  a right-click menu anywhere on the chart, including the timescale header. The menu also opens from the
  keyboard (context-menu key or Shift+F10) and is used with the arrow keys, Enter and Escape.

## 0.5.0 - 2026-09-26

### Added

- Duration statistics for each run: the project duration in working days of the project calendar, measured
  from the project start (actual starts included). Shown for the deterministic schedule, P50, P80 and the chosen
  confidence level, with the contingency at each (days and percent of the deterministic duration), and the
  minimum, maximum, mean, median, standard deviation, skewness and kurtosis (as Excel's SKEW and KURT). Also the
  run's start time, run time, iterations and seed, and the project, data date and activity and risk counts. They
  appear in a "Duration statistics" section in the web app's Results and in the HTML report, as `duration` and
  `model` blocks in the summary JSON, and on the CLI's `simulate` output. Existing JSON fields are unchanged.
- A new landing page for the browser app, themed on industrial project planning: a plant drawn in
  elevation with its schedule under the ground line, the three steps as cards with small previews, and a
  closing band for the privacy promise. Once a file is open the page shows a slim file strip instead. The
  loading screen draws a small schedule while the engine downloads. Everything is inline SVG and the
  design system's colours, in light and dark mode; nothing is loaded from other sites, and a test now
  checks that.
- A Gantt chart of the schedule, laid out like Primavera P6's: an activity table (ID, name, original and
  remaining duration, start, finish with "A" for actual dates, total float) beside bars on a two-tier
  timescale with zoom, grouped by WBS in P6's order with collapsible bands and summary bars. Bars use P6's
  colours: actual work blue, remaining work green, critical work red, with milestones, float lines and the
  data date. It is the default view of the parsed activities; the table is one click away, and both share
  the search and critical-only filters. The WBS order comes from `PROJWBS.seq_num`, which is now read.

## 0.4.0 - 2026-09-24

Still online at <https://srinivashpatro.github.io/schedule-risk/v0.4/> (tag `v0.4.0`).

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

Still online at <https://srinivashpatro.github.io/schedule-risk/v0.3/> (branch `release/0.3`).

- Engine core (P6-rules CPM, calendars, constraints, risk model, Monte Carlo with Latin Hypercube
  sampling, seed-reproducible), the `sra` CLI, and the Blazor WebAssembly browser app on GitHub
  Pages.
