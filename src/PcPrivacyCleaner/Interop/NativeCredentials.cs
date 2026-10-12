using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PcPrivacyCleaner.Interop
{
    /// <summary>
    /// Windows Credential Manager (the "자격 증명 관리자" list) for the current user.
    /// Uses the API directly instead of parsing "cmdkey /list", whose output text differs by Windows language.
    /// Passwords are never read: only target name, user name and type.
    /// </summary>
    internal static class NativeCredentials
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct CREDENTIAL
        {
            public int Flags;
            public int Type;
            public string TargetName;
            public string Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public int CredentialBlobSize;
            public IntPtr CredentialBlob;
            public int Persist;
            public int AttributeCount;
            public IntPtr Attributes;
            public string TargetAlias;
            public string UserName;
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredEnumerate(string filter, int flags, out int count, out IntPtr credentials);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredDelete(string target, int type, int flags);

        [DllImport("advapi32.dll")]
        private static extern void CredFree(IntPtr buffer);

        public sealed class Entry
        {
            public string Target { get; set; }
            public string UserName { get; set; }
            public int Type { get; set; }
        }

        public static List<Entry> List()
        {
            var list = new List<Entry>();
            int count;
            IntPtr ptr;
            // Returns false with ERROR_NOT_FOUND when the list is empty.
            if (!CredEnumerate(null, 0, out count, out ptr)) return list;
            try
            {
                for (int i = 0; i < count; i++)
                {
                    IntPtr credPtr = Marshal.ReadIntPtr(ptr, i * IntPtr.Size);
                    var c = (CREDENTIAL)Marshal.PtrToStructure(credPtr, typeof(CREDENTIAL));
                    list.Add(new Entry { Target = c.TargetName, UserName = c.UserName, Type = c.Type });
                }
            }
            finally
            {
                CredFree(ptr);
            }
            return list;
        }

        public static void Delete(string target, int type)
        {
            if (!CredDelete(target, type, 0))
                throw new InvalidOperationException("자격 증명 삭제 실패 (오류 코드 " + Marshal.GetLastWin32Error() + ")");
        }
    }
}
