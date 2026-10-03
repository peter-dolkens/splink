using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpLink.Fronius;

/// <summary>Bridge configuration: which Fronius inverters to poll, by name.</summary>
public sealed class BridgeConfig
{
    [JsonPropertyName("fronius")]
    public List<FroniusEntry> Fronius { get; set; } = [];

    public sealed class FroniusEntry
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("host")] public string Host { get; set; } = "";
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static BridgeConfig Load(string path)
    {
        try
        {
            var config = JsonSerializer.Deserialize<BridgeConfig>(File.ReadAllText(path), Options)
                         ?? throw new FroniusException($"{path}: empty configuration");
            foreach (var e in config.Fronius)
                if (string.IsNullOrWhiteSpace(e.Name) || string.IsNullOrWhiteSpace(e.Host))
                    throw new FroniusException($"{path}: every fronius entry needs both 'name' and 'host'");
            var duplicate = config.Fronius.GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
            if (duplicate is not null)
                throw new FroniusException($"{path}: duplicate fronius name '{duplicate.Key}'");
            return config;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            throw new FroniusException($"{path}: {ex.Message}", ex);
        }
    }

    public IReadOnlyList<FroniusInverterConfig> ToInverters() =>
        Fronius.Select(e => new FroniusInverterConfig(e.Name, e.Host)).ToList();

    /// <summary>Parses the inline form: <c>name=host,name=host</c>.</summary>
    public static IReadOnlyList<FroniusInverterConfig> ParseInline(string spec)
    {
        var list = new List<FroniusInverterConfig>();
        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var bits = part.Split('=', 2);
            if (bits.Length != 2 || bits[0].Length == 0 || bits[1].Length == 0)
                throw new FroniusException($"--fronius expects name=host pairs, got '{part}'");
            list.Add(new FroniusInverterConfig(bits[0], bits[1]));
        }
        return list;
    }
}
