using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace CronosCrypter.Core
{
    public class Encryption
    {
        private const int AesSaltSize = 16;
        private const int KeyDerivationIterations = 100000;

        public static byte[] Encrypt(byte[] payload, EncryptionType encryption, string key)
        {
            if (payload == null)
            {
                throw new ArgumentNullException(nameof(payload));
            }

            if (string.IsNullOrEmpty(key))
            {
                throw new ArgumentException("Encryption key must not be null or empty.", nameof(key));
            }

            return encryption == EncryptionType.AES
                ? AES_Encrypt(payload, key)
                : XOR_Encrypt(payload, key);
        }

        private static byte[] AES_Encrypt(byte[] bytesToBeEncrypted, string encKey)
        {
            byte[] saltBytes = new byte[AesSaltSize];
            RandomNumberGenerator.Fill(saltBytes);

            byte[] passwordBytes = Encoding.UTF8.GetBytes(encKey);
            byte[] aesKey = null;
            byte[] aesIv = null;

            try
            {
                using (var keyDerivation = new Rfc2898DeriveBytes(passwordBytes, saltBytes, KeyDerivationIterations, HashAlgorithmName.SHA256))
                using (var ms = new MemoryStream())
                using (var aes = new AesCryptoServiceProvider())
                {
                    aes.KeySize = 256;
                    aes.BlockSize = 128;
                    aes.Mode = CipherMode.CBC;
                    aes.Padding = PaddingMode.PKCS7;

                    aesKey = keyDerivation.GetBytes(aes.KeySize / 8);
                    aesIv = keyDerivation.GetBytes(aes.BlockSize / 8);
                    aes.Key = aesKey;
                    aes.IV = aesIv;

                    using (var cs = new CryptoStream(ms, aes.CreateEncryptor(), CryptoStreamMode.Write))
                    {
                        cs.Write(bytesToBeEncrypted, 0, bytesToBeEncrypted.Length);
                        cs.FlushFinalBlock();
                    }

                    byte[] cipherText = ms.ToArray();
                    byte[] payloadWithSalt = new byte[saltBytes.Length + cipherText.Length];

                    Buffer.BlockCopy(saltBytes, 0, payloadWithSalt, 0, saltBytes.Length);
                    Buffer.BlockCopy(cipherText, 0, payloadWithSalt, saltBytes.Length, cipherText.Length);
                    Array.Clear(cipherText, 0, cipherText.Length);

                    return payloadWithSalt;
                }
            }
            finally
            {
                // Clear all sensitive material as soon as possible.
                Array.Clear(passwordBytes, 0, passwordBytes.Length);
                Array.Clear(saltBytes, 0, saltBytes.Length);

                if (aesKey != null)
                {
                    Array.Clear(aesKey, 0, aesKey.Length);
                }

                if (aesIv != null)
                {
                    Array.Clear(aesIv, 0, aesIv.Length);
                }
            }
        }

        private static byte[] XOR_Encrypt(byte[] bytesToBeEncrypted, string key)
        {
            byte[] encryptedBytes = new byte[bytesToBeEncrypted.Length];

            for (int i = 0; i < bytesToBeEncrypted.Length; i++)
            {
                encryptedBytes[i] = (byte)(bytesToBeEncrypted[i] ^ key[i % key.Length]);
            }

            return encryptedBytes;
        }
    }
}
