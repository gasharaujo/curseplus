namespace CursePlus.Core;

public sealed class ChangePlanner
{
    public ChangePlan Build(ProfileState target, ProfileState source, ApplyMode mode = ApplyMode.PreferSource,
        ProfileState? baseline = null, IReadOnlyDictionary<string, ConflictChoice>? choices = null)
    {
        var blockers = new List<string>();
        if (source.Pack is not null && target.Pack?.ProjectId != source.Pack.ProjectId)
            blockers.Add("A sincronização completa exige o mesmo projeto de modpack. Use quick add para compartilhar mods entre packs.");
        if (source.Pack is null && target.Pack is not null)
            blockers.Add("Um perfil próprio não pode substituir um modpack publicado. Escolha um perfil próprio vinculado.");
        if (source.Pack is null && (string.IsNullOrEmpty(source.OriginId) || (source.OriginId != target.OriginId && source.Id != target.Id)))
            blockers.Add("Vincule explicitamente este perfil próprio à origem antes de sincronizar.");
        var packChange = source.Pack?.FileId != target.Pack?.FileId;
        if (!packChange && source.MinecraftVersion != target.MinecraftVersion)
            blockers.Add("A versão do Minecraft difere. Crie um novo perfil pelo CurseForge para restaurar este estado.");
        if (!packChange && !source.Loader.Family.Equals(target.Loader.Family, StringComparison.OrdinalIgnoreCase))
            blockers.Add("O tipo de modloader difere. Crie um novo perfil pelo CurseForge para restaurar este estado.");
        if (source.Mods.Any(m => m.ProjectId <= 0 || m.FileId <= 0)) blockers.Add("Há referências de mods sem IDs válidos.");
        if (source.Mods.Select(m => m.ProjectId).Distinct().Count() != source.Mods.Count) blockers.Add("Mods duplicados na origem.");
        if (target.Mods.Select(m => m.ProjectId).Distinct().Count() != target.Mods.Count) blockers.Add("Mods duplicados no destino.");
        if (blockers.Count > 0) return new() { Before = target, Desired = source, Mode = mode, Blockers = blockers };

        var operations = new List<PlannedOperation>();
        bool KeepTarget(string key) => mode == ApplyMode.Merge && choices?.GetValueOrDefault(key) == ConflictChoice.Target;
        var targetMods = target.Mods.ToDictionary(m => m.ProjectId);
        var sourceMods = source.Mods.ToDictionary(m => m.ProjectId);
        var baselineMods = baseline?.Mods.ToDictionary(m => m.ProjectId) ?? [];
        string FileKey(FileState file)
        {
            if (file.Kind == FileKind.LocalMod)
            {
                var filename = file.Path.Split('/')[^1].TrimDisabled();
                var mod = source.Mods.Concat(target.Mods).FirstOrDefault(m => m.FileName.Equals(filename, StringComparison.OrdinalIgnoreCase));
                if (mod is not null) return $"mod:{mod.ProjectId}";
            }
            return "file:" + file.Path.ToLowerInvariant();
        }
        var desiredMods = new Dictionary<long, ModState>(sourceMods);
        var desiredFiles = source.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        var targetFiles = target.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        var baseFiles = baseline?.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase) ?? new(StringComparer.OrdinalIgnoreCase);
        if (packChange) operations.Add(new() { Kind = OperationKind.ChangePackVersion, Key = "pack", Description = $"Modpack: {target.Pack?.Version} → {source.Pack?.Version} (arquivo {source.Pack?.FileId})" });
        if (!packChange && source.Loader != target.Loader) operations.Add(new() { Kind = OperationKind.ChangeLoader, Key = "loader", Description = $"Modloader: {target.Loader.Version} → {source.Loader.Version}" });

        foreach (var current in target.Mods)
        {
            var key = $"mod:{current.ProjectId}";
            if (!sourceMods.ContainsKey(current.ProjectId))
            {
                var removedBySource = baselineMods.ContainsKey(current.ProjectId);
                if (mode == ApplyMode.Merge && (!removedBySource || KeepTarget(key))) desiredMods[current.ProjectId] = current;
                else operations.Add(new() { Kind = OperationKind.RemoveMod, Key = key, Mod = current, Description = $"Remover {current.Name}", IsConflict = removedBySource && mode == ApplyMode.Merge });
            }
        }
        foreach (var mod in source.Mods)
        {
            var key = $"mod:{mod.ProjectId}";
            targetMods.TryGetValue(mod.ProjectId, out var current);
            var conflict = current is not null && (current.FileId != mod.FileId || current.Enabled != mod.Enabled ||
                current.Modified != mod.Modified || (current.Modified && current.Sha256 != mod.Sha256));
            if (conflict && KeepTarget(key)) { desiredMods[mod.ProjectId] = current!; continue; }
            // A native pack change can replace any mod, so reconcile every desired mod after it.
            if (packChange || current is null || current.FileId != mod.FileId || (current.Modified && !mod.Modified))
                operations.Add(new() { Kind = OperationKind.InstallMod, Key = key, Mod = mod, Description = $"Instalar {mod.Name} · arquivo {mod.FileId}", IsConflict = conflict, TargetDescription = current is null ? null : $"Destino: arquivo {current.FileId}" });
            if (packChange || current is null || current.Enabled != mod.Enabled)
                operations.Add(new() { Kind = OperationKind.SetModEnabled, Key = key, Mod = mod, Description = $"{(mod.Enabled ? "Ativar" : "Desativar")} {mod.Name}", IsConflict = conflict });
        }
        foreach (var file in target.Files)
        {
            var key = FileKey(file);
            if (desiredFiles.ContainsKey(file.Path)) continue;
            var removedBySource = baseFiles.ContainsKey(file.Path);
            if (mode == ApplyMode.Merge && (!removedBySource || KeepTarget(key))) desiredFiles[file.Path] = file;
            else operations.Add(new() { Kind = OperationKind.DeleteFile, Key = key, File = file, Description = $"Remover arquivo: {file.Path}", IsConflict = removedBySource && mode == ApplyMode.Merge });
        }
        foreach (var file in source.Files)
        {
            var key = FileKey(file);
            targetFiles.TryGetValue(file.Path, out var current);
            var conflict = current is not null && current.Sha256 != file.Sha256;
            if (conflict && KeepTarget(key)) { desiredFiles[file.Path] = current!; continue; }
            if (packChange || current is null || current.Sha256 != file.Sha256)
                operations.Add(new() { Kind = OperationKind.WriteFile, Key = key, File = file, Description = $"Aplicar arquivo: {file.Path}", IsConflict = conflict, TargetDescription = current is null ? null : "Destino possui outra versão do arquivo" });
        }
        // A preserved modified mod must keep its own bytes as well as its metadata.
        foreach (var mod in target.Mods)
        {
            if (!KeepTarget($"mod:{mod.ProjectId}")) continue;
            var sourceMod = source.Mods.FirstOrDefault(m => m.ProjectId == mod.ProjectId);
            var sourcePaths = source.Files.Where(f => f.Kind == FileKind.LocalMod &&
                (f.Path.Split('/')[^1].TrimDisabled().Equals(sourceMod?.FileName, StringComparison.OrdinalIgnoreCase) ||
                 f.Path.Split('/')[^1].TrimDisabled().Equals(mod.FileName, StringComparison.OrdinalIgnoreCase))).Select(f => f.Path).ToList();
            foreach (var path in sourcePaths) desiredFiles.Remove(path);
            var targetModFiles = target.Files.Where(f => f.Kind == FileKind.LocalMod && f.Path.Split('/')[^1].TrimDisabled().Equals(mod.FileName, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var file in targetModFiles) desiredFiles[file.Path] = file;
            operations.RemoveAll(o => o.File is not null && (sourcePaths.Contains(o.File.Path, StringComparer.OrdinalIgnoreCase) || targetModFiles.Any(f => f.Path.Equals(o.File.Path, StringComparison.OrdinalIgnoreCase))));
        }
        return new()
        {
            Before = target, SourceHash = ChangeDelta.Fingerprint(source), Mode = mode, Operations = operations,
            Desired = source with { Id = target.Id, Name = target.Name, Directory = target.Directory,
                Mods = desiredMods.Values.OrderBy(m => m.ProjectId).ToList(), Files = desiredFiles.Values.OrderBy(f => f.Path).ToList() }
        };
    }
}
