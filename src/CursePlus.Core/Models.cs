using System.Text.Json;
using System.Text.Json.Serialization;

namespace CursePlus.Core;

public static class JsonFormat
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
    public static readonly JsonSerializerOptions Compact = new(Options)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

public sealed record PackReference(long ProjectId, long FileId, string Name, string Version, string? FileName = null);
public sealed record LoaderReference(string Family, string Version);
public sealed record ModState(long ProjectId, long FileId, string Name, string FileName,
    bool Enabled, string? Website = null, bool Modified = false, string? Sha256 = null);
public enum FileKind { Configuration, Script, LocalMod }
public sealed record FileState(string Path, string Sha256, long Size, FileKind Kind);

public sealed record ProfileState
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    [JsonIgnore] public string Directory { get; init; } = "";
    public string OriginId { get; init; } = "";
    public string MinecraftVersion { get; init; } = "";
    public LoaderReference Loader { get; init; } = new("", "");
    public PackReference? Pack { get; init; }
    public List<ModState> Mods { get; init; } = [];
    public List<FileState> Files { get; init; } = [];
    [JsonIgnore] public List<string> MissingMods { get; init; } = [];
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;
    public override string ToString() => Name;
}

public enum BundleKind { Sync, Snapshot, Baseline }
public sealed record ChangeBundle
{
    public int SchemaVersion { get; init; } = 1;
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "";
    public BundleKind Kind { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public ProfileState Desired { get; init; } = new();
    public ProfileState? Baseline { get; init; }
    public DeltaReference? Delta { get; init; }
    [JsonIgnore] public List<string> ContentSources { get; init; } = [];
}

public sealed record DeltaReference(string OriginalHash, List<long> RemovedMods, List<string> RemovedFiles);

public sealed record PresetMod(long ProjectId, string Name, string? Website);
public sealed record ModPreset
{
    public int SchemaVersion { get; init; } = 1;
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "";
    public List<PresetMod> Mods { get; init; } = [];
}

public enum ApplyMode { PreferSource, Merge }
public enum ConflictChoice { Source, Target }
public enum OperationKind
{
    ChangePackVersion, ChangeLoader, RemoveMod, InstallMod, SetModEnabled, WriteFile, DeleteFile
}
public sealed record PlannedOperation
{
    public OperationKind Kind { get; init; }
    public string Key { get; init; } = "";
    public string Description { get; init; } = "";
    public ModState? Mod { get; init; }
    public FileState? File { get; init; }
    public bool IsConflict { get; init; }
    public string? TargetDescription { get; init; }
    public bool UsesFileException => Kind is OperationKind.WriteFile or OperationKind.DeleteFile;
}
public sealed record ChangePlan
{
    public string SourceHash { get; init; } = "";
    public ProfileState Before { get; init; } = new();
    public ProfileState Desired { get; init; } = new();
    public List<PlannedOperation> Operations { get; init; } = [];
    public List<string> Blockers { get; init; } = [];
    public ApplyMode Mode { get; init; }
    public bool CanApply => Blockers.Count == 0;
}

public sealed record StoredBundle(string Path, string Name, BundleKind Kind, DateTimeOffset CreatedAt,
    string ProfileId, string OriginId, string? ReferenceProfileId = null, long PackFileId = 0)
{
    public override string ToString() => $"{Name} · {CreatedAt.LocalDateTime:dd/MM HH:mm}";
}

public sealed record AppSettings
{
    public string InstancesDirectory { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "curseforge", "minecraft", "Instances");
    public string CurseForgeExecutable { get; init; } = "";
    public string NativeUiLanguage { get; init; } = "auto";
    public List<StoredBundle> Bundles { get; init; } = [];
    public Dictionary<string, string> ProfileOrigins { get; init; } = new();
}
