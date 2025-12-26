#pragma once
#include "pch.h"

namespace ScrcpyVideoEngine {
	inline uint64_t ReadBE64(const uint8_t* d) { return ((uint64_t)d[0] << 56) | ((uint64_t)d[1] << 48) | ((uint64_t)d[2] << 40) | ((uint64_t)d[3] << 32) | ((uint64_t)d[4] << 24) | ((uint64_t)d[5] << 16) | ((uint64_t)d[6] << 8) | (uint64_t)d[7]; }
	inline uint32_t ReadBE32(const uint8_t* d) { return ((uint32_t)d[0] << 24) | ((uint32_t)d[1] << 16) | ((uint32_t)d[2] << 8) | (uint32_t)d[3]; }
	inline void WriteBE32(uint8_t* b, uint32_t val) { b[0] = (uint8_t)(val >> 24); b[1] = (uint8_t)(val >> 16); b[2] = (uint8_t)(val >> 8); b[3] = (uint8_t)val; }
	inline void WriteBE16(uint8_t* b, uint16_t val) { b[0] = (uint8_t)(val >> 8); b[1] = (uint8_t)val; }

	inline std::string Base64Encode(const uint8_t* buf, size_t bufLen) {
		static const std::string base64_chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
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
}