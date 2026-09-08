import os
import shutil
import time
from knowledge_base import KnowledgeBase
from ollama_suggester import OllamaSuggester

def main():
    print("=== Start RAG Verification ===")
    
    # 1. Setup paths
    kb_dir = "knowledge_base"
    os.makedirs(kb_dir, exist_ok=True)
    test_doc = os.path.join(kb_dir, "project_rules.md")
    
    # 2. Write sample grounded content
    content = (
        "# Antigravity Project Rules\n\n"
        "Here are the guidelines for the team:\n"
        "- Rule 1: The official mascot of Project Antigravity is a floating green apple named Gravity.\n"
        "- Rule 2: The project codebase must follow Python PEP 8 conventions.\n"
        "- Rule 3: All tests should run under 2 seconds.\n"
    )
    with open(test_doc, "w", encoding="utf-8") as f:
        f.write(content)
        
    print(f"Created sample document: {test_doc}")
    
    # 3. Initialize KB and Sync
    print("Initializing KnowledgeBase...")
    kb = KnowledgeBase(db_path="kb_verify.db", chroma_path="chroma_verify_db")
    
    print("Syncing folder...")
    kb.sync_folder(kb_dir)
    
    # 4. Perform Search Test
    query = "What is the mascot of Antigravity?"
    print(f"\nSearching database for: '{query}'")
    search_results = kb.search(query, limit=2)
    
    print("Search Results:")
    for idx, r in enumerate(search_results):
        print(f"[{idx}] Source: {r['source']} | Path: {r['path']}")
        print(f"    Text: {r['text']}")
        
    if not search_results:
        print("ERROR: No search results returned from database!")
        cleanup(test_doc, kb_dir)
        return
        
    # 5. Query Ollama/Gemma with Grounding Context
    print("\nQuerying Ollama (gemma2:2b) with grounding context...")
    suggester = OllamaSuggester(model_name="gemma2:2b")
    
    transcript_history = [
        ("[Remote]", "Can you tell me about the rules of the project? What is the mascot?"),
        ("[Local]", "Sure, let me check the documentation.")
    ]
    
    start_time = time.time()
    suggestions = suggester.generate_suggestions(transcript_history, context_chunks=search_results)
    latency = time.time() - start_time
    
    print(f"Response generated in {latency:.2f} seconds.")
    print("\n=== Suggestions Output ===")
    print(suggestions)
    print("===========================")
    
    # 6. Verification assertions
    print("\n=== Verification Check ===")
    lower_sug = suggestions.lower()
    mascot_found = "mascot" in lower_sug or "gravity" in lower_sug or "floating green apple" in lower_sug
    citation_found = "project_rules.md" in lower_sug
    
    if mascot_found:
        print("[OK] SUCCESS: Mascot information was correctly found in Gemma's output!")
    else:
        print("[FAIL] FAIL: Gemma did not mention the mascot from the context.")
        
    if citation_found:
        print("[OK] SUCCESS: Citation '[project_rules.md]' was correctly found in Gemma's output!")
    else:
        print("[FAIL] FAIL: Gemma did not include the source citation.")
        
    # Teardown
    cleanup(test_doc, kb_dir)
    
def cleanup(test_doc, kb_dir):
    print("\nCleaning up verification databases...")
    try:
        if os.path.exists(test_doc):
            os.remove(test_doc)
    except Exception as e:
        print(f"Could not remove test doc: {e}")
        
    # Remove verify database files
    try:
        if os.path.exists("kb_verify.db"):
            os.remove("kb_verify.db")
    except Exception as e:
        print(f"Could not remove SQLite verify database: {e}")
        
    # Remove verify chroma folder
    try:
        if os.path.exists("chroma_verify_db"):
            shutil.rmtree("chroma_verify_db", ignore_errors=True)
    except Exception as e:
        print(f"Could not remove Chroma verify database: {e}")
        
    print("Teardown completed.")

if __name__ == "__main__":
    main()
