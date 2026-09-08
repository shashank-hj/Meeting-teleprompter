# Meeting Tips for Teleprompter Project

## Database Selection
For a local meeting teleprompter running on CPU-only hardware:
- **SQLite with FTS5** - Excellent for keyword search, minimal overhead
- **ChromaDB with sqlite-vec** - Vector similarity search with SQLite backend
- Avoid external vector databases (Weaviate, Pinecone) - too heavy for 16GB RAM

## Model Choices
- **ASR**: faster-whisper (tiny.en or base.en) - optimized for CPU
- **LLM**: gemma2:2b via Ollama - fits in ~1.6GB, good instruction following
- **Embeddings**: EmbeddingGemma 300M or all-MiniLM-L6-v2

## Audio Capture
- Use **WASAPI loopback** for system audio (remote speakers)
- Use **standard microphone capture** for local speaker
- Separate channels avoid needing speaker diarization

## Scheduling Priority
1. Audio capture (never blocks)
2. VAD + ASR transcription
3. User-requested retrieval/generation
4. Proactive retrieval
5. Proactive generation
6. Background indexing

## Memory Budget (16GB baseline)
- Windows + meeting client: 5-8 GB
- Application + UI: < 500 MB
- Whisper ASR: 400-900 MB
- LLM + context: 2-4 GB
- Embeddings + DB: 300-700 MB
- Safety margin: 2-3 GB