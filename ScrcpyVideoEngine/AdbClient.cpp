#include "pch.h"
#include "AdbClient.h"
#include "Utils.h"

#define SC_CONTROL_MSG_TYPE_UHID_CREATE 12
#define SC_HID_ID_MOUSE 2

static const uint8_t SC_HID_MOUSE_REPORT_DESC[] = {
	0x05, 0x01, 0x09, 0x02, 0xA1, 0x01, 0x09, 0x01, 0xA1, 0x00, 0x05, 0x09, 0x19, 0x01, 0x29, 0x05, 0x15, 0x00, 0x25, 0x01, 0x95, 0x05,
	0x75, 0x01, 0x81, 0x02, 0x95, 0x01, 0x75, 0x03, 0x81, 0x01, 0x05, 0x01, 0x09, 0x30, 0x09, 0x31, 0x09, 0x38, 0x15, 0x81, 0x25, 0x7F,
	0x75, 0x08, 0x95, 0x03, 0x81, 0x06, 0x05, 0x0C, 0x0A, 0x38, 0x02, 0x15, 0x81, 0x25, 0x7F, 0x75, 0x08, 0x95, 0x01, 0x81, 0x06, 0xC0, 0xC0,
};

namespace ScrcpyVideoEngine {
	AdbClient::AdbClient() : m_socket(INVALID_SOCKET), m_running(false), m_connectPromise(nullptr), m_localIdCounter(1), m_authAttempted(false), m_videoLocalId(0), m_controlLocalId(0), m_serverLocalId(0), m_enableVideo(true), m_enableUhid(false), m_videoStage(1), m_videoReadPos(0) {
		m_recvBuffer.resize(65536 + 24);
		m_videoBuffer.reserve(1024 * 512);
	}

	AdbClient::~AdbClient() { Disconnect(); }

	bool AdbClient::Connect(const std::string& ip, int port) {
		Disconnect();
		m_authAttempted = false;
		WSADATA wsaData; if (WSAStartup(MAKEWORD(2, 2), &wsaData) != 0) return false;
		m_socket = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
		if (m_socket == INVALID_SOCKET) return false;
		int rcvBufSize = 512 * 1024;
		setsockopt(m_socket, SOL_SOCKET, SO_RCVBUF, (char*)&rcvBufSize, sizeof(rcvBufSize));
		sockaddr_in addr; addr.sin_family = AF_INET; addr.sin_addr.s_addr = inet_addr(ip.c_str()); addr.sin_port = htons(port);
		if (connect(m_socket, (sockaddr*)&addr, sizeof(addr)) == SOCKET_ERROR) { closesocket(m_socket); m_socket = INVALID_SOCKET; WSACleanup(); return false; }
		m_running = true;
		m_connectPromise = new std::promise<bool>();
		auto fut = m_connectPromise->get_future();
		m_recvThread = std::thread(&AdbClient::ReceiveLoop, this);
		std::string host = "host::\0";
		SendPacket(A_CNXN, 0x01000001, 1024 * 1024, host.data(), (uint32_t)host.size());
		if (fut.wait_for(std::chrono::seconds(5)) == std::future_status::ready) return fut.get();
		Disconnect(); return false;
	}

	void AdbClient::Disconnect() {
		m_running = false;
		if (m_socket != INVALID_SOCKET) { shutdown(m_socket, SD_BOTH); closesocket(m_socket); m_socket = INVALID_SOCKET; }
		if (m_recvThread.joinable()) m_recvThread.join();
		m_localToRemote.clear(); m_pendingOpens.clear(); m_pendingCloses.clear(); m_shellBuffers.clear();
		m_videoReadPos = 0; m_videoStage = 1; m_videoBuffer.clear(); m_pendingConfig.clear();
		WSACleanup();
	}

	bool AdbClient::RecvExact(void* buf, size_t len) {
		char* p = (char*)buf; size_t total = 0;
		while (total < len) {
			int r = recv(m_socket, p + total, (int)(len - total), 0);
			if (r <= 0) return false;
			total += r;
		}
		return true;
	}

	bool AdbClient::SendPacket(uint32_t cmd, uint32_t a0, uint32_t a1, const void* data, size_t len) {
		if (m_socket == INVALID_SOCKET || !m_running) return false;
		AdbPacketHeader h = { cmd, a0, a1, (uint32_t)len, 0, cmd ^ 0xFFFFFFFF };
		std::vector<uint8_t> pkt(sizeof(AdbPacketHeader) + len);
		memcpy(pkt.data(), &h, sizeof(AdbPacketHeader));
		if (len > 0) memcpy(pkt.data() + sizeof(AdbPacketHeader), data, len);
		send(m_socket, (char*)pkt.data(), (int)pkt.size(), 0);
		return true;
	}

	void AdbClient::ReceiveLoop() {
		try {
			while (m_running) {
				AdbPacketHeader h;
				if (!RecvExact(&h, sizeof(AdbPacketHeader))) break;
				if (h.data_length > 0) {
					if (m_recvBuffer.size() < h.data_length) m_recvBuffer.resize(h.data_length);
					if (!RecvExact(m_recvBuffer.data(), h.data_length)) break;
				}
				HandlePacket(h.command, h.arg0, h.arg1, h.data_length, m_recvBuffer.data());
			}
		}
		catch (...) { if (m_logCallback) m_logCallback("Exception in ReceiveLoop."); }
		m_running = false;
	}

	void AdbClient::HandlePacket(uint32_t cmd, uint32_t a0, uint32_t a1, uint32_t dlen, const uint8_t* payload) {
		switch (cmd) {
		case A_CNXN:
			if (m_connectPromise) { m_connectPromise->set_value(true); delete m_connectPromise; m_connectPromise = nullptr; }
			break;
		case A_AUTH:
			if (a0 == 1) {
				if (!m_authAttempted) {
					m_authAttempted = true;
					if (m_signCallback) {
						auto sig = m_signCallback({ payload, payload + dlen });
						if (!sig.empty()) { SendPacket(A_AUTH, 2, 0, sig.data(), (uint32_t)sig.size()); break; }
					}
				}
				if (m_keyCallback) { auto key = m_keyCallback(); SendPacket(A_AUTH, 3, 0, key.data(), (uint32_t)key.size()); }
			}
			break;
		case A_OPEN: {
			uint32_t rid = a0, lid = ++m_localIdCounter;
			{ std::lock_guard<std::mutex> lock(m_mapMutex); m_localToRemote[lid] = rid; }
			if (m_enableVideo) {
				if (m_videoLocalId == 0) m_videoLocalId = lid; else m_controlLocalId = lid;
			}
			else {
				m_controlLocalId = lid; m_videoLocalId = 0;
			}
			SendPacket(A_OKAY, rid, lid, nullptr, 0);
			if (m_enableUhid && lid == m_controlLocalId) {
				std::thread([this]() {
					std::this_thread::sleep_for(std::chrono::milliseconds(200));
					if (!m_running) return;
					size_t descLen = sizeof(SC_HID_MOUSE_REPORT_DESC);
					std::vector<uint8_t> p(10 + descLen);
					int off = 0;
					p[off++] = SC_CONTROL_MSG_TYPE_UHID_CREATE;
					WriteBE16(&p[off], SC_HID_ID_MOUSE); off += 2;
					WriteBE16(&p[off], 0); off += 2; WriteBE16(&p[off], 0); off += 2; p[off++] = 0;
					WriteBE16(&p[off], (uint16_t)descLen); off += 2;
					memcpy(&p[off], SC_HID_MOUSE_REPORT_DESC, descLen);
					uint32_t rid = GetRemoteId(m_controlLocalId);
					if (rid != 0) SendPacket(A_WRTE, m_controlLocalId, rid, p.data(), p.size());
					if (m_logCallback) m_logCallback("UHID Mouse Creation Packet Sent.");
				}).detach();
			}
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
			if (m_enableVideo && a1 == m_videoLocalId) {
				m_videoBuffer.insert(m_videoBuffer.end(), payload, payload + dlen);
				bool work = true;
				while (work) {
					work = false;
					size_t available = m_videoBuffer.size() - m_videoReadPos;
					uint8_t* ptr = m_videoBuffer.data() + m_videoReadPos;
					if (m_videoStage == 1 && available >= 64) { m_videoReadPos += 64; m_videoStage = 2; work = true; }
					else if (m_videoStage == 2 && available >= 12) {
						uint32_t w = ReadBE32(ptr + 4), h = ReadBE32(ptr + 8);
						if (m_resCallback) m_resCallback(w, h);
						m_videoReadPos += 12; m_videoStage = 3; work = true;
					}
					else if (m_videoStage == 3 && available >= 12) {
						uint64_t ptsData = ReadBE64(ptr); uint32_t pSize = ReadBE32(ptr + 8);
						if (available >= (12 + pSize)) {
							bool isConfig = (ptsData & 0x8000000000000000) != 0;
							int64_t ptsUs = ptsData & 0x3FFFFFFFFFFFFFFF;
							const uint8_t* packetStart = ptr + 12;
							if (isConfig) m_pendingConfig.assign(packetStart, packetStart + pSize);
							else if (m_videoEngine) {
								DWORD configSize = (DWORD)m_pendingConfig.size();
								Microsoft::WRL::ComPtr<IMFMediaBuffer> mediaBuffer;
								if (SUCCEEDED(MFCreateMemoryBuffer(pSize + configSize, &mediaBuffer))) {
									BYTE* dest = nullptr; mediaBuffer->Lock(&dest, nullptr, nullptr);
									if (configSize > 0) { memcpy(dest, m_pendingConfig.data(), configSize); dest += configSize; m_pendingConfig.clear(); }
									memcpy(dest, packetStart, pSize);
									mediaBuffer->Unlock(); mediaBuffer->SetCurrentLength(pSize + configSize);
									m_videoEngine->PushFrame(mediaBuffer, ptsUs);
								}
							}
							m_videoReadPos += (12 + pSize); work = true;
						}
					}
				}
				CompactVideoBuffer();
			}
			else if (a1 != m_controlLocalId) {
				std::lock_guard<std::mutex> lock(m_shellMutex);
				if (m_shellBuffers.count(a1)) m_shellBuffers[a1]->append((char*)payload, dlen);
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

	uint32_t AdbClient::GetRemoteId(uint32_t localId) {
		std::lock_guard<std::mutex> lock(m_mapMutex);
		auto it = m_localToRemote.find(localId);
		return (it != m_localToRemote.end()) ? it->second : 0;
	}

	uint32_t AdbClient::OpenStream(const std::string& destination) {
		if (!m_running) return 0;
		uint32_t lid = ++m_localIdCounter;
		std::string req = destination + '\0';
		SendPacket(A_OPEN, lid, 0, req.data(), (uint32_t)req.size());
		return lid;
	}

	bool AdbClient::ExecuteShellCommand(const std::string& command) {
		if (!m_running) return false;
		uint32_t lid = ++m_localIdCounter;
		auto op = new std::promise<bool>(); auto cl = new std::promise<bool>();
		{ std::lock_guard<std::mutex> lock(m_pendingMutex); m_pendingOpens[lid] = op; m_pendingCloses[lid] = cl; }
		std::string req = "shell:" + command + '\0';
		SendPacket(A_OPEN, lid, 0, req.data(), (uint32_t)req.size());
		bool success = op->get_future().wait_for(std::chrono::seconds(5)) == std::future_status::ready;
		if (success) success = cl->get_future().wait_for(std::chrono::seconds(10)) == std::future_status::ready;
		delete op; delete cl;
		return success;
	}

	std::string AdbClient::ExecuteShellAndRead(const std::string& cmd) {
		if (!m_running) return "";
		uint32_t lid = ++m_localIdCounter;
		auto buffer = std::make_shared<std::string>();
		auto cl = new std::promise<bool>();
		{
			std::lock_guard<std::mutex> lock1(m_pendingMutex); m_pendingCloses[lid] = cl;
			std::lock_guard<std::mutex> lock2(m_shellMutex); m_shellBuffers[lid] = buffer;
		}
		std::string req = "shell:" + cmd + '\0';
		SendPacket(A_OPEN, lid, 0, req.data(), (uint32_t)req.size());
		bool success = cl->get_future().wait_for(std::chrono::seconds(2)) == std::future_status::ready;
		{
			std::lock_guard<std::mutex> lock1(m_pendingMutex); if (m_pendingCloses.count(lid)) m_pendingCloses.erase(lid);
			std::lock_guard<std::mutex> lock2(m_shellMutex); if (m_shellBuffers.count(lid)) m_shellBuffers.erase(lid);
		}
		delete cl;
		return success ? *buffer : "";
	}

	void AdbClient::CompactVideoBuffer() {
		const size_t COMPACT_THRESHOLD = 256 * 1024;
		if (m_videoReadPos > COMPACT_THRESHOLD) {
			if (m_videoBuffer.size() > m_videoReadPos) m_videoBuffer.erase(m_videoBuffer.begin(), m_videoBuffer.begin() + m_videoReadPos);
			else m_videoBuffer.clear();
			m_videoReadPos = 0;
		}
	}
}