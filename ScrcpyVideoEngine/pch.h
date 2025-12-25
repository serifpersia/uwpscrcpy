#pragma once
#define _WINSOCK_DEPRECATED_NO_WARNINGS
#include <collection.h>
#include <ppltasks.h>
#include <windows.h>
#include <string>
#include <vector>
#include <mutex>
#include <queue>
#include <thread>
#include <atomic>
#include <map>
#include <functional>
#include <future>
#include <winsock2.h>
#include <ws2tcpip.h>
#include <d3d11.h>
#include <d3d11_1.h>
#include <dxgi1_2.h>
#include <mfapi.h>
#include <mferror.h>
#include <mfidl.h>
#include <mftransform.h>

#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")
#pragma comment(lib, "mf.lib")
#pragma comment(lib, "mfplat.lib")
#pragma comment(lib, "mfuuid.lib")
#pragma comment(lib, "ws2_32.lib")