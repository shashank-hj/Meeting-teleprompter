from faster_whisper import WhisperModel
import numpy as np
import re
from collections import Counter
from scipy.signal import butter, sosfilt


class Transcriber:
    # MINIMAL prompt — only critical terms. Long prompts cause Whisper to
    # hallucinate those words when it hears noise or silence.
    DEFAULT_PROMPT = "SQL, database, API, server, deployment, meeting, project."

    def __init__(self, model_size="base.en", device="cpu", compute_type="int8"):
        print(f"Loading Whisper model '{model_size}' on {device} ({compute_type})...")
        self.model = WhisperModel(model_size, device=device, compute_type=compute_type)
        self.initial_prompt = self.DEFAULT_PROMPT
        print("Whisper model loaded successfully.")

    @staticmethod
    def _normalize(audio_data, target_rms=0.04):
        """Adaptively normalize audio for ASR. Less aggressive to avoid amplifying noise."""
        if len(audio_data) == 0:
            return audio_data

        # High-pass filter at 120Hz to remove DC offset / low-freq rumble
        sos = butter(4, 120, 'hp', fs=16000, output='sos')
        audio_data = sosfilt(sos, audio_data).astype(np.float32)

        rms = np.sqrt(np.mean(audio_data ** 2))
        if rms < 1e-6:
            return audio_data

        # Boost to target RMS, capped at 12x (was 30x — too aggressive, amplified noise)
        gain = target_rms / rms
        gain = min(gain, 12.0)
        normalized = audio_data * gain

        # Soft clip to keep in [-1, 1] without harsh distortion
        normalized = np.tanh(normalized)
        return normalized.astype(np.float32)

    @staticmethod
    def _is_hallucination(text):
        """
        Detect Whisper hallucinations: repeated words/phrases, repeated patterns,
        or text that's mostly single-word repetition.
        Handles both space-separated and comma-separated repetitions.
        """
        if not text or len(text.strip()) < 3:
            return True

        # Normalize: collapse all punctuation separators to spaces
        text_lower = text.lower().strip()
        normalized = re.sub(r'[,;.\-_!?]+', ' ', text_lower)
        normalized = re.sub(r'\s+', ' ', normalized).strip()

        words = normalized.split()
        if not words:
            return True

        # 1. Single word repeated >4 times, >50% of all words
        if len(words) > 3:
            word_counts = Counter(words)
            most_common_word, most_common_count = word_counts.most_common(1)[0]
            if most_common_count > 4 and most_common_count / len(words) > 0.5:
                return True

        # 2. Very long segment that is clearly noise (>30 words, mostly repetition)
        if len(words) > 30:
            word_counts = Counter(words)
            top_word, top_count = word_counts.most_common(1)[0]
            if top_count / len(words) > 0.4:
                return True

        # 3. Short phrase repeated 3+ times consecutively (2-gram, 3-gram)
        if len(words) >= 6:
            for n in [2, 3]:
                for i in range(len(words) - n * 2 + 1):
                    phrase = " ".join(words[i:i+n])
                    count = 0
                    for j in range(i, len(words) - n + 1, n):
                        if " ".join(words[j:j+n]) == phrase:
                            count += 1
                        else:
                            break
                    if count >= 3:
                        return True

        # 4. Long phrase repetition — the whole text is the same phrase repeated
        #    e.g. "we're going to be able to do that" x3
        if len(words) >= 6:
            # Check for any phrase of length 4+ that appears 2+ times
            # and covers >50% of the text
            for n in range(4, len(words) // 2 + 1):
                for start in range(0, min(n, len(words) - n + 1)):
                    phrase_words = words[start:start+n]
                    occurrences = 0
                    for i in range(len(words) - n + 1):
                        if words[i:i+n] == phrase_words:
                            occurrences += 1
                    if occurrences >= 2 and occurrences * n >= len(words) * 0.5:
                        return True

        # 5. Same sentence/clause repeated (after normalizing punctuation)
        #    Also try splitting on commas for comma-separated repetitions
        for sep in [r'[.!?]+', r'[,]+']:
            sentences = re.split(sep, normalized)
            sentences = [s.strip() for s in sentences if s.strip() and len(s.strip()) > 5]
            if len(sentences) > 2:
                unique = set(sentences)
                if len(unique) <= max(1, len(sentences) // 3):
                    return True

        # 6. Text shorter than 4 characters
        if len(normalized) < 4:
            return True

        # 7. Words like "azure" repeated — check even 2-word combos
        if len(words) >= 4:
            # Check if first word appears >70% of the time
            first_word = words[0]
            first_count = sum(1 for w in words if w == first_word)
            if first_count / len(words) > 0.7 and first_count > 3:
                return True

        return False

    def transcribe(self, audio_data):
        """
        Transcribes a NumPy array of 16kHz mono audio.
        Returns (text, confidence) where confidence is 0.0-1.0 (higher = more confident).
        """
        if len(audio_data) == 0:
            return ("", 1.0)

        # Check if audio is mostly silence — skip transcription entirely
        rms = np.sqrt(np.mean(audio_data ** 2))
        if rms < 0.001:
            return ("", 0.0)

        # Normalize amplitude — quieter gain to avoid amplifying noise
        audio_data = self._normalize(audio_data)

        # Tight VAD to aggressively filter noise segments
        segments, info = self.model.transcribe(
            audio_data,
            beam_size=3,              # Lower beam = faster, less prone to hallucination loops
            language="en",
            initial_prompt=self.initial_prompt,
            vad_filter=True,
            vad_parameters=dict(
                min_silence_duration_ms=600,   # Longer silence gap to filter noise
                speech_pad_ms=200,             # Less padding around speech
                threshold=0.5,                 # VAD confidence threshold (0-1)
            ),
            condition_on_previous_text=False,  # CRITICAL: prevents hallucination chains
            no_speech_threshold=0.5,           # Lower = more segments filtered as noise
            compression_ratio_threshold=2.2,   # Stricter: reject repetitive output
            temperature=0.0,
            log_prob_threshold=-1.2,           # Reject low-confidence segments
            no_speech_prob_threshold=0.5,      # Skip segments with high no-speech probability
        )

        text_segments = []
        logprobs = []
        no_speech_probs = []
        for segment in segments:
            seg_text = segment.text.strip()
            if not seg_text:
                continue

            # Skip segments with very high no-speech probability
            if segment.no_speech_prob > 0.6:
                continue

            # Skip hallucinated segments
            if self._is_hallucination(seg_text):
                print(f"[WHISPER] Filtered hallucination: '{seg_text[:60]}...' (no_speech={segment.no_speech_prob:.2f})")
                continue

            text_segments.append(seg_text)
            logprobs.append(segment.avg_logprob)
            no_speech_probs.append(segment.no_speech_prob)

        full_text = " ".join(text_segments).strip()

        # Final pass: check if the concatenated result is a hallucination
        if self._is_hallucination(full_text):
            print(f"[WHISPER] Filtered full hallucination: '{full_text[:60]}...'")
            return ("", 0.0)

        # Compute aggregate confidence
        if logprobs:
            avg_logprob = sum(logprobs) / len(logprobs)
            avg_no_speech = sum(no_speech_probs) / len(no_speech_probs)
            prob_score = max(0.0, min(1.0, 1.0 + avg_logprob))
            confidence = prob_score * (1.0 - avg_no_speech * 0.5)
            confidence = max(0.0, min(1.0, confidence))
        else:
            confidence = 0.0

        return (full_text, confidence)


if __name__ == "__main__":
    import time
    t = Transcriber(model_size="base.en")
    dummy_input = np.zeros(16000 * 2, dtype=np.float32)
    start = time.time()
    result = t.transcribe(dummy_input)
    print(f"Transcription took {time.time() - start:.3f}s. Result: '{result}'")