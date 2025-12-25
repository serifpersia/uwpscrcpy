#include "pch.h"
#include "AdbBridge.h"
#include <codecvt>
#include <ppltasks.h>
#include <concurrent_vector.h>
#include <windows.storage.streams.h>
#include <windows.security.cryptography.h>
#include <windows.ui.core.h>

using namespace ScrcpyVideoEngine;
using namespace Platform;
using namespace Windows::Storage::Streams;
using namespace Windows::Security::Cryptography;
using namespace Windows::UI::Core;
using namespace concurrency;

AdbBridge::AdbBridge() {
	m_adbCore = new AdbCore();
	m_videoEngine = nullptr;
	m_dispatcher = nullptr;

	m_adbCore->SetLogger([this](const std::string& msg) { this->OnLogInternal(msg); });
	m_adbCore->SetAuthCallbacks(
		[this](const std::vector<uint8_t>& t) { return this->PerformSign(t); },
		[this]() { return this->GetKey(); }
	);
}

AdbBridge::~AdbBridge() {
	if (m_adbCore) {
		delete m_adbCore;
		m_adbCore = nullptr;
	}
}

void AdbBridge::Initialize(VideoEngine^ videoEngine, CoreDispatcher^ dispatcher) {
	m_videoEngine = videoEngine;
	m_dispatcher = dispatcher; // Save the dispatcher

	m_adbCore->SetDataCallbacks(
		[this](uint32_t w, uint32_t h) { this->OnMetadataInternal(w, h); },
		[this](const std::vector<uint8_t>& p, int64_t pts) { this->OnVideoPacketInternal(p, pts); }
	);
}

bool AdbBridge::Connect(String^ ip, int port) {
	std::wstring_convert<std::codecvt_utf8<wchar_t>> conv;
	return m_adbCore->Connect(conv.to_bytes(ip->Data()), port);
}

// THIS IS THE CORE FIX: Marshalling the auth callbacks
std::vector<uint8_t> AdbBridge::PerformSign(const std::vector<uint8_t>& t) {
	if (!AuthSignCallback || m_dispatcher == nullptr) return {};

	// Use a task_completion_event to wait for the result from the UI thread
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

	// Block the background thread until the UI thread completes the task
	return create_task(tce).get();
}

std::vector<uint8_t> AdbBridge::GetKey() {
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


// --- Other methods are mostly the same ---


void AdbBridge::DeployServer(const Platform::Array<byte>^ jarData) {
	if (jarData != nullptr) {
		m_adbCore->DeployServer({ jarData->Data, jarData->Data + jarData->Length });
	}
}

void AdbBridge::StartScrcpy(int bitRate, int maxSize, int maxFps) {
	m_adbCore->StartScrcpy(bitRate, maxSize, maxFps);
}

void AdbBridge::Stop() {
	m_adbCore->Stop();
}

void AdbBridge::OnLogInternal(const std::string& msg) {
	if (m_dispatcher == nullptr) return;
	m_dispatcher->RunAsync(CoreDispatcherPriority::Low, ref new DispatchedHandler([this, msg]() {
		try {
			std::wstring_convert<std::codecvt_utf8<wchar_t>> conv;
			OnLog(ref new String(conv.from_bytes(msg).c_str()));
		}
		catch (...) {}
	}));
}

void AdbBridge::OnMetadataInternal(uint32_t width, uint32_t height) {
	if (m_dispatcher == nullptr) return;
	m_dispatcher->RunAsync(CoreDispatcherPriority::Normal, ref new DispatchedHandler([this, width, height]() {
		OnResolutionChanged(width, height);
	}));
}

void AdbBridge::OnVideoPacketInternal(const std::vector<uint8_t>& packet, int64_t pts) {
	if (m_videoEngine != nullptr) {
		auto platArray = ref new Array<byte>((byte*)packet.data(), (unsigned int)packet.size());
		IBuffer^ iBuf = CryptographicBuffer::CreateFromByteArray(platArray);
		m_videoEngine->PushFrame(iBuf, pts);
	}
}