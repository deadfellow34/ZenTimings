using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace ZenTimings
{
    /// <summary>
    /// Grants "Lock pages in memory" to the current account, which is what large-page buffers
    /// need.
    /// </summary>
    /// <remarks>
    /// The right lives in the LSA policy database, not under HKLM\SOFTWARE - the hive that backs
    /// it is readable by SYSTEM alone, so reg cannot touch it and secpol.msc is only a front end
    /// over these same four calls. Doing it here is what lets Windows Home users have it at all:
    /// the API ships in every edition, secpol.msc does not.
    /// </remarks>
    internal static class LargePageRight
    {
        private const string Privilege = "SeLockMemoryPrivilege";

        private const uint POLICY_CREATE_ACCOUNT = 0x00000010;
        private const uint POLICY_LOOKUP_NAMES = 0x00000800;

        [StructLayout(LayoutKind.Sequential)]
        private struct LsaObjectAttributes
        {
            public int Length;
            public IntPtr RootDirectory;
            public IntPtr ObjectName;
            public uint Attributes;
            public IntPtr SecurityDescriptor;
            public IntPtr SecurityQualityOfService;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LsaUnicodeString
        {
            public ushort Length;
            public ushort MaximumLength;
            public IntPtr Buffer;
        }

        [DllImport("advapi32.dll")]
        private static extern uint LsaOpenPolicy(IntPtr systemName, ref LsaObjectAttributes attributes,
            uint access, out IntPtr policy);

        [DllImport("advapi32.dll")]
        private static extern uint LsaAddAccountRights(IntPtr policy, byte[] sid,
            LsaUnicodeString[] rights, uint count);

        [DllImport("advapi32.dll")]
        private static extern uint LsaClose(IntPtr policy);

        [DllImport("advapi32.dll")]
        private static extern int LsaNtStatusToWinError(uint status);

        /// <summary>
        /// True when the right was added. <paramref name="error"/> carries the Win32 message when
        /// it was not.
        /// </summary>
        public static bool Grant(out string error)
        {
            error = null;
            IntPtr policy = IntPtr.Zero;
            IntPtr name = IntPtr.Zero;

            try
            {
                byte[] sid;
                using (var identity = WindowsIdentity.GetCurrent())
                {
                    if (identity == null || identity.User == null)
                    {
                        error = "No account SID.";
                        return false;
                    }

                    sid = new byte[identity.User.BinaryLength];
                    identity.User.GetBinaryForm(sid, 0);
                }

                var attributes = new LsaObjectAttributes();
                attributes.Length = Marshal.SizeOf(typeof(LsaObjectAttributes));

                uint status = LsaOpenPolicy(IntPtr.Zero, ref attributes,
                    POLICY_CREATE_ACCOUNT | POLICY_LOOKUP_NAMES, out policy);
                if (status != 0)
                {
                    error = new Win32Exception(LsaNtStatusToWinError(status)).Message;
                    return false;
                }

                // Counted in bytes and NOT null-terminated - the terminator would be taken as part
                // of the name and the call would fail on a privilege that does exist.
                name = Marshal.StringToHGlobalUni(Privilege);
                var rights = new[]
                {
                    new LsaUnicodeString
                    {
                        Buffer = name,
                        Length = (ushort)(Privilege.Length * 2),
                        MaximumLength = (ushort)((Privilege.Length + 1) * 2),
                    }
                };

                status = LsaAddAccountRights(policy, sid, rights, 1);
                if (status != 0)
                {
                    error = new Win32Exception(LsaNtStatusToWinError(status)).Message;
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                if (name != IntPtr.Zero)
                    Marshal.FreeHGlobal(name);
                if (policy != IntPtr.Zero)
                    LsaClose(policy);
            }
        }
    }
}
