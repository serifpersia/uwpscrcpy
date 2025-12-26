#include "pch.h"
#include "ScrcpyController.h"
#include "Utils.h"

#define SC_CONTROL_MSG_TYPE_INJECT_TOUCH_EVENT 2
#define SC_CONTROL_MSG_TYPE_INJECT_SCROLL_EVENT 3
#define SC_CONTROL_MSG_TYPE_BACK_OR_SCREEN_ON 4
#define SC_CONTROL_MSG_TYPE_UHID_INPUT 13
#define SC_CONTROL_MSG_TYPE_UHID_DESTROY 14
#define SC_HID_ID_MOUSE 2

using namespace Platform;
using namespace Windows::UI::Core;
using namespace concurrency;

std::string GenerateScid() {
	static bool seeded = false; if (!seeded) { srand((unsigned int)time(NULL)); seeded = true; }
	const char hex_f[] = "01234567"; const char hex_r[] = "0123456789abcdef";
	std::string scid = ""; scid += hex_f[rand() % 8];
	for (int i = 0; i < 7; ++i) scid += hex_r[rand() % 16];
	return scid;
}

namespace ScrcpyVideoEngine {
	ScrcpyController::ScrcpyController() : m_dispatcher(nullptr), m_panel(nullptr) {
		m_client = std::make_shared<AdbClient>();
		m_engine = std::make_shared<VideoEngine>();
		m_client->SetVideoEngine(m_engine);

		m_client->SetLogCallback([this](const std::string& msg) { Log(msg); });
		m_client->SetSignCallback([this](const std::vector<uint8_t>& t) { return PerformSign(t); });
		m_client->SetKeyCallback([this]() { return GetKey(); });

		auto resCb = [this](uint32_t w, uint32_t h) {
			if (m_dispatcher) m_dispatcher->RunAsync(CoreDispatcherPriority::Normal, ref new DispatchedHandler([this, w, h]() { OnResolutionChanged(w, h); }));
		};
		m_client->SetResolutionCallback(resCb);
		m_engine->SetResolutionCallback(resCb);
	}

	ScrcpyController::~ScrcpyController() { Stop(); }

	void ScrcpyController::SetDispatcher(CoreDispatcher^ dispatcher) {
		m_dispatcher = dispatcher;
		m_engine->SetDispatcher(dispatcher);
	}

	void ScrcpyController::SetPanel(Object^ panel) { m_panel = panel; }

	bool ScrcpyController::Connect(String^ ip, int port) {
		std::wstring_convert<std::codecvt_utf8<wchar_t>> conv;
		return m_client->Connect(conv.to_bytes(ip->Data()), port);
	}

	void ScrcpyController::DeployServer(const Array<byte>^ jarData) {
		if (!jarData) return;
		std::vector<uint8_t> dataVec = { jarData->Data, jarData->Data + jarData->Length };
		m_client->ExecuteShellCommand("rm /data/local/tmp/scrcpy-server.jar");
		std::string b64 = Base64Encode(dataVec.data(), dataVec.size());
		size_t offset = 0, total = b64.size();
		while (offset < total) {
			size_t len = (std::min)((size_t)1024, total - offset);
			m_client->ExecuteShellCommand("echo -n \"" + b64.substr(offset, len) + "\" " + (offset == 0 ? ">" : ">>") + " /data/local/tmp/scrcpy.b64");
			offset += len;
		}
		m_client->ExecuteShellCommand("base64 -d /data/local/tmp/scrcpy.b64 > /data/local/tmp/scrcpy-server.jar");
		m_client->ExecuteShellCommand("rm /data/local/tmp/scrcpy.b64");
	}

	void ScrcpyController::StartScrcpy(int bitRate, int maxSize, int maxFps, bool video, bool uhid) {
		m_scid = GenerateScid();
		m_client->Configure(video, uhid);
		m_client->OpenStream("reverse:forward:localabstract:scrcpy_" + m_scid + ";tcp:27183");
		std::this_thread::sleep_for(std::chrono::milliseconds(200));
		std::string args = "log_level=info scid=" + m_scid + " tunnel_forward=false audio=false control=true cleanup=true video=" + (video ? "true" : "false") + " ";
		args += "video_bit_rate=" + std::to_string(bitRate) + " max_size=" + std::to_string(maxSize) + " max_fps=" + std::to_string(maxFps) + " send_device_meta=true send_codec_meta=true ";
		if (uhid) args += "mouse=uhid ";
		std::string cmd = "shell:CLASSPATH=/data/local/tmp/scrcpy-server.jar app_process / com.genymobile.scrcpy.Server 3.3.3 " + args;
		m_client->SetServerLocalId(m_client->OpenStream(cmd));
	}

	void ScrcpyController::Stop() {
		m_client->Disconnect();
		m_engine->Shutdown();
		m_client->SetVideoLocalIds(0, 0);
	}

	void ScrcpyController::InitializeVideo(uint32_t width, uint32_t height) {
		if (m_panel) m_engine->Initialize(width, height, m_panel);
	}

	void ScrcpyController::InjectTouch(int action, int pointerId, int x, int y, int width, int height, float pressure, int buttons) {
		std::vector<uint8_t> p(32);
		p[0] = SC_CONTROL_MSG_TYPE_INJECT_TOUCH_EVENT; p[1] = (uint8_t)action;
		WriteBE32(&p[6], (uint32_t)pointerId); WriteBE32(&p[10], (uint32_t)x); WriteBE32(&p[14], (uint32_t)y);
		WriteBE16(&p[18], (uint16_t)width); WriteBE16(&p[20], (uint16_t)height);
		WriteBE16(&p[22], (uint16_t)(pressure * 65535.0f)); WriteBE32(&p[24], (uint32_t)buttons);
		SendControlMsg(p);
	}

	void ScrcpyController::InjectScroll(int x, int y, int width, int height, int hScroll, int vScroll, int buttons) {
		std::vector<uint8_t> p(21);
		p[0] = SC_CONTROL_MSG_TYPE_INJECT_SCROLL_EVENT;
		WriteBE32(&p[1], (uint32_t)x); WriteBE32(&p[5], (uint32_t)y);
		WriteBE16(&p[9], (uint16_t)width); WriteBE16(&p[11], (uint16_t)height);
		WriteBE16(&p[13], (uint16_t)hScroll); WriteBE16(&p[15], (uint16_t)vScroll);
		WriteBE32(&p[17], (uint32_t)buttons);
		SendControlMsg(p);
	}

	void ScrcpyController::InjectBackOrScreenOn(int action) {
		std::vector<uint8_t> p(2); p[0] = SC_CONTROL_MSG_TYPE_BACK_OR_SCREEN_ON; p[1] = (uint8_t)action;
		SendControlMsg(p);
	}

	void ScrcpyController::EnableUhidMouse(bool enable) {
		if (!enable) {
			std::vector<uint8_t> p(3); p[0] = SC_CONTROL_MSG_TYPE_UHID_DESTROY; WriteBE16(&p[1], SC_HID_ID_MOUSE);
			SendControlMsg(p);
		}
	}

	void ScrcpyController::InjectUhidInput(int buttons, int dx, int dy, int vScroll, int hScroll) {
		auto clamp = [](int v) -> int8_t { return (v > 127) ? 127 : ((v < -127) ? -127 : (int8_t)v); };
		uint8_t report[5] = { (uint8_t)buttons, (uint8_t)clamp(dx), (uint8_t)clamp(dy), (uint8_t)clamp(vScroll), (uint8_t)clamp(hScroll) };
		std::vector<uint8_t> p(10);
		p[0] = SC_CONTROL_MSG_TYPE_UHID_INPUT; WriteBE16(&p[1], SC_HID_ID_MOUSE);
		WriteBE16(&p[3], 5); memcpy(&p[5], report, 5);
		SendControlMsg(p);
	}

	Windows::Foundation::IAsyncOperation<int>^ ScrcpyController::GetVolumeAsync() {
		return create_async([this]() -> int {
			try {
				std::string output = m_client->ExecuteShellAndRead("media volume --get");
				std::regex re("volume is (\\d+)"); std::smatch match;
				if (std::regex_search(output, match, re) && match.size() > 1) return std::stoi(match.str(1));
			}
			catch (...) {} return -1;
		});
	}

	void ScrcpyController::SetVolume(int volume) {
		std::string cmd = "media volume --stream 3 --set " + std::to_string(volume);
		std::thread([this, cmd]() { m_client->ExecuteShellCommand(cmd); }).detach();
	}

	void ScrcpyController::SendControlMsg(const std::vector<uint8_t>& msg) {
		uint32_t lid = m_client->GetControlLocalId();
		if (lid == 0) return;
		uint32_t rid = m_client->GetRemoteId(lid);
		if (rid != 0) m_client->SendPacket(A_WRTE, lid, rid, msg.data(), msg.size());
	}

	void ScrcpyController::Log(const std::string& msg) {
		if (!m_dispatcher) return;
		m_dispatcher->RunAsync(CoreDispatcherPriority::Low, ref new DispatchedHandler([this, msg]() {
			try { std::wstring_convert<std::codecvt_utf8<wchar_t>> conv; OnLog(ref new String(conv.from_bytes(msg).c_str())); }
			catch (...) {}
		}));
	}

	std::vector<uint8_t> ScrcpyController::PerformSign(const std::vector<uint8_t>& t) {
		if (!AuthSignCallback || !m_dispatcher) return {};
		task_completion_event<std::vector<uint8_t>> tce;
		m_dispatcher->RunAsync(CoreDispatcherPriority::Normal, ref new DispatchedHandler([this, t, tce]() {
			try {
				auto res = AuthSignCallback(ref new Array<byte>((byte*)t.data(), (unsigned int)t.size()));
				tce.set(res ? std::vector<uint8_t>(res->Data, res->Data + res->Length) : std::vector<uint8_t>{});
			}
			catch (...) { tce.set({}); }
		}));
		return create_task(tce).get();
	}

	std::vector<uint8_t> ScrcpyController::GetKey() {
		if (!AuthKeyCallback || !m_dispatcher) return {};
		task_completion_event<std::vector<uint8_t>> tce;
		m_dispatcher->RunAsync(CoreDispatcherPriority::Normal, ref new DispatchedHandler([this, tce]() {
			try {
				auto res = AuthKeyCallback();
				tce.set(res ? std::vector<uint8_t>(res->Data, res->Data + res->Length) : std::vector<uint8_t>{});
			}
			catch (...) { tce.set({}); }
		}));
		return create_task(tce).get();
	}
}