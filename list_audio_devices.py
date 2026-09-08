import sounddevice as sd

def list_devices():
    host_apis = sd.query_hostapis()
    print("=== Host APIs ===")
    for i, api in enumerate(host_apis):
        print(f"[{i}] {api['name']} (Default Input: {api['default_input_device']}, Default Output: {api['default_output_device']})")
    
    print("\n=== Audio Devices ===")
    devices = sd.query_devices()
    for i, dev in enumerate(devices):
        host_api_name = host_apis[dev['hostapi']]['name']
        print(f"[{i}] Name: {dev['name']}")
        print(f"    Host API: {host_api_name}")
        print(f"    Max Input Channels: {dev['max_input_channels']}, Max Output Channels: {dev['max_output_channels']}")
        print(f"    Default Sample Rate: {dev['default_samplerate']}")
        print("-" * 50)

if __name__ == "__main__":
    list_devices()
