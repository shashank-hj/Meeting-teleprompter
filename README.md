# Local Meeting Teleprompter

Native Windows desktop implementation of [the design proposal](local-meeting-teleprompter-proposal.html), using C#/.NET 10 and WinUI 3 / Windows App SDK 2.4. The Python web prototype has been retired.

Current review: **native MVP implemented; release acceptance remains open**. See [proposal coverage, completed changes, current measurements, and remaining gates](docs/PROJECT-STATUS.md). The regression suite includes 84 checks with the optional installed-Edge check.

The desktop now includes a compact **Meeting view**, audio/readiness guidance, a session clock, **Auto assist** pause without stopping transcription, and quick **Answer latest** / **Evidence** actions. See [how it works in live meetings](docs/LIVE-MEETINGS.md). The **?** button contains the same practical walkthrough in the app.

## Build and open

Requirements: Windows 10 build 19041 or later, Windows x64, .NET 10 SDK. Package restore needs internet access; the app does not use cloud inference.

```powershell
.\scripts\build.ps1
.\scripts\start.ps1
```

Close the app before rebuilding the same configuration. Windows keeps its loaded DLLs locked while the app is running; `build.ps1` reports the process ID if it finds that lock. Export any transcript before closing the app.

The release executable is at:
`src\MeetingTeleprompter.App\bin\Release\net10.0-windows10.0.19041.0\win-x64\MeetingTeleprompter.App.exe`

## Local model setup

This computer is configured with Whisper base.en, Gemma 2 2B from the existing local model cache, EmbeddingGemma, Silero ONNX, and sqlite-vec. Start with `scripts/start.ps1`; native servers start when needed, with speech taking priority. The 20 November comparison measured 2.92-second median transcription requests in the low-latency profile. See [current and historical validation results](docs/VALIDATION.md).

The Gemma 2 profile is used for this laptop's CPU and memory budget; Gemma 4 is still an optional candidate, not an installed dependency. Keep the existing Ollama model blob referenced in Settings, or change the generator model path before removing that cache.

To reproduce setup, run `scripts/install-local-models.ps1`. It verifies downloaded runtime and model checksums, reuses the existing Gemma 2 blob, and preserves other settings. On another computer, pass `-GeneratorModelPath` with a compatible instruct GGUF whose license you have accepted. Speech profile selection is also available in Settings.

Model weights and native inference binaries are separate local assets, excluded from Git. For a different computer, download compatible Windows CPU builds from the upstream projects, retaining their DLLs next to the executable:

- [whisper.cpp](https://github.com/ggml-org/whisper.cpp): `whisper-server.exe` and `ggml-base.en.bin`.
- [llama.cpp](https://github.com/ggml-org/llama.cpp): `llama-server.exe` and a compatible small instruct GGUF. The proposal recommends Gemma 4 E2B Q4; select weights whose license you have accepted and whose architecture your runtime supports.
- Optional [EmbeddingGemma](https://ai.google.dev/gemma/docs/embeddinggemma) GGUF for semantic retrieval.
- Optional [Silero VAD](https://github.com/snakers4/silero-vad) v5/v6 ONNX model with input/state/sr and output/stateN tensors.
- Optional Windows x64 [sqlite-vec](https://github.com/asg017/sqlite-vec) `vec0.dll` for native vector distance queries.

Open **Settings**, enter the executable/model paths, and save. The app starts configured native servers locally, limits their CPU threads, prioritizes ASR over generation, and stops its owned processes on exit. Generation uses one slot and a 2048-token context. **Check device** benchmarks a supplied 1–10 second, 16 kHz mono WAV without microphone capture. Memory conservation is enabled by default: it estimates a 2 GiB reserve and can use keyword search and cited passages when generation cannot fit. Leaving executable/model paths empty attaches to servers you started yourself.

Default endpoints:

| Capability | Loopback endpoint |
| --- | --- |
| Speech | http://127.0.0.1:8178/ |
| Generation | http://127.0.0.1:8179/ |
| Embeddings | http://127.0.0.1:8180/ |

Equivalent manual startup (adjust paths):

```powershell
.\runtimes\whisper\whisper-server.exe -m .\models\ggml-base.en.bin --host 127.0.0.1 --port 8178 -ng -t 4
.\runtimes\llama\llama-server.exe -m .\models\instruct.gguf --host 127.0.0.1 --port 8179 -ngl 0 -c 2048 -np 1 -t 3
.\runtimes\llama\llama-server.exe -m .\models\embeddinggemma.gguf --host 127.0.0.1 --port 8180 --embedding --pooling mean -ngl 0 -c 2048 -t 2
```

Inference endpoints accept only numeric loopback HTTP addresses, with proxies and redirects disabled. Native worker messages never go to a cloud API. Do not expose the model servers to the network.

Without optional assets, keyword search and a basic energy-based voice detector remain available. Enable semantic search after configuring the embedding model; use **Refresh sources** to generate missing vectors. Change the embedding model/version identifier whenever replacing weights. With no sqlite-vec DLL, the same versioned vectors use a managed cosine fallback suited to small collections.

## Using the app

1. Add folders containing TXT, Markdown, HTML, text PDFs, or DOCX. The included `knowledge_base` folder is available as starter material.
2. Add public web URLs to fetch and index them immediately. Their stored copies change only when you add the URL again or click **Refresh sources**.
3. Select the meeting output device and microphone in Settings. Windows microphone privacy settings must allow desktop apps.
4. Start a session. WASAPI captures the selected output device's system audio, including other applications playing through it. The microphone appears as “You”; output audio appears as “Call”. Remote speakers are not individually identified.
5. Suggestions appear on topic/question/claim triggers. Meeting and typed questions stream labeled drafts, followed by the checked answer or a request for evidence. Use concise answer, evidence, explain, check claim, or follow-up routes, including on individual cards. Citation buttons show the cited passage and its heading/page/chunk, fetch time, and original location; choose **Open original** to navigate there.
6. Choose **Meeting view** for a compact window above the call. Pause **Auto assist** to read without automatic replacements; transcription and manual requests continue. Use **Ask a question**, **Answer latest**, or **Evidence** in that view.
7. Export before ending if you want a transcript file. **End & clear** and closing the app clear the in-memory transcript and cards. Export is available from **…** even with the transcript pane hidden.

The UI retains the latest 600 final utterances and 15 cards; exports contain that retained transcript. Always-on-top is optional. Audio device loss stops the session with an error; select the new device and restart.

Source removal deletes the document's stored chunks, full-text entries, and embeddings, not the original file. Removing a file also disables its containing watched folder so the deleted source is not immediately reimported; remaining indexed files stay searchable. Re-add the folder to resume watching.

## Architecture and behavior

- **Core:** contracts, transcript stabilization, bounded conversation state, trigger cooldowns, cancellation, source parsing, SQLite FTS5, embedding storage, hybrid ranking, safe URL fetcher, and runtime lifecycle.
- **Host:** separate ASR and generation bridge processes over randomized, current-user-only named pipes using protocol version 1. Native whisper.cpp and llama.cpp servers execute model inference in separate processes. Requests have deadlines; cancelled or failed pipe workers are discarded and restarted on the next request.
- **App:** WinUI shell, WASAPI capture, optional ONNX Silero VAD, source manager, live transcript, suggestion cards, settings, and explicit export.

Capture callbacks never wait for inference. Audio buffers and assistance queues are bounded. Whisper requests use a partial after approximately 2.5 seconds, natural-pause finalization, and a 7.5-second maximum window with 480 ms overlap. These are window lengths, not end-to-end latency guarantees. Overlapping windows are deduplicated by channel; final corrections replace canonical text without repeated triggers.

Generation is serialized. Manual requests cancel obsolete proactive work; topic advances suppress stale proactive results even during trigger cooldown. Empty retrieval does not launch generation. Invalid or missing citation markers cause an evidence-only fallback. This checks source identifiers, not factual entailment: generated claims still need human review.

Folder changes are debounced, with interrupted-write retries, deletion/rename/directory reconciliation, and explicit overflow errors. Unchanged chunks keep their embeddings. Background indexing pauses during calls. Web fetching validates redirects, pins public DNS results, rejects local/private addresses and credentials, and limits each fetch to 15 seconds and 10 MB. Compressed responses are rejected; local files are limited to 20 MB. JavaScript-only pages can use an isolated installed-Edge fallback, limited to 25 seconds, 40 same-origin GET requests and 10 MB total. It blocks private/third-party requests, downloads and service workers. Disable it in Settings if needed. Scanned PDF OCR and authenticated pages are not included. Failed URL refreshes keep the saved evidence and display their status.

Settings and the default index live in `%LOCALAPPDATA%\MeetingTeleprompter`. The index directory is configurable. Audio is held in memory by the app; routine app diagnostics do not persist transcripts or prompts. Export is the explicit persistence action. Native server behavior is governed by the installed upstream runtime; app-managed stdout/stderr is discarded.

## Validation and performance

```powershell
.\scripts\build.ps1
```

This builds all projects and runs the executable acceptance checks for transcript revisions/overlap, address safety, chunking, ingestion, incremental indexes, parser upgrades, embedding/cascade behavior, retrieval fusion, proactive triggers, auto-assist pause/resume, scheduling/cancellation, filesystem watching, resource fallback and actual named-pipe transport/failure/restart. Add `--with-browser` to the host's `--self-test` command to include installed Edge (84 checks total). Native model output is not simulated in the product; automated transport tests use a deterministic HTTP fixture.

Run `scripts/render-ui.ps1` for an isolated sample-content render of the actual XAML layouts, with audio capture disabled. It checks key controls at multiple window sizes and writes preview images under `artifacts/ui-live-review`. It does not replace interactive desktop or live-call testing.

For repeatable comparisons of the installed base speech profiles and optional small model, run `scripts/evaluate-profiles.ps1 -IncludeSmallSpeechModel`. Its separate indexes and reports stay under `artifacts/profile-evaluation`; it does not change app settings. Extra settings files can compare other installed native-model candidates. The current comparison and unvalidated runtime experiments are documented in [PROJECT-STATUS.md](docs/PROJECT-STATUS.md).

A concurrent local ASR/generation benchmark is available after model installation. Supply an existing raw mono 16 kHz signed little-endian PCM16 recording, up to 60 seconds:

```powershell
.\src\MeetingTeleprompter.Host\bin\Release\net10.0\MeetingTeleprompter.Host.exe --benchmark "$env:LOCALAPPDATA\MeetingTeleprompter\settings.json" .\meeting-sample.pcm
```

It outputs timings and host memory as JSON, without transcript text. The real-model and WASAPI checks have now been run on this computer; see [the measured results](docs/VALIDATION.md). Use `scripts/validate-local-models.ps1` to repeat the public-corpus accuracy, latency, retrieval, and microphone-off hardware checks. Sustained performance in your own conferencing workload still depends on meeting-client load, audio conditions, and available memory.

## Packaging

```powershell
.\scripts\package.ps1
.\scripts\verify-package.ps1 -RunChecks -WithBrowser
```

Creates a self-contained x64 MSIX at `dist\MeetingTeleprompter.msix`, including worker executables but excluding model assets. Windows SDK MakeAppx is required. To sign with your existing matching certificate, pass `-CertificatePath`. The manifest publisher is `CN=LocalMeetingTeleprompter`; change it to your actual publisher before distribution. An unsigned package cannot be installed normally.

The verification script checks package contents and can extract and run the actual packaged worker checks. It does not install the package or change certificate trust. Reports and extracted workers stay under `artifacts/msix-validation`.

## Cleanup and recovery

The old Python server, browser template, Python caches, and obsolete Python/Chroma indexes were removed from the active project. A recoverable copy is kept locally in:

`.cleanup-backup\python-prototype-20260911.zip`

The original proposal and `knowledge_base` documents are preserved. The backup, model assets, local databases, and build outputs are excluded from Git.
