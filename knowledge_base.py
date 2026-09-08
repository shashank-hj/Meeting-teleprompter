import os
import sqlite3
import hashlib
import time
import chromadb
from document_parser import get_document_chunks
from url_ingester import fetch_url

class KnowledgeBase:
    def __init__(self, db_path="knowledge.db", chroma_path="chroma_db"):
        self.db_path = db_path
        self.chroma_path = chroma_path
        
        # 1. Initialize SQLite Database
        self._init_sqlite()
        
        # 2. Initialize Chroma Client
        self.chroma_client = chromadb.PersistentClient(path=chroma_path)
        self.collection = self.chroma_client.get_or_create_collection(
            name="meeting_teleprompter_knowledge"
        )

    def _init_sqlite(self):
        conn = sqlite3.connect(self.db_path)
        c = conn.cursor()
        
        # Create sources table
        c.execute("""
            CREATE TABLE IF NOT EXISTS sources (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                source_type TEXT NOT NULL, -- 'file', 'url', or 'web_cache'
                source_path TEXT NOT NULL UNIQUE, -- file path or URL
                title TEXT,
                last_updated REAL,
                content_hash TEXT,
                cache_ttl REAL DEFAULT NULL  -- expiry timestamp for web_cache entries
            )
        """)
        
        # Add cache_ttl column if missing (migration for existing DBs)
        try:
            c.execute("ALTER TABLE sources ADD COLUMN cache_ttl REAL DEFAULT NULL")
        except sqlite3.OperationalError:
            pass  # Column already exists
        
        # Create chunks table
        c.execute("""
            CREATE TABLE IF NOT EXISTS chunks (
                id TEXT PRIMARY KEY, -- matches ChromaDB chunk ID
                source_id INTEGER,
                chunk_index INTEGER,
                text TEXT,
                FOREIGN KEY(source_id) REFERENCES sources(id) ON DELETE CASCADE
            )
        """)
        
        # Enable FTS5 virtual table for keyword search
        try:
            c.execute("""
                CREATE VIRTUAL TABLE IF NOT EXISTS chunks_fts USING fts5(
                    chunk_id,
                    text
                )
            """)
            self.fts_supported = True
        except sqlite3.OperationalError:
            print("Warning: FTS5 not supported by this SQLite library. Falling back to simple LIKE queries.")
            self.fts_supported = False
            
        conn.commit()
        conn.close()

    def _get_db_connection(self):
        conn = sqlite3.connect(self.db_path)
        conn.row_factory = sqlite3.Row
        return conn

    def _compute_hash(self, content_str):
        return hashlib.md5(content_str.encode('utf-8', errors='ignore')).hexdigest()

    def remove_source(self, source_path):
        """
        Removes a source and all its associated chunks from SQLite and ChromaDB.
        """
        conn = self._get_db_connection()
        c = conn.cursor()
        
        c.execute("SELECT id FROM sources WHERE source_path = ?", (source_path,))
        row = c.fetchone()
        if not row:
            conn.close()
            return
            
        source_id = row['id']
        
        # Get chunk IDs to delete from Chroma
        c.execute("SELECT id FROM chunks WHERE source_id = ?", (source_id,))
        chunk_rows = c.fetchall()
        chunk_ids = [r['id'] for r in chunk_rows]
        
        # Delete from Chroma
        if chunk_ids:
            try:
                self.collection.delete(ids=chunk_ids)
            except Exception as e:
                print(f"Error deleting from Chroma collection: {e}")
                
        # Delete FTS indices
        if self.fts_supported and chunk_ids:
            # Rebuilding or deleting FTS depends on content mapping.
            # In FTS5, deleting from content table triggers delete if mapped, 
            # otherwise we clean it manually:
            for cid in chunk_ids:
                c.execute("DELETE FROM chunks_fts WHERE chunk_id = ?", (cid,))
                
        # Delete from SQLite (Cascades to chunks table)
        c.execute("DELETE FROM chunks WHERE source_id = ?", (source_id,))
        c.execute("DELETE FROM sources WHERE id = ?", (source_id,))
        
        conn.commit()
        conn.close()
        print(f"Removed source index: {source_path}")

    def add_file(self, file_path):
        """
        Indexes a file if it has changed or is new.
        """
        if not os.path.exists(file_path):
            print(f"File not found: {file_path}")
            return
            
        # Parse and chunk
        chunks = get_document_chunks(file_path)
        if not chunks:
            return
            
        content_str = "".join([c['text'] for c in chunks])
        content_hash = self._compute_hash(content_str)
        mtime = os.path.getmtime(file_path)
        
        # Check database
        conn = self._get_db_connection()
        c = conn.cursor()
        c.execute("SELECT id, content_hash FROM sources WHERE source_path = ?", (file_path,))
        row = c.fetchone()
        
        if row:
            if row['content_hash'] == content_hash:
                # File is unchanged
                conn.close()
                return
            else:
                # File changed, delete old indexes first
                conn.close()
                self.remove_source(file_path)
                conn = self._get_db_connection()
                c = conn.cursor()
                
        # Insert source metadata
        c.execute(
            "INSERT INTO sources (source_type, source_path, title, last_updated, content_hash) VALUES (?, ?, ?, ?, ?)",
            ("file", file_path, os.path.basename(file_path), mtime, content_hash)
        )
        source_id = c.lastrowid
        
        chroma_ids = []
        chroma_texts = []
        chroma_metadatas = []
        
        for idx, chunk in enumerate(chunks):
            chunk_id = f"file_{source_id}_chunk_{idx}"
            text = chunk['text']
            
            # Store in SQLite
            c.execute(
                "INSERT INTO chunks (id, source_id, chunk_index, text) VALUES (?, ?, ?, ?)",
                (chunk_id, source_id, idx, text)
            )
            
            # Store in FTS5
            if self.fts_supported:
                c.execute(
                    "INSERT INTO chunks_fts (chunk_id, text) VALUES (?, ?)",
                    (chunk_id, text)
                )
                
            # Collect for Chroma batch
            chroma_ids.append(chunk_id)
            chroma_texts.append(text)
            chroma_metadatas.append({
                "source": os.path.basename(file_path),
                "type": "file",
                "path": file_path
            })
            
        # Write to databases
        conn.commit()
        conn.close()
        
        try:
            self.collection.add(
                ids=chroma_ids,
                documents=chroma_texts,
                metadatas=chroma_metadatas
            )
            print(f"Indexed file: {os.path.basename(file_path)} ({len(chunks)} chunks)")
        except Exception as e:
            print(f"Error indexing to Chroma collection: {e}")

    def add_url(self, url):
        """
        Indexes a URL page.
        """
        try:
            title, text_content, domain, timestamp = fetch_url(url)
        except Exception as e:
            print(f"Error fetching URL {url}: {e}")
            return
            
        if not text_content.strip():
            return
            
        content_hash = self._compute_hash(text_content)
        
        # Check database
        conn = self._get_db_connection()
        c = conn.cursor()
        c.execute("SELECT id, content_hash FROM sources WHERE source_path = ?", (url,))
        row = c.fetchone()
        
        if row:
            if row['content_hash'] == content_hash:
                # URL content unchanged
                conn.close()
                return
            else:
                conn.close()
                self.remove_source(url)
                conn = self._get_db_connection()
                c = conn.cursor()
                
        # Insert source
        c.execute(
            "INSERT INTO sources (source_type, source_path, title, last_updated, content_hash) VALUES (?, ?, ?, ?, ?)",
            ("url", url, title, timestamp, content_hash)
        )
        source_id = c.lastrowid
        
        # Simple word-boundary chunking for URL text
        # Splitting manually to bypass get_document_chunks file check
        chunks = []
        chunk_size = 500
        overlap = 100
        words = text_content.split()
        
        i = 0
        while i < len(words):
            chunk_words = words[i:i + chunk_size // 5] # estimate words
            chunk = " ".join(chunk_words).strip()
            if chunk:
                chunks.append(chunk)
            i += len(chunk_words) - overlap // 5
            if len(chunk_words) < chunk_size // 5:
                break
                
        chroma_ids = []
        chroma_texts = []
        chroma_metadatas = []
        
        for idx, chunk_text in enumerate(chunks):
            chunk_id = f"url_{source_id}_chunk_{idx}"
            
            # Store in SQLite
            c.execute(
                "INSERT INTO chunks (id, source_id, chunk_index, text) VALUES (?, ?, ?, ?)",
                (chunk_id, source_id, idx, chunk_text)
            )
            
            if self.fts_supported:
                c.execute(
                    "INSERT INTO chunks_fts (chunk_id, text) VALUES (?, ?)",
                    (chunk_id, chunk_text)
                )
                
            chroma_ids.append(chunk_id)
            chroma_texts.append(chunk_text)
            chroma_metadatas.append({
                "source": domain,
                "type": "url",
                "path": url
            })
            
        conn.commit()
        conn.close()
        
        try:
            self.collection.add(
                ids=chroma_ids,
                documents=chroma_texts,
                metadatas=chroma_metadatas
            )
            print(f"Indexed URL: {url} ({len(chunks)} chunks)")
        except Exception as e:
            print(f"Error indexing URL to Chroma collection: {e}")

    def add_web_cache(self, url, title, text, ttl_seconds=3600):
        """
        Cache a web search result in the KB with a TTL.
        These entries are automatically cleaned up after expiry.
        """
        if not text or not text.strip():
            return

        content_hash = self._compute_hash(text)
        cache_ttl = time.time() + ttl_seconds

        # Check if already cached
        conn = self._get_db_connection()
        c = conn.cursor()
        c.execute("SELECT id, content_hash FROM sources WHERE source_path = ?", (url,))
        row = c.fetchone()

        if row:
            if row["content_hash"] == content_hash:
                # Update TTL
                c.execute("UPDATE sources SET cache_ttl = ? WHERE id = ?", (cache_ttl, row["id"]))
                conn.commit()
                conn.close()
                return
            else:
                conn.close()
                self.remove_source(url)
                conn = self._get_db_connection()
                c = conn.cursor()

        # Insert source
        c.execute(
            "INSERT INTO sources (source_type, source_path, title, last_updated, content_hash, cache_ttl) "
            "VALUES (?, ?, ?, ?, ?, ?)",
            ("web_cache", url, title, time.time(), content_hash, cache_ttl)
        )
        source_id = c.lastrowid

        # Chunk the text
        chunk_size = 500
        overlap = 100
        words = text.split()
        chunks = []
        i = 0
        while i < len(words):
            chunk_words = words[i:i + chunk_size // 5]
            chunk = " ".join(chunk_words).strip()
            if chunk:
                chunks.append(chunk)
            i += len(chunk_words) - overlap // 5
            if len(chunk_words) < chunk_size // 5:
                break

        chroma_ids = []
        chroma_texts = []
        chroma_metadatas = []

        for idx, chunk_text in enumerate(chunks):
            chunk_id = f"cache_{source_id}_chunk_{idx}"

            c.execute(
                "INSERT INTO chunks (id, source_id, chunk_index, text) VALUES (?, ?, ?, ?)",
                (chunk_id, source_id, idx, chunk_text)
            )

            if self.fts_supported:
                c.execute(
                    "INSERT INTO chunks_fts (chunk_id, text) VALUES (?, ?)",
                    (chunk_id, chunk_text)
                )

            chroma_ids.append(chunk_id)
            chroma_texts.append(chunk_text)
            chroma_metadatas.append({
                "source": title or url,
                "type": "web_cache",
                "path": url
            })

        conn.commit()
        conn.close()

        try:
            self.collection.add(
                ids=chroma_ids,
                documents=chroma_texts,
                metadatas=chroma_metadatas
            )
            print(f"[KB] Cached web result: {title or url} ({len(chunks)} chunks, TTL={ttl_seconds}s)")
        except Exception as e:
            print(f"[KB] Error caching web result: {e}")

    def cleanup_expired_cache(self):
        """Remove expired web cache entries."""
        now = time.time()
        conn = self._get_db_connection()
        c = conn.cursor()
        c.execute(
            "SELECT id, source_path FROM sources WHERE source_type = 'web_cache' AND cache_ttl IS NOT NULL AND cache_ttl < ?",
            (now,)
        )
        expired = c.fetchall()
        conn.close()

        for row in expired:
            self.remove_source(row["source_path"])

        if expired:
            print(f"[KB] Cleaned up {len(expired)} expired web cache entries")

    def sync_folder(self, folder_path):
        """
        Ingests all supported files in folder, and removes indexed files
        that have been deleted.
        """
        if not os.path.exists(folder_path):
            os.makedirs(folder_path)
            
        supported_exts = ('.txt', '.md', '.markdown', '.pdf', '.docx')
        folder_files = []
        
        # Scan folder
        for root, dirs, files in os.walk(folder_path):
            for file in files:
                if file.lower().endswith(supported_exts):
                    full_path = os.path.join(root, file)
                    folder_files.append(os.path.abspath(full_path))
                    
        # Add or update files
        for fp in folder_files:
            self.add_file(fp)
            
        # Clean up deleted files from index
        conn = self._get_db_connection()
        c = conn.cursor()
        c.execute("SELECT source_path FROM sources WHERE source_type = 'file'")
        indexed_files = [r['source_path'] for r in c.fetchall()]
        conn.close()
        
        for fp in indexed_files:
            if fp not in folder_files:
                self.remove_source(fp)
                
        # Ingest URLs from urls.txt if present
        urls_file = os.path.join(folder_path, "urls.txt")
        active_urls = []
        if os.path.exists(urls_file):
            try:
                with open(urls_file, "r", encoding="utf-8", errors="ignore") as f:
                    for line in f:
                        url = line.strip()
                        if url and (url.startswith("http://") or url.startswith("https://")):
                            active_urls.append(url)
            except Exception as e:
                print(f"Error reading {urls_file}: {e}")
                
        # Index new/changed URLs
        for url in active_urls:
            self.add_url(url)
            
        # Clean up deleted URLs from index
        conn = self._get_db_connection()
        c = conn.cursor()
        c.execute("SELECT source_path FROM sources WHERE source_type = 'url'")
        indexed_urls = [r['source_path'] for r in c.fetchall()]
        conn.close()
        
        for url in indexed_urls:
            if url not in active_urls:
                self.remove_source(url)

    def search(self, query, limit=3):
        """
        Performs hybrid search (lexical FTS5 + ChromaDB vector search).
        Returns a list of unique grounded chunks.
        """
        if not query or not query.strip():
            return []
            
        results = []
        seen_ids = set()
        
        # 1. Semantic Vector Search (ChromaDB)
        try:
            vector_res = self.collection.query(
                query_texts=[query],
                n_results=limit
            )
            
            if vector_res and 'documents' in vector_res and vector_res['documents']:
                documents = vector_res['documents'][0]
                ids = vector_res['ids'][0]
                metadatas = vector_res['metadatas'][0]
                
                for idx, doc in enumerate(documents):
                    cid = ids[idx]
                    meta = metadatas[idx]
                    seen_ids.add(cid)
                    results.append({
                        "id": cid,
                        "text": doc,
                        "source": meta["source"],
                        "path": meta["path"],
                        "score": 1.0 # default weight
                    })
        except Exception as e:
            print(f"Error querying Chroma vector database: {e}")
            
        # 2. Keyword/Lexical Search (SQLite FTS5)
        fts_res = []
        if self.fts_supported:
            try:
                conn = self._get_db_connection()
                c = conn.cursor()
                # Query matching FTS5
                # Match queries with * wildcard
                search_query = " OR ".join(f"{word}*" for word in query.split() if len(word) > 1)
                if search_query:
                    c.execute("""
                        SELECT fts.chunk_id, fts.text, s.title, s.source_path 
                        FROM chunks_fts fts
                        JOIN chunks c ON c.id = fts.chunk_id
                        JOIN sources s ON s.id = c.source_id
                        WHERE chunks_fts MATCH ? 
                        LIMIT ?
                    """, (search_query, limit))
                    fts_res = c.fetchall()
                conn.close()
            except sqlite3.OperationalError:
                # Syntax error or other search issue
                pass
                
        # Parse lexical results
        for row in fts_res:
            cid = row['chunk_id']
            if cid not in seen_ids:
                seen_ids.add(cid)
                results.append({
                    "id": cid,
                    "text": row['text'],
                    "source": row['title'],
                    "path": row['source_path'],
                    "score": 0.8 # slightly lower lexical weight
                })
                
        return results[:limit]

if __name__ == "__main__":
    kb = KnowledgeBase(db_path="test_knowledge.db", chroma_path="test_chroma_db")
    
    # Write a test file
    test_dir = "./test_kb"
    os.makedirs(test_dir, exist_ok=True)
    test_file = os.path.join(test_dir, "meeting_tips.txt")
    with open(test_file, "w", encoding="utf-8") as f:
        f.write("Important tips for running meetings:\n"
                "- Keep agenda concise.\n"
                "- Action items should always have assignees.\n"
                "- Always use the Antigravity local meeting teleprompter assistant to take notes!")
                
    print("Syncing folder...")
    kb.sync_folder(test_dir)
    
    print("\nQuerying: 'Antigravity teleprompter'")
    search_res = kb.search("Antigravity teleprompter")
    for r in search_res:
        print(f"[{r['source']}] (Score: {r['score']}):")
        print(f"   {r['text']}")
        
    # Clean up test directories
    kb.remove_source(os.path.abspath(test_file))
    if os.path.exists(test_file):
        os.remove(test_file)
    os.rmdir(test_dir)
    
    # Delete test databases
    if os.path.exists("test_knowledge.db"):
        os.remove("test_knowledge.db")
    import shutil
    if os.path.exists("test_chroma_db"):
        shutil.rmtree("test_chroma_db", ignore_errors=True)
