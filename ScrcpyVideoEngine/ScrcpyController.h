#pragma once
#include "pch.h"
#include "AdbClient.h"
#include "VideoEngine.h"

namespace ScrcpyVideoEngine {
	public delegate void LogHandler(Platform::String^ message);
	public delegate void ResolutionChangedHandler(uint32_t newWidth, uint32_t newHeight);
	public delegate Platform::Array<byte>^ AuthSignHandler(const Platform::Array<byte>^ token);
	public delegate Platform::Array<byte>^ AuthKeyHandler();

	[Windows::Foundation::Metadata::WebHostHidden]
	public ref class ScrcpyController sealed {
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
		void InitializeVideo(uint32_t width, uint32_t height);

		void InjectTouch(int action, int pointerId, int x, int y, int width, int height, float pressure, int buttons);
		void InjectScroll(int x, int y, int width, int height, int hScroll, int vScroll, int buttons);
		void InjectBackOrScreenOn(int action);
		void EnableUhidMouse(bool enable);
		void InjectUhidInput(int buttons, int dx, int dy, int vScroll, int hScroll);

		Windows::Foundation::IAsyncOperation<int>^ GetVolumeAsync();
		void SetVolume(int volume);

	private:
		void Log(const std::string& msg);
		std::vector<uint8_t> PerformSign(const std::vector<uint8_t>& token);
		std::vector<uint8_t> GetKey();
		void SendControlMsg(const std::vector<uint8_t>& msg);

		std::shared_ptr<AdbClient> m_client;
		std::shared_ptr<VideoEngine> m_engine;
		Windows::UI::Core::CoreDispatcher^ m_dispatcher;
		Platform::Object^ m_panel;
		std::string m_scid;
	};
}