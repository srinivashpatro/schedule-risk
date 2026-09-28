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
- [x] Must Finish By in the results. A Must Finish By no longer changes the simulation, so show it
      where it belongs in a risk analysis: the deadline, the chance of finishing by it (share of
      iterations that finish on or before it) and how far the P80 finish is from it, next to the
      deterministic date and its chance. A Must Finish By at 00:00 means by the end of the previous
      working day, as in P6, so instants are compared. Shown only when a Must Finish By is set.
      Done: a web KPI tile and HTML report tile, a dotted line and label on the finish chart (named
      at the chart's edge when it lies far outside the finishes) plus a note in the hover readout,
      `must_finish_by` and `prob_meet_must_finish_by` in the summary JSON (C# and Python), and the
      figure on the CLI's simulate line. No CSV change: `iterations.csv` already has each finish.
      synth_500 with a Must Finish By of 15-Mar-2029: 50% before mitigation, 83% after. Files
      without one give the same results and JSON as before.
- [x] Duration statistics in the results (for 0.5.0), after the summary table of a commercial QSRA tool:
      the project duration in working days of the project calendar from the project start (earliest
      start in the deterministic schedule, actual starts included; the same in every iteration),
      deterministic and at each P-level; contingency at P50 and P80 (minus deterministic, in days and as
      a percentage of it); minimum, maximum, mean, median, standard deviation, skewness and excess
      kurtosis (sample formulas, as Excel's SKEW and KURT); the run's start time, run time, iterations
      and seed; project, data date, activity and risk counts. Done: a "Duration statistics" section in
      the web app (Pre and Post columns, plus the chosen P-level) and the HTML report, `duration` and
      `model` blocks in the summary JSON (C# and Python, identical on synth_500), and a duration line in
      the CLI's `simulate` output. Durations are rounded to 6 decimals because, as whole minutes over
      minutes-per-day, they often end in a 5 at the fifth decimal, where C# and Python round differently.
      The start time stays out of the JSON so that a seed still gives an identical file.
- [ ] Critical by longest path. Criticality is float-based: an activity counts as critical when its
      total float is at or below the critical threshold. Two things still distort that in the
      Monte Carlo: activity constraints (for example finish on or before) give negative float in
      iterations that overrun them even when the activity does not drive the finish, and
      differences between calendars can leave float above zero on the path that does drive it.
      P6's alternative is the project option "Define critical activities as Longest Path" (PROJECT
      `critical_path_type`, `CT_DrivPath`?), which marks the chain of driving relationships back
      from the project finish; P6 may also store each activity's longest-path flag (TASK
      `driving_path_flag`?), which `sra verify` could compare. Column names to be confirmed from
      a P6 export. To plan: honour the option in the deterministic schedule (activity grid, `cpm`
      CSV, `validate`), and decide whether the Monte Carlo's criticality index uses the longest
      path in every iteration, always or only when the file sets the option.

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

## Browser app

- [x] Landing page themed on industrial project planning. Step 01 before a file is open: a blueprint
      grid behind the hero, a plant drawn in elevation (tower crane, steel frame, process columns, pipe
      rack, tanks) with its schedule under the ground line (done work in ink up to a data date, the
      critical chain in the accent), three step cards with small previews, and a closing poster band
      for the privacy promise. Once a file is open the hero shrinks to a slim strip, so the schedule
      gets the room. The loading screen draws a small schedule while the engine downloads. All inline
      SVG and CSS from the design system's tokens (light and dark), no image files, motion off with
      prefers-reduced-motion; `WebAssetsTests` fails if any page, style or script refers to another
      site.
- [x] P6-style Gantt chart in step 01 after a file loads, the default view beside the activity table
      (a Gantt | Table switch). Activity table (ID, name, original and remaining duration, start and
      finish as P6 shows them with "A" for actual dates, total float) beside bars on a two-tier timescale
      (Week / Day to Decade / Year) with zoom about the middle of the view and fit; grouped by WBS in P6's
      order (`PROJWBS.seq_num`, now read by ScheduleBuilder, then file order) with bands shaded by
      level, summary bars and collapse, or flat; activities by start then ID; search and critical-only
      filters shared with the table; P6's bar colours (actual blue, remaining green, critical red, as
      chart tokens in light and dark), milestones as diamonds, float lines from early to late finish
      (none for negative float, shown red in the table), the data date, a CSS tooltip on hover or
      focus, and click or Enter to scroll a row's bar into view. Layout in Core (`GanttLayout`, 20
      tests); the view renders only the rows in view: synth_5000's Gantt appears 350-410 ms after the
      schedule is calculated in the browser. To confirm from P6: whether a WBS element's own activities
      come before its child WBS bands (they do here) and the week start day (Monday here). Follow-ups:
      relationship lines (need an overlay across virtualised rows), baselines, the Gantt in the HTML
      report.

- [x] Expand all / Collapse all for the Gantt's WBS bands, from buttons in the Gantt toolbar and from
      a right-click menu anywhere on the chart (table, bars and the timescale header), which replaces
      the browser's own menu there. The menu works from the keyboard (context-menu key or Shift+F10,
      arrow keys, Enter, Escape) and its items are disabled while grouping by WBS is off. Collapse all
      collapses every band, as P6 does, so only the top bands remain.
- [x] Risk model: a searchable activity picker where a row applies to "Activities" (uncertainty,
      risks, drivers, correlation), listing ID and name grouped by WBS in the Gantt's order and
      filtering on either as you type; completed activities, milestones and summaries are tagged.
      Several activities can be picked (chips; Backspace removes the last), by mouse or keyboard
      (arrows, Enter, Escape), and an ID that is not in the schedule can still be typed. The model file
      keeps its list of IDs, so saved models and the CLI are unchanged. The list floats over the page
      so the scrolling tables do not clip it. `GanttLayout.Filter` filters one built layout as the
      user types: on synth_5000 the list follows typing in about 60-110 ms in the browser.
- [x] Results: one Summary panel at the top of Results with the key outputs of a run. Finish: the
      deterministic finish and the chance of meeting it (and the Must Finish By, when the project has
      one), P50, P80 and the level chosen on the chart (highlighted) with their contingency, mean and
      median, each a date with its duration. Spread of the duration: minimum, maximum, standard
      deviation, skewness, excess kurtosis. Pre- and post-mitigation side by side. Top five risk drivers
      (risks and drivers together, by rank correlation with the finish; activity durations when the
      model has neither), and the critical (criticality index 50% or more) and near-critical (10% to
      49%) activities with their counts and the top five of each; the thresholds apply to the whole
      percent shown, so an activity that reads 50% is never near-critical. The run and model lines
      (start, run time, iterations, seed; project, data date, counts) head and close the panel. It
      replaces the headline tiles and the Duration statistics tables, and the chart no longer repeats
      the iteration count and seed. Built by `ResultsSummary` in Core (replacing `StatisticsTable`), so
      the HTML report shows the same Summary in the same two columns; `SimulationSummary` gains the
      median finish date (not in the JSON). 11 tests.
- [x] Report exports as PDF, PowerPoint and Word, in the app's theme, with the charts and tables shown
      on screen. Written in the browser by Core's `Reporting/Export` (no dependencies): one
      `ReportContent` (the Summary, finish date histogram and cumulative curve, confidence levels, risk
      ranking or duration sensitivity, criticality index, risk drivers, driving activities, milestones,
      health checks, engine check) laid out three ways. PDF: A4, Archivo embedded as subset CID fonts
      with ToUnicode maps (text copies and searches), tables that continue with their heads, short
      tables and headings kept with what follows. Word: A4 with styles, header (mark and name) and
      footer (generated line, page X of Y), tables that repeat their heads, Archivo embedded as
      obfuscated fonts. PowerPoint: 16:9 on the app's warm grey, one slide per chart, tables split
      evenly over slides, the Summary as two slides with text sized to fit, Archivo embedded as EOT
      font data as LibreOffice writes it. Charts are the app's SVG charts (and its bar lists) made
      standalone with the palette and Archivo subsets inside, drawn to PNG by the browser (canvas) at
      2x; PDF takes them as zlib RGB. Fonts: static 400/600/800 instances of the Archivo variable font
      with Latin Extended (`tools/make_doc_fonts.py`), fetched from the site when first exporting.
      Checked: qpdf and poppler on the PDFs, the Open XML SDK validator in the tests (0 errors), Word
      and PowerPoint files rendered by LibreOffice (Word's embedded Archivo used). 14 tests. To
      confirm in PowerPoint itself: that it takes the embedded fonts (LibreOffice 24.2 does not read
      PPTX fonts, so the slides were checked with its fallback font).
- [x] Rename the app to "Project Risk Analysis" wherever users see it: the browser tab and home-screen
      title, the top bar, the loading screen, the landing headline ("Project risk analysis for
      Primavera P6"), and the reports' titles, headings and footers (HTML, PDF, Word, PowerPoint, and
      the documents' properties), all from `Brand.Name` in Core; the README title. The app has no page
      footer of its own. Code and project names (`ScheduleRisk.*`), the `sra` command and the web
      address (/schedule-risk/) stay. Up to 1100px wide the name stacks over two lines, so the top bar
      keeps the rows it had. Tests check the name in the page, the top bar and the reports.
- [x] Results: "What the results mean", plain-language notes on each Summary figure so that anyone
      can read the results: how likely the current finish (and the Must Finish By) is, what the P50 and
      P80 promise and the contingency they need, the chosen level when it is neither, the mean and
      median and what their gap says, skewness and kurtosis in words (including results that fall
      into two groups, such as a risk that happens or not), the P10 to P90 spread, what mitigation
      gains, what drives the finish, the critical activities, and how far to trust the run. Then a
      glossary of the terms, which replaces the note under the Summary. Built by `ResultsNarrative` in
      Core from fixed templates and named thresholds (no AI model, no network), documented in
      docs/READING_RESULTS.md; sentences of at most 25 words. Shown under the Summary on the Results
      page (glossary folded) and right after the Summary in the HTML, PDF and Word reports, and as two
      slides in PowerPoint. 21 tests (52 cases): exact wording on fixed seeds, each threshold's
      boundaries, and the edge cases (no spread, no risks, lumpy results, a deadline, shorter
      durations).

## Risk workflow (design: seven steps from register to report)

The Claude design `design/workflow/steps.png` turns the app's three steps (Schedule, Risk model,
Results) into a guided risk process: 01 Setup, 02 Identify, 03 Assess, then Promote (a bridge, not a
numbered step), 04 Schedule check, 05 Model, 06 Results, 07 Review. Steps 01-03 and Promote are new: a
qualitative risk register (probability-impact matrix) whose approved risks above a threshold become
the quantified risks the Monte Carlo already uses. Steps 04-07 reorganise what exists, plus a tornado
and a mitigation cost-benefit in Results. Everything stays in the browser; the register is saved and
opened as a local JSON file like the risk model. Register logic and the promotion rules live in Core
(UI-agnostic, tested, shared with the CLI); the scheduling engine and simulation are not changed, so
seeds give the same results as today for the same risk model.

To decide before the first item (the plan assumes the choice in brackets):
- Where the XER is opened: on the landing page before step 01, as today, with steps 02-03 usable
  without a schedule and activities picked at Promote [assumed]; or as part of Setup.
- Where the Gantt and activity table go: step 04 Schedule check, with the engine check and health
  checks [assumed].
- Default matrix (decided from the user's reference, an owner's corporate risk guideline; its
  wording is proprietary, so only the structure and numbers below go in the repo and the default
  wording is our own):
  - Probability A-E: A Remote 0-5%, B Unlikely 5-25%, C Occasional 25-50%, D Likely 50-70%,
    E Most likely 70-95%. At or near 100% the risk is a certainty: Setup warns and suggests putting
    it in the base schedule instead of the register.
  - Severity I-V (Insignificant, Minor, Significant, Major, Very high), one scale per dimension:
    schedule as % of the planned project duration (I: none, absorbed by float; II <3%; III 3-6%;
    IV 6-10%; V >10%), cost as % of the approved control budget (<=0.25, 0.25-0.5, 0.5-1, 1-2, >2%),
    and descriptive scales for quality/performance, health and safety, environment and regulatory.
    A risk is scored on each dimension that applies and its severity is the worst of them (confirmed).
  - Cells are named severity.probability (IV.D) and rated by a lookup table, not a score (rows E to A,
    columns I to V): E G A A R R; D G G A A R; C G G A A A; B G G G A A; A G G G A A.
    Red: intolerable, report regularly to senior management, focused intervention. Amber: major or
    significant, tighten controls, track the remedial actions. Green: low, monitor and escalate if it
    worsens.
  - Promote: Red and Amber risks with a schedule severity of II or more (confirmed).
  - Every scale, band, colour, text and the lookup table stay editable in Setup and are saved with
    the register, so another organisation's matrix can be entered.
- Cost-benefit method: per risk, days of P80 saved by its response (a paired run with only that risk
  mitigated, same seed) against the response cost, and value of those days at a cost of delay per day
  set in Setup [assumed]; or the cheaper expected-value method (probability x mean impact, no extra
  runs).
- Review lists PDF, Word, PowerPoint and CSV: keep the HTML report as a fifth format [assumed].
- Import of P6's own risk register (XER `PROJRISK`/`RISKTYPE`): later, not in this list.

- [x] Workflow shell. The seven steps and the Promote bridge in a column beside the page, as in the
      design (number, title and one-line description; Promote with an arrow), mapped for now to the
      existing panels: 01-03 and Promote show what they will hold, 04 the schedule panel, 05 the risk
      model, 06 results, 07 the report formats as a page instead of the Report dialog (the top bar's
      Report button opens it). The landing page shows until a step is picked or a schedule opens,
      which counts as 04. Setup, Identify, Assess and Schedule check are always open; Promote, Model
      and Results need a schedule, Review a run. Up to 1400px the descriptions go, and on a phone the
      column becomes a scrolling row. The step list is `Reporting/Workflow` in Core (text only, for
      the app and later the reports); the panels' kickers and the landing cards use the new numbers.
      In the app's own tokens (the red accent, light and dark), not the mock-up's blue. 4 tests.
- [x] Core: risk register and matrix. `Risk/Register` in Core: matrix settings (probability scale with
      letters, labels and % ranges; severity dimensions, each with five bands of text and, for
      schedule and cost, % ranges; the cell rating lookup; rating colours and guidance text; promote
      rule; categories), risks (ID, title, cause-event-effect description, category, threat or opportunity,
      status Proposed / Approved / Rejected / Closed, raised by and date), probability letter and a
      severity per dimension at each assessment point (inherent, current with existing controls,
      target after the response, as the arrows 5 -> 6 -> 7 in the reference; confirmed), response (avoid / transfer / mitigate / accept; exploit / share /
      enhance / accept for opportunities), owner, response cost, actions (text, owner, due date,
      status). JSON load and save with a version field (docs/RISK_REGISTER.md), validation messages,
      overall severity (worst dimension), cell and rating by lookup. Tests first. Needs approval: engine core.
      Done (approved): `ScheduleRisk.Core.Risk.Register` (`MatrixSettings`, `RiskRegister`,
      `RegisterRisk`, `Assessment`, `RiskAction`), the default matrix above with our own wording, any
      matrix size from 2x2 to 10x10, file format version 1 in docs/RISK_REGISTER.md (probability as band
      letters, severity as I, II...), validation (overlapping or near-certain bands, grid and band
      counts, duplicate ids, unknown areas or levels, responses that do not suit a threat or an
      opportunity, unknown categories), overdue actions. C# only: the register does not change the
      simulation, so there is no Python twin. The model and the simulation do not read it yet (Promote).
      19 tests.
- [x] 01 Setup. Edit the probability scale, the severity dimensions and their bands, the cell ratings
      (click a cell to cycle Red / Amber / Green), the rating guidance, the promote rule and the
      categories, with the defaults above and a live preview of the matrix; cost of delay per day. Open and save
      the register file.
      Done: `SetupPanel` in the app, the matrix drawn as in the reference (probability rows from E at the
      top, severity columns I to V, cell names, Red / Amber / Green from `--rag-*` tokens in light and
      dark), guidance cards under it, tables for probability bands, severity levels and each area
      (measured areas with ranges), categories as editable chips, the promote rule and cost of delay in
      the side column with the register's check messages as you edit. Adding or removing a band, a
      level or an area, renaming an area's id and resetting the matrix go through `RiskRegister`, so the
      grid, the areas' bands, the promote rule and the risks' assessments stay in step (10 tests). The
      register lives in `AppState` for the tab, needs no schedule, and is opened and saved as
      `*.register.json`.
- [x] 02 Identify. Propose risks (a form with the cause-event-effect prompts), approve or reject them,
      and edit their descriptions; a register table with search, category and status filters.
      Done: `IdentifyPanel`, with the proposal form in the side column (title, threat or opportunity,
      category, cause / event / effect with a live one-sentence statement, raised by and date; who raised
      it and the category stay for the next proposal) and the register beside it (status tabs with
      counts, search, category and type filters, the statement under each title, an edit row per risk).
      Workflow in `RiskRegister`: proposed -> approved or rejected, approved -> closed, rejected ->
      proposed and closed -> approved (reopen); approval needs a title and an event, and suggests a cause
      and an effect; only risks never approved can be deleted, approved ones are closed so the register
      keeps its history. `RegisterFilter` for search (every word, any field, ignoring case) and filters.
      The register file's Open / Save / New are shared with Setup (`RegisterFile`). 18 tests.
- [x] 03 Assess. Pick each approved risk's probability and severities from the guidance tables (the
      band text shown, as in the reference) at each assessment point; a heat map in the reference's
      layout (probability rows E to A, severity columns I to V, cell names, Red / Amber / Green, counts
      per cell, click a cell to filter the register) with a risk's path drawn as arrows between its
      assessment points; response, owner, cost and
      actions per risk; an action list across risks with overdue actions marked.
      Done: `AssessPanel`: approved risks listed in the side column with their current cell, the heat
      map (`HeatMapView`, SVG) for the inherent, current or target point with Red / Amber / Green counts
      and a count and ids per cell, clicking a cell lists its risks, and the selected risk's path drawn as
      markers 1-2-3 joined by arrows (spread apart when two points share a cell). Per risk: probability
      and a severity per area at each point, chosen from the matrix with the band's guidance shown under
      each choice, "Copy inherent" / "Copy current" to start a point from the one before, the cell and
      rating per point; response (threat or opportunity strategies), owner, cost and description;
      actions with owner, due date and status, overdue ones marked; and all actions across the register
      (open only by default, overdue first). Core: `HeatMap` (approved risks only, placed or not
      assessed, counts by rating, paths), `RiskRegister.ActionList` and `AssessmentIssues` (warnings: not
      assessed, no owner, an assessment worse than the one before, Amber or Red today with no response).
      8 tests.
- [x] Promote. Approved risks scoring at or above the threshold become quantified risks: the
      probability letter gives a probability (band midpoint, editable: A 2.5%, B 15%, C 37.5%, D 60%,
      E 82.5%), the schedule severity gives a triangle in working days from its % band times the
      planned project duration (project start to the deterministic finish; confirmed)
      (band low / midpoint / high, editable; I gives no schedule impact, V capped at 20%, confirmed), pre-mitigation from the current assessment and post from the target, mapped to activities with the activity picker. Core `RegisterPromoter` writes them
      into the risk model's register with the same IDs (traceable both ways); promoting again updates
      them and keeps the activity mapping. CLI: `sra promote register.json model.json`. Tests on the
      band arithmetic and the round trip.
      Done (approved): `RegisterPromoter` in Core: the planned duration (project start to deterministic
      finish in working days of the project calendar, equal to the Results' deterministic duration),
      eligibility with a reason for each risk left out, the numbers (band midpoint; schedule band low /
      middle / high x planned duration, rounded to 0.1 day; an opportunity's days negative; no target =
      no mitigation, a target without schedule severity = 0 days after mitigation), values set by hand
      and the activity mapping kept in the register (`promotion`, optional, file version still 1), and
      `Apply` writing rows marked `"source": "register"` into the model: added, updated, removed when they
      no longer qualify, skipped with a reason (no activities, no upper limit, id already used by a risk
      typed into the model). `PromotePanel` in the app and `sra promote` on the command line. 14 tests,
      and a register -> promote -> simulate run on synth_500 (P80 moves from 24-Jan-2029 to 11-Dec-2028
      after mitigation). The placeholder page for unbuilt steps is gone.
- [x] 04 Schedule check. Its own step: the engine check against P6, the health checks, the Gantt and
      the activity table (moved from the current step 01; the landing page keeps file opening).
      Scope (owner, 2026-09-28): the health checks become P6's Check Schedule parameters and the DCMA
      14-Point Assessment side by side, as in the owner's health-check report (JSON with `meta`,
      `p6_check_schedule` and `dcma_14_point`; definitions as in the p6-schedule-health-check skill).
      Plan:
      1. Core `Analysis/HealthCheck`, tests first: the 17 P6 parameters in three tabs (Relationships and
         Assignments, Dates and Durations, Constraints) and the 14 DCMA checks, each with count,
         denominator, actual, operator, target, PASS / FAIL / N/A, a note, and the flagged activities
         or relationships (ID and name, with the value that flagged them). Thresholds in hours (352 h =
         44 x 8 h for long lags, large float and large durations) and every target and operator
         editable; defaults are the owner's template, including the two operators that read backwards
         (Positive Lags `> 5%`, Relationship Types `< 90%`), reported both as configured and in the
         conventional reading, never silently fixed. N/A with the reason for an unstarted (baseline)
         export (Out of Sequence, Late / Missed Activities, BEI) and when the XER has no TASKRSRC table
         (Resources / Cost). Terminal activities exempt from Logic as in the skill (open ends at most 1%
         of activities).
      2. ScheduleBuilder reads what the checks need and does not read yet: `target_start_date` /
         `target_end_date` (baseline, for Late Activities, Missed Activities and BEI),
         `driving_path_flag`, and whether the file has a TASKRSRC table and which activities have an
         assignment. Read only: no scheduling change, and `sra verify` stays green on every fixture.
      3. JSON in the skill's schema from `sra validate --json out.json` (and a download in the app), so
         the owner's Word report builder reads it unchanged. Checked against the skill's
         `health_check.py` on every fixture in testdata/: same counts and statuses except where the
         decisions below say otherwise.
      4. Step 04 page: the engine check against P6, a scorecard (passed / failed / N/A per section), the
         two tables with target, actual and status, each row opening its flagged activities (click an
         ID to show it in the Gantt), then the Gantt and the activity table. The HTML, PDF, Word and
         PowerPoint reports and `sra validate` switch from today's 12 checks to the two sections.
      Decided (owner, 2026-09-28): (a) recalculated values, (b) the DCMA method, (c) retire the old checks.
      Were: (a) float, durations and critical path from our recalculated schedule (identical to
      P6's stored values when `sra verify` matches, and available for files P6 never scheduled) or
      from the values stored in the XER as the skill does; (b) DCMA #12 Critical Path Test and #13
      CPLI by the DCMA method on our engine (delay a critical activity and check the finish moves by
      the same amount; CPLI = (critical path length + project total float) / critical path length in
      working days) or by the skill's approximation from `driving_path_flag` (on synth_500, which has
      no flags, the skill fails #12 while the engine's test passes); (c) today's 12-check
      `ScheduleValidator` (and its Python twin) retired in favour of the new checks, keeping only the
      "constraints not modelled (ALAP)" warning.
      Done: `Analysis/HealthCheck` in Core (`HealthReport`, `HealthItem`, `HealthFlag`, `HealthCheckSettings`),
      docs/SCHEDULE_CHECK.md. ScheduleBuilder also reads the baseline dates, `driving_path_flag`, the raw
      status code, TASKRSRC and links to successors in other projects (read only; `sra verify` passes on
      all 9 fixtures). Step 04: a scorecard and the two tables (P6 grouped by tab), each row opening its
      flagged activities or relationships, an ID click showing the activity in the Gantt and table, and
      "Download JSON". Reports (HTML, PDF, Word, PowerPoint) carry both sections; `sra validate` prints
      them and writes `--json`. Against the owner's `health_check.py` on every fixture: identical counts,
      statuses and flagged sets except #12 / #13 as decided (the skill fails #12 on files with no
      driving_path_flag) and float to 4 decimals. Dangling Start leaves out started activities, as P6's
      description says (no fixture differs). `ScheduleValidator` is gone; its Python twin
      (reference/python/sra/validate.py) stays as the reference implementation's own check and golden
      files keep their `validation` block, no longer compared. 24 tests.

- [x] 05 Model. The current risk model panel: promoted risks shown with their register ID and score
      and edited at the register (a link back to Promote), other risks, uncertainty, drivers and
      correlation as today.
      Decided (owner, 2026-09-28): the model's discrete risks come from the risk register only.
      Link (approved 2026-09-28):
      1. The register is the single source. In step 05 the Risk register section lists the promoted
         risks read-only (id, title, current cell, probability, days, activities) with links to Assess
         and Promote; "+ Add risk" goes. Uncertainty, drivers, correlation and the simulation settings
         stay in the model.
      2. Every run promotes first: Run simulation = promote the register into the model, then simulate,
         so the model can never drift from the register. The Promote page stays for review, activity
         mapping and values set by hand; a run warns about eligible risks with no activities.
      3. Ids: model risks all come from the register, so the R01 clash goes away and register ids keep
         their R01 form. Promote no longer needs the "typed into the model" rule.
      4. Files: the register file stays the source of risks; the model file keeps uncertainty, drivers,
         correlation and settings, plus the promoted risks as written at the last run so a saved model
         still reproduces its results. CLI: `sra simulate --register register.json` promotes before
         simulating.
      5. Existing models with typed-in risks: "Move risks to the register" (app and `sra promote
         --import`) turns each into an approved register risk: its current probability band from its
         probability, its schedule severity from its mean impact over the planned duration, its numbers
         kept as values set by hand (so results do not change), mitigation as the target, and its
         filter resolved to activity ids against the open schedule. Risks in % of duration are listed
         for the user to convert, since the register works in days.
      Done: step 05 lists the model's risks read-only (id, title, current cell, probability, impact and its
      unit, mitigation, activities) with links to Assess and Promote; "+ Add risk" is gone. Every run
      promotes the register first (`AppState.PromoteForRun`; eligible risks left out are listed with the
      run's warnings), and opening step 05 does too, so the list is what the next run uses. Typed-in risks
      in an opened model are marked, with "Move to the register" (`RegisterImporter` in Core). A moved risk
      keeps its numbers as values set by hand in its own unit (new `promotion.impactUnits`, so % risks move
      too), its filter resolved to activities, `promotion.always` (promoted whatever its rating; a checkbox
      in Promote), and an assessment inferred from its probability and mean impact for the heat map; ids
      the register already has are renamed. The sample project's risks move into a fresh register when it
      loads (same results: P80 29-Jun-2029). CLI: `sra simulate --register` and `sra promote --import`.
      Moving the example model and promoting back gives identical P50, P80 and mean before and after
      mitigation (test, and the CLI on synth_500). 8 tests. Risks typed into a model are still simulated
      until moved, so opening an older model never changes its results silently.

- [x] 06 Results. Pre vs post mitigation as today; the risk ranking drawn as a tornado; a cost-benefit
      table (per risk: response cost, days saved at P80 and at the chosen level, value at the cost of
      delay, net benefit) using the method chosen above, reproducible for a seed. Tornado and
      cost-benefit also in the HTML report and the exports.
      Done: `RiskTornado` (risks by |pre-mitigation rank correlation|, post value beside each) drawn in
      Results, the HTML report (`HtmlReport.Tornado`) and the PDF / Word / PowerPoint exports
      (`ReportCharts.Tornado`), replacing the ranking bars. `Simulation/CostBenefit`: the candidates are the
      risks whose mitigated probability or impact differ; `WithMitigated` copies the model with one risk at
      its mitigated values and every risk in its place, so the paired run draws the same random numbers
      (no change to the simulation engine); one run per candidate with the pre-mitigation run's seed and
      iterations (or its convergence rule); days saved at P80, at the chosen level and on average; value =
      P80 days x the register's cost of delay, net = value - response cost (from Assess), benefit / cost;
      largest P80 saving first. In Results on request ("Work out cost-benefit", with progress and cancel),
      in every report, and `sra simulate --cost-benefit [--register] [--level]`. Tests: mitigating a model's
      only risk alone gives exactly the post-mitigation run; savings are never negative for a mitigation;
      same seed, same rows. 6 tests.
- [x] 07 Review. The export page (PDF, Word, PowerPoint, CSV and HTML), and the reports gain the
      register: heat maps pre and post, the register table, the action list and the cost-benefit; the
      CSV gains `register.csv`. Update README Status and Not yet.
      Done: `RegisterReport` in Core (the heat map drawn as SVG once for the HTML report and the export
      charts, the lead line, cells and responses), a "Risk register" section (heat maps now and after
      the responses, the approved risks with category, owner, both cells and the response) and a "Risk
      actions" section (overdue first, as of the report date) after the cost-benefit in the HTML, PDF,
      Word and PowerPoint reports; `CsvExport.Register` (one row per risk: description, status, cell and
      rating at each point, response and cost, open and overdue actions) in the app's CSVs and from `sra
      simulate --register`. The Review page lists what the report will hold. The export fixture now
      carries a register and a cost-benefit, so the PDF checks and the Open XML validator cover the new
      sections in every format. 6 tests.

- [x] Risk register as an Excel workbook, with a template. Open reads .xlsx or JSON; Save as Excel and Save
      as JSON; an Excel template on the register's matrix. Sheets: Guide, Risks (one row per risk, grouped
      columns for the three assessments with a severity column per area and a rating formula, response,
      model link), Actions, Matrix (editable blocks read back). Dropdowns from the matrix, frozen headers,
      the app's look. Read back by header name; empty IDs numbered; errors name sheet, row and column. Core
      `RegisterWorkbook` with a small .xlsx reader and writer (no new dependency); `sra register` converts
      and writes a template. Owner's decisions: Excel and JSON both kept, layout designed from the app's
      fields, the Matrix sheet read back, opening replaces the register. The template is also in
      docs/risk-register-template.xlsx. 19 tests.

- [x] Report structure in the owner's order: table of contents with page numbers (PDF), a TOC field (Word),
      slide numbers (PowerPoint), links (HTML); schedule health check; Summary & Charts (bell curve and
      S-curve); Risk & Activity Breakdown; Analysis of the Result as a Parameter / Value / Analysis
      Statement table; Sensitivity & Criticality (risk tornado, criticality index); Finish-Driving
      Activities; the remaining results; annexure of milestones only.

## Hosting and access

- [ ] Restricted access with Cloudflare Access. A copy of the browser app that only invited users can
      open. The login is checked by Cloudflare on every request, before any file is served, so it
      cannot be bypassed from the browser (a login screen inside the app could be, since its DLLs
      download to every visitor). The app gets no login code, no JWT handling and no new network
      call; Access's sign-in page and `CF_Authorization` cookie live outside the app, so
      `WebAssetsTests` still holds. XER files still never leave the browser: Cloudflare serves the
      app's own files and learns who signed in, never schedule data.
      Decided (owner, 2026-09-28): invited email addresses, signing in with Cloudflare's one-time
      PIN; the free `<project>.pages.dev` address; sessions of 2 days; the GitHub Pages copy stays
      up and public; the repository stays public for now. So Access restricts the Cloudflare copy,
      not the app: anyone can still use the GitHub Pages copy or build from source.
      Done in the repository:
      1. The workflow assembles two copies of the site (current app plus v0.4/ and v0.3/): GitHub
         Pages as before (base href `/schedule-risk/`, `404.html`, `.nojekyll`), and Cloudflare
         Pages at the root of its address (base href `/`, no `404.html`, so Pages serves index.html
         for unknown paths), checked against Pages' 25 MiB and 20,000-file limits. It deploys the
         Cloudflare copy with Wrangler once the repository has `CLOUDFLARE_API_TOKEN` (secret),
         `CLOUDFLARE_ACCOUNT_ID` and `CLOUDFLARE_PAGES_PROJECT` (variables); until then the step is
         skipped.
      2. Ended session: the files the app fetches after it has loaded (the report fonts on the first
         export, the sample project) fail once the session ends, because the request is sent to the
         sign-in page on another site. The app now says to open it in a new tab, sign in there, and
         try again in the first tab, which keeps its work (`SiteFiles`, 3 tests).
      3. docs/HOSTING.md: the Cloudflare setup (account, token, Pages project, repository settings,
         one-time PIN, one Access application covering `<project>.pages.dev` and
         `*.<project>.pages.dev`, 48-hour sessions, the email policy kept in Cloudflare and not in
         this public repository, Web Analytics left off), inviting and removing people, and the
         checks below. README and CHANGELOG.
      Left for the owner: the Cloudflare setup in docs/HOSTING.md. Tick when, from a private window,
      every path (/, `_framework/`, `sample/`, `fonts/`, v0.4/, v0.3/, a deployment address) shows
      the Access sign-in and no app file; an uninvited address gets no PIN; and signed in, the app,
      sample, simulation and every report export work as before.

## Shipped

- [x] v0.3: engine core (P6-rules CPM, calendars, constraints, risk model, Monte Carlo with
      Latin Hypercube, seed-reproducible), `sra` CLI, Blazor WebAssembly app on GitHub Pages
- [x] Interactive finish-date chart (hover and keyboard readout)
- [x] Combined finish-date chart: histogram on the primary (frequency) axis and the S-curve on the
      secondary (cumulative percent) axis, with the bars grouped by day or by week
- [x] Modernist redesign of the browser app, with a sample project
- [x] v0.4: interactive chart and redesign above, the engine-accuracy and Must Finish By fixes in
      this file, and the chance of meeting the Must Finish By (see CHANGELOG.md)
- [x] v0.5: duration statistics in the results, the landing page themed on industrial project
      planning, and the P6-style Gantt chart (see CHANGELOG.md)
- [x] v0.6: the Results Summary panel, plain-language notes on what the results mean, report
      exports as PDF, Word and PowerPoint, the rename to Project Risk Analysis, Expand all /
      Collapse all in the Gantt, and the searchable activity picker (see CHANGELOG.md)
