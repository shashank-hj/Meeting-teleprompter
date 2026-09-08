"""
Web Search Module — DuckDuckGo search with caching.
Provides fallback retrieval when local knowledge base has no results.
"""
import time
import re
import threading
import requests
from urllib.parse import quote_plus


# ── Rate limiting ────────────────────────────────────────────────────────────
_last_search_time = 0.0
_search_lock = threading.Lock()
MIN_SEARCH_INTERVAL = 30.0  # seconds between web searches

# ── Result cache (in-memory, TTL-based) ──────────────────────────────────────
_cache = {}  # key: query_lower -> {text, url, title, fetched_at}
_cache_lock = threading.Lock()
CACHE_TTL = 3600  # 1 hour


def _cache_get(query):
    """Get cached result if still valid."""
    key = query.lower().strip()
    with _cache_lock:
        if key in _cache:
            entry = _cache[key]
            if time.time() - entry["fetched_at"] < CACHE_TTL:
                return entry
            else:
                del _cache[key]
    return None


def _cache_set(query, text, url, title):
    """Store result in cache."""
    key = query.lower().strip()
    with _cache_lock:
        _cache[key] = {
            "text": text,
            "url": url,
            "title": title,
            "fetched_at": time.time(),
        }
        # Evict oldest if cache too large
        if len(_cache) > 50:
            oldest_key = min(_cache, key=lambda k: _cache[k]["fetched_at"])
            del _cache[oldest_key]


def search_web(query, num_results=3):
    """
    Search DuckDuckGo lite and return results.
    Returns list of {title, url, snippet} dicts.
    """
    global _last_search_time

    if not query or not query.strip():
        return []

    # Rate limit
    with _search_lock:
        elapsed = time.time() - _last_search_time
        if elapsed < MIN_SEARCH_INTERVAL:
            print(f"[WEB SEARCH] Rate limited, waiting {MIN_SEARCH_INTERVAL - elapsed:.0f}s...")
            time.sleep(MIN_SEARCH_INTERVAL - elapsed)

    # Check cache first
    cached = _cache_get(query)
    if cached:
        print(f"[WEB SEARCH] Cache hit for '{query[:40]}...'")
        return [{"title": cached["title"], "url": cached["url"], "snippet": cached["text"][:200]}]

    try:
        url = "https://lite.duckduckgo.com/lite/"
        headers = {
            "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36"
        }
        # POST method required (GET triggers bot detection)
        response = requests.post(url, data={"q": query}, headers=headers, timeout=10.0)
        response.raise_for_status()

        results = _parse_ddg_lite(response.text, num_results)

        with _search_lock:
            _last_search_time = time.time()

        print(f"[WEB SEARCH] '{query[:40]}...' -> {len(results)} results")
        return results

    except Exception as e:
        print(f"[WEB SEARCH] Error: {e}")
        return []


def _parse_ddg_lite(html, num_results=3):
    """Parse DuckDuckGo lite HTML response."""
    from bs4 import BeautifulSoup

    soup = BeautifulSoup(html, "html.parser")
    results = []

    # DDG lite uses <a class="result-link"> for result titles and URLs
    result_links = soup.find_all("a", class_="result-link")
    for link in result_links:
        if len(results) >= num_results:
            break
        title = link.get_text(strip=True)
        href = link.get("href", "")
        if not title or not href or not href.startswith("http"):
            continue

        # Get snippet from the next sibling element
        snippet = ""
        next_el = link.find_next_sibling()
        if next_el:
            snippet = next_el.get_text(strip=True)[:300]

        results.append({"title": title, "url": href, "snippet": snippet})

    # Fallback: try any <a> with http href if no results found
    if not results:
        for link in soup.find_all("a", href=True):
            href = link.get("href", "")
            title = link.get_text(strip=True)
            if title and href.startswith("http") and len(title) > 5:
                results.append({"title": title, "url": href, "snippet": ""})
                if len(results) >= num_results:
                    break

    return results


def fetch_top_result(url, max_chars=3000):
    """
    Fetch a URL and extract clean text content.
    Returns truncated text suitable for LLM context.
    """
    from url_ingester import extract_text_from_html

    try:
        headers = {
            "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36"
        }
        response = requests.get(url, headers=headers, timeout=10.0)
        response.raise_for_status()

        content_type = response.headers.get("Content-Type", "")
        if "text/html" in content_type or "application/xhtml" in content_type:
            title, text = extract_text_from_html(response.text)
        elif "text/plain" in content_type:
            title = url
            text = response.text
        else:
            return None

        # Truncate to max_chars
        if len(text) > max_chars:
            text = text[:max_chars].rsplit(" ", 1)[0] + "..."

        return text.strip()

    except Exception as e:
        print(f"[WEB FETCH] Error fetching {url}: {e}")
        return None


def search_and_fetch(query, num_results=3):
    """
    Search DuckDuckGo and fetch the top result's content.
    Returns (text, url, title) or (None, None, None) if nothing found.
    """
    results = search_web(query, num_results=num_results)
    if not results:
        return (None, None, None)

    # Try each result until we get usable text
    for result in results:
        url = result.get("url", "")
        title = result.get("title", "")
        if not url:
            continue

        text = fetch_top_result(url)
        if text and len(text) > 100:  # Minimum viable content
            # Cache the result
            _cache_set(query, text, url, title)
            return (text, url, title)

    return (None, None, None)


if __name__ == "__main__":
    print("Testing web search...")
    query = "HIPAA compliance requirements"
    print(f"Searching: '{query}'")
    results = search_web(query, num_results=2)
    for r in results:
        print(f"  [{r['title']}] {r['url']}")
        print(f"    {r['snippet'][:100]}...")

    if results:
        print(f"\nFetching top result: {results[0]['url']}")
        text = fetch_top_result(results[0]["url"], max_chars=500)
        if text:
            print(f"  Got {len(text)} chars: {text[:200]}...")
        else:
            print("  Failed to fetch content")
