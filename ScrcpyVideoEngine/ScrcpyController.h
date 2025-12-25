#pragma once
#include <wrl.h>
#include <string>
#include <vector>
#include <mutex>
#include <map>
#include <atomic>
#include <functional>
#include <future>
#include <queue>
#include <condition_variable>
#include <winsock2.h>
#include <ws2tcpip.h>
#include <d3d11.h>
#include <d3d11_1.h>
#include <dxgi1_2.h>
#include <dxgi1_3.h>
#include <robuffer.h>
#include <windows.ui.xaml.media.dxinterop.h>
#include <Windows.Foundation.h>

namespace Windows { namespace UI { namespace Core { ref class CoreDispatcher; } } }

namespace ScrcpyVideoEngine
{
	public delegate void LogHandler(Platform::String^ message);
	public delegate void ResolutionChangedHandler(uint32_t newWidth, uint32_t newHeight);
	public delegate Platform::Array<byte>^ AuthSignHandler(const Platform::Array<byte>^ token);
	public delegate Platform::Array<byte>^ AuthKeyHandler();

	struct PacketData {
		Windows::Storage::Streams::IBuffer^ buffer;
		int64_t pts;
	};

	public ref class ScrcpyController sealed
	{
	public:
		ScrcpyController();
		virtual ~ScrcpyController();

		event LogHandler^ OnLog;
		event ResolutionChangedHandler^ OnResolutionChanged;
		property AuthSignHandler^ AuthSignCallback;
		property AuthKeyHandler^ AuthKeyCallback;

		void SetDispatcher(Windows::UI::Core::CoreDispatcher^ dispatcher);
		void SetPanel(Platform::Object^ panel);
		bool Connect(Platform::String^ ip, int port);
		void DeployServer(const Platform::Array<byte>^ jarData);
		void StartScrcpy(int bitRate, int maxSize, int maxFps);
		void Stop();

		// --- THIS IS THE FIX ---
		// Moved from private to public so C# can call it.
		void InitializeVideo(uint32_t width, uint32_t height);

	private:
		std::mutex m_renderMutex;
		SOCKET m_socket;
		std::atomic<bool> m_running;
		Windows::Foundation::IAsyncAction^ m_recvAction;
		std::promise<bool>* m_connectPromise;
		uint32_t m_localIdCounter;
		std::mutex m_mapMutex;
		std::map<uint32_t, uint32_t> m_localToRemote;
		std::mutex m_pendingMutex;
		std::map<uint32_t, std::promise<bool>*> m_pendingOpens;
		std::map<uint32_t, std::promise<bool>*> m_pendingCloses;
		uint32_t m_videoLocalId;
		uint32_t m_controlLocalId;
		uint32_t m_serverLocalId;
		std::string m_scid;
		std::atomic<bool> m_authAttempted;
		std::vector<uint8_t> m_recvBuffer;
		std::vector<uint8_t> m_videoBuffer;
		size_t m_videoReadPos;
		int m_videoStage;
		std::vector<uint8_t> m_pendingConfig;
		bool m_isInitialized;
		Windows::Foundation::IAsyncAction^ m_decoderAction;
		std::mutex m_queueMutex;
		std::condition_variable m_queueCv;
		std::queue<PacketData> m_packetQueue;
		uint32_t m_width;
		uint32_t m_height;
		int64_t m_baselinePts;
		Windows::UI::Core::CoreDispatcher^ m_dispatcher;
		Microsoft::WRL::ComPtr<ID3D11Device> m_d3dDevice;
		Microsoft::WRL::ComPtr<ID3D11DeviceContext> m_d3dContext;
		Microsoft::WRL::ComPtr<IMFDXGIDeviceManager> m_dxgiManager;
		Microsoft::WRL::ComPtr<IMFTransform> m_decoder;
		UINT m_resetToken;
		Microsoft::WRL::ComPtr<ISwapChainPanelNative> m_panelNative;
		Microsoft::WRL::ComPtr<IDXGISwapChain1> m_swapChain;
		Microsoft::WRL::ComPtr<ID3D11VideoDevice> m_videoDevice;
		Microsoft::WRL::ComPtr<ID3D11VideoContext> m_videoContext;
		Microsoft::WRL::ComPtr<ID3D11VideoProcessorEnumerator> m_videoProcessorEnum;
		Microsoft::WRL::ComPtr<ID3D11VideoProcessor> m_videoProcessor;

		void Log(const std::string& msg);
		std::vector<uint8_t> PerformSign(const std::vector<uint8_t>& token);
		std::vector<uint8_t> GetKey();
		void ReceiveLoop();
		void HandlePacket(uint32_t cmd, uint32_t arg0, uint32_t arg1, uint32_t dlen, const uint8_t* payload);
		void PushFrame(Windows::Storage::Streams::IBuffer^ buf, int64_t raw_pts);
		void DecoderLoop();
		void ProcessDecodedOutput();
		void RenderFrame(ID3D11Texture2D* decoderTex, UINT subIndex);
		uint32_t OpenStream(const std::string& destination);
		bool ExecuteShellCommand(const std::string& command);
		bool SendPacket(uint32_t cmd, uint32_t arg0, uint32_t arg1, const void* data, size_t length);
		uint64_t ReadBE64(const uint8_t* data);
		uint32_t ReadBE32(const uint8_t* data);
		void CompactVideoBuffer();
		bool InitDX11();
		bool InitDecoder(uint32_t width, uint32_t height);
		void CreateSwapChain(uint32_t width, uint32_t height);
	};
}