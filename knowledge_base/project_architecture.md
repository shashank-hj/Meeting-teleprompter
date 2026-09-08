# Project Architecture and Technical Decisions

## Teleprompter Project Overview

### Product Goal
A Windows desktop application that listens to live Google Meet, Zoom, and Microsoft Teams calls, follows the conversation in real time, retrieves relevant material from user-provided sources, and proactively presents useful information or suggested responses.

### Technical Stack (Current Prototype)
- **Backend**: Python, Flask, Flask-SocketIO
- **Frontend**: Single-file HTML/JS web UI (dark theme)
- **ASR**: faster-whisper (base.en model) — Whisper CTranslate2 implementation
- **LLM**: gemma2:2b via Ollama — local inference
- **Knowledge Base**: SQLite FTS5 + ChromaDB for hybrid search (lexical + vector)
- **Audio**: WASAPI loopback (remote speakers) + microphone capture (local speaker)
- **Embeddings**: ChromaDB default (all-MiniLM-L6-v2)

### Target Architecture (Production)
- **Language**: C# and .NET 10
- **Desktop Framework**: WinUI 3 with Windows App SDK 2.4
- **ASR**: whisper.cpp with Whisper base.en
- **LLM**: Gemma 4 E2B Instruct Q4 GGUF
- **Embeddings**: EmbeddingGemma 300M
- **VAD**: Silero VAD via ONNX Runtime
- **Vector DB**: sqlite-vec (or ChromaDB)
- **Isolation**: Separate worker processes communicating via named pipes

### Memory Budget (16GB baseline)
- Windows + meeting client: 5-8 GB
- Application + UI: Under 500 MB
- Whisper ASR: 400-900 MB
- LLM (Gemma 4 E2B) + context: 2-4 GB
- Embeddings, database, retrieval: 300-700 MB
- Safety margin: 2-3 GB

### Scheduling Priority (from proposal)
1. Audio capture (never blocks)
2. VAD + ASR transcription
3. User-requested retrieval and generation
4. Proactive retrieval
5. Proactive generation
6. File indexing and URL refresh

## Key Technical Decisions

### Why Local-First?
- Meeting audio and transcripts stay on the computer
- Document content and embeddings stay on the computer
- Model inference stays on the computer
- Only URLs explicitly supplied by the user are fetched
- Privacy boundary: local by default, network only when explicitly needed

### Why WASAPI Loopback?
- Captures system audio without separate integrations with Meet, Zoom, or Teams
- Loopback records whatever the speakers play — works with any conferencing app
- Combined with microphone capture, separates local and remote speakers

### Why SQLite + ChromaDB?
- SQLite FTS5: fast keyword search for exact terms, acronyms, identifiers
- ChromaDB: vector similarity for semantic search (finds related concepts)
- Hybrid search combines both for better retrieval quality
- Both run locally with minimal resource usage

### CPU-Only Constraint
- No dedicated GPU assumed (integrated GPU or NPU optional later)
- Models must be small enough to fit in available RAM alongside conferencing app
- Inference must not block audio capture or transcription
- Benchmarks must be run on actual hardware during setup

## Performance Targets
- Partial transcript updates: 1-4 seconds behind speech
- Retrieval results: 1-3 seconds after topic/question trigger
- Short generated suggestions: 4-15 seconds typical latency

## Proactive Trigger Engine
The system should generate suggestions when it detects:
- Direct or implied questions from any speaker
- Topic and named-entity changes
- Claims that may benefit from evidence
- Numbers, dates, product names, or organizations
- Decisions, objections, risks, and unresolved action items
- Semantic similarity between conversation and indexed content

Triggers should use cooldowns, deduplication, and novelty thresholds to avoid spam.
