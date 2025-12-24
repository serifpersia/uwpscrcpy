#include "pch.h"
#include "VideoEngine.h"
#include <mfapi.h>
#include <mferror.h>
#include <codecapi.h>
#include <string>
#include <sstream>

using namespace ScrcpyVideoEngine;
using namespace Platform;
using namespace Microsoft::WRL;
using namespace Windows::Storage::Streams;

const GUID CLSID_CMSH264DecoderMFT_Local = { 0x62CE7E72, 0x4C71, 0x4d20, { 0xB1, 0x5D, 0x45, 0x28, 0x31, 0xA8, 0x7D, 0x9D } };
const GUID CODECAPI_AVLowLatencyMode_Local = { 0x9c27fa99, 0x6272, 0x4dc5, { 0x9c, 0x9b, 0xb9, 0x29, 0x18, 0x8d, 0x48, 0x2a } };

VideoEngine::VideoEngine() {
	MFStartup(MF_VERSION);
	m_isRunning = false;
	m_isInitialized = false;
}

VideoEngine::~VideoEngine() {
	Stop();
	MFShutdown();
}

void VideoEngine::Log(String^ msg) {
	LogWithThread("INFO", msg);
}

void VideoEngine::LogWithThread(String^ action, String^ msg) {
	DWORD threadId = GetCurrentThreadId();
	std::wstringstream ss;
	ss << L"[" << threadId << L"] " << action->Data() << L": " << msg->Data();
	String^ formatted = ref new String(ss.str().c_str());
	try { OnDebugLog(formatted); }
	catch (...) {}
	OutputDebugString(formatted->Data());
	OutputDebugString(L"\n");
}

void VideoEngine::Initialize(uint32_t width, uint32_t height) {
	if (m_isInitialized) return;
	m_width = width; m_height = height;
	Log("Initializing Video Engine...");
	if (!InitDX11()) return;
	if (!InitDecoder(width, height)) return;
	m_isInitialized = true;
	Log("Video Engine Initialized.");
	Start();
}

void VideoEngine::Start() {
	if (m_isRunning) return;
	m_isRunning = true;
	m_workerThread = std::thread([this]() {
		SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_HIGHEST);
		this->DecoderLoop();
	});
	Log("Worker Thread Started");
}

void VideoEngine::Stop() {
	m_isRunning = false;
	m_queueCv.notify_all();
	if (m_workerThread.joinable()) {
		m_workerThread.join();
	}
	if (m_d3dContext && m_swapChain) {
		ComPtr<ID3D11Texture2D> backBuffer;
		if (SUCCEEDED(m_swapChain->GetBuffer(0, IID_PPV_ARGS(&backBuffer)))) {
			ComPtr<ID3D11RenderTargetView> rtv;
			m_d3dDevice->CreateRenderTargetView(backBuffer.Get(), nullptr, &rtv);
			if (rtv) {
				float black[] = { 0.0f, 0.0f, 0.0f, 1.0f };
				m_d3dContext->ClearRenderTargetView(rtv.Get(), black);
				m_swapChain->Present(1, 0);
			}
		}
	}
	m_decoder.Reset();
	m_videoProcessor.Reset();
	m_videoProcessorEnum.Reset();
	m_videoContext.Reset();
	m_videoDevice.Reset();
	{
		std::lock_guard<std::mutex> lock(m_queueMutex);
		std::queue<PacketData> empty;
		std::swap(m_packetQueue, empty);
	}
	m_isInitialized = false;
}

void VideoEngine::PushFrame(IBuffer^ buf, int64_t raw_pts) {
	if (!buf || buf->Length == 0 || !m_isRunning) return;

	PacketData packet;
	packet.pts = raw_pts;
	packet.buffer = buf;

	{
		std::lock_guard<std::mutex> lock(m_queueMutex);
		m_packetQueue.push(packet);
	}
	m_queueCv.notify_one();
}

void VideoEngine::ResizeSwapChain(uint32_t newWidth, uint32_t newHeight) {
	if (!m_swapChain || !m_d3dContext) return;

	m_d3dContext->OMSetRenderTargets(0, nullptr, nullptr);

	HRESULT hr = m_swapChain->ResizeBuffers(2, newWidth, newHeight, DXGI_FORMAT_B8G8R8A8_UNORM, 0);
	if (FAILED(hr)) {
		Log("Failed to resize swap chain.");
	}
	else {
		Log("Swap chain resized successfully.");
	}
}


void VideoEngine::DecoderLoop() {
	LogWithThread("WORKER", "Thread Loop Start");
	while (m_isRunning) {
		PacketData packet;
		{
			std::unique_lock<std::mutex> lock(m_queueMutex);
			m_queueCv.wait(lock, [this] { return !m_packetQueue.empty() || !m_isRunning; });
			if (!m_isRunning) break;
			packet = m_packetQueue.front();
			m_packetQueue.pop();
		}
		if (!m_decoder || !m_isInitialized || packet.buffer == nullptr) continue;
		if (m_baselinePts == -1) m_baselinePts = packet.pts;

		ComPtr<IUnknown> unk = reinterpret_cast<IUnknown*>(packet.buffer);
		ComPtr<IBufferByteAccess> bufferByteAccess;
		if (FAILED(unk.As(&bufferByteAccess))) continue;

		byte* rawData = nullptr;
		if (FAILED(bufferByteAccess->Buffer(&rawData))) continue;

		ComPtr<IMFMediaBuffer> mb;
		MFCreateMemoryBuffer(packet.buffer->Length, &mb);
		BYTE* dest = nullptr;
		mb->Lock(&dest, nullptr, nullptr);
		memcpy(dest, rawData, packet.buffer->Length);
		mb->Unlock();
		mb->SetCurrentLength(packet.buffer->Length);

		ComPtr<IMFSample> sample;
		MFCreateSample(&sample);
		sample->AddBuffer(mb.Get());
		sample->SetSampleDuration(0);
		sample->SetSampleTime((packet.pts - m_baselinePts) * 10);
		HRESULT hr = m_decoder->ProcessInput(0, sample.Get(), 0);
		if (SUCCEEDED(hr) || hr == MF_E_NOTACCEPTING) {
			ProcessDecodedOutput();
		}
	}
	LogWithThread("WORKER", "Thread Loop End");
}

void VideoEngine::ProcessDecodedOutput() {
	MFT_OUTPUT_DATA_BUFFER output = { 0 };
	DWORD status = 0;
	while (true) {
		HRESULT hr = m_decoder->ProcessOutput(0, 1, &output, &status);
		if (hr == MF_E_TRANSFORM_NEED_MORE_INPUT) break;

		if (hr == MF_E_TRANSFORM_STREAM_CHANGE) {
			ComPtr<IMFMediaType> t;
			m_decoder->GetOutputAvailableType(0, 0, &t);
			m_decoder->SetOutputType(0, t.Get(), 0);

			UINT32 w = 0, h = 0;
			MFVideoArea aperture = { 0 };
			UINT32 blobSize = 0;

			if (SUCCEEDED(t->GetBlob(MF_MT_MINIMUM_DISPLAY_APERTURE, (UINT8*)&aperture, sizeof(aperture), &blobSize)) && blobSize == sizeof(aperture)) {
				w = aperture.Area.cx;
				h = aperture.Area.cy;
			}

			if (w == 0 || h == 0) {
				MFGetAttributeSize(t.Get(), MF_MT_FRAME_SIZE, &w, &h);
			}

			if (m_width != w || m_height != h)
			{
				m_width = w;
				m_height = h;
				LogWithThread("DECODER", "Resolution Change Detected");
				try { OnResolutionChanged(m_width, m_height); }
				catch (...) {}
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
						UINT subIndex = 0;
						dxgiBuf->GetSubresourceIndex(&subIndex);
						RenderFrame(decoderTex.Get(), subIndex);
					}
				}
			}
		}
		if (output.pSample) output.pSample->Release();
		if (output.pEvents) output.pEvents->Release();
	}
}

void VideoEngine::RenderFrame(ID3D11Texture2D* decoderTex, UINT subIndex) {
	if (!m_swapChain || !m_videoProcessor || !m_videoContext) return;

	ComPtr<ID3D11Texture2D> backBuffer;
	if (FAILED(m_swapChain->GetBuffer(0, IID_PPV_ARGS(&backBuffer)))) return;

	D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC outputViewDesc = { D3D11_VPOV_DIMENSION_TEXTURE2D };
	ComPtr<ID3D11VideoProcessorOutputView> outputView;
	if (FAILED(m_videoDevice->CreateVideoProcessorOutputView(backBuffer.Get(), m_videoProcessorEnum.Get(), &outputViewDesc, &outputView))) {
		return;
	}

	D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC inputViewDesc = { 0 };
	inputViewDesc.ViewDimension = D3D11_VPIV_DIMENSION_TEXTURE2D;
	inputViewDesc.Texture2D.ArraySlice = subIndex;
	ComPtr<ID3D11VideoProcessorInputView> inputView;
	if (FAILED(m_videoDevice->CreateVideoProcessorInputView(decoderTex, m_videoProcessorEnum.Get(), &inputViewDesc, &inputView))) {
		return;
	}

	D3D11_VIDEO_PROCESSOR_STREAM stream = { 0 };
	stream.Enable = TRUE;
	stream.pInputSurface = inputView.Get();


	RECT sourceRect = { 0, 0, (LONG)m_width, (LONG)m_height };
	m_videoContext->VideoProcessorSetStreamSourceRect(m_videoProcessor.Get(), 0, TRUE, &sourceRect);
	m_videoContext->VideoProcessorSetStreamDestRect(m_videoProcessor.Get(), 0, FALSE, nullptr);

	HRESULT hr = m_videoContext->VideoProcessorBlt(m_videoProcessor.Get(), outputView.Get(), 0, 1, &stream);

	if (SUCCEEDED(hr)) {
		m_swapChain->Present(0, 0);
	}
}

bool VideoEngine::InitDX11() {
	D3D_FEATURE_LEVEL levels[] = { D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0, D3D_FEATURE_LEVEL_9_3 };
	D3D_FEATURE_LEVEL actualLevel;
	UINT flags = D3D11_CREATE_DEVICE_VIDEO_SUPPORT | D3D11_CREATE_DEVICE_BGRA_SUPPORT;

	HRESULT hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, flags, levels, ARRAYSIZE(levels), D3D11_SDK_VERSION, &m_d3dDevice, &actualLevel, &m_d3dContext);
	if (FAILED(hr)) {
		hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_WARP, nullptr, flags, levels, ARRAYSIZE(levels), D3D11_SDK_VERSION, &m_d3dDevice, &actualLevel, &m_d3dContext);
	}
	if (FAILED(hr)) { Log("Failed to create D3D11 Device."); return false; }

	if (FAILED(m_d3dDevice.As(&m_videoDevice))) { Log("Failed to query for ID3D11VideoDevice."); return false; }
	if (FAILED(m_d3dContext.As(&m_videoContext))) { Log("Failed to query for ID3D11VideoContext."); return false; }

	ComPtr<ID3D10Multithread> multithread;
	if (SUCCEEDED(m_d3dDevice.As(&multithread))) {
		multithread->SetMultithreadProtected(TRUE);
	}
	MFCreateDXGIDeviceManager(&m_resetToken, &m_dxgiManager);
	m_dxgiManager->ResetDevice(m_d3dDevice.Get(), m_resetToken);

	D3D11_VIDEO_PROCESSOR_CONTENT_DESC desc = {};
	desc.InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;
	desc.InputFrameRate.Numerator = 60; desc.InputFrameRate.Denominator = 1;
	desc.InputWidth = m_width; desc.InputHeight = m_height;
	desc.OutputFrameRate.Numerator = 60; desc.OutputFrameRate.Denominator = 1;
	desc.OutputWidth = m_width; desc.OutputHeight = m_height;
	desc.Usage = D3D11_VIDEO_USAGE_PLAYBACK_NORMAL;

	if (FAILED(m_videoDevice->CreateVideoProcessorEnumerator(&desc, &m_videoProcessorEnum))) { Log("Failed to create Video Processor Enumerator."); return false; }
	if (FAILED(m_videoDevice->CreateVideoProcessor(m_videoProcessorEnum.Get(), 0, &m_videoProcessor))) { Log("Failed to create Video Processor."); return false; }

	return true;
}

bool VideoEngine::InitDecoder(uint32_t width, uint32_t height) {
	HRESULT hr = CoCreateInstance(CLSID_CMSH264DecoderMFT_Local, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&m_decoder));
	if (FAILED(hr)) { Log("Failed to create H264 MFT decoder."); return false; }
	ComPtr<IMFAttributes> mftAttr;
	if (SUCCEEDED(m_decoder->GetAttributes(&mftAttr))) {
		mftAttr->SetUINT32(MF_LOW_LATENCY, 1);
		mftAttr->SetUINT32(CODECAPI_AVLowLatencyMode_Local, 1);
	}
	m_decoder->ProcessMessage(MFT_MESSAGE_SET_D3D_MANAGER, (ULONG_PTR)m_dxgiManager.Get());
	ComPtr<IMFMediaType> inType;
	MFCreateMediaType(&inType);
	inType->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
	inType->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_H264);
	MFSetAttributeSize(inType.Get(), MF_MT_FRAME_SIZE, width, height);
	inType->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
	MFSetAttributeRatio(inType.Get(), MF_MT_FRAME_RATE, 60, 1);
	MFSetAttributeRatio(inType.Get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
	m_decoder->SetInputType(0, inType.Get(), 0);
	ComPtr<IMFMediaType> outType;
	MFCreateMediaType(&outType);
	outType->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
	outType->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_NV12);
	MFSetAttributeSize(outType.Get(), MF_MT_FRAME_SIZE, width, height);
	outType->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
	MFSetAttributeRatio(outType.Get(), MF_MT_FRAME_RATE, 60, 1);
	MFSetAttributeRatio(outType.Get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
	m_decoder->SetOutputType(0, outType.Get(), 0);
	m_decoder->ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0);
	m_decoder->ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0);
	return true;
}

void VideoEngine::SetPanel(Platform::Object^ panel) {
	if (!m_d3dDevice) return;
	ComPtr<IInspectable> insp = reinterpret_cast<IInspectable*>(panel);
	if (SUCCEEDED(insp.As(&m_panelNative))) CreateSwapChain(m_width, m_height);
}

void VideoEngine::CreateSwapChain(uint32_t width, uint32_t height) {
	if (!m_panelNative || !m_d3dDevice) return;
	DXGI_SWAP_CHAIN_DESC1 desc = { 0 };
	desc.Width = width; desc.Height = height; desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM; desc.Stereo = false;
	desc.SampleDesc.Count = 1; desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT; desc.BufferCount = 2;
	desc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL; desc.Scaling = DXGI_SCALING_STRETCH;
	ComPtr<IDXGIDevice> dxgiDevice; m_d3dDevice.As(&dxgiDevice);
	ComPtr<IDXGIAdapter> adapter; dxgiDevice->GetAdapter(&adapter);
	ComPtr<IDXGIFactory2> factory; adapter->GetParent(IID_PPV_ARGS(&factory));
	factory->CreateSwapChainForComposition(m_d3dDevice.Get(), &desc, nullptr, &m_swapChain);
	ComPtr<IDXGISwapChain2> swapChain2;
	if (SUCCEEDED(m_swapChain.As(&swapChain2))) swapChain2->SetMaximumFrameLatency(1);
	m_panelNative->SetSwapChain(m_swapChain.Get());
}