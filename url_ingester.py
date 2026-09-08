import requests
from bs4 import BeautifulSoup
import urllib.parse
import time

def extract_text_from_html(html_content):
    """
    Cleans up HTML and extracts raw page content.
    Strips out headers, footers, scripts, and styling tags.
    """
    soup = BeautifulSoup(html_content, 'html.parser')
    
    # 1. Strip unwanted tags
    for s in soup(['script', 'style', 'nav', 'footer', 'header', 'iframe', 'aside', 'noscript']):
        s.decompose()
        
    # Get Title
    title = soup.title.string.strip() if soup.title else "Untitled Page"
    
    # 2. Get text
    text = soup.get_text()
    
    # 3. Clean up whitespace
    lines = (line.strip() for line in text.splitlines())
    chunks = (phrase for line in lines for phrase in line.split("  "))
    cleaned_text = "\n".join(chunk for chunk in chunks if chunk)
    
    return title, cleaned_text

def fetch_url(url, timeout=10.0):
    """
    Fetches the HTML content of a URL.
    Returns: (title, text_content, domain, timestamp) or raises exception.
    """
    headers = {
        "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"
    }
    
    # Check scheme
    parsed_url = urllib.parse.urlparse(url)
    if not parsed_url.scheme or parsed_url.scheme not in ('http', 'https'):
        raise ValueError("Invalid URL scheme. Only HTTP and HTTPS are supported.")
        
    response = requests.get(url, headers=headers, timeout=timeout)
    response.raise_for_status()
    
    # Check content type
    content_type = response.headers.get('Content-Type', '')
    if 'text/html' not in content_type and 'application/xhtml+xml' not in content_type:
        # If it's plain text, return it directly
        if 'text/plain' in content_type:
            title = parsed_url.netloc
            return title, response.text.strip(), parsed_url.netloc, time.time()
        raise ValueError(f"URL did not return HTML or Text (returned: {content_type})")
        
    title, text = extract_text_from_html(response.text)
    return title, text, parsed_url.netloc, time.time()

if __name__ == "__main__":
    # Test ingester with mock HTML
    mock_html = """
    <html>
      <head><title>Test Page</title></head>
      <body>
        <nav><a href="/home">Home</a></nav>
        <h1>Main Heading</h1>
        <p>This is a paragraph of text explaining the teleprompter RAG indexing system.</p>
        <script>console.log("hello");</script>
        <footer>Copyright 2026</footer>
      </body>
    </html>
    """
    print("Testing HTML extraction...")
    title, text = extract_text_from_html(mock_html)
    print(f"Title: {title}")
    print(f"Content:\n{text}")
