import os
import zipfile
import xml.etree.ElementTree as ET

# Attempt to load pdf reader
try:
    import pypdfium2 as pdfium
except ImportError:
    pdfium = None

def parse_docx(file_path):
    """
    Parses paragraphs from a Word document in a self-contained way
    by extracting text runs from the underlying XML zip structure.
    """
    paragraphs = []
    try:
        with zipfile.ZipFile(file_path) as docx:
            xml_content = docx.read('word/document.xml')
            root = ET.fromstring(xml_content)
            # Find all paragraph tags {http://schemas.openxmlformats.org/wordprocessingml/2006/main}p
            for paragraph in root.iter('{http://schemas.openxmlformats.org/wordprocessingml/2006/main}p'):
                # Extract all text run tags {http://schemas.openxmlformats.org/wordprocessingml/2006/main}t
                p_text = "".join(node.text for node in paragraph.iter('{http://schemas.openxmlformats.org/wordprocessingml/2006/main}t') if node.text)
                if p_text.strip():
                    paragraphs.append(p_text.strip())
        return "\n".join(paragraphs)
    except Exception as e:
        print(f"Error parsing DOCX file {file_path}: {e}")
        return ""

def parse_pdf(file_path):
    """
    Extracts text from a PDF file using pypdfium2.
    """
    if pdfium is None:
        print("Warning: pypdfium2 is not installed. PDF parsing is disabled.")
        return ""
    try:
        pdf = pdfium.PdfDocument(file_path)
        pages_text = []
        for page in pdf:
            textpage = page.get_textpage()
            text = textpage.get_text_range()
            if text:
                pages_text.append(text)
        return "\n".join(pages_text)
    except Exception as e:
        print(f"Error parsing PDF file {file_path}: {e}")
        return ""

def parse_text(file_path):
    """
    Reads plain text or Markdown files.
    """
    try:
        with open(file_path, 'r', encoding='utf-8', errors='ignore') as f:
            return f.read()
    except Exception as e:
        print(f"Error reading text file {file_path}: {e}")
        return ""

def get_document_chunks(file_path, chunk_size=500, overlap=100):
    """
    Parses a document based on its extension, chunks it, and returns the chunks list.
    Each chunk is a dictionary containing:
        'text': the text chunk,
        'source': the file path / source label
    """
    ext = os.path.splitext(file_path)[1].lower()
    full_text = ""
    
    if ext in ('.txt', '.md', '.markdown'):
        full_text = parse_text(file_path)
    elif ext == '.pdf':
        full_text = parse_pdf(file_path)
    elif ext == '.docx':
        full_text = parse_docx(file_path)
    else:
        print(f"Unsupported file format: {ext} for file {file_path}")
        return []
        
    if not full_text.strip():
        return []
        
    # Word-boundary aware chunking
    chunks = []
    start = 0
    text_len = len(full_text)
    
    while start < text_len:
        end = start + chunk_size
        
        # Land on a space to avoid truncating words
        if end < text_len:
            space_idx = full_text.rfind(' ', end - 50, end)
            if space_idx != -1:
                end = space_idx
                
        chunk = full_text[start:end].strip()
        if chunk:
            chunks.append({
                "text": chunk,
                "source": os.path.basename(file_path)
            })
            
        start = end - overlap
        if start >= text_len or chunk_size <= overlap:
            break
            
        # Adjust start forward to a space
        if start > 0:
            space_idx = full_text.find(' ', start, start + 50)
            if space_idx != -1:
                start = space_idx + 1
                
    return chunks

if __name__ == "__main__":
    # Test parser
    test_file = "test_doc.md"
    with open(test_file, "w", encoding="utf-8") as f:
        f.write("# Project Antigravity\n\nThis is a local meeting teleprompter project. It listens to audio streams and transcribes them. Then it uses Gemma to generate suggestions.\n\nSettings:\n- ASR: faster-whisper\n- LLM: gemma2:2b\n- Database: SQLite + ChromaDB")
        
    print("Testing document chunker...")
    chunks = get_document_chunks(test_file, chunk_size=100, overlap=20)
    for i, chunk in enumerate(chunks):
        print(f"\nChunk [{i}] from {chunk['source']}:")
        print(f"---{chunk['text']}---")
        
    os.remove(test_file)
