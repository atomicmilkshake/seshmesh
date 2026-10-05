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

## GPU (CUDA) acceleration

The default build is CPU-only. A second release asset (`…-cuda.zip`) is built with
`-p:CasrGpu=true` and contains onnxruntime's CUDA execution provider (built against
**CUDA 13**):

```pwsh
dotnet publish src/Casr.App/Casr.App.csproj -c Release -r win-x64 --self-contained true -p:CasrGpu=true
```

Requirements for the CUDA EP to engage on a machine:
- an NVIDIA GPU (any that supports CUDA 13);
- the CUDA 13 toolkit `bin` directory on `PATH`;
- cuDNN 9 DLLs available — run `scripts/Download-CudaSupport.ps1` once; it fetches
  NVIDIA's redistributable wheel (no account needed) into `%LOCALAPPDATA%\Casr\cuda`,
  which SeshMesh prepends to its DLL search path before creating the provider.

Provider selection is automatic and never fatal: CUDA when it resolves, otherwise the
CPU provider (the `*-cuda` build still runs everywhere). `CASR_ONNX_CPU=1` forces CPU as
an escape hatch. The `🧠 Neural` tooltip reports the active device.

Batched inference is what makes the GPU pay: 32 texts per ONNX call instead of one.
Measured on this machine (RTX 3080, MiniLM-L6, 256-token cap):

| mode | per text | throughput |
|---|---|---|
| CPU, single | 3.00 ms | ~330/s |
| CPU, batch 32 | 1.75 ms | ~570/s |
| CUDA, single | 2.61 ms | ~380/s |
| CUDA, batch 32 | 0.14 ms | **~6,900/s** |

End-to-end index throughput also includes parsing and SQLite writes (~290 messages/s
observed while re-embedding 93k messages).

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
  the model is present and otherwise skips with a reason — never a fake pass. The
  batched path is verified element-wise against single-text calls.
- Switching models requires a full re-embed for the new model; it is per-session
  resumable (an interrupted re-embed skips sessions that already carry an active-model
  vector), but there is no cross-model migration that avoids re-embedding entirely.
- The CUDA build is a separate download and needs CUDA 13 + cuDNN 9 on the machine;
  the hashing embedder remains the default and the only offline option.
