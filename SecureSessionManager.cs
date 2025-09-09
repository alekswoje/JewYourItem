using System;
using System.Runtime.InteropServices;
using System.Text;

namespace JewYourItem.Utility
{
    public class SecureSessionManager
    {
        private const string CREDENTIAL_TARGET = "JewYourItem_POESESSID";
        
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CredWrite(ref CREDENTIAL credential, uint flags);
        
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CredRead(string target, CREDENTIAL_TYPE type, int reservedFlag, out IntPtr credentialPtr);
        
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool CredFree(IntPtr credential);
        
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CredDelete(string target, CREDENTIAL_TYPE type, int reservedFlag);

        private enum CREDENTIAL_TYPE : uint
        {
            GENERIC = 1,
            DOMAIN_PASSWORD = 2,
            DOMAIN_CERTIFICATE = 3,
            DOMAIN_VISIBLE_PASSWORD = 4,
            GENERIC_CERTIFICATE = 5,
            DOMAIN_EXTENDED = 6,
            MAXIMUM = 7
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct CREDENTIAL
        {
            public uint Flags;
            public CREDENTIAL_TYPE Type;
            public IntPtr TargetName;
            public IntPtr Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public IntPtr TargetAlias;
            public IntPtr UserName;
        }

        public static bool StoreSessionId(string sessionId)
        {
            try
            {
                if (string.IsNullOrEmpty(sessionId))
                {
                    return DeleteSessionId();
                }

                var credential = new CREDENTIAL
                {
                    Type = CREDENTIAL_TYPE.GENERIC,
                    TargetName = Marshal.StringToCoTaskMemUni(CREDENTIAL_TARGET),
                    CredentialBlob = Marshal.StringToCoTaskMemUni(sessionId),
                    CredentialBlobSize = (uint)Encoding.Unicode.GetByteCount(sessionId),
                    Persist = 1, // CRED_PERSIST_LOCAL_MACHINE
                    AttributeCount = 0,
                    Attributes = IntPtr.Zero,
                    UserName = IntPtr.Zero,
                    TargetAlias = IntPtr.Zero,
                    Comment = IntPtr.Zero,
                    LastWritten = new System.Runtime.InteropServices.ComTypes.FILETIME(),
                    Flags = 0
                };

                bool result = CredWrite(ref credential, 0);
                
                Marshal.FreeCoTaskMem(credential.TargetName);
                Marshal.FreeCoTaskMem(credential.CredentialBlob);
                
                return result;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static string RetrieveSessionId()
        {
            try
            {
                IntPtr credentialPtr;
                bool result = CredRead(CREDENTIAL_TARGET, CREDENTIAL_TYPE.GENERIC, 0, out credentialPtr);
                
                if (!result)
                {
                    return string.Empty;
                }

                var credential = Marshal.PtrToStructure<CREDENTIAL>(credentialPtr);
                string sessionId = Marshal.PtrToStringUni(credential.CredentialBlob, (int)credential.CredentialBlobSize / 2);
                
                CredFree(credentialPtr);
                return sessionId ?? string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        public static bool DeleteSessionId()
        {
            try
            {
                return CredDelete(CREDENTIAL_TARGET, CREDENTIAL_TYPE.GENERIC, 0);
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static bool HasStoredSessionId()
        {
            try
            {
                IntPtr credentialPtr;
                bool result = CredRead(CREDENTIAL_TARGET, CREDENTIAL_TYPE.GENERIC, 0, out credentialPtr);
                
                if (result)
                {
                    CredFree(credentialPtr);
                    return true;
                }
                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
