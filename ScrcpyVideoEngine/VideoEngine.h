#pragma once
#include <wrl.h>
#include <mutex>
#include <atomic>
#include <queue>
#include <thread>
#include <condition_variable>
#include <d3d11.h>
#include <d3d11_1.h>
#include <dxgi1_2.h>
#include <dxgi1_3.h>
#include <robuffer.h> 
#include <windows.ui.xaml.media.dxinterop.h> 
#include <vector>

namespace ScrcpyVideoEngine
{
	public delegate void DebugHandler(Platform::String^ message);

	struct PacketData {
		std::vector<byte> data;
		int64_t pts;
	};

	public ref class VideoEngine sealed
	{
	public:
		VideoEngine();
		virtual ~VideoEngine();

		void Initialize(uint32_t width, uint32_t height);
		void SetPanel(Platform::Object^ panel);
		void PushFrame(Windows::Storage::Streams::IBuffer^ buf, int64_t raw_pts);

		void Start();
		void Stop();

		event DebugHandler^ OnDebugLog;

	private:
		void Log(Platform::String^ msg);
		void LogWithThread(Platform::String^ action, Platform::String^ msg);

		bool InitDX11();
		bool InitDecoder(uint32_t width, uint32_t height);
		void CreateSwapChain(uint32_t width, uint32_t height);
		void DecoderLoop();
		void ProcessDecodedOutput();
		void RenderFrame(ID3D11Texture2D* decoderTex, UINT subIndex);

		bool m_isInitialized = false;
		std::atomic<bool> m_isRunning;
		uint32_t m_width = 0;
		uint32_t m_height = 0;
		int64_t m_baselinePts = -1;

		std::thread m_workerThread;
		std::mutex m_queueMutex;
		std::condition_variable m_queueCv;
		std::queue<PacketData> m_packetQueue;

		Microsoft::WRL::ComPtr<ID3D11Device> m_d3dDevice;
		Microsoft::WRL::ComPtr<ID3D11DeviceContext> m_d3dContext;
		Microsoft::WRL::ComPtr<IMFDXGIDeviceManager> m_dxgiManager;
		Microsoft::WRL::ComPtr<IMFTransform> m_decoder;
		UINT m_resetToken = 0;

		Microsoft::WRL::ComPtr<ISwapChainPanelNative> m_panelNative;
		Microsoft::WRL::ComPtr<IDXGISwapChain1> m_swapChain;

		Microsoft::WRL::ComPtr<ID3D11VideoDevice> m_videoDevice;
		Microsoft::WRL::ComPtr<ID3D11VideoContext> m_videoContext;
		Microsoft::WRL::ComPtr<ID3D11VideoProcessorEnumerator> m_videoProcessorEnum;
		Microsoft::WRL::ComPtr<ID3D11VideoProcessor> m_videoProcessor;
	};
}