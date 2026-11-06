namespace OpenMono.Win.Inference;

public sealed record ModelFiles(string ModelPath, string? MmprojPath, IReadOnlyList<string> Log, string? Error);

// Puts a rung's GGUF (and its vision projector) in the models directory, the
// way install.sh does: <repo>\models\<file>, from the Hugging Face URL in
// scripts/models.json or from OPENMONO_MODEL_MIRROR/models/<file>. A missing
// mmproj is not fatal: vision is off, exactly as on Linux.
public sealed class ModelStore
{
    // install.sh's sanity floors (1 GB model, 100 MB mmproj) are sized for the
    // 27B; the Windows ladder goes down to a ~0.6 GB 0.8B and a ~0.2 GB
    // projector, so only an obviously wrong body (an error page) is rejected.
    public const long MinModelBytes = 64L * 1024 * 1024;

    public const long MinMmprojBytes = 16L * 1024 * 1024;

    private readonly FileDownloader _downloader;
    private readonly Func<string, string?> _getEnv;

    public ModelStore(HttpClient http, string modelsDir, Func<string, string?> getEnv, TextWriter? progress = null)
    {
        _downloader = new FileDownloader(http, progress);
        _getEnv = getEnv;
        ModelsDir = modelsDir;
    }

    public string ModelsDir { get; }

    // Explicit OPENMONO_MODELS_DIR, else <repo>\models next to scripts\models.json.
    public static string ModelsDirFor(string modelsJsonPath, Func<string, string?> getEnv) =>
        getEnv(LlamaCppConstants.ModelsDirEnvVar) is { Length: > 0 } explicitDir
            ? explicitDir
            : Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(modelsJsonPath))!)!, "models");

    public string PathOf(string file) => Path.Combine(ModelsDir, file);

    public bool Has(string file) => File.Exists(PathOf(file));

    public async Task<ModelFiles> EnsureAsync(ModelRung rung, bool withMmproj, CancellationToken ct = default)
    {
        var log = new List<string>();
        var model = PathOf(rung.File);
        if (File.Exists(model))
        {
            log.Add($"Model already present: {rung.File} ({FileDownloader.Gb(new FileInfo(model).Length)}).");
        }
        else
        {
            var url = SourceUrl(rung.File, rung.Url);
            log.Add($"Downloading {rung.Label} from {url} to {ModelsDir}. This takes a while on the first run.");
            var got = await _downloader.DownloadAsync(url, model, MinModelBytes, ct);
            if (!got.Ok)
            {
                return new ModelFiles(model, null, log, $"Model download failed: {got.Error}");
            }

            log.Add($"Model downloaded: {rung.File} ({FileDownloader.Gb(got.Bytes)}).");
        }

        if (!withMmproj)
        {
            return new ModelFiles(model, null, log, null);
        }

        if (rung.Mmproj is null || rung.MmprojUrl is null)
        {
            log.Add($"No mmproj configured for {rung.Alias}; vision will be disabled.");
            return new ModelFiles(model, null, log, null);
        }

        var mmproj = PathOf(rung.Mmproj);
        if (File.Exists(mmproj))
        {
            return new ModelFiles(model, mmproj, log, null);
        }

        var mmprojUrl = SourceUrl(rung.Mmproj, rung.MmprojUrl);
        log.Add($"Downloading the vision projector {rung.Mmproj} from {mmprojUrl}.");
        var projector = await _downloader.DownloadAsync(mmprojUrl, mmproj, MinMmprojBytes, ct);
        if (!projector.Ok)
        {
            log.Add($"mmproj download failed ({projector.Error}); vision will be unavailable.");
            return new ModelFiles(model, null, log, null);
        }

        return new ModelFiles(model, mmproj, log, null);
    }

    private string SourceUrl(string file, string url) =>
        _getEnv(LlamaCppConstants.ModelMirrorEnvVar) is { Length: > 0 } mirror
            ? $"{mirror.TrimEnd('/')}/models/{file}"
            : url;
}
