using System.Text.Json.Serialization;

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(MonitorJson))]
[JsonSerializable(typeof(IEnumerable<MonitorJson>))]
internal sealed partial class CliJson : JsonSerializerContext;
