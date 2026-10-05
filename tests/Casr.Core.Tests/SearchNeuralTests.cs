using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Casr.Core.Models;
using Casr.Core.Search;
using Casr.Core.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.ML.OnnxRuntime.Tensors;
using Xunit;

namespace Casr.Core.Tests;

/// <summary>
/// Neural-embeddings upgrade: provider abstraction, model-dir isolation,
/// mixed-model coexistence, tokenizer/decode units, graceful ONNX skips.
/// 5-pillar discipline: validate ingress, execute in temp-dir isolation,
/// verify intermediate artifacts, inspect egress out-of-band (raw SQL /
/// independent instances), round-trip re-parse. Temp-only I/O; the
/// production models dir is never created or written.
/// </summary>
public class SearchNeuralTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string? _savedModelsDir;

    public SearchNeuralTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "casr_neural_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _savedModelsDir = Environment.GetEnvironmentVariable(EmbeddingModelManager.ModelsDirEnvVar);
        Environment.SetEnvironmentVariable(EmbeddingModelManager.ModelsDirEnvVar,
            Path.Combine(_tempDir, "models"));
        Directory.CreateDirectory(EmbeddingModelManager.ModelsDirectory);
    }

    public void Dispose()
    {
        try { TextEmbedder.ResetToDefault(); } catch { }
        try { EmbeddingModelManager.ResetForTests(); } catch { }
        try
        {
            Environment.SetEnvironmentVariable(
                EmbeddingModelManager.ModelsDirEnvVar, _savedModelsDir);
        }
        catch { }
        // Microsoft.Data.Sqlite pools connections: without this the pooled handle
        // on mix.db survives SessionDatabase.Dispose and Directory.Delete throws
        // (swallowed below), leaking the temp dir on every run.
        try { SqliteConnection.ClearAllPools(); } catch { }
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static (SessionSummary Summary, CanonicalSession Session) Fixture(
        string id, string title, (MessageRole Role, string Content)[] messages)
    {
        Assert.NotEmpty(messages); // P1: ingress must be non-trivial
        var summary = new SessionSummary
        {
            SessionId = id,
            Provider = "test",
            ProviderDisplayName = "Test",
            Title = title,
            Workspace = "/tmp",
            StartedAt = DateTime.Now.AddHours(-1),
            LastActiveAt = DateTime.Now,
            MessagesCount = messages.Length,
            FileSizeBytes = 1000,
            SourcePath = "/tmp/" + id,
        };
        var session = new CanonicalSession
        {
            SessionId = id,
            ProviderSlug = "test",
            Title = title,
            Messages = messages.Select((m, i) => new CanonicalMessage
            {
                Index = i,
                Role = m.Role,
                Content = m.Content,
                TimestampEpochMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            }).ToList(),
        };
        return (summary, session);
    }

    private static double Norm(float[] v)
    {
        double n = 0;
        foreach (var x in v) n += (double)x * x;
        return Math.Sqrt(n);
    }

    // ---- Pillar coverage: abstraction delegation parity (hashing default byte-identical) ----

    [Fact]
    public void DelegationParity_EmbedByteIdenticalToHashingDefault()
    {
        // P1: ingress — provider contract constants.
        Assert.Equal("hashing-trigram-v1", TextEmbedder.ModelId);
        Assert.Equal(512, TextEmbedder.Dims);
        Assert.Equal("hashing-trigram-v1", HashingEmbedder.Instance.ModelId);
        Assert.Equal(512, HashingEmbedder.Instance.Dims);
        Assert.False(TextEmbedder.IsNeuralActive);

        var inputs = new[] { "fix nvml linking error", "", "   ", "héllo wörld _ hearing 123", new string('x', 6000) };
        var independent = new HashingEmbedder(); // distinct instance, not the singleton
        foreach (var text in inputs)
        {
            // P2: isolated execution through the static facade.
            var viaFacade = TextEmbedder.Embed(text);
            var viaProvider = independent.Embed(text);

            // P3: intermediate artifact shape.
            Assert.Equal(512, viaFacade.Length);
            Assert.Equal(512 * 4, TextEmbedder.ToBytes(viaFacade).Length);

            // P4: out-of-band inspection — byte-identical to an independent instance.
            Assert.Equal(TextEmbedder.ToBytes(viaProvider), TextEmbedder.ToBytes(viaFacade));

            // P5: round-trip re-parse + quality: normalized (or exactly zero for blank).
            var back = TextEmbedder.FromBytes(TextEmbedder.ToBytes(viaFacade), 512);
            Assert.Equal(viaFacade, back);
            if (string.IsNullOrWhiteSpace(text)) Assert.All(back, x => Assert.Equal(0f, x));
            else Assert.InRange(Norm(back), 0.999, 1.001);
            var self = TextEmbedder.Cosine(viaFacade, viaFacade);
            if (string.IsNullOrWhiteSpace(text)) Assert.Equal(0, self); // zero vector: no self-score
            else Assert.InRange(self, 0.999, 1.001);
        }
    }

    [Fact]
    public void DelegationParity_EmbedSessionMatchesProviderWeighting()
    {
        // P1: multi-part ingress with empties and weight hints.
        var parts = new List<(string? Content, int WeightHint)>
        {
            ("first task statement", 20),
            (null, 5),
            ("   ", 5),
            ("middle context dump with filler words", 400),
            ("final outcome summary", 30),
        };
        Assert.Contains(parts, p => !string.IsNullOrWhiteSpace(p.Content));

        // P2: execute via facade and via the provider directly.
        var viaFacade = TextEmbedder.EmbedSession(parts);
        var viaProvider = new HashingEmbedder().EmbedSession(parts);

        // P3/P4: shape + byte parity with the independent provider instance.
        Assert.Equal(512, viaFacade.Length);
        Assert.Equal(TextEmbedder.ToBytes(viaProvider), TextEmbedder.ToBytes(viaFacade));

        // P5: normalized session vector; empty input yields the zero vector.
        Assert.InRange(Norm(viaFacade), 0.999, 1.001);
        var empty = TextEmbedder.EmbedSession(new List<(string?, int)> { (null, 1), ("  ", 2) });
        Assert.All(empty, x => Assert.Equal(0f, x));
    }

    // ---- Dims-mismatch unit: foreign-model blobs never score ----

    [Fact]
    public void FromBytes_DimsMismatchYieldsZeroVectorAndZeroScore()
    {
        // P1: a 384-dim neural-shaped blob (non-zero random payload).
        var rng = new Random(42);
        var neuralBlob = new byte[384 * 4];
        rng.NextBytes(neuralBlob);
        Assert.Contains(neuralBlob, b => b != 0);

        // P2/P3: decode as 512-dim hashing space.
        var decoded = TextEmbedder.FromBytes(neuralBlob, 512);

        // P4/P5: short blob -> zero vector -> cosine 0: can never cross-score.
        Assert.All(decoded, x => Assert.Equal(0f, x));
        var qv = TextEmbedder.Embed("nvml linking error");
        Assert.Equal(0, TextEmbedder.Cosine(qv, decoded));
    }

    // ---- Mixed-model coexistence through the real database ----

    [Fact]
    public void MixedModelCoexistence_NeuralRowsNeverCrossScore()
    {
        var dbPath = Path.Combine(_tempDir, "mix.db");
        using var db = new SessionDatabase(dbPath);
        var (s1, c1) = Fixture("hash-a", "NVML linking failure", new[]
        {
            (MessageRole.User, "Nvidia NVML linking error in C++?"),
            (MessageRole.Assistant, "Link against nvml.lib please."),
        });
        var (s2, c2) = Fixture("hash-b", "Gardening notes", new[]
        {
            (MessageRole.User, "How to prune roses in spring?"),
            (MessageRole.Assistant, "Cut above outward buds."),
        });

        // P2: index via the public write path (hashing provider).
        db.UpsertConversation(s1, c1);
        db.UpsertConversation(s2, c2);

        // P1/P3: ingress + intermediate artifacts inspected out-of-band.
        // Width-agnostic invariants (the DB crew stores float16 rows tagged
        // "<base>|f16" with dims unchanged; legacy float32 rows may remain):
        // every row carries hashing dims, a compatible model tag, and a blob
        // whose width detects as 2 or 4 bytes/element.
        var back = db.GetMessagesBySession("hash-a");
        Assert.Equal(2, back.Count);
        using (var conn = new SqliteConnection("Data Source=" + dbPath))
        {
            conn.Open();
            using var modelCmd = conn.CreateCommand();
            modelCmd.CommandText = "SELECT model, dims, LENGTH(embedding) FROM message_embeddings;";
            var rows = new List<(string Model, int Dims, int Len)>();
            using (var r = modelCmd.ExecuteReader())
                while (r.Read()) rows.Add((r.GetString(0), r.GetInt32(1), r.GetInt32(2)));
            Assert.Equal(4, rows.Count); // 2 sessions x 2 messages
            Assert.All(rows, row =>
            {
                Assert.Equal(TextEmbedder.Dims, row.Dims);
                Assert.True(TextEmbedder.IsModelCompatible(row.Model),
                    $"unexpected model tag '{row.Model}'");
                Assert.Contains(TextEmbedder.DetectWidth(
                    new byte[row.Len], row.Dims), new[] { 2, 4 });
            });

            // Simulate the future neural writer: a row the hashing query must ignore.
            // Content deliberately shares query tokens — exclusion must come from the
            // model/dims prefilter, not from lexical luck.
            var rng = new Random(7);
            var neuralBlob = new byte[384 * 4];
            rng.NextBytes(neuralBlob);
            using var ins = conn.CreateCommand();
            ins.CommandText = "INSERT INTO message_embeddings " +
                "(session_id, message_index, chunk_ord, embedding, dims, model, updated_at) " +
                "VALUES ('neural-sim', 0, 0, @e, 384, 'minilm-l6-v2-onnx', @at);";
            ins.Parameters.AddWithValue("@e", neuralBlob);
            ins.Parameters.AddWithValue("@at", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            Assert.Equal(1, ins.ExecuteNonQuery());
        }

        // P2: run the real semantic query.
        var results = db.SearchSemantic("NVML linking error", limit: 50);

        // P4: independent egress — row counts per model straight from SQL.
        using (var conn = new SqliteConnection("Data Source=" + dbPath))
        {
            conn.Open();
            using var cnt = conn.CreateCommand();
            cnt.CommandText = "SELECT model, COUNT(*) FROM message_embeddings GROUP BY model ORDER BY model;";
            var counts = new Dictionary<string, int>();
            using (var r = cnt.ExecuteReader())
                while (r.Read()) counts[r.GetString(0)] = r.GetInt32(1);
            Assert.Equal(1, counts["minilm-l6-v2-onnx"]);
            var hashingRows = counts
                .Where(kv => TextEmbedder.IsModelCompatible(kv.Key))
                .Sum(kv => kv.Value);
            Assert.Equal(4, hashingRows);
        }

        // P5: hashing recall intact; the neural row never surfaces despite token overlap.
        Assert.DoesNotContain(results, r => r.SessionId == "neural-sim");
        Assert.Contains(results, r => r.SessionId == "hash-a");
        Assert.True(results.All(r => r.Score > 0));
    }

    // ---- Model-dir isolation ----

    [Fact]
    public void ModelDirIsolation_RespectsEnvOverrideAndNeverTouchesProd()
    {
        // P1: override is honored; prod path resolved with the override cleared.
        var isolated = EmbeddingModelManager.ModelsDirectory;
        Assert.Equal(Path.Combine(_tempDir, "models"), isolated);

        var saved = Environment.GetEnvironmentVariable(EmbeddingModelManager.ModelsDirEnvVar);
        string prodDir;
        try
        {
            Environment.SetEnvironmentVariable(EmbeddingModelManager.ModelsDirEnvVar, null);
            prodDir = EmbeddingModelManager.ModelsDirectory;
        }
        finally
        {
            Environment.SetEnvironmentVariable(EmbeddingModelManager.ModelsDirEnvVar, saved);
        }
        Assert.EndsWith(Path.Combine("Casr", "models"), prodDir);
        Assert.NotEqual(isolated, prodDir);

        // Snapshot prod state (existence + file count) — our calls must not change it.
        bool prodExisted = Directory.Exists(prodDir);
        int prodFiles = prodExisted ? Directory.GetFiles(prodDir).Length : -1;

        // P2: readiness against the empty isolated dir fails with a clear message.
        Assert.False(EmbeddingModelManager.IsModelPresent);
        Assert.False(EmbeddingModelManager.TryEnsureReady(out var error));
        Assert.NotNull(error);
        Assert.Contains("not downloaded", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Download-EmbeddingModel.ps1", error, StringComparison.Ordinal);
        Assert.Equal(error, EmbeddingModelManager.LastError);

        // P4: prod dir untouched by every call above.
        Assert.Equal(prodExisted, Directory.Exists(prodDir));
        if (prodExisted) Assert.Equal(prodFiles, Directory.GetFiles(prodDir).Length);

        // P5: a corrupt (undersized) model file in the ISOLATED dir fails honestly.
        File.WriteAllText(EmbeddingModelManager.ModelPath, "not a model");
        File.WriteAllText(EmbeddingModelManager.VocabPath, "[CLS]\n");
        Assert.True(EmbeddingModelManager.IsModelPresent); // presence: files exist
        Assert.False(OnnxEmbedder.TryCreate(out var emb, out var createError));
        Assert.Null(emb);
        Assert.NotNull(createError);
        Assert.Equal(prodExisted, Directory.Exists(prodDir)); // still untouched
    }

    [Fact]
    public void TryEnableNeural_MissingModelKeepsHashingDefault()
    {
        // P1: isolated dir is empty (ctor created it, nothing downloaded).
        Assert.False(EmbeddingModelManager.IsModelPresent);

        // P2: opt-in fails honestly.
        Assert.False(TextEmbedder.TryEnableNeural(out var error));
        Assert.NotNull(error);
        Assert.Contains("not downloaded", error, StringComparison.OrdinalIgnoreCase);

        // P3/P4: facade still on hashing — model/dims/label unchanged, embeds work.
        Assert.False(TextEmbedder.IsNeuralActive);
        Assert.Equal("hashing-trigram-v1", TextEmbedder.ModelId);
        Assert.Equal(512, TextEmbedder.Dims);
        Assert.Equal(error, TextEmbedder.LastError);
        var v = TextEmbedder.Embed("still hashing");
        Assert.Equal(512, v.Length);
        Assert.InRange(Norm(v), 0.999, 1.001);
    }

    // ---- Tokenizer + decode units (pure managed, no native runtime, no model) ----

    [Fact]
    public void Tokenizer_WordPieceBasics()
    {
        // P1: ingress — tiny vocab file written to temp, read back independently.
        var vocabPath = Path.Combine(_tempDir, "vocab.txt");
        var lines = new[] { "[CLS]", "[SEP]", "[UNK]", "hello", "world", "##s", ",", "mini", "##lm" };
        File.WriteAllLines(vocabPath, lines);
        var readBack = File.ReadAllLines(vocabPath);
        Assert.Equal(lines, readBack);
        var expectedId = readBack
            .Select((t, i) => (t, i))
            .ToDictionary(x => x.t, x => x.i);

        // P2: load via the production loader.
        var tok = MiniLmTokenizer.Load(vocabPath);

        // P3: intermediate — vocab size matches the file line count.
        Assert.Equal(lines.Length, tok.VocabSize);

        // P4: encode verified against the independently built id map.
        var (ids, mask, typeIds) = tok.Encode("hello worlds, miniLM", 32);
        var want = new long[]
        {
            expectedId["[CLS]"], expectedId["hello"], expectedId["world"], expectedId["##s"],
            expectedId[","], expectedId["mini"], expectedId["##LM".ToLowerInvariant()],
            expectedId["[SEP]"],
        };
        Assert.Equal(want, ids);
        Assert.All(mask, m => Assert.Equal(1L, m));
        Assert.Equal(new long[ids.Length], typeIds);

        // P5: unknowns, truncation, and case-insensitivity round-trip.
        var (uIds, _, _) = tok.Encode("xyzzy", 32);
        Assert.Contains((long)expectedId["[UNK]"], uIds);
        var (tIds, _, _) = tok.Encode(string.Join(" ", Enumerable.Repeat("hello", 500)), 16);
        Assert.Equal(16, tIds.Length);
        var (cIds, _, _) = tok.Encode("HELLO", 32);
        Assert.Contains((long)expectedId["hello"], cIds);
    }

    [Fact]
    public void HiddenStateDecode_F32F16ParityAndShapeGuards()
    {
        // P1: ingress — identical payloads in f32 and f16, shape [1,2,4].
        var values = new float[] { 1, 2, 3, 4, 0.5f, -1, 0, 2 };
        var f32 = new DenseTensor<float>(values.ToArray(), new[] { 1, 2, 4 });
        var f16 = new DenseTensor<Half>(values.Select(v => (Half)v).ToArray(), new[] { 1, 2, 4 });
        Assert.Equal(new[] { 1, 2, 4 }, f32.Dimensions.ToArray());

        // P2/P3: decode both through the production path.
        var fromF32 = OnnxEmbedder.DecodeHiddenStates(f32, 4);
        var fromF16 = OnnxEmbedder.DecodeHiddenStates(f16, 4);

        // P4: f16 matches f32 within half-precision tolerance (independent paths agree).
        Assert.Equal(fromF32.Length, fromF16.Length);
        for (var i = 0; i < fromF32.Length; i++)
            Assert.InRange(fromF16[i], fromF32[i] - 0.002, fromF32[i] + 0.002);

        // P5: masked mean-pool round-trip: mask [1,0] recovers row 0 exactly.
        var pooled = OnnxEmbedder.MeanPool(fromF32, new long[] { 1, 0 }, 4);
        Assert.Equal(new float[] { 1, 2, 3, 4 }, pooled);
        var normed = EmbeddingVectors.L2Normalize(pooled.ToArray());
        Assert.InRange(Norm(normed), 0.999, 1.001);

        // Shape/type guards throw descriptively instead of misshaping.
        var wrongHidden = new DenseTensor<float>(new float[1 * 2 * 5], new[] { 1, 2, 5 });
        var ex1 = Assert.Throws<InvalidOperationException>(() => OnnxEmbedder.DecodeHiddenStates(wrongHidden, 4));
        Assert.Contains("hidden size", ex1.Message, StringComparison.OrdinalIgnoreCase);
        var rank2 = new DenseTensor<float>(new float[8], new[] { 2, 4 });
        Assert.Throws<InvalidOperationException>(() => OnnxEmbedder.DecodeHiddenStates(rank2, 4));
        var bad = Assert.Throws<InvalidOperationException>(() => OnnxEmbedder.DecodeHiddenStates("nope", 4));
        Assert.Contains("float", bad.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Width + tag compatibility: legacy f32, current f16, foreign neural ----

    [Fact]
    public void CodecCompat_LegacyF32AndF16DecodeWhileForeignModelNeverMatches()
    {
        // P1: ingress — a real hashing vector and its byte widths.
        var vec = TextEmbedder.Embed("nvml linking error");
        Assert.InRange(Norm(vec), 0.999, 1.001);
        var f32 = TextEmbedder.ToBytes(vec);
        var f16 = TextEmbedder.ToBytesF16(vec);
        Assert.Equal(512 * 4, f32.Length);
        Assert.Equal(512 * 2, f16.Length);

        // P2/P3: width detection + tag compatibility through the public API.
        Assert.Equal(4, TextEmbedder.DetectWidth(f32, 512));
        Assert.Equal(2, TextEmbedder.DetectWidth(f16, 512));
        Assert.Equal(0, TextEmbedder.DetectWidth(new byte[100], 512));
        Assert.True(TextEmbedder.IsModelCompatible(TextEmbedder.ModelId));
        Assert.True(TextEmbedder.IsModelCompatible(TextEmbedder.F16ModelId));
        Assert.False(TextEmbedder.IsModelCompatible("minilm-l6-v2-onnx"));
        Assert.False(TextEmbedder.IsModelCompatible("minilm-l6-v2-onnx|f16"));
        Assert.False(TextEmbedder.IsModelCompatible(null));
        Assert.False(TextEmbedder.IsModelCompatible("hashing-trigram-v2"));

        // P4: out-of-band decode — auto reads both widths.
        var backF32 = TextEmbedder.FromBytesAuto(f32, 512);
        var backF16 = TextEmbedder.FromBytesAuto(f16, 512);
        Assert.Equal(vec, backF32);

        // P5: round-trip quality — f16 loses precision but keeps direction;
        // legacy and current rows score ~1 against the live query vector.
        Assert.InRange(TextEmbedder.Cosine(vec, backF32), 0.999, 1.001);
        Assert.InRange(TextEmbedder.Cosine(vec, backF16), 0.99, 1.001);
        var qv = TextEmbedder.Embed("nvml linking error");
        Assert.InRange(TextEmbedder.Cosine(qv, backF16), 0.99, 1.001);
    }

    // ---- Full ONNX inference: runs when the optional model is present, else an honest skip ----

    private static void RunWithProductionModel(Action body)
    {
        // The class fixture redirects CASR_MODELS_DIR so the other tests never touch
        // the production models dir. Model-gated tests resolve the real location instead.
        var saved = Environment.GetEnvironmentVariable(EmbeddingModelManager.ModelsDirEnvVar);
        Environment.SetEnvironmentVariable(EmbeddingModelManager.ModelsDirEnvVar, null);
        try { body(); }
        finally { Environment.SetEnvironmentVariable(EmbeddingModelManager.ModelsDirEnvVar, saved); }
    }

    [Fact]
    public void HashingEmbedder_EmbedBatch_MatchesSingleEmbeddings()
    {
        IEmbeddingProvider provider = HashingEmbedder.Instance;
        var texts = new[] { "alpha beta gamma", "", "delta epsilon zeta eta", "   " };

        var batch = provider.EmbedBatch(texts);

        Assert.Equal(texts.Length, batch.Length);
        for (var i = 0; i < texts.Length; i++)
        {
            Assert.Equal(provider.Embed(texts[i]), batch[i]);
        }
    }

    [RequiresEmbeddingModelFact]
    public void OnnxEmbedder_EmbedBatch_MatchesSingleEmbeddings()
    {
        RunWithProductionModel(() =>
        {
            Assert.True(OnnxEmbedder.TryCreate(out var embedder, out var error), $"TryCreate failed: {error}");
            using (embedder)
            {
                var texts = new[]
                {
                    "first text",
                    "second, much longer text with more tokens and moar words again",
                    "third"
                };

                var batch = embedder!.EmbedBatch(texts);
                Assert.Equal(texts.Length, batch.Length);
                for (var i = 0; i < texts.Length; i++)
                {
                    var single = embedder.Embed(texts[i]);
                    Assert.Equal(single.Length, batch[i].Length);
                    // Batched matmuls sum in a different order, so compare by cosine.
                    Assert.InRange(EmbeddingVectors.Cosine(single, batch[i]), 0.99999, 1.00001);
                }

                // Blanks stay blank in place; a blank row never shifts its neighbours.
                var mixed = embedder.EmbedBatch(new[] { "x", "   ", null, "y" });
                Assert.Equal(384, mixed[0].Length);
                Assert.All(mixed[1], f => Assert.Equal(0f, f));
                Assert.All(mixed[2], f => Assert.Equal(0f, f));
                Assert.InRange(EmbeddingVectors.Cosine(embedder.Embed("y"), mixed[3]), 0.99999, 1.00001);
            }
        });
    }

    [RequiresEmbeddingModelFact]
    public void OnnxInference_EndToEndWhenModelPresent()
    {
        RunWithProductionModel(() =>
        {
            // P1: ingress — model + vocab exist with real sizes; hash verifies.
            var modelInfo = new FileInfo(EmbeddingModelManager.ModelPath);
            var vocabInfo = new FileInfo(EmbeddingModelManager.VocabPath);
            Assert.True(modelInfo.Length > 10_000_000);
            Assert.True(vocabInfo.Length > 100_000);
            Assert.True(EmbeddingModelManager.TryEnsureReady(out _));

            // P2: create + embed through the production factory.
            Assert.True(OnnxEmbedder.TryCreate(out var embedder, out var error), $"TryCreate failed: {error}");
            Assert.NotNull(embedder);
            // Provider must be explicit: CUDA on the GPU build (when CUDA 13 + cuDNN 9 resolve),
            // CPU on the standard build or any machine without the CUDA stack.
            Assert.Contains(embedder!.ProviderLabel, new[] { "CPU", "CUDA (GPU)" });
            using (embedder)
            {
                var vec = embedder.Embed("the quick brown fox jumps over the lazy dog");

                // P3: intermediate — 384 dims, finite, normalized.
                Assert.Equal(384, vec.Length);
                Assert.All(vec, x => Assert.True(float.IsFinite(x)));
                Assert.InRange(Norm(vec), 0.999, 1.001);

                // P4: independent check — bytes on disk decode back identically.
                var bytes = EmbeddingVectors.ToBytes(vec);
                Assert.Equal(384 * 4, bytes.Length);
                Assert.Equal(vec, EmbeddingVectors.FromBytes(bytes, 384));

                // P5: round-trip quality — deterministic, self-similar, session path works.
                var again = embedder.Embed("the quick brown fox jumps over the lazy dog");
                Assert.Equal(vec, again);
                Assert.InRange(EmbeddingVectors.Cosine(vec, again), 0.999, 1.001);
                var session = embedder.EmbedSession(new List<(string?, int)>
                {
                    ("first message", 10), ("second message", 10),
                });
                Assert.Equal(384, session.Length);
                Assert.InRange(Norm(session), 0.999, 1.001);
            }
        });
    }
}

/// <summary>
/// Runs the ONNX end-to-end test only when the optional MiniLM model + vocab are
/// present; otherwise the test is skipped with a reason at discovery time (xUnit 2
/// has no runtime skip). Never a fake pass: absent model = reported skip.
/// </summary>
public sealed class RequiresEmbeddingModelFactAttribute : FactAttribute
{
    public RequiresEmbeddingModelFactAttribute()
    {
        if (!EmbeddingModelManager.IsModelPresent)
        {
            Skip = "Requires the downloaded MiniLM model (scripts/Download-EmbeddingModel.ps1); " +
                   "the model is optional and not bundled.";
        }
    }
}
