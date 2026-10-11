using System.Text.Json;
using System.Text.Json.Serialization;
using DualSenseTweaker.Core.Diagnostics;
using DualSenseTweaker.Core.State;

namespace DualSenseTweaker.Core.Profiles;

/// <summary>
/// Profiles as JSON files in &lt;root&gt;/profiles plus &lt;root&gt;/settings.json for app-wide settings.
/// Manual rumble is never persisted.
/// </summary>
public sealed class ProfileStore
{
    public const int FormatVersion = 2;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        // Compile-time metadata: loading profiles at startup needs no reflection.
        TypeInfoResolver = ProfileJsonContext.Default,
    };

    private readonly string _profilesDirectory;
    private readonly string _settingsFile;

    public ProfileStore(string rootDirectory)
    {
        _profilesDirectory = Path.Combine(rootDirectory, "profiles");
        _settingsFile = Path.Combine(rootDirectory, "settings.json");
    }

    public static string DefaultRootDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DualSenseTweaker");

    /// <summary>All stored profiles; missing built-in profiles (first run, or deleted files) are recreated.</summary>
    public IReadOnlyList<Profile> LoadAll()
    {
        var profiles = new List<Profile>();
        if (Directory.Exists(_profilesDirectory))
        {
            foreach (var path in Directory.EnumerateFiles(_profilesDirectory, "*.json"))
            {
                if (Read(path) is { } profile && !profiles.Any(p => SameName(p.Name, profile.Name)))
                    profiles.Add(profile);
            }
        }

        foreach (var preset in ProfilePresets.Defaults)
        {
            bool kindPresent = preset.IsBuiltIn
                ? profiles.Any(p => p.Kind == preset.Kind)
                : profiles.Any(p => p.Kind == ProfileKind.Custom);
            if (!kindPresent && !profiles.Any(p => SameName(p.Name, preset.Name)))
            {
                profiles.Add(preset);
                TrySave(preset);
            }
        }

        return [.. profiles.OrderBy(p => p.Kind == ProfileKind.Custom).ThenBy(p => p.Kind).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <exception cref="IOException">The file could not be written.</exception>
    public void Save(Profile profile)
    {
        Directory.CreateDirectory(_profilesDirectory);
        WriteFile(PathFor(profile.Name), ToFile(profile));
    }

    public bool Delete(string name)
    {
        var path = PathFor(name);
        if (!File.Exists(path))
            return false;
        File.Delete(path);
        return true;
    }

    /// <summary>Reads a profile file from anywhere. Imported profiles are always custom profiles.</summary>
    /// <exception cref="InvalidDataException">The file is not a valid profile.</exception>
    public static Profile Import(string path)
    {
        Profile? profile;
        try
        {
            profile = Parse(File.ReadAllText(path));
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(L.Format($"Súbor nie je platný profil: {ex.Message}"), ex);
        }
        if (profile is null)
            throw new InvalidDataException(L.Text("Súbor nie je platný profil."));
        var name = string.IsNullOrWhiteSpace(profile.Name) ? Path.GetFileNameWithoutExtension(path) : profile.Name;
        return profile with { Name = name.Trim(), Kind = ProfileKind.Custom };
    }

    public static void Export(Profile profile, string path) => WriteFile(path, ToFile(profile));

    public AppSettings LoadAppSettings()
    {
        if (!File.Exists(_settingsFile))
            return new AppSettings();
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_settingsFile), JsonOptions) ?? new AppSettings();
            return settings with
            {
                Refresh = (settings.Refresh ?? new RefreshOptions()).Normalize(),
                Preferences = (settings.Preferences ?? new AppPreferences()).Normalize(),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Warn(L.Format($"Nastavenia sa nepodarilo načítať: {ex.Message}"));
            return new AppSettings();
        }
    }

    public void SaveAppSettings(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsFile)!);
            WriteFile(_settingsFile, settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn(L.Format($"Nastavenia sa nepodarilo uložiť: {ex.Message}"));
        }
    }

    public static bool SameName(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.CurrentCultureIgnoreCase);

    private static ProfileFile ToFile(Profile profile) =>
        new(FormatVersion, profile.Name.Trim(), profile.Kind, profile.Settings.WithoutRumble());

    private static Profile? Parse(string json)
    {
        var file = JsonSerializer.Deserialize<ProfileFile>(json, JsonOptions);
        if (file is null || string.IsNullOrWhiteSpace(file.Name))
            return null;
        var kind = Enum.IsDefined(file.Kind) ? file.Kind : ProfileKind.Custom;
        var settings = (file.Settings ?? ProfilePresets.For(kind)).Normalize().WithoutRumble();
        return new Profile(file.Name.Trim(), kind, settings);
    }

    private Profile? Read(string path)
    {
        try
        {
            return Parse(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Warn(L.Format($"Profil {path} sa nepodarilo načítať: {ex.Message}"));
            return null;
        }
    }

    private void TrySave(Profile profile)
    {
        try
        {
            Save(profile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn(L.Format($"Profil {profile.Name} sa nepodarilo uložiť: {ex.Message}"));
        }
    }

    private string PathFor(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
            throw new ArgumentException(L.Text("Názov profilu nesmie byť prázdny."), nameof(name));
        var invalid = Path.GetInvalidFileNameChars();
        var fileName = new string(trimmed.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        return Path.Combine(_profilesDirectory, fileName + ".json");
    }

    private static void WriteFile<T>(string path, T value)
    {
        // Write to a temp file first so a crash never leaves a half-written file behind.
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }

    internal sealed record ProfileFile(int Format, string Name, ProfileKind Kind, ProfileSettings? Settings);
}

[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(ProfileStore.ProfileFile))]
internal sealed partial class ProfileJsonContext : JsonSerializerContext;
