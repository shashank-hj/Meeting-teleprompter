# Project status — 20 November 2026

The native Windows MVP is implemented. This review completed missing ingestion, indexing, transcript, cancellation, resource-control, and desktop interaction work. The proposal is **not fully accepted for release**: sustained live-call performance, additional model/runtime evaluations, visual verification in this session, and publisher signing remain open.

The original proposal is a design and research record. Optional candidates, future experiments, and explicitly deferred decisions are distinguished below from the shipping baseline.

Final verification: the release compiles successfully, all **81 acceptance checks pass**, and the actual worker extracted from the finished MSIX passes the same suite including installed Edge. The microphone-off WASAPI/Silero/ASR check passed. Package contents were verified to include the Windows worker/browser driver and exclude model weights and other-platform Node binaries. The unsigned package is `dist/MeetingTeleprompter.msix` (238,361,463 bytes).

## Proposal coverage

| Proposal requirement | Current implementation | Verification / limit |
| --- | --- | --- |
| Native Windows, C#/.NET 10, WinUI 3 | App, Core, and separate worker host | Release builds successfully; Windows x64 is the shipping target |
| System audio and local microphone | WASAPI loopback and microphone, separate channels and device selectors | Component checks; public-sample loopback check; microphone was kept disabled during this review |
| VAD and continuous transcription | Silero ONNX, bounded audio queues, partial/final Whisper windows | Real Whisper inference plus deterministic segmentation checks; optional energy detector fallback |
| Transcript stabilization | Overlap deduplication, channel identities, final corrections replacing prior canonical text | Automated checks; corrected finals do not repeatedly trigger assistance |
| Bounded conversation state | Recent utterances, topics, entities, decisions, open questions, bounded summary and segment lengths | Core implementation and transcript checks |
| Proactive assistance without a question | Topic, question, claim, decision/risk triggers, novelty and cooldown controls | Trigger and pipeline checks |
| Cancel obsolete work | Manual requests supersede proactive work; topic shifts cancel stale drafts even during cooldown | Pipeline regression checks; answers to an outstanding meeting question remain focused until a newer question or cancellation |
| Short local suggestions | One generation at a time, 100-token proactive output budget, explicit longer routes | Generation priorities and deadlines cross the versioned named-pipe boundary |
| Hybrid local retrieval | FTS5, versioned EmbeddingGemma vectors, sqlite-vec or managed cosine, fusion and source diversity | Real semantic retrieval and automated lexical/fusion/cascade checks |
| TXT, Markdown, HTML, DOCX, PDF | Local parsers, heading/paragraph sections, PDF page locations, content-derived chunk IDs | Ingestion checks; text PDFs supported; scanned PDF OCR remains outside the baseline |
| Incremental indexes | Transactional chunk/FTS changes; unchanged chunk embeddings survive insertions and explicit URL refresh; title changes invalidate embedding inputs | Incremental, in-place update, and deletion behavior covered by tests |
| Watched folders | Debounce, interrupted-write retries, bounded pending changes, file renames/deletions, directory reconciliation | Real FileSystemWatcher checks; overflow is reported and requires explicit refresh |
| Immediate URL import, explicit refresh | Public HTTP fetch, saved snapshot, original supplied URL, title, fetch time and failure status | Tests prove reads do not fetch again and failed refresh retains the saved copy |
| JavaScript page fallback | Short-lived installed Edge through Playwright when ordinary HTML lacks readable text | Real Edge test; same-origin GET resources only, DNS-pinned safe fetching, request/byte/time limits, no downloads or service workers; third-party CDNs/authenticated apps may need an exported copy |
| Citations and freshness | Cards retain the actual evidence snapshot; citation opens its passage, heading/page/chunk and original location | Data path covered by tests; desktop visual inspection remains open for this session |
| User-selectable routes | Answer, evidence, explanation, claim check, follow-up, cancellation, per-card actions | Implemented in WinUI; typed questions also stream drafts |
| Session controls and overlay | Listening indicator, start/stop, optional always-on-top, live meters, ephemeral transcript, explicit export | XAML build; hardware path checked separately; no conference product integration needed |
| Privacy | Numeric-loopback-only inference, private named pipes, opt-in transcript export, no routine transcript logs, source deletion cascades | Transport, URL-safety, and deletion checks; no cloud inference |
| CPU and RAM control | ASR starts first; generation loads on demand; optional memory conservation reserves an estimated 2 GiB and falls back to keyword search/source evidence | Low-memory fallback regression and measured resource report; estimates are not an OS-enforced memory quota |
| Setup/device benchmark | **Check device** uses a user-selected 1–10 s, 16 kHz mono WAV without microphone capture; reports speed, memory, citation fixture and profile advice | Shared benchmark engine and CLI; this is a sample check, not a live-call certification |
| Model installation and packaging | Checksummed runtime/assets installer accepting an explicit GGUF path; separate model assets; self-contained x64 MSIX script | Trusted publisher certificate still required for ordinary MSIX installation |

## Changes completed in this review

- Preserved embeddings for unchanged chunks and repaired FTS updates for chunks revised in place.
- Retained headings in extraction, search, citations, and embedding inputs; kept PDF page metadata.
- Added secure JavaScript rendering and persisted URL import/refresh failures without losing saved evidence.
- Added final-transcript correction handling, stale-topic cancellation, and worker deadline/priority propagation.
- Added per-card routes, cited-passage viewing, typed-answer drafts, and a device-check control.
- Added memory-aware model startup and an evidence fallback, based on actual low-memory measurements.
- Added repeatable multi-profile evaluation, native CPU/working-set sampling, available-memory measurement, domain-term scoring for annotated corpora, and backend-reported generation throughput.
- Corrected watcher directory notifications and bounded its pending changes; added real filesystem checks.
- Versioned source parsers so existing unchanged files receive upgraded extraction; startup reconciles configured local folders without fetching saved URLs.
- Corrected worker output discovery for packaging and excluded other platforms' browser runtime binaries.

## Current measured profile comparison

Same local AMI corpus: 12 public excerpts, 58.45 seconds, 157 reference words. Each profile used the installed Gemma 2 2B generator, with concurrent generation during the final four speech samples. These are request latencies after audio collection; startup and meeting audio buffering are additional.

| Speech profile | Median ASR | Maximum ASR | Word error rate | Aggregate real-time factor | Sampled native peak RAM |
| --- | ---: | ---: | ---: | ---: | ---: |
| base.en, context 512 | 2.92 s | 3.79 s | 22.3% | 0.58 | 3.52 GiB |
| base.en, full context | 7.27 s | 10.68 s | 20.4% | 1.61 | 3.55 GiB |
| small.en Q5, context 512 | 11.11 s | 17.19 s | 22.9% | 2.34 | 3.61 GiB |

The low-latency base.en profile remains the appropriate installed choice. Every generation fixture supplied the correct release date and owner with a valid citation. Its first concurrent answer took 12.58 seconds, with 9.93 seconds to first token; subsequent identical prompts benefited from caching. Backend throughput averaged approximately 6.7 tokens/s in that profile.

The smallest sampled free-memory value was only 0.30 GiB with all model servers resident. This workload therefore **did not meet the proposal's 2–3 GB headroom goal**. Memory-aware fallback is enabled by default; close unused applications or use a smaller generator when running the full inference stack. No claim is made that the combined two-channel/conference-client workload has passed sustained acceptance.

A later single-clip run of the shared device-check engine from the packaged worker took 5.10 seconds for ASR and 26.90 seconds for its generated answer, exceeding the latency targets under that session load. The citation fixture passed. This is additional evidence of load sensitivity, not a replacement for the comparison above or a successful live-call certification.

Raw reports are local, ignored artifacts under `artifacts/profile-evaluation/20261120-141822/`. Historical measurements are retained in [VALIDATION.md](VALIDATION.md).

## Acceptance still requiring external evidence or credentials

1. **Sustained real calls and several CPUs.** Validate Meet, Zoom, and Teams with representative accents, cross-talk, compression, noise, names/acronyms, both audio channels, UI responsiveness, thermals, and a full-system RAM margin. This machine and the small public corpus cannot establish those outcomes. Add domain terms to corpus entries to measure their recognition separately.
2. **Exact recommended generation candidates.** Gemma 4 E2B/E4B and Qwen3.5 2B have not been installed or compared here. The functioning installed selection remains Gemma 2 2B. Accepted licenses, compatible weights, and sufficient RAM are prerequisites to those comparisons.
3. **Research addendum experiments.** sherpa-onnx, Qwen ASR, Foundry Local and OpenVINO comparisons remain experiments; no claim is made that adapters for these runtimes have been implemented or validated. Existing narrow interfaces keep those additions separate from the baseline.
4. **Kompact gate.** Written Windows/offline/API/licensing/privacy/redistribution answers and a vendor runtime are still required before an adapter or benchmark. See [the evaluation checklist](RUNTIME-EVALUATION.md). No vendor was contacted.
5. **Visual desktop verification.** The computer-use runtime failed to initialize in this session with `failed to write kernel assets: The system cannot find the path specified`. A clean XAML build does not replace visual testing of dialogs, card layout, and all interactions.
6. **Publisher signing.** The generated MSIX is unsigned. A trusted certificate matching the manifest publisher is needed for installation/distribution. Certificate trust and installation were not changed.
7. **Dependency audit.** NuGet's vulnerability feed was unreachable during package restore, including an escalated retry. Package creation can be checked, but that audit is not claimed as passed.

Live web search, authenticated websites, individual remote-speaker identification, scanned-document OCR, automatic URL refresh, and macOS/Linux are outside the stated MVP baseline. They are not silently included as completed features.

## Reproduce

```powershell
.\scripts\build.ps1
.\src\MeetingTeleprompter.Host\bin\Release\net10.0\MeetingTeleprompter.Host.exe --self-test --with-browser
.\scripts\evaluate-profiles.ps1 -IncludeSmallSpeechModel
.\scripts\package.ps1
.\scripts\verify-package.ps1 -RunChecks -WithBrowser
.\scripts\start.ps1
```

PowerShell execution policy must permit the repository scripts. Additional candidate settings files can be passed to `evaluate-profiles.ps1 -AdditionalSettingsPath`; it keeps evaluation indexes separate and does not change application settings. Candidate binaries/weights are supplied separately.

The browser implementation follows the primary [Playwright network routing](https://playwright.dev/dotnet/docs/network) and [installed browser guidance](https://playwright.dev/dotnet/docs/browsers).
