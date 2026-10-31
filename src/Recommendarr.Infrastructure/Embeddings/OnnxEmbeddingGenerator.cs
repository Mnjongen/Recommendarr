using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using Recommendarr.Core.Recommendations;
using Recommendarr.Infrastructure.Options;

namespace Recommendarr.Infrastructure.Embeddings;

/// <summary>
/// Built-in embedding model: a BERT-style sentence encoder (default BAAI/bge-small-en-v1.5) run
/// locally with ONNX Runtime. The model and vocabulary are downloaded from Hugging Face into the
/// model directory on first use.
/// </summary>
public sealed class OnnxEmbeddingGenerator(
    IOptions<EmbeddingOptions> embeddingOptions,
    IOptions<StorageOptions> storageOptions,
    IHttpClientFactory httpClientFactory,
    ILogger<OnnxEmbeddingGenerator> logger) : IEmbeddingGenerator<string, Embedding<float>>
{
    public const string HttpClientName = "models";

    private const int MaxTokens = 512;
    private const int BatchSize = 16;

    private readonly EmbeddingOptions _options = embeddingOptions.Value;
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private InferenceSession? _session;
    private BertTokenizer? _tokenizer;

    /// <summary>"bge-small-en-v1.5" is shorthand for the BAAI repository; "org/name" is used as-is.</summary>
    private string Repository => _options.Model.Contains('/') ? _options.Model : $"BAAI/{_options.Model}";

    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);
        var result = new GeneratedEmbeddings<Embedding<float>>();
        foreach (var batch in values.Chunk(BatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.AddRange(Embed(batch));
        }
        return result;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is not null ? null
        : serviceType == typeof(EmbeddingGeneratorMetadata) ? new EmbeddingGeneratorMetadata("onnx", defaultModelId: Repository)
        : serviceType.IsInstanceOfType(this) ? this
        : null;

    public void Dispose()
    {
        _session?.Dispose();
        _loadLock.Dispose();
    }

    private List<Embedding<float>> Embed(string[] texts)
    {
        var tokenized = texts.Select(Tokenize).ToList();
        var length = tokenized.Max(t => t.Length);

        var ids = new DenseTensor<long>([texts.Length, length]);
        var mask = new DenseTensor<long>([texts.Length, length]);
        var types = new DenseTensor<long>([texts.Length, length]); // single segment: all zeros
        for (var b = 0; b < texts.Length; b++)
        {
            for (var t = 0; t < tokenized[b].Length; t++)
            {
                ids[b, t] = tokenized[b][t];
                mask[b, t] = 1;
            }
        }

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", ids),
            NamedOnnxValue.CreateFromTensor("attention_mask", mask),
        };
        if (_session!.InputMetadata.ContainsKey("token_type_ids"))
            inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", types));

        using var outputs = _session.Run(inputs);
        var hidden = (outputs.FirstOrDefault(o => o.Name == "last_hidden_state") ?? outputs.First()).AsTensor<float>();
        var dimensions = hidden.Dimensions[2];

        var embeddings = new List<Embedding<float>>(texts.Length);
        for (var b = 0; b < texts.Length; b++)
        {
            var vector = new float[dimensions];
            if (_options.OnnxPooling == OnnxPooling.Cls)
            {
                for (var d = 0; d < dimensions; d++)
                    vector[d] = hidden[b, 0, d];
            }
            else
            {
                var count = tokenized[b].Length;
                for (var t = 0; t < count; t++)
                {
                    for (var d = 0; d < dimensions; d++)
                        vector[d] += hidden[b, t, d] / count;
                }
            }
            embeddings.Add(new Embedding<float>(VectorMath.Normalize(vector)) { ModelId = Repository });
        }
        return embeddings;
    }

    private long[] Tokenize(string text)
    {
        var tokenizer = _tokenizer!;
        var pieces = tokenizer.EncodeToIds(text, addSpecialTokens: false);
        return pieces.Take(MaxTokens - 2)
            .Prepend(tokenizer.ClassificationTokenId)
            .Append(tokenizer.SeparatorTokenId)
            .Select(id => (long)id)
            .ToArray();
    }

    private async Task EnsureLoadedAsync(CancellationToken ct)
    {
        if (_session is not null)
            return;

        await _loadLock.WaitAsync(ct);
        try
        {
            if (_session is not null)
                return;

            var directory = Path.Combine(storageOptions.Value.ModelDirectory, Repository.Replace('/', '_'));
            Directory.CreateDirectory(directory);
            var modelPath = await DownloadIfMissingAsync(directory, "model.onnx", "onnx/model.onnx", ct);
            var vocabPath = await DownloadIfMissingAsync(directory, "vocab.txt", "vocab.txt", ct);

            _tokenizer = BertTokenizer.Create(vocabPath);
            _session = new InferenceSession(modelPath);
            logger.LogInformation("Loaded embedding model {Model}", Repository);
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private async Task<string> DownloadIfMissingAsync(string directory, string fileName, string repositoryPath, CancellationToken ct)
    {
        var path = Path.Combine(directory, fileName);
        if (File.Exists(path))
            return path;

        var url = $"https://huggingface.co/{Repository}/resolve/main/{repositoryPath}";
        logger.LogInformation("Downloading {Url}", url);

        // Download to a temporary name so an interrupted download is never mistaken for a model.
        var partial = path + ".partial";
        var http = httpClientFactory.CreateClient(HttpClientName);
        using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var target = File.Create(partial);
            await source.CopyToAsync(target, ct);
        }
        File.Move(partial, path, overwrite: true);
        logger.LogInformation("Downloaded {File} ({Size:N0} bytes)", fileName, new FileInfo(path).Length);
        return path;
    }
}
