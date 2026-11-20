# Validation history

## 20 November 2026 review

The implementation review passed the acceptance suite, including actual installed-Edge rendering with blocked private/third-party requests, named-pipe inference transport, real folder changes/retries/renames/deletions, incremental FTS/vector behavior, parser upgrades, transcript corrections, stale-work cancellation, and low-memory evidence fallback. The final suite contains 81 checks including the browser check. Compilation completed without compiler errors; package restore's NU1900 feed warning is recorded below.

Three installed Whisper profiles were compared against the same twelve public AMI excerpts (58.45 seconds, 157 reference words), with the installed Gemma 2 2B generator running concurrently for the final four samples:

| Profile | Median ASR request | Maximum | WER | Real-time factor | Sampled simultaneous native working-set peak |
| --- | ---: | ---: | ---: | ---: | ---: |
| base.en, 512 context | 2.92 s | 3.79 s | 22.3% | 0.58 | 3.52 GiB |
| base.en, full context | 7.27 s | 10.68 s | 20.4% | 1.61 | 3.55 GiB |
| small.en Q5, 512 context | 11.11 s | 17.19 s | 22.9% | 2.34 | 3.61 GiB |

In the selected base profile, semantic retrieval took 172 ms. The first concurrent grounded fixture took 9.93 seconds to first token and 12.58 seconds to finish. All four generated fixtures named the supplied owner/date and cited [1]; repeated identical prompts benefit from caching. Average backend-reported generation throughput was approximately 6.7 tokens/s. Request times exclude speech collection and startup.

The minimum sampled free system RAM was 0.30 GiB. The combined stack did not meet the proposal's desired 2–3 GB safety margin under this machine's current background load. The application now starts ASR first, defers generator startup, and can conserve memory by using keyword retrieval and cited source passages. The budget is a conservative estimate, not a guarantee against other processes allocating RAM.

The shared device-check engine was also exercised from the extracted packaged worker with one 2.61-second public WAV. Under the later session load it measured 5.10 seconds for ASR and 26.90 seconds for generation (21.72 seconds to first token); the citation fixture passed. This separate one-clip check exceeded the latency targets and recommended a lower-latency profile. It reinforces that this machine has not passed sustained-call acceptance; the earlier multi-profile measurements must not be treated as guaranteed current latency.

The native audio path was checked again after the changes: a public 2.61-second sample was played through Realtek output, captured by WASAPI loopback, segmented by Silero and transcribed through the isolated worker. One final frame was transcribed; microphone capture was disabled. With the current 2.5-second preview threshold this short clip produced no partial frame. Capture completed in approximately 4.26 seconds including the finalization wait; this is not an ASR latency measurement.

Current raw artifacts (local, ignored):

- `artifacts/profile-evaluation/20261120-141822/comparison.json` and each profile's settings/results.
- `artifacts/meeting-validation/completion-audio-results.json`.
- `artifacts/msix-validation/20261120/package-check.json` and `device-benchmark.json` (the latter is the one-clip run before the final parser-version update).
- `artifacts/msix-validation/latest-package-check.json` — final MSIX content/hash verification and all 81 checks run from its extracted worker. The final package SHA-256 is `95EA5A95D2DF029013E9A805AD2DEC4EC1E7D9DC0EA3CA38F9C8C13E7D43CC80`.

The final package is self-contained Windows x64 and excludes model weights. It still needs publisher signing for normal installation. Package restore reported NU1900 because the NuGet vulnerability feed could not be reached, including on an escalated retry; the dependency audit is therefore unverified. Desktop visual verification could not run because the computer-use helper failed to initialize after the session restart. Neither limitation is counted as a passing check.

See [the requirement-by-requirement status and remaining acceptance gates](PROJECT-STATUS.md). The October report below is retained as historical evidence; its 1.40-second result is not presented as the current machine measurement.

## 3 October 2026

The app is configured on this computer and has passed real local model and Windows audio checks. No microphone recording was made.

## Installed configuration

| Component | Installed selection |
| --- | --- |
| Computer | Intel Core i5-8350U, 4 cores / 8 logical processors, 16 GB RAM |
| Speech | whisper.cpp b5130, Whisper base.en, CPU, 2 threads |
| Speech profile | 512 audio-context positions (about 10.24 seconds), best-of 1; live windows are at most 4 seconds |
| Generation | llama.cpp b10909; existing Gemma 2 2B GGUF weights from the local Ollama cache; CPU, 2 threads, one slot |
| Embeddings | EmbeddingGemma 300M Q8_0 with sqlite-vec 0.1.9 |
| Voice activity | Silero v6.2.1 ONNX, one inference thread per channel |
| Source index | Included knowledge_base documents indexed with lexical and semantic retrieval |

Gemma 2 2B is the working low-memory generation profile on this machine. The proposal's Gemma 4 candidates have not been benchmarked or installed. The chosen GGUF lives in the existing Ollama model cache, but the app runs it directly through llama.cpp; an Ollama service is not needed.

## Speech accuracy and latency

The test uses twelve public [AMI meeting corpus excerpts](https://huggingface.co/datasets/edinburghcstr/ami), totaling 58.45 seconds and 157 reference words. Selection is deterministic: the first four utterances lasting 2–10 seconds with at least six words, at test-set offsets 0, 1500, and 3000. The excerpts cover three meeting sessions. They were not selected by recognition accuracy.

| Measurement | Initial full-context profile | Selected low-latency profile |
| --- | ---: | ---: |
| Median request-to-transcript time | 4.62 s | 1.40 s |
| Maximum request-to-transcript time | 6.65 s | 1.84 s |
| Word error rate | 20.4% (32/157) | 21.0% (33/157) |
| Total ASR time / total audio duration | 1.01 | 0.29 |
| Retrieval request | 224 ms | 231 ms |

The initial run used full audio context and the server's default best-of value. The selected profile reduces context to 512 and best-of to 1. These request latencies exclude the time spent collecting speech and startup. In normal operation, a partial is requested after roughly two seconds of speech; finalization happens at a pause or four-second window boundary. Concurrent speech on two channels adds work.

Word error rate counts word substitutions, insertions, and deletions after case and punctuation normalization. A 21% result still calls for reviewing names, numbers, and citations. This small headset-microphone sample does not certify accuracy for every accent, microphone, conferencing codec, or actual user meeting.

## Generation, retrieval, and resource use

The final four ASR samples started a generation concurrently. The fixture asks for a release date and owner from one explicitly supplied passage.

- All four responses named the correct date and owner and cited [1].
- First response: 9.59 s to first token and 13.07 s to completion.
- Repeated requests: 3.56–3.97 s to completion. These reuse the same prompt and can benefit from llama.cpp prompt caching; they are not independent cold-prompt latency estimates.
- Runtime startup: 16.66 s for the three local servers in the selected-profile run.
- Sum of native-process peak working sets: approximately 3.29 GiB. This is a sum of process peaks, not a simultaneous total-system peak; Windows, the desktop app, and the meeting client are additional.
- Semantic retrieval returned five cited passages from the configured source index.

The measured ASR profile has substantially more throughput headroom than the initial configuration. A sustained call with a running conferencing client, thermal throttling, overlapping speakers, and larger source collections remains a separate workload from these checks.

## Actual Windows audio path

A public 2.61-second meeting excerpt was played through **Speakers / Headphones (Realtek Audio)** and captured through WASAPI loopback. Silero produced one partial and one final speech frame; the final frame was successfully transcribed by the isolated local worker.

The test included playback stopping completely, exercising idle-output finalization. Microphone capture was explicitly disabled. Test audio and ASR hypotheses were not persisted by the audio-capture check.

## Repeat the checks

From the repository root, with the app closed:

```powershell
.\scripts\build.ps1
.\scripts\validate-local-models.ps1
```

The second command runs real inference and plays one public excerpt through the selected output. It does not capture the microphone. Use `-AudioOnly` for just the hardware check.

Local measurement artifacts:

- `artifacts/meeting-validation/results.json` — initial full-context run.
- `artifacts/meeting-validation/low-latency-results.json` — selected profile.
- `artifacts/meeting-validation/audio-results.json` — WASAPI / Silero / ASR hardware check.
- `artifacts/meeting-validation/samples.json` — public sample identifiers and references.

These generated artifacts and downloaded audio remain local and are excluded from Git. The validation code and this report are part of the project.
