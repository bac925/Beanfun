using System;
using System.IO;

namespace Beanfun.GameMaintenance
{
    /// <summary>
    /// Minimal PKG1 WZ version reader, ported from cmsdl's miniwzlib approach.
    /// Reads only the first 8 MiB and validates candidate version hashes against
    /// root-directory offsets; it does not parse game data.
    /// </summary>
    public static class WzVersionReader
    {
        private const uint WzOffsetConstant = 0x581C3F6D;
        private const int ReadCap = 8 * 1024 * 1024;

        public static int ReadVersion(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            long fileLen = fs.Length;
            int toRead = (int)Math.Min(fileLen, ReadCap);
            byte[] data = new byte[toRead];
            int total = 0;
            while (total < toRead)
            {
                int n = fs.Read(data, total, toRead - total);
                if (n <= 0) break;
                total += n;
            }
            if (total != data.Length) Array.Resize(ref data, total);
            return ReadVersion(data, (ulong)fileLen);
        }

        public static int ReadVersion(byte[] data, ulong fileLen)
        {
            if (data.Length < 16) throw new InvalidDataException("WZ header 太短。");
            if (data[0] != (byte)'P' || data[1] != (byte)'K' || data[2] != (byte)'G' || data[3] != (byte)'1')
                return 0;

            uint dataStart = BitConverter.ToUInt32(data, 12);
            if (dataStart < 16 || dataStart > fileLen || dataStart + 2 > data.Length)
                throw new InvalidDataException("WZ data_start 無效或不在讀取範圍內。");

            ushort encver = BitConverter.ToUInt16(data, (int)dataStart);
            int? first = null;
            for (int ver = 0; ver < 2000; ver++)
            {
                uint hash = ComputeVersionHash(ver);
                if (ComputeEncVersion(hash) != (byte)encver) continue;
                first ??= ver;
                if (DirectoryOffsetsValid(data, dataStart, fileLen, hash)) return ver;
            }
            if (first.HasValue) return first.Value;
            throw new InvalidDataException($"無法由 encver 0x{encver:X4} 判斷 WZ 版本。");
        }

        private static bool DirectoryOffsetsValid(byte[] data, uint dataStart, ulong fileLen, uint versionHash)
        {
            int pos = checked((int)dataStart + 2);
            if (!ReadCompressedInt(data, ref pos, out int count) || count <= 0 || count > 500000) return false;
            for (int i = 0; i < count; i++)
            {
                if (!ReadByte(data, ref pos, out byte type)) return false;
                switch (type)
                {
                    case 1:
                        if (pos + 6 > data.Length) return false;
                        pos += 6;
                        if (!ReadOffset(data, ref pos, dataStart, versionHash, out uint off1) || !OffsetInRange(off1, dataStart, fileLen)) return false;
                        continue;
                    case 2:
                        if (!ReadInt32(data, ref pos, out _)) return false;
                        break;
                    case 3:
                    case 4:
                        if (!SkipWzString(data, ref pos)) return false;
                        break;
                    default:
                        return false;
                }
                if (!ReadCompressedInt(data, ref pos, out _)) return false;
                if (!ReadCompressedInt(data, ref pos, out _)) return false;
                if (!ReadOffset(data, ref pos, dataStart, versionHash, out uint off) || !OffsetInRange(off, dataStart, fileLen)) return false;
            }
            return true;
        }

        private static bool OffsetInRange(uint offset, uint dataStart, ulong fileLen) => offset >= dataStart && offset < fileLen;
        private static bool ReadByte(byte[] d, ref int p, out byte v) { if (p >= d.Length) { v=0; return false; } v=d[p++]; return true; }
        private static bool ReadInt32(byte[] d, ref int p, out int v) { if (p+4>d.Length) { v=0; return false; } v=BitConverter.ToInt32(d,p); p+=4; return true; }
        private static bool ReadUInt32(byte[] d, ref int p, out uint v) { if (p+4>d.Length) { v=0; return false; } v=BitConverter.ToUInt32(d,p); p+=4; return true; }
        private static bool ReadCompressedInt(byte[] d, ref int p, out int v)
        {
            if (!ReadByte(d, ref p, out byte raw)) { v=0; return false; }
            sbyte b = unchecked((sbyte)raw);
            if (b == -128) return ReadInt32(d, ref p, out v);
            v = b; return true;
        }
        private static bool SkipWzString(byte[] d, ref int p)
        {
            if (!ReadByte(d, ref p, out byte raw)) return false;
            sbyte marker = unchecked((sbyte)raw);
            if (marker == 0) return true;
            int bytes;
            if (marker > 0)
            {
                int chars;
                if (marker == 127) { if (!ReadInt32(d, ref p, out chars) || chars < 0) return false; }
                else chars = marker;
                try { bytes = checked(chars * 2); } catch { return false; }
            }
            else
            {
                if (marker == -128) { if (!ReadInt32(d, ref p, out bytes) || bytes < 0) return false; }
                else bytes = -marker;
            }
            if (p + bytes > d.Length) return false;
            p += bytes; return true;
        }
        private static bool ReadOffset(byte[] d, ref int p, uint dataStart, uint versionHash, out uint result)
        {
            uint offsetPos = (uint)p;
            uint offset = unchecked((offsetPos - dataStart) ^ 0xFFFFFFFFu);
            offset = unchecked(offset * versionHash);
            offset = unchecked(offset - WzOffsetConstant);
            int rot = (int)(offset & 0x1F);
            offset = (offset << rot) | (offset >> ((32 - rot) & 31));
            if (!ReadUInt32(d, ref p, out uint encrypted)) { result=0; return false; }
            offset ^= encrypted;
            result = unchecked(offset + dataStart * 2);
            return true;
        }
        private static uint ComputeVersionHash(int version)
        {
            uint hash=0;
            foreach (char c in version.ToString()) hash = unchecked(hash * 32 + c + 1u);
            return hash;
        }
        private static byte ComputeEncVersion(uint hash)
        {
            byte b0=(byte)(hash>>24), b1=(byte)(hash>>16), b2=(byte)(hash>>8), b3=(byte)hash;
            return unchecked((byte)~(b0 ^ b1 ^ b2 ^ b3));
        }
    }
}
