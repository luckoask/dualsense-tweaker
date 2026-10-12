using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using DualSenseTweaker.MacroGamer.Core;

namespace DualSenseTweaker.MacroGamer.Scripting;

public sealed class ScriptWorkerClient(string workerPath, DiagnosticLog? diagnostics = null, string? dotnetExecutable = null) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private WorkerJob? _job;
    private Task? _errorPump;
    private bool _disposed;
    public bool IsRunning
    {
        get { var process = _process; if (process is null) return false; try { return !process.HasExited; } catch (InvalidOperationException) { return false; } }
    }
    public string MemoryIsolationMode => OperatingSystem.IsWindows() ? "256 MiB Windows Job process limit + 128 MiB managed heap" : "128 MiB managed heap; native process memory limit requires Windows";
    public async Task<ScriptResponse> RequestAsync(ScriptRequest request, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_process is null) Start();
            if (_process!.HasExited) throw new InvalidOperationException($"Script worker exited ({_process.ExitCode}).");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(2000);
            string line = JsonSerializer.Serialize(request, ScriptWire.Options);
            if (line.Length > ScriptWire.MaxLineCharacters) throw new InvalidDataException("Script request exceeds limit.");
            await _process.StandardInput.WriteLineAsync(line.AsMemory(), deadline.Token);
            await _process.StandardInput.FlushAsync(deadline.Token);
            string response = await ScriptWire.ReadLineAsync(_process.StandardOutput, deadline.Token) ?? throw new EndOfStreamException("Script worker stopped before replying.");
            var result = JsonSerializer.Deserialize<ScriptResponse>(response, ScriptWire.Options) ?? throw new InvalidDataException("Invalid worker response.");
            Validate(result); return result;
        }
        catch (OperationCanceledException)
        {
            await StopProcessAsync();
            diagnostics?.Write(token.IsCancellationRequested ? "Script worker canceled." : "Script worker exceeded the two-second request limit.", "Error");
            if (token.IsCancellationRequested) throw;
            throw new TimeoutException("Script worker exceeded the two-second request limit and was terminated.");
        }
        catch
        { await StopProcessAsync(); throw; }
        finally { _gate.Release(); }
    }
    private void Start()
    {
        if (!File.Exists(workerPath)) throw new FileNotFoundException("Script worker is missing from the Macro Gamer installation.", workerPath);
        string executable = workerPath;
        bool dll = workerPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        if (dll) executable = dotnetExecutable ?? Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(workerPath))!,
        };
        if (dll) start.ArgumentList.Add(Path.GetFullPath(workerPath));
        start.Environment["DOTNET_GCHeapHardLimit"] = "0x08000000";
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        _process = Process.Start(start) ?? throw new InvalidOperationException("Script worker did not start.");
        // Never send a user script until native resource enforcement has succeeded.
        if (OperatingSystem.IsWindows()) _job = new(_process);
        _errorPump = PumpErrorsAsync(_process.StandardError);
    }
    private async Task PumpErrorsAsync(StreamReader reader)
    {
        int remaining = 8192; var buffer = new char[1024];
        while (true)
        {
            int count = await reader.ReadAsync(buffer);
            if (count == 0) return;
            int bounded = Math.Min(count, remaining);
            if (bounded > 0) diagnostics?.Write("Script worker: " + new string(buffer, 0, bounded), "Error");
            remaining -= bounded;
        }
    }
    private static void Validate(ScriptResponse result)
    {
        if (result.Actions is null || result.Actions.Length > 2048 || result.Bindings is null || result.Bindings.Length > 64 ||
            result.Logs is null || result.Logs.Length > 128 || result.Logs.Any(s => s is null || s.Length > 2048) ||
            result.Error?.Length > 8192 || !result.Success && string.IsNullOrWhiteSpace(result.Error) ||
            result.ManagedHeapLimitBytes is < 1 or > 134_217_728)
            throw new InvalidDataException("Worker response exceeds contract limits.");
        foreach (var action in result.Actions) (action ?? throw new InvalidDataException("Null worker action.")).Validate();
        foreach (var binding in result.Bindings)
            if (binding is null || binding.Index is < 0 or >= 64 || binding.Kind is not (TriggerKind.Press or TriggerKind.Release) ||
                binding.Buttons == 0 || ((uint)binding.Buttons & ~((1u << 23) - 1)) != 0)
                throw new InvalidDataException("Invalid worker trigger binding.");
        if (result.Bindings.Select(b => b.Index).Distinct().Count() != result.Bindings.Length)
            throw new InvalidDataException("Duplicate worker trigger binding.");
    }
    private async Task StopProcessAsync()
    {
        if (_process is null) return;
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
            if (_errorPump is not null) await _errorPump;
        }
        finally { _job?.Dispose(); _job = null; _process.Dispose(); _process = null; _errorPump = null; }
    }
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try { if (_disposed) return; _disposed = true; await StopProcessAsync(); }
        finally { _gate.Release(); }
    }
}
