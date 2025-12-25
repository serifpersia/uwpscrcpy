#pragma once
#include "AdbCore.h"
#include "VideoEngine.h" // <-- IMPORTANT: Includes the correct delegate definitions

// Forward declare CoreDispatcher
namespace Windows { namespace UI { namespace Core { ref class CoreDispatcher; } } }

namespace ScrcpyVideoEngine
{
	// LogHandler is unique to the bridge
	public delegate void LogHandler(Platform::String^ message);

	// Auth delegates are unique to the bridge
	public delegate Platform::Array<byte>^ AuthSignHandler(const Platform::Array<byte>^ token);
	public delegate Platform::Array<byte>^ AuthKeyHandler();

	public ref class AdbBridge sealed
	{
	private:
		AdbCore* m_adbCore;
		VideoEngine^ m_videoEngine;
		Windows::UI::Core::CoreDispatcher^ m_dispatcher;

	public:
		AdbBridge();
		virtual ~AdbBridge();

		// --- EVENTS ---
		event LogHandler^ OnLog;
		// This now uses the delegate defined in VideoEngine.h, fixing the error
		event ResolutionChangedHandler^ OnResolutionChanged;

		// --- PROPERTIES ---
		property AuthSignHandler^ AuthSignCallback;
		property AuthKeyHandler^ AuthKeyCallback;

		// --- METHODS ---
		void Initialize(VideoEngine^ videoEngine, Windows::UI::Core::CoreDispatcher^ dispatcher);
		bool Connect(Platform::String^ ip, int port);
		void DeployServer(const Platform::Array<byte>^ jarData);
		void StartScrcpy(int bitRate, int maxSize, int maxFps);
		void Stop();

	internal:
		void OnLogInternal(const std::string& msg);
		void OnMetadataInternal(uint32_t width, uint32_t height);
		void OnVideoPacketInternal(const std::vector<uint8_t>& packet, int64_t pts);

		std::vector<uint8_t> PerformSign(const std::vector<uint8_t>& token);
		std::vector<uint8_t> GetKey();
	};
}