namespace Recommendarr.Infrastructure.Options;

// All sections live under the "Recommendarr" configuration root, so environment variables
// look like Recommendarr__Tmdb__ApiKey.

public enum HistoryProviderKind
{
    Tautulli,
    Plex,
}

public enum EmbeddingProviderKind
{
    Onnx,
    Ollama,
    OpenAI,
}

/// <summary>How the built-in ONNX model turns token vectors into one sentence vector.</summary>
public enum OnnxPooling
{
    /// <summary>The [CLS] token, as used by the BGE models.</summary>
    Cls,

    /// <summary>Mean over all tokens, as used by the sentence-transformers MiniLM/MPNet models.</summary>
    Mean,
}

public sealed class StorageOptions
{
    public const string Section = "Recommendarr:Storage";

    /// <summary>Directory holding the SQLite database and downloaded models. Mounted as /config in Docker.</summary>
    public string DataDirectory { get; set; } = "config";

    public string DatabasePath => Path.Combine(DataDirectory, "recommendarr.db");

    public string ModelDirectory => Path.Combine(DataDirectory, "models");
}

public sealed class NetworkOptions
{
    public const string Section = "Recommendarr:Network";

    /// <summary>
    /// Connect over IPv4 before IPv6 for all outgoing HTTP (TMDB, plex.tv, Seerr, ...). Works around
    /// home networks with broken IPv6 routes; IPv6 is still used when a host has no IPv4 address.
    /// </summary>
    public bool PreferIPv4 { get; set; } = true;
}

public sealed class HistoryOptions
{
    public const string Section = "Recommendarr:History";

    public HistoryProviderKind Provider { get; set; } = HistoryProviderKind.Tautulli;
}

public sealed class TautulliOptions
{
    public const string Section = "Recommendarr:Tautulli";

    public string? BaseUrl { get; set; }

    public string? ApiKey { get; set; }
}

public sealed class PlexOptions
{
    public const string Section = "Recommendarr:Plex";

    public string? BaseUrl { get; set; }

    /// <summary>Server owner's X-Plex-Token; used to read history for all users.</summary>
    public string? Token { get; set; }
}

public sealed class SyncOptions
{
    public const string Section = "Recommendarr:Sync";

    /// <summary>How often history and the TMDB catalog are synced.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(6);

    public bool RunOnStartup { get; set; } = true;
}

public sealed class TmdbOptions
{
    public const string Section = "Recommendarr:Tmdb";

    /// <summary>TMDB API Read Access Token (v4 bearer token).</summary>
    public string? ApiKey { get; set; }

    public string Language { get; set; } = "en-US";

    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromDays(7);
}

public sealed class SeerrOptions
{
    public const string Section = "Recommendarr:Seerr";

    public string? BaseUrl { get; set; }

    public string? ApiKey { get; set; }
}

public sealed class EmbeddingOptions
{
    public const string Section = "Recommendarr:Embeddings";

    public EmbeddingProviderKind Provider { get; set; } = EmbeddingProviderKind.Onnx;

    /// <summary>
    /// Model name for Ollama/OpenAI. For ONNX, a Hugging Face repository ("org/name") that has
    /// onnx/model.onnx and vocab.txt; a bare name means a BAAI model.
    /// </summary>
    public string Model { get; set; } = "bge-small-en-v1.5";

    public OnnxPooling OnnxPooling { get; set; } = OnnxPooling.Cls;

    /// <summary>Endpoint for Ollama or an OpenAI-compatible server.</summary>
    public string? Endpoint { get; set; }

    public string? ApiKey { get; set; }
}

public sealed class LlmOptions
{
    public const string Section = "Recommendarr:Llm";

    public bool Enabled { get; set; }

    /// <summary>Ollama, OpenAI (any OpenAI-compatible endpoint), or Anthropic.</summary>
    public string Provider { get; set; } = "Ollama";

    public string? Model { get; set; }

    public string? Endpoint { get; set; }

    public string? ApiKey { get; set; }
}

public sealed class TelegramOptions
{
    public const string Section = "Recommendarr:Telegram";

    public string? BotToken { get; set; }
}

public sealed class DiscordOptions
{
    public const string Section = "Recommendarr:Discord";

    public string? BotToken { get; set; }
}
