import pyaudiowpatch as pyaudio

def main():
    p = pyaudio.PyAudio()
    try:
        # Get WASAPI Host API info
        wasapi_info = None
        for i in range(p.get_host_api_count()):
            api_info = p.get_host_api_info_by_index(i)
            if api_info["type"] == pyaudio.paWASAPI:
                wasapi_info = api_info
                break
        
        if wasapi_info is None:
            print("WASAPI API not found on this system!")
            return
            
        print(f"WASAPI Host API index: {wasapi_info['index']}")
        
        # Get default input/output
        try:
            default_input = p.get_default_input_device_info()
            print(f"Default Input Device: {default_input['name']} (Index: {default_input['index']})")
        except IOError:
            print("No default input device found")
            
        try:
            default_output = p.get_default_output_device_info()
            print(f"Default Output Device: {default_output['name']} (Index: {default_output['index']})")
        except IOError:
            print("No default output device found")

        print("\n=== WASAPI Loopback Devices ===")
        for index in range(p.get_device_count()):
            info = p.get_device_info_by_index(index)
            if info["hostApi"] == wasapi_info["index"]:
                # Check if it is a loopback device or default output device that can be used for loopback
                is_loopback = info.get("isLoopbackDevice", False)
                # Some versions of pyaudiowpatch expose it via isLoopbackDevice flag
                print(f"[{index}] {info['name']}")
                print(f"    Max Input Channels: {info['maxInputChannels']}")
                print(f"    Max Output Channels: {info['maxOutputChannels']}")
                print(f"    Default Sample Rate: {info['defaultSampleRate']}")
                print(f"    isLoopbackDevice: {is_loopback}")
                print("-" * 50)
                
    finally:
        p.terminate()

if __name__ == "__main__":
    main()
