namespace DualSenseTweaker.Core.State;

/// <summary>
/// Thread-safe holder of an immutable record. Every effective change bumps a version number so
/// readers (the device writer) can tell cheaply whether there is anything new.
/// </summary>
public class VersionedState<T> where T : class, IEquatable<T>
{
    private readonly Lock _gate = new();
    private readonly Func<T, T> _normalize;
    private T _current;
    private long _version;

    public VersionedState(T initial, Func<T, T>? normalize = null)
    {
        _normalize = normalize ?? (value => value);
        _current = _normalize(initial);
    }

    /// <summary>Raised after every effective change, on the thread that made it.</summary>
    public event EventHandler? Changed;

    public T Current => Volatile.Read(ref _current);

    public (T Value, long Version) Read()
    {
        lock (_gate)
            return (_current, _version);
    }

    /// <returns>True if the value actually changed.</returns>
    public bool Update(Func<T, T> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_gate)
        {
            var next = _normalize(change(_current));
            if (next.Equals(_current))
                return false;
            Volatile.Write(ref _current, next);
            _version++;
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool Replace(T value) => Update(_ => value);
}

/// <summary>The active profile's settings as edited in the UI.</summary>
public sealed class SettingsState(ProfileSettings? initial = null)
    : VersionedState<ProfileSettings>(initial ?? new ProfileSettings(), s => s.Normalize())
{
    public bool UpdateOutput(Func<OutputSnapshot, OutputSnapshot> change) =>
        Update(s => s with { Output = change(s.Output) });

    public bool UpdateReactive(Func<AudioReactiveSettings, AudioReactiveSettings> change) =>
        Update(s => s with { Reactive = change(s.Reactive) });

    public bool UpdateHaptics(Func<HapticsSettings, HapticsSettings> change) =>
        Update(s => s with { Haptics = change(s.Haptics) });
}

/// <summary>App-wide periodic refresh options.</summary>
public sealed class RefreshState(RefreshOptions? initial = null)
    : VersionedState<RefreshOptions>(initial ?? new RefreshOptions(), o => o.Normalize());

/// <summary>Runtime values from the audio engine, layered over the profile's static output.</summary>
public sealed record LiveOverlay
{
    public static LiveOverlay None { get; } = new();

    /// <summary>Final lightbar color (brightness applied); null = use the profile color.</summary>
    public RgbColor? Lightbar { get; init; }
    public byte RumbleHeavy { get; init; }
    public byte RumbleSoft { get; init; }

    /// <summary>
    /// True while audio haptics play over USB. Reports must then carry no rumble-emulation bits,
    /// otherwise the controller switches its actuators from audio haptics to rumble emulation.
    /// </summary>
    public bool AudioHapticsActive { get; init; }
}

public sealed class LiveOutput() : VersionedState<LiveOverlay>(LiveOverlay.None);
