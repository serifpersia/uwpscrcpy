using System;
using System.Diagnostics;

namespace uwpscrcpy
{
    public static class H264Parser
    {
        public static void AnalyzePacket(byte[] data, int length, ulong pts, bool isConfig, bool isKey)
        {
            string typeTag = isConfig ? "CONFIG" : (isKey ? "KEY   " : "P     ");
            Debug.WriteLine($"\n[PACKET] {typeTag} | PTS: {pts} | Total Size: {length}");

            int offset = 0;
            while (offset < length)
            {
                int nalStart = FindNalUnitStart(data, offset, length);
                if (nalStart == -1) break;

                int nextNal = FindNalUnitStart(data, nalStart + 3, length);
                int nalSize = (nextNal == -1 ? length : nextNal) - nalStart;

                int headerOffset = nalStart;
                if (nalStart + 2 < length && data[nalStart + 2] == 1) headerOffset += 3;
                else headerOffset += 4;

                if (headerOffset >= length) break;

                byte nalHeader = data[headerOffset];
                int nal_ref_idc = (nalHeader & 0x60) >> 5;
                int nal_unit_type = (nalHeader & 0x1F);

                string nalName = GetNalName(nal_unit_type);
                string sliceInfo = "";

                if (nal_unit_type == 1 || nal_unit_type == 5)
                {
                    sliceInfo = ParseSliceHeader(data, headerOffset + 1, Math.Min(20, length - headerOffset));
                }

                Debug.WriteLine($"   -> [NAL] Offset: {nalStart,6} | Size: {nalSize,6} | Ref: {nal_ref_idc} | Type: {nal_unit_type} ({nalName}) {sliceInfo}");

                offset = (nextNal == -1) ? length : nextNal;
            }
        }

        private static string GetNalName(int type)
        {
            switch (type)
            {
                case 1: return "Non-IDR Slice";
                case 5: return "IDR Slice (Key)";
                case 6: return "SEI";
                case 7: return "SPS";
                case 8: return "PPS";
                case 9: return "Access Unit Delim";
                default: return "Unknown";
            }
        }

        private static int FindNalUnitStart(byte[] data, int start, int end)
        {
            for (int i = start; i < end - 3; i++)
            {
                if (data[i] == 0 && data[i + 1] == 0)
                {
                    if (data[i + 2] == 1) return i;
                    if (data[i + 2] == 0 && data[i + 3] == 1) return i;
                }
            }
            return -1;
        }

        private static string ParseSliceHeader(byte[] buffer, int offset, int length)
        {
            try
            {
                var br = new BitStreamReader(buffer, offset, length);
                int first_mb = br.ReadUE();
                int slice_type_val = br.ReadUE();
                return $"| Slice: {GetSliceType(slice_type_val)} | FirstMB: {first_mb}";
            }
            catch { return "| Parse Err"; }
        }

        private static string GetSliceType(int val)
        {
            if (val > 4) val -= 5;
            switch (val)
            {
                case 0: return "P (Predictive)";
                case 1: return "B (Bi-Dir)";
                case 2: return "I (Intra)";
                default: return $"Type_{val}";
            }
        }
    }

    public class BitStreamReader
    {
        private byte[] _data;
        private int _byteOffset;
        private int _bitOffset;
        private int _maxBytes;

        public BitStreamReader(byte[] data, int offset, int length)
        {
            _data = data;
            _byteOffset = offset;
            _maxBytes = offset + length;
            _bitOffset = 7;
        }

        public int ReadBit()
        {
            if (_byteOffset >= _maxBytes) throw new Exception("EOF");
            int val = (_data[_byteOffset] >> _bitOffset) & 1;
            _bitOffset--;
            if (_bitOffset < 0)
            {
                _bitOffset = 7;
                _byteOffset++;
            }
            return val;
        }

        public int ReadBits(int n)
        {
            int val = 0;
            for (int i = 0; i < n; i++) val = (val << 1) | ReadBit();
            return val;
        }

        public int ReadUE()
        {
            int leadingZeros = 0;
            while (ReadBit() == 0 && leadingZeros < 32) leadingZeros++;
            if (leadingZeros == 0) return 0;
            int suffix = ReadBits(leadingZeros);
            return (1 << leadingZeros) - 1 + suffix;
        }
    }
}