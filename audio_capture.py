import pyaudiowpatch as pyaudio
import numpy as np
import queue
import time
import threading

class StreamConfig:
    """Stores the actual configuration of an opened audio stream."""
    def __init__(self, stream, channels, sample_rate):
        self.stream = stream
        self.channels = channels
        self.sample_rate = sample_rate

class AudioCapture:
    def __init__(self, mic_name=None, loopback_name=None, sample_rate=16000, rms_threshold=0.005, silence_seconds=1.0, max_speech_seconds=15.0):
        self.p = pyaudio.PyAudio()
        self.target_sr = sample_rate
        self.rms_threshold = rms_threshold
        self.silence_seconds = silence_seconds
        self.max_speech_seconds = max_speech_seconds

        self.mic_info = None
        self.loopback_info = None

        self._find_devices(mic_name, loopback_name)

        self.mic_queue = queue.Queue()
        self.loopback_queue = queue.Queue()

        self.mic_cfg = None       # StreamConfig for microphone
        self.loopback_cfg = None  # StreamConfig for loopback

        self.is_running = False
        self.utterance_queue = queue.Queue()
        self.processing_thread = None

    def _find_devices(self, mic_name, loopback_name):
        wasapi_idx = None
        for i in range(self.p.get_host_api_count()):
            api_info = self.p.get_host_api_info_by_index(i)
            if api_info["type"] == pyaudio.paWASAPI:
                wasapi_idx = api_info["index"]
                break

        if wasapi_idx is None:
            raise RuntimeError("WASAPI host API not found.")

        for index in range(self.p.get_device_count()):
            info = self.p.get_device_info_by_index(index)
            if info["hostApi"] != wasapi_idx:
                continue

            name = info["name"]

            if mic_name:
                if mic_name.lower() in name.lower() and info["maxInputChannels"] > 0:
                    self.mic_info = info
            else:
                if ("mic" in name.lower() or "microphone" in name.lower()) and info["maxInputChannels"] > 0:
                    if not self.mic_info:
                        self.mic_info = info

            if loopback_name:
                if loopback_name.lower() in name.lower() and info.get("isLoopbackDevice", False):
                    self.loopback_info = info
            else:
                if info.get("isLoopbackDevice", False):
                    if not self.loopback_info or "speaker" in name.lower() or "headphone" in name.lower():
                        self.loopback_info = info

        if not self.mic_info:
            try:
                default_in = self.p.get_default_input_device_info()
                if default_in["hostApi"] == wasapi_idx:
                    self.mic_info = default_in
            except IOError:
                pass

        if not self.loopback_info:
            for index in range(self.p.get_device_count()):
                info = self.p.get_device_info_by_index(index)
                if info["hostApi"] == wasapi_idx and info.get("isLoopbackDevice", False):
                    self.loopback_info = info
                    break

        if not self.mic_info:
            print("Warning: No WASAPI Microphone device detected.")
        else:
            print(f"Using Microphone: {self.mic_info['name']} (Index: {self.mic_info['index']})")

        if not self.loopback_info:
            print("Warning: No WASAPI Speakers [Loopback] device detected.")
        else:
            print(f"Using Loopback: {self.loopback_info['name']} (Index: {self.loopback_info['index']})")

    def _open_stream(self, device_info, queue_obj, label):
        """Try to open an audio stream with fallback configurations."""
        if not device_info:
            return None

        device_index = device_info["index"]
        native_sr = int(device_info["defaultSampleRate"])
        max_channels = device_info["maxInputChannels"]

        # For microphones, ALWAYS try mono first. Microphone arrays report
        # maxInputChannels=2 but usually only one channel has real data, and
        # averaging the channels combines a beamformed signal with noise.
        # Loopback devices need stereo to capture full system audio.
        is_loopback = label == "Loopback"

        if is_loopback:
            configs = [
                (2, native_sr, f"stereo @ {native_sr}Hz"),
                (1, native_sr, f"mono @ {native_sr}Hz"),
                (1, 16000, "mono @ 16000Hz"),
            ]
        else:
            configs = [
                (1, native_sr, f"mono @ {native_sr}Hz"),
                (1, 16000, "mono @ 16000Hz"),
                (2, native_sr, f"stereo @ {native_sr}Hz"),
            ]

        for channels, rate, desc in configs:
            try:
                stream = self.p.open(
                    format=pyaudio.paFloat32,
                    channels=channels,
                    rate=rate,
                    input=True,
                    input_device_index=device_index,
                    stream_callback=self._make_callback(queue_obj)
                )
                print(f"  [{label}] Opened: {desc}")
                return StreamConfig(stream, channels, rate)
            except OSError as e:
                print(f"  [{label}] Failed {desc}: {e}")
                continue
            except Exception as e:
                print(f"  [{label}] Failed {desc}: {e}")
                continue

        print(f"  [{label}] Could not open any stream for this device")
        return None

    def _make_callback(self, q):
        def callback(in_data, frame_count, time_info, status):
            q.put(in_data)
            return (None, pyaudio.paContinue)
        return callback

    def start(self):
        if self.is_running:
            return

        self.is_running = True

        print("Opening audio streams...")
        self.mic_cfg = self._open_stream(self.mic_info, self.mic_queue, "Mic")
        self.loopback_cfg = self._open_stream(self.loopback_info, self.loopback_queue, "Loopback")

        if not self.mic_cfg and not self.loopback_cfg:
            print("ERROR: No audio streams could be opened!")
            self.is_running = False
            raise RuntimeError("No audio input devices available. Check if another app is blocking the microphone.")

        if self.mic_cfg:
            self.mic_cfg.stream.start_stream()
        if self.loopback_cfg:
            self.loopback_cfg.stream.start_stream()

        self.processing_thread = threading.Thread(target=self._process_loop, daemon=True)
        self.processing_thread.start()
        print("Audio capture started.")

    def stop(self):
        self.is_running = False
        if self.processing_thread:
            self.processing_thread.join(timeout=2.0)

        for cfg in (self.mic_cfg, self.loopback_cfg):
            if cfg:
                try:
                    cfg.stream.stop_stream()
                    cfg.stream.close()
                except Exception:
                    pass
        self.mic_cfg = None
        self.loopback_cfg = None

        try:
            self.p.terminate()
        except Exception:
            pass
        print("Audio capture stopped.")

    def get_utterance(self, timeout=None):
        try:
            return self.utterance_queue.get(timeout=timeout)
        except queue.Empty:
            return None

    @staticmethod
    def _to_mono_and_resample(audio_data, channels, native_sr, target_sr):
        """Convert interleaved float32 samples to mono, resampled to target_sr."""
        if channels > 1:
            audio_data = audio_data.reshape(-1, channels)
            mono = np.mean(audio_data, axis=1)
        else:
            mono = audio_data

        # Resample to target with proper anti-aliasing (resample_poly).
        # Plain decimation like [::3] aliases high frequencies into the speech
        # band and corrupts the signal, causing Whisper to fail.
        if native_sr == target_sr:
            return mono.astype(np.float32)
        elif native_sr == 48000 and target_sr == 16000:
            from scipy.signal import resample_poly
            return resample_poly(mono, 1, 3).astype(np.float32)
        else:
            from fractions import Fraction
            r = Fraction(target_sr, native_sr)
            from scipy.signal import resample_poly
            return resample_poly(mono, r.numerator, r.denominator).astype(np.float32)

    @staticmethod
    def _trim_silence(audio, threshold=0.003, pad_samples=1600):
        """Trim leading/trailing silence from audio (16kHz)."""
        if len(audio) == 0:
            return audio

        # Frame-based RMS
        frame = 320  # 20ms
        n = len(audio)
        n_frames = max(1, n // frame)
        rms = np.array([
            np.sqrt(np.mean(audio[i*frame:(i+1)*frame] ** 2))
            for i in range(n_frames)
        ])

        voiced = np.where(rms > threshold)[0]
        if len(voiced) == 0:
            return audio

        start = max(0, voiced[0] * frame - pad_samples)
        end = min(n, (voiced[-1] + 1) * frame + pad_samples)
        return audio[start:end]

    def _process_loop(self):
        local_state = {
            "queue": self.mic_queue,
            "tag": "local",
            "cfg": self.mic_cfg,
            "gain": 10.0,      # Boost quiet microphone input
            "threshold": self.rms_threshold * 0.4,
            "in_speech": False,
            "speech_chunks": [],
            "silence_start": None,
            "speech_start_time": None
        }

        remote_state = {
            "queue": self.loopback_queue,
            "tag": "remote",
            "cfg": self.loopback_cfg,
            "gain": 1.0,       # Loopback is already strong
            "threshold": self.rms_threshold,
            "in_speech": False,
            "speech_chunks": [],
            "silence_start": None,
            "speech_start_time": None
        }

        while self.is_running:
            processed_any = False

            for state in (local_state, remote_state):
                cfg = state["cfg"]
                if not cfg:
                    continue

                q = state["queue"]

                chunks = []
                while not q.empty():
                    try:
                        chunks.append(q.get_nowait())
                    except queue.Empty:
                        break

                if not chunks:
                    continue

                processed_any = True

                raw_bytes = b"".join(chunks)
                audio_data = np.frombuffer(raw_bytes, dtype=np.float32)

                if len(audio_data) == 0:
                    continue

                # Convert interleaved -> mono, resample to 16kHz
                mono = self._to_mono_and_resample(
                    audio_data, cfg.channels, cfg.sample_rate, self.target_sr
                )

                if len(mono) == 0:
                    continue

                # Apply channel-specific gain (boost quiet mic)
                if state["gain"] != 1.0:
                    mono = mono * state["gain"]
                    np.clip(mono, -1.0, 1.0, out=mono)

                # Voice Activity Detection
                rms = np.sqrt(np.mean(mono ** 2)) if len(mono) > 0 else 0
                is_active = rms > state["threshold"]
                current_time = time.time()

                if is_active:
                    if not state["in_speech"]:
                        state["in_speech"] = True
                        state["speech_chunks"] = [mono]
                        state["speech_start_time"] = current_time
                        state["silence_start"] = None
                    else:
                        state["speech_chunks"].append(mono)
                        state["silence_start"] = None

                    elapsed_speech = current_time - state["speech_start_time"]
                    if elapsed_speech >= self.max_speech_seconds:
                        speech_audio = np.concatenate(state["speech_chunks"])
                        speech_audio = self._trim_silence(speech_audio)
                        self.utterance_queue.put((state["tag"], speech_audio))
                        state["speech_chunks"] = []
                        state["speech_start_time"] = current_time
                else:
                    if state["in_speech"]:
                        state["speech_chunks"].append(mono)
                        if state["silence_start"] is None:
                            state["silence_start"] = current_time
                        elif current_time - state["silence_start"] >= self.silence_seconds:
                            speech_audio = np.concatenate(state["speech_chunks"])
                            speech_audio = self._trim_silence(speech_audio)
                            self.utterance_queue.put((state["tag"], speech_audio))
                            state["in_speech"] = False
                            state["speech_chunks"] = []
                            state["silence_start"] = None
                            state["speech_start_time"] = None

            if not processed_any:
                time.sleep(0.02)


if __name__ == "__main__":
    cap = AudioCapture(rms_threshold=0.005, silence_seconds=1.0)
    cap.start()
    try:
        print("Speak into microphone or play system audio. Press Ctrl+C to stop.")
        while True:
            utt = cap.get_utterance(timeout=0.5)
            if utt:
                tag, audio = utt
                duration = len(audio) / 16000
                rms = np.sqrt(np.mean(audio**2))
                print(f"Captured -> {tag} | {duration:.2f}s | RMS={rms:.4f}")
    except KeyboardInterrupt:
        pass
    finally:
        cap.stop()