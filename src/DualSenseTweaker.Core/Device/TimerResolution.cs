using System.Runtime.InteropServices;

namespace DualSenseTweaker.Core.Device;

/// <summary>
/// Raises the Windows timer resolution to 1 ms while the writer runs, so Thread.Sleep can honor
/// the 4 ms send interval instead of the default ~15.6 ms tick. Since Windows 10 2004 this only
/// affects the calling process.
/// </summary>
internal static class TimerResolution
{
    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint period);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint period);

    public static IDisposable Request1Ms()
    {
        if (!OperatingSystem.IsWindows() || timeBeginPeriod(1) != 0)
            return NoOp.Instance;
        return new Releaser();
    }

    private sealed class Releaser : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                timeEndPeriod(1);
        }
    }

    private sealed class NoOp : IDisposable
    {
        public static readonly NoOp Instance = new();

        public void Dispose() { }
    }
}
