using System.Text.Json;
using System.Text.Json.Serialization;
using DualSenseTweaker.ControllerBridge;
using DualSenseTweaker.MacroGamer.Core;

namespace DualSenseTweaker.MacroGamer.Scripting;

public enum ScriptOperation { Load, Validate, Invoke }
public sealed record ScriptRequest
{
    public ScriptOperation Operation { get; init; }
    public string Source { get; init; } = "";
    public string Name { get; init; } = "script.lua";
    public int HandlerIndex { get; init; }
    public PadFrame? Input { get; init; }
    public Dictionary<string, double> Variables { get; init; } = [];
}
public sealed record ScriptBinding(int Index, TriggerKind Kind, PadButtons Buttons);
public sealed record ScriptResponse
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public MacroAction[] Actions { get; init; } = [];
    public ScriptBinding[] Bindings { get; init; } = [];
    public string[] Logs { get; init; } = [];
    public bool CancelRequested { get; init; }
    public int InstructionSlices { get; init; }
    public long ManagedHeapLimitBytes { get; init; }
}
public static class ScriptWire
{
    public const int MaxLineCharacters = 512_000;
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 24, Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };
    public static async ValueTask<string?> ReadLineAsync(TextReader reader, CancellationToken token)
    {
        // StreamReader.ReadLineAsync would allocate an unbounded line before a quota check.
        var builder = new System.Text.StringBuilder(); var character = new char[1];
        while (true)
        {
            int count = await reader.ReadAsync(character.AsMemory(), token);
            if (count == 0) return builder.Length == 0 ? null : throw new EndOfStreamException("Truncated script protocol line.");
            if (character[0] == '\n') return builder.ToString();
            if (character[0] == '\r') continue;
            if (builder.Length >= MaxLineCharacters) throw new InvalidDataException("Script protocol line exceeds limit.");
            builder.Append(character[0]);
        }
    }
}
