#include "pch.h"
#include "VideoEngine.h"

using namespace Microsoft::WRL;
using namespace Windows::UI::Core;

const GUID CLSID_CMSH264DecoderMFT_Local = { 0x62CE7E72, 0x4C71, 0x4d20, { 0xB1, 0x5D, 0x45, 0x28, 0x31, 0xA8, 0x7D, 0x9D } };
const GUID CODECAPI_AVLowLatencyMode_Local = { 0x9c27fa99, 0x6272, 0x4dc5, { 0x9c, 0x9b, 0xb9, 0x29, 0x18, 0x8d, 0x48, 0x2a } };

namespace ScrcpyVideoEngine {
	VideoEngine::VideoEngine() : m_running(false), m_isInitialized(false), m_width(0), m_height(0), m_baselinePts(-1), m_resetToken(0), m_dispatcher(nullptr) {
		MFStartup(MF_VERSION);
	}

	VideoEngine::~VideoEngine() { Shutdown(); MFShutdown(); }

	void VideoEngine::Initialize(uint32_t width, uint32_t height, Platform::Object^ panel) {
		std::lock_guard<std::mutex> lock(m_renderMutex);
		ComPtr<IInspectable> insp = reinterpret_cast<IInspectable*>(panel);
		if (insp != nullptr) insp.As(&m_panelNative);

		if (m_isInitialized) { ApplyResolutionChange(width, height); return; }
		m_width = width; m_height = height;

		m_decoder.Reset();
		if (m_panelNative) {
			if (m_dispatcher && !m_dispatcher->HasThreadAccess) {
				m_dispatcher->RunAsync(CoreDispatcherPriority::High, ref new DispatchedHandler([this]() {
					if (m_panelNative) m_panelNative->SetSwapChain(nullptr);
				}));
			}
			else {
				m_panelNative->SetSwapChain(nullptr);
			}
		}
		m_swapChain.Reset();

		if (!InitDX11() || !InitDecoder(width, height)) return;
		if (m_panelNative) CreateSwapChain(width, height);

		m_isInitialized = true;
		m_running = true;
		m_decoderThread = std::thread(&VideoEngine::DecoderLoop, this);
	}

	void VideoEngine::Shutdown() {
		m_running = false;
		m_queueCv.notify_all();
		if (m_decoderThread.joinable()) m_decoderThread.join();

		auto cleanupUI = [this]() {
			std::lock_guard<std::mutex> lock(m_renderMutex);
			if (m_panelNative) m_panelNative->SetSwapChain(nullptr);
			m_cachedOutputView.Reset();
			m_cachedBackBuffer.Reset();
			m_swapChain.Reset();
			m_decoder.Reset();
			m_videoProcessor.Reset();
			m_videoProcessorEnum.Reset();
		};

		if (m_dispatcher && !m_dispatcher->HasThreadAccess) {
			m_dispatcher->RunAsync(CoreDispatcherPriority::High, ref new DispatchedHandler([cleanupUI]() {
				cleanupUI();
			}));
		}
		else {
			cleanupUI();
		}

		{ std::lock_guard<std::mutex> qLock(m_queueMutex); std::queue<PacketData> empty; std::swap(m_packetQueue, empty); }
		m_isInitialized = false;
		m_baselinePts = -1;
	}

	void VideoEngine::PushFrame(ComPtr<IMFMediaBuffer> buf, int64_t pts) {
		if (!buf || !m_running) return;
		{
			std::lock_guard<std::mutex> lock(m_queueMutex);
			m_packetQueue.push({ buf, pts });
		}
		m_queueCv.notify_one();
	}

	void VideoEngine::DecoderLoop() {
		while (m_running) {
			PacketData packet;
			{
				std::unique_lock<std::mutex> lock(m_queueMutex);
				m_queueCv.wait(lock, [this] { return !m_packetQueue.empty() || !m_running; });
				if (!m_running) break;
				packet = m_packetQueue.front();
				m_packetQueue.pop();
			}
			std::lock_guard<std::mutex> lock(m_renderMutex);
			if (!m_decoder || !m_isInitialized || !m_running) continue;
			if (m_baselinePts == -1) m_baselinePts = packet.pts;

			ComPtr<IMFSample> sample;
			MFCreateSample(&sample);
			sample->AddBuffer(packet.mediaBuffer.Get());
			sample->SetSampleDuration(0);
			sample->SetSampleTime((packet.pts - m_baselinePts) * 10);

			HRESULT hr = m_decoder->ProcessInput(0, sample.Get(), 0);
			if (SUCCEEDED(hr) || hr == MF_E_NOTACCEPTING) ProcessDecodedOutput();
		}
	}

	void VideoEngine::ProcessDecodedOutput() {
		MFT_OUTPUT_DATA_BUFFER output = { 0 };
		DWORD status = 0;
		while (m_running) {
			HRESULT hr = m_decoder->ProcessOutput(0, 1, &output, &status);
			if (hr == MF_E_TRANSFORM_NEED_MORE_INPUT) break;
			if (hr == MF_E_TRANSFORM_STREAM_CHANGE) {
				ComPtr<IMFMediaType> t;
				if (SUCCEEDED(m_decoder->GetOutputAvailableType(0, 0, &t))) {
					m_decoder->SetOutputType(0, t.Get(), 0);
					UINT32 w = 0, h = 0;
					MFVideoArea aperture = { 0 };
					UINT32 blobSize = 0;
					if (SUCCEEDED(t->GetBlob(MF_MT_MINIMUM_DISPLAY_APERTURE, (UINT8*)&aperture, sizeof(aperture), &blobSize)) && aperture.Area.cx > 0) {
						w = aperture.Area.cx; h = aperture.Area.cy;
					}
					else {
						MFGetAttributeSize(t.Get(), MF_MT_FRAME_SIZE, &w, &h);
					}
					if (w > 0 && h > 0) {
						ApplyResolutionChange(w, h);
						if (m_resCallback) m_resCallback(w, h);
					}
				}
				continue;
			}
			if (SUCCEEDED(hr) && output.pSample) {
				ComPtr<IMFMediaBuffer> buf;
				if (SUCCEEDED(output.pSample->GetBufferByIndex(0, &buf))) {
					ComPtr<IMFDXGIBuffer> dxgiBuf;
					if (SUCCEEDED(buf.As(&dxgiBuf))) {
						ComPtr<ID3D11Texture2D> decoderTex;
						if (SUCCEEDED(dxgiBuf->GetResource(IID_PPV_ARGS(&decoderTex)))) {
							UINT subIndex = 0; dxgiBuf->GetSubresourceIndex(&subIndex);
							RenderFrame(decoderTex.Get(), subIndex);
						}
					}
				}
			}
			if (output.pSample) { output.pSample->Release(); output.pSample = nullptr; }
			if (output.pEvents) { output.pEvents->Release(); output.pEvents = nullptr; }
		}
	}

	void VideoEngine::RenderFrame(ID3D11Texture2D* decoderTex, UINT subIndex) {
		if (!m_swapChain || !m_videoProcessor || !m_videoContext) return;
		if (!m_cachedOutputView) {
			if (FAILED(m_swapChain->GetBuffer(0, IID_PPV_ARGS(&m_cachedBackBuffer)))) return;
			D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC outputViewDesc = { D3D11_VPOV_DIMENSION_TEXTURE2D };
			if (FAILED(m_videoDevice->CreateVideoProcessorOutputView(m_cachedBackBuffer.Get(), m_videoProcessorEnum.Get(), &outputViewDesc, &m_cachedOutputView))) { m_cachedBackBuffer.Reset(); return; }
		}
		D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC inputViewDesc = { 0 };
		inputViewDesc.ViewDimension = D3D11_VPIV_DIMENSION_TEXTURE2D;
		inputViewDesc.Texture2D.ArraySlice = subIndex;
		ComPtr<ID3D11VideoProcessorInputView> inputView;
		if (FAILED(m_videoDevice->CreateVideoProcessorInputView(decoderTex, m_videoProcessorEnum.Get(), &inputViewDesc, &inputView))) return;

		D3D11_VIDEO_PROCESSOR_STREAM stream = { 0 };
		stream.Enable = TRUE;
		stream.pInputSurface = inputView.Get();
		RECT sourceRect = { 0, 0, (LONG)m_width, (LONG)m_height };
		m_videoContext->VideoProcessorSetStreamSourceRect(m_videoProcessor.Get(), 0, TRUE, &sourceRect);
		m_videoContext->VideoProcessorSetStreamDestRect(m_videoProcessor.Get(), 0, FALSE, nullptr);
		if (SUCCEEDED(m_videoContext->VideoProcessorBlt(m_videoProcessor.Get(), m_cachedOutputView.Get(), 0, 1, &stream))) {
			m_swapChain->Present(0, DXGI_PRESENT_ALLOW_TEARING);
		}
	}

	bool VideoEngine::InitDX11() {
		if (!m_d3dDevice) {
			D3D_FEATURE_LEVEL levels[] = { D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0, D3D_FEATURE_LEVEL_9_3 };
			UINT flags = D3D11_CREATE_DEVICE_VIDEO_SUPPORT | D3D11_CREATE_DEVICE_BGRA_SUPPORT;
			ComPtr<ID3D11Device> device; ComPtr<ID3D11DeviceContext> context; D3D_FEATURE_LEVEL featureLevel;
			if (FAILED(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, flags, levels, ARRAYSIZE(levels), D3D11_SDK_VERSION, &device, &featureLevel, &context))) return false;
			m_d3dDevice = device; m_d3dContext = context;
			m_d3dDevice.As(&m_videoDevice); m_d3dContext.As(&m_videoContext);
			ComPtr<ID3D10Multithread> multithread;
			if (SUCCEEDED(m_d3dDevice.As(&multithread))) multithread->SetMultithreadProtected(TRUE);
			MFCreateDXGIDeviceManager(&m_resetToken, &m_dxgiManager);
			m_dxgiManager->ResetDevice(m_d3dDevice.Get(), m_resetToken);
		}
		m_videoProcessorEnum.Reset(); m_videoProcessor.Reset();
		D3D11_VIDEO_PROCESSOR_CONTENT_DESC desc = {};
		desc.InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;
		desc.InputFrameRate = { 60, 1 }; desc.InputWidth = m_width; desc.InputHeight = m_height;
		desc.OutputFrameRate = { 60, 1 }; desc.OutputWidth = m_width; desc.OutputHeight = m_height;
		desc.Usage = D3D11_VIDEO_USAGE_OPTIMAL_SPEED;
		if (FAILED(m_videoDevice->CreateVideoProcessorEnumerator(&desc, &m_videoProcessorEnum))) return false;
		if (FAILED(m_videoDevice->CreateVideoProcessor(m_videoProcessorEnum.Get(), 0, &m_videoProcessor))) return false;
		return true;
	}

	bool VideoEngine::InitDecoder(uint32_t width, uint32_t height) {
		if (FAILED(CoCreateInstance(CLSID_CMSH264DecoderMFT_Local, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&m_decoder)))) return false;
		ComPtr<IMFAttributes> mftAttr;
		if (SUCCEEDED(m_decoder->GetAttributes(&mftAttr))) {
			mftAttr->SetUINT32(MF_LOW_LATENCY, 1);
			mftAttr->SetUINT32(CODECAPI_AVLowLatencyMode_Local, 1);
			mftAttr->SetUINT32(CODECAPI_AVDecVideoMaxCodedWidth, width);
			mftAttr->SetUINT32(CODECAPI_AVDecVideoMaxCodedHeight, height);
		}
		m_decoder->ProcessMessage(MFT_MESSAGE_SET_D3D_MANAGER, (ULONG_PTR)m_dxgiManager.Get());
		ComPtr<IMFMediaType> inType; MFCreateMediaType(&inType);
		inType->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
		inType->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_H264);
		MFSetAttributeSize(inType.Get(), MF_MT_FRAME_SIZE, width, height);
		m_decoder->SetInputType(0, inType.Get(), 0);
		ComPtr<IMFMediaType> outType; MFCreateMediaType(&outType);
		outType->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
		outType->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_NV12);
		MFSetAttributeSize(outType.Get(), MF_MT_FRAME_SIZE, width, height);
		m_decoder->SetOutputType(0, outType.Get(), 0);
		m_decoder->ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0);
		m_decoder->ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0);
		return true;
	}

	void VideoEngine::CreateSwapChain(uint32_t width, uint32_t height) {
		if (!m_panelNative || !m_d3dDevice) return;
		if (m_swapChain) {
			m_d3dContext->ClearState(); m_d3dContext->Flush();
			if (SUCCEEDED(m_swapChain->ResizeBuffers(2, width, height, DXGI_FORMAT_B8G8R8A8_UNORM, DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING))) return;
		}
		DXGI_SWAP_CHAIN_DESC1 desc = { 0 };
		desc.Width = width; desc.Height = height; desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
		desc.SampleDesc.Count = 1; desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT; desc.BufferCount = 2;
		desc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL; desc.Scaling = DXGI_SCALING_STRETCH;
		desc.Flags = DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING;
		ComPtr<IDXGIDevice> dxgiDevice; m_d3dDevice.As(&dxgiDevice);
		ComPtr<IDXGIAdapter> adapter; dxgiDevice->GetAdapter(&adapter);
		ComPtr<IDXGIFactory2> factory; adapter->GetParent(IID_PPV_ARGS(&factory));
		m_swapChain.Reset();
		factory->CreateSwapChainForComposition(m_d3dDevice.Get(), &desc, nullptr, &m_swapChain);

		if (m_dispatcher && !m_dispatcher->HasThreadAccess) {
			m_dispatcher->RunAsync(CoreDispatcherPriority::High, ref new DispatchedHandler([this]() {
				if (m_panelNative && m_swapChain) m_panelNative->SetSwapChain(m_swapChain.Get());
			}));
		}
		else {
			m_panelNative->SetSwapChain(m_swapChain.Get());
		}
	}

	void VideoEngine::ApplyResolutionChange(uint32_t width, uint32_t height) {
		if (m_width == width && m_height == height) return;
		m_cachedOutputView.Reset(); m_cachedBackBuffer.Reset();
		m_width = width; m_height = height;
		if (m_d3dContext) { m_d3dContext->OMSetRenderTargets(0, nullptr, nullptr); m_d3dContext->ClearState(); m_d3dContext->Flush(); }
		if (m_swapChain) m_swapChain->ResizeBuffers(2, width, height, DXGI_FORMAT_B8G8R8A8_UNORM, DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING);
		if (m_videoDevice) {
			m_videoProcessorEnum.Reset(); m_videoProcessor.Reset();
			D3D11_VIDEO_PROCESSOR_CONTENT_DESC desc = {};
			desc.InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;
			desc.InputFrameRate = { 60, 1 }; desc.InputWidth = width; desc.InputHeight = height;
			desc.OutputFrameRate = { 60, 1 }; desc.OutputWidth = width; desc.OutputHeight = height;
			desc.Usage = D3D11_VIDEO_USAGE_OPTIMAL_SPEED;
			m_videoDevice->CreateVideoProcessorEnumerator(&desc, &m_videoProcessorEnum);
			if (m_videoProcessorEnum) m_videoDevice->CreateVideoProcessor(m_videoProcessorEnum.Get(), 0, &m_videoProcessor);
		}
	}
}