using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ScheduleRisk.Core.Risk.Register;

public enum RiskKind { Threat, Opportunity }
public enum RiskStatus { Proposed, Approved, Rejected, Closed }
public enum AssessmentPoint { Inherent, Current, Target }
public enum ResponseStrategy { None, Avoid, Transfer, Mitigate, Accept, Exploit, Share, Enhance }
public enum ActionStatus { Open, Done, Cancelled }

/// <summary>
/// A risk placed on the matrix at one point in time: a probability band and a severity level on each area that
/// applies. The risk's severity is its worst area, so a risk that is minor for the schedule but major for safety
/// sits in the Major column.
/// </summary>
public sealed class Assessment
{
    /// <summary>Probability band index (0 = the least likely band), or null when not assessed.</summary>
    public int? Probability { get; set; }
    /// <summary>Severity level index (0 = I) by area id; areas that do not apply are left out.</summary>
    public Dictionary<string, int> Severity { get; set; } = new();

    public int? OverallSeverity => Severity.Count == 0 ? null : Severity.Values.Max();

    public bool Complete => Probability != null && Severity.Count > 0;

    public Assessment Clone() => new() { Probability = Probability, Severity = new Dictionary<string, int>(Severity) };
}

public sealed class RiskAction
{
    public string Text { get; set; } = "";
    public string Owner { get; set; } = "";
    public DateOnly? Due { get; set; }
    public ActionStatus Status { get; set; } = ActionStatus.Open;

    public bool Overdue(DateOnly today) => Status == ActionStatus.Open && Due is DateOnly d && d < today;
}

/// <summary>
/// One risk in the register: its description (cause, event, effect), where it is in the approval workflow, and
/// three assessments: inherent (before any controls), current (with the controls in place today) and target
/// (once the response is carried out). Current and target become the pre- and post-mitigation risk in the model.
/// </summary>
public sealed class RegisterRisk
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Cause { get; set; } = "";
    public string Event { get; set; } = "";
    public string Effect { get; set; } = "";
    public string Category { get; set; } = "";
    public RiskKind Kind { get; set; } = RiskKind.Threat;
    public RiskStatus Status { get; set; } = RiskStatus.Proposed;
    public string RaisedBy { get; set; } = "";
    public DateOnly? Raised { get; set; }
    public string Owner { get; set; } = "";
    public Assessment Inherent { get; set; } = new();
    public Assessment Current { get; set; } = new();
    public Assessment Target { get; set; } = new();
    public ResponseStrategy Response { get; set; } = ResponseStrategy.None;
    public string ResponseDescription { get; set; } = "";
    /// <summary>Cost of carrying out the response, in the register's currency; null when not known.</summary>
    public double? ResponseCost { get; set; }
    public List<RiskAction> Actions { get; set; } = new();

    /// <summary>The risk as one sentence in cause-event-effect form: "Because of (cause), (event), which would lead to
    /// (effect)." Empty until the event is written.</summary>
    public string Statement
    {
        get
        {
            static string Clean(string s) => s.Trim().TrimEnd('.').Trim();
            static string Lower(string s) => s.Length > 1 && char.IsUpper(s[0]) && !char.IsUpper(s[1]) ? char.ToLowerInvariant(s[0]) + s[1..] : s;
            static string Upper(string s) => s.Length > 0 ? char.ToUpperInvariant(s[0]) + s[1..] : s;
            string cause = Clean(Cause), ev = Clean(Event), effect = Clean(Effect);
            if (ev.Length == 0) return "";
            string text = cause.Length > 0 ? $"Because of {Lower(cause)}, {ev}" : Upper(ev);
            if (effect.Length > 0) text += $", which would lead to {effect}";
            return text + ".";
        }
    }

    public Assessment At(AssessmentPoint p) => p switch
    {
        AssessmentPoint.Inherent => Inherent,
        AssessmentPoint.Current => Current,
        _ => Target,
    };

    /// <summary>The response strategies that suit a threat or an opportunity (PMBOK's lists).</summary>
    public static IReadOnlyList<ResponseStrategy> Responses(RiskKind kind) => kind == RiskKind.Threat
        ? new[] { ResponseStrategy.Avoid, ResponseStrategy.Transfer, ResponseStrategy.Mitigate, ResponseStrategy.Accept }
        : new[] { ResponseStrategy.Exploit, ResponseStrategy.Share, ResponseStrategy.Enhance, ResponseStrategy.Accept };
}

/// <summary>
/// The qualitative risk register (docs/RISK_REGISTER.md): its matrix and its risks. Saved and opened as a JSON
/// file on the user's device, next to the risk model; nothing here is sent anywhere.
/// </summary>
public sealed class RiskRegister
{
    public const int FormatVersion = 1;

    public string Name { get; set; } = "Risk register";
    public MatrixSettings Matrix { get; set; } = MatrixSettings.Default();
    public List<RegisterRisk> Risks { get; } = new();

    public string NextId()
    {
        int k = 1;
        while (Risks.Any(r => r.Id == $"R{k:00}")) k++;
        return $"R{k:00}";
    }

    // ------------------------------------------------------------------ identifying risks (step 02 Identify)

    /// <summary>Adds a proposed risk with the next free id.</summary>
    public RegisterRisk Propose(string title, string cause, string @event, string effect, string category, RiskKind kind, string raisedBy, DateOnly raised)
    {
        var r = new RegisterRisk
        {
            Id = NextId(), Title = title.Trim(), Cause = cause.Trim(), Event = @event.Trim(), Effect = effect.Trim(),
            Category = category, Kind = kind, RaisedBy = raisedBy.Trim(), Raised = raised, Status = RiskStatus.Proposed,
        };
        Risks.Add(r);
        return r;
    }

    /// <summary>What a risk needs before it can be approved (errors) and what it should have (warnings).</summary>
    public static List<RegisterIssue> ApprovalIssues(RegisterRisk r)
    {
        var issues = new List<RegisterIssue>();
        if (string.IsNullOrWhiteSpace(r.Title)) issues.Add(new RegisterIssue(true, "Give the risk a title."));
        if (string.IsNullOrWhiteSpace(r.Event)) issues.Add(new RegisterIssue(true, "Describe the event: what might happen."));
        if (string.IsNullOrWhiteSpace(r.Cause)) issues.Add(new RegisterIssue(false, "Add the cause: why it might happen."));
        if (string.IsNullOrWhiteSpace(r.Effect)) issues.Add(new RegisterIssue(false, "Add the effect on the project if it happens."));
        return issues;
    }

    /// <summary>The approval workflow: a proposed risk is approved or rejected, an approved one is closed once it can
    /// no longer happen, and a rejected or closed one can be reopened (as proposed or approved).</summary>
    public static bool CanMove(RiskStatus from, RiskStatus to) => (from, to) switch
    {
        (RiskStatus.Proposed, RiskStatus.Approved or RiskStatus.Rejected) => true,
        (RiskStatus.Approved, RiskStatus.Closed) => true,
        (RiskStatus.Rejected, RiskStatus.Proposed) => true,
        (RiskStatus.Closed, RiskStatus.Approved) => true,
        _ => false,
    };

    public void SetStatus(RegisterRisk r, RiskStatus to)
    {
        if (!CanMove(r.Status, to))
            throw new InvalidOperationException($"{r.Id} is {r.Status.ToString().ToLowerInvariant()} and cannot become {to.ToString().ToLowerInvariant()}.");
        if (to == RiskStatus.Approved && ApprovalIssues(r).FirstOrDefault(i => i.Error) is { } missing)
            throw new InvalidOperationException($"{r.Id} cannot be approved yet: {missing.Text}");
        r.Status = to;
    }

    /// <summary>Only risks never approved can be deleted; an approved risk is closed instead, so the register keeps its history.</summary>
    public bool CanDelete(RegisterRisk r) => r.Status is RiskStatus.Proposed or RiskStatus.Rejected;

    public void Delete(RegisterRisk r)
    {
        if (!CanDelete(r)) throw new InvalidOperationException($"{r.Id} has been approved: close it instead of deleting it.");
        Risks.Remove(r);
    }

    public Dictionary<RiskStatus, int> StatusCounts() =>
        System.Enum.GetValues<RiskStatus>().ToDictionary(s => s, s => Risks.Count(r => r.Status == s));

    // ------------------------------------------------------------------ editing the matrix (step 01 Setup)
    // Structural edits go through the register so that the rating grid, every area's bands and the risks'
    // assessments stay in step with the matrix.

    private IEnumerable<Assessment> Assessments =>
        Risks.SelectMany(r => new[] { r.Inherent, r.Current, r.Target });

    public void ResetMatrix() => Matrix = MatrixSettings.Default();

    /// <summary>Adds a band above the most likely one, rated like the band below it.</summary>
    public void AddProbabilityBand()
    {
        var m = Matrix;
        var last = m.Probability.LastOrDefault();
        string letter = last is { Letter.Length: 1 } && char.IsAsciiLetter(last.Letter[0]) && last.Letter[0] is not ('z' or 'Z')
            ? ((char)(last.Letter[0] + 1)).ToString() : "?";
        double min = last?.Max ?? 0;
        m.Probability.Add(new ProbabilityBand { Letter = letter, Label = "New band", Min = min, Max = Math.Min(1, min + (last == null ? .25 : last.Max - last.Min)) });
        m.Ratings.Add(m.Ratings.Count > 0 ? (RiskRating[])m.Ratings[^1].Clone() : new RiskRating[m.SeverityLevels.Count]);
    }

    /// <summary>Removes a band (the matrix keeps at least two). Assessments on it lose their probability; those above move down.</summary>
    public void RemoveProbabilityBand(int index)
    {
        var m = Matrix;
        if (m.Probability.Count <= MatrixSettings.MinSize || index < 0 || index >= m.Probability.Count) return;
        m.Probability.RemoveAt(index);
        if (index < m.Ratings.Count) m.Ratings.RemoveAt(index);
        foreach (var a in Assessments)
            if (a.Probability == index) a.Probability = null;
            else if (a.Probability > index) a.Probability--;
    }

    /// <summary>Adds a level above the highest one: a band on every area (a measured area continues its ranges) and a
    /// grid column rated like the one below it.</summary>
    public void AddSeverityLevel()
    {
        var m = Matrix;
        if (m.SeverityLevels.Count >= MatrixSettings.MaxLevels) return;
        m.SeverityLevels.Add("New level");
        foreach (var d in m.Dimensions)
        {
            var band = new SeverityBand();
            if (d.Quantitative && d.Bands.Count > 0)
            {
                var last = d.Bands[^1];
                last.Max ??= last.Min is > 0 ? last.Min * 2 : 1;
                band.Min = last.Max;
            }
            d.Bands.Add(band);
        }
        for (int p = 0; p < m.Ratings.Count; p++)
            m.Ratings[p] = m.Ratings[p].Append(m.Ratings[p].Length > 0 ? m.Ratings[p][^1] : RiskRating.Green).ToArray();
    }

    /// <summary>Removes a level (the matrix keeps at least two) from the grid and every area. Assessments at that level
    /// lose it; higher ones move down, and so does the promote rule's minimum.</summary>
    public void RemoveSeverityLevel(int index)
    {
        var m = Matrix;
        if (m.SeverityLevels.Count <= MatrixSettings.MinSize || index < 0 || index >= m.SeverityLevels.Count) return;
        m.SeverityLevels.RemoveAt(index);
        foreach (var d in m.Dimensions)
            if (index < d.Bands.Count) d.Bands.RemoveAt(index);
        for (int p = 0; p < m.Ratings.Count; p++)
            if (index < m.Ratings[p].Length) m.Ratings[p] = m.Ratings[p].Where((_, s) => s != index).ToArray();
        foreach (var a in Assessments)
            foreach (var (area, level) in a.Severity.ToList())
                if (level == index) a.Severity.Remove(area);
                else if (level > index) a.Severity[area] = level - 1;
        if (m.Promote.MinScheduleSeverity > index || m.Promote.MinScheduleSeverity >= m.SeverityLevels.Count)
            m.Promote.MinScheduleSeverity = Math.Max(0, m.Promote.MinScheduleSeverity - 1);
    }

    /// <summary>Adds an area described in words, with an empty band per level.</summary>
    public SeverityDimension AddDimension()
    {
        int k = Matrix.Dimensions.Count + 1;
        while (Matrix.Dimension($"area{k}") != null) k++;
        var d = new SeverityDimension { Id = $"area{k}", Name = "New area" };
        d.Bands.AddRange(Matrix.SeverityLevels.Select(_ => new SeverityBand()));
        Matrix.Dimensions.Add(d);
        return d;
    }

    /// <summary>Removes an area; assessments forget their severity on it.</summary>
    public void RemoveDimension(string id)
    {
        Matrix.Dimensions.RemoveAll(d => d.Id == id);
        foreach (var a in Assessments) a.Severity.Remove(id);
    }

    /// <summary>Changes an area's id, and with it the assessments and the promote rule that refer to it.</summary>
    public void RenameDimension(string id, string newId)
    {
        newId = newId.Trim();
        if (newId == id) return;
        if (newId.Length == 0) throw new ArgumentException("An area id cannot be empty.", nameof(newId));
        if (Matrix.Dimension(newId) != null) throw new ArgumentException($"Area id {newId} is already used.", nameof(newId));
        var d = Matrix.Dimension(id) ?? throw new ArgumentException($"No area {id}.", nameof(id));
        d.Id = newId;
        foreach (var a in Assessments)
            if (a.Severity.Remove(id, out int level)) a.Severity[newId] = level;
        if (Matrix.Promote.ScheduleDimension == id) Matrix.Promote.ScheduleDimension = newId;
    }

    /// <summary>Problems with the matrix and the risks. Errors make the register unusable until fixed; warnings do not.</summary>
    public List<RegisterIssue> Validate()
    {
        var issues = Matrix.Validate();
        void Error(string t) => issues.Add(new RegisterIssue(true, t));
        void Warn(string t) => issues.Add(new RegisterIssue(false, t));

        foreach (var g in Risks.GroupBy(r => r.Id))
        {
            if (string.IsNullOrWhiteSpace(g.Key)) Error("Every risk needs an id.");
            else if (g.Count() > 1) Error($"Risk id {g.Key} is used more than once.");
        }
        foreach (var r in Risks)
        {
            string name = string.IsNullOrWhiteSpace(r.Id) ? $"\"{r.Title}\"" : r.Id;
            foreach (var point in new[] { AssessmentPoint.Inherent, AssessmentPoint.Current, AssessmentPoint.Target })
            {
                var a = r.At(point);
                string at = point.ToString().ToLowerInvariant();
                if (a.Probability is int p && (p < 0 || p >= Matrix.Probability.Count))
                    Error($"{name}: the {at} probability is not a band of the matrix.");
                foreach (var (area, level) in a.Severity)
                {
                    if (Matrix.Dimension(area) == null) Error($"{name}: the {at} assessment rates severity area {area}, which the matrix does not have.");
                    else if (level < 0 || level >= Matrix.SeverityLevels.Count) Error($"{name}: the {at} {area} severity is not a level of the matrix.");
                }
            }
            if (r.Response != ResponseStrategy.None && !RegisterRisk.Responses(r.Kind).Contains(r.Response))
                Error($"{name}: {r.Response} is not a response to {(r.Kind == RiskKind.Threat ? "a threat" : "an opportunity")}.");
            if (r.ResponseCost < 0) Error($"{name}: the response cost cannot be negative.");
            if (r.Category.Length > 0 && !Matrix.Categories.Contains(r.Category))
                Warn($"{name}: category {r.Category} is not one of the register's categories.");
        }
        return issues;
    }

    // ------------------------------------------------------------------ JSON

    private static string Lower<T>(T e) where T : struct, Enum => e.ToString().ToLowerInvariant();

    private static char Letter(RiskRating r) => r switch { RiskRating.Red => 'R', RiskRating.Amber => 'A', _ => 'G' };

    public string ToJson()
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteNumber("version", FormatVersion);
            w.WriteString("name", Name);
            WriteMatrix(w, Matrix);
            w.WriteStartArray("risks");
            foreach (var r in Risks) WriteRisk(w, r, Matrix);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void WriteMatrix(Utf8JsonWriter w, MatrixSettings m)
    {
        w.WriteStartObject("matrix");
        w.WriteStartArray("probability");
        foreach (var p in m.Probability)
        {
            w.WriteStartObject();
            w.WriteString("letter", p.Letter);
            w.WriteString("label", p.Label);
            w.WriteNumber("min", p.Min);
            w.WriteNumber("max", p.Max);
            if (p.Guidance.Length > 0) w.WriteString("guidance", p.Guidance);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteStartArray("severityLevels");
        foreach (var s in m.SeverityLevels) w.WriteStringValue(s);
        w.WriteEndArray();
        w.WriteStartArray("dimensions");
        foreach (var d in m.Dimensions)
        {
            w.WriteStartObject();
            w.WriteString("id", d.Id);
            w.WriteString("name", d.Name);
            if (d.Quantitative) w.WriteString("unit", d.Unit);
            w.WriteStartArray("bands");
            foreach (var b in d.Bands)
            {
                w.WriteStartObject();
                w.WriteString("text", b.Text);
                if (b.Min is double min) w.WriteNumber("min", min);
                if (b.Max is double max) w.WriteNumber("max", max);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteStartArray("ratings");
        foreach (var row in m.Ratings) w.WriteStringValue(new string(row.Select(Letter).ToArray()));
        w.WriteEndArray();
        w.WriteStartArray("guidance");
        foreach (var g in m.Guidance)
        {
            w.WriteStartObject();
            w.WriteString("rating", Lower(g.Rating));
            w.WriteString("label", g.Label);
            w.WriteStartArray("actions");
            foreach (var a in g.Actions) w.WriteStringValue(a);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteStartObject("promote");
        w.WriteString("minRating", Lower(m.Promote.MinRating));
        w.WriteString("scheduleDimension", m.Promote.ScheduleDimension);
        w.WriteString("minScheduleSeverity", MatrixSettings.Roman(m.Promote.MinScheduleSeverity));
        w.WriteEndObject();
        w.WriteStartArray("categories");
        foreach (var c in m.Categories) w.WriteStringValue(c);
        w.WriteEndArray();
        w.WriteNumber("costOfDelayPerDay", m.CostOfDelayPerDay);
        w.WriteString("currency", m.Currency);
        w.WriteEndObject();
    }

    private static void WriteRisk(Utf8JsonWriter w, RegisterRisk r, MatrixSettings m)
    {
        w.WriteStartObject();
        w.WriteString("id", r.Id);
        w.WriteString("title", r.Title);
        w.WriteString("cause", r.Cause);
        w.WriteString("event", r.Event);
        w.WriteString("effect", r.Effect);
        w.WriteString("category", r.Category);
        w.WriteString("kind", Lower(r.Kind));
        w.WriteString("status", Lower(r.Status));
        w.WriteString("raisedBy", r.RaisedBy);
        if (r.Raised is DateOnly raised) w.WriteString("raised", raised.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        w.WriteString("owner", r.Owner);
        foreach (var point in new[] { AssessmentPoint.Inherent, AssessmentPoint.Current, AssessmentPoint.Target })
        {
            var a = r.At(point);
            if (a.Probability == null && a.Severity.Count == 0) continue;
            w.WriteStartObject(Lower(point));
            if (a.Probability is int p)
            {
                if (p >= 0 && p < m.Probability.Count) w.WriteString("probability", m.Probability[p].Letter);
                else w.WriteNumber("probability", p);
            }
            w.WriteStartObject("severity");
            foreach (var (area, level) in a.Severity) w.WriteString(area, MatrixSettings.Roman(level));
            w.WriteEndObject();
            w.WriteEndObject();
        }
        w.WriteString("response", Lower(r.Response));
        w.WriteString("responseDescription", r.ResponseDescription);
        if (r.ResponseCost is double cost) w.WriteNumber("responseCost", cost);
        w.WriteStartArray("actions");
        foreach (var a in r.Actions)
        {
            w.WriteStartObject();
            w.WriteString("text", a.Text);
            w.WriteString("owner", a.Owner);
            if (a.Due is DateOnly due) w.WriteString("due", due.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            w.WriteString("status", Lower(a.Status));
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    /// <summary>Reads a register file. Missing fields take their defaults and a missing matrix is the default matrix;
    /// a file from a newer version, or a value that cannot be read, is refused with a FormatException.</summary>
    public static RiskRegister FromJson(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var root = doc.RootElement;
        int version = root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : FormatVersion;
        if (version > FormatVersion)
            throw new FormatException($"This register file is version {version}; this app reads up to version {FormatVersion}. Use a newer version of the app.");

        var reg = new RiskRegister { Name = S(root, "name", "Risk register") };
        if (root.TryGetProperty("matrix", out var m) && m.ValueKind == JsonValueKind.Object) reg.Matrix = ReadMatrix(m);
        if (root.TryGetProperty("risks", out var risks) && risks.ValueKind == JsonValueKind.Array)
            foreach (var r in risks.EnumerateArray()) reg.Risks.Add(ReadRisk(r, reg.Matrix));
        return reg;
    }

    private static string S(JsonElement e, string name, string dflt = "") =>
        e.TryGetProperty(name, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? dflt : v.ToString()) : dflt;

    private static double? N(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static List<string> Strings(JsonElement e, string name) =>
        e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : new List<string>();

    private static T ReadEnum<T>(JsonElement e, string name, T dflt) where T : struct, Enum
    {
        string s = S(e, name);
        if (s.Length == 0) return dflt;
        return System.Enum.TryParse<T>(s, ignoreCase: true, out var value) && System.Enum.IsDefined(value) ? value
            : throw new FormatException($"\"{s}\" is not a valid {name} (expected one of: {string.Join(", ", System.Enum.GetNames<T>().Select(n => n.ToLowerInvariant()))}).");
    }

    private static DateOnly? Date(JsonElement e, string name)
    {
        string s = S(e, name);
        if (s.Length == 0) return null;
        return DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d
            : throw new FormatException($"\"{s}\" is not a date in the form yyyy-mm-dd ({name}).");
    }

    private static MatrixSettings ReadMatrix(JsonElement e)
    {
        var dflt = MatrixSettings.Default();
        var m = new MatrixSettings();
        if (e.TryGetProperty("probability", out var probs) && probs.ValueKind == JsonValueKind.Array)
            foreach (var p in probs.EnumerateArray())
                m.Probability.Add(new ProbabilityBand { Letter = S(p, "letter"), Label = S(p, "label"), Min = N(p, "min") ?? 0, Max = N(p, "max") ?? 0, Guidance = S(p, "guidance") });
        else m.Probability = dflt.Probability;
        var levels = Strings(e, "severityLevels");
        m.SeverityLevels = levels.Count > 0 ? levels : dflt.SeverityLevels;
        if (e.TryGetProperty("dimensions", out var dims) && dims.ValueKind == JsonValueKind.Array)
            foreach (var d in dims.EnumerateArray())
            {
                var dim = new SeverityDimension { Id = S(d, "id"), Name = S(d, "name") };
                string unit = S(d, "unit");
                dim.Unit = unit.Length > 0 ? unit : null;
                if (dim.Name.Length == 0) dim.Name = dim.Id;
                if (d.TryGetProperty("bands", out var bands) && bands.ValueKind == JsonValueKind.Array)
                    foreach (var b in bands.EnumerateArray())
                        dim.Bands.Add(new SeverityBand { Text = S(b, "text"), Min = N(b, "min"), Max = N(b, "max") });
                m.Dimensions.Add(dim);
            }
        else m.Dimensions = dflt.Dimensions;
        var ratings = Strings(e, "ratings");
        if (ratings.Count > 0)
            foreach (var row in ratings)
                m.Ratings.Add(row.Where(c => !char.IsWhiteSpace(c)).Select(c => char.ToUpperInvariant(c) switch
                {
                    'R' => RiskRating.Red,
                    'A' => RiskRating.Amber,
                    'G' => RiskRating.Green,
                    _ => throw new FormatException($"Rating grid row \"{row}\": use R, A or G for each cell."),
                }).ToArray());
        else m.Ratings = dflt.Ratings;
        if (e.TryGetProperty("guidance", out var guid) && guid.ValueKind == JsonValueKind.Array)
            foreach (var g in guid.EnumerateArray())
                m.Guidance.Add(new RatingGuidance { Rating = ReadEnum(g, "rating", RiskRating.Green), Label = S(g, "label"), Actions = Strings(g, "actions") });
        else m.Guidance = dflt.Guidance;
        if (e.TryGetProperty("promote", out var pr) && pr.ValueKind == JsonValueKind.Object)
        {
            m.Promote.MinRating = ReadEnum(pr, "minRating", RiskRating.Amber);
            m.Promote.ScheduleDimension = S(pr, "scheduleDimension", "schedule");
            string min = S(pr, "minScheduleSeverity", "II");
            m.Promote.MinScheduleSeverity = MatrixSettings.ParseRoman(min);
            if (m.Promote.MinScheduleSeverity < 0) throw new FormatException($"\"{min}\" is not a severity level (use I, II, III...).");
        }
        m.Categories = e.TryGetProperty("categories", out _) ? Strings(e, "categories") : dflt.Categories;
        m.CostOfDelayPerDay = N(e, "costOfDelayPerDay") ?? 0;
        m.Currency = S(e, "currency");
        return m;
    }

    private static Assessment ReadAssessment(JsonElement e, MatrixSettings m, string id, string point)
    {
        var a = new Assessment();
        if (e.TryGetProperty("probability", out var p))
        {
            if (p.ValueKind == JsonValueKind.Number) a.Probability = p.GetInt32();
            else if (p.ValueKind == JsonValueKind.String)
            {
                int i = m.ProbabilityIndex(p.GetString() ?? "");
                a.Probability = i >= 0 ? i : throw new FormatException($"{id}: {point} probability \"{p.GetString()}\" is not a band of the matrix.");
            }
        }
        if (e.TryGetProperty("severity", out var sev) && sev.ValueKind == JsonValueKind.Object)
            foreach (var s in sev.EnumerateObject())
            {
                string text = s.Value.ValueKind == JsonValueKind.String ? s.Value.GetString() ?? "" : s.Value.ToString();
                int level = MatrixSettings.ParseRoman(text);
                a.Severity[s.Name] = level >= 0 ? level : throw new FormatException($"{id}: {point} {s.Name} severity \"{text}\" is not a level (use I, II, III...).");
            }
        return a;
    }

    private static RegisterRisk ReadRisk(JsonElement e, MatrixSettings m)
    {
        var r = new RegisterRisk
        {
            Id = S(e, "id"), Title = S(e, "title"), Cause = S(e, "cause"), Event = S(e, "event"), Effect = S(e, "effect"),
            Category = S(e, "category"), Kind = ReadEnum(e, "kind", RiskKind.Threat), Status = ReadEnum(e, "status", RiskStatus.Proposed),
            RaisedBy = S(e, "raisedBy"), Raised = Date(e, "raised"), Owner = S(e, "owner"),
            Response = ReadEnum(e, "response", ResponseStrategy.None), ResponseDescription = S(e, "responseDescription"),
            ResponseCost = N(e, "responseCost"),
        };
        foreach (var point in new[] { AssessmentPoint.Inherent, AssessmentPoint.Current, AssessmentPoint.Target })
            if (e.TryGetProperty(Lower(point), out var a) && a.ValueKind == JsonValueKind.Object)
            {
                var read = ReadAssessment(a, m, r.Id, Lower(point));
                switch (point)
                {
                    case AssessmentPoint.Inherent: r.Inherent = read; break;
                    case AssessmentPoint.Current: r.Current = read; break;
                    default: r.Target = read; break;
                }
            }
        if (e.TryGetProperty("actions", out var acts) && acts.ValueKind == JsonValueKind.Array)
            foreach (var a in acts.EnumerateArray())
                r.Actions.Add(new RiskAction { Text = S(a, "text"), Owner = S(a, "owner"), Due = Date(a, "due"), Status = ReadEnum(a, "status", ActionStatus.Open) });
        return r;
    }
}

/// <summary>Finds risks in the register: every word of the search must appear in the id, title, cause, event, effect,
/// category, owner or who raised it (ignoring case), and each filter that is set must match.</summary>
public sealed class RegisterFilter
{
    public string Search { get; set; } = "";
    /// <summary>Empty for every category.</summary>
    public string Category { get; set; } = "";
    public RiskStatus? Status { get; set; }
    public RiskKind? Kind { get; set; }

    public bool Matches(RegisterRisk r)
    {
        if (Status is RiskStatus s && r.Status != s) return false;
        if (Kind is RiskKind k && r.Kind != k) return false;
        if (Category.Length > 0 && r.Category != Category) return false;
        var words = Search.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return true;
        string text = string.Join("\n", r.Id, r.Title, r.Cause, r.Event, r.Effect, r.Category, r.Owner, r.RaisedBy);
        return words.All(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    public IEnumerable<RegisterRisk> Apply(IEnumerable<RegisterRisk> risks) => risks.Where(Matches);
}
