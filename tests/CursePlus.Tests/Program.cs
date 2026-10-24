using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CursePlus.Core;

if (args.Length == 4 && args[0] == "--compact-export")
{
    var archive = new BundleArchive();
    var original = await archive.ReadAsync(args[1]);
    var current = await archive.ReadAsync(args[2]);
    var delta = ChangeDelta.Create(current with { Kind = BundleKind.Sync }, original.Desired);
    if (delta.Desired.Files.Count == 0 && args[3].EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        await archive.WriteAsync(args[3], delta, "unused");
    else await archive.ComposeAsync(args[3], delta, [args[2], args[1]]);
    var restored = ChangeDelta.Resolve(await archive.ReadAsync(args[3]), original);
    if (!StateComparer.Equivalent(restored.Desired, current.Desired)) throw new Exception("Compact export roundtrip mismatch.");
    Console.WriteLine(JsonSerializer.Serialize(new { previousBytes = new FileInfo(args[2]).Length, exportedBytes = new FileInfo(args[3]).Length, changedMods = delta.Desired.Mods.Count, attachedFiles = delta.Desired.Files.Count, originalFilesExcluded = original.Desired.Files.Count - delta.Delta!.RemovedFiles.Count, roundtrip = true }, JsonFormat.Options));
    return 0;
}

if (args.Length == 3 && args[0] == "--verify-real-export")
{
    var captured = await new ProfileReader().ReadAsync(Path.GetFullPath(args[1]));
    captured = captured with { OriginId = captured.Pack is { } pack ? $"curseforge:{pack.ProjectId}" : "custom:" + captured.Id };
    var archive = new BundleArchive();
    await archive.WriteAsync(Path.GetFullPath(args[2]), new() { Name = "Verificação local · " + captured.Name, Desired = captured }, captured.Directory);
    var restored = await archive.ReadAsync(Path.GetFullPath(args[2]));
    if (!StateComparer.Equivalent(captured, restored.Desired)) throw new Exception("Real profile export roundtrip mismatch.");
    Console.WriteLine($"Real export verified: {captured.Mods.Count} managed mods; {captured.Files.Count} custom files. Instance unchanged.");
    return 0;
}

var tests = new List<(string Name, Func<Task> Run)>();
void Test(string name, Action action) => tests.Add((name, () => { action(); return Task.CompletedTask; }));
void AsyncTest(string name, Func<Task> action) => tests.Add((name, action));
void Assert(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }
void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
async Task ThrowsAsync<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
ModState Mod(long id, long file = 100, bool enabled = true) => new(id, file, "Mod " + id, "mod" + id + ".jar", enabled);
ProfileState State(params ModState[] mods) => new() { Id = "test", Name = "Test", OriginId = "curseforge:42", MinecraftVersion = "1.20.1", Loader = new("forge", "47.4.10"), Pack = new(42, 1000, "Test pack", "1"), Mods = mods.ToList() };
var planner = new ChangePlanner();
Test("idempotent inventory", () => Assert(planner.Build(State(Mod(1)), State(Mod(1))).Operations.Count == 0));
Test("source removes destination-only mods", () => Assert(planner.Build(State(Mod(1), Mod(2)), State(Mod(1))).Operations.Single().Kind == OperationKind.RemoveMod));
Test("merge preserves destination-only mods", () => Assert(planner.Build(State(Mod(1), Mod(2)), State(Mod(1)), ApplyMode.Merge).Desired.Mods.Count == 2));
Test("merge respects source deletion from baseline", () => Assert(planner.Build(State(Mod(1)), State(), ApplyMode.Merge, State(Mod(1))).Operations.Single().Kind == OperationKind.RemoveMod));
Test("merge can keep a deleted mod", () => Assert(planner.Build(State(Mod(1)), State(), ApplyMode.Merge, State(Mod(1)), new Dictionary<string, ConflictChoice> { ["mod:1"] = ConflictChoice.Target }).Desired.Mods.Count == 1));
Test("source pins exact file", () => Assert(planner.Build(State(Mod(1, 10)), State(Mod(1, 11))).Operations.Single().Mod?.FileId == 11));
Test("merge can preserve target version", () => Assert(planner.Build(State(Mod(1, 10)), State(Mod(1, 11)), ApplyMode.Merge, null, new Dictionary<string, ConflictChoice> { ["mod:1"] = ConflictChoice.Target }).Desired.Mods[0].FileId == 10));
Test("activation tracked independently", () => Assert(planner.Build(State(Mod(1)), State(Mod(1, enabled: false))).Operations.Single().Kind == OperationKind.SetModEnabled));
Test("different pack blocked", () => Assert(!planner.Build(State(), State() with { Pack = new(77, 1000, "Other", "1") }).CanApply));
Test("pack update precedes mods", () => Assert(planner.Build(State(), State(Mod(1)) with { Pack = new(42, 2000, "Test pack", "2") }).Operations[0].Kind == OperationKind.ChangePackVersion));
Test("pack update reconciles every desired mod", () => Assert(planner.Build(State(Mod(1)), State(Mod(1)) with { Pack = new(42, 2000, "Test pack", "2") }).Operations.Any(o => o.Kind == OperationKind.InstallMod)));
Test("standalone Minecraft change blocked", () => Assert(!planner.Build(State(), State() with { MinecraftVersion = "1.21.1" }).CanApply));
Test("loader version is native operation", () => Assert(planner.Build(State(), State() with { Loader = new("forge", "47.4.11") }).Operations.Single().Kind == OperationKind.ChangeLoader));
Test("loader family change blocked in place", () => Assert(!planner.Build(State(), State() with { Loader = new("fabric", "0.16") }).CanApply));
Test("unbound custom profiles blocked", () => Assert(!planner.Build(State() with { Pack = null, OriginId = "" }, State() with { Pack = null, OriginId = "" }).CanApply));
Test("explicitly bound custom profiles allowed", () => Assert(planner.Build(State() with { Pack = null, OriginId = "same" }, State() with { Pack = null, OriginId = "same" }).CanApply));
Test("same custom profile can restore its earlier origin", () => Assert(planner.Build(State() with { Pack = null, OriginId = "new" }, State() with { Pack = null, OriginId = "old" }).CanApply));
Test("unrelated custom profiles still require binding", () => Assert(!planner.Build(State() with { Id = "B", Pack = null, OriginId = "B" }, State() with { Id = "A", Pack = null, OriginId = "A" }).CanApply));
Test("duplicate project blocked", () => Assert(!planner.Build(State(), State(Mod(1), Mod(1, 101))).CanApply));
foreach (var path in new[] { "../secret", "/absolute", @"C:\escape", "config/../../escape", "config/CON.txt", "config/file.", "config/a:stream", "saves/world.dat", "minecraftinstance.json", "mods/normal.txt" })
    Test("reject unsafe/scope path " + path, () => Throws<InvalidDataException>(() => SafePaths.Classify(path)));
Test("configuration classification", () => Assert(SafePaths.Classify(@"defaultconfigs\mod.toml") == FileKind.Configuration));
Test("KubeJS classification", () => Assert(SafePaths.Classify("kubejs/server_scripts/test.js") == FileKind.Script));
Test("disabled local jar classification", () => Assert(SafePaths.Classify("mods/custom.jar.disabled") == FileKind.LocalMod));

AsyncTest("profile metadata + modified and local jars + scope", async () =>
{
    var fixture = new Fixture();
    await fixture.Save(State(Mod(1)));
    await File.WriteAllTextAsync(Path.Combine(fixture.Directory, "mods", "mod1.jar"), "modified");
    await fixture.File("mods/local.jar", "local");
    await fixture.File("config/settings.toml", "config");
    await fixture.File("scripts/test.zs", "script");
    await fixture.File("saves/world/level.dat", "world");
    await fixture.File("shaderpacks/shader.zip", "shader");
    var state = await new ProfileReader().ReadAsync(fixture.Directory);
    Assert(state.Mods.Single().Modified);
    Assert(state.Files.Count == 4);
    Assert(state.Files.All(f => !f.Path.StartsWith("saves/")));
});
AsyncTest("disabled mod recognized from actual file", async () =>
{
    var fixture = new Fixture(); await fixture.Save(State(Mod(1)));
    File.Move(Path.Combine(fixture.Directory, "mods/mod1.jar"), Path.Combine(fixture.Directory, "mods/mod1.jar.disabled"));
    var state = await new ProfileReader().ReadAsync(fixture.Directory); Assert(!state.Mods.Single().Enabled);
});
AsyncTest("normal jars are references rather than payload", async () =>
{
    var fixture = new Fixture(); await fixture.Save(State(Mod(1)));
    var state = await new ProfileReader().ReadAsync(fixture.Directory);
    Assert(!state.Mods.Single().Modified && state.Files.Count == 0);
});
AsyncTest("archive round trip + integrity + portable paths", async () =>
{
    var fixture = new Fixture(); await fixture.Save(State(Mod(1))); await fixture.File("config/test.toml", "hello");
    var state = await new ProfileReader().ReadAsync(fixture.Directory);
    var path = Path.Combine(fixture.Directory, "roundtrip.zip");
    var archive = new BundleArchive(); await archive.WriteAsync(path, new() { Name = "roundtrip", Desired = state }, fixture.Directory);
    var result = await archive.ReadAsync(path);
    Assert(result.Desired.Mods.Single().ProjectId == 1 && result.Desired.Files.Count == 1);
    Assert(result.Desired.Directory == "");
    using var zip = ZipFile.OpenRead(path);
    Assert(zip.GetEntry("summary.txt") is not null && zip.Entries.Count == 3);
    using var text = new StreamReader(zip.GetEntry("changes.json")!.Open());
    Assert(!(await text.ReadToEndAsync()).Contains(fixture.Directory.Replace(@"\", @"\\")));
});
AsyncTest("archive rejects corruption", async () =>
{
    var fixture = new Fixture(); await fixture.Save(State()); await fixture.File("config/test", "hello");
    var state = await new ProfileReader().ReadAsync(fixture.Directory); var archive = new BundleArchive();
    var path = Path.Combine(fixture.Directory, "bad.zip"); await archive.WriteAsync(path, new() { Name = "bad", Desired = state }, fixture.Directory);
    using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
    { var entry = zip.Entries.Single(e => e.FullName.StartsWith("payload/")); var name = entry.FullName; entry.Delete(); using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write("other"); }
    await ThrowsAsync<InvalidDataException>(() => archive.ReadAsync(path));
});
AsyncTest("archive rejects traversal entry", async () =>
{
    var fixture = new Fixture(); var path = Path.Combine(fixture.Directory, "traversal.zip");
    using (var zip = ZipFile.Open(path, ZipArchiveMode.Create)) { using var w = new StreamWriter(zip.CreateEntry("../bad").Open()); w.Write("bad"); }
    await ThrowsAsync<InvalidDataException>(() => new BundleArchive().ReadAsync(path));
});
AsyncTest("capture/export detects file changes", async () =>
{
    var fixture = new Fixture(); await fixture.Save(State()); await fixture.File("config/test", "old");
    var state = await new ProfileReader().ReadAsync(fixture.Directory);
    await fixture.File("config/test", "new");
    await ThrowsAsync<IOException>(() => new BundleArchive().WriteAsync(Path.Combine(fixture.Directory, "changed.zip"), new() { Name = "test", Desired = state }, fixture.Directory));
});
Test("normal managed jar cannot be a direct-write payload", () =>
{
    var hash = new string('a', 64);
    Throws<InvalidDataException>(() => BundleArchive.Validate(new() { Name = "bad", Desired = State(Mod(1)) with { Files = [new("mods/mod1.jar", hash, 1, FileKind.LocalMod)] } }));
});
Test("modified jar requires payload", () => Throws<InvalidDataException>(() => BundleArchive.Validate(new() { Name = "bad", Desired = State(Mod(1) with { Modified = true }) })));
Test("preset preserves project identity, no pinned version", () => AppStore.ValidatePreset(new() { Name = "list", Mods = [new(1, "Mod", null)] }));
Test("preset rejects duplicate identities", () => Throws<InvalidDataException>(() => AppStore.ValidatePreset(new() { Name = "list", Mods = [new(1, "A", null), new(1, "B", null)] })));

AsyncTest("baseline lookup tied to exact pack version", async () =>
{
    var fixture = new Fixture(); await fixture.Save(State());
    var store = new AppStore(Path.Combine(fixture.Directory, "store")); await store.LoadAsync();
    var state = store.BindOrigin(await new ProfileReader().ReadAsync(fixture.Directory));
    await store.SaveBundleAsync(new() { Name = "original", Kind = BundleKind.Baseline, Desired = state }, fixture.Directory);
    Assert(store.FindBaseline(state) is not null);
    Assert(store.FindBaseline(state with { Pack = state.Pack! with { FileId = 9999 } }) is null);
});
AsyncTest("config-only sync creates backup and journal; worlds untouched", async () =>
{
    var source = new Fixture(); var target = new Fixture();
    await source.Save(State()); await target.Save(State());
    await source.File("config/test.toml", "source"); await target.File("config/test.toml", "target"); await target.File("saves/World/level.dat", "world");
    var reader = new ProfileReader(); var store = new AppStore(Path.Combine(target.Directory, "store")); await store.LoadAsync();
    var sourceState = store.BindOrigin(await reader.ReadAsync(source.Directory)); var targetState = store.BindOrigin(await reader.ReadAsync(target.Directory));
    var path = Path.Combine(source.Directory, "sync.zip"); await new BundleArchive().WriteAsync(path, new() { Name = "sync", Desired = sourceState }, source.Directory);
    var native = new FakeNative(target);
    var result = await new SyncExecutor(native, reader, store).ApplyAsync(planner.Build(targetState, sourceState), path);
    Assert(result.Status == "complete" && File.Exists(result.BackupPath));
    Assert(await File.ReadAllTextAsync(Path.Combine(target.Directory, "config/test.toml")) == "source");
    Assert(await File.ReadAllTextAsync(Path.Combine(target.Directory, "saves/World/level.dat")) == "world");
    var backup = await new BundleArchive().ReadAsync(result.BackupPath);
    Assert(backup.Desired.Files.Single().Sha256 != sourceState.Files.Single().Sha256);
    Assert(planner.Build(store.BindOrigin(await reader.ReadAsync(target.Directory)), sourceState).Operations.Count == 0);
});
AsyncTest("normal mod install goes through native adapter", async () =>
{
    var source = new Fixture(); var target = new Fixture(); await source.Save(State(Mod(1))); await target.Save(State());
    var reader = new ProfileReader(); var store = new AppStore(Path.Combine(target.Directory, "store")); await store.LoadAsync();
    var sourceState = store.BindOrigin(await reader.ReadAsync(source.Directory)); var targetState = store.BindOrigin(await reader.ReadAsync(target.Directory));
    var path = Path.Combine(source.Directory, "sync.zip"); await new BundleArchive().WriteAsync(path, new() { Name = "sync", Desired = sourceState }, source.Directory);
    var native = new FakeNative(target); await new SyncExecutor(native, reader, store).ApplyAsync(planner.Build(targetState, sourceState), path);
    Assert(native.Calls.Contains("install:1"));
});
AsyncTest("native failure is journaled; backup retained", async () =>
{
    var source = new Fixture(); var target = new Fixture(); await source.Save(State(Mod(1))); await target.Save(State());
    var reader = new ProfileReader(); var store = new AppStore(Path.Combine(target.Directory, "store")); await store.LoadAsync();
    var a = store.BindOrigin(await reader.ReadAsync(source.Directory)); var b = store.BindOrigin(await reader.ReadAsync(target.Directory));
    var path = Path.Combine(source.Directory, "sync.zip"); await new BundleArchive().WriteAsync(path, new() { Name = "sync", Desired = a }, source.Directory);
    await ThrowsAsync<InvalidOperationException>(() => new SyncExecutor(new FakeNative(target) { FailInstall = true }, reader, store).ApplyAsync(planner.Build(b, a), path));
    var journal = await AppStore.ReadJsonAsync<OperationJournal>(Directory.GetFiles(store.JournalDirectory).Single());
    Assert(journal?.Status == "failed" && File.Exists(journal.BackupPath));
});
AsyncTest("stale preview prevents mutation", async () =>
{
    var source = new Fixture(); var target = new Fixture(); await source.Save(State(Mod(1))); await target.Save(State());
    var reader = new ProfileReader(); var store = new AppStore(Path.Combine(target.Directory, "store")); await store.LoadAsync();
    var a = store.BindOrigin(await reader.ReadAsync(source.Directory)); var b = store.BindOrigin(await reader.ReadAsync(target.Directory));
    var path = Path.Combine(source.Directory, "sync.zip"); await new BundleArchive().WriteAsync(path, new() { Name = "sync", Desired = a }, source.Directory);
    await target.File("config/changed", "new");
    var native = new FakeNative(target);
    await ThrowsAsync<InvalidOperationException>(() => new SyncExecutor(native, reader, store).ApplyAsync(planner.Build(b, a), path));
    Assert(native.Calls.Count == 0);
});

Test("keep normal B mod discards modified A payload", () =>
{
    var normal = Mod(1, 200) with { FileName = "new.jar" };
    var changed = Mod(1, 100) with { FileName = "old.jar", Modified = true, Sha256 = new string('a', 64) };
    var a = State(changed) with { Files = [new("mods/old.jar", new string('a', 64), 10, FileKind.LocalMod)] };
    var result = planner.Build(State(normal), a, ApplyMode.Merge, null, new Dictionary<string, ConflictChoice> { ["mod:1"] = ConflictChoice.Target });
    Assert(result.Desired.Mods.Single().FileId == 200 && result.Desired.Files.Count == 0 && result.Operations.Count == 0);
});
Test("reject same payload hash with inconsistent sizes", () => Throws<InvalidDataException>(() => BundleArchive.Validate(new() { Name = "bad", Desired = State() with { Files = [new("config/a", new string('a', 64), 10, FileKind.Configuration), new("config/b", new string('a', 64), 11, FileKind.Configuration)] } })));
AsyncTest("resume package includes retained destination bytes", async () =>
{
    var source = new Fixture(); var target = new Fixture(); await source.Save(State()); await target.Save(State());
    await source.File("config/a", "A"); await target.File("config/b", "B");
    var reader = new ProfileReader(); var archives = new BundleArchive();
    var a = await reader.ReadAsync(source.Directory); var b = await reader.ReadAsync(target.Directory);
    var aZip = Path.Combine(source.Directory, "a.zip"); var bZip = Path.Combine(target.Directory, "b.zip");
    await archives.WriteAsync(aZip, new() { Name = "a", Desired = a }, source.Directory);
    await archives.WriteAsync(bZip, new() { Name = "b", Desired = b }, target.Directory);
    var output = Path.Combine(target.Directory, "resume.zip");
    await archives.ComposeAsync(output, new() { Name = "resume", Desired = a with { Files = [.. a.Files, .. b.Files] } }, [aZip, bZip]);
    Assert((await archives.ReadAsync(output)).Desired.Files.Count == 2);
});
AsyncTest("pack version and mods are reconciled together", async () =>
{
    var source = new Fixture(); var target = new Fixture();
    await source.Save(State(Mod(1)) with { Pack = new(42, 2000, "Test pack", "2") }); await target.Save(State(Mod(2)));
    var reader = new ProfileReader(); var store = new AppStore(Path.Combine(target.Directory, "store")); await store.LoadAsync();
    var a = store.BindOrigin(await reader.ReadAsync(source.Directory)); var b = store.BindOrigin(await reader.ReadAsync(target.Directory));
    var path = Path.Combine(source.Directory, "sync.zip"); await new BundleArchive().WriteAsync(path, new() { Name = "sync", Desired = a }, source.Directory);
    var native = new FakeNative(target); await new SyncExecutor(native, reader, store).ApplyAsync(planner.Build(b, a), path);
    Assert(native.Calls[0] == "pack" && native.Calls.Contains("remove:2") && native.Calls.Contains("install:1"));
    Assert((await reader.ReadAsync(target.Directory)).Pack?.FileId == 2000);
});

AsyncTest("native readiness requires physical jars", async () =>
{
    var fixture = new Fixture(); await fixture.Save(State(Mod(1)));
    var state = await new ProfileReader().ReadAsync(fixture.Directory, false);
    Assert(await NativeProfileReadiness.CheckAsync(state, false));
    File.Delete(Path.Combine(fixture.Directory, "mods/mod1.jar"));
    Assert(!await NativeProfileReadiness.CheckAsync(state, false));
});
AsyncTest("published readiness requires full original manifest", async () =>
{
    var fixture = new Fixture(); await fixture.Save(State(Mod(1)));
    var state = await new ProfileReader().ReadAsync(fixture.Directory, false);
    Assert(!await NativeProfileReadiness.CheckAsync(state, true));
    var path = Path.Combine(fixture.Directory, "minecraftinstance.json");
    var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!;
    json["manifest"]!["files"] = new System.Text.Json.Nodes.JsonArray(System.Text.Json.Nodes.JsonNode.Parse("{\"projectID\":1,\"fileID\":100,\"required\":true}"));
    await File.WriteAllTextAsync(path, json.ToJsonString());
    Assert(await NativeProfileReadiness.CheckAsync(state, true));
    json["manifest"]!["files"]![0]!["fileID"] = 999;
    await File.WriteAllTextAsync(path, json.ToJsonString());
    Assert(!await NativeProfileReadiness.CheckAsync(state, true));
});
AsyncTest("native readiness tolerates incomplete metadata during install", async () =>
{
    var fixture = new Fixture(); await fixture.Save(State());
    var state = await new ProfileReader().ReadAsync(fixture.Directory, false);
    await File.WriteAllTextAsync(Path.Combine(fixture.Directory, "minecraftinstance.json"), "{");
    Assert(!await NativeProfileReadiness.CheckAsync(state, false));
});

AsyncTest("pack label survives native update without manifest version", async () =>
{
    var fixture = new Fixture(); await fixture.Save(State());
    var path = Path.Combine(fixture.Directory, "minecraftinstance.json");
    var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!;
    json["manifest"]!.AsObject().Remove("version");
    json["installedModpack"]!["installedFile"] = System.Text.Json.Nodes.JsonNode.Parse("{\"id\":1000,\"fileName\":\"Pack 1.0.zip\"}");
    await File.WriteAllTextAsync(path, json.ToJsonString());
    var state = await new ProfileReader().ReadAsync(fixture.Directory, false);
    Assert(state.Pack?.Version == "Pack 1.0" && state.Pack.FileId == 1000);
});

Test("modified jar and native mod share one conflict choice", () =>
{
    var a = State(Mod(1) with { Modified = true, Sha256 = new string('a', 64) }) with { Files = [new("mods/mod1.jar", new string('a', 64), 1, FileKind.LocalMod)] };
    var b = State(Mod(1) with { Modified = true, Sha256 = new string('b', 64) }) with { Files = [new("mods/mod1.jar", new string('b', 64), 1, FileKind.LocalMod)] };
    var preview = planner.Build(b, a, ApplyMode.Merge);
    Assert(preview.Operations.Where(o => o.File is not null).All(o => o.Key == "mod:1"));
    var kept = planner.Build(b, a, ApplyMode.Merge, null, new Dictionary<string, ConflictChoice> { ["mod:1"] = ConflictChoice.Target });
    Assert(kept.Desired.Mods.Single().Sha256 == new string('b', 64) && kept.Desired.Files.Single().Sha256 == new string('b', 64));
});

AsyncTest("capture treats missing recorded jars as removals", async () =>
{
    var fixture = new Fixture(); await fixture.Save(State(Mod(1)));
    File.Delete(Path.Combine(fixture.Directory, "mods/mod1.jar"));
    var state = await new ProfileReader().ReadAsync(fixture.Directory);
    Assert(state.Mods.Count == 0 && state.MissingMods.Count == 1);
});

DeltaTests.Register(tests);
var failures = 0;
foreach (var test in tests)
{
    try { await test.Run(); Console.WriteLine("PASS " + test.Name); }
    catch (Exception error) { failures++; Console.WriteLine("FAIL " + test.Name + ": " + error); }
}
Console.WriteLine($"{tests.Count - failures}/{tests.Count} checks passed.");
return failures == 0 ? 0 : 1;

sealed class Fixture
{
    public string Directory { get; } = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
    public ProfileState Current { get; private set; } = new();
    public Fixture() => System.IO.Directory.CreateDirectory(Directory);
    public async Task File(string path, string value)
    {
        var full = Path.Combine(Directory, path.Replace('/', Path.DirectorySeparatorChar));
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(full)!); await System.IO.File.WriteAllTextAsync(full, value);
    }
    public async Task Save(ProfileState state)
    {
        Current = state;
        var addons = new List<object>();
        foreach (var mod in state.Mods)
        {
            var path = "mods/" + mod.FileName + (mod.Enabled ? "" : ".disabled");
            await File(path, "native-" + mod.ProjectId + "-" + mod.FileId);
            var hash = Convert.ToHexString(SHA1.HashData(await System.IO.File.ReadAllBytesAsync(Path.Combine(Directory, path))));
            addons.Add(new { addonID = mod.ProjectId, name = mod.Name, fileNameOnDisk = mod.FileName, isEnabled = mod.Enabled, isModified = mod.Modified,
                installedFile = new { id = mod.FileId, fileName = mod.FileName, hashes = new[] { new { type = 1, value = hash } } } });
        }
        var metadata = new { guid = "fixture-" + Path.GetFileName(Directory), name = state.Name, gameVersion = state.MinecraftVersion,
            baseModLoader = new { name = state.Loader.Family + "-" + state.Loader.Version }, projectID = state.Pack?.ProjectId ?? 0, fileID = state.Pack?.FileId ?? 0,
            installedModpack = new { name = state.Pack?.Name }, manifest = new { version = state.Pack?.Version }, installedAddons = addons };
        await System.IO.File.WriteAllTextAsync(Path.Combine(Directory, "minecraftinstance.json"), JsonSerializer.Serialize(metadata));
    }
}

sealed class FakeNative(Fixture fixture) : ICurseForge
{
    public List<string> Calls { get; } = [];
    public bool FailInstall { get; init; }
    public Func<Task>? AfterPackUpdate { get; init; }
    public Task EnsureReadyAsync(CancellationToken ct) => Task.CompletedTask;
    public Task CheckProfileAsync(ProfileState profile, CancellationToken ct) => Task.CompletedTask;
    public async Task ChangePackVersionAsync(ProfileState profile, PackReference pack, CancellationToken ct) { Calls.Add("pack"); await fixture.Save(fixture.Current with { Pack = pack }); if (AfterPackUpdate is not null) await AfterPackUpdate(); }
    public async Task ChangeLoaderAsync(ProfileState profile, LoaderReference loader, CancellationToken ct) { Calls.Add("loader"); await fixture.Save(fixture.Current with { Loader = loader }); }
    public async Task InstallModAsync(ProfileState profile, ModState mod, CancellationToken ct)
    { if (FailInstall) throw new InvalidOperationException("Native failure"); Calls.Add("install:" + mod.ProjectId); await fixture.Save(fixture.Current with { Mods = [.. fixture.Current.Mods.Where(m => m.ProjectId != mod.ProjectId), mod] }); }
    public async Task RemoveModAsync(ProfileState profile, ModState mod, CancellationToken ct)
    {
        Calls.Add("remove:" + mod.ProjectId);
        var file = Path.Combine(fixture.Directory, "mods", mod.FileName + (mod.Enabled ? "" : ".disabled"));
        if (System.IO.File.Exists(file)) System.IO.File.Delete(file);
        await fixture.Save(fixture.Current with { Mods = fixture.Current.Mods.Where(m => m.ProjectId != mod.ProjectId).ToList() });
    }
    public async Task SetModEnabledAsync(ProfileState profile, ModState mod, CancellationToken ct)
    {
        Calls.Add("enable:" + mod.ProjectId);
        var old = fixture.Current.Mods.FirstOrDefault(m => m.ProjectId == mod.ProjectId);
        if (old is not null && old.Enabled != mod.Enabled)
        {
            var oldPath = Path.Combine(fixture.Directory, "mods", old.FileName + (old.Enabled ? "" : ".disabled"));
            if (System.IO.File.Exists(oldPath)) System.IO.File.Delete(oldPath);
        }
        await fixture.Save(fixture.Current with { Mods = fixture.Current.Mods.Select(m => m.ProjectId == mod.ProjectId ? mod : m).ToList() });
    }
    public Task<ProfileState> CreateProfileAsync(ProfileState desired, string name, CancellationToken ct, string? contentArchive = null) => throw new NotSupportedException("Test does not create profiles.");
    public Task<ModState?> InstallCompatibleModAsync(ProfileState profile, PresetMod mod, CancellationToken ct) => throw new NotSupportedException("Not used in this test.");
}
