# Local Meeting Teleprompter

Native Windows desktop implementation of [the design proposal](local-meeting-teleprompter-proposal.html), using C#/.NET 10 and WinUI 3 / Windows App SDK 2.4. The Python web prototype has been retired.

## Build and open

Requirements: Windows 10 build 19041 or later, Windows x64, .NET 10 SDK. Package restore needs internet access; the app does not use cloud inference.

```powershell
.\scripts\build.ps1
.\scripts\start.ps1
```

The release executable is at:
`src\MeetingTeleprompter.App\bin\Release\net10.0-windows10.0.19041.0\win-x64\MeetingTeleprompter.App.exe`

## Local model setup

Model weights and native inference binaries are separate assets, not included in the repository. Download compatible Windows CPU builds from the upstream projects, retaining their DLLs next to the executable:

- [whisper.cpp](https://github.com/ggml-org/whisper.cpp): `whisper-server.exe` and `ggml-base.en.bin`.
- [llama.cpp](https://github.com/ggml-org/llama.cpp): `llama-server.exe` and a compatible small instruct GGUF. The proposal recommends Gemma 4 E2B Q4; select weights whose license you have accepted and whose architecture your runtime supports.
- Optional [EmbeddingGemma](https://ai.google.dev/gemma/docs/embeddinggemma) GGUF for semantic retrieval.
- Optional [Silero VAD](https://github.com/snakers4/silero-vad) v5/v6 ONNX model with input/state/sr and output/stateN tensors.
- Optional Windows x64 [sqlite-vec](https://github.com/asg017/sqlite-vec) `vec0.dll` for native vector distance queries.

Open **Settings**, enter the executable/model paths, and save. The app starts configured native servers locally, limits their CPU threads, prioritizes ASR over generation, and stops the processes it owns on exit. Generation uses one slot and a 2048-token context. Leaving executable/model paths empty attaches to servers you started yourself.

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
4. Start a session. WASAPI captures the selected output device's system audio, including other applications playing through it. The microphone appears as “You”; output audio appears as “Remote”. Remote speakers are not individually identified.
5. Suggestions appear on topic/question/claim triggers. Use concise answer, evidence, explain, check claim, or follow-up routes. Citation buttons open the original source; PDF page or chunk location and fetch time are displayed.
6. Export before stopping if you want a transcript file. **Stop**, **Clear session**, and closing the app clear the in-memory transcript and cards.

The UI retains the latest 600 final utterances and 15 cards; exports contain that retained transcript. Always-on-top is optional. Audio device loss stops the session with an error; select the new device and restart.

Source removal deletes the document's stored chunks, full-text entries, and embeddings, not the original file. Removing a file also disables its containing watched folder so the deleted source is not immediately reimported; remaining indexed files stay searchable. Re-add the folder to resume watching.

## Architecture and behavior

- **Core:** contracts, transcript stabilization, bounded conversation state, trigger cooldowns, cancellation, source parsing, SQLite FTS5, embedding storage, hybrid ranking, safe URL fetcher, and runtime lifecycle.
- **Host:** separate ASR and generation bridge processes over randomized, current-user-only named pipes using protocol version 1. Native whisper.cpp and llama.cpp servers execute model inference in separate processes. Requests have deadlines; cancelled or failed pipe workers are discarded and restarted on the next request.
- **App:** WinUI shell, WASAPI capture, optional ONNX Silero VAD, source manager, live transcript, suggestion cards, settings, and explicit export.

Capture callbacks never wait for inference. Audio buffers and assistance queues are bounded. Transcription uses windowed Whisper requests: partials after approximately two seconds of speech, finals after a pause or four seconds. Those are window lengths, not measured end-to-end latency guarantees. Overlapping transcript windows are deduplicated by channel.

Generation is serialized. Manual requests cancel obsolete proactive work; topic advances suppress stale proactive results even during trigger cooldown. Empty retrieval does not launch generation. Invalid or missing citation markers cause an evidence-only fallback. This checks source identifiers, not factual entailment: generated claims still need human review.

Folder changes are debounced, with retries for interrupted writes, deletion/rename handling, and explicit watcher overflow errors. Background indexing pauses during calls. Web fetching validates each redirect, pins public DNS results when connecting, rejects local/private addresses and credentials, and limits the entire request to 15 seconds and 10 MB. Compressed HTTP responses are rejected. Local files are limited to 20 MB; HTML scripts are never executed. Scanned PDF OCR, authenticated pages, and browser-based JavaScript rendering are not included.

Settings and the default index live in `%LOCALAPPDATA%\MeetingTeleprompter`. The index directory is configurable. Audio is held in memory by the app; routine app diagnostics do not persist transcripts or prompts. Export is the explicit persistence action. Native server behavior is governed by the installed upstream runtime; app-managed stdout/stderr is discarded.

## Validation and performance

```powershell
.\scripts\build.ps1
```

This builds all projects and runs 26 executable acceptance checks for transcript revisions/overlap, address safety, chunking, file ingestion, FTS refresh/removal, embedding versioning/cascade deletion, retrieval fusion, proactive triggers, scheduler recovery, manual cancellation of stale generation, and actual named-pipe worker transport/failure/restart against a local test endpoint. Native model output is not simulated in the product; only the automated integration test uses a deterministic HTTP fixture.

A concurrent local ASR/generation benchmark is available after model installation. Supply an existing raw mono 16 kHz signed little-endian PCM16 recording, up to 60 seconds:

```powershell
.\src\MeetingTeleprompter.Host\bin\Release\net10.0\MeetingTeleprompter.Host.exe --benchmark "$env:LOCALAPPDATA\MeetingTeleprompter\settings.json" .\meeting-sample.pcm
```

It outputs timings and host memory as JSON, without transcript text. Measure total native-runtime memory and meeting-client CPU use separately in Task Manager. The proposal's 16 GB memory envelope, ASR quality, and 1–15 second latency targets require measurements with real models and representative meeting audio; they are not certified by unit tests.

## Packaging

```powershell
.\scripts\package.ps1
```

Creates a self-contained x64 MSIX at `dist\MeetingTeleprompter.msix`, including worker executables but excluding model assets. Windows SDK MakeAppx is required. To sign with your existing matching certificate, pass `-CertificatePath`. The manifest publisher is `CN=LocalMeetingTeleprompter`; change it to your actual publisher before distribution. An unsigned package cannot be installed normally.

## Cleanup and recovery

The old Python server, browser template, Python caches, and obsolete Python/Chroma indexes were removed from the active project. A recoverable copy is kept locally in:

`.cleanup-backup\python-prototype-20260911.zip`

The original proposal and `knowledge_base` documents are preserved. The backup, model assets, local databases, and build outputs are excluded from Git.
