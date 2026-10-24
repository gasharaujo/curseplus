namespace CursePlus.Core;

public interface ICurseForge
{
    Task EnsureReadyAsync(CancellationToken ct);
    Task CheckProfileAsync(ProfileState profile, CancellationToken ct);
    Task ChangePackVersionAsync(ProfileState profile, PackReference pack, CancellationToken ct);
    Task ChangeLoaderAsync(ProfileState profile, LoaderReference loader, CancellationToken ct);
    Task InstallModAsync(ProfileState profile, ModState mod, CancellationToken ct);
    Task RemoveModAsync(ProfileState profile, ModState mod, CancellationToken ct);
    Task SetModEnabledAsync(ProfileState profile, ModState mod, CancellationToken ct);
    Task<ProfileState> CreateProfileAsync(ProfileState desired, string name, CancellationToken ct, string? contentArchive = null);
    Task<ModState?> InstallCompatibleModAsync(ProfileState profile, PresetMod mod, CancellationToken ct);
}

public sealed record ExecutionProgress(int Completed, int Total, string Message);
public sealed record OperationJournal
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public string ArchivePath { get; init; } = "";
    public string TargetId { get; init; } = "";
    public string TargetDirectory { get; init; } = "";
    public string BackupPath { get; init; } = "";
    public string Status { get; init; } = "running";
    public string? Error { get; init; }
    public List<string> Completed { get; init; } = [];
    public ApplyMode Mode { get; init; }
    public ProfileState? Desired { get; init; }
}

public sealed class SyncExecutor(ICurseForge native, ProfileReader reader, AppStore store)
{
    public async Task<OperationJournal> ApplyAsync(ChangePlan plan, string archivePath,
        IProgress<ExecutionProgress>? progress = null, CancellationToken ct = default)
    {
        if (!plan.CanApply) throw new InvalidOperationException(string.Join('\n', plan.Blockers));
        var archive = new BundleArchive();
        var incoming = await store.ReadBundleAsync(archivePath, ct);
        if (plan.SourceHash != ChangeDelta.Fingerprint(incoming.Desired))
            throw new InvalidDataException("O pacote mudou desde a prévia. Gere a prévia novamente.");
        // Reject an archive replaced between preview and execution.
        foreach (var op in plan.Operations.Where(o => o.Kind == OperationKind.WriteFile))
            if (!incoming.Desired.Files.Any(f => f.Path.Equals(op.File!.Path, StringComparison.OrdinalIgnoreCase) && f.Sha256 == op.File.Sha256))
                throw new InvalidDataException("O pacote mudou desde a prévia. Gere a prévia novamente.");
        await native.EnsureReadyAsync(ct);
        await native.CheckProfileAsync(plan.Before, ct);
        var before = store.BindOrigin(await reader.ReadAsync(plan.Before.Directory, true, ct));
        if (!StateComparer.Equivalent(before, plan.Before))
            throw new InvalidOperationException("O perfil mudou desde a prévia. Atualize a comparação antes de aplicar.");
        progress?.Report(new(0, plan.Operations.Count, "Salvando ponto de restauração…"));
        var backup = await store.SaveBundleAsync(new()
        { Name = "Antes de aplicar · " + before.Name, Kind = BundleKind.Snapshot, Desired = before }, before.Directory, ct: ct);
        var backupContents = await store.ReadBundleAsync(backup.Path, ct);
        var journal = new OperationJournal { ArchivePath = Path.GetFullPath(archivePath), TargetId = before.Id,
            TargetDirectory = before.Directory, BackupPath = backup.Path, Mode = plan.Mode, Desired = plan.Desired };
        var journalPath = Path.Combine(store.JournalDirectory, journal.Id + ".json");
        await AppStore.WriteJsonAsync(journalPath, journal, ct);
        try
        {
            var pending = plan.Operations.ToList();
            var nativeProfile = before;
            var total = pending.Count;
            var completed = 0;
            while (pending.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var op = pending[0];
                pending.RemoveAt(0);
                progress?.Report(new(completed, Math.Max(total, completed + pending.Count + 1), op.Description));
                switch (op.Kind)
                {
                    case OperationKind.ChangePackVersion:
                        await native.ChangePackVersionAsync(nativeProfile, plan.Desired.Pack!, CancellationToken.None);
                        // Native updates reset content: compute again from the actual resulting state.
                        var refreshed = store.BindOrigin(await reader.ReadAsync(before.Directory, true, CancellationToken.None));
                        nativeProfile = refreshed;
                        var remaining = new ChangePlanner().Build(refreshed, plan.Desired, ApplyMode.PreferSource);
                        if (!remaining.CanApply) throw new InvalidOperationException(string.Join('\n', remaining.Blockers));
                        if (remaining.Operations.Any(o => o.Kind == OperationKind.ChangePackVersion)) throw new InvalidOperationException("O CurseForge não confirmou a versão solicitada do pack.");
                        pending = remaining.Operations;
                        total = completed + 1 + pending.Count;
                        break;
                    case OperationKind.ChangeLoader: await native.ChangeLoaderAsync(nativeProfile, plan.Desired.Loader, CancellationToken.None); break;
                    case OperationKind.InstallMod: await native.InstallModAsync(nativeProfile, op.Mod!, CancellationToken.None); break;
                    case OperationKind.RemoveMod: await native.RemoveModAsync(nativeProfile, op.Mod!, CancellationToken.None); break;
                    case OperationKind.SetModEnabled: await native.SetModEnabledAsync(nativeProfile, op.Mod!, CancellationToken.None); break;
                    case OperationKind.WriteFile:
                        ValidateFileException(op.File!, plan.Desired);
                        await archive.WritePayloadAsync(incoming.ContentSources.Concat(backupContents.ContentSources), op.File!, SafePaths.Resolve(before.Directory, op.File!.Path), ct);
                        break;
                    case OperationKind.DeleteFile:
                        ValidateFileException(op.File!, before);
                        var deletePath = SafePaths.Resolve(before.Directory, op.File!.Path);
                        // A native remove may already have deleted the bytes.
                        if (File.Exists(deletePath)) File.Delete(deletePath);
                        break;
                }
                completed++;
                journal = journal with { Completed = [.. journal.Completed, op.Description] };
                // Finish the journal even if the user requested cancellation during a native operation.
                await AppStore.WriteJsonAsync(journalPath, journal, CancellationToken.None);
            }
            var final = store.BindOrigin(await reader.ReadAsync(before.Directory, true, ct));
            if (!StateComparer.Equivalent(final, plan.Desired))
                throw new InvalidOperationException("A verificação final encontrou diferenças. O ponto anterior foi preservado; atualize a prévia para revisar ou retomar.");
            if (plan.Desired.Pack is null && before.OriginId != plan.Desired.OriginId)
                await store.LinkOriginAsync(final, plan.Desired.OriginId, CancellationToken.None);
            journal = journal with { Status = "complete" };
            await AppStore.WriteJsonAsync(journalPath, journal, CancellationToken.None);
            progress?.Report(new(completed, completed, "Sincronização concluída e verificada."));
            return journal;
        }
        catch (Exception error)
        {
            journal = journal with { Status = error is OperationCanceledException ? "cancelled" : "failed", Error = error.Message };
            await AppStore.WriteJsonAsync(journalPath, journal, CancellationToken.None);
            throw;
        }
    }

    private static void ValidateFileException(FileState file, ProfileState state)
    {
        if (SafePaths.Classify(file.Path) != file.Kind) throw new InvalidDataException("Exceção de arquivo inválida.");
        if (file.Kind == FileKind.LocalMod)
        {
            var name = file.Path.Split('/')[^1].TrimDisabled();
            if (state.Mods.Any(m => m.FileName.Equals(name, StringComparison.OrdinalIgnoreCase) && !m.Modified))
                throw new InvalidOperationException("Mods normais só podem ser alterados pelo CurseForge.");
        }
    }
}

public static class StateComparer
{
    public static bool Equivalent(ProfileState a, ProfileState b)
    {
        if (a.Pack?.ProjectId != b.Pack?.ProjectId || a.Pack?.FileId != b.Pack?.FileId ||
            a.MinecraftVersion != b.MinecraftVersion || a.Loader != b.Loader || a.Mods.Count != b.Mods.Count || a.Files.Count != b.Files.Count)
            return false;
        var mods = b.Mods.ToDictionary(m => m.ProjectId);
        var files = b.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        return a.Mods.All(m => mods.TryGetValue(m.ProjectId, out var n) && ChangeDelta.SameMod(m, n)) &&
            a.Files.All(f => files.TryGetValue(f.Path, out var g) && f.Sha256 == g.Sha256);
    }
}
