# Neural embeddings (MiniLM via ONNX)

Status: **wired** — real ONNX inference path, opt-in, model not bundled.

## How it works

- `src/Casr.Core/Search/EmbeddingProvider.cs` — `IEmbeddingProvider`
  (`ModelId`, `Dims`, `Embed`, `EmbedSession`) plus `HashingEmbedder`
  (today's trigram behavior, the default) and shared `EmbeddingVectors`
  math (session weighting, cosine, byte codec).
- `src/Casr.Core/Search/OnnxEmbedder.cs` — MiniLM-class transformer,
  384 dims, WordPiece tokenizer + attention-masked mean pooling +
  L2 normalization, via `Microsoft.ML.OnnxRuntime` 1.30.0. Accepts
  float32 or float16 model outputs; refuses misshaped tensors loudly.
- `src/Casr.Core/Search/EmbeddingModelManager.cs` — models dir
  (`%LOCALAPPDATA%\Casr\models`, override `$CASR_MODELS_DIR`), SHA-256
  check (sidecar or `$CASR_MODEL_SHA256` pin), lazy load, `LastError`,
  log-once, never blocks startup.
- `src/Casr.Core/Search/TextEmbedder.cs` — unchanged static API
  (`Embed`, `EmbedSession`, `Cosine`, `ToBytes`, `FromBytes`,
  `ModelId`, `Dims`, `DisplayLabel`) delegating to the active provider,
  so `SessionDatabase` needed no changes. `TryEnableNeural()` /
  `ResetToDefault()` / `SetProvider()` switch providers.

## Mixed-model coexistence

Every embedding row already carries `model` + `dims`, and
`SearchSemantic` prefilters on both in SQL — hashing rows
(`hashing-trigram-v1`, 512) and neural rows (`minilm-l6-v2-onnx`, 384)
never cross-score. Verified by `SearchNeuralTests`
(`MixedModelCoexistence_NeuralRowsNeverCrossScore`).

## Getting the model

```pwsh
.\scripts\Download-EmbeddingModel.ps1
```

Downloads `all-MiniLM-L6-v2` ONNX (~90 MB) + `vocab.txt` from Hugging Face
and writes the SHA sidecar. No model ships with the repo. Enabling neural
search in the app re-embeds all sessions (old hashing rows stay and are
simply skipped by the prefilter).

## Honest limitations

- ONNX path is exercised by tests only at the decode/tokenizer level;
  end-to-end inference runs only when the model file is present
  (the inference test skips with a reason otherwise — never a fake pass).
- Switching models requires a full re-embed; there is no incremental
  cross-model migration yet.
- No UI toggle yet — `TryEnableNeural` is the programmatic entry point.
