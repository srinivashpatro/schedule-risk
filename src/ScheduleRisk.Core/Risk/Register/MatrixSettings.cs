namespace ScheduleRisk.Core.Risk.Register;

public enum RiskRating { Green, Amber, Red }

/// <summary>One probability band of the matrix, e.g. C Occasional, 25-50%. Min and Max are fractions (0.25 = 25%).</summary>
public sealed class ProbabilityBand
{
    public string Letter { get; set; } = "";
    public string Label { get; set; } = "";
    public double Min { get; set; }
    public double Max { get; set; }
    public string Guidance { get; set; } = "";

    public ProbabilityBand Clone() => (ProbabilityBand)MemberwiseClone();
}

/// <summary>One severity level of one area: its description and, for a measured area, its range in the area's unit.
/// A missing Max means "and above".</summary>
public sealed class SeverityBand
{
    public string Text { get; set; } = "";
    public double? Min { get; set; }
    public double? Max { get; set; }

    public SeverityBand Clone() => (SeverityBand)MemberwiseClone();
}

/// <summary>An area a risk's severity is judged on (schedule, cost, safety...), with one band per severity level.
/// Schedule and cost are measured (Unit set, bands with ranges); the others are described in words.</summary>
public sealed class SeverityDimension
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Unit { get; set; }
    public List<SeverityBand> Bands { get; set; } = new();

    public bool Quantitative => !string.IsNullOrEmpty(Unit);

    public SeverityDimension Clone() => new() { Id = Id, Name = Name, Unit = Unit, Bands = Bands.Select(b => b.Clone()).ToList() };
}

/// <summary>What a rating asks of the project, e.g. Red: report to senior management.</summary>
public sealed class RatingGuidance
{
    public RiskRating Rating { get; set; }
    public string Label { get; set; } = "";
    public List<string> Actions { get; set; } = new();
}

/// <summary>Which risks go on to the quantitative model: at or above a rating, and at or above a severity on the
/// schedule area (a risk that cannot delay the schedule has nothing to simulate).</summary>
public sealed class PromoteRule
{
    public RiskRating MinRating { get; set; } = RiskRating.Amber;
    public string ScheduleDimension { get; set; } = "schedule";
    /// <summary>Severity level index (0 = I).</summary>
    public int MinScheduleSeverity { get; set; } = 1;
}

public sealed record RegisterIssue(bool Error, string Text)
{
    public override string ToString() => (Error ? "Error: " : "Warning: ") + Text;
}

/// <summary>
/// The probability-severity matrix of a risk register: probability bands (rows, lowest first), severity levels
/// (columns, I upwards), the areas severity is judged on, and a rating for every cell looked up from a grid
/// rather than computed from a score, so any organisation's matrix can be entered. Cells are named
/// severity.probability, e.g. IV.D.
/// </summary>
public sealed class MatrixSettings
{
    public List<ProbabilityBand> Probability { get; set; } = new();
    public List<string> SeverityLevels { get; set; } = new();
    public List<SeverityDimension> Dimensions { get; set; } = new();
    /// <summary>Ratings[p][s] for probability band p and severity level s.</summary>
    public List<RiskRating[]> Ratings { get; set; } = new();
    public List<RatingGuidance> Guidance { get; set; } = new();
    public PromoteRule Promote { get; set; } = new();
    public List<string> Categories { get; set; } = new();
    /// <summary>Value of one working day of delay to the project finish, for the cost-benefit of responses (0 = not set).</summary>
    public double CostOfDelayPerDay { get; set; }
    public string Currency { get; set; } = "";

    /// <summary>A band reaching above this is close to a certainty: it belongs in the base schedule, not the register.</summary>
    public const double CertaintyWarning = 0.95;

    private static readonly string[] RomanNumerals = { "I", "II", "III", "IV", "V", "VI", "VII", "VIII", "IX", "X" };

    public static string Roman(int level) => level >= 0 && level < RomanNumerals.Length ? RomanNumerals[level] : (level + 1).ToString();

    public static int ParseRoman(string s)
    {
        int i = Array.FindIndex(RomanNumerals, r => string.Equals(r, s.Trim(), StringComparison.OrdinalIgnoreCase));
        return i >= 0 ? i : int.TryParse(s, out int n) && n >= 1 ? n - 1 : -1;
    }

    public SeverityDimension? Dimension(string id) => Dimensions.FirstOrDefault(d => d.Id == id);

    public int ProbabilityIndex(string letter) =>
        Probability.FindIndex(p => string.Equals(p.Letter, letter.Trim(), StringComparison.OrdinalIgnoreCase));

    private bool InGrid(int p, int s) => p >= 0 && p < Ratings.Count && s >= 0 && s < Ratings[p].Length;

    public string CellName(int probability, int severity) =>
        $"{Roman(severity)}.{(probability >= 0 && probability < Probability.Count ? Probability[probability].Letter : "?")}";

    public string? CellName(Assessment a) =>
        a.Probability is int p && a.OverallSeverity is int s ? CellName(p, s) : null;

    public RiskRating Rate(int probability, int severity) =>
        InGrid(probability, severity) ? Ratings[probability][severity] : throw new ArgumentOutOfRangeException(nameof(probability), $"no cell {CellName(probability, severity)} in the rating grid");

    public RiskRating? Rate(Assessment a) =>
        a.Probability is int p && a.OverallSeverity is int s && InGrid(p, s) ? Ratings[p][s] : null;

    /// <summary>Setup: a click on a cell moves it Green, Amber, Red, Green.</summary>
    public void CycleRating(int probability, int severity)
    {
        var r = Rate(probability, severity);
        Ratings[probability][severity] = r == RiskRating.Red ? RiskRating.Green : r + 1;
    }

    public const int MinSize = 2;
    public static int MaxLevels => RomanNumerals.Length;

    public RatingGuidance? GuidanceFor(RiskRating r) => Guidance.FirstOrDefault(g => g.Rating == r);

    public MatrixSettings Clone() => new()
    {
        Probability = Probability.Select(p => p.Clone()).ToList(),
        SeverityLevels = SeverityLevels.ToList(),
        Dimensions = Dimensions.Select(d => d.Clone()).ToList(),
        Ratings = Ratings.Select(r => (RiskRating[])r.Clone()).ToList(),
        Guidance = Guidance.Select(g => new RatingGuidance { Rating = g.Rating, Label = g.Label, Actions = g.Actions.ToList() }).ToList(),
        Promote = new PromoteRule { MinRating = Promote.MinRating, ScheduleDimension = Promote.ScheduleDimension, MinScheduleSeverity = Promote.MinScheduleSeverity },
        Categories = Categories.ToList(),
        CostOfDelayPerDay = CostOfDelayPerDay,
        Currency = Currency,
    };

    /// <summary>Problems with the matrix itself; an empty list means it can be used.</summary>
    public List<RegisterIssue> Validate()
    {
        var issues = new List<RegisterIssue>();
        void Error(string t) => issues.Add(new RegisterIssue(true, t));
        void Warn(string t) => issues.Add(new RegisterIssue(false, t));

        int np = Probability.Count, ns = SeverityLevels.Count;
        if (np < 2) Error("The matrix needs at least two probability bands.");
        if (ns < 2) Error("The matrix needs at least two severity levels.");
        if (ns > RomanNumerals.Length) Error($"The matrix can have at most {RomanNumerals.Length} severity levels.");

        foreach (var g in Probability.GroupBy(p => p.Letter.Trim().ToUpperInvariant()).Where(g => g.Count() > 1 || g.Key.Length == 0))
            Error(g.Key.Length == 0 ? "Every probability band needs a letter." : $"Probability letter {g.Key} is used by more than one band.");
        for (int i = 0; i < np; i++)
        {
            var b = Probability[i];
            if (b.Min < 0 || b.Max > 1 || b.Min > b.Max)
                Error($"Probability band {b.Letter} ({Pct(b.Min)}-{Pct(b.Max)}) must lie between 0% and 100% with its minimum below its maximum.");
            if (i > 0 && b.Min < Probability[i - 1].Max - 1e-9)
                Error($"Probability bands {Probability[i - 1].Letter} and {b.Letter} overlap: list the bands from least to most likely without overlaps.");
            if (b.Max > CertaintyWarning + 1e-9)
                Warn($"Probability band {b.Letter} reaches {Pct(b.Max)}: a risk that close to certainty should go into the base schedule or estimate instead.");
        }

        foreach (var g in Dimensions.GroupBy(d => d.Id).Where(g => g.Count() > 1 || string.IsNullOrWhiteSpace(g.Key)))
            Error(string.IsNullOrWhiteSpace(g.Key) ? "Every severity area needs an id." : $"Severity area id {g.Key} is used more than once.");
        foreach (var d in Dimensions)
        {
            if (d.Bands.Count != ns)
            {
                Error($"{d.Name} has {d.Bands.Count} bands for {ns} severity levels.");
                continue;
            }
            if (!d.Quantitative) continue;
            for (int s = 0; s < ns; s++)
            {
                var b = d.Bands[s];
                if (b.Min is null || (b.Max is null && s < ns - 1))
                    Error($"{d.Name} level {Roman(s)} needs a range in {d.Unit}.");
                else if (b.Max is double max && b.Min > max)
                    Error($"{d.Name} level {Roman(s)} has its minimum ({b.Min}) above its maximum ({max}).");
                else if (s > 0 && d.Bands[s - 1].Max is double prev && b.Min < prev)
                    Error($"{d.Name} levels {Roman(s - 1)} and {Roman(s)} overlap.");
            }
        }

        if (Ratings.Count != np || Ratings.Any(r => r.Length != ns))
            Error($"The rating grid must have {np} rows (one per probability band) of {ns} ratings (one per severity level).");

        if (Dimension(Promote.ScheduleDimension) == null)
            Error($"The promote rule refers to severity area {Promote.ScheduleDimension}, which the matrix does not have.");
        if (Promote.MinScheduleSeverity < 0 || Promote.MinScheduleSeverity >= Math.Max(ns, 1))
            Error("The promote rule's minimum schedule severity is not a level of the matrix.");
        if (CostOfDelayPerDay < 0) Error("The cost of delay per day cannot be negative.");
        return issues;
    }

    private static string Pct(double f) => (f * 100).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "%";

    /// <summary>
    /// The default matrix: 5 x 5, probability A (remote) to E (most likely), severity I (insignificant) to V (very high)
    /// on six areas, schedule as a share of the planned project duration and cost as a share of the approved control
    /// budget. Its structure and numbers follow an owner's corporate risk guideline; the wording is our own.
    /// </summary>
    public static MatrixSettings Default()
    {
        static SeverityDimension Measured(string id, string name, string unit, (string Text, double Min, double? Max)[] bands) => new()
        {
            Id = id, Name = name, Unit = unit,
            Bands = bands.Select(b => new SeverityBand { Text = b.Text, Min = b.Min, Max = b.Max }).ToList(),
        };
        static SeverityDimension Described(string id, string name, params string[] texts) => new()
        {
            Id = id, Name = name, Bands = texts.Select(t => new SeverityBand { Text = t }).ToList(),
        };
        static RiskRating[] Row(string s) => s.Select(c => c switch { 'R' => RiskRating.Red, 'A' => RiskRating.Amber, _ => RiskRating.Green }).ToArray();

        return new MatrixSettings
        {
            Probability =
            {
                new() { Letter = "A", Label = "Remote", Min = 0, Max = .05, Guidance = "Not expected; it could happen only in exceptional circumstances. Recorded for completeness." },
                new() { Letter = "B", Label = "Unlikely", Min = .05, Max = .25, Guidance = "Not likely on this project, although it has happened elsewhere in the industry." },
                new() { Letter = "C", Label = "Occasional", Min = .25, Max = .50, Guidance = "It may well not happen, but it has happened from time to time and is a credible possibility here." },
                new() { Letter = "D", Label = "Likely", Min = .50, Max = .70, Guidance = "It has happened on similar projects and is more likely than not to happen on this one." },
                new() { Letter = "E", Label = "Most likely", Min = .70, Max = .95, Guidance = "It can reasonably be expected to happen on this project." },
            },
            SeverityLevels = { "Insignificant", "Minor", "Significant", "Major", "Very high" },
            Dimensions =
            {
                Measured("schedule", "Schedule", "% of planned project duration", new (string, double, double?)[]
                {
                    ("No delay to the finish; absorbed by the available float", 0, 0),
                    ("Delay of less than 3% of the planned project duration", 0, 3),
                    ("Delay of 3% to 6% of the planned project duration", 3, 6),
                    ("Delay of 6% to 10% of the planned project duration", 6, 10),
                    ("Delay of more than 10% of the planned project duration", 10, 20),
                }),
                Measured("cost", "Cost", "% of approved control budget", new (string, double, double?)[]
                {
                    ("Up to 0.25% of the approved control budget", 0, .25),
                    ("0.25% to 0.5% of the approved control budget", .25, .5),
                    ("0.5% to 1% of the approved control budget", .5, 1),
                    ("1% to 2% of the approved control budget", 1, 2),
                    ("More than 2% of the approved control budget", 2, null),
                }),
                Described("quality", "Quality and performance",
                    "No effect on objectives or on how users see the result",
                    "Slightly below expectations but within acceptable limits; users may notice",
                    "Outside acceptable limits; users are dissatisfied and cost or time are affected",
                    "Well outside the limits; substantial rework and changes are needed",
                    "Unacceptable; failure could cause a major loss of business"),
                Described("safety", "Health and safety",
                    "No injury of note and no working time lost",
                    "A minor injury to one person; work continues",
                    "Injuries to one or more people with substantial working time lost",
                    "A fatality or serious injuries, or work largely stopped; an investigation is likely",
                    "More than one fatality; legal action against the company is likely"),
                Described("environment", "Environment",
                    "No environmental damage of note",
                    "Minor damage, contained on site and put right; no adverse publicity",
                    "Moderate damage near the project that can be put right, at some cost or delay",
                    "Major damage the company cannot put right alone, with substantial cost, delay and adverse publicity",
                    "Severe damage that could make the project unviable and bring heavy penalties"),
                Described("regulatory", "Regulatory",
                    "A minor lapse, put right by declaration",
                    "Minor non-compliance; small penalties or some media attention possible",
                    "Significant non-compliance; substantial penalties or adverse publicity likely",
                    "Major non-compliance; heavy penalties or a temporary stop to work possible",
                    "Extensive non-compliance; work could be stopped indefinitely"),
            },
            // Rows A (remote) to E (most likely), columns I to V.
            Ratings = { Row("GGGAA"), Row("GGGAA"), Row("GGAAA"), Row("GGAAR"), Row("GAARR") },
            Guidance =
            {
                new() { Rating = RiskRating.Red, Label = "Red", Actions = { "Not tolerable: act now to reduce it", "Report to senior management at every review", "Give it an owner and focused actions with dates" } },
                new() { Rating = RiskRating.Amber, Label = "Amber", Actions = { "Significant: strengthen the existing controls", "Track the actions and check that they work" } },
                new() { Rating = RiskRating.Green, Label = "Green", Actions = { "Low: keep the existing controls in place", "Watch it and escalate it if it gets worse" } },
            },
            Categories = { "Design", "Procurement", "Construction", "Commissioning", "Commercial", "Regulatory", "Health and safety", "Environment", "External" },
        };
    }
}
