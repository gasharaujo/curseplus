using System.Security.Cryptography;
using System.Text;

namespace CursePlus.Core;

public static class ChangeDelta
{
    public static ChangeBundle Create(ChangeBundle bundle, ProfileState original)
    {
        var source = FileScope.Clean(bundle.Desired);
        original = FileScope.Clean(original);
        CheckOrigin(source, original);
        var baseMods = original.Mods.ToDictionary(m => m.ProjectId);
        var baseFiles = original.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        var sourceModIds = source.Mods.Select(m => m.ProjectId).ToHashSet();
        var sourcePaths = source.Files.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var mods = source.Mods.Where(m => !baseMods.TryGetValue(m.ProjectId, out var b) || !SameMod(m, b))
            .Select(m => m with { Website = null, Sha256 = m.Modified ? m.Sha256 : null }).ToList();
        return bundle with
        {
            SchemaVersion = 2, Baseline = null,
            Desired = source with
            {
                Mods = mods,
                Files = source.Files.Where(f => !baseFiles.TryGetValue(f.Path, out var b) || f.Sha256 != b.Sha256).ToList()
            },
            Delta = new(Fingerprint(original),
                original.Mods.Where(b => !sourceModIds.Contains(b.ProjectId)).Select(m => m.ProjectId).ToList(),
                original.Files.Where(b => !sourcePaths.Contains(b.Path)).Select(f => f.Path).ToList())
        };
    }

    public static ChangeBundle Resolve(ChangeBundle delta, ChangeBundle original)
    {
        if (delta.Delta is not { } changes) return delta;
        original = original with { Desired = FileScope.Clean(original.Desired) };
        if (original.Delta is not null) throw new InvalidDataException("A referência original precisa ser completa.");
        CheckOrigin(delta.Desired, original.Desired);
        if (changes.OriginalHash != Fingerprint(original.Desired))
            throw new InvalidDataException("O original salvo difere da referência usada na exportação. Use o mesmo original para abrir estas alterações.");
        var mods = original.Desired.Mods.ToDictionary(m => m.ProjectId);
        var files = original.Desired.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        foreach (var id in changes.RemovedMods)
            if (!mods.Remove(id)) throw new InvalidDataException("Remoção de mod ausente no original.");
        foreach (var path in changes.RemovedFiles)
            if (!files.Remove(path)) throw new InvalidDataException("Remoção de arquivo ausente no original.");
        foreach (var mod in delta.Desired.Mods) mods[mod.ProjectId] = mod;
        foreach (var file in delta.Desired.Files) files[file.Path] = file;
        var resolved = delta with
        {
            SchemaVersion = 1, Delta = null, Baseline = original.Desired,
            Desired = delta.Desired with { Mods = mods.Values.OrderBy(m => m.ProjectId).ToList(), Files = files.Values.OrderBy(f => f.Path).ToList() },
            ContentSources = [.. delta.ContentSources.Concat(original.ContentSources).Distinct(StringComparer.OrdinalIgnoreCase)]
        };
        BundleArchive.Validate(resolved);
        return resolved;
    }

    private static void CheckOrigin(ProfileState source, ProfileState original)
    {
        if (source.Pack?.ProjectId != original.Pack?.ProjectId || source.Pack?.FileId != original.Pack?.FileId ||
            (source.Pack is null && (string.IsNullOrEmpty(source.OriginId) || source.OriginId != original.OriginId)))
            throw new InvalidDataException("A exportação exige o original da mesma origem e versão do pack.");
    }

    public static bool SameMod(ModState a, ModState b) => a.ProjectId == b.ProjectId && a.FileId == b.FileId &&
        a.Enabled == b.Enabled && a.Modified == b.Modified && (!a.Modified || a.Sha256 == b.Sha256);

    public static string Fingerprint(ProfileState state)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            hash.AppendData(BitConverter.GetBytes(bytes.Length)); hash.AppendData(bytes);
        }
        Add(state.Pack?.ProjectId.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "");
        Add(state.Pack?.FileId.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "");
        Add(state.MinecraftVersion); Add(state.Loader.Family); Add(state.Loader.Version);
        foreach (var mod in state.Mods.OrderBy(m => m.ProjectId))
        { Add($"{mod.ProjectId}:{mod.FileId}:{mod.Enabled}:{mod.Modified}"); Add(mod.Modified ? mod.Sha256 ?? "" : ""); }
        Add("files");
        foreach (var file in state.Files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase))
        { Add(file.Path.Replace('\\', '/').ToLowerInvariant()); Add(file.Sha256); Add(file.Size.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}
