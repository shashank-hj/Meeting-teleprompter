import sounddevice as sd
import numpy as np
import time

def main():
    # Find WASAPI devices
    devices = sd.query_devices()
    mic_idx = None
    spk_idx = None
    
    for i, dev in enumerate(devices):
        if dev['hostapi'] == 2:  # Windows WASAPI
            name = dev['name']
            if 'Microphone' in name or 'Mic' in name:
                mic_idx = i
            elif 'Speakers' in name or 'Headphones' in name:
                spk_idx = i
                
    print(f"Found WASAPI Microphone: {mic_idx} ({devices[mic_idx]['name'] if mic_idx is not None else 'None'})")
    print(f"Found WASAPI Speakers (for Loopback): {spk_idx} ({devices[spk_idx]['name'] if spk_idx is not None else 'None'})")
    
    if mic_idx is None or spk_idx is None:
        print("Could not find both mic and speakers under WASAPI. Listing all WASAPI devices:")
        for i, dev in enumerate(devices):
            if dev['hostapi'] == 2:
                print(f"  Device [{i}]: {dev['name']}")
        return

    # Let's try to open both streams
    mic_sr = int(devices[mic_idx]['default_samplerate'])
    spk_sr = int(devices[spk_idx]['default_samplerate'])
    
    print(f"Mic Sample Rate: {mic_sr}, Speaker Sample Rate: {spk_sr}")
    
    mic_buffer = []
    spk_buffer = []
    
    def mic_callback(indata, frames, time_info, status):
        if status:
            print(f"Mic Status: {status}")
        mic_buffer.append(indata.copy())

    def spk_callback(indata, frames, time_info, status):
        if status:
            print(f"Speaker Status: {status}")
        spk_buffer.append(indata.copy())

    try:
        # Loopback settings
        loopback_settings = sd.WasapiSettings(loopback=True)
        
        # Start mic stream
        mic_stream = sd.InputStream(
            device=mic_idx,
            channels=1,
            samplerate=mic_sr,
            callback=mic_callback
        )
        
        # Start speaker/loopback stream
        # Note: WASAPI loopback requires using the output device and specifying loopback=True
        spk_stream = sd.InputStream(
            device=spk_idx,
            channels=2, # Loopback is usually stereo
            samplerate=spk_sr,
            extra_settings=loopback_settings,
            callback=spk_callback
        )
        
        print("Starting capture streams...")
        with mic_stream, spk_stream:
            print("Recording for 5 seconds... play some system audio to test loopback!")
            for i in range(5):
                time.sleep(1)
                print(f"Elapsed: {i+1}s | Mic frames: {len(mic_buffer)} | Spk frames: {len(spk_buffer)}")
                
        print("Capture completed successfully!")
        
    except Exception as e:
        print(f"Error opening streams: {e}")

if __name__ == "__main__":
    main()
