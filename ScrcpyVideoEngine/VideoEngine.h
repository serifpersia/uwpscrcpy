#pragma once
#include "pch.h"

namespace ScrcpyVideoEngine {
	struct PacketData {
		Microsoft::WRL::ComPtr<IMFMediaBuffer> mediaBuffer;
		int64_t pts;
	};

	class VideoEngine {
	public:
		VideoEngine();
		~VideoEngine();
		void SetDispatcher(Windows::UI::Core::CoreDispatcher^ dispatcher) { m_dispatcher = dispatcher; }
		void Initialize(uint32_t width, uint32_t height, Platform::Object^ panel);
		void Shutdown();
		void PushFrame(Microsoft::WRL::ComPtr<IMFMediaBuffer> buf, int64_t pts);
		void ApplyResolutionChange(uint32_t width, uint32_t height);
		void SetResolutionCallback(std::function<void(uint32_t, uint32_t)> callback) { m_resCallback = callback; }

	private:
		bool InitDX11();
		bool InitDecoder(uint32_t width, uint32_t height);
		void CreateSwapChain(uint32_t width, uint32_t height);
		void DecoderLoop();
		void ProcessDecodedOutput();
		void RenderFrame(ID3D11Texture2D* decoderTex, UINT subIndex);
		void UpdateVideoProcessorRects(); // New helper function

		std::atomic<bool> m_running;
		std::atomic<bool> m_isInitialized;
		std::thread m_decoderThread;
		std::mutex m_renderMutex;
		std::mutex m_queueMutex;
		std::condition_variable m_queueCv;
		std::queue<PacketData> m_packetQueue;

		uint32_t m_width;
		uint32_t m_height;
		int64_t m_baselinePts;
		UINT m_resetToken;

		Windows::UI::Core::CoreDispatcher^ m_dispatcher;
		Microsoft::WRL::ComPtr<ID3D11Device> m_d3dDevice;
		Microsoft::WRL::ComPtr<ID3D11DeviceContext> m_d3dContext;
		Microsoft::WRL::ComPtr<IMFDXGIDeviceManager> m_dxgiManager;
		Microsoft::WRL::ComPtr<IMFTransform> m_decoder;
		Microsoft::WRL::ComPtr<ISwapChainPanelNative> m_panelNative;
		Microsoft::WRL::ComPtr<IDXGISwapChain1> m_swapChain;
		Microsoft::WRL::ComPtr<ID3D11VideoDevice> m_videoDevice;
		Microsoft::WRL::ComPtr<ID3D11VideoContext> m_videoContext;
		Microsoft::WRL::ComPtr<ID3D11VideoProcessorEnumerator> m_videoProcessorEnum;
		Microsoft::WRL::ComPtr<ID3D11VideoProcessor> m_videoProcessor;
		Microsoft::WRL::ComPtr<ID3D11VideoProcessorOutputView> m_cachedOutputView;
		Microsoft::WRL::ComPtr<ID3D11Texture2D> m_cachedBackBuffer;

		// --- PERFORMANCE FIX #2: Caching Variables ---
		// Map to cache InputViews for decoder textures so we don't recreate them every frame
		std::map<ID3D11Texture2D*, Microsoft::WRL::ComPtr<ID3D11VideoProcessorInputView>> m_inputViewCache;
		// ---------------------------------------------

		std::function<void(uint32_t, uint32_t)> m_resCallback;
	};
}