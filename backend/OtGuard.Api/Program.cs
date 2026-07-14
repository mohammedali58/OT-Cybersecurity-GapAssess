using System.Security.Claims;
using System.Text.RegularExpressions;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<AssessmentStore>();
builder.Services.AddCors(options => options.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));
var app = builder.Build();
app.UseCors();
app.UseStaticFiles();

app.MapPost("/api/auth/login", (LoginRequest request) =>
{
    var users = new Dictionary<string, (string Password, string Role)>(StringComparer.OrdinalIgnoreCase)
    {
        ["admin"] = ("admin", "Admin"), ["assessor"] = ("assessor", "Assessor"), ["viewer"] = ("viewer", "Viewer")
    };
    return users.TryGetValue(request.Username, out var user) && user.Password == request.Password
        ? Results.Ok(new LoginResponse(request.Username, user.Role, Convert.ToBase64String(Guid.NewGuid().ToByteArray())))
        : Results.Unauthorized();
});

app.MapGet("/api/assessments", (AssessmentStore store) => Results.Ok(store.Assessments));
app.MapPost("/api/assessments", (CreateAssessmentRequest request, AssessmentStore store) =>
{
    var assessment = new Assessment(Guid.NewGuid(), request.Name, request.BaselineVersion, DateTimeOffset.UtcNow, "Draft", [], []);
    store.Assessments.Add(assessment);
    return Results.Created($"/api/assessments/{assessment.Id}", assessment);
});

app.MapPost("/api/assessments/{assessmentId:guid}/inventory", async (Guid assessmentId, IFormFile file, AssessmentStore store) =>
{
    if (!IsAllowed(file, ".xlsx", ".xls", ".csv")) return Results.BadRequest("Inventory must be an Excel or CSV file.");
    var assets = await InventoryParser.ParseAsync(file);
    var assessment = store.Get(assessmentId);
    if (assessment is null) return Results.NotFound();
    assessment.Assets = assets;
    return Results.Ok(assets);
});

app.MapPost("/api/baselines", async (IFormFile file, string version, AssessmentStore store) =>
{
    if (!IsAllowed(file, ".xlsx", ".xls", ".csv")) return Results.BadRequest("Baseline must be an Excel or CSV file.");
    // CSV is supported in the starter without third-party packages. Add ClosedXML for .xlsx parsing in production.
    var rules = await BaselineParser.ParseAsync(file);
    var baseline = new Baseline(Guid.NewGuid(), version, DateTimeOffset.UtcNow, rules);
    store.Baselines.Add(baseline);
    return Results.Ok(baseline);
});

app.MapPost("/api/assessments/{assessmentId:guid}/configurations", async (Guid assessmentId, string assetName, IFormFile file, AssessmentStore store) =>
{
    if (!IsAllowed(file, ".txt", ".cfg", ".conf")) return Results.BadRequest("Configuration must be .txt, .cfg, or .conf.");
    var assessment = store.Get(assessmentId);
    if (assessment is null) return Results.NotFound();
    using var reader = new StreamReader(file.OpenReadStream());
    var parsed = SwitchConfigParser.Parse(assetName, await reader.ReadToEndAsync());
    store.Configurations[(assessmentId, assetName)] = parsed;
    return Results.Ok(parsed);
});

app.MapPost("/api/assessments/{assessmentId:guid}/run", (Guid assessmentId, Guid baselineId, AssessmentStore store) =>
{
    var assessment = store.Get(assessmentId);
    var baseline = store.Baselines.SingleOrDefault(b => b.Id == baselineId);
    if (assessment is null || baseline is null) return Results.NotFound();
    assessment.Results = assessment.Assets.Where(a => a.AssetType.Contains("switch", StringComparison.OrdinalIgnoreCase))
        .SelectMany(a => baseline.Rules.Select(rule => ComparisonEngine.Compare(rule, a, store.Configurations.GetValueOrDefault((assessmentId, a.Name))))).ToList();
    assessment.Status = "Complete";
    return Results.Ok(assessment.Results);
});

app.MapGet("/api/assessments/{assessmentId:guid}/report.csv", (Guid assessmentId, AssessmentStore store) =>
{
    var assessment = store.Get(assessmentId);
    if (assessment is null) return Results.NotFound();
    var lines = new[] { "Control ID,Requirement,Asset,Current Configuration,Expected Baseline,Status,Severity,Recommendation" }
        .Concat(assessment.Results.Select(r => string.Join(',', new[] { r.ControlId, r.Requirement, r.AssetName, r.CurrentConfiguration, r.ExpectedConfiguration, r.Status, r.Severity, r.Recommendation }.Select(Csv))));
    return Results.File(System.Text.Encoding.UTF8.GetBytes(string.Join(Environment.NewLine, lines)), "text/csv", "ot-gap-assessment.csv");
});
app.MapFallbackToFile("index.html");
app.Run();

static bool IsAllowed(IFormFile file, params string[] extensions) => file.Length > 0 && extensions.Contains(Path.GetExtension(file.FileName), StringComparer.OrdinalIgnoreCase);
static string Csv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";

public record LoginRequest(string Username, string Password);
public record LoginResponse(string Username, string Role, string Token);
public record CreateAssessmentRequest(string Name, string BaselineVersion);
public record Asset(string Name, string AssetType, string Model, string IpAddress, string Site, string? Port, string? Vlan, string Status);
public record BaselineRule(string ControlId, string AssetType, string Requirement, string ExpectedConfiguration, string MatchingLogic, string Severity, string Recommendation);
public record Baseline(Guid Id, string Version, DateTimeOffset CreatedAt, List<BaselineRule> Rules);
public record ParsedSwitchConfig(string AssetName, string? Hostname, List<string> Interfaces, List<string> Vlans, bool SshEnabled, bool TelnetEnabled, bool AaaEnabled, bool SnmpV3Enabled, bool NtpEnabled, bool LoggingEnabled, List<string> RawLines);
public record Finding(string ControlId, string Requirement, string AssetName, string CurrentConfiguration, string ExpectedConfiguration, string Status, string Severity, string Recommendation);
public class Assessment(Guid id, string name, string baselineVersion, DateTimeOffset createdAt, string status, List<Asset> assets, List<Finding> results)
{ public Guid Id { get; } = id; public string Name { get; } = name; public string BaselineVersion { get; } = baselineVersion; public DateTimeOffset CreatedAt { get; } = createdAt; public string Status { get; set; } = status; public List<Asset> Assets { get; set; } = assets; public List<Finding> Results { get; set; } = results; }
public class AssessmentStore { public List<Assessment> Assessments { get; } = []; public List<Baseline> Baselines { get; } = []; public Dictionary<(Guid, string), ParsedSwitchConfig> Configurations { get; } = []; public Assessment? Get(Guid id) => Assessments.SingleOrDefault(x => x.Id == id); }

public static class InventoryParser
{ public static async Task<List<Asset>> ParseAsync(IFormFile file) { using var reader = new StreamReader(file.OpenReadStream()); var rows = (await reader.ReadToEndAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries); return rows.Skip(1).Select(line => { var c = line.Split(',').Select(x => x.Trim()).ToArray(); var name = c.ElementAtOrDefault(0) ?? "Unknown"; var model = c.ElementAtOrDefault(2) ?? ""; var hint = string.Join(' ', c); var type = Regex.IsMatch(hint, "switch|cisco|hirschmann|ie-", RegexOptions.IgnoreCase) ? "Network Switch" : c.ElementAtOrDefault(1) ?? "Unclassified"; return new Asset(name, type, model, c.ElementAtOrDefault(3) ?? "", c.ElementAtOrDefault(4) ?? "", c.ElementAtOrDefault(5), c.ElementAtOrDefault(6), "Active"); }).ToList(); } }
public static class BaselineParser
{ public static async Task<List<BaselineRule>> ParseAsync(IFormFile file) { using var reader = new StreamReader(file.OpenReadStream()); var rows = (await reader.ReadToEndAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries); return rows.Skip(1).Select(line => { var c = line.Split(',').Select(x => x.Trim()).ToArray(); return new BaselineRule(c.ElementAtOrDefault(0) ?? "", c.ElementAtOrDefault(1) ?? "Network Switch", c.ElementAtOrDefault(2) ?? "", c.ElementAtOrDefault(3) ?? "", c.ElementAtOrDefault(4) ?? "contains", c.ElementAtOrDefault(5) ?? "Medium", c.ElementAtOrDefault(6) ?? ""); }).ToList(); } }
public static class SwitchConfigParser
{ public static ParsedSwitchConfig Parse(string assetName, string text) { var lines = text.Split('\n').Select(l => l.Trim()).ToList(); bool Has(string s) => lines.Any(l => l.Contains(s, StringComparison.OrdinalIgnoreCase)); return new(assetName, lines.FirstOrDefault(l => l.StartsWith("hostname ", StringComparison.OrdinalIgnoreCase))?.Split(' ', 2)[1], lines.Where(l => l.StartsWith("interface ", StringComparison.OrdinalIgnoreCase)).ToList(), lines.Where(l => l.StartsWith("vlan ", StringComparison.OrdinalIgnoreCase)).ToList(), Has("transport input ssh"), Has("transport input telnet"), Has("aaa new-model"), Has("snmp-server group") && Has(" v3"), Has("ntp server"), Has("logging host"), lines); } }
public static class ComparisonEngine
{ public static Finding Compare(BaselineRule rule, Asset asset, ParsedSwitchConfig? config) { if (config is null) return new(rule.ControlId, rule.Requirement, asset.Name, "Not detected", rule.ExpectedConfiguration, "Not Detected", rule.Severity, rule.Recommendation); var current = config.RawLines.FirstOrDefault(l => l.Contains(rule.ExpectedConfiguration, StringComparison.OrdinalIgnoreCase)); var match = current is not null; return new(rule.ControlId, rule.Requirement, asset.Name, current ?? "Not detected", rule.ExpectedConfiguration, match ? "Compliant" : "Non-Compliant", rule.Severity, rule.Recommendation); } }
