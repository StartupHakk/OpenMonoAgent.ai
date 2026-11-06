namespace OpenMono.Win.Inference;

// Fixed values of the Windows llama.cpp path. Port, health route and the
// LLAMA_* knobs match the Linux llama-server service (scripts/install.sh).
public static class LlamaCppConstants
{
    public const int DefaultPort = 7474;

    public const string DefaultLoopbackEndpoint = "http://127.0.0.1:7474";

    // releases/latest can be a non-build tag without Windows binaries, so the
    // installer lists recent releases and takes the newest b<N> build that
    // ships the asset it needs.
    public const string ReleasesApiUrl = "https://api.github.com/repos/ggml-org/llama.cpp/releases?per_page=30";

    public const string ReleasesPageUrl = "https://github.com/ggml-org/llama.cpp/releases";

    public const string ReleasesUrlEnvVar = "OPENMONO_LLAMA_CPP_RELEASES_URL";

    // Pin one build tag (for example b6500) instead of the newest.
    public const string ReleaseTagEnvVar = "OPENMONO_LLAMA_CPP_RELEASE";

    // Same dev override as install.sh: fetch <mirror>/models/<file>.
    public const string ModelMirrorEnvVar = "OPENMONO_MODEL_MIRROR";

    // Where llama.cpp builds are installed (default ~/.openmono/win/llama.cpp).
    public const string InstallDirEnvVar = "OPENMONO_LLAMA_CPP_DIR";

    public const string ModelsDirEnvVar = "OPENMONO_MODELS_DIR";

    public const string RepoDirEnvVar = "OPENMONO_WIN_REPO";

    public const string ApiKeyEnvVar = "LLAMA_API_KEY";

    public const string ThreadsEnvVar = "LLAMA_THREADS";

    public static string ServerExecutableName =>
        OperatingSystem.IsWindows() ? "llama-server.exe" : "llama-server";
}
