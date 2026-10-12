using System;
using System.IO;
using System.Management;
using System.Security.Principal;

namespace PcPrivacyCleaner.Services
{
    /// <summary>Who is running the tool and with what rights. Captured once at startup.</summary>
    public sealed class RunContext
    {
        public string ExePath { get; private set; }
        public string ExeDir { get; private set; }
        public string IdentityName { get; private set; }
        public string Sid { get; private set; }
        public bool IsAdmin { get; private set; }

        /// <summary>The user logged in at the console (may differ from IdentityName after UAC elevation).</summary>
        public string ConsoleUser { get; private set; }

        /// <summary>
        /// True when elevation switched to another account (typically IT staff typed their password).
        /// Then the current-user stores (certificates, credentials, history) belong to that other account.
        /// </summary>
        public bool OtherAccount { get; private set; }

        /// <summary>Start with 연습 모드 checked (passed through when relaunching as admin).</summary>
        public bool ScanOnly { get; set; }

        public static RunContext Capture()
        {
            var ctx = new RunContext();
            ctx.ExePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
            ctx.ExeDir = Path.GetDirectoryName(ctx.ExePath);

            var identity = WindowsIdentity.GetCurrent();
            ctx.IdentityName = identity.Name;
            ctx.Sid = identity.User != null ? identity.User.Value : "";
            ctx.IsAdmin = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);

            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT UserName FROM Win32_ComputerSystem"))
                {
                    foreach (ManagementObject mo in searcher.Get())
                    {
                        ctx.ConsoleUser = mo["UserName"] as string;
                        break;
                    }
                }
            }
            catch
            {
                ctx.ConsoleUser = null;
            }
            ctx.OtherAccount = !string.IsNullOrEmpty(ctx.ConsoleUser) &&
                !string.Equals(ctx.ConsoleUser, ctx.IdentityName, StringComparison.OrdinalIgnoreCase);
            return ctx;
        }

#if DEBUG
        /// <summary>
        /// --mock only: show a sample account in the header so screenshots (README) never carry the
        /// real PC or user name. Debug-only, like the rest of the mock path.
        /// </summary>
        public void UseSampleIdentity()
        {
            IdentityName = @"DESKTOP\sample";
            ConsoleUser = IdentityName;
            OtherAccount = false;
        }
#endif
    }
}
