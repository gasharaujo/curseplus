using System.Text.Json;

namespace CursePlus.Core;

public sealed class AppStore(string root)
{
    public string Root { get; } = Path.GetFullPath(root);
    public string BundlesDirectory => Path.Combine(Root, "snapshots");
    public string PresetsDirectory => Path.Combine(Root, "presets");
    public string JournalDirectory => Path.Combine(Root, "operations");
    public AppSettings Settings { get; private set; } = new();

    public async Task LoadAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(BundlesDirectory);
        Directory.CreateDirectory(PresetsDirectory);
        Directory.CreateDirectory(JournalDirectory);
        var path = Path.Combine(Root, "settings.json");
        if (File.Exists(path))
            Settings = await ReadJsonAsync<AppSettings>(path, ct) ?? new();
    }

    public async Task SaveSettingsAsync(AppSettings settings, CancellationToken ct = default)
    {
        await WriteJsonAsync(Path.Combine(Root, "settings.json"), settings, ct);
        Settings = settings;
    }

    public ProfileState BindOrigin(ProfileState profile) => profile with
    {
        OriginId = Settings.ProfileOrigins.GetValueOrDefault(profile.Id,
            profile.Pack is { } pack ? $"curseforge:{pack.ProjectId}" : "")
    };

    public async Task<ProfileState> EnsureOriginAsync(ProfileState profile, CancellationToken ct = default)
    {
        profile = BindOrigin(profile);
        if (profile.OriginId.Length > 0) return profile;
        return await LinkOriginAsync(profile, Guid.NewGuid().ToString("N"), ct);
    }

    public async Task<ProfileState> LinkOriginAsync(ProfileState profile, string origin, CancellationToken ct = default)
    {
        var origins = new Dictionary<string, string>(Settings.ProfileOrigins) { [profile.Id] = origin };
        await SaveSettingsAsync(Settings with { ProfileOrigins = origins }, ct);
        return profile with { OriginId = origin };
    }

    public async Task<StoredBundle> SaveBundleAsync(ChangeBundle bundle, string sourceDirectory, string? referenceId = null, CancellationToken ct = default)
    {
        if (bundle.Kind == BundleKind.Snapshot && bundle.Delta is null && FindBaseline(bundle.Desired) is { } baseline)
            bundle = ChangeDelta.Create(bundle, (await new BundleArchive().ReadAsync(baseline.Path, ct, false)).Desired);
        var path = Path.Combine(BundlesDirectory, bundle.Id + ".curseplus.zip");
        await new BundleArchive().WriteAsync(path, bundle, sourceDirectory, ct);
        var record = new StoredBundle(path, bundle.Name, bundle.Kind, bundle.CreatedAt, bundle.Desired.Id, bundle.Desired.OriginId, referenceId, bundle.Desired.Pack?.FileId ?? 0);
        await SaveSettingsAsync(Settings with { Bundles = [.. Settings.Bundles, record] }, ct);
        return record;
    }

    public async Task<ChangeBundle> ReadBundleAsync(string path, CancellationToken ct = default)
    {
        var archive = new BundleArchive();
        var bundle = await archive.ReadAsync(path, ct);
        return await ResolveBundleAsync(bundle, ct);
    }

    public async Task<ChangeBundle> ResolveBundleAsync(ChangeBundle bundle, CancellationToken ct = default)
    {
        if (bundle.Delta is null) return bundle;
        var baseline = FindBaseline(bundle.Desired) ?? throw new InvalidDataException("Defina o original da mesma origem e versão antes de abrir estas alterações.");
        return ChangeDelta.Resolve(bundle, await new BundleArchive().ReadAsync(baseline.Path, ct, false));
    }

    public StoredBundle? FindBaseline(ProfileState profile) => Settings.Bundles
        .Where(b => b.Kind == BundleKind.Baseline && b.OriginId == profile.OriginId && b.PackFileId == (profile.Pack?.FileId ?? 0) && File.Exists(b.Path))
        .OrderByDescending(b => b.CreatedAt).FirstOrDefault();

    public async Task<List<ModPreset>> GetPresetsAsync(CancellationToken ct = default)
    {
        var presets = new List<ModPreset>();
        foreach (var file in Directory.EnumerateFiles(PresetsDirectory, "*.json"))
        {
            var preset = await ReadJsonAsync<ModPreset>(file, ct);
            if (preset is not null) { ValidatePreset(preset); presets.Add(preset); }
        }
        return presets.OrderBy(p => p.Name).ToList();
    }

    public Task SavePresetAsync(ModPreset preset, CancellationToken ct = default)
    {
        ValidatePreset(preset);
        var id = Guid.TryParse(preset.Id, out var guid) ? guid.ToString("N") : throw new InvalidDataException("ID da lista inválido.");
        return WriteJsonAsync(Path.Combine(PresetsDirectory, id + ".json"), preset, ct);
    }

    public static void ValidatePreset(ModPreset preset)
    {
        if (preset.SchemaVersion != 1 || string.IsNullOrWhiteSpace(preset.Name) || preset.Mods.Any(m => m.ProjectId <= 0 || string.IsNullOrWhiteSpace(m.Name)) || preset.Mods.Count > 5000 || preset.Mods.Select(m => m.ProjectId).Distinct().Count() != preset.Mods.Count)
            throw new InvalidDataException("Lista quick add inválida.");
    }

    public static async Task<T?> ReadJsonAsync<T>(string path, CancellationToken ct = default)
    {
        await using var input = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(input, JsonFormat.Options, ct);
    }

    public static async Task WriteJsonAsync<T>(string path, T value, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await JsonSerializer.SerializeAsync(output, value, JsonFormat.Options, ct);
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
