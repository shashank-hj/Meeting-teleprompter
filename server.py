"""
Local Meeting Teleprompter - Web UI Server
Flask + SocketIO backend with live audio + file upload processing.
Phase 3+4: Smart triggers, conversation state, route-specific prompts, cancel, export.
"""
import os
import sys
import time
import json
import tempfile
import threading
import traceback
from datetime import datetime

from flask import Flask, render_template, jsonify, request, Response
from flask_socketio import SocketIO, emit

# ── Teleprompter engine imports ─────────────────────────────────────────────
from audio_capture import AudioCapture
from transcribe import Transcriber
from ollama_suggester import OllamaSuggester, ROUTE_PROMPTS
from knowledge_base import KnowledgeBase
from file_processor import extract_audio, SUPPORTED_ALL
from trigger_engine import TriggerEngine
from conversation_state import ConversationState
from web_search import search_and_fetch

# ── App setup ───────────────────────────────────────────────────────────────
app = Flask(__name__, template_folder="templates", static_folder="static")
app.config["SECRET_KEY"] = "teleprompter-local-secret"
socketio = SocketIO(app, cors_allowed_origins="*", async_mode="threading")

# ── Global engine state ─────────────────────────────────────────────────────
class Engine:
    def __init__(self):
        self.is_running = False
        self.is_initialized = False
        self.transcriber = None
        self.suggester = None
        self.capture = None
        self.kb = None
        self.trigger_engine = TriggerEngine(cooldown_seconds=15.0)
        self.conversation_state = ConversationState()

        self.transcript = []
        self.transcript_id = 0
        self.suggestions = []
        self.suggestion_id = 0
        self.start_time = time.time()

        self.stats = {
            "status": "idle",
            "whisper_model": "base.en",
            "llm_model": "gemma2:2b",
            "uptime": 0,
            "utterances_processed": 0,
            "suggestions_generated": 0,
        }

        self.suggestion_lock = threading.Lock()
        self.current_suggestion_query_id = 0
        self.is_generating = False
        self.generation_cancel = threading.Event()
        self.init_lock = threading.Lock()

engine = Engine()

def safe_emit(event, data):
    """Emit socket event safely with error handling."""
    try:
        socketio.emit(event, data)
    except Exception as e:
        print(f"[EMIT ERROR] {event}: {e}")

def emit_status(status):
    engine.stats["status"] = status
    safe_emit("status", {"status": status})
    print(f"[STATUS] {status}")

# ── Engine lifecycle ────────────────────────────────────────────────────────
def init_engine():
    """Initialize all components. Thread-safe, idempotent."""
    with engine.init_lock:
        if engine.is_initialized:
            return True

        try:
            emit_status("initializing")

            # Transcriber
            emit_status("loading_whisper")
            print("[INIT] Loading Whisper model...")
            engine.transcriber = Transcriber(model_size=engine.stats["whisper_model"])
            print("[INIT] Whisper loaded OK")

            # LLM Suggester
            print("[INIT] Connecting to Ollama...")
            engine.suggester = OllamaSuggester(model_name=engine.stats["llm_model"])
            print("[INIT] Ollama connected OK")

            # Knowledge Base
            print("[INIT] Initializing knowledge base...")
            engine.kb = KnowledgeBase()
            engine.kb.sync_folder("knowledge_base")
            print("[INIT] Knowledge base OK")

            engine.is_initialized = True
            engine.is_running = False
            emit_status("ready")
            print("[INIT] Core components initialized (audio capture deferred to start)")
            return True

        except Exception as e:
            print(f"[INIT ERROR] {e}")
            traceback.print_exc()
            emit_status("error")
            return False

def start_engine():
    """Start audio capture and begin processing loop."""
    try:
        if not engine.is_initialized:
            if not init_engine():
                return

        # Create fresh AudioCapture each time (PyAudio handles go stale)
        print("[ENGINE] Creating audio capture...")
        try:
            engine.capture = AudioCapture(rms_threshold=0.005, silence_seconds=1.0)
            engine.capture.start()
        except Exception as e:
            print(f"[ENGINE] Audio capture failed: {e}")
            traceback.print_exc()
            print("[ENGINE] Running in upload-only mode")
            engine.is_running = True
            engine.start_time = time.time()
            engine.stats["status"] = "upload_only"
            emit_status("upload_only")
            t2 = threading.Thread(target=_stats_loop, daemon=True)
            t2.start()
            return

        engine.is_running = True
        engine.start_time = time.time()
        engine.stats["status"] = "listening"
        emit_status("listening")
        print("[ENGINE] Audio capture started, listening...")

        # Background processing thread
        t = threading.Thread(target=_process_loop, daemon=True)
        t.start()

        # Stats updater
        t2 = threading.Thread(target=_stats_loop, daemon=True)
        t2.start()

    except Exception as e:
        print(f"[START ERROR] {e}")
        traceback.print_exc()
        emit_status("error")

def stop_engine():
    """Stop everything."""
    engine.is_running = False
    engine.generation_cancel.set()  # Cancel any in-flight generation
    if engine.capture:
        try:
            engine.capture.stop()
        except Exception as e:
            print(f"[STOP ERROR] {e}")
    engine.stats["status"] = "stopped"
    emit_status("stopped")
    print("[ENGINE] Stopped")

def _process_loop():
    """Main loop: pull utterances, transcribe, smart-trigger suggestions."""
    print("[PROCESS] Loop started")
    consecutive_errors = 0

    while engine.is_running:
        try:
            utt = engine.capture.get_utterance(timeout=0.1)
            if utt:
                consecutive_errors = 0
                speaker_tag, audio_data = utt
                engine.stats["utterances_processed"] += 1

                duration = len(audio_data) / 16000
                print(f"[PROCESS] Utterance received: [{speaker_tag}] {duration:.1f}s")

                # Transcribe
                timestamp = datetime.now().strftime("%H:%M:%S")
                safe_emit("transcribing", {
                    "speaker": speaker_tag,
                    "timestamp": timestamp
                })

                result = engine.transcriber.transcribe(audio_data)
                if isinstance(result, tuple):
                    text, confidence = result
                else:
                    text, confidence = result, 0.8

                if text and text.strip():
                    engine.transcript_id += 1
                    entry = {
                        "id": engine.transcript_id,
                        "speaker": speaker_tag,
                        "text": text.strip(),
                        "timestamp": timestamp,
                        "confidence": round(confidence, 2)
                    }
                    engine.transcript.append(entry)

                    if len(engine.transcript) > 50:
                        engine.transcript = engine.transcript[-50:]

                    # Update conversation state
                    engine.conversation_state.update(entry)

                    print(f"[PROCESS] Transcript: {text.strip()[:80]}... (conf={confidence:.2f})")
                    safe_emit("transcript", entry)

                    # Smart trigger: decide whether to generate
                    trigger_result = engine.trigger_engine.should_trigger(engine.transcript)
                    if trigger_result["trigger"]:
                        print(f"[PROCESS] Trigger: {trigger_result['reason']}")
                        _trigger_suggestion(trigger_reason=trigger_result["reason"])
                else:
                    print(f"[PROCESS] Empty transcription for {duration:.1f}s audio")
            else:
                time.sleep(0.01)

        except Exception as e:
            consecutive_errors += 1
            print(f"[PROCESS ERROR] {e}")
            if consecutive_errors > 10:
                print("[PROCESS] Too many consecutive errors, stopping")
                break
            time.sleep(0.2)

    print("[PROCESS] Loop ended")

def _trigger_suggestion(trigger_reason="manual", route="concise"):
    """Queue a suggestion generation request."""
    with engine.suggestion_lock:
        engine.current_suggestion_query_id += 1
        query_id = engine.current_suggestion_query_id
        history_snapshot = list(engine.transcript)
        engine.is_generating = True
        engine.generation_cancel.clear()

    safe_emit("generating", {"active": True, "reason": trigger_reason})

    t = threading.Thread(
        target=_generate_suggestion_thread,
        args=(query_id, history_snapshot, trigger_reason, route),
        daemon=True
    )
    t.start()

def _generate_suggestion_thread(query_id, history_snapshot, trigger_reason="manual", route="concise"):
    """Generate a suggestion using 3-layer retrieval: KB -> Web -> LLM general knowledge."""
    try:
        # Filter hallucinated transcript entries — don't send noise to LLM
        clean_snapshot = [
            e for e in history_snapshot
            if not Transcriber._is_hallucination(e.get("text", ""))
        ]
        if not clean_snapshot:
            print("[SUGGESTION] All transcript entries are hallucinations, skipping")
            with engine.suggestion_lock:
                if query_id == engine.current_suggestion_query_id:
                    engine.is_generating = False
            safe_emit("generating", {"active": False})
            return

        # Use conversation state for smarter search query
        search_query = engine.conversation_state.get_search_query(clean_snapshot)
        if not search_query:
            search_query = " ".join(e["text"] for e in clean_snapshot[-2:])

        # ── Layer 1: Local Knowledge Base ──────────────────────────────────
        context_chunks = []
        source_type = "knowledge_base"
        if search_query.strip() and engine.kb:
            context_chunks = engine.kb.search(search_query, limit=5)

        # ── Layer 2: Web Search (if KB empty) ─────────────────────────────
        if not context_chunks and search_query.strip():
            print(f"[3-LAYER] KB empty for '{search_query[:40]}...', trying web search...")
            safe_emit("status", {"status": "searching_web"})
            web_text, web_url, web_title = search_and_fetch(search_query)
            if web_text:
                context_chunks = [{"text": web_text, "source": web_title or web_url, "path": web_url}]
                source_type = "web_search"
                # Auto-cache into KB with 1-hour TTL
                if engine.kb:
                    engine.kb.add_web_cache(web_url, web_title, web_text, ttl_seconds=3600)
                print(f"[3-LAYER] Web search found: {web_title or web_url}")
            else:
                print(f"[3-LAYER] Web search returned nothing")
                source_type = "general_knowledge"
        elif not context_chunks:
            source_type = "general_knowledge"

        # ── Layer 3: LLM General Knowledge (fallback, no context) ─────────
        # source_type already set to "general_knowledge" if both KB and web failed

        # Build context for LLM using conversation state
        llm_context = engine.conversation_state.get_context_for_llm(clean_snapshot)

        text, sources_used = engine.suggester.generate_for_route(
            route,
            clean_snapshot,
            context_chunks,
            cancel_event=engine.generation_cancel,
            source_type=source_type
        )

        # Restore listening status after web search
        if engine.stats["status"] == "searching_web":
            safe_emit("status", {"status": "listening"})

        if engine.generation_cancel.is_set():
            print("[SUGGESTION] Cancelled")
            return

        with engine.suggestion_lock:
            if query_id == engine.current_suggestion_query_id:
                engine.suggestion_id += 1
                timestamp = datetime.now().strftime("%H:%M:%S")

                # Use sources from LLM response if available, else from KB
                if not sources_used:
                    sources_used = list(dict.fromkeys(
                        c.get("source", "") for c in context_chunks if c.get("source")
                    ))

                suggestion_entry = {
                    "id": engine.suggestion_id,
                    "text": text,
                    "timestamp": timestamp,
                    "sources": sources_used,
                    "route": route,
                    "trigger_reason": trigger_reason,
                    "source_type": source_type,
                }
                engine.suggestions.append(suggestion_entry)

                if len(engine.suggestions) > 20:
                    engine.suggestions = engine.suggestions[-20:]

                engine.stats["suggestions_generated"] += 1
                engine.is_generating = False

                safe_emit("generating", {"active": False})
                safe_emit("suggestion", suggestion_entry)

    except Exception as e:
        print(f"[SUGGESTION ERROR] {e}")
        traceback.print_exc()
        with engine.suggestion_lock:
            engine.is_generating = False
        safe_emit("generating", {"active": False})

def _stats_loop():
    """Periodically emit stats."""
    while engine.is_running:
        engine.stats["uptime"] = int(time.time() - engine.start_time)
        safe_emit("stats", engine.stats)
        time.sleep(2)

# ── Flask routes ────────────────────────────────────────────────────────────
@app.route("/")
def index():
    return render_template("index.html")

@app.route("/api/status")
def api_status():
    return jsonify(engine.stats)

@app.route("/api/transcript")
def api_transcript():
    return jsonify(engine.transcript[-50:])

@app.route("/api/suggestions")
def api_suggestions():
    return jsonify(engine.suggestions[-20:])

@app.route("/api/start", methods=["POST"])
def api_start():
    if not engine.is_running:
        threading.Thread(target=start_engine, daemon=True).start()
    return jsonify({"ok": True})

@app.route("/api/stop", methods=["POST"])
def api_stop():
    stop_engine()
    return jsonify({"ok": True})

@app.route("/api/sources", methods=["GET"])
def api_get_sources():
    if not engine.kb:
        return jsonify([])
    try:
        conn = engine.kb._get_db_connection()
        c = conn.cursor()
        c.execute("SELECT id, source_type, source_path, title, last_updated FROM sources ORDER BY last_updated DESC")
        rows = [dict(r) for r in c.fetchall()]
        conn.close()
        return jsonify(rows)
    except Exception as e:
        return jsonify({"error": str(e)}), 500

@app.route("/api/sources", methods=["POST"])
def api_add_source():
    data = request.get_json()
    url = data.get("url", "").strip()
    if not url:
        return jsonify({"error": "No URL provided"}), 400
    if not engine.kb:
        return jsonify({"error": "Knowledge base not initialized"}), 500
    try:
        engine.kb.add_url(url)
        return jsonify({"ok": True, "message": f"Indexed: {url}"})
    except Exception as e:
        return jsonify({"error": str(e)}), 500

@app.route("/api/sources/<int:source_id>", methods=["DELETE"])
def api_delete_source(source_id):
    if not engine.kb:
        return jsonify({"error": "Knowledge base not initialized"}), 500
    try:
        conn = engine.kb._get_db_connection()
        c = conn.cursor()
        c.execute("SELECT source_path FROM sources WHERE id = ?", (source_id,))
        row = c.fetchone()
        conn.close()
        if row:
            engine.kb.remove_source(row["source_path"])
        return jsonify({"ok": True})
    except Exception as e:
        return jsonify({"error": str(e)}), 500

@app.route("/api/sources/refresh", methods=["POST"])
def api_refresh_sources():
    if not engine.kb:
        return jsonify({"error": "Knowledge base not initialized"}), 500
    try:
        engine.kb.sync_folder("knowledge_base")
        return jsonify({"ok": True, "message": "Sources refreshed"})
    except Exception as e:
        return jsonify({"error": str(e)}), 500

@app.route("/api/upload", methods=["POST"])
def api_upload_file():
    if "file" not in request.files:
        return jsonify({"error": "No file provided"}), 400

    file = request.files["file"]
    if not file.filename:
        return jsonify({"error": "No file selected"}), 400

    ext = os.path.splitext(file.filename)[1].lower()
    if ext not in SUPPORTED_ALL:
        return jsonify({"error": f"Unsupported format: {ext}"}), 400

    upload_dir = os.path.join(tempfile.gettempdir(), "teleprompter_uploads")
    os.makedirs(upload_dir, exist_ok=True)
    save_path = os.path.join(upload_dir, file.filename)
    file.save(save_path)

    # Ensure transcriber is ready
    if not engine.transcriber:
        print("[UPLOAD] Waiting for transcriber to initialize...")
        safe_emit("upload_progress", {
            "filename": file.filename,
            "status": "transcribing",
            "progress": 0,
            "message": "Initializing transcription engine..."
        })
        if not engine.is_initialized:
            threading.Thread(target=init_engine, daemon=True).start()
        for _ in range(120):
            if engine.transcriber:
                break
            time.sleep(1)

    if not engine.transcriber:
        return jsonify({"error": "Transcriber not ready"}), 503

    threading.Thread(target=_process_upload, args=(save_path, file.filename), daemon=True).start()
    return jsonify({"ok": True, "message": f"Processing: {file.filename}"})

def _process_upload(file_path, original_name):
    try:
        safe_emit("upload_progress", {
            "filename": original_name,
            "status": "extracting",
            "progress": 0,
            "message": "Extracting audio from file..."
        })

        chunks, total_duration = extract_audio(file_path)
        total = len(chunks)

        safe_emit("upload_progress", {
            "filename": original_name,
            "status": "transcribing",
            "progress": 10,
            "message": f"Audio extracted ({total_duration:.1f}s). Transcribing {total} chunk(s)..."
        })

        all_text = []
        for i, (speaker, audio_chunk) in enumerate(chunks):
            pct = int(10 + (i / total) * 85)
            safe_emit("upload_progress", {
                "filename": original_name,
                "status": "transcribing",
                "progress": pct,
                "message": f"Transcribing chunk {i+1}/{total}..."
            })

            result = engine.transcriber.transcribe(audio_chunk)
            if isinstance(result, tuple):
                text, confidence = result
            else:
                text, confidence = result, 0.8

            if text and text.strip():
                engine.transcript_id += 1
                entry = {
                    "id": engine.transcript_id,
                    "speaker": "remote",
                    "text": text.strip(),
                    "timestamp": datetime.now().strftime("%H:%M:%S"),
                    "source": original_name,
                    "confidence": round(confidence, 2)
                }
                engine.transcript.append(entry)
                engine.conversation_state.update(entry)
                all_text.append(text.strip())
                safe_emit("transcript", entry)

        if all_text:
            safe_emit("upload_progress", {
                "filename": original_name,
                "status": "suggesting",
                "progress": 95,
                "message": "Generating suggestions..."
            })
            _trigger_suggestion(trigger_reason="upload")

        try:
            os.remove(file_path)
        except Exception:
            pass

        safe_emit("upload_progress", {
            "filename": original_name,
            "status": "done",
            "progress": 100,
            "message": f"Done! Transcribed {len(all_text)} segment(s)."
        })

        engine.stats["utterances_processed"] += len(all_text)

    except Exception as e:
        print(f"[UPLOAD ERROR] {e}")
        traceback.print_exc()
        safe_emit("upload_progress", {
            "filename": original_name,
            "status": "error",
            "progress": 0,
            "message": f"Error: {e}"
        })

@app.route("/api/export", methods=["GET"])
def api_export_session():
    """Export transcript and suggestions as a Markdown file."""
    now = datetime.now()
    date_str = now.strftime("%Y-%m-%d %H:%M")

    lines = [
        f"# Meeting Transcript — {date_str}\n",
        f"**Duration:** {int(engine.stats['uptime'] // 60)}m {engine.stats['uptime'] % 60}s  ",
        f"**Utterances:** {engine.stats['utterances_processed']}  ",
        f"**Suggestions:** {engine.stats['suggestions_generated']}\n",
        "---\n",
        "## Transcript\n",
    ]

    for e in engine.transcript:
        speaker = "You" if e.get("speaker") == "local" else "Remote"
        source = f" *(from {e['source']})*" if e.get("source") else ""
        conf = e.get("confidence", 0)
        conf_badge = "🟢" if conf > 0.7 else "🟡" if conf > 0.4 else "🔴"
        lines.append(f"**[{e.get('timestamp', '?')}] {speaker}** {conf_badge}{source}")
        lines.append(f"> {e.get('text', '')}\n")

    if engine.suggestions:
        lines.append("---\n")
        lines.append("## Suggestions\n")
        for s in engine.suggestions:
            route = s.get("route", "concise")
            reason = s.get("trigger_reason", "")
            sources = ", ".join(s.get("sources", []))
            lines.append(f"### [{s.get('timestamp', '?')}] Route: {route} (trigger: {reason})")
            if sources:
                lines.append(f"*Sources: {sources}*")
            lines.append(f"\n{s.get('text', '')}\n")

    md_content = "\n".join(lines)
    return Response(
        md_content,
        mimetype="text/markdown",
        headers={"Content-Disposition": f"attachment; filename=meeting_{now.strftime('%Y%m%d_%H%M')}.md"}
    )

# ── SocketIO events ─────────────────────────────────────────────────────────
@socketio.on("connect")
def handle_connect():
    print("[WS] Client connected")
    safe_emit("status", {"status": engine.stats["status"]})
    safe_emit("stats", engine.stats)
    for entry in engine.transcript[-30:]:
        safe_emit("transcript", entry)
    for sug in engine.suggestions[-10:]:
        safe_emit("suggestion", sug)

@socketio.on("disconnect")
def handle_disconnect():
    print("[WS] Client disconnected")

@socketio.on("request_start")
def handle_request_start():
    print("[WS] request_start received")
    engine.generation_cancel.clear()
    engine.trigger_engine.reset()
    engine.conversation_state.reset()
    if not engine.is_running:
        threading.Thread(target=start_engine, daemon=True).start()

@socketio.on("request_stop")
def handle_request_stop():
    print("[WS] request_stop received")
    stop_engine()

@socketio.on("request_cancel_generation")
def handle_cancel_generation():
    print("[WS] request_cancel_generation received")
    engine.generation_cancel.set()
    with engine.suggestion_lock:
        engine.is_generating = False
    safe_emit("generating", {"active": False})

@socketio.on("request_route")
def handle_request_route(data):
    route = data.get("route", "concise")
    suggestion_id = data.get("suggestion_id", 0)

    # Cancel any in-flight generation
    engine.generation_cancel.set()
    time.sleep(0.1)
    engine.generation_cancel.clear()

    with engine.suggestion_lock:
        engine.current_suggestion_query_id += 1
        query_id = engine.current_suggestion_query_id
        history_snapshot = list(engine.transcript)
        engine.is_generating = True

    safe_emit("generating", {"active": True, "reason": f"route:{route}"})

    def gen():
        try:
            # Filter hallucinated entries
            clean_snapshot = [
                e for e in history_snapshot
                if not Transcriber._is_hallucination(e.get("text", ""))
            ]
            if not clean_snapshot:
                with engine.suggestion_lock:
                    engine.is_generating = False
                safe_emit("generating", {"active": False})
                return

            search_query = engine.conversation_state.get_search_query(clean_snapshot)
            if not search_query:
                search_query = " ".join(e["text"] for e in clean_snapshot[-2:])

            # Layer 1: KB
            context_chunks = engine.kb.search(search_query, limit=5) if engine.kb and search_query.strip() else []
            source_type = "knowledge_base"

            # Layer 2: Web search if KB empty
            if not context_chunks and search_query.strip():
                web_text, web_url, web_title = search_and_fetch(search_query)
                if web_text:
                    context_chunks = [{"text": web_text, "source": web_title or web_url, "path": web_url}]
                    source_type = "web_search"
                    if engine.kb:
                        engine.kb.add_web_cache(web_url, web_title, web_text, ttl_seconds=3600)
                else:
                    source_type = "general_knowledge"

            # Layer 3: LLM general knowledge (no context)
            text, sources_used = engine.suggester.generate_for_route(
                route,
                clean_snapshot,
                context_chunks,
                cancel_event=engine.generation_cancel,
                source_type=source_type
            )

            if engine.generation_cancel.is_set():
                return

            with engine.suggestion_lock:
                if query_id == engine.current_suggestion_query_id:
                    engine.suggestion_id += 1
                    timestamp = datetime.now().strftime("%H:%M:%S")

                    if not sources_used:
                        sources_used = list(dict.fromkeys(
                            c.get("source", "") for c in context_chunks if c.get("source")
                        ))

                    suggestion_entry = {
                        "id": engine.suggestion_id,
                        "text": text,
                        "timestamp": timestamp,
                        "sources": sources_used,
                        "route": route,
                        "trigger_reason": "route_request",
                        "source_type": source_type,
                    }
                    engine.suggestions.append(suggestion_entry)
                    engine.stats["suggestions_generated"] += 1
                    engine.is_generating = False

                    safe_emit("generating", {"active": False})
                    safe_emit("suggestion", suggestion_entry)

        except Exception as e:
            print(f"[ROUTE ERROR] {e}")
            traceback.print_exc()
            with engine.suggestion_lock:
                engine.is_generating = False
            safe_emit("generating", {"active": False})

    threading.Thread(target=gen, daemon=True).start()

# ── Main ────────────────────────────────────────────────────────────────────
if __name__ == "__main__":
    print("\n" + "=" * 60)
    print("  LOCAL MEETING TELEPROMPTER - Web UI")
    print("  Phase 3+4: Smart Triggers, Route Prompts, Cancel, Export")
    print("=" * 60)
    print("  Open browser: http://localhost:5000")
    print("  Press Ctrl+C to stop\n")

    # Pre-initialize models in background
    threading.Thread(target=init_engine, daemon=True).start()

    socketio.run(app, host="0.0.0.0", port=5000, debug=False, allow_unsafe_werkzeug=True)
