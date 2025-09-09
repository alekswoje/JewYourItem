using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;

namespace JewYourItem.Utility
{
    public class EncryptedSettings
    {
        public static string EncryptString(string plainText)
        {
            if (string.IsNullOrEmpty(plainText))
                return string.Empty;

            try
            {
                byte[] data = Encoding.UTF8.GetBytes(plainText);
                byte[] encrypted = ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
                return Convert.ToBase64String(encrypted);
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
                byte[] encrypted = Convert.FromBase64String(cipherText);
                byte[] decrypted = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(decrypted);
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
