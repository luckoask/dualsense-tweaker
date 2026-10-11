using System.Numerics;

namespace DualSenseTweaker.Core.Motion;

/// <summary>What the aim test draws: arena in mouse counts, crosshair, target, trail and score.</summary>
/// <param name="ShotCount">Increments on every shot so the view can flash.</param>
public sealed record AimTestState(
    Vector2 Arena,
    Vector2 Crosshair,
    Vector2 Target,
    float TargetRadius,
    IReadOnlyList<Vector2> Trail,
    int Hits,
    int Misses,
    double AverageHitSeconds,
    int ShotCount,
    bool LastShotHit);

/// <summary>
/// Gyro aim test: the crosshair moves by the same mouse counts a game would receive, R2 shoots
/// at the current target. Measures hits, accuracy and the average time to hit.
/// </summary>
public sealed class AimTrainer
{
    public static readonly Vector2 ArenaSize = new(1040, 400);
    private const float Radius = 26;
    private const float MinSpawnDistance = 180;
    private const int TrailLength = 40;

    private readonly Random _random;
    private readonly Queue<Vector2> _trail = new();
    private Vector2 _crosshair = ArenaSize / 2;
    private Vector2 _target;
    private double _targetAge;
    private double _hitSecondsSum;
    private int _hits, _misses, _shots;
    private bool _lastHit;

    public AimTrainer(int? seed = null)
    {
        _random = seed is { } s ? new Random(s) : new Random();
        _target = SpawnTarget();
    }

    public AimTestState State => new(ArenaSize, _crosshair, _target, Radius, [.. _trail], _hits, _misses,
        _hits == 0 ? 0 : _hitSecondsSum / _hits, _shots, _lastHit);

    /// <param name="movement">Mouse counts since the last update (positive X right, Y down).</param>
    /// <param name="shots">R2 pulls since the last update.</param>
    /// <param name="seconds">Time since the last update.</param>
    public void Update(Vector2 movement, int shots, double seconds)
    {
        _targetAge += Math.Max(seconds, 0);
        _crosshair = Vector2.Clamp(_crosshair + movement, Vector2.Zero, ArenaSize);
        _trail.Enqueue(_crosshair);
        while (_trail.Count > TrailLength)
            _trail.Dequeue();

        for (int i = 0; i < shots; i++)
            Shoot();
    }

    public void Recenter()
    {
        _crosshair = ArenaSize / 2;
        _trail.Clear();
    }

    public void ResetScore()
    {
        _hits = _misses = 0;
        _hitSecondsSum = 0;
        _targetAge = 0;
        _target = SpawnTarget();
    }

    private void Shoot()
    {
        _shots++;
        _lastHit = Vector2.Distance(_crosshair, _target) <= Radius;
        if (!_lastHit)
        {
            _misses++;
            return;
        }
        _hits++;
        _hitSecondsSum += _targetAge;
        _targetAge = 0;
        _target = SpawnTarget();
    }

    private Vector2 SpawnTarget()
    {
        var margin = new Vector2(Radius * 2);
        Vector2 candidate = ArenaSize / 2;
        for (int attempt = 0; attempt < 20; attempt++)
        {
            candidate = margin + new Vector2((float)_random.NextDouble(), (float)_random.NextDouble()) * (ArenaSize - margin * 2);
            if (Vector2.Distance(candidate, _crosshair) >= MinSpawnDistance)
                break;
        }
        return candidate;
    }
}
