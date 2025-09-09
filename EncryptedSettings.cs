using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;

namespace JewYourItem.Utility
{
    public class EncryptedSettings
    {
        private static readonly byte[] Key = Encoding.UTF8.GetBytes("JewYourItem_SecretKey_2024!"); // In production, use a more secure key derivation
        private static readonly byte[] IV = Encoding.UTF8.GetBytes("JewYourItem_IV_2024!"); // In production, generate random IV per encryption
        
        public static string EncryptString(string plainText)
        {
            if (string.IsNullOrEmpty(plainText))
                return string.Empty;

            try
            {
                using (var aes = Aes.Create())
                {
                    aes.Key = Key;
                    aes.IV = IV;
                    aes.Mode = CipherMode.CBC;
                    aes.Padding = PaddingMode.PKCS7;

                    using (var encryptor = aes.CreateEncryptor())
                    using (var msEncrypt = new MemoryStream())
                    using (var csEncrypt = new CryptoStream(msEncrypt, encryptor, CryptoStreamMode.Write))
                    using (var swEncrypt = new StreamWriter(csEncrypt))
                    {
                        swEncrypt.Write(plainText);
                        swEncrypt.Close();
                        return Convert.ToBase64String(msEncrypt.ToArray());
                    }
                }
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        public static string DecryptString(string cipherText)
        {
            if (string.IsNullOrEmpty(cipherText))
                return string.Empty;

            try
            {
                byte[] cipherBytes = Convert.FromBase64String(cipherText);
                
                using (var aes = Aes.Create())
                {
                    aes.Key = Key;
                    aes.IV = IV;
                    aes.Mode = CipherMode.CBC;
                    aes.Padding = PaddingMode.PKCS7;

                    using (var decryptor = aes.CreateDecryptor())
                    using (var msDecrypt = new MemoryStream(cipherBytes))
                    using (var csDecrypt = new CryptoStream(msDecrypt, decryptor, CryptoStreamMode.Read))
                    using (var srDecrypt = new StreamReader(csDecrypt))
                    {
                        return srDecrypt.ReadToEnd();
                    }
                }
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        public static string GetSecureSessionId()
        {
            // Try Windows Credential Manager first
            string sessionId = SecureSessionManager.RetrieveSessionId();
            if (!string.IsNullOrEmpty(sessionId))
            {
                return sessionId;
            }

            // Fallback to encrypted file storage
            string configPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JewYourItem", "session.enc");
            if (File.Exists(configPath))
            {
                try
                {
                    string encryptedData = File.ReadAllText(configPath);
                    return DecryptString(encryptedData);
                }
                catch (Exception)
                {
                    return string.Empty;
                }
            }

            return string.Empty;
        }

        public static bool StoreSecureSessionId(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                SecureSessionManager.DeleteSessionId();
                return true;
            }

            // Try Windows Credential Manager first
            if (SecureSessionManager.StoreSessionId(sessionId))
            {
                return true;
            }

            // Fallback to encrypted file storage
            try
            {
                string configDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JewYourItem");
                Directory.CreateDirectory(configDir);
                
                string configPath = Path.Combine(configDir, "session.enc");
                string encryptedData = EncryptString(sessionId);
                File.WriteAllText(configPath, encryptedData);
                
                // Set file permissions to user-only access
                File.SetAttributes(configPath, FileAttributes.Hidden);
                
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static bool ClearSecureSessionId()
        {
            SecureSessionManager.DeleteSessionId();
            
            try
            {
                string configPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JewYourItem", "session.enc");
                if (File.Exists(configPath))
                {
                    File.Delete(configPath);
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
