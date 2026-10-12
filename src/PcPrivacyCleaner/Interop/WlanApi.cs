using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PcPrivacyCleaner.Interop
{
    /// <summary>
    /// Saved Wi-Fi profiles through the Native Wifi API instead of parsing "netsh wlan show profiles",
    /// whose output text differs by Windows language. A desktop PC without Wi-Fi (WLAN service not running)
    /// simply has no profiles; that is not an error.
    /// </summary>
    internal static class WlanApi
    {
        [DllImport("wlanapi.dll")]
        private static extern int WlanOpenHandle(int clientVersion, IntPtr reserved, out int negotiatedVersion, out IntPtr handle);

        [DllImport("wlanapi.dll")]
        private static extern int WlanCloseHandle(IntPtr handle, IntPtr reserved);

        [DllImport("wlanapi.dll")]
        private static extern int WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr interfaceList);

        [DllImport("wlanapi.dll")]
        private static extern int WlanGetProfileList(IntPtr handle, ref Guid interfaceGuid, IntPtr reserved, out IntPtr profileList);

        [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)]
        private static extern int WlanDeleteProfile(IntPtr handle, ref Guid interfaceGuid, string profileName, IntPtr reserved);

        [DllImport("wlanapi.dll")]
        private static extern void WlanFreeMemory(IntPtr memory);

        // WLAN_INTERFACE_INFO: GUID (16) + WCHAR[256] description (512) + state (4) = 532 bytes.
        private const int InterfaceInfoSize = 532;
        // WLAN_PROFILE_INFO: WCHAR[256] name (512) + flags (4) = 516 bytes.
        private const int ProfileInfoSize = 516;

        public sealed class Profile
        {
            public Guid InterfaceId { get; set; }
            public string Name { get; set; }
        }

        public static List<Profile> ListProfiles()
        {
            var result = new List<Profile>();
            IntPtr handle;
            if (WlanOpenHandle(2, IntPtr.Zero, out _, out handle) != 0) return result;
            try
            {
                IntPtr ifList;
                if (WlanEnumInterfaces(handle, IntPtr.Zero, out ifList) != 0) return result;
                try
                {
                    // WLAN_INTERFACE_INFO_LIST: DWORD count, DWORD index, then the array.
                    int ifCount = Marshal.ReadInt32(ifList, 0);
                    for (int i = 0; i < ifCount; i++)
                    {
                        IntPtr info = IntPtr.Add(ifList, 8 + i * InterfaceInfoSize);
                        var guid = (Guid)Marshal.PtrToStructure(info, typeof(Guid));
                        IntPtr profList;
                        if (WlanGetProfileList(handle, ref guid, IntPtr.Zero, out profList) != 0) continue;
                        try
                        {
                            int pCount = Marshal.ReadInt32(profList, 0);
                            for (int j = 0; j < pCount; j++)
                            {
                                IntPtr p = IntPtr.Add(profList, 8 + j * ProfileInfoSize);
                                string name = Marshal.PtrToStringUni(p);
                                if (!string.IsNullOrEmpty(name)) result.Add(new Profile { InterfaceId = guid, Name = name });
                            }
                        }
                        finally
                        {
                            WlanFreeMemory(profList);
                        }
                    }
                }
                finally
                {
                    WlanFreeMemory(ifList);
                }
            }
            finally
            {
                WlanCloseHandle(handle, IntPtr.Zero);
            }
            return result;
        }

        public static void DeleteProfile(Profile profile)
        {
            IntPtr handle;
            int rc = WlanOpenHandle(2, IntPtr.Zero, out _, out handle);
            if (rc != 0) throw new InvalidOperationException("Wi-Fi 서비스에 연결하지 못했습니다 (오류 코드 " + rc + ")");
            try
            {
                var guid = profile.InterfaceId;
                rc = WlanDeleteProfile(handle, ref guid, profile.Name, IntPtr.Zero);
                if (rc != 0) throw new InvalidOperationException("Wi-Fi 삭제 실패 (오류 코드 " + rc + ")");
            }
            finally
            {
                WlanCloseHandle(handle, IntPtr.Zero);
            }
        }
    }
}
