using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Apps2Samsung.Helpers.Tizen.Certificate
{
    public class CipherUtil
    {
        private const string KeyString = "KYANINYLhijklmnopqrstuvwx";

        private byte[] KeyBytes => Encoding.UTF8.GetBytes(KeyString).Take(24).ToArray();

        public string GetEncryptedString(string plainText)
        {
            using var tdes = new TripleDESCryptoServiceProvider
            {
                Key = KeyBytes,
                Mode = CipherMode.ECB,
                Padding = PaddingMode.PKCS7
            };

            var data = Encoding.UTF8.GetBytes(plainText);
            byte[] encrypted = tdes.CreateEncryptor().TransformFinalBlock(data, 0, data.Length);
            return Convert.ToBase64String(encrypted);
        }
        public string GetDecryptedString(string encryptedBase64)
        {
            byte[] encryptedBytes = Convert.FromBase64String(encryptedBase64);

            using var tripleDes = TripleDES.Create();
            tripleDes.Key = KeyBytes;
            tripleDes.Mode = CipherMode.ECB;
            tripleDes.Padding = PaddingMode.PKCS7;

            byte[] decrypted = tripleDes.CreateDecryptor().TransformFinalBlock(encryptedBytes, 0, encryptedBytes.Length);
            return Encoding.UTF8.GetString(decrypted);
        }
        public string GenerateRandomPassword(int length = 12)
        {
            if (length < 8)
                throw new ArgumentException("Password must be at least 8 characters long.");

            const string upper = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
            const string lower = "abcdefghijklmnopqrstuvwxyz";
            const string digits = "0123456789";
            const string all = upper + lower + digits;

            var randomBytes = new byte[length];
            using var rng = RandomNumberGenerator.Create();
            rng.GetBytes(randomBytes);

            var chars = new char[length];
            chars[0] = upper[randomBytes[0] % upper.Length];
            chars[1] = lower[randomBytes[1] % lower.Length];
            chars[2] = digits[randomBytes[2] % digits.Length];

            for (int i = 3; i < length; i++)
                chars[i] = all[randomBytes[i] % all.Length];

            return new string(chars.OrderBy(_ => Guid.NewGuid()).ToArray());
        }
    }
}
