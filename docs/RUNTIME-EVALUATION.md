# Runtime evaluation gate

The production baseline uses whisper.cpp, llama.cpp, local EmbeddingGemma, ONNX Silero and SQLite. A candidate must pass the same end-to-end workload before adoption. This checklist is ready for a vendor trial or an installed alternative; it does not imply that a trial or adapter is already available.

## Evidence required from a proprietary vendor

Obtain written answers and artifacts for:

- Windows 10/11 x64 support, CPU instruction requirements, named laptop CPUs and 16 GB configurations.
- Offline installation, activation, update and expiration behavior; network destinations and telemetry defaults.
- Stable versioned API/SDK, streaming response and incremental ASR semantics, deadlines, cancellation and restart behavior.
- Runtime and model redistribution rights, model licenses, installer/update size and unattended installation.
- Signed binaries, checksum verification, security maintenance and vulnerability response.
- Simultaneous ASR, embedding and small-LLM working sets, context limits, and actual laptop benchmarks.

No vendor outreach has been sent from this project review. The Kompact adapter remains gated on these answers and a usable trial runtime, as specified in the proposal.

## Comparison procedure

1. Keep each provider behind the applicable `ITranscriber`, `IEmbedder`, `IRetriever`, or `IGenerator` interface. Preserve loopback transport, priorities, request deadlines and cancellation.
2. Supply representative local WAV files and reference text in the corpus manifest. Entries accept `Id`, `Meeting`, `Path`, `Reference`, `Row` and optional `DomainTerms`.
3. Evaluate speech in isolation and concurrently with generation. Record WER, domain-term accuracy, partial/final latency, real-time factor, CPU and simultaneous RAM. The existing harness measures final request latency; live partial stability needs a streaming recording run.
4. Use several independent grounded-generation prompts. Record citation correctness and factual entailment separately, instruction following, time to first token, tokens/s, full completion, cancellation and peak RAM. Repeated identical prompts alone overstate prompt-cache performance.
5. Run the whole pipeline with each conference client active, both channels, thermal load and ordinary background applications. Include UI latency, call quality, device changes, worker crashes, stale-work cancellation and source ingestion while idle.
6. Require local inference and the proposal's RAM/latency targets. Record a capability as unvalidated when the model, SDK, device or license is unavailable; do not substitute a passing fake endpoint for actual model evidence.

`scripts/evaluate-profiles.ps1` compares installed Whisper settings/models and accepts extra native llama/Whisper settings profiles. Different runtime families still require their own adapters; the script does not manufacture those adapters.
