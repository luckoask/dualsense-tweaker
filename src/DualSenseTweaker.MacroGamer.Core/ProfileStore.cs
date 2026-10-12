using System.Text.Json;
using System.Text.Json.Serialization;
using DualSenseTweaker.ControllerBridge;

namespace DualSenseTweaker.MacroGamer.Core;

public sealed record ScriptDefinition
{
    [JsonRequired] public Guid Id { get; init; } = Guid.NewGuid();
    [JsonRequired] public string Name { get; init; } = "New script";
    public bool Enabled { get; init; }
    [JsonRequired] public string Source { get; init; } = "-- Write Lua here";
    public void Validate()
    {
        if (Id == Guid.Empty || string.IsNullOrWhiteSpace(Name) || Name.Length > 128 || Source is null || Source.Length > 64_000)
            throw new InvalidDataException("Invalid script definition.");
    }
}

public sealed record MacroProfile
{
    [JsonRequired] public int FormatVersion { get; init; } = 1;
    [JsonRequired] public Guid Id { get; init; } = Guid.NewGuid();
    [JsonRequired] public string Name { get; init; } = "Default";
    public string? ControllerId { get; init; }
    public string? GameExecutable { get; init; }
    public OutputBackendKind Backend { get; init; } = OutputBackendKind.Diagnostic;
    [JsonRequired] public MacroDefinition[] Macros { get; init; } = [];
    public ScriptDefinition[] Scripts { get; init; } = [];
    public InputRecording[] Recordings { get; init; } = [];
    public Dictionary<string, double> Variables { get; init; } = [];
    public void Validate()
    {
        if (FormatVersion != 1 || Id == Guid.Empty || string.IsNullOrWhiteSpace(Name) || Name.Length > 128 ||
            ControllerId?.Length > 1024 || GameExecutable?.Length > 1024 || !Enum.IsDefined(Backend) ||
            Macros is null || Macros.Length > 256 || Scripts is null || Scripts.Length > 64 ||
            Recordings is null || Recordings.Length > 16 || Variables is null || Variables.Count > 64)
            throw new InvalidDataException("Invalid or unsupported profile format.");
        foreach (var macro in Macros) (macro ?? throw new InvalidDataException("Null macro.")).Validate();
        foreach (var script in Scripts) (script ?? throw new InvalidDataException("Null script.")).Validate();
        foreach (var recording in Recordings) (recording ?? throw new InvalidDataException("Null recording.")).Validate();
        if (Macros.Select(m => m.Id).Distinct().Count() != Macros.Length || Scripts.Select(s => s.Id).Distinct().Count() != Scripts.Length)
            throw new InvalidDataException("Duplicate macro/script identities.");
        foreach (var (key, value) in Variables)
            if (string.IsNullOrWhiteSpace(key) || key.Length > 64 || !double.IsFinite(value)) throw new InvalidDataException("Invalid profile variable.");
    }
    public MacroProfile Duplicate(string name) => this with
    {
        Id = Guid.NewGuid(), Name = name,
        Macros = Macros.Select(m => m.Snapshot() with { Id = Guid.NewGuid() }).ToArray(),
        Scripts = Scripts.Select(s => s with { Id = Guid.NewGuid() }).ToArray(),
        Variables = new(Variables), Recordings = Recordings.Select(r => r with { Points = r.Points.ToArray() }).ToArray(),
    };
    public static MacroProfile Default() => new()
    {
        Macros = [new MacroDefinition
        {
            Name = "R1 → E tap", Enabled = false, Trigger = new() { Buttons = PadButtons.R1 },
            Actions = [new() { Kind = ActionKind.KeyTap, Code = "E", DurationMs = 60 }],
        }],
        Scripts = [new() { Name = "R1 tap", Enabled = false, Source = "on_press(\"R1\", function()\n    key_tap(\"E\", 60)\nend)" }],
    };
}

public sealed record CompanionSettings
{
    public int FormatVersion { get; init; } = 1;
    public Guid? SelectedProfileId { get; init; }
    public string Language { get; init; } = "SK";
    public string Theme { get; init; } = "Dark";
    public bool AutoGameProfiles { get; init; }
    public void Validate()
    {
        if (FormatVersion != 1 || Language is not ("SK" or "EN" or "CZ") || Theme is not ("Dark" or "Light"))
            throw new InvalidDataException("Invalid companion settings.");
    }
}

public sealed class MacroProfileStore(string root, DiagnosticLog? diagnostics = null)
{
    public const int MaxFileBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 32,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };
    private readonly Lock _gate = new();
    public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DualSenseTweaker", "MacroGamer");
    public string Root => root;
    public void Save(MacroProfile profile) { profile.Validate(); lock (_gate) WriteAtomic(PathFor(profile.Id), profile); }
    public void Delete(Guid id) { if (id == Guid.Empty) throw new ArgumentException("Invalid profile identity."); lock (_gate) File.Delete(PathFor(id)); }
    public IReadOnlyList<MacroProfile> LoadAll()
    {
        lock (_gate)
        {
            var profiles = new List<MacroProfile>();
            string folder = Path.Combine(root, "profiles");
            if (Directory.Exists(folder))
            {
                foreach (var path in Directory.EnumerateFiles(folder, "*.json").Take(257))
                {
                    if (profiles.Count >= 256) { diagnostics?.Write("Profile count limit reached.", "Warning"); break; }
                    try
                    {
                        var profile = Import(path);
                        if (!profiles.Any(p => p.Id == profile.Id)) profiles.Add(profile);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
                    { diagnostics?.Write($"Profile {Path.GetFileName(path)} rejected: {ex.Message}", "Error"); }
                }
            }
            if (profiles.Count == 0) { var initial = MacroProfile.Default(); Save(initial); profiles.Add(initial); }
            return profiles.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        }
    }
    public CompanionSettings LoadSettings()
    {
        var path = Path.Combine(root, "settings.json");
        if (!File.Exists(path)) return new();
        try { var value = Read<CompanionSettings>(path); value.Validate(); return value; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        { diagnostics?.Write($"Settings rejected: {ex.Message}", "Error"); return new(); }
    }
    public void SaveSettings(CompanionSettings settings) { settings.Validate(); lock (_gate) WriteAtomic(Path.Combine(root, "settings.json"), settings); }
    public static MacroProfile Import(string path)
    {
        try { var result = Read<MacroProfile>(path); result.Validate(); return result; }
        catch (JsonException ex) { throw new InvalidDataException($"Malformed profile: {ex.Message}", ex); }
    }
    public static void Export(MacroProfile profile, string path) { profile.Validate(); WriteAtomic(path, profile); }
    public static MacroDefinition ImportMacro(string path) { var result = Read<MacroDefinition>(path); result.Validate(); return result; }
    public static void ExportMacro(MacroDefinition macro, string path) { macro.Validate(); WriteAtomic(path, macro); }
    private string PathFor(Guid id) => Path.Combine(root, "profiles", id.ToString("N") + ".json");
    private static T Read<T>(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaxFileBytes) throw new InvalidDataException("File exceeds import limit.");
        return JsonSerializer.Deserialize<T>(stream, Options) ?? throw new InvalidDataException("Empty JSON document.");
    }
    private static void WriteAtomic<T>(string path, T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Options);
        if (bytes.Length > MaxFileBytes) throw new InvalidDataException("File exceeds persistence limit.");
        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        string temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { stream.Write(bytes); stream.Flush(flushToDisk: true); }
            File.Move(temporary, full, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
