using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using CursePlus.Core;

static class DeltaTests
{
    static ModState Mod(long id, long file = 100, bool enabled = true) => new(id, file, "Mod " + id, "mod" + id + ".jar", enabled);
    static ProfileState State(params ModState[] mods) => new() { Id = "test", Name = "Test", OriginId = "curseforge:42", MinecraftVersion = "1.20.1", Loader = new("forge", "47.4.10"), Pack = new(42, 1000, "Test pack", "1"), Mods = mods.ToList() };
    static void Assert(bool value) { if (!value) throw new Exception("Assertion failed."); }
    static async Task Reject(Func<Task> action) { try { await action(); } catch (InvalidDataException) { return; } throw new Exception("Expected invalid data rejection."); }

    public static void Register(List<(string Name, Func<Task> Run)> tests)
    {
        void Add(string name, Func<Task> run) => tests.Add((name, run));
        Add("delta contains only added changed removed mods", async () =>
        {
            var original = State(Mod(1), Mod(2), Mod(3), Mod(4));
            var current = State(Mod(1), Mod(2, 101), Mod(3) with { Enabled = false }, Mod(5));
            var delta = ChangeDelta.Create(new() { Name = "changes", Desired = current }, original);
            Assert(delta.Desired.Mods.Select(m => m.ProjectId).SequenceEqual(new long[] { 2, 3, 5 }) && delta.Delta!.RemovedMods.SequenceEqual(new long[] { 4 }));
            var fixture = new Fixture(); var path = Path.Combine(fixture.Directory, "changes.curseplus.json");
            await new BundleArchive().WriteAsync(path, delta, "does-not-exist");
            var loaded = await new BundleArchive().ReadAsync(path);
            Assert(StateComparer.Equivalent(ChangeDelta.Resolve(loaded, new() { Desired = original }).Desired, current));
        });
        Add("large original exports one mod as tiny JSON without reading original files", async () =>
        {
            var original = State(Enumerable.Range(1, 429).Select(i => Mod(i)).ToArray()) with
            { Files = Enumerable.Range(1, 2797).Select(i => new FileState($"kubejs/assets/pack/texture{i}.png", new string('a', 64), 1024 * 1024, FileKind.Script)).ToList() };
            var current = original with { Mods = [.. original.Mods, Mod(9000)] };
            var fixture = new Fixture(); var path = Path.Combine(fixture.Directory, "tiny.curseplus.json");
            var watch = Stopwatch.StartNew(); var allocated = GC.GetTotalAllocatedBytes();
            var delta = ChangeDelta.Create(new() { Name = "One mod", Desired = current }, original);
            await new BundleArchive().WriteAsync(path, delta, "missing-original-assets");
            File.Copy(path, Path.Combine(AppContext.BaseDirectory, "light-export-example.curseplus.json"), true);
            var report = new { baselineMods = 429, baselineFiles = 2797, changedMods = delta.Desired.Mods.Count, attachedFiles = delta.Desired.Files.Count, exportedBytes = new FileInfo(path).Length, elapsedMs = watch.Elapsed.TotalMilliseconds, managedAllocatedBytes = GC.GetTotalAllocatedBytes() - allocated };
            await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "light-export-benchmark.json"), JsonSerializer.Serialize(report, JsonFormat.Options));
            Console.WriteLine("LIGHT EXPORT " + JsonSerializer.Serialize(report, JsonFormat.Compact));
            Assert(new FileInfo(path).Length < 2048 && delta.Desired.Mods.Count == 1 && delta.Desired.Files.Count == 0 && delta.Baseline is null);
        });
        Add("unchanged profile export is empty delta", async () =>
        {
            var original = State(Mod(1)); var delta = ChangeDelta.Create(new() { Name = "no changes", Desired = original }, original);
            Assert(delta.Desired.Mods.Count == 0 && delta.Desired.Files.Count == 0 && delta.Delta!.RemovedMods.Count == 0 && delta.Delta.RemovedFiles.Count == 0);
            var f = new Fixture(); await new BundleArchive().WriteAsync(Path.Combine(f.Directory, "empty.json"), delta, "unused");
        });
        Add("delta ZIP includes only changed files with readable paths", async () =>
        {
            var original = new Fixture(); var source = new Fixture(); await original.Save(State()); await source.Save(State());
            await original.File("config/same.toml", "same"); await source.File("config/same.toml", "same");
            await original.File("scripts/change.js", "old"); await source.File("scripts/change.js", "new");
            await original.File("scripts/remove.js", "old"); await source.File("scripts/add.js", "new");
            var reader = new ProfileReader(); var baseline = await reader.ReadAsync(original.Directory); var current = await reader.ReadAsync(source.Directory);
            var delta = ChangeDelta.Create(new() { Name = "files", Desired = current }, baseline); var path = Path.Combine(source.Directory, "delta.zip");
            await new BundleArchive().WriteAsync(path, delta, source.Directory);
            using var zip = ZipFile.OpenRead(path);
            Assert(zip.Entries.Count == 4 && zip.GetEntry("files/scripts/change.js") is not null && zip.GetEntry("files/scripts/add.js") is not null && zip.Entries.All(e => !e.FullName.StartsWith("payload/")));
            Assert(delta.Delta!.RemovedFiles.SequenceEqual(new[] { "scripts/remove.js" }));
            Assert(StateComparer.Equivalent(ChangeDelta.Resolve(await new BundleArchive().ReadAsync(path), new() { Desired = baseline }).Desired, current));
        });
        Add("delta rejects wrong original and origin", () =>
        {
            var original = State(Mod(1)); var delta = ChangeDelta.Create(new() { Name = "mods", Desired = State(Mod(2)) }, original);
            return Reject(() => Task.FromResult(ChangeDelta.Resolve(delta, new() { Desired = State(Mod(3)) })));
        });
        Add("delta rejects different pack file", () => Reject(() => Task.FromResult(ChangeDelta.Create(new() { Desired = State(), Name = "bad" }, State() with { Pack = new(42, 2000, "Other", "2") }))));
        Add("standalone JSON cannot silently discard attachments", async () =>
        {
            var f = new Fixture(); await f.Save(State()); await f.File("config/test", "data"); var current = await new ProfileReader().ReadAsync(f.Directory);
            var delta = ChangeDelta.Create(new() { Desired = current, Name = "files" }, current with { Files = [] });
            await Reject(() => new BundleArchive().WriteAsync(Path.Combine(f.Directory, "bad.json"), delta, f.Directory));
        });
        Add("delta rejects unreferenced ZIP content", async () =>
        {
            var f = new Fixture(); var delta = ChangeDelta.Create(new() { Name = "empty", Desired = State() }, State()); var path = Path.Combine(f.Directory, "bad.zip");
            await new BundleArchive().WriteAsync(path, delta, f.Directory);
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Update)) { using var writer = new StreamWriter(zip.CreateEntry("files/config/junk").Open()); writer.Write("junk"); }
            await Reject(() => new BundleArchive().ReadAsync(path));
        });
        Add("delta rejects unsafe removed file", () => Reject(() =>
        {
            var d = ChangeDelta.Create(new() { Name = "bad", Desired = State() }, State());
            BundleArchive.Validate(d with { Delta = d.Delta! with { RemovedFiles = ["../escape"] } }); return Task.CompletedTask;
        }));
        Add("delta ZIP corruption preserves destination", async () =>
        {
            var f = new Fixture(); await f.Save(State()); await f.File("config/test", "hello"); var current = await new ProfileReader().ReadAsync(f.Directory);
            var delta = ChangeDelta.Create(new() { Name = "file", Desired = current }, current with { Files = [] }); var path = Path.Combine(f.Directory, "bad.zip");
            await new BundleArchive().WriteAsync(path, delta, f.Directory);
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Update)) { zip.GetEntry("files/config/test")!.Delete(); using var w = new StreamWriter(zip.CreateEntry("files/config/test").Open()); w.Write("wrong"); }
            var destination = Path.Combine(f.Directory, "protected"); await File.WriteAllTextAsync(destination, "keep");
            try { await new BundleArchive().WritePayloadAsync(path, current.Files.Single(), destination, default); throw new Exception("Corruption accepted."); }
            catch (IOException) { }
            Assert(await File.ReadAllTextAsync(destination) == "keep");
        });
        Add("reader skips backups temporary files and generated KubeJS files", async () =>
        {
            var f = new Fixture(); await f.Save(State());
            foreach (var path in new[] { "config/a.bak", "config/a.old", "config/a.tmp", "config/test.log", "scripts/file.js~", "kubejs/.cache/data", "kubejs/logs/output", "kubejs/exported/data", "config/Thumbs.db" }) await f.File(path, "junk");
            await f.File("config/a.toml", "keep"); await f.File("kubejs/assets/icon.png", "asset");
            Assert((await new ProfileReader().ReadAsync(f.Directory)).Files.Count == 2);
        });
        Add("delta executor restores original bytes and applies only changes", async () =>
        {
            var original = new Fixture(); var source = new Fixture(); var target = new Fixture();
            foreach (var f in new[] { original, source, target }) { await f.Save(State(Mod(1))); await f.File("config/same", "original"); await f.File("config/removed", "remove"); }
            await source.File("config/add", "custom"); File.Delete(Path.Combine(source.Directory, "config/removed")); await target.File("config/same", "target change");
            var reader = new ProfileReader(); var store = new AppStore(Path.Combine(target.Directory, "store")); await store.LoadAsync();
            var baseline = store.BindOrigin(await reader.ReadAsync(original.Directory));
            await store.SaveBundleAsync(new() { Name = "original", Kind = BundleKind.Baseline, Desired = baseline }, original.Directory);
            var current = store.BindOrigin(await reader.ReadAsync(source.Directory)); var delta = ChangeDelta.Create(new() { Name = "changes", Desired = current }, baseline);
            var path = Path.Combine(source.Directory, "changes.zip"); await new BundleArchive().WriteAsync(path, delta, source.Directory);
            var loaded = await store.ReadBundleAsync(path); var before = store.BindOrigin(await reader.ReadAsync(target.Directory));
            var journal = await new SyncExecutor(new FakeNative(target), reader, store).ApplyAsync(new ChangePlanner().Build(before, loaded.Desired), path);
            Assert(journal.Status == "complete" && StateComparer.Equivalent(await reader.ReadAsync(target.Directory), current));
            Assert((await new BundleArchive().ReadAsync(journal.BackupPath)).Delta is not null);
            Assert(await File.ReadAllTextAsync(Path.Combine(target.Directory, "config/same")) == "original");
        });
        Add("delta mod installation uses native adapter", async () =>
        {
            var original = new Fixture(); var source = new Fixture(); var target = new Fixture();
            await original.Save(State(Mod(1))); await target.Save(State(Mod(1))); await source.Save(State(Mod(1), Mod(2)));
            var reader = new ProfileReader(); var store = new AppStore(Path.Combine(target.Directory, "store")); await store.LoadAsync();
            var baseline = store.BindOrigin(await reader.ReadAsync(original.Directory)); await store.SaveBundleAsync(new() { Name = "original", Kind = BundleKind.Baseline, Desired = baseline }, original.Directory);
            var current = store.BindOrigin(await reader.ReadAsync(source.Directory)); var delta = ChangeDelta.Create(new() { Name = "mods", Desired = current }, baseline);
            var path = Path.Combine(source.Directory, "mods.json"); await new BundleArchive().WriteAsync(path, delta, source.Directory); var loaded = await store.ReadBundleAsync(path);
            var native = new FakeNative(target); await new SyncExecutor(native, reader, store).ApplyAsync(new ChangePlanner().Build(store.BindOrigin(await reader.ReadAsync(target.Directory)), loaded.Desired), path);
            Assert(native.Calls.Contains("install:2") && StateComparer.Equivalent(await reader.ReadAsync(target.Directory), current));
        });
        Add("delta resume includes retained destination content", async () =>
        {
            var f = new Fixture(); await f.Save(State()); await f.File("config/original", "base"); var reader = new ProfileReader(); var archive = new BundleArchive();
            var baseline = await reader.ReadAsync(f.Directory); var basePath = Path.Combine(f.Directory, "base.zip"); await archive.WriteAsync(basePath, new() { Name = "original", Desired = baseline }, f.Directory);
            await f.File("config/a", "A"); var current = await reader.ReadAsync(f.Directory); var delta = ChangeDelta.Create(new() { Name = "delta", Desired = current }, baseline); var deltaPath = Path.Combine(f.Directory, "delta.zip"); await archive.WriteAsync(deltaPath, delta, f.Directory);
            await f.File("config/b", "B"); var merged = await reader.ReadAsync(f.Directory); var backupPath = Path.Combine(f.Directory, "backup.zip"); await archive.WriteAsync(backupPath, new() { Name = "backup", Desired = merged }, f.Directory);
            var resumed = ChangeDelta.Create(new() { Name = "resume", Desired = merged }, baseline); var output = Path.Combine(f.Directory, "resume.zip"); await archive.ComposeAsync(output, resumed, [deltaPath, backupPath, basePath]);
            var loaded = ChangeDelta.Resolve(await archive.ReadAsync(output), await archive.ReadAsync(basePath));
            Assert(StateComparer.Equivalent(loaded.Desired, merged)); using var zip = ZipFile.OpenRead(output); Assert(zip.Entries.Count == 4);
        });
        Add("changed archive mods are rejected before native mutation", async () =>
        {
            var f = new Fixture(); await f.Save(State()); var reader = new ProfileReader(); var store = new AppStore(Path.Combine(f.Directory, "store")); await store.LoadAsync();
            var before = store.BindOrigin(await reader.ReadAsync(f.Directory)); var source = before with { Mods = [Mod(1)] }; var plan = new ChangePlanner().Build(before, source);
            var path = Path.Combine(f.Directory, "changed.zip"); await new BundleArchive().WriteAsync(path, new() { Name = "replaced", Desired = before with { Mods = [Mod(2)] } }, f.Directory);
            var native = new FakeNative(f); await Reject(() => new SyncExecutor(native, reader, store).ApplyAsync(plan, path)); Assert(native.Calls.Count == 0);
        });
        Add("delta without original is rejected before any mutation", async () =>
        {
            var f = new Fixture(); await f.Save(State()); var store = new AppStore(Path.Combine(f.Directory, "store")); await store.LoadAsync();
            var delta = ChangeDelta.Create(new() { Name = "needs original", Desired = State(Mod(1)) }, State()); var path = Path.Combine(f.Directory, "delta.json");
            await new BundleArchive().WriteAsync(path, delta, f.Directory);
            await Reject(() => store.ReadBundleAsync(path)); Assert(!Directory.EnumerateFiles(store.BundlesDirectory).Any());
        });
        Add("delta includes only changed local jar bytes", async () =>
        {
            var f = new Fixture(); await f.Save(State(Mod(1))); var reader = new ProfileReader(); var original = await reader.ReadAsync(f.Directory);
            await f.File("mods/mod1.jar", "modified managed mod"); await f.File("mods/local.jar", "local mod");
            var current = await reader.ReadAsync(f.Directory); var delta = ChangeDelta.Create(new() { Name = "local", Desired = current }, original);
            var path = Path.Combine(f.Directory, "local.zip"); await new BundleArchive().WriteAsync(path, delta, f.Directory);
            Assert(delta.Desired.Mods.Single().Modified && delta.Desired.Files.Count == 2);
            Assert(StateComparer.Equivalent(ChangeDelta.Resolve(await new BundleArchive().ReadAsync(path), new() { Desired = original }).Desired, current));
        });
        Add("delta cannot hide a managed normal jar as custom file", () => Reject(() =>
        {
            var original = State(Mod(1)); var delta = ChangeDelta.Create(new() { Name = "bad", Desired = original }, original);
            delta = delta with { Desired = delta.Desired with { Files = [new("mods/mod1.jar", new string('a', 64), 1, FileKind.LocalMod)] } };
            ChangeDelta.Resolve(delta, new() { Desired = original }); return Task.CompletedTask;
        }));
        Add("delta pack update keeps merged target bytes after native reset", async () =>
        {
            var original = new Fixture(); var source = new Fixture(); var target = new Fixture();
            var upgraded = State(Mod(1)) with { Pack = new(42, 2000, "Test pack", "2") };
            await original.Save(upgraded); await source.Save(upgraded); await target.Save(State(Mod(2)));
            await original.File("config/original", "original"); await source.File("config/original", "original"); await target.File("config/keep-b", "B");
            var reader = new ProfileReader(); var store = new AppStore(Path.Combine(target.Directory, "store")); await store.LoadAsync();
            var baseline = store.BindOrigin(await reader.ReadAsync(original.Directory)); await store.SaveBundleAsync(new() { Name = "original", Kind = BundleKind.Baseline, Desired = baseline }, original.Directory);
            var current = store.BindOrigin(await reader.ReadAsync(source.Directory)); var delta = ChangeDelta.Create(new() { Name = "upgrade", Desired = current }, baseline); var path = Path.Combine(source.Directory, "upgrade.json"); await new BundleArchive().WriteAsync(path, delta, source.Directory);
            var loaded = await store.ReadBundleAsync(path); var before = store.BindOrigin(await reader.ReadAsync(target.Directory));
            var plan = new ChangePlanner().Build(before, loaded.Desired, ApplyMode.Merge, loaded.Baseline);
            var native = new FakeNative(target) { AfterPackUpdate = () => { File.Delete(Path.Combine(target.Directory, "config/keep-b")); return Task.CompletedTask; } };
            var result = await new SyncExecutor(native, reader, store).ApplyAsync(plan, path);
            Assert(result.Status == "complete" && native.Calls[0] == "pack" && await File.ReadAllTextAsync(Path.Combine(target.Directory, "config/keep-b")) == "B");
            Assert(await File.ReadAllTextAsync(Path.Combine(target.Directory, "config/original")) == "original");
        });
    }
}
