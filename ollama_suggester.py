"""
Ollama Suggester — route-specific prompts with cancellation support.
Each route has its own system prompt and output format.
"""
import re
import threading
import requests


# ── Route-specific system prompts ─────────────────────────────────────────────
ROUTE_PROMPTS = {
    "concise": (
        "You are a real-time meeting teleprompter. Your job is to help [Local] "
        "in a meeting by providing short, actionable suggestions.\n\n"
        "You will receive:\n"
        "1. GROUNDING CONTEXT — reference material from the knowledge base. "
        "Use this as background info only.\n"
        "2. MEETING TRANSCRIPT — what has been said so far.\n\n"
        "IMPORTANT: The transcript may contain noise, repeated words, or "
        "hallucinated text. Focus ONLY on the meaningful sentences. Ignore "
        "any lines that are just repeated words (like 'Azure Azure Azure' "
        "or 'all right all right'). If no meaningful content exists in the "
        "transcript, say 'Waiting for meaningful conversation to begin.'\n\n"
        "Based on the actual conversation topic, suggest 1-2 concise "
        "actionable points for [Local].\n"
        "Rules:\n"
        "- Start directly with bullet points, no preamble.\n"
        "- Each bullet: 1-2 sentences, under 25 words.\n"
        "- ONLY cite a source if you actually used a specific fact from it, "
        "e.g. (sql_reference.md). Do NOT add [source_name] as placeholder.\n"
        "- If no relevant grounding context exists, give advice from general "
        "knowledge about the actual topic being discussed.\n"
        "- Keep total output under 80 words."
    ),
    "evidence": (
        "You are a meeting evidence assistant. Your job is to provide factual "
        "support for or against what was said in the meeting.\n\n"
        "IMPORTANT: Ignore any transcript lines that are just repeated noise "
        "(e.g. 'Azure Azure Azure'). Focus only on meaningful sentences.\n\n"
        "Provide 2-3 supporting facts with specific details.\n"
        "Rules:\n"
        "- Start with '## Supporting Evidence' header.\n"
        "- List 2-3 bullet points with specific facts, numbers, or definitions.\n"
        "- Cite a source only if you used a specific fact from it, e.g. "
        "(sql_reference.md). Do NOT use [source_name] as placeholder.\n"
        "- If no relevant facts can be found, say so clearly.\n"
        "- Keep under 120 words."
    ),
    "explain": (
        "You are a meeting explainer. Your job is to explain the current topic "
        "clearly so [Local] can understand and respond effectively.\n\n"
        "IMPORTANT: Ignore any transcript lines that are just repeated noise "
        "(e.g. 'Azure Azure Azure'). Focus only on meaningful sentences.\n\n"
        "Explain the topic in plain language.\n"
        "Rules:\n"
        "- Write 2-4 sentences (max 80 words).\n"
        "- Use grounding context as background info, not the primary source.\n"
        "- Avoid jargon unless you define it.\n"
        "- End with 'Key point: ...' summarizing the most important takeaway.\n"
        "- If no meaningful topic exists, say 'Waiting for discussion topic.'"
    ),
    "verify": (
        "You are a fact-checker for a meeting. Your job is to verify claims "
        "made during the conversation.\n\n"
        "IMPORTANT: Ignore any transcript lines that are just repeated noise "
        "(e.g. 'Azure Azure Azure'). Focus only on meaningful sentences.\n\n"
        "Evaluate the most recent meaningful claim.\n"
        "Rules:\n"
        "- Start with one of: **VERIFIED**, **UNCERTAIN**, or **NEEDS REVIEW**\n"
        "- Then: 1-2 sentences explaining your assessment.\n"
        "- Cite sources only if you used a specific fact, e.g. (cloud_devops.md).\n"
        "- If no meaningful claims found, say 'No verifiable claims in transcript.'\n"
        "- Keep under 70 words."
    ),
    "search": (
        "You are a knowledge retrieval assistant. Your job is to summarize what "
        "the indexed knowledge sources say about the current topic.\n\n"
        "Summarize the relevant findings from grounding context.\n"
        "Rules:\n"
        "- Start with '## From Your Sources' header.\n"
        "- List 2-3 key findings, citing source name in parentheses.\n"
        "- If no relevant context exists, say 'No relevant sources found.'\n"
        "- Keep under 100 words."
    ),
    "followup": (
        "You are a meeting follow-up generator. Your job is to suggest questions "
        "[Local] should ask to get more clarity or make better decisions.\n\n"
        "IMPORTANT: Ignore any transcript lines that are just repeated noise "
        "(e.g. 'Azure Azure Azure'). Focus only on meaningful sentences.\n\n"
        "Suggest 2-3 clarifying questions.\n"
        "Rules:\n"
        "- Number the questions: 1. 2. 3.\n"
        "- Each question should seek a specific fact, decision, or next step.\n"
        "- If no meaningful discussion exists, suggest general opening questions.\n"
        "- Keep under 70 words."
    ),
}

DEFAULT_ROUTE = "concise"


class OllamaSuggester:
    def __init__(self, model_name="gemma2:2b", host="http://localhost:11434"):
        self.model_name = model_name
        self.host = host
        self.generate_url = f"{host}/api/generate"
        self.system_prompt = ROUTE_PROMPTS["concise"]

    @staticmethod
    def _clean_suggestion_text(text):
        """Remove literal [source_name] artifacts and clean up LLM output."""
        if not text:
            return text
        # Remove literal [source_name] and variations — LLM output artifact
        text = re.sub(r'\[source_name\]', '', text, flags=re.IGNORECASE)
        text = re.sub(r'\[source\]', '', text, flags=re.IGNORECASE)
        text = re.sub(r'\[.*?_name\]', '', text, flags=re.IGNORECASE)
        # Remove empty bullet lines left after cleanup
        text = re.sub(r'^\s*[-*]\s*$', '', text, flags=re.MULTILINE)
        # Collapse multiple blank lines
        text = re.sub(r'\n{3,}', '\n\n', text)
        return text.strip()

    def _build_prompt(self, route, transcript_context, context_chunks=None, source_type="knowledge_base"):
        system = ROUTE_PROMPTS.get(route, ROUTE_PROMPTS[DEFAULT_ROUTE])

        context_str = ""
        if context_chunks:
            formatted = []
            for chunk in context_chunks:
                snippet = chunk.get("text", "")[:400]
                formatted.append(f"[Source: {chunk.get('source', 'unknown')}]\n{snippet}")
            context_str = "GROUNDING CONTEXT:\n" + "\n\n".join(formatted) + "\n\n"
        else:
            context_str = "GROUNDING CONTEXT: (none available — use general knowledge)\n\n"

        # Source type transparency
        source_note = ""
        if source_type == "general_knowledge":
            source_note = (
                "IMPORTANT: No indexed source or web result was found for this topic. "
                "Answer from your general knowledge. At the end of your response, add: "
                "'Note: This is from general knowledge, not from an indexed source.'\n\n"
            )
        elif source_type == "web_search":
            source_note = (
                "Note: This answer is grounded in web search results, not your local "
                "knowledge base. Cite the web source name shown in the context.\n\n"
            )

        return (
            f"{source_note}{system}\n\n"
            f"{context_str}"
            f"MEETING TRANSCRIPT:\n{transcript_context}\n\n"
            f"YOUR RESPONSE:"
        )

    def generate_suggestions(self, transcript_history, context_chunks=None, cancel_event=None):
        """
        Generate a suggestion for the default 'concise' route.
        cancel_event: threading.Event — if set, generation is cancelled early.
        """
        return self.generate_for_route(
            DEFAULT_ROUTE, transcript_history, context_chunks, cancel_event
        )

    def generate_for_route(self, route, transcript_history, context_chunks=None, cancel_event=None, source_type="knowledge_base"):
        """
        Generate a suggestion for a specific route.
        Returns (text, sources_used) tuple.
        cancel_event: threading.Event — if set, abort early.
        source_type: 'knowledge_base', 'web_search', or 'general_knowledge'
        """
        if not transcript_history:
            return ("", [])

        # Build transcript context
        if isinstance(transcript_history, list):
            if transcript_history and isinstance(transcript_history[0], dict):
                lines = []
                for e in transcript_history:
                    speaker = e.get("speaker", "unknown")
                    text = e.get("text", "")
                    label = "Local" if speaker == "local" else "Remote"
                    lines.append(f"[{label}]: {text}")
                transcript_str = "\n".join(lines[-5:])
            else:
                lines = []
                for speaker, text in transcript_history:
                    lines.append(f"{speaker}: {text}")
                transcript_str = "\n".join(lines[-5:])
        else:
            transcript_str = str(transcript_history)

        prompt = self._build_prompt(route, transcript_str, context_chunks, source_type)

        payload = {
            "model": self.model_name,
            "prompt": prompt,
            "stream": False,
            "options": {
                "temperature": 0.4,
                "top_p": 0.9,
                "num_predict": 200  # Allow longer, more substantive answers
            }
        }

        # Check cancellation before starting
        if cancel_event and cancel_event.is_set():
            return ("", [])

        try:
            response = requests.post(
                self.generate_url, json=payload, timeout=60.0
            )

            # Check cancellation after request
            if cancel_event and cancel_event.is_set():
                return ("", [])

            if response.status_code == 200:
                result = response.json()
                text = self._clean_suggestion_text(result.get("response", "").strip())

                sources_used = []
                if context_chunks:
                    seen = set()
                    for c in context_chunks:
                        src = c.get("source", "")
                        if src and src not in seen:
                            seen.add(src)
                            sources_used.append(src)

                return (text, sources_used)
            else:
                return (f"Error: Ollama returned status {response.status_code}", [])

        except requests.exceptions.RequestException as e:
            return (f"Error connecting to Ollama: {e}", [])


if __name__ == "__main__":
    suggester = OllamaSuggester()
    test_history = [
        {"speaker": "remote", "text": "We need to decide on the database for our local meeting app."},
        {"speaker": "local", "text": "I was thinking SQLite with vector search could work."},
        {"speaker": "remote", "text": "Is SQLite fast enough for real-time vector search on CPU?"},
    ]

    for route in ROUTE_PROMPTS:
        print(f"\n--- Route: {route} ---")
        text, sources = suggester.generate_for_route(route, test_history)
        print(f"Text: {text}")
        print(f"Sources: {sources}")
