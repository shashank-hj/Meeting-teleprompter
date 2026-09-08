"""
Proactive Trigger Engine — decides WHEN to generate a suggestion.
Analyzes transcript signals to avoid spamming the user.
"""
import time
import re


# ── Question detection ────────────────────────────────────────────────────────
_QUESTION_WORDS = {
    "what", "how", "why", "when", "where", "which", "who", "whom",
    "whose", "could", "would", "should", "is", "are", "do", "does",
    "did", "can", "will", "shall", "may", "might", "have", "has", "had",
}


def is_question(text: str) -> bool:
    t = text.strip()
    if t.endswith("?"):
        return True
    first_word = t.split()[0].lower().rstrip(",.") if t.split() else ""
    if first_word in _QUESTION_WORDS:
        return True
    if re.search(r"\b(?:tell me|explain|clarify|can you|could you|would you)\b", t, re.I):
        return True
    return False


# ── Topic change detection ────────────────────────────────────────────────────
def _word_overlap(a: str, b: str) -> float:
    """Jaccard similarity of word sets."""
    wa = set(re.findall(r"\w+", a.lower()))
    wb = set(re.findall(r"\w+", b.lower()))
    if not wa or not wb:
        return 0.0
    return len(wa & wb) / len(wa | wb)


def detect_topic_change(prev_text: str, curr_text: str, threshold: float = 0.12) -> bool:
    return _word_overlap(prev_text, curr_text) < threshold


# ── Claim / number detection ──────────────────────────────────────────────────
_NUMBER_PATTERN = re.compile(
    r"\b(?:\d{1,5}(?:\.\d+)?%?|\$[\d,.]+|"
    r"Q[1-4]\s*\d{2,4}|FY\d{2,4}|"
    r"(?:january|february|march|april|may|june|july|august|september|october|november|december)"
    r"\s+\d{1,2}(?:,?\s*\d{4})?|"
    r"\d{1,2}[/-]\d{1,2}[/-]\d{2,4})\b",
    re.I,
)
_STRONG_WORDS = re.compile(
    r"\b(?:always|never|must|cannot|impossible|guaranteed|100%|zero|none|all)\b", re.I
)


def detect_claims(text: str) -> bool:
    return bool(_NUMBER_PATTERN.search(text) or _STRONG_WORDS.search(text))


# ── Entity extraction ─────────────────────────────────────────────────────────
_ENTITY_PATTERN = re.compile(
    r"\b([A-Z][a-z]+(?:\s+[A-Z][a-z]+)*|\b[A-Z]{2,5}\b)\b"
)


def detect_entities(text: str) -> list:
    return list(set(_ENTITY_PATTERN.findall(text)))


# ── Trigger engine ────────────────────────────────────────────────────────────
class TriggerEngine:
    """Decides whether to trigger suggestion generation."""

    def __init__(
        self,
        cooldown_seconds: float = 15.0,
        min_utterances: int = 1,
        topic_sentences_before_change: int = 2,
    ):
        self.cooldown_seconds = cooldown_seconds
        self.min_utterances = min_utterances
        self.topic_sentences_before_change = topic_sentences_before_change

        self._last_suggestion_time = 0.0
        self._last_suggestion_text = ""
        self._topic_utterance_count = 0
        self._last_topic_text = ""
        self._consecutive_questions = 0

    def should_trigger(self, transcript: list, last_suggestion_text: str = None) -> dict:
        """
        Analyze transcript and decide whether to trigger.

        Returns dict with keys:
            trigger: bool
            reason: str (for UI display)
        """
        if not transcript:
            return {"trigger": False, "reason": ""}

        now = time.time()
        latest = transcript[-1]
        text = latest.get("text", "")

        # Always update state
        if last_suggestion_text is not None:
            self._last_suggestion_text = last_suggestion_text

        # Minimum utterances check
        if len(transcript) < self.min_utterances:
            return {"trigger": False, "reason": ""}

        # Cooldown check
        elapsed = now - self._last_suggestion_time
        if elapsed < self.cooldown_seconds:
            return {"trigger": False, "reason": ""}

        # Deduplication: skip if transcript is mostly same as last suggestion context
        if self._last_suggestion_text:
            overlap = _word_overlap(text, self._last_suggestion_text)
            if overlap > 0.7:
                return {"trigger": False, "reason": ""}

        # --- Signal detection ---

        # 1. Questions — always trigger
        if is_question(text):
            self._consecutive_questions += 1
            self._last_suggestion_time = now
            return {"trigger": True, "reason": "question"}

        self._consecutive_questions = 0

        # 2. Topic change — trigger after N sentences on previous topic
        if self._last_topic_text and detect_topic_change(self._last_topic_text, text):
            if self._topic_utterance_count >= self.topic_sentences_before_change:
                self._topic_utterance_count = 0
                self._last_topic_text = text
                self._last_suggestion_time = now
                return {"trigger": True, "reason": "topic_change"}
        else:
            self._topic_utterance_count += 1
        self._last_topic_text = text

        # 3. Claims with numbers — may need evidence
        if detect_claims(text):
            self._last_suggestion_time = now
            return {"trigger": True, "reason": "claim"}

        # 4. Every N utterances after cooldown (default: every 3 after 15s)
        if len(transcript) % 3 == 0 and elapsed > self.cooldown_seconds:
            self._last_suggestion_time = now
            return {"trigger": True, "reason": "periodic"}

        return {"trigger": False, "reason": ""}

    def reset(self):
        self._last_suggestion_time = 0.0
        self._last_suggestion_text = ""
        self._topic_utterance_count = 0
        self._last_topic_text = ""
        self._consecutive_questions = 0


if __name__ == "__main__":
    engine = TriggerEngine(cooldown_seconds=0)  # No cooldown for testing
    test_transcript = [
        {"text": "Let's discuss the quarterly budget numbers."},
        {"text": "The Q3 revenue was $2.4M, which is 15% above target."},
        {"text": "We need to plan the marketing campaign for next month."},
    ]

    for i, entry in enumerate(test_transcript):
        result = engine.should_trigger(test_transcript[:i + 1])
        print(f"  [{i}] '{entry['text'][:50]}...' -> {result}")

    print("\nQuestion test:")
    q_transcript = [{"text": "What is the current burn rate?"}]
    print(f"  -> {engine.should_trigger(q_transcript)}")
