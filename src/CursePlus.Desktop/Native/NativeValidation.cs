using System.IO;
using System.Text.Json;
using CursePlus.Core;

namespace CursePlus.Desktop.Native;

// Explicit development command. All profile mutations are restricted to disposable validation profiles.
internal static class NativeValidation
{
    public static async Task RunAsync(string input, string output)
    {
        using var spec = JsonDocument.Parse(await File.ReadAllTextAsync(input));
        var root = spec.RootElement;
        var action = root.GetProperty("action").GetString()!;
        var store = new AppStore(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, "native-data"));
        await store.LoadAsync();
        var reader = new ProfileReader();
        void Trace(string message) => File.AppendAllText(Path.ChangeExtension(output, ".trace"), DateTimeOffset.Now.ToString("O") + " " + message + "\n");
        var native = new CurseForgeAutomation(store, reader, Trace);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var ct = timeout.Token;
        object? result = null;
        try
        {
            var name = root.TryGetProperty("name", out var n) ? n.GetString()! : "";
            if (!name.StartsWith("CursePlus Validation ", StringComparison.Ordinal))
                throw new InvalidOperationException("Validation requires a disposable CursePlus Validation profile.");
            var desired = root.TryGetProperty("desired", out var d) ? d.Deserialize<ProfileState>(JsonFormat.Options)! : new ProfileState
            { Name = name, MinecraftVersion = "1.20.1", Loader = new("forge", "47.4.0"), OriginId = "curseplus-validation" };
            var profile = (await reader.DiscoverAsync(store.Settings.InstancesDirectory, ct)).SingleOrDefault(p => p.Name == name);
            Trace("ensure ready"); await native.EnsureReadyAsync(ct); Trace("ready: " + action);
            switch (action)
            {
                case "create": result = await native.CreateProfileAsync(desired, name, ct, root.TryGetProperty("archive", out var a) ? a.GetString() : null); break;
                case "check": await native.CheckProfileAsync(profile!, ct); break;
                case "delete-profile":
                    await native.DeleteValidationProfileAsync(profile!, ct);
                    result = new { deleted = true, profileId = profile!.Id };
                    break;
                case "clone-current-pack":
                    var sourceName = root.GetProperty("sourceName").GetString()!;
                    if (!sourceName.StartsWith("CursePlus Validation ", StringComparison.Ordinal)) throw new InvalidOperationException("Disposable source required.");
                    var source = (await reader.DiscoverAsync(store.Settings.InstancesDirectory, ct)).Single(p => p.Name == sourceName);
                    var sourceBefore = store.BindOrigin(await reader.ReadAsync(source.Directory, true, ct));
                    var unlockedBefore = await ReadUnlocked(source.Directory);
                    var copy = await native.CreateProfileAsync(sourceBefore, name, ct);
                    var unchanged = StateComparer.Equivalent(sourceBefore, await reader.ReadAsync(source.Directory, true, ct)) && unlockedBefore == await ReadUnlocked(source.Directory);
                    var pristine = !root.TryGetProperty("baseline", out var originalPath) || StateComparer.Equivalent((await new BundleArchive().ReadAsync(originalPath.GetString()!, ct)).Desired, await reader.ReadAsync(copy.Directory, true, ct));
                    if (!unchanged || !pristine) throw new InvalidOperationException($"Source unchanged: {unchanged}; clean baseline matches: {pristine}.");
                    result = new { copy, sourceUnchanged = unchanged, matchesOriginal = pristine };
                    break;
                case "install": await native.InstallModAsync(profile!, root.GetProperty("mod").Deserialize<ModState>(JsonFormat.Options)!, ct); break;
                case "quick":
                    var compatible = await native.InstallCompatibleModAsync(profile!, root.GetProperty("mod").Deserialize<PresetMod>(JsonFormat.Options)!, ct);
                    result = new { compatible = compatible is not null, mod = compatible };
                    break;
                case "remove": await native.RemoveModAsync(profile!, profile!.Mods.Single(m => m.ProjectId == root.GetProperty("projectId").GetInt64()), ct); break;
                case "enabled": await native.SetModEnabledAsync(profile!, profile!.Mods.Single(m => m.ProjectId == root.GetProperty("projectId").GetInt64()) with { Enabled = root.GetProperty("enabled").GetBoolean() }, ct); break;
                case "loader": await native.ChangeLoaderAsync(profile!, root.GetProperty("loader").Deserialize<LoaderReference>(JsonFormat.Options)!, ct); break;
                case "pack": await native.ChangePackVersionAsync(profile!, root.GetProperty("pack").Deserialize<PackReference>(JsonFormat.Options)!, ct); break;
                case "capture":
                    var captured = await store.EnsureOriginAsync(await reader.ReadAsync(profile!.Directory, true, ct), ct);
                    var bundle = new ChangeBundle { Name = "Native validation snapshot", Kind = BundleKind.Snapshot, Desired = captured };
                    await new BundleArchive().WriteAsync(root.GetProperty("archive").GetString()!, bundle, profile.Directory, ct);
                    result = bundle;
                    break;
                case "fixture-config":
                    var fixtureBefore = store.BindOrigin(await reader.ReadAsync(profile!.Directory, true, ct));
                    var fixtureRoot = Path.Combine(store.Root, "fixture");
                    System.IO.Directory.CreateDirectory(Path.Combine(fixtureRoot, "config"));
                    foreach (var existing in fixtureBefore.Files)
                    {
                        var destination = SafePaths.Resolve(fixtureRoot, existing.Path);
                        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        File.Copy(SafePaths.Resolve(fixtureBefore.Directory, existing.Path), destination, true);
                    }
                    var configPath = Path.Combine(fixtureRoot, "config", "curseplus-validation.json");
                    await File.WriteAllTextAsync(configPath, "{\"curseplusValidation\":true}", ct);
                    var fixtureHash = await ProfileReader.HashFileAsync(configPath, ct);
                    var fixtureState = fixtureBefore with { Files = [.. fixtureBefore.Files.Where(f => f.Path != "config/curseplus-validation.json"), new("config/curseplus-validation.json", fixtureHash.Sha256, new FileInfo(configPath).Length, FileKind.Configuration)] };
                    var fixtureArchive = Path.Combine(store.Root, "fixture.curseplus.zip");
                    await new BundleArchive().WriteAsync(fixtureArchive, new ChangeBundle { Name = "Configuration exception fixture", Desired = fixtureState }, fixtureRoot, ct);
                    result = await new SyncExecutor(native, reader, store).ApplyAsync(new ChangePlanner().Build(fixtureBefore, fixtureState), fixtureArchive, ct: ct);
                    break;
                case "restore":
                case "clone":
                    var archivePath = root.GetProperty("archive").GetString()!;
                    var saved = await store.ReadBundleAsync(archivePath, ct);
                    if (action == "clone") profile = await native.CreateProfileAsync(saved.Desired, name, ct, archivePath);
                    var before = store.BindOrigin(await reader.ReadAsync(profile!.Directory, true, ct));
                    var plan = new ChangePlanner().Build(before, saved.Desired);
                    result = await new SyncExecutor(native, reader, store).ApplyAsync(plan, archivePath, ct: ct);
                    break;
                default: throw new InvalidOperationException("Unknown validation action.");
            }
            if (profile is not null) result ??= await reader.ReadAsync(profile.Directory, false, ct);
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { success = true, result }, JsonFormat.Options));
        }
        catch (Exception error)
        {
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { success = false, error = error.ToString() }, JsonFormat.Options));
        }
        finally
        {
            CurseForgeAutomation.DumpUi(Path.ChangeExtension(output, ".ui.json"));
            CurseForgeAutomation.CaptureUi(Path.ChangeExtension(output, ".png"));
        }
    }
    private static async Task<bool> ReadUnlocked(string directory)
    {
        using var metadata = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "minecraftinstance.json")));
        return metadata.RootElement.GetProperty("isUnlocked").GetBoolean();
    }
}
