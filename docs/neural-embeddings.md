# Neural embeddings (MiniLM via ONNX)

Status: **wired** — real ONNX inference path, UI opt-in shipped, model not bundled.

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
(`MixedModelCoexistence_NeuralRowsNeverCrossScore`). When a session is
re-indexed under the active model its old rows are replaced; sessions that
have not been re-embedded yet keep their foreign-model rows, which the
prefilter simply skips.

## Enabling in the app

The Deep Content row has a `🧠 Neural` toggle (disabled with an explanatory
tooltip while the model is missing):

- **Enable** — loads the model off the UI thread, then re-embeds the transcript
  index for the new model. Progress uses the normal index readout; the rebuild
  is cancellable and a cancelled run resumes on later scans (a session counts as
  re-embedded as soon as it has one active-model vector).
- **Disable** — returns to the offline hashing embedder; the next scan re-embeds
  for it the same way.
- The choice persists in `settings.json` (`NeuralEmbeddings`); at startup it is
  applied off the UI thread before the first index.

## Getting the model

```pwsh
.\scripts\Download-EmbeddingModel.ps1
```

Downloads `all-MiniLM-L6-v2` ONNX (~90 MB) + `vocab.txt` from Hugging Face
and writes the SHA sidecar. No model ships with the repo. Enabling neural
search in the app re-embeds all sessions for the new model.

## Honest limitations

- End-to-end inference (`OnnxInference_EndToEndWhenModelPresent`) runs whenever
  the model is present and otherwise skips with a reason — never a fake pass.
- Switching models requires a full re-embed; there is no incremental
  cross-model migration yet. On CPU this can take minutes for a large store;
  the re-embed is cancellable and resumes on the next scan.
- The hashing embedder remains the default and the only offline option.
