using System;
using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Windows.Storage;

namespace uwpscrcpy
{
    public class AdbCrypto
    {
        private readonly RSA _rsa;
        private const string KEY_CONTAINER = "AdbPrivateKey_v2";

        public AdbCrypto()
        {
            _rsa = RSA.Create();
            _rsa.KeySize = 2048;

            var localSettings = ApplicationData.Current.LocalSettings;
            if (localSettings.Values.ContainsKey(KEY_CONTAINER))
            {
                string xml = localSettings.Values[KEY_CONTAINER].ToString();
                FromXmlString(_rsa, xml);
            }
            else
            {
                string xml = ToXmlString(_rsa, true);
                localSettings.Values[KEY_CONTAINER] = xml;
            }
        }

        public byte[] Sign(byte[] token)
        {
            return _rsa.SignHash(token, HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1);
        }

        public byte[] GetPublicKeyBlob()
        {
            var parameters = _rsa.ExportParameters(false);
            var nBytes = (byte[])parameters.Modulus.Clone();
            Array.Reverse(nBytes);
            var nBytesPos = new byte[nBytes.Length + 1];
            Array.Copy(nBytes, nBytesPos, nBytes.Length);
            BigInteger N = new BigInteger(nBytesPos);
            BigInteger R = BigInteger.Pow(2, 2048);
            BigInteger RR = (R * R) % N;

            uint n0 = (uint)(N & 0xFFFFFFFF);
            uint n0inv = 1;
            unchecked { for (int i = 0; i < 5; i++) n0inv *= (2 - n0 * n0inv); }
            n0inv = unchecked(0 - n0inv);

            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write((uint)64);
                w.Write(n0inv);
                WriteBigInt(w, N, 256);
                WriteBigInt(w, RR, 256);
                w.Write((uint)65537);

                string b64 = Convert.ToBase64String(ms.ToArray());
                return Encoding.UTF8.GetBytes(b64 + " scrcpy-uwp\0");
            }
        }

        private void WriteBigInt(BinaryWriter w, BigInteger bi, int size)
        {
            byte[] b = bi.ToByteArray();
            int len = Math.Min(b.Length, size);
            w.Write(b, 0, len);
            for (int i = len; i < size; i++) w.Write((byte)0);
        }

        private static string ToXmlString(RSA rsa, bool includePrivate)
        {
            RSAParameters p = rsa.ExportParameters(includePrivate);
            StringBuilder sb = new StringBuilder();
            sb.Append("<RSAKeyValue>");
            sb.Append("<Modulus>" + Convert.ToBase64String(p.Modulus) + "</Modulus>");
            sb.Append("<Exponent>" + Convert.ToBase64String(p.Exponent) + "</Exponent>");
            if (includePrivate)
            {
                sb.Append("<P>" + Convert.ToBase64String(p.P) + "</P>");
                sb.Append("<Q>" + Convert.ToBase64String(p.Q) + "</Q>");
                sb.Append("<DP>" + Convert.ToBase64String(p.DP) + "</DP>");
                sb.Append("<DQ>" + Convert.ToBase64String(p.DQ) + "</DQ>");
                sb.Append("<InverseQ>" + Convert.ToBase64String(p.InverseQ) + "</InverseQ>");
                sb.Append("<D>" + Convert.ToBase64String(p.D) + "</D>");
            }
            sb.Append("</RSAKeyValue>");
            return sb.ToString();
        }

        private static void FromXmlString(RSA rsa, string xmlString)
        {
            RSAParameters p = new RSAParameters();
            using (var xml = System.Xml.XmlReader.Create(new StringReader(xmlString)))
            {
                while (xml.Read())
                {
                    if (xml.NodeType == System.Xml.XmlNodeType.Element)
                    {
                        string name = xml.Name;
                        if (name == "RSAKeyValue") continue;
                        xml.Read();
                        if (xml.NodeType == System.Xml.XmlNodeType.Text)
                        {
                            byte[] data = Convert.FromBase64String(xml.Value);
                            switch (name)
                            {
                                case "Modulus": p.Modulus = data; break;
                                case "Exponent": p.Exponent = data; break;
                                case "P": p.P = data; break;
                                case "Q": p.Q = data; break;
                                case "DP": p.DP = data; break;
                                case "DQ": p.DQ = data; break;
                                case "InverseQ": p.InverseQ = data; break;
                                case "D": p.D = data; break;
                            }
                        }
                    }
                }
            }
            rsa.ImportParameters(p);
        }
    }
}