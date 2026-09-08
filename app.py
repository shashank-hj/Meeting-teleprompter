import time
import threading
import sys
import os
from audio_capture import AudioCapture
from transcribe import Transcriber
from ollama_suggester import OllamaSuggester
from knowledge_base import KnowledgeBase

# Try to use rich for pretty terminal printing
class SimpleConsole:
    def print(self, *args, **kwargs):
        print(*args, **kwargs)

try:
    from rich.console import Console
    from rich.panel import Panel
    from rich.text import Text
    console = Console()
except ImportError:
    console = SimpleConsole()

class TeleprompterApp:
    def __init__(self, whisper_model="tiny.en", gemma_model="gemma2:2b", rms_threshold=0.003):
        self.transcriber = Transcriber(model_size=whisper_model)
        self.suggester = OllamaSuggester(model_name=gemma_model)
        self.capture = AudioCapture(rms_threshold=rms_threshold, silence_seconds=1.2)
        self.kb = KnowledgeBase()
        
        self.transcript_history = []
        self.max_history_len = 15
        
        self.suggestion_lock = threading.Lock()
        self.current_suggestion_id = 0
        self.is_generating_suggestion = False

    def print_suggestion_box(self, content):
        timestamp = time.strftime("%H:%M:%S")
        if hasattr(console, "print") and not isinstance(console, SimpleConsole):
            panel = Panel(
                Text(content, style="cyan"),
                title=f"[bold green]💡 Teleprompter Suggestions ({timestamp})[/bold green]",
                border_style="green",
                expand=False
            )
            console.print()
            console.print(panel)
            console.print()
        else:
            print(f"\n💡 [Teleprompter Suggestions - {timestamp}]")
            print(content)
            print("-" * 50 + "\n")

    def _generate_suggestion_thread(self, query_id, history_snapshot):
        try:
            # 1. Search the knowledge base for grounding context
            search_words = []
            # Take up to the last 2 segments of the transcript to build a contextual query
            for speaker, text in history_snapshot[-2:]:
                search_words.append(text)
            search_query = " ".join(search_words)
            
            context_chunks = []
            if search_query.strip():
                context_chunks = self.kb.search(search_query, limit=3)
                
            # 2. Query Ollama with grounding context
            suggestions = self.suggester.generate_suggestions(history_snapshot, context_chunks)
            
            with self.suggestion_lock:
                # Only display if this is still the most recent request
                if query_id == self.current_suggestion_id:
                    self.print_suggestion_box(suggestions)
                    self.is_generating_suggestion = False
        except Exception as e:
            with self.suggestion_lock:
                if query_id == self.current_suggestion_id:
                    self.is_generating_suggestion = False
            print(f"Error in suggestion generation thread: {e}")

    def trigger_suggestion_generation(self):
        with self.suggestion_lock:
            self.current_suggestion_id += 1
            query_id = self.current_suggestion_id
            history_snapshot = list(self.transcript_history)
            self.is_generating_suggestion = True
            
        # Spawn thread so we don't block transcription
        t = threading.Thread(
            target=self._generate_suggestion_thread,
            args=(query_id, history_snapshot),
            daemon=True
        )
        t.start()

    def run(self):
        print("Syncing local knowledge base directory...")
        self.kb.sync_folder("knowledge_base")
        print("Knowledge base sync complete.")
        
        print("\n" + "="*60)
        print("         LOCAL REAL-TIME MEETING TELEPROMPTER ACTIVE")
        print("="*60)
        print("Listening for speech from Microphone and Speakers (Loopback)...")
        print("Speak or play audio to begin. Press Ctrl+C to exit.\n")
        
        self.capture.start()
        
        try:
            while True:
                # Check for completed speech chunks (timeout to avoid locking up)
                utt = self.capture.get_utterance(timeout=0.1)
                if utt:
                    speaker_tag, audio_data = utt
                    
                    # 1. Transcribe the audio
                    timestamp = time.strftime("%H:%M:%S")
                    tag_str = "[Local]" if speaker_tag == "local" else "[Remote]"
                    
                    # Log transcribing state
                    sys.stdout.write(f"\r[{timestamp}] {tag_str} Transcribing...")
                    sys.stdout.flush()
                    
                    text = self.transcriber.transcribe(audio_data)
                    
                    # Clear loading status
                    sys.stdout.write("\r" + " " * 50 + "\r")
                    sys.stdout.flush()
                    
                    if text:
                        # Print finalized transcription
                        speaker_label = "[bold blue][Local][/bold blue]" if speaker_tag == "local" else "[bold magenta][Remote][/bold magenta]"
                        if hasattr(console, "print") and not isinstance(console, SimpleConsole):
                            console.print(f"[{timestamp}] {speaker_label}: {text}")
                        else:
                            print(f"[{timestamp}] {tag_str}: {text}")
                            
                        # 2. Append to history
                        self.transcript_history.append((tag_str, text))
                        if len(self.transcript_history) > self.max_history_len:
                            self.transcript_history.pop(0)
                            
                        # 3. Trigger suggestions
                        self.trigger_suggestion_generation()
                        
                # Sleep a tiny bit to be gentle on CPU
                time.sleep(0.01)
                
        except KeyboardInterrupt:
            print("\nExiting...")
        finally:
            self.capture.stop()

if __name__ == "__main__":
    # Allow model configurations via command line or defaults
    import argparse
    parser = argparse.ArgumentParser(description="Real-Time Meeting Teleprompter")
    parser.add_argument("--whisper-model", type=str, default="tiny.en", help="faster-whisper model (e.g. tiny.en, base.en)")
    parser.add_argument("--gemma-model", type=str, default="gemma2:2b", help="Ollama gemma model tag (e.g. gemma2:2b, llama3.2)")
    parser.add_argument("--threshold", type=float, default=0.003, help="VAD RMS energy threshold (default: 0.003)")
    
    args = parser.parse_args()
    
    app = TeleprompterApp(
        whisper_model=args.whisper_model, 
        gemma_model=args.gemma_model,
        rms_threshold=args.threshold
    )
    app.run()
