using System.Text.Json;
using DualSenseTweaker.MacroGamer.Scripting;

// No filesystem, native input, networking, process API or CLR objects are registered with Lua.
// Only the authenticated parent process owns these anonymous standard-I/O handles.
var sandbox = new LuaSandbox();
while (true)
{
    string? line;
    try { line = await ScriptWire.ReadLineAsync(Console.In, CancellationToken.None); }
    catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException) { return 2; }
    if (line is null) return 0;
    ScriptResponse response;
    try
    {
        var request = JsonSerializer.Deserialize<ScriptRequest>(line, ScriptWire.Options) ?? throw new InvalidDataException("Missing script request.");
        response = sandbox.Process(request);
    }
    catch (Exception ex) when (ex is JsonException or InvalidDataException)
    { response = new() { Error = ex.Message }; }
    if (response.Error is { Length: > 8192 }) response = response with { Error = response.Error[..8192] };
    response = response with { ManagedHeapLimitBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes };
    await Console.Out.WriteLineAsync(JsonSerializer.Serialize(response, ScriptWire.Options));
    await Console.Out.FlushAsync();
}
