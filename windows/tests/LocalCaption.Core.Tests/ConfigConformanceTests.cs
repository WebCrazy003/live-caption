using System.Text.Json;
using System.Text.Json.Nodes;
using LocalCaption.Core.Data;

namespace LocalCaption.Core.Tests;

/// <summary>
/// The <c>testdata/config</c> half of the shared vectors. Split out from
/// <see cref="ConformanceTests"/> because each case needs its own scratch directory.
/// </summary>
public class ConfigConformanceTests
{
    [Fact]
    public void ConfigVectors()
    {
        var defaults = new Config();

        foreach (var (file, v) in Vectors.Load<ConfigVector>("config"))
        {
            var dir = Path.Combine(Path.GetTempPath(), $"lc-conformance-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var path = Path.Combine(dir, "config.json");

                // Encoded-shape assertions need no file on disk at all.
                if (v.ExpectEncodedContains is not null || v.ExpectEncodedOmits is not null)
                {
                    var json = JsonSerializer.Serialize(defaults);
                    foreach (var needle in v.ExpectEncodedContains ?? [])
                        Assert.True(json.Contains(needle, StringComparison.Ordinal),
                            $"{file}: encoded config should contain '{needle}'");
                    foreach (var needle in v.ExpectEncodedOmits ?? [])
                        Assert.False(json.Contains(needle, StringComparison.Ordinal),
                            $"{file}: encoded config should omit '{needle}'");
                    continue;
                }

                // Round-trip vectors build a config from overrides, write it, and reload it.
                if (v.RoundTripOverrides is { } overrides)
                {
                    var seed = new JsonObject();
                    foreach (var (dotted, value) in overrides)
                    {
                        var parts = dotted.Split('.');
                        if (parts.Length != 2) continue;
                        if (seed[parts[0]] is not JsonObject group)
                        {
                            group = [];
                            seed[parts[0]] = group;
                        }
                        group[parts[1]] = JsonNode.Parse(value.GetRawText());
                    }

                    var configured = JsonSerializer.Deserialize<Config>(seed.ToJsonString())!;
                    configured.Write(path);
                    var (reloaded, reloadRepaired) = Config.LoadOrRepair(path);
                    Assert.False(reloadRepaired, $"{file}: a config we just wrote must reload cleanly");
                    Assert.True(configured == reloaded, $"{file}: write→read must be the identity");
                    continue;
                }

                if (v.Input is { } input) File.WriteAllText(path, input, Files.Utf8NoBom);

                var (config, repaired) = Config.LoadOrRepair(path);

                if (v.ExpectRepaired is { } expectedRepaired)
                    Assert.True(expectedRepaired == repaired,
                        $"{file}: repaired flag, expected {expectedRepaired}, got {repaired}");

                if (v.ExpectBackupCount is { } expectedBackups)
                {
                    var backups = Directory.GetFiles(dir, "config.json.bak-*").Length;
                    Assert.True(expectedBackups == backups,
                        $"{file}: backup count, expected {expectedBackups}, got {backups}");
                }

                if (v.ExpectFileExists == true)
                    Assert.True(File.Exists(path), $"{file}: config should have been written");

                if (v.ExpectRewritten == true)
                {
                    var (onDisk, _) = Config.LoadOrRepair(path);
                    Assert.True(Config.CurrentSchemaVersion == onDisk.SchemaVersion,
                        $"{file}: migration must be persisted, not just returned");
                }

                if (v.ExpectReloadClean == true)
                {
                    var (_, repairedAgain) = Config.LoadOrRepair(path);
                    Assert.False(repairedAgain, $"{file}: the repaired file must itself reload cleanly");
                }

                foreach (var (keyPath, expected) in v.Expect ?? [])
                {
                    var actual = ValueAt(config, keyPath);
                    if (expected.ValueKind == JsonValueKind.String && expected.GetString() == "$default")
                        AssertSame(ValueAt(defaults, keyPath), actual, $"{file}: {keyPath} should be the default");
                    else
                        AssertSame(expected, actual, $"{file}: {keyPath}");
                }
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    /// <summary>Encode the config and read a dotted, snake_cased key path out of it.</summary>
    private static JsonElement ValueAt(Config config, string keyPath)
    {
        var node = JsonSerializer.SerializeToElement(config);
        foreach (var key in keyPath.Split('.'))
        {
            Assert.True(node.TryGetProperty(key, out var child),
                $"key path '{keyPath}' has no segment '{key}' in the encoded config");
            node = child;
        }
        return node;
    }

    private static void AssertSame(JsonElement expected, JsonElement actual, string message)
    {
        // Compare by raw JSON text so 30 and 30.0 do not read as different, and so a type
        // mismatch (say a string where a number belongs) surfaces here rather than silently.
        var e = Normalise(expected);
        var a = Normalise(actual);
        Assert.True(e == a, $"{message}: expected {e}, got {a}");
    }

    private static string Normalise(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Number => element.GetDouble().ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        JsonValueKind.String => element.GetString() ?? "",
        _ => element.GetRawText(),
    };
}
