"""
Rolling Conversation State — maintains compact context for the LLM.
Replaces sending the full transcript every time.
"""
import re
from collections import Counter


# Common stop words to filter out of topic extraction
_STOP_WORDS = frozenset(
    "a an the is are was were be been being have has had do does did "
    "will would shall should may might can could of in on at to for "
    "with by from as into through during before after above below between "
    "out off over under again further then once that this these those "
    "and but or nor not so yet both either neither each every all any "
    "few more most other some such no nor too very just about also how "
    "its it he she they we you i me him her us them my your his our their "
    "what which who whom whose when where why how am if than".split()
)


def _extract_topics(text: str, top_n: int = 5) -> list:
    """Extract significant words as topic candidates."""
    words = re.findall(r"\b[a-z]{3,}\b", text.lower())
    words = [w for w in words if w not in _STOP_WORDS]
    return [w for w, _ in Counter(words).most_common(top_n)]


def _extract_entities(text: str) -> list:
    """Extract capitalized words and acronyms as named entities."""
    entities = set()
    # Capitalized words (not at sentence start, relaxed for simplicity)
    for match in re.finditer(r"\b([A-Z][a-z]{2,}(?:\s+[A-Z][a-z]{2,})*)\b", text):
        entities.add(match.group(1))
    # Acronyms (2-5 uppercase letters)
    for match in re.finditer(r"\b([A-Z]{2,5})\b", text):
        entities.add(match.group(1))
    return sorted(entities)


def _detect_decisions(text: str) -> list:
    """Detect decision-like statements."""
    decisions = []
    patterns = [
        r"(?:we (?:decided|agreed|will|shall|need to|should))\s+(.{10,80}?)[.!?]",
        r"(?:let's|lets)\s+(.{10,80}?)[.!?]",
        r"(?:the (?:plan|decision|agreement) is)\s+(.{10,80}?)[.!?]",
    ]
    for pat in patterns:
        for m in re.finditer(pat, text, re.I):
            decisions.append(m.group(1).strip())
    return decisions


def _detect_open_questions(text: str) -> list:
    """Detect unresolved questions."""
    questions = []
    for sent in re.split(r"[.!?]+", text):
        sent = sent.strip()
        if sent.endswith("?") or re.search(r"\b(?:what|how|why|when|where|who)\b", sent, re.I):
            if len(sent) > 10:
                questions.append(sent)
    return questions[:3]


class ConversationState:
    """Maintains rolling conversation context for LLM prompts."""

    def __init__(self, max_history: int = 30):
        self.max_history = max_history
        self.topics: list = []
        self.entities: set = set()
        self.decisions: list = []
        self.open_questions: list = []
        self.summary: str = ""
        self._utterance_count = 0
        self._summary_interval = 5

    def update(self, entry: dict) -> None:
        """Process a new transcript entry."""
        text = entry.get("text", "")
        if not text:
            return

        self._utterance_count += 1

        # Update topics
        new_topics = _extract_topics(text)
        if new_topics:
            self.topics = new_topics[:5]

        # Update entities
        new_entities = _extract_entities(text)
        self.entities.update(new_entities)

        # Detect decisions
        new_decisions = _detect_decisions(text)
        self.decisions.extend(new_decisions)
        self.decisions = self.decisions[-5:]

        # Detect open questions
        new_questions = _detect_open_questions(text)
        self.open_questions.extend(new_questions)
        self.open_questions = self.open_questions[-5:]

        # Update summary periodically
        if self._utterance_count % self._summary_interval == 0:
            self._update_summary(entry)

    def _update_summary(self, last_entry: dict) -> None:
        """Build a compact summary from accumulated state."""
        parts = []
        if self.topics:
            parts.append(f"Topics: {', '.join(self.topics[:3])}")
        if self.entities:
            top_entities = sorted(self.entities)[:8]
            parts.append(f"Entities: {', '.join(top_entities)}")
        if self.decisions:
            parts.append(f"Decisions: {'; '.join(self.decisions[-2:])}")
        if self.open_questions:
            parts.append(f"Open questions: {'; '.join(self.open_questions[-2:])}")
        self.summary = " | ".join(parts) if parts else ""

    def get_context_for_llm(self, transcript: list) -> str:
        """
        Build compact context string for the LLM.
        Returns a focused context instead of full transcript.
        """
        parts = []

        # Rolling summary
        if self.summary:
            parts.append(f"CONVERSATION STATE: {self.summary}")

        # Last 5 transcript entries (most relevant)
        recent = transcript[-5:] if len(transcript) > 5 else transcript
        transcript_lines = []
        for e in recent:
            speaker = e.get("speaker", "unknown")
            text = e.get("text", "")
            label = "Local" if speaker == "local" else "Remote"
            transcript_lines.append(f"[{label}]: {text}")
        parts.append("RECENT TRANSCRIPT:\n" + "\n".join(transcript_lines))

        return "\n\n".join(parts)

    def get_search_query(self, transcript: list) -> str:
        """Build best keywords for knowledge base search."""
        query_parts = []

        # 1. Always include tracked entities (technical terms, names, acronyms)
        if self.entities:
            query_parts.extend(sorted(self.entities)[-5:])

        # 2. Detect question intent and extract core topic
        if transcript:
            latest_text = transcript[-1].get("text", "")
            # Extract topic from questions: "What is X?", "How does Y work?", "Explain Z"
            question_topic = self._extract_question_topic(latest_text)
            if question_topic:
                query_parts.append(question_topic)

            # Extract meaningful words (keep 2+ char words to preserve acronyms like SQL, API)
            words = re.findall(r"\b[a-zA-Z]{2,}\b", latest_text)
            words = [w.lower() for w in words if w.lower() not in _STOP_WORDS]
            query_parts.extend(words[:8])

        # 3. Add current topics for additional context
        if self.topics:
            for t in self.topics[:3]:
                if t not in [w.lower() for w in query_parts]:
                    query_parts.append(t)

        # Deduplicate while preserving order
        seen = set()
        unique = []
        for w in query_parts:
            wl = w.lower()
            if wl not in seen and len(wl) > 1:
                seen.add(wl)
                unique.append(wl)

        return " ".join(unique) if unique else ""

    @staticmethod
    def _extract_question_topic(text):
        """Extract the core topic from a question like 'What is SQL?' → 'SQL'."""
        patterns = [
            r"(?:what|who) (?:is|are|does) (?:a |an |the )?(.+?)[\?]?$",
            r"(?:how|why) (?:does|do|is|are|can|should) (?:a |an |the )?(.+?)[\?]?$",
            r"(?:explain|describe|define|tell me about) (.+?)[\?]?$",
            r"(?:can you|could you|would you) (?:explain|describe|define|tell me about) (.+?)[\?]?$",
        ]
        text_lower = text.lower().strip().rstrip("?")
        for pat in patterns:
            m = re.match(pat, text_lower, re.IGNORECASE)
            if m:
                topic = m.group(1).strip()
                # Remove trailing prepositions
                topic = re.sub(r"\s+(in|on|at|for|with|about)$", "", topic)
                if len(topic) > 1:
                    return topic
        return None

    def reset(self) -> None:
        """Clear all state."""
        self.topics.clear()
        self.entities.clear()
        self.decisions.clear()
        self.open_questions.clear()
        self.summary = ""
        self._utterance_count = 0


if __name__ == "__main__":
    state = ConversationState()
    entries = [
        {"speaker": "remote", "text": "We need to discuss the Q3 budget for the API server migration."},
        {"speaker": "local", "text": "The current AWS costs are $4,200 per month, which is 20% over budget."},
        {"speaker": "remote", "text": "Can we move to a cheaper provider like DigitalOcean?"},
        {"speaker": "local", "text": "Let's decide: we'll migrate to DigitalOcean by end of Q4."},
        {"speaker": "remote", "text": "What about the downtime during migration?"},
    ]

    for entry in entries:
        state.update(entry)
        print(f"  Entry: {entry['text'][:60]}...")
        print(f"  Topics: {state.topics}")
        print(f"  Entities: {state.entities}")
        print(f"  Decisions: {state.decisions}")
        print(f"  Open Qs: {state.open_questions}")
        print()

    print("LLM Context:")
    print(state.get_context_for_llm(entries))
    print("\nSearch Query:", state.get_search_query(entries))
