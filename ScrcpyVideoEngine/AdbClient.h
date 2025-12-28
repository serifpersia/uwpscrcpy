#pragma once
#include "pch.h"
#include "VideoEngine.h"

#define A_CNXN 0x4e584e43
#define A_OPEN 0x4e45504f
#define A_OKAY 0x59414b4f
#define A_WRTE 0x45545257
#define A_CLSE 0x45534c43
#define A_AUTH 0x48545541

namespace ScrcpyVideoEngine {
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

	class AdbClient {
	public:
		AdbClient();
		~AdbClient();

		bool Connect(const std::string& ip, int port);
		void Disconnect();
		bool SendPacket(uint32_t cmd, uint32_t arg0, uint32_t arg1, const void* data, size_t length);
		uint32_t OpenStream(const std::string& destination);
		bool ExecuteShellCommand(const std::string& command);
		std::string ExecuteShellAndRead(const std::string& cmd);

		void SetSignCallback(std::function<std::vector<uint8_t>(const std::vector<uint8_t>&)> cb) { m_signCallback = cb; }
		void SetKeyCallback(std::function<std::vector<uint8_t>()> cb) { m_keyCallback = cb; }
		void SetLogCallback(std::function<void(const std::string&)> cb) { m_logCallback = cb; }
		void SetResolutionCallback(std::function<void(uint32_t, uint32_t)> cb) { m_resCallback = cb; }

		void SetVideoEngine(std::shared_ptr<VideoEngine> engine) { m_videoEngine = engine; }
		void Configure(bool video, bool uhid) { m_enableVideo = video; m_enableUhid = uhid; }
		void SetVideoLocalIds(uint32_t vid, uint32_t ctrl) { m_videoLocalId = vid; m_controlLocalId = ctrl; }
		void SetServerLocalId(uint32_t id) { m_serverLocalId = id; }

		uint32_t GetNextLocalId() { return ++m_localIdCounter; }
		uint32_t GetControlLocalId() { return m_controlLocalId; }
		uint32_t GetRemoteId(uint32_t localId);

	private:
		void ReceiveLoop();
		void HandlePacket(uint32_t cmd, uint32_t arg0, uint32_t arg1, uint32_t dlen, const uint8_t* payload);
		bool RecvExact(void* buf, size_t len);

		SOCKET m_socket;
		std::atomic<bool> m_running;
		std::thread m_recvThread;
		std::promise<bool>* m_connectPromise;
		uint32_t m_localIdCounter;
		bool m_authAttempted;

		std::mutex m_mapMutex;
		std::map<uint32_t, uint32_t> m_localToRemote;
		std::mutex m_pendingMutex;
		std::map<uint32_t, std::promise<bool>*> m_pendingOpens;
		std::map<uint32_t, std::promise<bool>*> m_pendingCloses;
		std::mutex m_shellMutex;
		std::map<uint32_t, std::shared_ptr<std::string>> m_shellBuffers;

		uint32_t m_videoLocalId;
		uint32_t m_controlLocalId;
		uint32_t m_serverLocalId;
		bool m_enableVideo;
		bool m_enableUhid;

		std::vector<uint8_t> m_recvBuffer;

		std::unique_ptr<uint8_t[]> m_videoBufferBytes;
		size_t m_videoCapacity;
		size_t m_videoWritePos;
		size_t m_videoReadPos;
		int m_videoStage;
		std::vector<uint8_t> m_pendingConfig;

		std::function<std::vector<uint8_t>(const std::vector<uint8_t>&)> m_signCallback;
		std::function<std::vector<uint8_t>()> m_keyCallback;
		std::function<void(const std::string&)> m_logCallback;
		std::function<void(uint32_t, uint32_t)> m_resCallback;
		std::shared_ptr<VideoEngine> m_videoEngine;
	};
}