using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Text;
using CursePlus.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using Microsoft.Win32;

namespace CursePlus.Desktop.Native;

public sealed partial class CurseForgeAutomation(AppStore store, ProfileReader reader, Action<string>? trace = null) : ICurseForge
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public static string DetectExecutable()
    {
        var roots = new[] { Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Local") };
        foreach (var root in roots)
        {
            var exe = Path.Combine(root, "Programs", "CurseForge Windows", "CurseForge.exe");
            if (File.Exists(exe)) return exe;
        }
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
        {
            var exe = Path.Combine(root, "CurseForge Windows", "CurseForge.exe");
            if (File.Exists(exe)) return exe;
        }
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Classes\curseforge\shell\open\command");
        if (key?.GetValue("") is string command && command.StartsWith('"'))
        { var end = command.IndexOf('"', 1); if (end > 1 && File.Exists(command[1..end])) return command[1..end]; }
        return "";
    }

    public async Task EnsureReadyAsync(CancellationToken ct)
    {
        if (!Process.GetProcessesByName("CurseForge").Any(p => p.MainWindowHandle != IntPtr.Zero))
        {
            var exe = store.Settings.CurseForgeExecutable;
            if (!File.Exists(exe)) exe = DetectExecutable();
            if (!File.Exists(exe)) throw new InvalidOperationException("Selecione o executável do CurseForge em Configurações.");
            Process.Start(new ProcessStartInfo(exe, "--force-renderer-accessibility=complete") { UseShellExecute = true });
        }
        await RunUiAsync(ui => ui.WaitReady(ct), ct);
    }

    public async Task CheckProfileAsync(ProfileState profile, CancellationToken ct)
    {
        var profiles = await reader.DiscoverAsync(store.Settings.InstancesDirectory, ct);
        if (profiles.Count(p => p.Name == profile.Name) != 1)
            throw new InvalidOperationException("O nome do perfil precisa ser único no CurseForge. Renomeie os perfis duplicados antes de continuar.");
        if (!profiles.Any(p => p.Id == profile.Id && Path.GetFullPath(p.Directory) == Path.GetFullPath(profile.Directory)))
            throw new InvalidOperationException("O perfil selecionado não está mais disponível.");
        await RunUiAsync(ui => ui.OpenProfile(profile.Name, ct), ct);
    }

    public async Task ChangePackVersionAsync(ProfileState profile, PackReference pack, CancellationToken ct)
    {
        if (profile.Pack?.ProjectId != pack.ProjectId) throw new InvalidOperationException("O modpack do destino é diferente.");
        if (profile.Pack.FileId == pack.FileId) return;
        var label = await ResolveVersionLabelAsync(profile, pack, ct);
        var wasUnlocked = await IsUnlockedAsync(profile, ct);
        await SetContentManagementAsync(profile, false, ct);
        try
        {
            await RunUiAsync(ui =>
            {
                ui.OpenVersionDialog(profile.Name, ct);
                try
                {
                    ui.SelectVersion(label, ct);
                    ui.SetCheckBox(["Update to a separate modpack profile", "Atualizar para um perfil separado"], false, true);
                    ui.ContinueVersion(ct);
                }
                catch { ui.CancelVersion(); throw; }
            }, ct);
            await WaitProfileAsync(profile, p => p.Pack?.FileId == pack.FileId, "A versão solicitada do modpack não foi confirmada.", ct, TimeSpan.FromMinutes(20), true);
        }
        finally
        {
            if (wasUnlocked) await SetContentManagementAsync(await reader.ReadAsync(profile.Directory, false, CancellationToken.None), true, CancellationToken.None);
        }
    }

    private async Task<string> ResolveVersionLabelAsync(ProfileState target, PackReference pack, CancellationToken ct)
    {
        var profiles = await reader.DiscoverAsync(store.Settings.InstancesDirectory, ct);
        var reference = profiles.FirstOrDefault(p => p.Pack?.ProjectId == pack.ProjectId && p.Pack.FileId == pack.FileId);
        if (reference is null)
        {
            var prefix = target.Name.StartsWith("CursePlus Validation ", StringComparison.Ordinal) ? "CursePlus Validation Reference " : "CursePlus Reference ";
            var name = prefix + pack.ProjectId + "-" + pack.FileId;
            if (profiles.Any(p => p.Name == name)) name += " " + Guid.NewGuid().ToString("N")[..6];
            reference = await CreateProfileAsync(target with { Pack = pack, OriginId = "curseforge:" + pack.ProjectId }, name, ct);
            var baseline = store.BindOrigin(await reader.ReadAsync(reference.Directory, true, ct));
            await store.SaveBundleAsync(new ChangeBundle { Name = "Original · " + pack.Name + " · " + pack.Version, Kind = BundleKind.Baseline, Desired = baseline }, reference.Directory, reference.Id, ct);
        }
        var wasUnlocked = await IsUnlockedAsync(reference, ct);
        await SetContentManagementAsync(reference, false, ct);
        var label = "";
        try
        {
            await RunUiAsync(ui =>
            {
                ui.OpenVersionDialog(reference.Name, ct);
                try { label = ui.CurrentVersionLabel(ct); }
                finally { ui.CancelVersion(); }
            }, ct);
        }
        finally { if (wasUnlocked) await SetContentManagementAsync(reference, true, CancellationToken.None); }
        return label[..^" - (current)".Length];
    }

    public async Task ChangeLoaderAsync(ProfileState profile, LoaderReference loader, CancellationToken ct)
    {
        if (profile.Loader.Family != loader.Family) throw new InvalidOperationException("Trocar a família do modloader exige um novo perfil.");
        await RunUiAsync(ui =>
        {
            ui.OpenOptions(profile.Name, ct);
            static string Label(LoaderReference value) => (value.Family switch { "forge" => "Forge", "neoforge" => "NeoForge", "fabric" => "Fabric", "quilt" => "Quilt", _ => value.Family }) + " - " + value.Version;
            ui.SelectOption([Label(loader), loader.Version, loader.Family + "-" + loader.Version], ct, [Label(profile.Loader)]);
            ui.Click(["Done", "Concluído", "Concluir"], ct);
        }, ct);
        await WaitProfileAsync(profile, p => p.Loader == loader, "O modloader solicitado não foi confirmado.", ct);
    }

    public async Task InstallModAsync(ProfileState profile, ModState mod, CancellationToken ct)
    {
        var current = await reader.ReadAsync(profile.Directory, false, ct);
        if (current.Mods.FirstOrDefault(m => m.ProjectId == mod.ProjectId && m.FileId == mod.FileId && !m.Modified) is { } installed && mod.Sha256 is not null)
        {
            var path = SafePaths.Resolve(profile.Directory, "mods/" + installed.FileName + (installed.Enabled ? "" : ".disabled"));
            if (File.Exists(path) && (await ProfileReader.HashFileAsync(path, ct)).Sha256 == mod.Sha256) return;
        }
        await SetContentManagementAsync(profile, true, ct);
        LaunchInstall(mod.ProjectId, mod.FileId);
        await RunUiAsync(ui =>
        {
            if (!ui.SelectTargetProfile(profile.Name, ct) || !ui.InstallSelectedProfile(ct))
                throw new InvalidOperationException("A versão solicitada não é compatível com este perfil no CurseForge.");
        }, ct);
        await WaitProfileAsync(profile, p => p.Mods.Any(m => m.ProjectId == mod.ProjectId && m.FileId == mod.FileId), $"O CurseForge não confirmou {mod.Name} (arquivo {mod.FileId}).", ct);
    }

    public async Task RemoveModAsync(ProfileState profile, ModState mod, CancellationToken ct)
    {
        var current = await reader.ReadAsync(profile.Directory, false, ct);
        if (current.Mods.All(m => m.ProjectId != mod.ProjectId)) return;
        mod = current.Mods.Single(m => m.ProjectId == mod.ProjectId);
        await SetContentManagementAsync(profile, true, ct);
        await RunUiAsync(ui =>
        {
            var title = new[] { "Delete " + mod.Name + "?", "Excluir " + mod.Name + "?", "Remover " + mod.Name + "?" };
            if (ui.Find(title) is null)
            {
                ui.OpenProfile(profile.Name, ct);
                ui.OpenMods(ct);
                ui.DeleteMod(mod, ct);
            }
            ui.ConfirmDeletion(title, ct);
        }, ct);
        await WaitProfileAsync(profile, p => p.Mods.All(m => m.ProjectId != mod.ProjectId), $"A remoção de {mod.Name} não foi confirmada.", ct);
    }

    public async Task SetModEnabledAsync(ProfileState profile, ModState mod, CancellationToken ct)
    {
        var current = await reader.ReadAsync(profile.Directory, false, ct);
        if (current.Mods.Any(m => m.ProjectId == mod.ProjectId && m.Enabled == mod.Enabled)) return;
        var installed = current.Mods.Single(m => m.ProjectId == mod.ProjectId);
        await SetContentManagementAsync(profile, true, ct);
        await RunUiAsync(ui =>
        {
            ui.OpenProfile(profile.Name, ct);
            ui.OpenMods(ct);
            ui.RightClickMod(installed, ct);
            ui.Click(mod.Enabled ? ["Enable project", "Enable", "Ativar projeto", "Ativar", "Habilitar"] : ["Disable project", "Disable", "Desativar projeto", "Desativar", "Desabilitar"], ct);
        }, ct);
        await WaitProfileAsync(profile, p => p.Mods.Any(m => m.ProjectId == mod.ProjectId && m.Enabled == mod.Enabled), $"A ativação de {mod.Name} não foi confirmada.", ct);
    }

    public async Task<ProfileState> CreateProfileAsync(ProfileState desired, string name, CancellationToken ct, string? contentArchive = null)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("Informe um nome para o perfil.");
        var before = await reader.DiscoverAsync(store.Settings.InstancesDirectory, ct);
        if (before.Any(p => p.Name == name)) throw new InvalidOperationException("Já existe um perfil com esse nome.");
        await EnsureReadyAsync(ct);
        if (desired.Pack is { } pack)
        {
            var installed = before.FirstOrDefault(p => p.Pack?.ProjectId == pack.ProjectId && p.Pack.FileId == pack.FileId);
            if (installed is not null)
                return await store.LinkOriginAsync(await CopyCurrentPublishedAsync(installed, name, ct), desired.OriginId, ct);
            LaunchInstall(pack.ProjectId, pack.FileId);
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromMinutes(20))
            {
                ct.ThrowIfCancellationRequested();
                var created = (await reader.DiscoverAsync(store.Settings.InstancesDirectory, ct))
                    .Where(p => before.All(old => old.Id != p.Id) && p.Pack?.ProjectId == pack.ProjectId && p.Pack.FileId == pack.FileId).ToList();
                if (created.Count > 1) throw new InvalidOperationException("Mais de um perfil novo foi criado. Revise a biblioteca do CurseForge.");
                if (created.SingleOrDefault() is { } candidate && await IsMetadataReadyAsync(candidate, true, ct))
                    return await store.LinkOriginAsync(await RenameCreatedProfileAsync(candidate, name, ct), desired.OriginId, ct);
                await Task.Delay(1000, ct);
            }
            throw new TimeoutException("O CurseForge não confirmou a instalação da versão exata do pack.");
        }
        else
        {
            // Importing a standard manifest lets CurseForge create Minecraft and the loader itself.
            var zip = Path.Combine(store.Root, "staging", Guid.NewGuid().ToString("N") + ".zip");
            Directory.CreateDirectory(Path.GetDirectoryName(zip)!);
            var content = contentArchive is null ? null : await store.ReadBundleAsync(contentArchive, ct);
            using (var archive = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create))
            {
                using (var writer = new StreamWriter(archive.CreateEntry("manifest.json").Open()))
                    writer.Write(JsonSerializer.Serialize(new { minecraft = new { version = desired.MinecraftVersion,
                    modLoaders = desired.Loader.Family is "vanilla" or "" ? [] : new[] { new { id = desired.Loader.Family + "-" + desired.Loader.Version, primary = true } } },
                    manifestType = "minecraftModpack", manifestVersion = 1, name, version = "1.0", author = "CursePlus",
                    files = desired.Mods.Select(m => new { projectID = m.ProjectId, fileID = m.FileId, required = true }), overrides = "overrides" }));
                if (content is not null)
                {
                    foreach (var file in content.Desired.Files.Where(f => f.Kind != FileKind.LocalMod))
                    {
                        await using var entry = archive.CreateEntry("overrides/" + SafePaths.Normalize(file.Path)).Open();
                        await new BundleArchive().CopyPayloadAsync(content.ContentSources, file, entry, ct);
                    }
                }
            }
            await RunUiAsync(ui =>
            {
                if (ui.HasFileDialog) { ui.FillFileDialog(zip, ct); return; }
                if (ui.Find(["Choose .zip file", "Choose Zip File", "Escolher arquivo .zip", "Escolher .zip"]) is null)
                {
                    ui.OpenLibrary(ct);
                    ui.Click(["Import", "Importar"], ct);
                }
                ui.Click(["Choose .zip file", "Choose Zip File", "Escolher arquivo .zip", "Escolher .zip"], ct);
                ui.FillFileDialog(zip, ct);
            }, ct);
        }
        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromMinutes(20))
        {
            ct.ThrowIfCancellationRequested();
            var profiles = await reader.DiscoverAsync(store.Settings.InstancesDirectory, ct);
            var created = profiles.FirstOrDefault(p => p.Name == name && before.All(old => old.Id != p.Id));
            if (created is not null && created.Pack?.FileId == desired.Pack?.FileId &&
                (desired.Pack is not null || (created.MinecraftVersion == desired.MinecraftVersion && created.Loader == desired.Loader &&
                 desired.Mods.All(m => created.Mods.Any(n => n.ProjectId == m.ProjectId && n.FileId == m.FileId)))) &&
                await IsMetadataReadyAsync(created, desired.Pack is not null, ct))
                return await store.LinkOriginAsync(created, desired.OriginId, ct);
            await Task.Delay(1500, ct);
        }
        throw new TimeoutException("O CurseForge não confirmou a criação do novo perfil.");
    }

    public async Task<ModState?> InstallCompatibleModAsync(ProfileState profile, PresetMod mod, CancellationToken ct)
    {
        var before = await reader.ReadAsync(profile.Directory, false, ct);
        if (before.Mods.FirstOrDefault(m => m.ProjectId == mod.ProjectId) is { } existing) return existing;
        await SetContentManagementAsync(profile, true, ct);
        var installed = false;
        // With no fileId, the native install dialog resolves the file for the selected profile.
        LaunchInstall(mod.ProjectId);
        await RunUiAsync(ui =>
        {
            installed = ui.SelectTargetProfile(profile.Name, ct) && ui.InstallSelectedProfile(ct);
        }, ct);
        if (!installed) return null;
        await WaitProfileAsync(profile, p => p.Mods.Any(m => m.ProjectId == mod.ProjectId), $"Não foi possível instalar o projeto {mod.ProjectId}. Verifique sua compatibilidade no CurseForge.", ct);
        return (await reader.ReadAsync(profile.Directory, false, ct)).Mods.FirstOrDefault(m => m.ProjectId == mod.ProjectId);
    }

    private async Task SetContentManagementAsync(ProfileState profile, bool enabled, CancellationToken ct)
    {
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(profile.Directory, "minecraftinstance.json"), ct));
        if (json.RootElement.TryGetProperty("isUnlocked", out var unlocked) && unlocked.ValueKind is JsonValueKind.True or JsonValueKind.False && unlocked.GetBoolean() == enabled) return;
        if (profile.Pack is null) return;
        await RunUiAsync(ui =>
        {
            ui.OpenOptions(profile.Name, ct);
            ui.SetCheckBox(["Allow content management for this profile", "Permitir gerenciamento de conteúdo para este perfil", "Permitir gerenciamento de conteúdo"], enabled, true);
            ui.Click(["Done", "Concluído", "Concluir"], ct);
        }, ct);
    }
    private static async Task<bool> IsUnlockedAsync(ProfileState profile, CancellationToken ct)
    {
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(profile.Directory, "minecraftinstance.json"), ct));
        return json.RootElement.TryGetProperty("isUnlocked", out var value) && value.ValueKind == JsonValueKind.True;
    }

    private void LaunchInstall(long projectId, long? fileId = null)
    {
        var uri = $"curseforge://install?addonId={projectId}&gameId=432" + (fileId is null ? "" : $"&fileId={fileId}");
        var executable = File.Exists(store.Settings.CurseForgeExecutable) ? store.Settings.CurseForgeExecutable : DetectExecutable();
        if (File.Exists(executable)) Process.Start(new ProcessStartInfo(executable) { ArgumentList = { uri }, UseShellExecute = true });
        else Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
    }

    private async Task WaitProfileAsync(ProfileState profile, Func<ProfileState, bool> condition, string error, CancellationToken ct, TimeSpan? limit = null, bool requireManifest = false)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < (limit ?? TimeSpan.FromMinutes(3)))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var current = await reader.ReadAsync(profile.Directory, false, ct);
                if (condition(current) && await IsMetadataReadyAsync(current, requireManifest, ct)) return;
            }
            catch (IOException) { }
            catch (JsonException) { }
            await Task.Delay(1000, ct);
        }
        throw new TimeoutException(error);
    }

    private static Task<bool> IsMetadataReadyAsync(ProfileState profile, bool requireManifest, CancellationToken ct) =>
        NativeProfileReadiness.CheckAsync(profile, requireManifest, ct);

    internal async Task DeleteValidationProfileAsync(ProfileState profile, CancellationToken ct)
    {
        if (!profile.Name.StartsWith("CursePlus Validation ", StringComparison.Ordinal))
            throw new InvalidOperationException("Somente perfis descartáveis podem ser excluídos pela validação.");
        var current = (await reader.DiscoverAsync(store.Settings.InstancesDirectory, ct)).Where(p => p.Name == profile.Name).ToArray();
        if (current.Length != 1 || current[0].Id != profile.Id || current[0].Directory != profile.Directory)
            throw new InvalidOperationException("A identidade do perfil descartável mudou.");
        await RunUiAsync(ui => ui.OpenDeleteProfile(profile.Name, ct), ct);
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(30))
        {
            if (!(await reader.DiscoverAsync(store.Settings.InstancesDirectory, ct)).Any(p => p.Id == profile.Id)) return;
            await Task.Delay(500, ct);
        }
        throw new InvalidOperationException("O CurseForge não confirmou a exclusão do perfil descartável.");
    }
    internal async Task<ProfileState> CopyCurrentPublishedAsync(ProfileState source, string name, CancellationToken ct)
    {
        var before = await reader.DiscoverAsync(store.Settings.InstancesDirectory, ct);
        var wasUnlocked = await IsUnlockedAsync(source, ct);
        await SetContentManagementAsync(source, false, ct);
        try
        {
            await RunUiAsync(ui =>
            {
                ui.OpenVersionDialog(source.Name, ct);
                try
                {
                    ui.SelectCurrentVersion(ct);
                    ui.SetCheckBox(["Update to a separate modpack profile", "Atualizar para um perfil separado"], true, true);
                    ui.ContinueVersion(ct);
                }
                catch { ui.CancelVersion(); throw; }
            }, ct);
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromMinutes(20))
            {
                ct.ThrowIfCancellationRequested();
                var copies = (await reader.DiscoverAsync(store.Settings.InstancesDirectory, ct))
                    .Where(p => before.All(old => old.Id != p.Id) && p.Pack?.ProjectId == source.Pack?.ProjectId && p.Pack?.FileId == source.Pack?.FileId).ToList();
                if (copies.Count > 1) throw new InvalidOperationException("Mais de um perfil novo foi criado durante a operação. Revise a biblioteca.");
                if (copies.SingleOrDefault() is { } copy && await IsMetadataReadyAsync(copy, true, ct))
                    return await RenameCreatedProfileAsync(copy, name, ct);
                await Task.Delay(1000, ct);
            }
            throw new TimeoutException("O CurseForge não confirmou o novo perfil do pack.");
        }
        finally { if (wasUnlocked) await SetContentManagementAsync(source, true, CancellationToken.None); }
    }
    internal async Task<ProfileState> RenameCreatedProfileAsync(ProfileState profile, string name, CancellationToken ct)
    {
        await RunUiAsync(ui =>
        {
            ui.OpenOptions(profile.Name, ct);
            ui.RenameOption(profile.Name, name);
            ui.Click(["Done", "Concluído", "Concluir"], ct);
        }, ct);
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(30))
        {
            var changed = (await reader.DiscoverAsync(store.Settings.InstancesDirectory, ct)).SingleOrDefault(p => p.Id == profile.Id && p.Name == name);
            if (changed is not null) return changed;
            await Task.Delay(500, ct);
        }
        throw new InvalidOperationException("O CurseForge não confirmou o nome do perfil criado.");
    }

    private async Task RunUiAsync(Action<UiSession> action, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            await Task.Run(() =>
            {
                using var session = new UiSession(trace);
                trace?.Invoke("foreground"); session.BringToFront(ct);
                trace?.Invoke("action"); action(session); trace?.Invoke("action complete");
            }, ct);
        }
        finally { gate.Release(); }
    }

    public static void DumpUi(string path)
    {
        using var automation = new UIA3Automation();
        var root = FindMainWindow(automation);
        var dialogs = NativeWindows("#32770").Select(automation.FromHandle);
        var dialogControls = dialogs.SelectMany(d => d.FindAllDescendants(e => e.ByControlType(ControlType.Edit).Or(e.ByControlType(ControlType.Button))));
        var nodes = root.FindAllDescendants().Take(1200).Concat(dialogControls).Select(e => new
        {
            Name = e.Properties.Name.ValueOrDefault,
            Type = e.Properties.ControlType.ValueOrDefault.ToString(),
            Id = e.Properties.AutomationId.ValueOrDefault,
            Framework = e.Properties.FrameworkId.ValueOrDefault,
            Bounds = e.Properties.BoundingRectangle.ValueOrDefault.ToString(),
            Offscreen = e.Properties.IsOffscreen.ValueOrDefault,
            Enabled = e.Properties.IsEnabled.ValueOrDefault,
            Parent = e.Parent?.Properties.Name.ValueOrDefault
        }).ToList();
        File.WriteAllText(path, JsonSerializer.Serialize(nodes, new JsonSerializerOptions { WriteIndented = true }));
    }
    public static void CaptureUi(string path)
    {
        using var automation = new UIA3Automation();
        FindMainWindow(automation).CaptureToFile(path);
    }

    private static AutomationElement FindMainWindow(UIA3Automation automation)
    {
        var handles = NativeWindows("Chrome_WidgetWin_1");
        if (handles.Count == 0) throw new InvalidOperationException("Abra a janela principal do CurseForge antes de continuar.");
        var handle = handles.OrderByDescending(h => { GetWindowRect(h, out var rect); return (long)(rect.Right - rect.Left) * (rect.Bottom - rect.Top); }).First();
        return automation.FromHandle(handle);
    }

    private delegate bool WindowCallback(IntPtr window, IntPtr state);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, IntPtr state);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int count);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [StructLayout(LayoutKind.Sequential)] private struct WindowRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out WindowRect rectangle);
    private static List<IntPtr> NativeWindows(string className)
    {
        var ids = Process.GetProcessesByName("CurseForge").Select(p => p.Id).ToHashSet();
        var windows = new List<IntPtr>();
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var id);
            if (ids.Contains((int)id) && IsWindowVisible(window))
            {
                var name = new StringBuilder(256); GetClassName(window, name, name.Capacity);
                if (name.ToString() == className) windows.Add(window);
            }
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    private sealed class UiSession(Action<string>? trace) : IDisposable
    {
        private readonly UIA3Automation automation = new();
        private AutomationElement? root;
        private AutomationElement? scopeOverride;
        private AutomationElement Root
        {
            get
            {
                return root ??= FindMainWindow(automation);
            }
        }
        private AutomationElement ControlRoot
        {
            get
            {
                if (scopeOverride is not null) return scopeOverride;
                var root = Root;
                // Chromium leaves the background page in the accessibility tree while a dialog is open.
                var closeButtons = root.FindAllDescendants(e => e.ByControlType(ControlType.Button).And(e.ByName("Close").Or(e.ByName("Fechar"))));
                foreach (var close in closeButtons.Reverse())
                {
                    var depth = 0;
                    for (var parent = close.Parent; parent is not null && parent.ControlType != ControlType.Document && depth++ < 20; parent = parent.Parent)
                        if (parent.ControlType == ControlType.Group && parent.Properties.BoundingRectangle.ValueOrDefault.Height > 100) return parent;
                }
                return root;
            }
        }
        public AutomationElement? Find(string[] names, ControlType? type = null, AutomationElement? scope = null)
        {
            var root = scope ?? ControlRoot;
            foreach (var name in names.Where(n => !string.IsNullOrWhiteSpace(n)))
            {
                var matches = root.FindAllDescendants(e => e.ByName(name, PropertyConditionFlags.IgnoreCase)).Where(e =>
                    !e.Properties.IsOffscreen.ValueOrDefault && e.Properties.BoundingRectangle.ValueOrDefault.Width > 0 &&
                    (type is null || e.Properties.ControlType.ValueOrDefault == type)).ToArray();
                if (matches.Length == 0) continue;
                foreach (var preferred in new[] { ControlType.MenuItem, ControlType.Button, ControlType.TabItem, ControlType.ListItem, ControlType.Text })
                {
                    var candidates = matches.Where(e => e.ControlType == preferred).ToArray();
                    if (candidates.Length == 1) return candidates[0];
                }
                if (matches.Length > 1 && matches.Select(e => e.BoundingRectangle).Distinct().Count() > 1)
                    throw new InvalidOperationException($"O CurseForge mostra mais de um controle '{name}'. Feche diálogos extras e tente novamente.");
                return matches[0];
            }
            return null;
        }
        public void WaitReady(CancellationToken ct)
        {
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(90))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var navigation = Root.FindAllDescendants(e => e.ByName("My Modpacks").Or(e.ByName("Meus modpacks")).Or(e.ByName("Minecraft")));
                    if (navigation.Any(e => !e.Properties.IsOffscreen.ValueOrDefault && e.Properties.BoundingRectangle.ValueOrDefault.Width > 0)) return;
                }
                catch (InvalidOperationException) { }
                Pause(1000, ct);
            }
            throw new InvalidOperationException("O CurseForge não terminou de carregar ou sua interface não está acessível. Abra-o normalmente, aguarde a biblioteca e use Verificar conexão. Se necessário, feche-o e deixe o CursePlus abri-lo com acessibilidade.");
        }
        private AutomationElement Wait(string[] names, CancellationToken ct)
        {
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(20))
            {
                ct.ThrowIfCancellationRequested();
                if (Find(names) is { } element) return element;
                Pause(250, ct);
            }
            throw new InvalidOperationException($"Não encontrei '{names[0]}' na interface do CurseForge. A operação foi interrompida; exporte o diagnóstico em Configurações.");
        }
        private void Activate(AutomationElement element) => ClickElement(element, MouseButton.Left);
        private void ClickElement(AutomationElement element, MouseButton button)
        {
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(3))
            {
                var rect = element.BoundingRectangle;
                var point = new System.Drawing.Point(rect.Left + rect.Width / 2, rect.Top + rect.Height / 2);
                var hit = automation.FromPoint(point);
                var ids = Process.GetProcessesByName("CurseForge").Select(p => p.Id).ToHashSet();
                if (ids.Contains(hit.Properties.ProcessId.ValueOrDefault))
                {
                    var depth = 0;
                    for (var parent = hit; parent is not null && depth++ < 20; parent = parent.Parent)
                        if (parent.ControlType == element.ControlType && parent.Name == element.Name && parent.BoundingRectangle == rect)
                        { Mouse.Click(point, button); return; }
                }
                Thread.Sleep(100);
            }
            throw new InvalidOperationException($"O controle '{element.Name}' está coberto ou a tela do CurseForge ainda está mudando. Aguarde e tente novamente.");
        }
        public void Click(string[] names, CancellationToken ct)
        {
            trace?.Invoke("find: " + names[0]); var element = Wait(names, ct);
            trace?.Invoke("click: " + element.Name); Activate(element);
            trace?.Invoke("clicked"); Pause(350, ct);
        }
        public bool ClickOptional(string[] names)
        { if (Find(names) is not { } e) return false; Activate(e); return true; }
        public void RightClickMod(ModState mod, CancellationToken ct) => ClickElement(ModRow(mod, ct), MouseButton.Right);
        public void DeleteMod(ModState mod, CancellationToken ct)
        {
            var row = ModRow(mod, ct);
            var button = row.FindFirstDescendant(e => e.ByControlType(ControlType.Button).And(e.ByName("Delete")));
            if (button is null) throw new InvalidOperationException("Botão nativo de remoção não encontrado na linha do mod.");
            Activate(button);
        }
        public void ConfirmDeletion(string[] title, CancellationToken ct)
        {
            Wait(title, ct);
            Click(["Delete", "Excluir", "Remover"], ct);
        }
        public void OpenProfile(string name, CancellationToken ct)
        {
            if (IsOpenProfile(name)) return;
            OpenLibrary(ct);
            Activate(ProfileCard(name));
            var watch = Stopwatch.StartNew();
            while (!IsOpenProfile(name) && watch.Elapsed < TimeSpan.FromSeconds(20)) Pause(200, ct);
            if (!IsOpenProfile(name)) throw new InvalidOperationException("O CurseForge não confirmou a abertura do perfil selecionado.");
        }
        private bool IsOpenProfile(string name)
        {
            var title = Root.FindAllDescendants(e => e.ByControlType(ControlType.Text)).Any(e =>
                (e.Name == name || e.Name.StartsWith(name + " by ", StringComparison.Ordinal) || e.Name.StartsWith(name + " por ", StringComparison.Ordinal)) && !HasButtonAncestor(e));
            return title && Root.FindAllDescendants(e => e.ByControlType(ControlType.ListItem)).Any(e =>
                e.Name.StartsWith("Last Played:", StringComparison.Ordinal) || e.Name.StartsWith("Última execução:", StringComparison.Ordinal));
        }
        public void OpenMods(CancellationToken ct)
        {
            foreach (var pattern in new[] { @"^Content\s*\(\d+\)$", @"^Mods\s*\(\d+\)$" })
            {
                var watch = Stopwatch.StartNew();
                AutomationElement? tab = null;
                while (tab is null && watch.Elapsed < TimeSpan.FromSeconds(20))
                {
                    ct.ThrowIfCancellationRequested();
                    tab = Root.FindAllDescendants(e => e.ByControlType(ControlType.Hyperlink))
                        .SingleOrDefault(e => System.Text.RegularExpressions.Regex.IsMatch(e.Name, pattern));
                    if (tab is null) Pause(150, ct);
                }
                if (tab is null) throw new InvalidOperationException("A aba nativa de conteúdo/mods não foi encontrada.");
                Activate(tab); Pause(300, ct);
            }
        }
        public AutomationElement ModRow(ModState mod, CancellationToken ct)
        {
            for (var attempt = 0; attempt < 160; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                var rows = Root.FindAllDescendants(e => e.ByControlType(ControlType.Button))
                    .Where(e => e.Name.Contains(".jar", StringComparison.OrdinalIgnoreCase)).ToList();
                if (rows.Count == 0) { Pause(150, ct); continue; }
                var row = rows.SingleOrDefault(e => e.Name.Contains(mod.FileName, StringComparison.OrdinalIgnoreCase));
                var bounds = Root.BoundingRectangle;
                bool Visible(AutomationElement e) => !e.Properties.IsOffscreen.ValueOrDefault &&
                    e.BoundingRectangle.Top >= bounds.Top && e.BoundingRectangle.Bottom <= bounds.Bottom && e.BoundingRectangle.Width > 0;
                if (row is not null && Visible(row)) return row;
                if (row?.Patterns.ScrollItem.IsSupported == true) { row.Patterns.ScrollItem.Pattern.ScrollIntoView(); Pause(150, ct); continue; }
                var anchor = rows.FirstOrDefault(Visible) ?? throw new InvalidOperationException("A lista nativa de mods não está visível.");
                var rect = anchor.BoundingRectangle;
                Mouse.MoveTo(rect.Left + rect.Width / 2, rect.Top + rect.Height / 2);
                Mouse.Scroll(row is null && attempt == 0 ? 1000 : row is not null && row.BoundingRectangle.Top < rect.Top ? 6 : -6);
                Pause(100, ct);
            }
            throw new InvalidOperationException($"A linha de {mod.Name} não foi encontrada na lista nativa do CurseForge.");
        }
        private static bool HasButtonAncestor(AutomationElement element)
        {
            var depth = 0;
            for (var parent = element.Parent; parent is not null && parent.ControlType != ControlType.Document && depth++ < 20; parent = parent.Parent)
                if (parent.ControlType == ControlType.Button) return true;
            return false;
        }
        private AutomationElement ProfileCard(string name) => Root.FindAllDescendants(e => e.ByControlType(ControlType.Button))
            .SingleOrDefault(e => e.Name.StartsWith(name + " ", StringComparison.Ordinal) &&
                e.FindFirstDescendant(c => c.ByControlType(ControlType.Text).And(c.ByName(name))) is not null)
            ?? throw new InvalidOperationException("Cartão do perfil não encontrado na biblioteca do CurseForge.");
        public void OpenLibrary(CancellationToken ct)
        {
            if (Find(["My Modpacks", "Meus modpacks"]) is null)
            {
                var game = Root.FindAllDescendants(e => e.ByControlType(ControlType.Hyperlink))
                    .FirstOrDefault(e => !e.Properties.IsOffscreen.ValueOrDefault && e.Name == "Minecraft")
                    ?? throw new InvalidOperationException("Abra a biblioteca de Minecraft no CurseForge.");
                Activate(game);
            }
            Click(["My Modpacks", "Meus modpacks"], ct);
        }
        public void ProfileMenu(string name, CancellationToken ct)
        {
            OpenLibrary(ct);
            ClickElement(ProfileCard(name), MouseButton.Right);
        }
        private void ClickProfileMenuItem(string[] names, CancellationToken ct)
        {
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(20))
            {
                ct.ThrowIfCancellationRequested();
                // Library cards also expose Change Version buttons. Only act on the open menu.
                if (Find(names, ControlType.ListItem, Root) is { } item)
                {
                    Activate(item);
                    Pause(350, ct);
                    return;
                }
                Pause(150, ct);
            }
            throw new InvalidOperationException($"O item '{names[0]}' não apareceu no menu nativo do perfil.");
        }
        public void OpenDeleteProfile(string name, CancellationToken ct)
        {
            if (Find(["Delete Profile", "Excluir perfil", "Apagar perfil"], ControlType.Text, Root) is null)
            {
                ProfileMenu(name, ct);
                ClickProfileMenuItem(["Delete Profile", "Excluir perfil", "Apagar perfil"], ct);
            }
            scopeOverride = Root;
            if (Find(["'" + name + "'"], ControlType.Text, Root) is null)
                throw new InvalidOperationException("O diálogo de exclusão não confirma o nome do perfil descartável.");
            SetCheckBox(["Delete Profile Backups", "Excluir backups do perfil"], false, false);
            Click(["Delete", "Excluir", "Apagar"], ct);
        }
        public void OpenOptions(string name, CancellationToken ct)
        {
            if (Find(["Profile Options", "Opções do perfil", "Opções de perfil"], ControlType.Text, Root) is null)
            {
                ProfileMenu(name, ct);
                ClickProfileMenuItem(["Profile Options", "Opções do perfil", "Opções de perfil"], ct);
            }
            // Options has separate accessibility groups for its header, body and footer.
            var fields = Root.FindAllDescendants(e => e.ByControlType(ControlType.Edit));
            if (!fields.Any(e => e.AsTextBox().Text == name))
                throw new InvalidOperationException("As opções abertas não correspondem ao perfil selecionado. Feche esse diálogo e tente novamente.");
            scopeOverride = Root;
        }
        public void OpenVersionDialog(string name, CancellationToken ct)
        {
            ProfileMenu(name, ct);
            ClickProfileMenuItem(["Change Version", "Alterar versão", "Mudar versão"], ct);
            scopeOverride = Root;
            var watch = Stopwatch.StartNew();
            while (Root.FindFirstDescendant(e => e.ByAutomationId("profile-version-update-to-new")) is null && watch.Elapsed < TimeSpan.FromSeconds(20)) Pause(150, ct);
            if (Root.FindFirstDescendant(e => e.ByAutomationId("profile-version-update-to-new")) is null)
                throw new InvalidOperationException("O CurseForge não confirmou o diálogo de versão do perfil selecionado.");
        }
        public string CurrentVersionLabel(CancellationToken ct)
        {
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(20))
            {
                var current = Root.FindAllDescendants(e => e.ByControlType(ControlType.ListItem))
                    .Where(e => e.Name.EndsWith(" - (current)", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (current.Length == 1) return current[0].Name;
                if (current.Length > 1) throw new InvalidOperationException("A versão atual aparece de forma ambígua no CurseForge.");
                Pause(150, ct);
            }
            throw new InvalidOperationException("O CurseForge não confirmou a versão atual do modpack.");
        }
        public void SelectCurrentVersion(CancellationToken ct)
        {
            SelectVersion(CurrentVersionLabel(ct), ct);
        }
        public void SelectVersion(string label, CancellationToken ct)
        {
            Click(["Select version", "Selecionar versão"], ct);
            var trigger = Root.FindAllDescendants(e => e.ByControlType(ControlType.Button))
                .Single(e => e.Name.Contains(" - (", StringComparison.Ordinal));
            if (!trigger.IsEnabled) throw new InvalidOperationException("O seletor nativo de versões não foi ativado.");
            SelectOption([label], ct, [trigger.Name]);
        }
        public void CancelVersion()
        {
            if (Find(["Change Version", "Alterar versão", "Mudar versão"], ControlType.Text, Root) is not null)
                Click(["Cancel", "Cancelar"], CancellationToken.None);
        }
        public void ContinueVersion(CancellationToken ct)
        {
            var button = Wait(["Continue", "Continuar"], ct);
            if (!button.IsEnabled) throw new InvalidOperationException("O CurseForge não permite instalar a versão selecionada neste fluxo.");
            Activate(button); Pause(350, ct);
        }
        public void RenameOption(string oldName, string name)
        {
            var field = Root.FindAllDescendants(e => e.ByControlType(ControlType.Edit)).Single(e => e.AsTextBox().Text == oldName);
            field.AsTextBox().Enter(name);
        }
        public void SetText(string[] names, string value, CancellationToken ct)
        {
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(20))
            {
                var edit = ControlRoot.FindAllDescendants(e => e.ByControlType(ControlType.Edit)).FirstOrDefault(e =>
                    !e.Properties.IsOffscreen.ValueOrDefault && names.Any(n => e.Name.Contains(n, StringComparison.CurrentCultureIgnoreCase)));
                if (edit is not null) { edit.AsTextBox().Enter(value); return; }
                Pause(250, ct);
            }
            throw new InvalidOperationException($"Campo '{names[0]}' não encontrado.");
        }
        public void SelectOption(string[] values, CancellationToken ct, string[]? opener = null)
        {
            if (opener is null)
            {
            var combos = ControlRoot.FindAllDescendants(e => e.ByControlType(ControlType.ComboBox)).Where(e => !e.Properties.IsOffscreen.ValueOrDefault).ToArray();
            foreach (var combo in combos)
            {
                combo.AsComboBox().Expand();
                Pause(200, ct);
                if (Find(values) is { } option) { Activate(option); return; }
                combo.AsComboBox().Collapse();
            }
            if (Find(values) is { } direct) { Activate(direct); return; }
            }
            if (opener is not null && Find(opener, ControlType.Button) is { } trigger)
            {
                Activate(trigger); Pause(250, ct);
                var scope = ControlRoot;
                var allOptions = scope.FindAllDescendants(e => e.ByControlType(ControlType.ListItem));
                var option = allOptions.FirstOrDefault(e => values.Contains(e.Name, StringComparer.OrdinalIgnoreCase));
                if (option?.Patterns.ScrollItem.IsSupported == true) { option.Patterns.ScrollItem.Pattern.ScrollIntoView(); Pause(150, ct); }
                for (var attempt = 0; attempt < 60; attempt++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (Find(values, ControlType.ListItem) is { } item) { Activate(item); return; }
                    var visible = (option?.Parent ?? scope).FindAllDescendants(e => e.ByControlType(ControlType.ListItem))
                        .FirstOrDefault(e => !e.Properties.IsOffscreen.ValueOrDefault && e.BoundingRectangle.Width > 0);
                    if (visible is null) break;
                    var rect = visible.BoundingRectangle;
                    Mouse.MoveTo(rect.Left + rect.Width / 2, rect.Top + rect.Height / 2);
                    Mouse.Scroll(-3); Pause(100, ct);
                }
            }
            throw new InvalidOperationException($"A opção '{values[0]}' não está disponível no CurseForge. Nenhuma versão alternativa foi selecionada.");
        }
        public void SetCheckBox(string[] names, bool value, bool required)
        {
            var checkbox = Find(names, ControlType.CheckBox);
            if (checkbox is null) { if (required) throw new InvalidOperationException($"Opção '{names[0]}' não encontrada."); return; }
            checkbox.AsCheckBox().IsChecked = value;
            Thread.Sleep(100);
            if (checkbox.AsCheckBox().IsChecked != value) throw new InvalidOperationException($"O CurseForge não confirmou a opção '{names[0]}'.");
        }
        public bool SelectTargetProfile(string name, CancellationToken ct)
        {
            var watch = Stopwatch.StartNew();
            AutomationElement? search = null;
            while (search is null && watch.Elapsed < TimeSpan.FromSeconds(20))
            {
                ct.ThrowIfCancellationRequested();
                search = Find(["Search for profile", "Pesquisar perfil", "Buscar perfil"], ControlType.Edit, Root);
                if (search is null) Pause(150, ct);
            }
            if (search is null) throw new InvalidOperationException("O seletor nativo de perfis não abriu.");
            search.AsTextBox().Enter(name); Pause(500, ct);
            var target = Wait([name], ct);
            var depth = 0;
            for (var parent = target.Parent; parent is not null && parent.ControlType != ControlType.Document && depth++ < 20; parent = parent.Parent)
            {
                if (parent.BoundingRectangle.Height > 120) break;
                if (parent.FindAllDescendants(e => e.ByControlType(ControlType.Text)).Any(e =>
                    e.Name.StartsWith("Incompatible modloader", StringComparison.OrdinalIgnoreCase) ||
                    e.Name.StartsWith("Modloader incompatível", StringComparison.OrdinalIgnoreCase) ||
                    e.Name.StartsWith("Carregador de mods incompatível", StringComparison.OrdinalIgnoreCase)))
                {
                    Click(["Close", "Fechar"], ct);
                    return false;
                }
            }
            Activate(target); Pause(350, ct);
            return true;
        }
        public bool InstallSelectedProfile(CancellationToken ct)
        {
            var button = Wait(["Install", "Instalar"], ct);
            if (!button.Properties.IsEnabled.ValueOrDefault)
            {
                Click(["Close", "Fechar"], ct);
                return false;
            }
            Activate(button);
            return true;
        }
        public void FillFileDialog(string path, CancellationToken ct)
        {
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(20))
            {
                ct.ThrowIfCancellationRequested();
                var dialog = FileDialog;
                if (dialog is null) { Pause(250, ct); continue; }
                var edit = dialog.FindAllDescendants(e => e.ByControlType(ControlType.Edit)).FirstOrDefault(e => e.AutomationId == "1148" || e.AutomationId == "1001");
                if (edit is not null)
                {
                    edit.AsTextBox().Enter(path);
                    var open = dialog.FindFirstDescendant(e => e.ByAutomationId("1").And(
                        e.ByControlType(ControlType.Button).Or(e.ByControlType(ControlType.SplitButton))));
                    if (open is null) throw new InvalidOperationException("Botão Abrir não encontrado na caixa nativa.");
                    Activate(open);
                    var closing = Stopwatch.StartNew();
                    while (FileDialog is not null && closing.Elapsed < TimeSpan.FromSeconds(15)) Pause(250, ct);
                    if (FileDialog is not null) throw new InvalidOperationException("O CurseForge não aceitou o arquivo na caixa Abrir.");
                    return;
                }
                Pause(250, ct);
            }
            throw new InvalidOperationException("A caixa nativa de seleção do arquivo não foi encontrada.");
        }
        private AutomationElement? FileDialog
        {
            get
            {
                var handles = NativeWindows("#32770");
                if (handles.Count > 1) throw new InvalidOperationException("Há mais de uma caixa de diálogo do CurseForge aberta.");
                if (handles.Count == 0) return null;
                var dialog = automation.FromHandle(handles[0]);
                if (dialog.Name is not ("Select File" or "Open" or "Abrir" or "Selecionar arquivo"))
                    throw new InvalidOperationException("Feche a caixa de diálogo pendente do CurseForge antes de importar o perfil.");
                return dialog;
            }
        }
        public bool HasFileDialog => FileDialog is not null;
        public void BringToFront(CancellationToken ct) { Root.SetForeground(); Pause(250, ct); }
        public void Pause(int ms, CancellationToken ct) { if (ct.WaitHandle.WaitOne(ms)) ct.ThrowIfCancellationRequested(); }
        public void Dispose() => automation.Dispose();
    }
}
