#include "pch.h"
#include "ScrcpyController.h"
#include <codecvt>
#include <ppltasks.h>
#include <windows.system.threading.h>
#include <windows.security.cryptography.h>
#include <mfapi.h>
#include <mferror.h>
#include <codecapi.h>

using namespace ScrcpyVideoEngine;
using namespace Platform;
using namespace Windows::Foundation;
using namespace Windows::Storage::Streams;
using namespace Windows::Security::Cryptography;
using namespace Windows::System::Threading;
using namespace Windows::UI::Core;
using namespace concurrency;
using namespace Microsoft::WRL;

#define A_CNXN 0x4e584e43
#define A_OPEN 0x4e45504f
#define A_OKAY 0x59414b4f
#define A_WRTE 0x45545257
#define A_CLSE 0x45534c43
#define A_AUTH 0x48545541

const GUID CLSID_CMSH264DecoderMFT_Local = { 0x62CE7E72, 0x4C71, 0x4d20, { 0xB1, 0x5D, 0x45, 0x28, 0x31, 0xA8, 0x7D, 0x9D } };
const GUID CODECAPI_AVLowLatencyMode_Local = { 0x9c27fa99, 0x6272, 0x4dc5, { 0x9c, 0x9b, 0xb9, 0x29, 0x18, 0x8d, 0x48, 0x2a } };

#pragma pack(push, 1)
struct AdbPacketHeader {
	uint32_t command;
	uint32_t arg0;
	uint32_t arg1;
	uint32_t data_length;
	uint32_t data_crc32;
	uint32_t magic;
};
#pragma pack(pop)

bool RecvExact(SOCKET s, void* buf, size_t len) {
	char* p = (char*)buf; size_t total = 0;
	while (total < len) {
		int r = recv(s, p + total, (int)(len - total), 0);
		if (r <= 0) return false;
		total += r;
	}
	return true;
}

std::string GenerateScid() {
	static bool seeded = false;
	if (!seeded) { srand((unsigned int)time(NULL)); seeded = true; }
	const char hex_f[] = "01234567";
	const char hex_r[] = "0123456789abcdef";
	std::string scid = "";
	scid += hex_f[rand() % 8];
	for (int i = 0; i < 7; ++i) scid += hex_r[rand() % 16];
	return scid;
}

static const std::string base64_chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
std::string Base64Encode(const uint8_t* buf, size_t bufLen) {
	std::string ret; int i = 0, j = 0; uint8_t c3[3], c4[4];
	while (bufLen--) {
		c3[i++] = *(buf++);
		if (i == 3) {
			c4[0] = (c3[0] & 0xfc) >> 2;
			c4[1] = ((c3[0] & 0x03) << 4) + ((c3[1] & 0xf0) >> 4);
			c4[2] = ((c3[1] & 0x0f) << 2) + ((c3[2] & 0xc0) >> 6);
			c4[3] = c3[2] & 0x3f;
			for (i = 0; (i < 4); i++) ret += base64_chars[c4[i]];
			i = 0;
		}
	}
	if (i) {
		for (j = i; j < 3; j++) c3[j] = '\0';
		c4[0] = (c3[0] & 0xfc) >> 2;
		c4[1] = ((c3[0] & 0x03) << 4) + ((c3[1] & 0xf0) >> 4);
		c4[2] = ((c3[1] & 0x0f) << 2) + ((c3[2] & 0xc0) >> 6);
		c4[3] = c3[2] & 0x3f;
		for (j = 0; (j < i + 1); j++) ret += base64_chars[c4[j]];
		while (i++ < 3) ret += '=';
	}
	return ret;
}


ScrcpyController::ScrcpyController()
	: m_socket(INVALID_SOCKET), m_running(false), m_isInitialized(false), m_recvAction(nullptr), 
	m_decoderAction(nullptr), m_connectPromise(nullptr), m_enableVideo(true), m_localIdCounter(1), 
	m_videoLocalId(0), m_controlLocalId(0), m_serverLocalId(0), m_videoStage(0),m_videoReadPos(0), 
	m_authAttempted(false), m_dispatcher(nullptr), m_width(0), m_height(0), m_baselinePts(-1), m_resetToken(0)
{
	MFStartup(MF_VERSION);
	m_recvBuffer.resize(65536 + 24);
	m_videoBuffer.reserve(1024 * 512);
}

ScrcpyController::~ScrcpyController()
{
	Stop();
	MFShutdown();
}

void ScrcpyController::SetDispatcher(CoreDispatcher^ dispatcher) { m_dispatcher = dispatcher; }

void ScrcpyController::SetPanel(Object^ panel)
{
	// Simply save the native interface for the panel.
	// We will use this later when the video is initialized.
	ComPtr<IInspectable> insp = reinterpret_cast<IInspectable*>(panel);
	if (insp != nullptr) {
		insp.As(&m_panelNative);
	}
}

bool ScrcpyController::Connect(String^ ip, int port)
{
	Stop();
	m_authAttempted = false;
	WSADATA wsaData; if (WSAStartup(MAKEWORD(2, 2), &wsaData) != 0) return false;
	m_socket = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
	if (m_socket == INVALID_SOCKET) return false;

	int rcvBufSize = 512 * 1024; // 512KB Buffer
	setsockopt(m_socket, SOL_SOCKET, SO_RCVBUF, (char*)&rcvBufSize, sizeof(rcvBufSize));

	std::wstring_convert<std::codecvt_utf8<wchar_t>> conv;
	std::string ip_str = conv.to_bytes(ip->Data());

	sockaddr_in addr;
	addr.sin_family = AF_INET;
	addr.sin_addr.s_addr = inet_addr(ip_str.c_str());
	addr.sin_port = htons(port);

	if (connect(m_socket, (sockaddr*)&addr, sizeof(addr)) == SOCKET_ERROR) {
		closesocket(m_socket);
		m_socket = INVALID_SOCKET;
		WSACleanup();
		return false;
	}

	m_running = true;
	m_connectPromise = new std::promise<bool>();
	std::future<bool> fut = m_connectPromise->get_future();

	m_recvAction = ThreadPool::RunAsync(
		ref new WorkItemHandler([this](IAsyncAction^ action) {
		this->ReceiveLoop();
	}),
		WorkItemPriority::High
		);

	std::string host = "host::\0";
	SendPacket(A_CNXN, 0x01000001, 1024 * 1024, host.data(), (uint32_t)host.size());

	if (fut.wait_for(std::chrono::seconds(5)) == std::future_status::ready) {
		return fut.get();
	}

	Stop();
	return false;
}

void ScrcpyController::Stop()
{
	if (!m_running && !m_isInitialized) return;

	m_running = false;

	if (m_decoderAction != nullptr) {
		m_decoderAction->Cancel();
		m_decoderAction = nullptr;
	}
	if (m_recvAction != nullptr) {
		m_recvAction->Cancel();
		m_recvAction = nullptr;
	}

	if (m_socket != INVALID_SOCKET) {
		shutdown(m_socket, SD_BOTH);
		closesocket(m_socket);
		m_socket = INVALID_SOCKET;
	}

	m_queueCv.notify_all();

	if (m_dispatcher)
	{
		m_dispatcher->RunAsync(CoreDispatcherPriority::Normal, ref new DispatchedHandler([this]() {
			std::lock_guard<std::mutex> lock(m_renderMutex);
			if (m_panelNative) {
				m_panelNative->SetSwapChain(nullptr);
			}

			// --- ADD THESE LINES ---
			m_cachedOutputView.Reset();
			m_cachedBackBuffer.Reset();
			// -----------------------

			m_swapChain.Reset();
			m_decoder.Reset();
			m_videoProcessor.Reset();
			m_videoProcessorEnum.Reset();
		}));
	}

	{ std::lock_guard<std::mutex> lock(m_queueMutex); std::queue<PacketData> empty; std::swap(m_packetQueue, empty); }

	m_isInitialized = false;
	m_baselinePts = -1;
	m_videoReadPos = 0;
	m_videoStage = 1;
	m_videoBuffer.clear();
	m_pendingConfig.clear();
	m_localToRemote.clear();
	m_pendingOpens.clear();
	m_pendingCloses.clear();
	m_videoLocalId = 0;
	m_controlLocalId = 0;
	m_serverLocalId = 0;

	WSACleanup();
}

void ScrcpyController::DeployServer(const Array<byte>^ jarData) {
	if (jarData != nullptr) {
		std::vector<uint8_t> dataVec = { jarData->Data, jarData->Data + jarData->Length };
		ExecuteShellCommand("rm /data/local/tmp/scrcpy-server.jar");
		std::string b64 = Base64Encode(dataVec.data(), dataVec.size());
		size_t offset = 0, total = b64.size();
		while (offset < total && m_running) {
			size_t len = (std::min)((size_t)1024, total - offset);
			ExecuteShellCommand("echo -n \"" + b64.substr(offset, len) + "\" " + (offset == 0 ? ">" : ">>") + " /data/local/tmp/scrcpy.b64");
			offset += len;
		}
		ExecuteShellCommand("base64 -d /data/local/tmp/scrcpy.b64 > /data/local/tmp/scrcpy-server.jar");
		ExecuteShellCommand("rm /data/local/tmp/scrcpy.b64");
	}
}

void ScrcpyController::StartScrcpy(int bitRate, int maxSize, int maxFps, bool video, bool uhid) {
	m_scid = GenerateScid();
	m_videoStage = 1;
	m_videoReadPos = 0;
	m_enableVideo = video;
	m_enableUhid = uhid; // <--- Store it

	m_pendingConfig.clear();
	m_videoBuffer.clear();
	m_videoBuffer.reserve(2 * 1024 * 1024);

	OpenStream("reverse:forward:localabstract:scrcpy_" + m_scid + ";tcp:27183");
	std::this_thread::sleep_for(std::chrono::milliseconds(200));

	std::string vidStr = video ? "true" : "false";

	std::string serverArgs = "log_level=info scid=" + m_scid + " tunnel_forward=false audio=false control=true cleanup=true ";
	serverArgs += "video=" + vidStr + " ";
	serverArgs += "video_bit_rate=" + std::to_string(bitRate) + " ";
	serverArgs += "max_size=" + std::to_string(maxSize) + " ";
	serverArgs += "max_fps=" + std::to_string(maxFps) + " ";
	serverArgs += "send_device_meta=true send_codec_meta=true ";

	// --- FIX: Pass mouse argument ---
	if (uhid) {
		serverArgs += "mouse=uhid ";
	}

	std::string cmd = "shell:CLASSPATH=/data/local/tmp/scrcpy-server.jar app_process / com.genymobile.scrcpy.Server 3.3.3 " + serverArgs;
	m_serverLocalId = OpenStream(cmd);
}

void ScrcpyController::Log(const std::string& msg)
{
	if (m_dispatcher == nullptr) return;
	m_dispatcher->RunAsync(CoreDispatcherPriority::Low, ref new DispatchedHandler([this, msg]() {
		try {
			std::wstring_convert<std::codecvt_utf8<wchar_t>> conv;
			OnLog(ref new String(conv.from_bytes(msg).c_str()));
		}
		catch (...) {}
	}));
}

std::vector<uint8_t> ScrcpyController::PerformSign(const std::vector<uint8_t>& t) {
	if (!AuthSignCallback || m_dispatcher == nullptr) return {};
	task_completion_event<std::vector<uint8_t>> tce;
	m_dispatcher->RunAsync(CoreDispatcherPriority::Normal, ref new DispatchedHandler([this, t, tce]() {
		try {
			auto tokenArray = ref new Array<byte>((byte*)t.data(), (unsigned int)t.size());
			auto resultArray = AuthSignCallback(tokenArray);
			if (resultArray) {
				tce.set({ resultArray->Data, resultArray->Data + resultArray->Length });
			}
			else {
				tce.set({});
			}
		}
		catch (Platform::Exception^ e) {
			tce.set_exception(e);
		}
	}));
	return create_task(tce).get();
}

std::vector<uint8_t> ScrcpyController::GetKey() {
	if (!AuthKeyCallback || m_dispatcher == nullptr) return {};
	task_completion_event<std::vector<uint8_t>> tce;
	m_dispatcher->RunAsync(CoreDispatcherPriority::Normal, ref new DispatchedHandler([this, tce]() {
		try {
			auto resultArray = AuthKeyCallback();
			if (resultArray) {
				tce.set({ resultArray->Data, resultArray->Data + resultArray->Length });
			}
			else {
				tce.set({});
			}
		}
		catch (Platform::Exception^ e) {
			tce.set_exception(e);
		}
	}));
	return create_task(tce).get();
}

void ScrcpyController::ReceiveLoop() {
	try {
		while (m_running) {
			AdbPacketHeader h;
			if (!RecvExact(m_socket, &h, sizeof(AdbPacketHeader))) break;
			if (h.data_length > 0) {
				if (m_recvBuffer.size() < h.data_length) m_recvBuffer.resize(h.data_length);
				if (!RecvExact(m_socket, m_recvBuffer.data(), h.data_length)) break;
			}
			HandlePacket(h.command, h.arg0, h.arg1, h.data_length, m_recvBuffer.data());
		}
	}
	catch (...) {
		Log("Exception in ReceiveLoop.");
	}
	m_running = false;
}

void ScrcpyController::HandlePacket(uint32_t cmd, uint32_t a0, uint32_t a1, uint32_t dlen, const uint8_t* payload) {
	switch (cmd) {
	case A_CNXN:
		if (m_connectPromise) { m_connectPromise->set_value(true); delete m_connectPromise; m_connectPromise = nullptr; }
		break;
	case A_AUTH:
		if (a0 == 1) {
			if (!m_authAttempted) {
				m_authAttempted = true;
				auto sig = PerformSign({ payload, payload + dlen });
				if (!sig.empty()) {
					SendPacket(A_AUTH, 2, 0, sig.data(), (uint32_t)sig.size());
					break;
				}
			}
			auto key = GetKey();
			SendPacket(A_AUTH, 3, 0, key.data(), (uint32_t)key.size());
		}
		break;
	case A_OPEN: {
		uint32_t rid = a0, lid = ++m_localIdCounter;
		{ std::lock_guard<std::mutex> lock(m_mapMutex); m_localToRemote[lid] = rid; }

		if (m_enableVideo) {
			if (m_videoLocalId == 0) m_videoLocalId = lid;
			else m_controlLocalId = lid;
		}
		else {
			m_controlLocalId = lid;
			m_videoLocalId = 0;
		}

		SendPacket(A_OKAY, rid, lid, nullptr, 0);

		// --- NEW: Auto-Enable Mouse when connection is ready ---
		if (m_enableUhid && lid == m_controlLocalId) {
			SendHidCreateMouse();
		}
		// -------------------------------------------------------
		break;
	}
	case A_OKAY: {
		{ std::lock_guard<std::mutex> lock(m_mapMutex); m_localToRemote[a1] = a0; }
		std::lock_guard<std::mutex> lock(m_pendingMutex);
		if (m_pendingOpens.count(a1)) { m_pendingOpens[a1]->set_value(true); m_pendingOpens.erase(a1); }
		break;
	}
	case A_WRTE: {
		// Always acknowledge the write immediately
		SendPacket(A_OKAY, a0, a1, nullptr, 0);

		if (a1 == m_serverLocalId) {
			// Server stdout logging (optional)
			// Log("[SERVER] " + std::string((char*)payload, dlen));
		}
		// ONLY process video data if Video is enabled AND the ID matches the video stream
		else if (m_enableVideo && a1 == m_videoLocalId) {
			m_videoBuffer.insert(m_videoBuffer.end(), payload, payload + dlen);
			bool work = true;
			while (work) {
				work = false;
				size_t available = m_videoBuffer.size() - m_videoReadPos;
				uint8_t* ptr = m_videoBuffer.data() + m_videoReadPos;

				// Stage 1: Device Name (64 bytes) - Skip
				if (m_videoStage == 1 && available >= 64) {
					m_videoReadPos += 64;
					m_videoStage = 2;
					work = true;
				}
				// Stage 2: Resolution Metadata (12 bytes)
				else if (m_videoStage == 2 && available >= 12) {
					// Format: [CodecID 4][Width 4][Height 4]
					uint32_t w = ReadBE32(ptr + 4);
					uint32_t h = ReadBE32(ptr + 8);

					if (m_dispatcher) {
						m_dispatcher->RunAsync(CoreDispatcherPriority::Normal, ref new DispatchedHandler([this, w, h]() {
							OnResolutionChanged(w, h);
						}));
					}
					m_videoReadPos += 12;
					m_videoStage = 3;
					work = true;
				}
				// Stage 3: Video Frame
				else if (m_videoStage == 3 && available >= 12) {
					uint64_t ptsData = ReadBE64(ptr);
					uint32_t pSize = ReadBE32(ptr + 8); // Packet Size

					if (available >= (12 + pSize)) {
						bool isConfig = (ptsData & 0x8000000000000000) != 0;
						int64_t ptsUs = ptsData & 0x3FFFFFFFFFFFFFFF;
						const uint8_t* packetStart = ptr + 12;

						if (isConfig) {
							// Store SPS/PPS for the next IDR frame
							m_pendingConfig.assign(packetStart, packetStart + pSize);
						}
						else {
							DWORD configSize = (DWORD)m_pendingConfig.size();
							DWORD totalSize = pSize + configSize;

							// Create DirectX Buffer directly
							ComPtr<IMFMediaBuffer> mediaBuffer;
							HRESULT hr = MFCreateMemoryBuffer(totalSize, &mediaBuffer);

							if (SUCCEEDED(hr)) {
								BYTE* dest = nullptr;
								mediaBuffer->Lock(&dest, nullptr, nullptr);

								// Prepend Config if available
								if (configSize > 0) {
									memcpy(dest, m_pendingConfig.data(), configSize);
									dest += configSize;
									m_pendingConfig.clear();
								}

								// Copy Frame Data
								memcpy(dest, packetStart, pSize);

								mediaBuffer->Unlock();
								mediaBuffer->SetCurrentLength(totalSize);

								PushFrame(mediaBuffer, ptsUs);
							}
						}
						m_videoReadPos += (12 + pSize);
						work = true;
					}
				}
			}
			if (work) CompactVideoBuffer();
		}
		else if (a1 == m_controlLocalId) {
			// In Controls-Only mode, metadata arrives here.
			// Since we don't need to parse it for video, we simply do nothing.
			// The packet is acknowledged (A_OKAY sent above) and dropped, 
			// keeping the connection alive without corrupting buffers.
		}
		break;
	}
	case A_CLSE: {
		SendPacket(A_OKAY, a0, a1, nullptr, 0);
		std::lock_guard<std::mutex> lock(m_pendingMutex);
		if (m_pendingCloses.count(a1)) { m_pendingCloses[a1]->set_value(true); m_pendingCloses.erase(a1); }
		break;
	}
	}
}

uint32_t ScrcpyController::OpenStream(const std::string& dest) {
	if (!m_running) return 0;
	uint32_t lid = ++m_localIdCounter;
	std::string req = dest + '\0';
	SendPacket(A_OPEN, lid, 0, req.data(), (uint32_t)req.size());
	return lid;
}

bool ScrcpyController::ExecuteShellCommand(const std::string& command) {
	if (!m_running) return false;
	uint32_t lid = ++m_localIdCounter;
	auto op = new std::promise<bool>();
	auto cl = new std::promise<bool>();
	{ std::lock_guard<std::mutex> lock(m_pendingMutex); m_pendingOpens[lid] = op; m_pendingCloses[lid] = cl; }
	std::string req = "shell:" + command + '\0';
	SendPacket(A_OPEN, lid, 0, req.data(), (uint32_t)req.size());
	bool success = op->get_future().wait_for(std::chrono::seconds(5)) == std::future_status::ready;
	if (success) {
		success = cl->get_future().wait_for(std::chrono::seconds(10)) == std::future_status::ready;
	}
	delete op; delete cl;
	return success;
}

bool ScrcpyController::SendPacket(uint32_t cmd, uint32_t a0, uint32_t a1, const void* data, size_t len) {
	if (m_socket == INVALID_SOCKET || !m_running) return false;
	AdbPacketHeader h = { cmd, a0, a1, (uint32_t)len, 0, cmd ^ 0xFFFFFFFF };
	std::vector<uint8_t> pkt(sizeof(AdbPacketHeader) + len);
	memcpy(pkt.data(), &h, sizeof(AdbPacketHeader));
	if (len > 0) {
		memcpy(pkt.data() + sizeof(AdbPacketHeader), data, len);
	}
	send(m_socket, (char*)pkt.data(), (int)pkt.size(), 0);
	return true;
}

uint64_t ScrcpyController::ReadBE64(const uint8_t* d) { return ((uint64_t)d[0] << 56) | ((uint64_t)d[1] << 48) | ((uint64_t)d[2] << 40) | ((uint64_t)d[3] << 32) | ((uint64_t)d[4] << 24) | ((uint64_t)d[5] << 16) | ((uint64_t)d[6] << 8) | (uint64_t)d[7]; }
uint32_t ScrcpyController::ReadBE32(const uint8_t* d) { return ((uint32_t)d[0] << 24) | ((uint32_t)d[1] << 16) | ((uint32_t)d[2] << 8) | (uint32_t)d[3]; }

void ScrcpyController::CompactVideoBuffer() {
	// OPTIMIZATION: "Lazy Compaction"
	// Don't shift memory after every packet. Moving memory is O(N) and expensive.
	// Only shift when we have a significant amount of dead space (e.g., 256KB).
	// This dramatically reduces CPU usage on Snapdragon 400.
	const size_t COMPACT_THRESHOLD = 256 * 1024; // 256KB

	if (m_videoReadPos > COMPACT_THRESHOLD) {
		if (m_videoBuffer.size() > m_videoReadPos) {
			// Move only the remaining data to the front
			m_videoBuffer.erase(m_videoBuffer.begin(), m_videoBuffer.begin() + m_videoReadPos);
		}
		else {
			m_videoBuffer.clear();
		}
		m_videoReadPos = 0;
	}
	// If we haven't reached the threshold, do nothing. 
	// We just keep appending to the vector and reading via the index.
}

void ScrcpyController::ApplyResolutionChange(uint32_t width, uint32_t height)
{
	if (m_width == width && m_height == height) return;

	// OPTIMIZATION: Release cached views immediately before resizing
	m_cachedOutputView.Reset();
	m_cachedBackBuffer.Reset();

	m_width = width;
	m_height = height;

	if (m_d3dContext) {
		m_d3dContext->OMSetRenderTargets(0, nullptr, nullptr);
		m_d3dContext->ClearState();
		m_d3dContext->Flush();
	}

	if (m_swapChain) {
		// ResizeBuffers requires all references to backbuffers to be released
		m_swapChain->ResizeBuffers(2, width, height, DXGI_FORMAT_B8G8R8A8_UNORM, DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING);
	}

	// Recreate Processor for new size
	if (m_videoDevice) {
		m_videoProcessorEnum.Reset();
		m_videoProcessor.Reset();
		// ... (Create Video Processor logic with new Width/Height) ...
		D3D11_VIDEO_PROCESSOR_CONTENT_DESC desc = {};
		desc.InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;
		desc.InputFrameRate.Numerator = 60; desc.InputFrameRate.Denominator = 1;
		desc.InputWidth = width; desc.InputHeight = height;
		desc.OutputFrameRate.Numerator = 60; desc.OutputFrameRate.Denominator = 1;
		desc.OutputWidth = width; desc.OutputHeight = height;
		desc.Usage = D3D11_VIDEO_USAGE_OPTIMAL_SPEED;

		m_videoDevice->CreateVideoProcessorEnumerator(&desc, &m_videoProcessorEnum);
		if (m_videoProcessorEnum) {
			m_videoDevice->CreateVideoProcessor(m_videoProcessorEnum.Get(), 0, &m_videoProcessor);
		}
	}
}

void ScrcpyController::InitializeVideo(uint32_t width, uint32_t height)
{
	std::lock_guard<std::mutex> lock(m_renderMutex);

	// If already initialized, just ensure resolution is correct
	if (m_isInitialized) {
		ApplyResolutionChange(width, height);
		return;
	}

	// First time setup
	m_width = width;
	m_height = height;
	Log("Initializing Video Subsystem...");

	m_decoder.Reset();
	if (m_panelNative) m_panelNative->SetSwapChain(nullptr);
	m_swapChain.Reset();

	if (!InitDX11()) { Log("Failed to init DX11."); return; }
	if (!InitDecoder(width, height)) { Log("Failed to init decoder."); return; }

	if (m_panelNative) CreateSwapChain(width, height);

	m_isInitialized = true;
	m_decoderAction = ThreadPool::RunAsync(
		ref new WorkItemHandler([this](IAsyncAction^ action) {
		this->DecoderLoop();
	}), WorkItemPriority::High);
}

void ScrcpyController::PushFrame(ComPtr<IMFMediaBuffer> buf, int64_t raw_pts) {
	if (!buf || !m_running) return;
	PacketData packet;
	packet.pts = raw_pts;
	packet.mediaBuffer = buf; // Direct copy of pointer
	{
		std::lock_guard<std::mutex> lock(m_queueMutex);
		m_packetQueue.push(packet);
	}
	m_queueCv.notify_one();
}

// In ScrcpyController.cpp

void ScrcpyController::DecoderLoop() {
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

		// --- INSERT THIS (Zero Copy usage) ---

		ComPtr<IMFSample> sample;
		MFCreateSample(&sample);

		// Use the buffer we created in HandlePacket directly!
		sample->AddBuffer(packet.mediaBuffer.Get());

		sample->SetSampleDuration(0);
		sample->SetSampleTime((packet.pts - m_baselinePts) * 10);

		HRESULT hr = m_decoder->ProcessInput(0, sample.Get(), 0);
		if (SUCCEEDED(hr) || hr == MF_E_NOTACCEPTING) {
			ProcessDecodedOutput();
		}
	}
}

void ScrcpyController::ProcessDecodedOutput() {
	MFT_OUTPUT_DATA_BUFFER output = { 0 };
	DWORD status = 0;
	while (m_running) {
		HRESULT hr = m_decoder->ProcessOutput(0, 1, &output, &status);
		if (hr == MF_E_TRANSFORM_NEED_MORE_INPUT) break;

		if (hr == MF_E_TRANSFORM_STREAM_CHANGE) {
			ComPtr<IMFMediaType> t;
			if (SUCCEEDED(m_decoder->GetOutputAvailableType(0, 0, &t))) {
				m_decoder->SetOutputType(0, t.Get(), 0);

				// --- FIX STARTS HERE ---
				UINT32 w = 0, h = 0;

				// 1. Try to get the defined Display Aperture (The clean area without green padding)
				MFVideoArea aperture = { 0 };
				UINT32 blobSize = 0;
				HRESULT hrAperture = t->GetBlob(MF_MT_MINIMUM_DISPLAY_APERTURE, (UINT8*)&aperture, sizeof(aperture), &blobSize);

				if (SUCCEEDED(hrAperture) && blobSize == sizeof(aperture) && aperture.Area.cx > 0 && aperture.Area.cy > 0) {
					w = aperture.Area.cx;
					h = aperture.Area.cy;
				}
				else {
					// 2. Fallback to Frame Size if Aperture is missing (usually includes padding)
					MFGetAttributeSize(t.Get(), MF_MT_FRAME_SIZE, &w, &h);
				}

				// Apply the Clean Resolution
				if (w > 0 && h > 0) {
					// This will resize the SwapChain to the exact "Clean" size
					// prohibiting the green bars from being part of the render target.
					ApplyResolutionChange(w, h);

					// Optional: Notify C# of the "Real" decoded size if needed
					if (m_dispatcher) {
						m_dispatcher->RunAsync(CoreDispatcherPriority::Normal, ref new DispatchedHandler([this, w, h]() {
							OnResolutionChanged(w, h);
						}));
					}
				}
				// --- FIX ENDS HERE ---
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
		if (output.pSample) { output.pSample->Release(); output.pSample = nullptr; }
		if (output.pEvents) { output.pEvents->Release(); output.pEvents = nullptr; }
	}
}

void ScrcpyController::RenderFrame(ID3D11Texture2D* decoderTex, UINT subIndex) {
	if (!m_swapChain || !m_videoProcessor || !m_videoContext) return;

	// OPTIMIZATION: Create OutputView only if it doesn't exist or was reset
	if (!m_cachedOutputView) {
		HRESULT hr = m_swapChain->GetBuffer(0, IID_PPV_ARGS(&m_cachedBackBuffer));
		if (FAILED(hr)) return;

		D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC outputViewDesc = { D3D11_VPOV_DIMENSION_TEXTURE2D };

		hr = m_videoDevice->CreateVideoProcessorOutputView(
			m_cachedBackBuffer.Get(),
			m_videoProcessorEnum.Get(),
			&outputViewDesc,
			&m_cachedOutputView
		);

		if (FAILED(hr)) {
			// If creation fails, reset buffer to try again next frame
			m_cachedBackBuffer.Reset();
			return;
		}
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

	// Use the Cached Output View
	HRESULT hr = m_videoContext->VideoProcessorBlt(m_videoProcessor.Get(), m_cachedOutputView.Get(), 0, 1, &stream);

	if (SUCCEEDED(hr)) {
		// Sync interval 0 for lowest latency (tearing allowed)
		m_swapChain->Present(0, DXGI_PRESENT_ALLOW_TEARING);
	}
}

bool ScrcpyController::InitDX11() {
	if (!m_d3dDevice) {
		D3D_FEATURE_LEVEL levels[] = { D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0, D3D_FEATURE_LEVEL_9_3 };
		UINT flags = D3D11_CREATE_DEVICE_VIDEO_SUPPORT | D3D11_CREATE_DEVICE_BGRA_SUPPORT;
		ComPtr<ID3D11Device> device;
		ComPtr<ID3D11DeviceContext> context;
		D3D_FEATURE_LEVEL featureLevel;
		HRESULT hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, flags, levels, ARRAYSIZE(levels), D3D11_SDK_VERSION, &device, &featureLevel, &context);
		if (FAILED(hr)) return false;

		m_d3dDevice = device;
		m_d3dContext = context;

		if (FAILED(m_d3dDevice.As(&m_videoDevice))) return false;
		if (FAILED(m_d3dContext.As(&m_videoContext))) return false;

		ComPtr<ID3D10Multithread> multithread;
		if (SUCCEEDED(m_d3dDevice.As(&multithread))) {
			multithread->SetMultithreadProtected(TRUE);
		}

		MFCreateDXGIDeviceManager(&m_resetToken, &m_dxgiManager);
		m_dxgiManager->ResetDevice(m_d3dDevice.Get(), m_resetToken);
	}

	m_videoProcessorEnum.Reset();
	m_videoProcessor.Reset();

	D3D11_VIDEO_PROCESSOR_CONTENT_DESC desc = {};
	desc.InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;
	desc.InputFrameRate.Numerator = 60; desc.InputFrameRate.Denominator = 1;
	desc.InputWidth = m_width; desc.InputHeight = m_height;
	desc.OutputFrameRate.Numerator = 60; desc.OutputFrameRate.Denominator = 1;
	desc.OutputWidth = m_width; desc.OutputHeight = m_height;
	desc.Usage = D3D11_VIDEO_USAGE_PLAYBACK_NORMAL;

	if (FAILED(m_videoDevice->CreateVideoProcessorEnumerator(&desc, &m_videoProcessorEnum))) return false;
	if (FAILED(m_videoDevice->CreateVideoProcessor(m_videoProcessorEnum.Get(), 0, &m_videoProcessor))) return false;

	return true;
}

bool ScrcpyController::InitDecoder(uint32_t width, uint32_t height) {
	HRESULT hr = CoCreateInstance(CLSID_CMSH264DecoderMFT_Local, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&m_decoder));
	if (FAILED(hr)) return false;
	ComPtr<IMFAttributes> mftAttr;
	if (SUCCEEDED(m_decoder->GetAttributes(&mftAttr))) {
		mftAttr->SetUINT32(MF_LOW_LATENCY, 1);
		mftAttr->SetUINT32(CODECAPI_AVLowLatencyMode_Local, 1);

		mftAttr->SetUINT32(CODECAPI_AVDecVideoMaxCodedWidth, width);
		mftAttr->SetUINT32(CODECAPI_AVDecVideoMaxCodedHeight, height);
	}

	m_decoder->ProcessMessage(MFT_MESSAGE_SET_D3D_MANAGER, (ULONG_PTR)m_dxgiManager.Get());
	ComPtr<IMFMediaType> inType;
	MFCreateMediaType(&inType);
	inType->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
	inType->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_H264);
	MFSetAttributeSize(inType.Get(), MF_MT_FRAME_SIZE, width, height);
	m_decoder->SetInputType(0, inType.Get(), 0);

	ComPtr<IMFMediaType> outType;
	MFCreateMediaType(&outType);
	outType->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
	outType->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_NV12);
	MFSetAttributeSize(outType.Get(), MF_MT_FRAME_SIZE, width, height);
	m_decoder->SetOutputType(0, outType.Get(), 0);

	m_decoder->ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0);
	m_decoder->ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0);
	return true;
}

void ScrcpyController::CreateSwapChain(uint32_t width, uint32_t height) {
	if (!m_panelNative || !m_d3dDevice) return;

	if (m_swapChain) {
		m_d3dContext->ClearState();
		m_d3dContext->Flush();
		HRESULT hr = m_swapChain->ResizeBuffers(2, width, height, DXGI_FORMAT_B8G8R8A8_UNORM, DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING);
		if (SUCCEEDED(hr)) {
			return;
		}
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
	m_panelNative->SetSwapChain(m_swapChain.Get());
}

// [ScrcpyController.cpp] - Add these new implementations

// --- Private Helpers ---

void ScrcpyController::WriteBE32(uint8_t* b, uint32_t val) {
	b[0] = (uint8_t)(val >> 24);
	b[1] = (uint8_t)(val >> 16);
	b[2] = (uint8_t)(val >> 8);
	b[3] = (uint8_t)val;
}

void ScrcpyController::WriteBE16(uint8_t* b, uint16_t val) {
	b[0] = (uint8_t)(val >> 8);
	b[1] = (uint8_t)val;
}

bool ScrcpyController::SendControlMsg(const std::vector<uint8_t>& msg) {
	if (!m_running || m_controlLocalId == 0) return false;

	// 1. Look up the Remote ID for the Control Stream
	uint32_t remoteId = 0;
	{
		std::lock_guard<std::mutex> lock(m_mapMutex);
		auto it = m_localToRemote.find(m_controlLocalId);
		if (it != m_localToRemote.end()) {
			remoteId = it->second;
		}
	}

	if (remoteId == 0) return false; // Control stream not ready yet

	// 2. Wrap in ADB A_WRTE packet
	// A_WRTE: arg0 = local_id, arg1 = remote_id
	return SendPacket(A_WRTE, m_controlLocalId, remoteId, msg.data(), msg.size());
}


// --- Public Control Methods ---

void ScrcpyController::InjectTouch(int action, int pointerId, int x, int y, int width, int height, float pressure, int buttons) {
	// Protocol: [TYPE 1][ACTION 1][PTR_ID 8][X 4][Y 4][W 2][H 2][PRESSURE 2][BUTTONS 4]
	// Total: 1 + 1 + 8 + 4 + 4 + 2 + 2 + 2 + 4 = 28 bytes (+ 4 padding?) -> 32 bytes usually

	std::vector<uint8_t> p(32); // 32 bytes fixed size for touch
	p[0] = SC_CONTROL_MSG_TYPE_INJECT_TOUCH_EVENT;
	p[1] = (uint8_t)action;

	// Pointer ID (8 bytes) - Using just the lower 32 bits for now as int, padded
	p[2] = 0; p[3] = 0; p[4] = 0; p[5] = 0;
	WriteBE32(&p[6], (uint32_t)pointerId);

	WriteBE32(&p[10], (uint32_t)x);
	WriteBE32(&p[14], (uint32_t)y);
	WriteBE16(&p[18], (uint16_t)width);
	WriteBE16(&p[20], (uint16_t)height);

	// Pressure (0 to 1 float -> 0 to 65535 uint16)
	uint16_t pressureFixed = (uint16_t)(pressure * 65535.0f);
	WriteBE16(&p[22], pressureFixed);

	WriteBE32(&p[24], (uint32_t)buttons);

	SendControlMsg(p);
}

void ScrcpyController::InjectScroll(int x, int y, int width, int height, int hScroll, int vScroll, int buttons) {
	// Protocol: [TYPE 1][X 4][Y 4][W 2][H 2][SCROLL_X 2][SCROLL_Y 2][BUTTONS 4]
	// Total: 21 bytes

	std::vector<uint8_t> p(21);
	p[0] = SC_CONTROL_MSG_TYPE_INJECT_SCROLL_EVENT;

	WriteBE32(&p[1], (uint32_t)x);
	WriteBE32(&p[5], (uint32_t)y);
	WriteBE16(&p[9], (uint16_t)width);
	WriteBE16(&p[11], (uint16_t)height);
	WriteBE16(&p[13], (uint16_t)hScroll); // Signed 16-bit
	WriteBE16(&p[15], (uint16_t)vScroll); // Signed 16-bit
	WriteBE32(&p[17], (uint32_t)buttons);

	SendControlMsg(p);
}

void ScrcpyController::InjectBackOrScreenOn(int action) {
	// Protocol: [TYPE 1][ACTION 1]
	std::vector<uint8_t> p(2);
	p[0] = SC_CONTROL_MSG_TYPE_BACK_OR_SCREEN_ON;
	p[1] = (uint8_t)action; // 0=down, 1=up

	SendControlMsg(p);
}


// --- UHID (Mouse Mode) Methods ---

void ScrcpyController::EnableUhidMouse(bool enable) {
	if (enable) {
		SendHidCreateMouse();
	}
	else {
		SendHidDestroyMouse();
	}
}

void ScrcpyController::SendHidCreateMouse() {
	// Protocol: [TYPE 1][ID 2][RET_SIZE 2]... [DESC_SIZE 2][DESC BYTES]

	size_t descLen = sizeof(SC_HID_MOUSE_REPORT_DESC);
	// Header size: 1 (type) + 2 (id) + 2 (res_ret_size) + 2 (res_req_size) + 1 (name_len=0) + 2 (desc_len)
	size_t headerLen = 10;

	std::vector<uint8_t> p(headerLen + descLen);

	int offset = 0;
	p[offset++] = SC_CONTROL_MSG_TYPE_UHID_CREATE;
	WriteBE16(&p[offset], SC_HID_ID_MOUSE); offset += 2;
	WriteBE16(&p[offset], 0); offset += 2; // Report return size
	WriteBE16(&p[offset], 0); offset += 2; // Report req size
	p[offset++] = 0; // Name length (0)

	WriteBE16(&p[offset], (uint16_t)descLen); offset += 2;
	memcpy(&p[offset], SC_HID_MOUSE_REPORT_DESC, descLen);

	SendControlMsg(p);
}

void ScrcpyController::InjectUhidInput(int buttons, int dx, int dy, int vScroll, int hScroll) {
	// Report format defined in SC_HID_MOUSE_REPORT_DESC
	// 5 bytes: [Buttons][X][Y][WheelV][WheelH]

	// Clamp values to -127 to 127 (signed byte range)
	auto clamp = [](int v) -> int8_t {
		if (v > 127) return 127;
		if (v < -127) return -127;
		return (int8_t)v;
	};

	uint8_t report[5];
	report[0] = (uint8_t)buttons;
	report[1] = (uint8_t)clamp(dx);
	report[2] = (uint8_t)clamp(dy);
	report[3] = (uint8_t)clamp(vScroll);
	report[4] = (uint8_t)clamp(hScroll);

	// Protocol: [TYPE 1][ID 2][SIZE 2][PAYLOAD N]
	std::vector<uint8_t> p(5 + 5);

	int offset = 0;
	p[offset++] = SC_CONTROL_MSG_TYPE_UHID_INPUT;
	WriteBE16(&p[offset], SC_HID_ID_MOUSE); offset += 2;
	WriteBE16(&p[offset], 5); offset += 2; // Payload size
	memcpy(&p[offset], report, 5);

	SendControlMsg(p);
}

void ScrcpyController::SendHidDestroyMouse() {
	std::vector<uint8_t> p(3);
	p[0] = SC_CONTROL_MSG_TYPE_UHID_DESTROY;
	WriteBE16(&p[1], SC_HID_ID_MOUSE);

	SendControlMsg(p);
}