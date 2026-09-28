# Changelog

## Unreleased

### Added

- A qualitative risk register: a probability-severity matrix
  (5 x 5 by default, probability A to E, severity I to V on schedule, cost, quality, health and safety,
  environment and regulatory, rated Red, Amber or Green from a grid), risks with three assessments
  (inherent, current, target), responses, owners and actions, saved as a JSON file
  (docs/RISK_REGISTER.md). It does not change the simulation.
- Step 01 Setup in the app: the risk matrix drawn with its Red, Amber and Green cells (click a cell to change
  it), the rating guidance, the probability bands, the severity levels and areas, the categories, which
  risks are promoted to the model and the cost of delay per day. Open and save the register as a file on
  your device.
- Step 02 Identify: propose risks as cause, event and effect (the app writes them as one sentence), approve,
  reject, close or reopen them, and find them in the register by search, status, category and type.
  Approval needs a title and an event; approved risks are closed, not deleted, so the register keeps
  its history.
- Step 03 Assess: place each approved risk on the matrix before any controls, with today's controls and
  after the response, choosing from the matrix's guidance; the heat map shows the risks per cell with
  Red, Amber and Green counts and draws a risk's movement as numbered arrows. Record the response, owner,
  cost and actions per risk, and see every action across the register with overdue ones marked.
- Promote: approved Red and Amber risks with a schedule severity of II or more become risks in the model.
  The probability is the middle of the probability band and the impact a range of working days from the
  schedule band times the planned duration (project start to deterministic finish); the current
  assessment is before mitigation and the target after it. Values can be set by hand, and each risk is
  mapped to the activities it would delay. Promoting again updates them. Also `sra promote` on the
  command line (docs/RISK_REGISTER.md).
- A second copy of the browser app on Cloudflare Pages behind Cloudflare Access, for invited email addresses
  only: they sign in with a one-time PIN sent by email, and a sign-in lasts 48 hours. The workflow deploys it
  once the repository has the Cloudflare settings; the GitHub Pages copy stays public. The app has no sign-in
  code and still sends nothing anywhere. Set-up steps and privacy notes are in docs/HOSTING.md.

- Results: the cost-benefit of each risk response. For each risk with a response, a run with only that risk
  mitigated on the same seed and iterations as the pre-mitigation run gives the working days it saves at P80,
  at the chosen level and on average; the P80 days are valued at the cost of delay from Setup and set against
  the response cost from Assess (value, net benefit, benefit / cost). In the reports too, and from the command
  line with `sra simulate --cost-benefit [--register register.json]`.

- Step 07 Review: the reports (HTML, PDF, Word, PowerPoint) carry the risk register: heat maps now and after
  the responses, the approved risks with their owners, cells and responses, and the actions with overdue
  ones first. The CSV export adds `register.csv`. The Review page lists what the report will hold.

### Changed

- Results: the risk ranking is drawn as a tornado, each risk's rank correlation with the finish before and
  after mitigation side by side (bars to the left shorten the finish), in the app and in every report.
- Step 05 Model: the model's discrete risks come from the risk register. Every run promotes the register
  first, and step 05 lists the risks read-only, with links to Assess and Promote. A model whose risks were
  typed into it offers "Move to the register": each becomes an approved register risk with the same
  numbers and activities, so the results do not change (also `sra promote --import`). `sra simulate
  --register` promotes before simulating. The sample project's risks now start in its register.
- Step 04 Schedule check: the schedule health checks are now P6 Professional's Check Schedule parameters
  (17, in the dialog's three tabs) and the DCMA 14-Point Assessment side by side, each with its target,
  result, PASS / FAIL / N/A and the activities or relationships it flagged (click an ID to show it in the
  Gantt). Two P6 operators that read against their description (Positive Lags, Relationship Types) are
  reported as configured and in the conventional reading. The Critical Path Test and CPLI use the DCMA
  method on the recalculated schedule. The same checks are in every report and `sra validate`, which
  writes them as JSON (`--json`) in the owner's health-check format (docs/SCHEDULE_CHECK.md). They replace
  the 12 checks of earlier versions.
- The app is laid out as the seven-step risk workflow: 01 Setup, 02 Identify, 03 Assess, Promote,
  04 Schedule check, 05 Model, 06 Results, 07 Review, listed in a column beside the page. The schedule,
  model and results are steps 04-06, and the report formats are now a page, step 07, instead of a dialog.
- When the sample project or the report fonts cannot be fetched from the site (a dropped connection, or an
  ended sign-in on the Cloudflare copy), the message now says to open the app in a new tab, sign in if asked,
  and try again, instead of showing the browser's own error. The tab keeps its schedule and results.

## 0.6.0 - 2026-09-27

### Added

- Expand all and Collapse all for the Gantt chart's WBS bands, from buttons in the Gantt toolbar and from
  a right-click menu anywhere on the chart, including the timescale header. The menu also opens from the
  keyboard (context-menu key or Shift+F10) and is used with the arrow keys, Enter and Escape.
- A searchable activity picker in the risk model, where a row applies to activities (uncertainty, risks,
  drivers, correlation): it lists each activity's ID and name, grouped by WBS as in the Gantt, and filters
  on either as you type. Pick several with the mouse or keyboard. Model files are unchanged: they still
  hold a list of activity IDs.
- A Summary panel at the top of the Results with the key outputs of a run in one place: the deterministic
  finish and the chance of meeting it (and the Must Finish By), P50, P80 and the chosen confidence level with
  their contingency, mean and median (each a date and a duration in working days), the spread of the duration
  (minimum, maximum, standard deviation, skewness, kurtosis), the top five risk drivers, and the critical
  (criticality index 50% or more) and near-critical (10% to 49%) activities. Pre- and post-mitigation values
  sit side by side. The HTML report opens with the same Summary.
- Report exports as PDF (A4), Word (A4) and PowerPoint (16:9), next to the HTML report and CSVs. They carry the
  same summary, charts and tables as the Results, in the app's look with the Archivo font embedded, the red mark
  and name on every page or slide, and the generated line and page numbers. Charts are pictures of the app's own
  charts; tables stay editable in Word and PowerPoint. Everything is written in the browser; the only files it
  fetches are the report fonts, from the app's own site, the first time.
- "What the results mean" under the Summary: plain-language notes that say what each result means for the
  finish date (how likely the current finish is, what the P50 and P80 promise, what the mean, median,
  skewness and kurtosis say, the spread, what mitigation gains, what drives the finish and how far to trust
  the run), and a glossary of the terms. They are written from fixed rules, not by an AI model, and nothing
  leaves the browser. Every report (HTML, PDF, Word, PowerPoint) carries them after its Summary. The rules
  are in docs/READING_RESULTS.md.

### Changed

- The app is now called Project Risk Analysis: in the browser tab, the top bar, the loading screen, the landing
  page ("Project risk analysis for Primavera P6") and the reports' titles and footers. The `sra` command, the
  code's names and the web address stay the same.
- The Summary replaces the headline tiles and the Duration statistics tables in the Results and the HTML
  report, so each figure is shown once. The run details (start, run time, iterations, seed) move from under the
  chart to the Summary's heading.
- The note under the Summary (working days, contingency, the critical thresholds, how drivers are ranked)
  moves into the new glossary.

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
