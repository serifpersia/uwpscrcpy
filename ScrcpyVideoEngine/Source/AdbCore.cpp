#include "pch.h"
#include "AdbCore.h"
#include <algorithm>
#include <ctime>

using namespace ScrcpyVideoEngine;

#define A_CNXN 0x4e584e43
#define A_OPEN 0x4e45504f
#define A_OKAY 0x59414b4f
#define A_WRTE 0x45545257
#define A_CLSE 0x45534c43
#define A_AUTH 0x48545541

#pragma pack(push, 1)
struct AdbPacketHeader {
	uint32_t command; uint32_t arg0; uint32_t arg1;
	uint32_t data_length; uint32_t data_crc32; uint32_t magic;
};
#pragma pack(pop)

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

bool RecvExact(SOCKET s, void* buf, size_t len) {
	char* p = (char*)buf; size_t total = 0;
	while (total < len) {
		int r = recv(s, p + total, (int)(len - total), 0);
		if (r <= 0) return false;
		total += r;
	}
	return true;
}

AdbCore::AdbCore() : m_socket(INVALID_SOCKET), m_running(false), m_localIdCounter(1),
m_videoLocalId(0), m_controlLocalId(0), m_serverLocalId(0), m_videoStage(0), m_videoReadPos(0), m_authAttempted(false), m_connectPromise(nullptr) {
	m_recvBuffer.resize(65536 + 24);
	m_videoBuffer.reserve(1024 * 512);
}

AdbCore::~AdbCore() {
	Stop();
}

void AdbCore::SetLogger(LogCallback logger) { m_logger = logger; }
void AdbCore::SetAuthCallbacks(SignCallback signCb, PublicKeyCallback keyCb) { m_signCallback = signCb; m_pubKeyCallback = keyCb; }
void AdbCore::SetDataCallbacks(MetadataCallback metaCb, VideoPacketCallback videoCb) { m_metaCallback = metaCb; m_videoCallback = videoCb; }

void AdbCore::Log(const std::string& msg) {
	std::string debugOut = "[AdbCore] " + msg + "\n";
	OutputDebugStringA(debugOut.c_str());
	if (m_logger) m_logger(msg);
}

void AdbCore::Stop() {
	std::lock_guard<std::mutex> stopLock(m_stopMutex);
	m_running = false;
	if (m_socket != INVALID_SOCKET) {
		shutdown(m_socket, SD_BOTH);
		closesocket(m_socket);
		m_socket = INVALID_SOCKET;
	}
	if (m_recvThread.joinable()) m_recvThread.join();
	{ std::lock_guard<std::mutex> lock(m_mapMutex); m_localToRemote.clear(); }
	{ std::lock_guard<std::mutex> lock(m_pendingMutex); m_pendingOpens.clear(); m_pendingCloses.clear(); }
	m_videoLocalId = 0; m_controlLocalId = 0; m_serverLocalId = 0; m_videoStage = 0; m_videoReadPos = 0;
	m_videoBuffer.clear(); m_authAttempted = false;
	WSACleanup();
}

void AdbCore::Disconnect() { Stop(); }

bool AdbCore::Connect(const std::string& ip, int port) {
	Stop();
	WSADATA wsaData; if (WSAStartup(MAKEWORD(2, 2), &wsaData) != 0) return false;
	m_socket = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
	if (m_socket == INVALID_SOCKET) return false;

	BOOL nodelay = TRUE;
	setsockopt(m_socket, IPPROTO_TCP, TCP_NODELAY, (const char*)&nodelay, sizeof(nodelay));
	int sndBuf = 1024 * 1024;
	setsockopt(m_socket, SOL_SOCKET, SO_RCVBUF, (const char*)&sndBuf, sizeof(sndBuf));

	sockaddr_in addr; addr.sin_family = AF_INET; addr.sin_addr.s_addr = inet_addr(ip.c_str()); addr.sin_port = htons(port);
	if (connect(m_socket, (sockaddr*)&addr, sizeof(addr)) == SOCKET_ERROR) { closesocket(m_socket); return false; }

	m_running = true;
	m_connectPromise = new std::promise<bool>();
	std::future<bool> fut = m_connectPromise->get_future();
	std::string host = "host::\0";
	SendPacket(A_CNXN, 0x01000001, 1024 * 1024, host.data(), (uint32_t)host.size());
	m_recvThread = std::thread(&AdbCore::ReceiveLoop, this);

	if (fut.wait_for(std::chrono::seconds(5)) == std::future_status::ready) return fut.get();
	Stop(); return false;
}

bool AdbCore::DeployServer(const std::vector<uint8_t>& jarData) {
	ExecuteShellCommand("rm /data/local/tmp/scrcpy-server.jar");
	std::string b64 = Base64Encode(jarData.data(), jarData.size());
	size_t offset = 0, total = b64.size();
	while (offset < total && m_running) {
		size_t len = (std::min)((size_t)1024, total - offset);
		ExecuteShellCommand("echo -n \"" + b64.substr(offset, len) + "\" " + (offset == 0 ? ">" : ">>") + " /data/local/tmp/scrcpy.b64");
		offset += len;
	}
	ExecuteShellCommand("base64 -d /data/local/tmp/scrcpy.b64 > /data/local/tmp/scrcpy-server.jar");
	ExecuteShellCommand("rm /data/local/tmp/scrcpy.b64");
	return true;
}

bool AdbCore::ExecuteShellCommand(const std::string& command) {
	if (!m_running) return false;
	uint32_t lid = ++m_localIdCounter;
	std::promise<bool> op; std::promise<bool> cl;
	{ std::lock_guard<std::mutex> lock(m_pendingMutex); m_pendingOpens[lid] = &op; m_pendingCloses[lid] = &cl; }
	std::string req = "shell:" + command + '\0';
	SendPacket(A_OPEN, lid, 0, req.data(), (uint32_t)req.size());
	if (op.get_future().wait_for(std::chrono::seconds(2)) != std::future_status::ready) return false;
	return cl.get_future().wait_for(std::chrono::seconds(5)) == std::future_status::ready;
}

uint32_t AdbCore::OpenStream(const std::string& dest) {
	if (!m_running) return 0;
	uint32_t lid = ++m_localIdCounter;
	std::string req = dest + '\0';
	SendPacket(A_OPEN, lid, 0, req.data(), (uint32_t)req.size());
	return lid;
}

void AdbCore::StartScrcpy(int bitRate, int maxSize, int maxFps) {
	m_scid = GenerateScid();
	m_videoStage = 1;
	m_videoReadPos = 0;
	m_pendingConfig.clear();
	m_videoBuffer.clear();

	OpenStream("reverse:forward:localabstract:scrcpy_" + m_scid + ";tcp:27183");
	std::this_thread::sleep_for(std::chrono::milliseconds(200));

	std::string serverArgs = "log_level=info scid=" + m_scid + " tunnel_forward=false video=true audio=false control=true cleanup=true ";
	serverArgs += "video_bit_rate=" + std::to_string(bitRate) + " ";
	serverArgs += "max_size=" + std::to_string(maxSize) + " ";
	serverArgs += "max_fps=" + std::to_string(maxFps) + " ";
	serverArgs += "send_device_meta=true send_codec_meta=true ";

	std::string cmd = "shell:CLASSPATH=/data/local/tmp/scrcpy-server.jar app_process / com.genymobile.scrcpy.Server 3.3.3 " + serverArgs;
	m_serverLocalId = OpenStream(cmd);
}

bool AdbCore::SendPacket(uint32_t cmd, uint32_t a0, uint32_t a1, const void* data, size_t len) {
	if (m_socket == INVALID_SOCKET || !m_running) return false;
	AdbPacketHeader h = { cmd, a0, a1, (uint32_t)len, 0, cmd ^ 0xFFFFFFFF };
	std::vector<uint8_t> pkt(24 + len);
	memcpy(pkt.data(), &h, 24);
	if (len > 0) {
		memcpy(pkt.data() + 24, data, len);
	}
	send(m_socket, (char*)pkt.data(), (int)pkt.size(), 0);
	return true;
}

void AdbCore::ReceiveLoop() {
	while (m_running) {
		AdbPacketHeader h;
		if (!RecvExact(m_socket, &h, 24)) break;
		if (h.data_length > 0) {
			if (m_recvBuffer.size() < h.data_length) {
				m_recvBuffer.resize(h.data_length);
			}
			if (!RecvExact(m_socket, m_recvBuffer.data(), h.data_length)) break;
		}
		HandlePacket(h.command, h.arg0, h.arg1, h.data_length, m_recvBuffer.data());
	}
}

void AdbCore::CompactVideoBuffer() {
	if (m_videoReadPos > 0 && m_videoBuffer.size() > m_videoReadPos) {
		m_videoBuffer.erase(m_videoBuffer.begin(), m_videoBuffer.begin() + m_videoReadPos);
	}
	else if (m_videoBuffer.size() == m_videoReadPos) {
		m_videoBuffer.clear();
	}
	m_videoReadPos = 0;
}

uint64_t AdbCore::ReadBE64(const uint8_t* d) { return ((uint64_t)d[0] << 56) | ((uint64_t)d[1] << 48) | ((uint64_t)d[2] << 40) | ((uint64_t)d[3] << 32) | ((uint64_t)d[4] << 24) | ((uint64_t)d[5] << 16) | ((uint64_t)d[6] << 8) | (uint64_t)d[7]; }
uint32_t AdbCore::ReadBE32(const uint8_t* d) { return ((uint32_t)d[0] << 24) | ((uint32_t)d[1] << 16) | ((uint32_t)d[2] << 8) | (uint32_t)d[3]; }

void AdbCore::HandlePacket(uint32_t cmd, uint32_t a0, uint32_t a1, uint32_t dlen, const uint8_t* payload) {
	switch (cmd) {
	case A_CNXN:
		if (m_connectPromise) { m_connectPromise->set_value(true); m_connectPromise = nullptr; }
		break;
	case A_AUTH:
		if (a0 == 1) { // AUTH_TOKEN
			if (!m_authAttempted && m_signCallback) {
				auto sig = m_signCallback({ payload, payload + dlen });
				if (!sig.empty()) {
					SendPacket(A_AUTH, 2, 0, sig.data(), (uint32_t)sig.size());
					m_authAttempted = true;
					break;
				}
			}
			if (m_pubKeyCallback) {
				auto key = m_pubKeyCallback();
				SendPacket(A_AUTH, 3, 0, key.data(), (uint32_t)key.size());
			}
		}
		break;
	case A_OPEN: {
		uint32_t rid = a0, lid = ++m_localIdCounter;
		{ std::lock_guard<std::mutex> lock(m_mapMutex); m_localToRemote[lid] = rid; }
		if (m_videoLocalId == 0) m_videoLocalId = lid; else m_controlLocalId = lid;
		SendPacket(A_OKAY, rid, lid, nullptr, 0);
		break;
	}
	case A_OKAY: {
		{ std::lock_guard<std::mutex> lock(m_mapMutex); m_localToRemote[a1] = a0; }
		std::lock_guard<std::mutex> lock(m_pendingMutex);
		if (m_pendingOpens.count(a1)) { m_pendingOpens[a1]->set_value(true); m_pendingOpens.erase(a1); }
		break;
	}
	case A_WRTE: {
		SendPacket(A_OKAY, a0, a1, nullptr, 0);
		if (a1 == m_serverLocalId) {
			Log("[SERVER] " + std::string((char*)payload, dlen));
		}
		else if (a1 == m_videoLocalId) {
			m_videoBuffer.insert(m_videoBuffer.end(), payload, payload + dlen);
			bool work = true;
			while (work) {
				work = false;
				size_t available = m_videoBuffer.size() - m_videoReadPos;
				uint8_t* ptr = m_videoBuffer.data() + m_videoReadPos;

				if (m_videoStage == 1 && available >= 64) { // Device Name
					m_videoReadPos += 64;
					m_videoStage = 2;
					work = true;
				}
				else if (m_videoStage == 2 && available >= 12) { // Codec Meta + WxH
					uint32_t w = ReadBE32(ptr + 4);
					uint32_t h = ReadBE32(ptr + 8);
					if (m_metaCallback) m_metaCallback(w, h);
					m_videoReadPos += 12;
					m_videoStage = 3;
					work = true;
				}
				else if (m_videoStage == 3 && available >= 12) { // Video Packet
					uint64_t ptsData = ReadBE64(ptr);
					uint32_t pSize = ReadBE32(ptr + 8);

					if (available >= (12 + pSize)) {
						bool isConfig = (ptsData & 0x8000000000000000) != 0;
						int64_t ptsUs = ptsData & 0x3FFFFFFFFFFFFFFF;
						const uint8_t* packetStart = ptr + 12;

						if (isConfig) {
							m_pendingConfig.assign(packetStart, packetStart + pSize);
						}
						else if (m_videoCallback) {
							std::vector<uint8_t> bufferToSend;
							if (!m_pendingConfig.empty()) {
								bufferToSend.insert(bufferToSend.end(), m_pendingConfig.begin(), m_pendingConfig.end());
								m_pendingConfig.clear();
							}
							bufferToSend.insert(bufferToSend.end(), packetStart, packetStart + pSize);
							m_videoCallback(bufferToSend, ptsUs);
						}
						m_videoReadPos += (12 + pSize);
						work = true;
					}
				}
			}
			if (work) CompactVideoBuffer();
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