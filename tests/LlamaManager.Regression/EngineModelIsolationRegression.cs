using System.IO;
using System.Text.Json;
using LlamaManager.Engines;
using LlamaManager.Models;
using LlamaManager.Services;
using static Program;

/// <summary>Guarded header-only GGUF fixtures; no GPU, engine launch, model scan or real configuration.</summary>
internal static class EngineModelIsolationRegression
{
    private static readonly string[] BackendIds = ["polystrata", "kvmem"];
    private static readonly string[] InstallationKeys =
        ["packageRoot", "serveRoot", "pythonPath", "bundlePython", "enginePath", "executablePath", "releaseVersion", "runtimeCuda"];

    public static void RunModelSwitchIsolation(string root)
    {
        var f = new Fixture(root);
        var config = new ManagerConfig();
        foreach (var id in BackendIds) config.Engines[id] = f.Settings(id);
        foreach (var id in BackendIds)
        {
            var source = config.Engines[id];
            var original = JsonSerializer.Serialize(source);
            var otherId = id == "kvmem" ? "polystrata" : "kvmem";
            var other = JsonSerializer.Serialize(config.Engines[otherId]);
            var expectedA = new Dictionary<string, string>(source.Parameters);
            var expectedB = new Dictionary<string, string>(source.ModelPresets[Variant(f.ModelB)]);
            var keyA = PathIdentity.Normalize(f.ModelA);
            var keyB = PathIdentity.Normalize(f.ModelB);

            var same = EngineModelConfiguration.SelectModel(source, Variant(f.ModelA), Schema(id));
            ParametersEqual(expectedA, same.Parameters);
            ParametersEqual(expectedA, same.ModelPresets[keyA]); // Current edits beat the intentionally stale A preset.
            Equal(2, same.ModelPresets.Count);
            Equal(original, JsonSerializer.Serialize(source));
            Check(same.ModelPresets.Keys.All(key => key == PathIdentity.Normalize(key)), "model preset keys were not canonical absolute paths");

            var selectedB = EngineModelConfiguration.SelectModel(source, Variant(f.ModelB), Schema(id));
            config.Engines[id] = selectedB;
            Check(!ReferenceEquals(source, selectedB) && !ReferenceEquals(source.Parameters, selectedB.Parameters) &&
                !ReferenceEquals(source.ModelPresets, selectedB.ModelPresets), "selection did not deep-copy settings");
            ParametersEqual(expectedA, selectedB.ModelPresets[keyA]);
            ParametersEqual(expectedB, selectedB.Parameters);
            ParametersEqual(expectedB, selectedB.ModelPresets[keyB]);
            Check(!ReferenceEquals(selectedB.Parameters, selectedB.ModelPresets[keyB]) &&
                !ReferenceEquals(source.ModelPresets[Variant(f.ModelB)], selectedB.ModelPresets[keyB]), "selected/source presets share a parameter dictionary");
            Equal(source.ExecutablePath, selectedB.ExecutablePath);
            Equal(source.LastStats, selectedB.LastStats);
            Equal(int.Parse(expectedB["servePort"]), selectedB.Port);
            Check(Path.IsPathFullyQualified(selectedB.ModelPath) && PathIdentity.Equals(f.ModelB, selectedB.ModelPath), "selection is not an absolute model identity");
            foreach (var key in InstallationKeys) Equal(expectedB[key], selectedB.Parameters[key]); // B's manual paths must not be overwritten by A/global paths.
            Equal(other, JsonSerializer.Serialize(config.Engines[otherId]));

            Check(EngineModelConfiguration.SetParameter(selectedB, f.ModelB, "context", "196608"), "current B parameter edit was rejected");
            expectedB["context"] = "196608";
            ParametersEqual(expectedB, selectedB.Parameters);
            ParametersEqual(expectedB, selectedB.ModelPresets[keyB]);
            ParametersEqual(expectedA, selectedB.ModelPresets[keyA]);
            var selectedA = EngineModelConfiguration.SelectModel(selectedB, Variant(f.ModelA), Schema(id));
            config.Engines[id] = selectedA;
            ParametersEqual(expectedA, selectedA.Parameters);
            ParametersEqual(expectedA, selectedA.ModelPresets[keyA]);
            ParametersEqual(expectedB, selectedA.ModelPresets[keyB]);
            Equal(int.Parse(expectedA["servePort"]), selectedA.Port);
            Equal(2, selectedA.ModelPresets.Count); // Different casing/slashes do not create another model.
            selectedA.ModelPresets[keyB]["copy-only"] = "detached";
            Equal(original, JsonSerializer.Serialize(source));
            Equal(other, JsonSerializer.Serialize(config.Engines[otherId]));
        }
    }

    public static void RunNewModelDefaultsAndParameterIsolation(string root)
    {
        var f = new Fixture(root);
        // Selection and parameter edits must work without opening even a GGUF header/tensor.
        using var lockedA = new FileStream(f.ModelA, FileMode.Open, FileAccess.Read, FileShare.None);
        using var lockedB = new FileStream(f.ModelB, FileMode.Open, FileAccess.Read, FileShare.None);
        foreach (var id in BackendIds)
        {
            var source = f.Settings(id, includeB: false);
            var before = JsonSerializer.Serialize(source);
            var selected = EngineModelConfiguration.SelectModel(source, Variant(f.ModelB), Schema(id));
            var expected = InstallationKeys.ToDictionary(key => key, key => source.Parameters[key], StringComparer.OrdinalIgnoreCase);
            expected["host"] = "127.0.0.1";
            expected["servePort"] = "8080";
            ParametersEqual(expected, selected.Parameters);
            ParametersEqual(expected, selected.ModelPresets[PathIdentity.Normalize(f.ModelB)]);
            ParametersEqual(source.Parameters, selected.ModelPresets[PathIdentity.Normalize(f.ModelA)]);
            Equal(8080, selected.Port);
            foreach (var key in new[] { "ctx", "context", "threads", "kvmem", "kvmemBudget", "kvmemGenReserve", "kvmemHostMib",
                "kvmemDiskMib", "kvmemDiskPath", "kv", "port", "mmproj", "draftModel", "modelOnly" })
                Check(!selected.Parameters.ContainsKey(key), "new model inherited/materialized tuning or pairing: " + key);
            Equal(before, JsonSerializer.Serialize(source));
            var config = new ManagerConfig { Strata = new StrataConfig { ServePort = 8091, Host = "0.0.0.0" } };
            config.Engines[id] = selected;
            var backend = new StrataBackend(id == "kvmem" ? StrataBackend.KvmemProfile : StrataBackend.PolyProfile);
            Equal(8080, backend.ResolvePort(new EngineStartRequest { Config = config })); // Never silently use legacy 8091.

            var selectedBefore = JsonSerializer.Serialize(selected);
            Check(!EngineModelConfiguration.SetParameter(selected, f.ModelA, "threads", "7"), "wrong current model was accepted");
            Check(!EngineModelConfiguration.SetParameter(selected, "", "threads", "7"), "empty model bypassed a real current selection");
            Equal(selectedBefore, JsonSerializer.Serialize(selected));
            Check(EngineModelConfiguration.SetParameter(selected, Variant(f.ModelB), "kv", "fp16"), "normalized current model was rejected");
            Equal("fp16", selected.Parameters["kv"]);
            Equal("fp16", selected.ModelPresets[PathIdentity.Normalize(f.ModelB)]["kv"]);
            ParametersEqual(source.Parameters, selected.ModelPresets[PathIdentity.Normalize(f.ModelA)]);
        }

        var generic = f.Settings("kvmem", includeB: false);
        var portSchema = new ParameterSpec[]
        {
            new("port", "Port", "Fixture", ParamType.Integer, "8080"),
            new("context", "Context", "Fixture", ParamType.Integer, "4096")
        };
        var genericB = EngineModelConfiguration.SelectModel(generic, f.ModelB, portSchema);
        Equal("8080", genericB.Parameters["port"]);
        Equal(8080, genericB.Port);
        Check(!genericB.Parameters.ContainsKey("servePort") && !genericB.Parameters.ContainsKey("host") &&
            !genericB.Parameters.ContainsKey("context"), "generic schema inherited endpoints or materialized non-endpoint defaults");
        var noDefaults = EngineModelConfiguration.SelectModel(generic, f.ModelB, []);
        Equal(InstallationKeys.Length, noDefaults.Parameters.Count);
        Equal(0, noDefaults.Port);

        var shared = f.Parameters("kvmem", "A");
        var sharedBefore = JsonSerializer.Serialize(shared);
        var aliased = new EngineSettings
        {
            ModelPath = f.ModelA, Parameters = shared,
            ModelPresets = new() { [Variant(f.ModelA)] = shared, [Variant(f.ModelB)] = shared }
        };
        Check(EngineModelConfiguration.SetParameter(aliased, Variant(f.ModelA), "context", "98304"), "SaveCurrentParameter contract rejected A");
        Equal("98304", aliased.Parameters["context"]);
        Equal("98304", aliased.ModelPresets[PathIdentity.Normalize(f.ModelA)]["context"]);
        Equal(sharedBefore, JsonSerializer.Serialize(shared));
        ParametersEqual(shared, aliased.ModelPresets[PathIdentity.Normalize(f.ModelB)]);
        Check(!ReferenceEquals(aliased.Parameters, aliased.ModelPresets[PathIdentity.Normalize(f.ModelA)]) &&
            !ReferenceEquals(aliased.ModelPresets[PathIdentity.Normalize(f.ModelA)], aliased.ModelPresets[PathIdentity.Normalize(f.ModelB)]),
            "SaveCurrentParameter aliases another preset");

        var empty = new EngineSettings
        {
            Parameters = new() { ["packageRoot"] = "backend-installation", ["context"] = "backend-default-context" },
            ModelPresets = new() { [PathIdentity.Normalize(f.ModelA)] = f.Parameters("kvmem", "A") }
        };
        var emptyBefore = JsonSerializer.Serialize(empty);
        var emptyCopy = EngineModelConfiguration.SelectModel(empty, "", Schema("kvmem"));
        Equal(emptyBefore, JsonSerializer.Serialize(emptyCopy));
        var presetsBefore = JsonSerializer.Serialize(empty.ModelPresets);
        Check(EngineModelConfiguration.SetParameter(emptyCopy, "", "threads", "6"), "empty current model should allow backend defaults");
        Equal("6", emptyCopy.Parameters["threads"]);
        Equal(presetsBefore, JsonSerializer.Serialize(emptyCopy.ModelPresets));
        Equal(emptyBefore, JsonSerializer.Serialize(empty));
        var emptyCopyBefore = JsonSerializer.Serialize(emptyCopy);
        Check(!EngineModelConfiguration.SetParameter(emptyCopy, f.ModelA, "threads", "9"), "non-current model was accepted while selection is empty");
        Equal(emptyCopyBefore, JsonSerializer.Serialize(emptyCopy));
        var firstModel = EngineModelConfiguration.SelectModel(emptyCopy, f.ModelB, Schema("kvmem"));
        Check(!firstModel.Parameters.ContainsKey("context") && !firstModel.Parameters.ContainsKey("threads") &&
            !firstModel.ModelPresets.ContainsKey(""), "first model inherited backend tuning or created an empty model preset");
    }

    public static void RunRecommendationCasIsolation(string root)
    {
        var f = new Fixture(root);
        var store = new ConfigStore(Path.Combine(f.Root, "llama-manager.config.json"));
        var original = store.Read();
        try
        {
            var seed = new ManagerConfig { ActiveBackendId = "kvmem" };
            foreach (var id in BackendIds)
                seed.Engines[id] = EngineModelConfiguration.SelectModel(f.Settings(id), f.ModelA, Schema(id));
            foreach (var (slot, pid) in new[] { (ManagerConfig.SlotMain, 10001), ("polystrata", 10002), ("kvmem", 10003) })
                seed.Running[slot] = new ServerSession
                {
                    ProcessId = pid, StartTimeUtcTicks = pid * 1000L, EnginePath = Path.Combine(f.Work, slot, "never-launched.exe"),
                    ModelPath = f.ModelB, Args = "--fixture-recorded-only", Port = 9000 + pid % 100
                };
            store.Save(seed);
            var prepared = ConfigService.ReadConfig();
            var expected = prepared.Engines["kvmem"];
            var expectedBefore = JsonSerializer.Serialize(expected);
            var otherBefore = JsonSerializer.Serialize(prepared.Engines["polystrata"]);
            var keyA = PathIdentity.Normalize(f.ModelA);
            var keyB = PathIdentity.Normalize(f.ModelB);
            var presetB = new Dictionary<string, string>(expected.ModelPresets[keyB]);
            var runningBefore = JsonSerializer.Serialize(prepared.Running);
            var fieldExpected = new Dictionary<string, string>(expected.Parameters) { ["threads"] = "5" };
            ConfigService.SaveModelParameter("kvmem", Variant(f.ModelA), "threads", "5");
            var fieldSaved = ConfigService.ReadConfig();
            ParametersEqual(fieldExpected, fieldSaved.Engines["kvmem"].Parameters);
            ParametersEqual(fieldExpected, fieldSaved.Engines["kvmem"].ModelPresets[keyA]);
            Equal(expectedBefore, JsonSerializer.Serialize(expected));
            UnrelatedUnchanged(fieldSaved);
            var fieldBeforeRefusal = JsonSerializer.Serialize(fieldSaved);
            Throws<IOException>(() => ConfigService.SaveModelParameter("kvmem", f.ModelB, "threads", "99"));
            Throws<IOException>(() => ConfigService.SaveModelParameter("kvmem", "", "threads", "99"));
            Equal(fieldBeforeRefusal, JsonSerializer.Serialize(ConfigService.ReadConfig()));
            expected = fieldSaved.Engines["kvmem"];
            expectedBefore = JsonSerializer.Serialize(expected);
            // The hardware producer is tested separately. This explicit 19-key patch also exercises
            // model-local endpoints and does not query hardware or inspect model data.
            var patch = new Dictionary<string, string>
            {
                ["kvmem"] = "1", ["context"] = "131072", ["kvmemBudget"] = "8192", ["kvmemGenReserve"] = "512",
                ["kvmemHostMib"] = "256", ["kvmemDiskMib"] = "8192", ["kvmemDiskPath"] = Path.Combine(f.Work, "recommendation-cache"),
                ["kvmemSessions"] = "2", ["kv"] = "q8_0", ["promptBatch"] = "128", ["stageMib"] = "128", ["threads"] = "8",
                ["vramReserveMib"] = "512", ["tailSkip"] = "0", ["cacheBias"] = "0", ["tensorCoreMma"] = "0", ["polyFlashAttention"] = "1",
                ["host"] = "127.0.0.1", ["servePort"] = "8080"
            };
            Equal(19, patch.Count);
            var patchBefore = JsonSerializer.Serialize(patch);
            ConfigService.SaveModelRecommendation("kvmem", Variant(f.ModelA), patch, expected);
            var applied = ConfigService.ReadConfig();
            var patchedA = new Dictionary<string, string>(expected.Parameters);
            foreach (var (key, value) in patch) patchedA[key] = value;
            ParametersEqual(patchedA, applied.Engines["kvmem"].Parameters);
            ParametersEqual(patchedA, applied.Engines["kvmem"].ModelPresets[keyA]);
            Equal(expectedBefore, JsonSerializer.Serialize(expected));
            Equal(patchBefore, JsonSerializer.Serialize(patch));
            UnrelatedUnchanged(applied);

            // Exercise the caller's explicit save after pure selection: persisted A -> B -> A.
            ConfigService.SaveEngineSettings(applied, "kvmem",
                EngineModelConfiguration.SelectModel(applied.Engines["kvmem"], Variant(f.ModelB), Schema("kvmem")));
            var selectedB = ConfigService.ReadConfig();
            ParametersEqual(presetB, selectedB.Engines["kvmem"].Parameters);
            UnrelatedUnchanged(selectedB);
            ConfigService.SaveEngineSettings(selectedB, "kvmem",
                EngineModelConfiguration.SelectModel(selectedB.Engines["kvmem"], Variant(f.ModelA), Schema("kvmem")));
            var restoredA = ConfigService.ReadConfig();
            ParametersEqual(patchedA, restoredA.Engines["kvmem"].Parameters);
            UnrelatedUnchanged(restoredA);

            var modelExpected = restoredA.Engines["kvmem"];
            store.Update(config => config.Engines["kvmem"].ModelPath = f.ModelB); // External selection changed during recommendation.
            var externalModel = JsonSerializer.Serialize(ConfigService.ReadConfig());
            Throws<IOException>(() => ConfigService.SaveModelRecommendation("kvmem", f.ModelA, patch, modelExpected));
            Equal(externalModel, JsonSerializer.Serialize(ConfigService.ReadConfig()));
            UnrelatedUnchanged(ConfigService.ReadConfig());

            store.Update(config => config.Engines["kvmem"].ModelPath = f.ModelA);
            var paramsExpected = ConfigService.ReadConfig().Engines["kvmem"];
            ConfigService.SaveModelParameter("kvmem", f.ModelA, "threads", "12");
            var externalParameters = JsonSerializer.Serialize(ConfigService.ReadConfig());
            Throws<IOException>(() => ConfigService.SaveModelRecommendation("kvmem", f.ModelA, patch, paramsExpected));
            var refused = ConfigService.ReadConfig();
            Equal(externalParameters, JsonSerializer.Serialize(refused));
            Equal("12", refused.Engines["kvmem"].Parameters["threads"]);
            Equal("12", refused.Engines["kvmem"].ModelPresets[keyA]["threads"]);
            UnrelatedUnchanged(refused);

            void UnrelatedUnchanged(ManagerConfig config)
            {
                ParametersEqual(presetB, config.Engines["kvmem"].ModelPresets[keyB]);
                Equal(otherBefore, JsonSerializer.Serialize(config.Engines["polystrata"]));
                Equal(runningBefore, JsonSerializer.Serialize(config.Running));
                Equal("kvmem", config.ActiveBackendId);
            }
        }
        finally { store.Save(original); } // Restore only the guarded runner fixture, never a user's config.
    }

    private static IReadOnlyList<ParameterSpec> Schema(string id) =>
        new StrataBackend(id == "kvmem" ? StrataBackend.KvmemProfile : StrataBackend.PolyProfile).Parameters;
    private static string Variant(string path) => path.ToUpperInvariant().Replace('\\', '/');
    private static void ParametersEqual(IReadOnlyDictionary<string, string> expected, IReadOnlyDictionary<string, string> actual)
    {
        Equal(expected.Count, actual.Count);
        foreach (var (key, value) in expected)
            Check(actual.TryGetValue(key, out var found) && found == value, "parameter changed/leaked: " + key);
    }

    private sealed class Fixture
    {
        public string Root { get; }
        public string Work { get; }
        public string ModelA { get; }
        public string ModelB { get; }

        public Fixture(string root)
        {
            Root = Path.GetFullPath(root);
            var name = Path.GetFileName(Root);
            Check(name.StartsWith("regression-", StringComparison.Ordinal) && Guid.TryParseExact(name["regression-".Length..], "N", out _) &&
                PathIdentity.Equals(Root, Environment.GetEnvironmentVariable("LLAMA_MANAGER_ROOT")), "model isolation tests require the runner's isolated LLAMA_MANAGER_ROOT");
            Check(PathIdentity.Equals(Root, ConfigService.GetManagerRoot()), "ConfigService is bound to a different root; never touch real config");
            Work = Path.Combine(Root, "engine-model-isolation", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Work);
            ModelA = Path.Combine(Work, "Model A.gguf");
            ModelB = Path.Combine(Work, "Model B.gguf");
            File.WriteAllBytes(ModelA, Fixtures.Gguf());
            File.WriteAllBytes(ModelB, Fixtures.Gguf(context: 16384));
        }

        public EngineSettings Settings(string id, bool includeB = true)
        {
            var current = Parameters(id, "A");
            var settings = new EngineSettings
            {
                ModelPath = ModelA, ExecutablePath = Path.Combine(Work, id, "shared-engine.exe"), Port = int.Parse(current["servePort"]),
                LastStats = id + "-stats", Parameters = current,
                ModelPresets = new() { [Variant(ModelA)] = new Dictionary<string, string>(current) { ["context"] = "stale-preset" } }
            };
            if (includeB) settings.ModelPresets[Variant(ModelB)] = Parameters(id, "B");
            return settings;
        }

        public Dictionary<string, string> Parameters(string id, string model)
        {
            var a = model == "A";
            var installation = Path.Combine(Work, "manual-installation", id, model);
            var port = (id == "kvmem" ? 8093 : 8091) + (a ? 0 : 1);
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["packageRoot"] = installation, ["serveRoot"] = Path.Combine(installation, "serve-source"),
                ["pythonPath"] = Path.Combine(installation, "python.exe"), ["bundlePython"] = Path.Combine(installation, "runtime", "python.exe"),
                ["enginePath"] = Path.Combine(installation, "engine.exe"), ["executablePath"] = Path.Combine(installation, "custom.exe"),
                ["releaseVersion"] = a ? "v0.19.1" : "v0.19.0", ["runtimeCuda"] = a ? "13.3" : "12.4",
                ["ctx"] = a ? "8192" : "16384", ["context"] = id == "kvmem" ? (a ? "65536" : "131072") : (a ? "8192" : "16384"),
                ["threads"] = a ? "3" : "7", ["host"] = a ? "0.0.0.0" : "127.0.0.2", ["servePort"] = port.ToString(), ["port"] = port.ToString(),
                ["kv"] = a ? "q8_0" : "fp16", ["modelOnly"] = id + "-" + model, ["explicitEmpty"] = "",
                ["mmproj"] = Path.Combine(Work, id, model, "paired-mmproj.gguf"), ["draftModel"] = id == "kvmem" ? "" : Path.Combine(Work, id, model, "paired-draft.gguf")
            };
            if (id == "kvmem")
            {
                values["kvmem"] = "1";
                values["kvmemBudget"] = a ? "8192" : "16384";
                values["kvmemGenReserve"] = a ? "512" : "1024";
                values["kvmemHostMib"] = a ? "256" : "1024";
                values["kvmemDiskMib"] = a ? "8192" : "4096";
                values["kvmemDiskPath"] = Path.Combine(Work, "user-cache-" + model);
            }
            return values;
        }
    }
}
