#pragma once
#include <string>
#include <vector>
#include <thread>
#include <mutex>
#include <map>
#include <atomic>
#include <functional>
#include <future>
#include <winsock2.h>
#include <ws2tcpip.h>

namespace ScrcpyVideoEngine
{
	// --- Callbacks to send data out of the core ---
	typedef std::function<void(const std::string&)> LogCallback;
	typedef std::function<void(uint32_t width, uint32_t height)> MetadataCallback;
	typedef std::function<void(const std::vector<uint8_t>& packet, int64_t pts)> VideoPacketCallback;

	// --- Callbacks to get authentication data from the owner ---
	typedef std::function<std::vector<uint8_t>(const std::vector<uint8_t>&)> SignCallback;
	typedef std::function<std::vector<uint8_t>()> PublicKeyCallback;

	class AdbCore {
	private:
		SOCKET m_socket;
		std::atomic<bool> m_running;
		std::thread m_recvThread;
		std::mutex m_stopMutex;

		uint32_t m_localIdCounter;
		std::mutex m_mapMutex;
		std::map<uint32_t, uint32_t> m_localToRemote;

		std::mutex m_pendingMutex;
		std::map<uint32_t, std::promise<bool>*> m_pendingOpens;
		std::map<uint32_t, std::promise<bool>*> m_pendingCloses;
		std::promise<bool>* m_connectPromise;

		uint32_t m_videoLocalId;
		uint32_t m_controlLocalId;
		uint32_t m_serverLocalId;
		std::string m_scid;
		std::atomic<bool> m_authAttempted;

		// --- Video stream processing state ---
		std::vector<uint8_t> m_videoBuffer;
		size_t m_videoReadPos;
		int m_videoStage;
		std::vector<uint8_t> m_pendingConfig; // Stores H.264 SPS/PPS

		std::vector<uint8_t> m_recvBuffer;

		// --- Callback function pointers ---
		LogCallback m_logger;
		SignCallback m_signCallback;
		PublicKeyCallback m_pubKeyCallback;
		MetadataCallback m_metaCallback;
		VideoPacketCallback m_videoCallback;

	public:
		AdbCore();
		~AdbCore();

		void SetLogger(LogCallback logger);
		void SetAuthCallbacks(SignCallback signCb, PublicKeyCallback keyCb);
		void SetDataCallbacks(MetadataCallback metaCb, VideoPacketCallback videoCb);

		bool Connect(const std::string& ip, int port);
		bool DeployServer(const std::vector<uint8_t>& jarData);
		void StartScrcpy(int bitRate, int maxSize, int maxFps);
		void Stop();
		void Disconnect();

	private:
		void Log(const std::string& msg);
		uint32_t OpenStream(const std::string& destination);
		bool ExecuteShellCommand(const std::string& command);
		bool SendPacket(uint32_t cmd, uint32_t arg0, uint32_t arg1, const void* data, size_t length);
		void ReceiveLoop();
		void HandlePacket(uint32_t cmd, uint32_t arg0, uint32_t arg1, uint32_t dlen, const uint8_t* payload);

		uint64_t ReadBE64(const uint8_t* data);
		uint32_t ReadBE32(const uint8_t* data);
		void CompactVideoBuffer();
	};
}