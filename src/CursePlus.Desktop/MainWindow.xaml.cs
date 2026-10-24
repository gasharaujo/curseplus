using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using CursePlus.Core;
using CursePlus.Desktop.Native;
using Microsoft.Win32;

namespace CursePlus.Desktop;

public partial class MainWindow : Window
{
    private readonly ProfileReader reader = new();
    private readonly BundleArchive archive = new();
    private readonly AppStore store;
    private readonly CurseForgeAutomation native;
    private CancellationTokenSource? operation;
    private ProfileState? selected;
    private ChangeBundle? loaded;
    private string? loadedPath;
    private ChangePlan? preview;
    private bool initialized;
    private bool busy;
    private readonly ObservableCollection<PreviewRow> rows = [];
    private ModPreset? selectedPreset;
    private List<PresetMod> presetMods = [];

    public MainWindow(string? dataRoot = null)
    {
        InitializeComponent();
        store = new(dataRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CursePlus"));
        native = new(store, reader);
        PlanGrid.ItemsSource = rows;
    }

    private async void WindowLoaded(object sender, RoutedEventArgs e)
    {
        if (initialized) return;
        initialized = true;
        await Work(async ct =>
        {
            await store.LoadAsync(ct);
            InstancesPath.Text = store.Settings.InstancesDirectory;
            ExecutablePath.Text = string.IsNullOrEmpty(store.Settings.CurseForgeExecutable) ? CurseForgeAutomation.DetectExecutable() : store.Settings.CurseForgeExecutable;
            await RefreshProfiles(ct);
            await RefreshLibraries(ct);
        }, "Lendo perfis…");
    }

    private async Task Work(Func<CancellationToken, Task> work, string message)
    {
        if (busy) return;
        busy = true; operation = new();
        CancelButton.Visibility = Visibility.Visible; CancelButton.IsEnabled = true;
        Progress.Visibility = Visibility.Visible; Progress.IsIndeterminate = true;
        StatusText.Text = message; ApplyButton.IsEnabled = false;
        try { await work(operation.Token); if (StatusText.Text == message) StatusText.Text = "Concluído."; }
        catch (OperationCanceledException) { StatusText.Text = "Operação interrompida. Atualize a prévia antes de retomar."; }
        catch (Exception error)
        {
            StatusText.Text = error.Message;
            MessageBox.Show(this, error.Message, "Não foi possível concluir", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            busy = false; operation.Dispose(); operation = null;
            CancelButton.Visibility = Visibility.Collapsed; Progress.Visibility = Visibility.Collapsed;
            ApplyButton.IsEnabled = preview is { CanApply: true, Operations.Count: > 0 };
        }
    }

    private ProfileState RequireProfile() => selected ?? throw new InvalidOperationException("Selecione um perfil.");
    private Task<ProfileState> Capture(ProfileState profile, CancellationToken ct) =>
        Task.Run(async () => await store.EnsureOriginAsync(await reader.ReadAsync(profile.Directory, true, ct), ct), ct);

    private async Task RefreshProfiles(CancellationToken ct)
    {
        var id = (ProfilePicker.SelectedItem as ProfileState)?.Id;
        var targetId = (TargetPicker.SelectedItem as ProfileState)?.Id;
        var profiles = (await reader.DiscoverAsync(store.Settings.InstancesDirectory, ct)).Select(store.BindOrigin).ToList();
        ProfilePicker.ItemsSource = profiles; TargetPicker.ItemsSource = profiles.ToList();
        ProfilePicker.SelectedItem = profiles.FirstOrDefault(p => p.Id == id) ?? profiles.FirstOrDefault();
        TargetPicker.SelectedItem = profiles.FirstOrDefault(p => p.Id == targetId) ?? ProfilePicker.SelectedItem;
        UpdateProfile();
        if (profiles.Count == 0) StatusText.Text = "Nenhum perfil encontrado. Escolha a pasta Instances em Configurações.";
    }

    private void UpdateProfile()
    {
        selected = ProfilePicker.SelectedItem is ProfileState profile ? store.BindOrigin(profile) : null;
        ModsGrid.ItemsSource = selected?.Mods;
        ProfileSummary.Text = selected is null ? "" : $"{selected.MinecraftVersion} · {selected.Loader.Family} {selected.Loader.Version}";
        InventorySummary.Text = selected is null ? "Nenhum perfil selecionado." :
            $"{selected.Pack?.Name ?? "Perfil próprio"} {selected.Pack?.Version} · {selected.Mods.Count} mods · {selected.Mods.Count(m => !m.Enabled)} desativados";
        if (selected is { MissingMods.Count: > 0 }) InventorySummary.Text += $" · {selected.MissingMods.Count} arquivos ausentes nos metadados do CurseForge";
        SnapshotPicker.ItemsSource = store.Settings.Bundles.Where(b => b.OriginId == selected?.OriginId || b.ProfileId == selected?.Id).OrderByDescending(b => b.CreatedAt).ToList();
    }
    private void ProfileChanged(object sender, SelectionChangedEventArgs e) { if (initialized) UpdateProfile(); }
    private async void RefreshClick(object sender, RoutedEventArgs e) => await Work(RefreshProfiles, "Atualizando instâncias…");

    private async Task RefreshLibraries(CancellationToken ct)
    {
        var id = selectedPreset?.Id;
        var presets = await store.GetPresetsAsync(ct);
        PresetPicker.ItemsSource = presets; PresetPicker.SelectedItem = presets.FirstOrDefault(p => p.Id == id);
        UpdateProfile();
        var journals = new List<JournalRow>();
        foreach (var path in Directory.EnumerateFiles(store.JournalDirectory, "*.json"))
        {
            var journal = await AppStore.ReadJsonAsync<OperationJournal>(path, ct);
            if (journal is not null) journals.Add(new(journal, $"{journal.StartedAt.LocalDateTime:dd/MM HH:mm} · {journal.Status} · {Path.GetFileName(journal.TargetDirectory)}"));
        }
        JournalPicker.ItemsSource = journals.OrderByDescending(j => j.Journal.StartedAt).ToList();
    }

    private async Task<(StoredBundle Record, ChangeBundle Bundle)> EnsureBaseline(ProfileState source, CancellationToken ct)
    {
        var existing = store.FindBaseline(source);
        if (existing is not null) return (existing, await archive.ReadAsync(existing.Path, ct, false));
        ProfileState original;
        if (source.Pack is not null)
        {
            StatusText.Text = "Criando referência limpa da versão do pack pelo CurseForge…";
            var name = $"{source.Name} - original {source.Pack.FileId} - {DateTime.Now:HHmmss}";
            original = await Capture(await native.CreateProfileAsync(source, name, ct), ct);
        }
        else original = source;
        var bundle = new ChangeBundle { Name = $"Original · {source.Pack?.Name ?? source.Name} {source.Pack?.Version}", Kind = BundleKind.Baseline, Desired = original };
        var record = await store.SaveBundleAsync(bundle, original.Directory, original.Id, ct);
        await RefreshLibraries(ct);
        return (record, bundle);
    }

    private async void CaptureOriginalClick(object sender, RoutedEventArgs e) => await Work(async ct =>
    {
        var (_, baseline) = await EnsureBaseline(await Capture(RequireProfile(), ct), ct);
        StatusText.Text = $"Referência original salva: {baseline.Name}";
    }, "Preparando referência original…");

    private async void CompareOriginalClick(object sender, RoutedEventArgs e) => await Work(async ct =>
    {
        var source = await Capture(RequireProfile(), ct);
        var (_, baseline) = await EnsureBaseline(source, ct);
        var comparison = new ChangePlanner().Build(baseline.Desired, source);
        ClearPreview(); SetRows(comparison.Operations, false);
        PlanGrid.IsReadOnly = true;
        PlanSummary.Text = $"{comparison.Operations.Count} diferenças entre o original e {source.Name}. Comparação somente leitura.";
        LoadedBundleText.Text = "Comparação com original"; Tabs.SelectedIndex = 1;
        StatusText.Text = "Comparação concluída.";
    }, "Comparando com o original…");

    private async void ExportClick(object sender, RoutedEventArgs e)
    {
        if (busy || selected is null) return;
        await Work(async ct =>
        {
            var reference = store.FindBaseline(store.BindOrigin(RequireProfile())) ?? throw new InvalidOperationException("Use Definir original antes de exportar. A exportação leve contém somente diferenças em relação ao original.");
            var baseline = await archive.ReadAsync(reference.Path, ct, false);
            var source = await Capture(RequireProfile(), ct);
            var bundle = ChangeDelta.Create(new() { Name = source.Name, Kind = BundleKind.Sync, Desired = source }, baseline.Desired);
            var extension = bundle.Desired.Files.Count == 0 ? ".curseplus.json" : ".curseplus.zip";
            var dialog = new SaveFileDialog { Filter = bundle.Desired.Files.Count == 0 ? "Alterações CursePlus|*.curseplus.json" : "Alterações com arquivos personalizados|*.curseplus.zip", FileName = SafeName(source.Name) + extension };
            if (dialog.ShowDialog(this) != true) return;
            await archive.WriteAsync(dialog.FileName, bundle, source.Directory, ct);
            StatusText.Text = $"Exportado: {new FileInfo(dialog.FileName).Length:N0} bytes · {bundle.Desired.Mods.Count} mods alterados · {bundle.Desired.Files.Count} arquivos anexados.";
            if (source.MissingMods.Count > 0) StatusText.Text += $" {source.MissingMods.Count} mods registrados mas ausentes foram listados no resumo e excluídos do estado exportado.";
        }, "Exportando alterações…");
    }

    private async void SaveSnapshotClick(object sender, RoutedEventArgs e)
    {
        if (busy || selected is null) return;
        var name = Ask("Salvar ponto", "Nome do ponto de restauração", $"{selected.Name} · {DateTime.Now:dd/MM HH:mm}");
        if (name is null) return;
        await Work(async ct =>
        {
            var source = await Capture(RequireProfile(), ct);
            await store.SaveBundleAsync(new() { Name = name, Kind = BundleKind.Snapshot, Desired = source }, source.Directory, ct: ct);
            if (source.Pack is null && store.FindBaseline(source) is null)
                await store.SaveBundleAsync(new() { Name = "Original · " + source.Name, Kind = BundleKind.Baseline, Desired = source }, source.Directory, ct: ct);
            await RefreshLibraries(ct); StatusText.Text = "Ponto de restauração salvo.";
        }, "Salvando estado da instância…");
    }

    private async void LoadBundleClick(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        var dialog = new OpenFileDialog { Filter = "Alterações e pontos CursePlus|*.curseplus.json;*.curseplus.zip" };
        if (dialog.ShowDialog(this) == true) await Work(ct => LoadBundle(dialog.FileName, ct), "Verificando pacote…");
    }
    private async Task LoadBundle(string path, CancellationToken ct)
    {
        loaded = await archive.ReadAsync(path, ct); loadedPath = path;
        LoadedBundleText.Text = $"{loaded.Name} · {loaded.Desired.Pack?.Name ?? "Perfil próprio"} {loaded.Desired.Pack?.Version} · {loaded.Desired.Mods.Count} mods{(loaded.Delta is null ? "" : " alterados · original necessário")}";
        PlanGrid.IsReadOnly = false; ClearPreview(); Tabs.SelectedIndex = 1;
    }
    private void ClearPreview() { preview = null; rows.Clear(); ApplyButton.IsEnabled = false; }
    private void PreviewInputChanged(object sender, SelectionChangedEventArgs e) { if (initialized) ClearPreview(); }
    private async void PreviewClick(object sender, RoutedEventArgs e) => await Work(PreparePreview, "Preparando prévia…");
    private async Task PreparePreview(CancellationToken ct)
    {
        if (loaded is null) throw new InvalidOperationException("Abra um pacote antes de gerar a prévia.");
        var target = store.BindOrigin(TargetPicker.SelectedItem as ProfileState ?? throw new InvalidOperationException("Selecione o destino."));
        if (loaded.Desired.Pack?.ProjectId != target.Pack?.ProjectId ||
            (loaded.Desired.Pack is null && loaded.Desired.OriginId != target.OriginId && loaded.Desired.Id != target.Id))
            throw new InvalidOperationException("Escolha o mesmo modpack ou vincule explicitamente o perfil próprio à origem.");
        await ResolveLoaded(ct);
        preview = new ChangePlanner().Build(await Capture(target, ct), loaded.Desired, ModePicker.SelectedIndex == 1 ? ApplyMode.Merge : ApplyMode.PreferSource, loaded.Baseline);
        SetRows(preview.Operations, preview.Mode == ApplyMode.Merge);
        PlanGrid.IsReadOnly = ModePicker.SelectedIndex != 1;
        PlanSummary.Text = preview.Blockers.Count > 0 ? string.Join("\n", preview.Blockers) :
            $"{preview.Operations.Count} operações · {preview.Operations.Count(o => o.IsConflict)} conflitos · {preview.Operations.Count(o => o.UsesFileException)} arquivos personalizados. " +
            (ModePicker.SelectedIndex == 1 ? "Marque MANTER B nos conflitos que deseja preservar." : "A origem define o conteúdo sincronizado do destino.");
        StatusText.Text = !preview.CanApply ? "A sincronização está bloqueada para esse destino." : preview.Operations.Count == 0 ? "O destino já corresponde à origem." : "Prévia pronta para revisão.";
    }
    private async void ApplyClick(object sender, RoutedEventArgs e)
    {
        if (busy || preview is not { CanApply: true } || loadedPath is null || loaded is null) return;
        PlanGrid.CommitEdit(DataGridEditingUnit.Cell, true); PlanGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var choices = rows.Where(r => r.IsConflict && r.KeepTarget).GroupBy(r => r.Key).ToDictionary(g => g.Key, _ => ConflictChoice.Target);
        var plan = new ChangePlanner().Build(preview.Before, loaded.Desired, preview.Mode, loaded.Baseline, choices);
        var path = loadedPath;
        await Work(async ct =>
        {
            var progress = new Progress<ExecutionProgress>(p => { Progress.IsIndeterminate = false; Progress.Maximum = Math.Max(1, p.Total); Progress.Value = p.Completed; StatusText.Text = p.Message; });
            try { await Task.Run(() => new SyncExecutor(native, reader, store).ApplyAsync(plan, path, progress, ct), ct); ClearPreview(); StatusText.Text = "Sincronização concluída e verificada."; }
            finally { await RefreshProfiles(CancellationToken.None); await RefreshLibraries(CancellationToken.None); }
        }, "Aplicando pelo CurseForge…");
    }

    private async void BindOriginClick(object sender, RoutedEventArgs e)
    {
        if (busy || loaded is null || loaded.Desired.Pack is not null || TargetPicker.SelectedItem is not ProfileState target) return;
        if (target.Pack is not null) { StatusText.Text = "Um modpack não pode ser vinculado a um perfil próprio."; return; }
        if (MessageBox.Show(this, $"Vincular '{target.Name}' à origem '{loaded.Desired.Name}'?", "Vincular perfis próprios", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        await Work(async ct => { await store.LinkOriginAsync(target, loaded.Desired.OriginId, ct); await RefreshProfiles(ct); }, "Vinculando origem…");
    }
    private async void NewProfileClick(object sender, RoutedEventArgs e)
    {
        if (busy || loaded is null) return;
        var name = Ask("Novo perfil", "Nome no CurseForge", loaded.Desired.Name + " - sincronizado");
        if (name is not null) await Work(ct => CreateFromLoaded(name, ct), "Criando perfil pelo CurseForge…");
    }
    private async Task CreateFromLoaded(string name, CancellationToken ct)
    {
        if (loaded is null) return;
        await ResolveLoaded(ct);
        var created = await native.CreateProfileAsync(loaded.Desired, name, ct, loadedPath);
        await RefreshProfiles(ct);
        TargetPicker.SelectedItem = ((List<ProfileState>)TargetPicker.ItemsSource).FirstOrDefault(p => p.Id == created.Id);
        await PreparePreview(ct); StatusText.Text = "Perfil criado. Revise a prévia e aplique as personalizações.";
    }

    private async Task ResolveLoaded(CancellationToken ct)
    {
        if (loaded?.Delta is null) return;
        if (store.FindBaseline(loaded.Desired) is null)
        {
            if (loaded.Desired.Pack is null)
                throw new InvalidOperationException("Este perfil próprio exige o mesmo original salvo na origem. Defina o original no destino e vincule a origem antes de aplicar.");
            await EnsureBaseline(loaded.Desired, ct);
        }
        loaded = await store.ResolveBundleAsync(loaded, ct);
    }

    private async void RestoreSnapshotClick(object sender, RoutedEventArgs e) => await Restore(false, false);
    private async void RestoreSnapshotNewClick(object sender, RoutedEventArgs e) => await Restore(false, true);
    private async void RestoreOriginalClick(object sender, RoutedEventArgs e) => await Restore(true, false);
    private async void RestoreOriginalNewClick(object sender, RoutedEventArgs e) => await Restore(true, true);
    private async Task Restore(bool original, bool newProfile)
    {
        if (busy || selected is null) return;
        var name = newProfile ? Ask("Restaurar em novo perfil", "Nome no CurseForge", selected.Name + " - restaurado") : null;
        if (newProfile && name is null) return;
        var saved = SnapshotPicker.SelectedItem as StoredBundle;
        await Work(async ct =>
        {
            var source = await Capture(RequireProfile(), ct);
            var record = original ? (await EnsureBaseline(source, ct)).Record : saved ?? throw new InvalidOperationException("Selecione um ponto salvo.");
            await LoadBundle(record.Path, ct); ModePicker.SelectedIndex = 0;
            if (newProfile) await CreateFromLoaded(name!, ct);
            else { TargetPicker.SelectedItem = ((List<ProfileState>)TargetPicker.ItemsSource).FirstOrDefault(p => p.Id == source.Id); await PreparePreview(ct); }
            StatusText.Text = "Restauração preparada. Revise a prévia antes de aplicar.";
        }, "Preparando restauração…");
    }

    private async void PresetFromSelectionClick(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        var mods = ModsGrid.SelectedItems.Cast<ModState>().ToList();
        if (mods.Count == 0) { StatusText.Text = "Selecione os mods. Use Ctrl ou Shift para escolher vários."; return; }
        var name = Ask("Criar quick add", "Nome da lista", "Meus mods");
        if (name is null) return;
        await Work(async ct =>
        {
            var preset = new ModPreset { Name = name, Mods = mods.Select(m => new PresetMod(m.ProjectId, m.Name, m.Website)).ToList() };
            await store.SavePresetAsync(preset, ct); selectedPreset = preset; await RefreshLibraries(ct); Tabs.SelectedIndex = 2;
        }, "Salvando lista…");
    }
    private void PresetChanged(object sender, SelectionChangedEventArgs e)
    {
        selectedPreset = PresetPicker.SelectedItem as ModPreset;
        PresetName.Text = selectedPreset?.Name ?? ""; presetMods = selectedPreset?.Mods.ToList() ?? [];
        PresetModsGrid.ItemsSource = presetMods;
    }
    private async void ImportPresetClick(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        var dialog = new OpenFileDialog { Filter = "Lista quick add|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        await Work(async ct =>
        {
            var preset = await AppStore.ReadJsonAsync<ModPreset>(dialog.FileName, ct) ?? throw new InvalidDataException("Lista vazia.");
            AppStore.ValidatePreset(preset); preset = preset with { Id = Guid.NewGuid().ToString("N") };
            await store.SavePresetAsync(preset, ct); selectedPreset = preset; await RefreshLibraries(ct);
        }, "Importando lista…");
    }
    private async void ExportPresetClick(object sender, RoutedEventArgs e)
    {
        if (busy || selectedPreset is null) return;
        var preset = selectedPreset with { Name = PresetName.Text.Trim(), Mods = presetMods };
        var dialog = new SaveFileDialog { Filter = "Lista quick add|*.json", FileName = SafeName(PresetName.Text) + ".curseplus.preset.json" };
        if (dialog.ShowDialog(this) == true) await Work(ct => { AppStore.ValidatePreset(preset); return AppStore.WriteJsonAsync(dialog.FileName, preset, ct); }, "Exportando lista…");
    }
    private async void SavePresetClick(object sender, RoutedEventArgs e)
    {
        if (busy || selectedPreset is null) return;
        await Work(async ct => { selectedPreset = selectedPreset with { Name = PresetName.Text.Trim(), Mods = presetMods }; await store.SavePresetAsync(selectedPreset, ct); await RefreshLibraries(ct); }, "Salvando lista…");
    }
    private void RemovePresetModsClick(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        var ids = PresetModsGrid.SelectedItems.Cast<PresetMod>().Select(m => m.ProjectId).ToHashSet();
        presetMods = presetMods.Where(m => !ids.Contains(m.ProjectId)).ToList(); PresetModsGrid.ItemsSource = presetMods;
    }
    private void AddPresetModsClick(object sender, RoutedEventArgs e)
    {
        if (busy || selectedPreset is null) return;
        var additions = ModsGrid.SelectedItems.Cast<ModState>().Select(m => new PresetMod(m.ProjectId, m.Name, m.Website));
        presetMods = presetMods.Concat(additions).DistinctBy(m => m.ProjectId).OrderBy(m => m.Name).ToList();
        PresetModsGrid.ItemsSource = presetMods;
        StatusText.Text = "Seleção adicionada. Salve a lista para conservar as alterações.";
    }
    private async void ApplyPresetClick(object sender, RoutedEventArgs e)
    {
        if (busy || selectedPreset is null || selected is null) return;
        var mods = presetMods.ToList();
        await Work(async ct =>
        {
            var target = await Capture(RequireProfile(), ct);
            await native.EnsureReadyAsync(ct); await native.CheckProfileAsync(target, ct);
            await store.SaveBundleAsync(new() { Name = "Antes do quick add · " + target.Name, Kind = BundleKind.Snapshot, Desired = target }, target.Directory, ct: ct);
            var failures = new List<string>();
            var unavailable = new List<string>();
            foreach (var mod in mods)
            {
                ct.ThrowIfCancellationRequested(); StatusText.Text = "Quick add: " + mod.Name;
                try { if (await native.InstallCompatibleModAsync(target, mod, ct) is null) unavailable.Add(mod.Name); }
                catch (OperationCanceledException) { throw; }
                catch (Exception error) { failures.Add($"{mod.Name}: {error.Message}"); break; }
            }
            await RefreshProfiles(ct); await RefreshLibraries(ct);
            StatusText.Text = failures.Count == 0 ? $"Quick add concluído · {mods.Count - unavailable.Count} projetos presentes · {unavailable.Count} indisponíveis." : "Quick add interrompido. O ponto anterior foi preservado.";
            if (failures.Count > 0) MessageBox.Show(this, string.Join('\n', failures), "Itens não instalados");
            else if (unavailable.Count > 0) MessageBox.Show(this, string.Join('\n', unavailable), "Sem versão compatível no CurseForge");
        }, "Aplicando quick add…");
    }

    private void ChooseInstancesClick(object sender, RoutedEventArgs e)
    { var dialog = new OpenFolderDialog { Title = "Pasta Instances do CurseForge" }; if (dialog.ShowDialog(this) == true) InstancesPath.Text = dialog.FolderName; }
    private void ChooseExecutableClick(object sender, RoutedEventArgs e)
    { var dialog = new OpenFileDialog { Filter = "CurseForge|CurseForge.exe" }; if (dialog.ShowDialog(this) == true) ExecutablePath.Text = dialog.FileName; }
    private async void SaveSettingsClick(object sender, RoutedEventArgs e) => await Work(async ct =>
    {
        if (!Directory.Exists(InstancesPath.Text)) throw new DirectoryNotFoundException("Pasta de instâncias não encontrada.");
        await store.SaveSettingsAsync(store.Settings with { InstancesDirectory = Path.GetFullPath(InstancesPath.Text.Trim()), CurseForgeExecutable = ExecutablePath.Text.Trim() }, ct);
        await RefreshProfiles(ct);
    }, "Salvando configurações…");
    private async void CheckConnectionClick(object sender, RoutedEventArgs e) => await Work(async ct =>
    { await native.EnsureReadyAsync(ct); StatusText.Text = "Conectado à interface do CurseForge."; }, "Aguardando a biblioteca do CurseForge…");
    private async void ExportDiagnosticClick(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        var dialog = new SaveFileDialog { Filter = "Diagnóstico de interface|*.json", FileName = "curseplus-interface.json" };
        if (dialog.ShowDialog(this) == true) await Work(ct => Task.Run(() => CurseForgeAutomation.DumpUi(dialog.FileName), ct), "Lendo controles da interface…");
    }
    private void OpenDataClick(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo(store.Root) { UseShellExecute = true });
    private async void ResumeClick(object sender, RoutedEventArgs e)
    {
        if (busy || JournalPicker.SelectedItem is not JournalRow row) return;
        await Work(async ct =>
        {
            await LoadBundle(row.Journal.ArchivePath, ct);
            if (row.Journal.Desired is { } desired)
            {
                var resumePath = Path.Combine(store.Root, "staging", "resume-" + Guid.NewGuid().ToString("N") + ".curseplus.zip");
                await ResolveLoaded(ct);
                var resumed = loaded! with { Desired = desired };
                if (loaded.Baseline is not null) resumed = ChangeDelta.Create(resumed, loaded.Baseline);
                var previous = await store.ReadBundleAsync(row.Journal.BackupPath, ct);
                await archive.ComposeAsync(resumePath, resumed, loaded.ContentSources.Concat(previous.ContentSources).ToList(), ct);
                await LoadBundle(resumePath, ct);
            }
            TargetPicker.SelectedItem = ((List<ProfileState>)TargetPicker.ItemsSource).FirstOrDefault(p => p.Id == row.Journal.TargetId) ?? throw new InvalidOperationException("O perfil da operação não está disponível.");
            ModePicker.SelectedIndex = row.Journal.Desired is null ? (int)row.Journal.Mode : 0;
            await PreparePreview(ct);
            StatusText.Text = "Prévia recalculada do estado atual. Revise os conflitos novamente antes de retomar.";
        }, "Revisando operação anterior…");
    }
    private void CancelClick(object sender, RoutedEventArgs e)
    { operation?.Cancel(); CancelButton.IsEnabled = false; StatusText.Text = "Interrompendo após a etapa atual…"; }
    private static string SafeName(string text) => string.Concat(text.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
    private string? Ask(string title, string label, string initial)
    {
        var window = new Window { Title = title, Owner = this, Width = 480, Height = 205, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 10) });
        var input = new TextBox { Text = initial }; panel.Children.Add(input);
        var button = new Button { Content = "Confirmar", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 17, 0, 0), IsDefault = true };
        button.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(input.Text)) window.DialogResult = true; };
        panel.Children.Add(button); window.Content = panel;
        window.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        return window.ShowDialog() == true ? input.Text.Trim() : null;
    }
    private sealed record JournalRow(OperationJournal Journal, string Name);
    internal async Task CaptureScreenshotsAsync(string directory)
    {
        for (var i = 0; i < 100 && (!initialized || busy); i++) await Task.Delay(100);
        Directory.CreateDirectory(directory);
        var counts = new List<object>();
        for (var tab = 0; tab < Tabs.Items.Count; tab++)
        {
            Tabs.SelectedIndex = tab; UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            var content = (FrameworkElement)Content;
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(content);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var output = File.Create(Path.Combine(directory, $"tab-{tab}.png")); encoder.Save(output);
            counts.Add(new { Tab = tab, Profiles = ProfilePicker.Items.Count, Mods = ModsGrid.Items.Count });
        }
        await AppStore.WriteJsonAsync(Path.Combine(directory, "smoke-result.json"), counts);
    }
    private void SetRows(IEnumerable<PlannedOperation> operations, bool canChoose)
    {
        rows.Clear();
        foreach (var op in operations)
        {
            var row = new PreviewRow(op, canChoose);
            row.SelectionChanged += changed =>
            {
                foreach (var sibling in rows.Where(r => r.Key == changed.Key && r != changed)) sibling.SetMergedChoice(changed.KeepTarget);
            };
            rows.Add(row);
        }
    }
    public sealed class PreviewRow(PlannedOperation op, bool canChoose = true) : System.ComponentModel.INotifyPropertyChanged
    {
        public string Key => op.Key;
        public string Description => op.Description;
        public string Detail => op.IsConflict ? op.TargetDescription ?? "Conflito com o destino" : op.UsesFileException ? "Arquivo personalizado" : "Pelo CurseForge";
        public bool IsConflict => op.IsConflict;
        public bool CanChoose => op.IsConflict && canChoose;
        private bool keep;
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        public event Action<PreviewRow>? SelectionChanged;
        internal void SetMergedChoice(bool value)
        { keep = value; PropertyChanged?.Invoke(this, new(nameof(KeepTarget))); }
        public bool KeepTarget
        {
            get => keep;
            set { if (!CanChoose || keep == value) return; SetMergedChoice(value); SelectionChanged?.Invoke(this); }
        }
    }
}
