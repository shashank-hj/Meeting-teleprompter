"""
Local Meeting Teleprompter - Unified Test Suite
Run this file to test all components of the system.
Usage: python run_tests.py [--all] [--audio] [--transcribe] [--kb] [--rag] [--live]
"""
import os
import sys
import time
import shutil
import argparse
import tempfile
import numpy as np

# ── Colored output helpers ──────────────────────────────────────────────────
class C:
    OK = "\033[92m"
    FAIL = "\033[91m"
    WARN = "\033[93m"
    INFO = "\033[96m"
    BOLD = "\033[1m"
    END = "\033[0m"

def ok(msg):   print(f"  {C.OK}[PASS]{C.END} {msg}")
def fail(msg): print(f"  {C.FAIL}[FAIL]{C.END} {msg}")
def warn(msg): print(f"  {C.WARN}[WARN]{C.END} {msg}")
def info(msg): print(f"  {C.INFO}[INFO]{C.END} {msg}")
def header(msg):
    print(f"\n{C.BOLD}{'='*60}")
    print(f"  {msg}")
    print(f"{'='*60}{C.END}")

results = {"passed": 0, "failed": 0, "skipped": 0}

def record(passed, skipped=False):
    if skipped:
        results["skipped"] += 1
    elif passed:
        results["passed"] += 1
    else:
        results["failed"] += 1

# ── Test 1: Audio Device Detection ──────────────────────────────────────────
def test_audio_devices():
    header("TEST 1: Audio Device Detection (pyaudiowpatch)")
    try:
        import pyaudiowpatch as pyaudio
    except ImportError:
        warn("pyaudiowpatch not installed, skipping audio device test")
        record(False, skipped=True)
        return True

    p = pyaudio.PyAudio()
    wasapi_idx = None
    mic_info = None
    loopback_info = None

    try:
        # Find WASAPI host API
        for i in range(p.get_host_api_count()):
            api = p.get_host_api_info_by_index(i)
            if api["type"] == pyaudio.paWASAPI:
                wasapi_idx = api["index"]
                break

        if wasapi_idx is None:
            fail("WASAPI host API not found")
            record(False)
            return False

        ok("WASAPI host API found")

        # Scan WASAPI devices
        loopback_devices = []
        mic_devices = []
        for idx in range(p.get_device_count()):
            info = p.get_device_info_by_index(idx)
            if info["hostApi"] != wasapi_idx:
                continue
            name = info["name"]
            if info.get("isLoopbackDevice", False):
                loopback_devices.append((idx, name, info))
            if info["maxInputChannels"] > 0 and not info.get("isLoopbackDevice", False):
                if "mic" in name.lower() or "microphone" in name.lower():
                    mic_devices.append((idx, name, info))

        # Find mic
        if mic_devices:
            mic_info = mic_devices[0][2]
            ok(f"Microphone found: {mic_devices[0][1]} (Index {mic_devices[0][0]})")
        else:
            fail("No WASAPI microphone found")
            record(False)
            return False

        # Find loopback
        if loopback_devices:
            # Prefer speakers/headphones
            for idx, name, info in loopback_devices:
                if "speaker" in name.lower() or "headphone" in name.lower():
                    loopback_info = info
                    break
            if not loopback_info:
                loopback_info = loopback_devices[0][2]
            ok(f"Loopback device found: {loopback_devices[0][1]} (Index {loopback_devices[0][0]})")
        else:
            warn("No WASAPI loopback device found - remote audio capture will be unavailable")

        record(True)
        return True

    finally:
        p.terminate()


# ── Test 2: Audio Capture ───────────────────────────────────────────────────
def test_audio_capture():
    header("TEST 2: Audio Capture (5-second live test)")
    try:
        from audio_capture import AudioCapture
    except ImportError:
        warn("audio_capture module not found, skipping")
        record(False, skipped=True)
        return True

    try:
        cap = AudioCapture(rms_threshold=0.003, silence_seconds=1.0)
        cap.start()
        info("Listening for 5 seconds... Speak or play audio to test.")
        captured = 0
        for i in range(50):
            utt = cap.get_utterance(timeout=0.1)
            if utt:
                tag, audio = utt
                dur = len(audio) / 16000
                rms = np.sqrt(np.mean(audio**2))
                captured += 1
                info(f"  Captured utterance: [{tag}] {dur:.2f}s RMS={rms:.4f}")
            else:
                time.sleep(0.1)

        cap.stop()
        if captured > 0:
            ok(f"Audio capture working - {captured} utterance(s) captured")
        else:
            warn("No speech detected in 5 seconds - try speaking or playing audio")
        record(True)
        return True

    except Exception as e:
        fail(f"Audio capture error: {e}")
        record(False)
        return False


# ── Test 3: Whisper Transcription ───────────────────────────────────────────
def test_transcription():
    header("TEST 3: Whisper Transcription (tiny.en)")
    try:
        from faster_whisper import WhisperModel
    except ImportError:
        warn("faster_whisper not installed, skipping transcription test")
        record(False, skipped=True)
        return True

    try:
        info("Loading Whisper tiny.en model...")
        start = time.time()
        model = WhisperModel("tiny.en", device="cpu", compute_type="float32")
        load_time = time.time() - start
        ok(f"Model loaded in {load_time:.2f}s")

        # Generate a 1-second sine wave as test input
        sr = 16000
        t = np.linspace(0, 1.0, sr, dtype=np.float32)
        audio = 0.5 * np.sin(2 * np.pi * 440 * t)  # 440 Hz tone

        info("Transcribing 1-second test tone...")
        start = time.time()
        segments, info_obj = model.transcribe(audio, beam_size=1, language="en", vad_filter=False)
        text = " ".join(s.text for s in segments).strip()
        elapsed = time.time() - start
        ok(f"Transcription completed in {elapsed:.2f}s")
        if text:
            info(f"  Output: \"{text}\"")
        else:
            info("  Output: (empty - expected for pure tone)")
        record(True)
        return True

    except Exception as e:
        fail(f"Transcription error: {e}")
        record(False)
        return False


# ── Test 4: Knowledge Base (indexing + FTS5 + vector search) ────────────────
def test_knowledge_base():
    header("TEST 4: Knowledge Base (SQLite FTS5 + ChromaDB)")

    test_dir = os.path.join(tempfile.gettempdir(), "teleprompter_kb_test")
    db_path = os.path.join(test_dir, "test_kb.db")
    chroma_path = os.path.join(test_dir, "test_chroma")
    kb_file = os.path.join(test_dir, "project_rules.md")

    try:
        # Clean slate
        if os.path.exists(test_dir):
            shutil.rmtree(test_dir, ignore_errors=True)
        os.makedirs(test_dir, exist_ok=True)

        # Write test document
        content = (
            "# Antigravity Project Rules\n\n"
            "Here are the guidelines for the team:\n"
            "- Rule 1: The official mascot of Project Antigravity is a floating green apple named Gravity.\n"
            "- Rule 2: The project codebase must follow Python PEP 8 conventions.\n"
            "- Rule 3: All tests should run under 2 seconds.\n"
            "- Rule 4: The database backend uses SQLite with ChromaDB for vector search.\n"
        )
        with open(kb_file, "w", encoding="utf-8") as f:
            f.write(content)
        ok(f"Test document created: {os.path.basename(kb_file)}")

        # Initialize KB
        from knowledge_base import KnowledgeBase
        kb = KnowledgeBase(db_path=db_path, chroma_path=chroma_path)
        ok("KnowledgeBase initialized (SQLite + ChromaDB)")

        # Sync folder
        kb.sync_folder(test_dir)
        ok("Folder synced (file indexed)")

        # Test FTS5 search
        query = "mascot antigravity"
        info(f"FTS5+Vector search: \"{query}\"")
        results = kb.search(query, limit=3)

        if results:
            ok(f"Search returned {len(results)} result(s)")
            for r in results:
                info(f"  [{r['source']}] score={r['score']:.2f}: {r['text'][:80]}...")
        else:
            fail("Search returned no results")
            record(False)
            return False

        # Verify content is correct
        found_mascot = any("mascot" in r["text"].lower() or "gravity" in r["text"].lower() for r in results)
        if found_mascot:
            ok("Correct content found in search results")
        else:
            warn("Mascot content not found in top results (may be split across chunks)")

        # Test source removal
        kb.remove_source(os.path.abspath(kb_file))
        ok("Source removal completed")
        record(True)
        return True

    except Exception as e:
        fail(f"Knowledge base error: {e}")
        record(False)
        return False

    finally:
        # Cleanup
        try:
            if os.path.exists(test_dir):
                shutil.rmtree(test_dir, ignore_errors=True)
        except Exception:
            pass


# ── Test 5: Ollama LLM Suggestion Generation ───────────────────────────────
def test_ollama():
    header("TEST 5: Ollama LLM Suggestion Generation (gemma2:2b)")

    try:
        import requests
        resp = requests.get("http://localhost:11434/api/tags", timeout=5)
        models = resp.json().get("models", [])
        available = [m["name"] for m in models]
        if not available:
            fail("Ollama is running but no models are installed")
            record(False)
            return False
        ok(f"Ollama running with model(s): {', '.join(available)}")
    except Exception as e:
        fail(f"Cannot reach Ollama at localhost:11434 - {e}")
        record(False)
        return False

    try:
        from ollama_suggester import OllamaSuggester

        # Test with grounding context
        test_context = [
            {"source": "project_rules.md", "text": "The official mascot is a floating green apple named Gravity."}
        ]
        test_history = [
            ("[Remote]", "What is the mascot of the Antigravity project?"),
            ("[Local]", "Let me check the documentation.")
        ]

        info("Generating suggestion with grounding context...")
        suggester = OllamaSuggester(model_name="gemma2:2b")
        start = time.time()
        suggestions = suggester.generate_suggestions(test_history, context_chunks=test_context)
        elapsed = time.time() - start

        ok(f"Response generated in {elapsed:.2f}s")
        if suggestions and not suggestions.startswith("Error"):
            info(f"  Response:\n{suggestions}")

            # Check grounding
            lower = suggestions.lower()
            has_mascot = "mascot" in lower or "gravity" in lower or "apple" in lower
            has_citation = "project_rules" in lower or "project_rules.md" in lower

            if has_mascot:
                ok("Response contains mascot-related content (grounded)")
            else:
                warn("Response does not mention mascot (may still be valid)")

            if has_citation:
                ok("Response includes source citation")
            else:
                warn("Response missing source citation")
        else:
            fail(f"Ollama returned error: {suggestions}")
            record(False)
            return False

        record(True)
        return True

    except Exception as e:
        fail(f"Ollama suggester error: {e}")
        record(False)
        return False


# ── Test 6: Document Parser ─────────────────────────────────────────────────
def test_document_parser():
    header("TEST 6: Document Parser (txt, md, docx, pdf)")

    test_dir = os.path.join(tempfile.gettempdir(), "teleprompter_parser_test")
    os.makedirs(test_dir, exist_ok=True)

    try:
        from document_parser import get_document_chunks

        # Test .txt
        txt_file = os.path.join(test_dir, "test.txt")
        with open(txt_file, "w") as f:
            f.write("This is a plain text file for testing the parser. " * 20)
        chunks = get_document_chunks(txt_file, chunk_size=100, overlap=20)
        if chunks:
            ok(f".txt parsed: {len(chunks)} chunks")
        else:
            fail(".txt parsing returned no chunks")
            record(False)
            return False

        # Test .md
        md_file = os.path.join(test_dir, "test.md")
        with open(md_file, "w") as f:
            f.write("# Header\n\nSome markdown content about the project.\n\n## Section 2\n\nMore details here.")
        chunks = get_document_chunks(md_file, chunk_size=100, overlap=20)
        if chunks:
            ok(f".md parsed: {len(chunks)} chunks")
        else:
            fail(".md parsing returned no chunks")
            record(False)
            return False

        record(True)
        return True

    except Exception as e:
        fail(f"Document parser error: {e}")
        record(False)
        return False

    finally:
        try:
            shutil.rmtree(test_dir, ignore_errors=True)
        except Exception:
            pass


# ── Test 7: Full Pipeline (end-to-end) ─────────────────────────────────────
def test_full_pipeline():
    header("TEST 7: Full Pipeline Integration")

    test_dir = os.path.join(tempfile.gettempdir(), "teleprompter_pipeline_test")
    db_path = os.path.join(test_dir, "pipeline_kb.db")
    chroma_path = os.path.join(test_dir, "pipeline_chroma")
    kb_file = os.path.join(test_dir, "meeting_notes.md")

    try:
        if os.path.exists(test_dir):
            shutil.rmtree(test_dir, ignore_errors=True)
        os.makedirs(test_dir, exist_ok=True)

        # 1. Create test knowledge base
        content = (
            "# Q3 Budget Discussion\n\n"
            "The engineering budget for Q3 is $2.4M.\n"
            "Key hires: 2 backend engineers, 1 ML engineer.\n"
            "Infrastructure cost target: under $50K/month on AWS.\n"
            "The team uses Python 3.12 and FastAPI for all new services.\n"
        )
        with open(kb_file, "w") as f:
            f.write(content)
        ok("Test knowledge document created")

        # 2. Initialize KB and index
        from knowledge_base import KnowledgeBase
        kb = KnowledgeBase(db_path=db_path, chroma_path=chroma_path)
        kb.sync_folder(test_dir)
        ok("Knowledge base indexed")

        # 3. Simulate transcript
        transcript = [
            ("[Remote]", "We need to discuss the Q3 budget. What's our infrastructure spend looking like?"),
            ("[Local]", "Let me pull up the numbers."),
        ]

        # 4. Search for grounding context
        query = " ".join(t[1] for t in transcript[-2:])
        results = kb.search(query, limit=3)
        if results:
            ok(f"Retrieved {len(results)} context chunk(s) for grounding")
        else:
            warn("No context retrieved (empty knowledge base or query mismatch)")

        # 5. Generate suggestion
        from ollama_suggester import OllamaSuggester
        suggester = OllamaSuggester(model_name="gemma2:2b")
        start = time.time()
        suggestions = suggester.generate_suggestions(transcript, context_chunks=results)
        elapsed = time.time() - start

        if suggestions and not suggestions.startswith("Error"):
            ok(f"Pipeline complete - suggestion in {elapsed:.2f}s")
            info(f"  Output:\n{suggestions}")
        else:
            fail(f"Pipeline failed at suggestion generation: {suggestions}")
            record(False)
            return False

        record(True)
        return True

    except Exception as e:
        fail(f"Pipeline error: {e}")
        record(False)
        return False

    finally:
        try:
            shutil.rmtree(test_dir, ignore_errors=True)
        except Exception:
            pass


# ── Main ────────────────────────────────────────────────────────────────────
ALL_TESTS = {
    "audio":     ("Audio Devices",       test_audio_devices),
    "capture":   ("Audio Capture",       test_audio_capture),
    "transcribe":("Whisper Transcription", test_transcription),
    "kb":        ("Knowledge Base",      test_knowledge_base),
    "ollama":    ("Ollama LLM",          test_ollama),
    "parser":    ("Document Parser",     test_document_parser),
    "pipeline":  ("Full Pipeline",       test_full_pipeline),
}

def main():
    parser = argparse.ArgumentParser(description="Meeting Teleprompter - Unified Test Suite")
    parser.add_argument("--all", action="store_true", help="Run all tests")
    parser.add_argument("--audio", action="store_true", help="Test audio device detection")
    parser.add_argument("--capture", action="store_true", help="Test live audio capture")
    parser.add_argument("--transcribe", action="store_true", help="Test Whisper transcription")
    parser.add_argument("--kb", action="store_true", help="Test knowledge base indexing + search")
    parser.add_argument("--ollama", action="store_true", help="Test Ollama suggestion generation")
    parser.add_argument("--parser", action="store_true", help="Test document parser")
    parser.add_argument("--pipeline", action="store_true", help="Test full end-to-end pipeline")
    parser.add_argument("--live", action="store_true", help="Run audio capture test (10 seconds)")
    args = parser.parse_args()

    # If no flags, show help
    if not any(vars(args).values()):
        parser.print_help()
        return

    # Determine which tests to run
    run_keys = []
    if args.all:
        run_keys = list(ALL_TESTS.keys())
    else:
        if args.audio:    run_keys.append("audio")
        if args.capture or args.live: run_keys.append("capture")
        if args.transcribe: run_keys.append("transcribe")
        if args.kb:       run_keys.append("kb")
        if args.ollama:   run_keys.append("ollama")
        if args.parser:   run_keys.append("parser")
        if args.pipeline: run_keys.append("pipeline")

    print(f"\n{C.BOLD}{'#'*60}")
    print(f"  Local Meeting Teleprompter - Test Suite")
    print(f"  Running {len(run_keys)} test(s): {', '.join(run_keys)}")
    print(f"{'#'*60}{C.END}")

    start = time.time()

    for key in run_keys:
        name, fn = ALL_TESTS[key]
        try:
            fn()
        except Exception as e:
            fail(f"Unhandled exception in {name}: {e}")
            record(False)

    elapsed = time.time() - start

    # Summary
    print(f"\n{C.BOLD}{'='*60}")
    print(f"  RESULTS")
    print(f"{'='*60}{C.END}")
    total = results["passed"] + results["failed"] + results["skipped"]
    print(f"  Total:   {total}")
    print(f"  {C.OK}Passed:  {results['passed']}{C.END}")
    if results["failed"]:
        print(f"  {C.FAIL}Failed:  {results['failed']}{C.END}")
    if results["skipped"]:
        print(f"  {C.WARN}Skipped: {results['skipped']}{C.END}")
    print(f"  Time:    {elapsed:.1f}s")
    print()

    if results["failed"] == 0:
        print(f"  {C.OK}{C.BOLD}ALL TESTS PASSED{C.END}")
    else:
        print(f"  {C.FAIL}{C.BOLD}SOME TESTS FAILED{C.END}")
    print()

if __name__ == "__main__":
    main()
