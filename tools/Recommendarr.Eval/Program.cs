// Offline evaluation of the recommendation engine on a real Recommendarr database.
//
//   dotnet run --project tools/Recommendarr.Eval -- [data-directory]
//
// The data directory (default: src/Recommendarr.Web/config) must contain recommendarr.db and the
// downloaded model under models/. The database is opened read-only, so a running app is fine.
// Embeddings for alternative embedding texts are computed once and cached in eval-cache/.

using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Recommendarr.Core.Models;
using Recommendarr.Core.Recommendations;
using Recommendarr.Infrastructure.Data;
using Recommendarr.Infrastructure.Embeddings;
using Recommendarr.Infrastructure.Options;
using Recommendarr.Infrastructure.Sync;
using Recommendarr.Infrastructure.Vectors;

var dataDirectory = Path.GetFullPath(args.FirstOrDefault() ?? Path.Combine("src", "Recommendarr.Web", "config"));
var cacheDirectory = Path.Combine(dataDirectory, "eval-cache");
Directory.CreateDirectory(cacheDirectory);

await using var connection = new SqliteConnection($"Data Source={Path.Combine(dataDirectory, "recommendarr.db")};Mode=ReadOnly");
await connection.OpenAsync();
SqliteVecInterceptor.Load(connection);
await using var db = new RecommendarrDbContext(new DbContextOptionsBuilder<RecommendarrDbContext>().UseSqlite(connection).Options);

var users = await db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.Username);
var plays = (await db.WatchEvents.AsNoTracking()
        .Where(w => w.ItemId != null)
        .Select(w => new { w.UserId, ItemId = w.ItemId!.Value, w.WatchedAt, w.PercentComplete })
        .ToListAsync())
    .Select(p => new UserPlay(users[p.UserId], new PlaySignal(p.ItemId, p.WatchedAt, p.PercentComplete)))
    .ToList();
var items = await db.Items.AsNoTracking().Where(i => i.EmbeddedAt != null).ToListAsync();
var links = (await db.ItemLinks.AsNoTracking().ToListAsync()).ToLookup(l => l.TargetItemId);
var storedVectors = await new SqliteVecStore(db).GetAsync(items.Select(i => i.Id));
// Watched titles without a vector still count, as misses.
var unembedded = (await db.Items.AsNoTracking().Where(i => i.EmbeddedAt == null).Select(i => new { i.Id, i.MediaType, i.ListedAt }).ToListAsync())
    .Select(i => new EvaluationTarget(i.Id, i.MediaType, links[i.Id].Select(RecommendationInputs.ToCandidateLink).ToList(), i.ListedAt != null))
    .ToList();
Console.WriteLine($"{plays.Count} plays, {items.Count} embedded titles, {storedVectors.Count} stored vectors\n");

// Alternative embedding texts. "stored" is what the app embedded (the production document).
var documentVariants = new Dictionary<string, Func<ItemEntity, string>>
{
    ["no-title"] = i => Document(i, title: false, cast: true),
    ["no-cast"] = i => Document(i, title: true, cast: false),
    ["no-title-no-cast"] = i => Document(i, title: false, cast: false),
};
var vectorSets = new Dictionary<string, IReadOnlyDictionary<int, float[]>> { ["stored"] = storedVectors };
IEmbeddingGenerator<string, Embedding<float>>? generator = null;
foreach (var (name, build) in documentVariants)
    vectorSets[name] = await LoadOrEmbedAsync(name, build);

// The standard suite (shared with the admin dashboard), plus embedding-text variants that need their own vectors.
List<(string Name, string Vectors, RecommendationOptions Options)> experiments = EvaluationExperiments
    .Standard(new RecommendationOptions())
    .Select(e => (e.Name, "stored", e.Options))
    .ToList();
foreach (var name in documentVariants.Keys)
    experiments.Add(($"embedding text: {name}", name, new RecommendationOptions()));

Console.WriteLine($"{"experiment",-36} {"hits",8} {"hit@25",7} {"MRR",6} {"pct",6} {"elig",5} {"link",5} {"reach",5}   per profile (hits, mean percentile)");
foreach (var (name, vectorsName, options) in experiments)
{
    var vectors = vectorSets[vectorsName];
    var candidates = items
        .Where(i => vectors.ContainsKey(i.Id))
        .Select(i => new CandidateItem(i.Id, i.MediaType, vectors[i.Id], i.VoteAverage, i.VoteCount,
            links[i.Id].Select(RecommendationInputs.ToCandidateLink).ToList(), i.ListedAt != null))
        .ToList();
    var result = RecommendationEvaluator.Evaluate(plays, vectors, candidates, options, unembedded: unembedded);
    var perUser = string.Join(", ", result.Cases.GroupBy(c => c.UserId).Select(g => $"{g.Key} {g.Count(c => c.Rank is not null)}/{g.Count()} {g.Average(c => c.Percentile):P0}"));
    Console.WriteLine($"{name,-36} {result.Hits,3}/{result.Cases.Count,-4} {result.HitRate,7:P0} {result.Mrr,6:0.000} {result.MeanPercentile,6:P1} {result.EligibleShare,5:P0} {result.LinkedShare,5:P0} {result.ReachableShare,5:P0}   {perUser}");
}

static string Document(ItemEntity i, bool title, bool cast) => EmbeddingDocument.Build(
    i.MediaType,
    title ? i.Title : "",
    title ? i.Year : null,
    EmbeddingDocument.SplitList(i.Genres),
    EmbeddingDocument.SplitList(i.Keywords),
    EmbeddingDocument.SplitList(i.Creators),
    cast ? EmbeddingDocument.SplitList(i.Cast) : [],
    i.Overview);

async Task<IReadOnlyDictionary<int, float[]>> LoadOrEmbedAsync(string name, Func<ItemEntity, string> build)
{
    var path = Path.Combine(cacheDirectory, $"{name}.bin");
    var result = new Dictionary<int, float[]>();
    if (File.Exists(path))
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        var count = reader.ReadInt32();
        var dimensions = reader.ReadInt32();
        for (var n = 0; n < count; n++)
            result[reader.ReadInt32()] = MemoryMarshal.Cast<byte, float>(reader.ReadBytes(dimensions * 4)).ToArray();
        if (items.All(i => result.ContainsKey(i.Id)))
            return result;
        result.Clear();
    }

    generator ??= CreateGenerator();
    Console.WriteLine($"Embedding {items.Count} titles for '{name}'...");
    foreach (var batch in items.Chunk(64))
    {
        var embeddings = await generator.GenerateAsync(batch.Select(build).ToList());
        for (var n = 0; n < batch.Length; n++)
            result[batch[n].Id] = embeddings[n].Vector.ToArray();
    }

    using (var writer = new BinaryWriter(File.Create(path)))
    {
        writer.Write(result.Count);
        writer.Write(result.First().Value.Length);
        foreach (var (id, vector) in result)
        {
            writer.Write(id);
            writer.Write(MemoryMarshal.AsBytes(vector.AsSpan()));
        }
    }
    return result;
}

IEmbeddingGenerator<string, Embedding<float>> CreateGenerator()
{
    var services = new ServiceCollection().AddHttpClient().BuildServiceProvider();
    return new OnnxEmbeddingGenerator(
        Options.Create(new EmbeddingOptions()),
        Options.Create(new StorageOptions { DataDirectory = dataDirectory }),
        services.GetRequiredService<IHttpClientFactory>(),
        NullLogger<OnnxEmbeddingGenerator>.Instance);
}
