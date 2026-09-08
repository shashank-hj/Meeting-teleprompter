"""
File Processor - Extracts audio from video/audio files and chunks it for transcription.
Uses pydub + imageio-ffmpeg (bundled ffmpeg) for broad format support.
"""
import os
import tempfile
import numpy as np

def get_ffmpeg_path():
    """Get ffmpeg binary path from imageio-ffmpeg."""
    try:
        import imageio_ffmpeg
        return imageio_ffmpeg.get_ffmpeg_exe()
    except ImportError:
        return "ffmpeg"

SUPPORTED_VIDEO = {".mp4", ".mkv", ".webm", ".avi", ".mov", ".flv", ".wmv", ".m4v"}
SUPPORTED_AUDIO = {".wav", ".mp3", ".ogg", ".flac", ".aac", ".wma", ".m4a"}
SUPPORTED_ALL = SUPPORTED_VIDEO | SUPPORTED_AUDIO

def extract_audio(file_path, target_sr=16000, chunk_duration_sec=15.0):
    """
    Extracts audio from a video or audio file.
    Returns a list of (speaker_tag, numpy_audio_array) tuples.
    
    For meeting videos: treats the whole file as "remote" audio.
    If the file is stereo with separate channels, tries to split.
    """
    from pydub import AudioSegment

    ext = os.path.splitext(file_path)[1].lower()
    if ext not in SUPPORTED_ALL:
        raise ValueError(f"Unsupported file format: {ext}")

    # Set ffmpeg path
    ffmpeg_path = get_ffmpeg_path()
    AudioSegment.converter = ffmpeg_path

    print(f"Loading file: {file_path}")
    audio = AudioSegment.from_file(file_path)

    # Convert to mono, target sample rate
    audio = audio.set_channels(1).set_frame_rate(target_sr)

    # Convert to numpy array
    samples = np.array(audio.get_array_of_samples(), dtype=np.float32)
    samples = samples / (np.max(np.abs(samples)) + 1e-8)  # Normalize

    total_duration = len(samples) / target_sr
    print(f"Audio extracted: {total_duration:.1f}s, {target_sr}Hz, mono")

    # Split into chunks
    chunk_samples = int(chunk_duration_sec * target_sr)
    chunks = []
    start = 0
    chunk_idx = 0

    while start < len(samples):
        end = min(start + chunk_samples, len(samples))
        chunk = samples[start:end]

        # Only emit chunks with actual audio content
        rms = np.sqrt(np.mean(chunk ** 2))
        if rms > 0.001:  # Minimal threshold for file audio
            chunks.append(("remote", chunk))
        
        start = end
        chunk_idx += 1

    print(f"Split into {len(chunks)} chunk(s)")
    return chunks, total_duration

def process_uploaded_file(file_path, transcriber, on_chunk_callback=None, on_progress_callback=None):
    """
    Process an uploaded file through the transcription pipeline.
    Calls on_chunk_callback(speaker, text, chunk_index, total_chunks) for each chunk.
    Calls on_progress_callback(progress_pct, message) for progress updates.
    Returns list of {speaker, text, chunk_index} dicts.
    """
    chunks, total_duration = extract_audio(file_path)
    results = []
    total = len(chunks)

    for i, (speaker, audio_chunk) in enumerate(chunks):
        if on_progress_callback:
            pct = int((i / total) * 100)
            on_progress_callback(pct, f"Transcribing chunk {i+1}/{total}...")

        text = transcriber.transcribe(audio_chunk)

        if text and text.strip():
            entry = {
                "speaker": speaker,
                "text": text.strip(),
                "chunk_index": i,
                "total_chunks": total
            }
            results.append(entry)

            if on_chunk_callback:
                on_chunk_callback(speaker, text.strip(), i, total)

    if on_progress_callback:
        on_progress_callback(100, "Done!")

    return results
