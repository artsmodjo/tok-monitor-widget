using System;
using System.Text.Json.Serialization;

namespace Artsmodjo.Widget;

internal sealed record ReadyStatus(
    [property: JsonPropertyName("ready")] bool Ready,
    [property: JsonPropertyName("processId")] int ProcessId,
    [property: JsonPropertyName("interactiveWidget")] bool InteractiveWidget,
    [property: JsonPropertyName("trayRegistered")] bool TrayRegistered,
    [property: JsonPropertyName("programmaticTrayMenuOpened")] bool ProgrammaticTrayMenuOpened,
    [property: JsonPropertyName("readyAtUtc")] DateTimeOffset ReadyAtUtc);

[JsonSourceGenerationOptions(WriteIndented = false, PropertyNameCaseInsensitive = false)]
[JsonSerializable(typeof(ReadyStatus))]
internal partial class ReadyStatusJsonContext : JsonSerializerContext { }
