using DualSenseTweaker.Core.State;

namespace DualSenseTweaker.Core.Device;

/// <summary>
/// Builds the 11-byte adaptive trigger blocks (mode byte + 10 parameters) of the output report.
/// Layouts follow the public reverse engineering in Nielk1's TriggerEffectGenerator (MIT),
/// which nowrep/dualsensectl implements the same way.
/// </summary>
public static class TriggerEffectFactory
{
    public const int Size = 11;

    public const byte TypeOff = 0x05;
    public const byte TypeFeedback = 0x21;
    public const byte TypeWeapon = 0x25;
    public const byte TypeVibration = 0x26;

    /// <returns>The effect block, or null for <see cref="TriggerMode.Game"/> (leave the trigger alone).</returns>
    public static byte[]? Build(TriggerEffect effect)
    {
        var e = effect.Normalize();
        return e.Mode switch
        {
            TriggerMode.Game => null,
            TriggerMode.Resistance => Resistance(e.StartPosition, e.Strength),
            TriggerMode.Section => Section(e.StartPosition, e.EndPosition, e.Strength),
            TriggerMode.Vibration => Vibration(e.StartPosition, e.Amplitude, e.Frequency),
            _ => Off(),
        };
    }

    public static byte[] Off()
    {
        var block = new byte[Size];
        block[0] = TypeOff;
        return block;
    }

    /// <summary>Constant resistance from <paramref name="position"/> (0–9) to the end, strength 1–8.</summary>
    public static byte[] Resistance(int position, int strength)
    {
        if (position is < 0 or > 9)
            throw new ArgumentOutOfRangeException(nameof(position), L.Text("Pozícia musí byť 0–9."));
        if (strength is < 0 or > 8)
            throw new ArgumentOutOfRangeException(nameof(strength), L.Text("Sila musí byť 0–8."));
        if (strength == 0)
            return Off();

        var (activeZones, forceZones) = PackZones(position, strength);
        var block = new byte[Size];
        block[0] = TypeFeedback;
        WriteZones(block, activeZones, forceZones);
        return block;
    }

    /// <summary>
    /// Resistance between <paramref name="start"/> (2–7) and <paramref name="end"/> (start+1–8)
    /// that gives way with a click, like a gun trigger.
    /// </summary>
    public static byte[] Section(int start, int end, int strength)
    {
        if (start is < 2 or > 7)
            throw new ArgumentOutOfRangeException(nameof(start), L.Text("Začiatok musí byť 2–7."));
        if (end > 8 || end <= start)
            throw new ArgumentOutOfRangeException(nameof(end), L.Text("Koniec musí byť od začiatku+1 do 8."));
        if (strength is < 0 or > 8)
            throw new ArgumentOutOfRangeException(nameof(strength), L.Text("Sila musí byť 0–8."));
        if (strength == 0)
            return Off();

        ushort startAndStopZones = (ushort)((1 << start) | (1 << end));
        var block = new byte[Size];
        block[0] = TypeWeapon;
        block[1] = (byte)startAndStopZones;
        block[2] = (byte)(startAndStopZones >> 8);
        block[3] = (byte)(strength - 1);
        return block;
    }

    /// <summary>Vibration from <paramref name="position"/> (0–9), amplitude 1–8, frequency in Hz.</summary>
    public static byte[] Vibration(int position, int amplitude, int frequency)
    {
        if (position is < 0 or > 9)
            throw new ArgumentOutOfRangeException(nameof(position), L.Text("Pozícia musí byť 0–9."));
        if (amplitude is < 0 or > 8)
            throw new ArgumentOutOfRangeException(nameof(amplitude), L.Text("Amplitúda musí byť 0–8."));
        if (frequency is < 0 or > 255)
            throw new ArgumentOutOfRangeException(nameof(frequency), L.Text("Frekvencia musí byť 0–255 Hz."));
        if (amplitude == 0 || frequency == 0)
            return Off();

        var (activeZones, amplitudeZones) = PackZones(position, amplitude);
        var block = new byte[Size];
        block[0] = TypeVibration;
        WriteZones(block, activeZones, amplitudeZones);
        block[9] = (byte)frequency;
        return block;
    }

    /// <summary>Every zone from <paramref name="position"/> on is active with the same 3-bit value (force-1).</summary>
    private static (ushort ActiveZones, uint ValueZones) PackZones(int position, int force)
    {
        uint value = (uint)((force - 1) & 0x07);
        ushort activeZones = 0;
        uint valueZones = 0;
        for (int zone = position; zone < 10; zone++)
        {
            valueZones |= value << (3 * zone);
            activeZones |= (ushort)(1 << zone);
        }
        return (activeZones, valueZones);
    }

    private static void WriteZones(byte[] block, ushort activeZones, uint valueZones)
    {
        block[1] = (byte)activeZones;
        block[2] = (byte)(activeZones >> 8);
        block[3] = (byte)valueZones;
        block[4] = (byte)(valueZones >> 8);
        block[5] = (byte)(valueZones >> 16);
        block[6] = (byte)(valueZones >> 24);
    }
}
