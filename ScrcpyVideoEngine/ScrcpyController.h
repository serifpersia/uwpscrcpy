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

// Scrcpy Control Protocol Message Types
#define SC_CONTROL_MSG_TYPE_INJECT_TOUCH_EVENT 2
#define SC_CONTROL_MSG_TYPE_INJECT_SCROLL_EVENT 3
#define SC_CONTROL_MSG_TYPE_BACK_OR_SCREEN_ON 4
#define SC_CONTROL_MSG_TYPE_UHID_CREATE 12
#define SC_CONTROL_MSG_TYPE_UHID_INPUT 13
#define SC_CONTROL_MSG_TYPE_UHID_DESTROY 14

// Standard HID Mouse Report Descriptor (Required for UHID mode)
static const uint8_t SC_HID_MOUSE_REPORT_DESC[] = {
	0x05, 0x01, 0x09, 0x02, 0xA1, 0x01, 0x09, 0x01, 0xA1, 0x00, 0x05,
	0x09, 0x19, 0x01, 0x29, 0x05, 0x15, 0x00, 0x25, 0x01, 0x95, 0x05,
	0x75, 0x01, 0x81, 0x02, 0x95, 0x01, 0x75, 0x03, 0x81, 0x01, 0x05,
	0x01, 0x09, 0x30, 0x09, 0x31, 0x09, 0x38, 0x15, 0x81, 0x25, 0x7F,
	0x75, 0x08, 0x95, 0x03, 0x81, 0x06, 0x05, 0x0C, 0x0A, 0x38, 0x02,
	0x15, 0x81, 0x25, 0x7F, 0x75, 0x08, 0x95, 0x01, 0x81, 0x06, 0xC0,
	0xC0,
};

#define SC_HID_ID_MOUSE 2

namespace Windows { namespace UI { namespace Core { ref class CoreDispatcher; } } }

namespace ScrcpyVideoEngine
{
	public delegate void LogHandler(Platform::String^ message);
	public delegate void ResolutionChangedHandler(uint32_t newWidth, uint32_t newHeight);
	public delegate Platform::Array<byte>^ AuthSignHandler(const Platform::Array<byte>^ token);
	public delegate Platform::Array<byte>^ AuthKeyHandler();

	struct PacketData {
		Microsoft::WRL::ComPtr<IMFMediaBuffer> mediaBuffer;
		int64_t pts;
	};

	[Windows::Foundation::Metadata::WebHostHidden]
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
		void StartScrcpy(int bitRate, int maxSize, int maxFps, bool video, bool uhid);
		void Stop();

		// --- THIS IS THE FIX ---
		// Moved from private to public so C# can call it.
		void InitializeVideo(uint32_t width, uint32_t height);

		// --- NEW: Public Control Methods (Safe Types) ---
		void InjectTouch(int action, int pointerId, int x, int y, int width, int height, float pressure, int buttons);
		void InjectScroll(int x, int y, int width, int height, int hScroll, int vScroll, int buttons);
		void InjectBackOrScreenOn(int action);

		void EnableUhidMouse(bool enable);
		void InjectUhidInput(int buttons, int dx, int dy, int vScroll, int hScroll);

	internal:
		// --- FIXED: Internal Helpers (Native Types Allowed Here) ---
		bool SendControlMsg(const std::vector<uint8_t>& msg);
		void WriteBE32(uint8_t* buf, uint32_t val);
		void WriteBE16(uint8_t* buf, uint16_t val);
		void SendHidCreateMouse();
		void SendHidDestroyMouse();


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
		Microsoft::WRL::ComPtr<ID3D11VideoProcessorOutputView> m_cachedOutputView;
		Microsoft::WRL::ComPtr<ID3D11Texture2D> m_cachedBackBuffer;

		void Log(const std::string& msg);
		std::vector<uint8_t> PerformSign(const std::vector<uint8_t>& token);
		std::vector<uint8_t> GetKey();
		void ReceiveLoop();
		void HandlePacket(uint32_t cmd, uint32_t arg0, uint32_t arg1, uint32_t dlen, const uint8_t* payload);
		void PushFrame(Microsoft::WRL::ComPtr<IMFMediaBuffer> buf, int64_t raw_pts);
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
		void ApplyResolutionChange(uint32_t width, uint32_t height);

		bool m_enableVideo;
		bool m_enableUhid; // <--- ADD THIS
	};
}