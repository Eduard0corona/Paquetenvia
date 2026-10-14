using System.Reflection;
using System.Text.Json;
using Paqueteria.ArchitectureTests.Architecture;
using Paqueteria.Infrastructure.Database.Pooling;

namespace Paqueteria.ArchitectureTests;

/// <summary>
/// SCL-007: each process validates the <see cref="DatabasePoolOptions"/> budget against its own configuration only,
/// while the API opens the Application and Realtime pools and the Worker opens the Worker pool. That check bounds the
/// total of both processes only when they carry the same three values, so the committed <c>DatabasePools</c> sections
/// of the API and the Worker must be identical and equal to the code defaults. An override at deploy time follows the
/// operations rule (docs/operations/env-001-pilot/README.md) and stays a residual risk
/// (docs/development/scl-007-pool-per-role.md).
/// </summary>
public sealed class Scl007PoolBudgetConsistencyArchitectureTests
{
    private const string ApiHost = "src/Paqueteria.Api";
    private const string WorkerHost = "src/Paqueteria.Worker";
    private const string Settings = "appsettings.json";
    private const string Section = DatabasePoolOptions.SectionName;

    private const string Application = "'ApplicationMaximumPoolSize':10";
    private const string Realtime = "'RealtimeMaximumPoolSize':4";
    private const string Worker = "'WorkerMaximumPoolSize':10";
    private const string PoolDefaults = Application + "," + Realtime + "," + Worker;

    private static readonly string Committed = Json("{'Logging':{},'DatabasePools':{" + PoolDefaults + "}}");

    private static readonly JsonDocumentOptions ConfigurationJson = new()
    {
        // The configuration JSON provider accepts both, so the guard reads what the host reads.
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    [Fact]
    public void Api_and_worker_appsettings_carry_identical_pool_sizes_equal_to_the_code_defaults()
    {
        var drift = Drift(
            File.ReadAllText(TestRepository.GetPath($"{ApiHost}/{Settings}")),
            File.ReadAllText(TestRepository.GetPath($"{WorkerHost}/{Settings}")),
            CodeDefaults());

        Assert.True(drift.Count == 0, string.Join(Environment.NewLine, drift));
    }

    [Theory]
    [InlineData(ApiHost)]
    [InlineData(WorkerHost)]
    public void No_other_appsettings_profile_sets_the_pool_sizes(string host)
    {
        // A profile loaded in one process only would change that process's sizes alone, which neither can detect.
        var overriding = Directory
            .GetFiles(TestRepository.GetPath(host), "appsettings*.json")
            .Where(path => !string.Equals(Path.GetFileName(path), Settings, StringComparison.Ordinal))
            .Where(path => SetsPoolSizes(File.ReadAllText(path)))
            .Select(path => Path.GetFileName(path)!)
            .ToArray();

        Assert.True(
            overriding.Length == 0,
            $"{host}: {string.Join(", ", overriding)} set {Section}; only {Settings} may, identically in the API and " +
            "the Worker.");
    }

    [Theory]
    [InlineData("{'DatabasePools':{'WorkerMaximumPoolSize':22}}", true)]
    [InlineData("{'databasePools':{}}", true)]
    [InlineData("{'DatabasePools:WorkerMaximumPoolSize':22}", true)]
    [InlineData("{'Logging':{'DatabasePools':1},'ConnectionStrings':{}}", false)]
    public void The_profile_guard_sees_every_form_of_the_section(string profile, bool sets)
    {
        Assert.Equal(sets, SetsPoolSizes(Json(profile)));
    }

    [Fact]
    public void The_guard_compares_every_pool_size_of_the_options()
    {
        var defaults = CodeDefaults();

        Assert.Equal<string>(
            ["ApplicationMaximumPoolSize", "RealtimeMaximumPoolSize", "WorkerMaximumPoolSize"],
            defaults.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(DatabasePoolOptions.RuntimeConnectionBudget, defaults.Values.Sum());
    }

    [Fact]
    public void Identical_sections_at_the_code_defaults_pass_whatever_their_layout()
    {
        const string reordered = """
            {
              // comment
              "DatabasePools": {
                "WorkerMaximumPoolSize": 10,
                "ApplicationMaximumPoolSize": 10,
                "RealtimeMaximumPoolSize": 4,
              },
            }
            """;

        Assert.Empty(Drift(Committed, Committed, CodeDefaults()));
        Assert.Empty(Drift(Committed, reordered, CodeDefaults()));
    }

    public static TheoryData<string, string, string, string> Drifts() => new()
    {
        {
            "the review finding: the Worker lowers the sizes it does not open and raises its own",
            Committed,
            Pools("'ApplicationMaximumPoolSize':1,'RealtimeMaximumPoolSize':1,'WorkerMaximumPoolSize':22"),
            "DatabasePools:WorkerMaximumPoolSize is 10 in the API and 22 in the Worker"
        },
        {
            "identical in both processes but not the code defaults",
            Pools("'ApplicationMaximumPoolSize':8," + Realtime + ",'WorkerMaximumPoolSize':8"),
            Pools("'ApplicationMaximumPoolSize':8," + Realtime + ",'WorkerMaximumPoolSize':8"),
            "API: DatabasePools:ApplicationMaximumPoolSize is 8; the code default is 10"
        },
        {
            "a size missing in one process",
            Committed,
            Pools(Application + "," + Realtime),
            "Worker: DatabasePools:WorkerMaximumPoolSize is missing"
        },
        {
            "a key that is not a pool size",
            Committed,
            Pools(PoolDefaults + ",'MinimumPoolSize':0"),
            "Worker: DatabasePools:MinimumPoolSize is not a pool size"
        },
        {
            "a size written as a string",
            Committed,
            Pools(Application + "," + Realtime + ",'WorkerMaximumPoolSize':'10'"),
            "Worker: DatabasePools:WorkerMaximumPoolSize must be a whole number"
        },
        {
            "a size repeated, where the last one wins",
            Committed,
            Pools(PoolDefaults + ",'WorkerMaximumPoolSize':22"),
            "Worker: DatabasePools:WorkerMaximumPoolSize must appear once, spelled exactly"
        },
        {
            "a size spelled in another case",
            Committed,
            Pools(Application + "," + Realtime + ",'workerMaximumPoolSize':10"),
            "Worker: DatabasePools:WorkerMaximumPoolSize must appear once, spelled exactly"
        },
        {
            "no section",
            Committed,
            Json("{'Logging':{}}"),
            "Worker: appsettings.json must have exactly one DatabasePools section"
        },
        {
            "a flat key next to the section",
            Committed,
            Json("{'DatabasePools':{" + PoolDefaults + "},'DatabasePools:WorkerMaximumPoolSize':22}"),
            "Worker: appsettings.json must have exactly one DatabasePools section"
        },
        {
            "a section that is not an object",
            Json("{'DatabasePools':24}"),
            Committed,
            "API: DatabasePools must be an object"
        },
    };

    [Theory]
    [MemberData(nameof(Drifts))]
    public void The_guard_fails_on_drift(string reason, string api, string worker, string expected)
    {
        var drift = Drift(api, worker, CodeDefaults());

        Assert.True(
            drift.Any(message => message.Contains(expected, StringComparison.Ordinal)),
            $"{reason}: expected a drift containing \"{expected}\", got: {string.Join(" | ", drift)}");
    }

    /// <summary>Test JSON written with single quotes, so the cases stay readable.</summary>
    private static string Json(string singleQuoted) => singleQuoted.Replace('\'', '"');

    private static string Pools(string entries) => Json("{'DatabasePools':{" + entries + "}}");

    /// <summary>The settable pool sizes of <see cref="DatabasePoolOptions"/> and their code defaults.</summary>
    private static Dictionary<string, int> CodeDefaults()
    {
        var defaults = new DatabasePoolOptions();
        return typeof(DatabasePoolOptions)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.CanWrite && property.PropertyType == typeof(int))
            .ToDictionary(
                property => property.Name,
                property => (int)property.GetValue(defaults)!,
                StringComparer.Ordinal);
    }

    /// <summary>Every difference between the API's and the Worker's <c>DatabasePools</c> and the code defaults.</summary>
    private static List<string> Drift(
        string apiSettings,
        string workerSettings,
        IReadOnlyDictionary<string, int> defaults)
    {
        var drift = new List<string>();
        var api = PoolSizes("API", apiSettings, defaults, drift);
        var worker = PoolSizes("Worker", workerSettings, defaults, drift);
        foreach (var key in defaults.Keys)
        {
            if (api.TryGetValue(key, out var apiSize) &&
                worker.TryGetValue(key, out var workerSize) &&
                apiSize != workerSize)
            {
                drift.Add(
                    $"{Section}:{key} is {apiSize} in the API and {workerSize} in the Worker; each process checks only " +
                    "its own sum, so the budget holds only when both carry the same three values.");
            }
        }

        return drift;
    }

    private static Dictionary<string, int> PoolSizes(
        string host,
        string settings,
        IReadOnlyDictionary<string, int> defaults,
        List<string> drift)
    {
        var sizes = new Dictionary<string, int>(StringComparer.Ordinal);
        using var document = JsonDocument.Parse(settings, ConfigurationJson);

        // Configuration keys are case-insensitive, and "DatabasePools:Key" at the root is the same setting.
        var sections = document.RootElement
            .EnumerateObject()
            .Where(property => property.Name.StartsWith(Section, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (sections is not [{ Name: Section } section])
        {
            drift.Add(
                $"{host}: {Settings} must have exactly one {Section} section, spelled exactly, and no other key " +
                $"starting with {Section}.");
            return sizes;
        }

        if (section.Value.ValueKind != JsonValueKind.Object)
        {
            drift.Add($"{host}: {Section} must be an object.");
            return sizes;
        }

        var entries = section.Value.EnumerateObject().ToArray();
        foreach (var group in entries.GroupBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase))
        {
            var key = defaults.Keys.FirstOrDefault(name =>
                string.Equals(name, group.Key, StringComparison.OrdinalIgnoreCase));
            if (key is null)
            {
                drift.Add($"{host}: {Section}:{group.Key} is not a pool size of {nameof(DatabasePoolOptions)}.");
                continue;
            }

            if (group.Count() != 1 || !string.Equals(group.Single().Name, key, StringComparison.Ordinal))
            {
                drift.Add($"{host}: {Section}:{key} must appear once, spelled exactly.");
                continue;
            }

            var value = group.Single().Value;
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var size))
            {
                drift.Add($"{host}: {Section}:{key} must be a whole number.");
                continue;
            }

            sizes[key] = size;
            if (size != defaults[key])
            {
                drift.Add($"{host}: {Section}:{key} is {size}; the code default is {defaults[key]}.");
            }
        }

        foreach (var key in defaults.Keys.Where(name =>
                     !entries.Any(entry => string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase))))
        {
            drift.Add($"{host}: {Section}:{key} is missing; the code default is {defaults[key]}.");
        }

        return sizes;
    }

    private static bool SetsPoolSizes(string settings)
    {
        using var document = JsonDocument.Parse(settings, ConfigurationJson);
        return document.RootElement
            .EnumerateObject()
            .Any(property => property.Name.StartsWith(Section, StringComparison.OrdinalIgnoreCase));
    }
}
