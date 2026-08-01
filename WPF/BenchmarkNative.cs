using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ZenTimings
{
    /// <summary>
    /// Native plumbing shared by the latency and bandwidth tests: buffer allocation with a
    /// large-page attempt, CPU topology, and priority raising.
    /// </summary>
    internal static class BenchmarkNative
    {
        private const uint MEM_COMMIT = 0x1000;
        private const uint MEM_RESERVE = 0x2000;
        private const uint MEM_RELEASE = 0x8000;
        private const uint MEM_LARGE_PAGES = 0x20000000;
        private const uint PAGE_READWRITE = 0x04;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr VirtualAlloc(IntPtr address, UIntPtr size, uint type, uint protect);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualFree(IntPtr address, UIntPtr size, uint type);

        [DllImport("kernel32.dll")]
        private static extern UIntPtr GetLargePageMinimum();

        [DllImport("kernel32.dll")]
        internal static extern IntPtr GetCurrentThread();

        [DllImport("kernel32.dll")]
        internal static extern UIntPtr SetThreadAffinityMask(IntPtr thread, UIntPtr mask);

        [DllImport("kernel32.dll")]
        private static extern bool SetThreadPriority(IntPtr thread, int priority);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll")]
        private static extern bool SetPriorityClass(IntPtr process, uint priorityClass);

        [DllImport("kernel32.dll")]
        private static extern uint GetPriorityClass(IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref uint length);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool LookupPrivilegeValue(string system, string name, out long luid);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll,
            ref TokenPrivileges state, uint length, IntPtr previous, IntPtr returnLength);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        // Pack = 4 is load-bearing: LUID is only 4-byte aligned in C, so without it Luid lands at
        // offset 8 instead of 4 and AdjustTokenPrivileges reads garbage - returning TRUE with
        // ERROR_NOT_ALL_ASSIGNED even for privileges the token holds.
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct TokenPrivileges
        {
            public uint Count;
            public long Luid;
            public uint Attributes;
        }

        private const uint TOKEN_ADJUST_PRIVILEGES = 0x20;
        private const uint TOKEN_QUERY = 0x08;
        private const uint SE_PRIVILEGE_ENABLED = 0x02;
        private const uint HIGH_PRIORITY_CLASS = 0x80;
        private const int THREAD_PRIORITY_TIME_CRITICAL = 15;
        private const int THREAD_PRIORITY_HIGHEST = 2;
        private const int RelationProcessorCore = 0;
        private const int RelationCache = 2;

        private static bool largePagePrivilegeTried;
        private static bool largePagePrivilegeHeld;

        /// <summary>
        /// "Lock pages in memory" is a policy right, off by default even for administrators.
        /// Enabling it succeeds only when the account has been granted the right; the attempt
        /// itself is harmless.
        /// </summary>
        private static bool TryEnableLargePagePrivilege()
        {
            if (largePagePrivilegeTried)
                return largePagePrivilegeHeld;

            largePagePrivilegeTried = true;

            IntPtr token = IntPtr.Zero;
            try
            {
                if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out token))
                    return false;

                long luid;
                if (!LookupPrivilegeValue(null, "SeLockMemoryPrivilege", out luid))
                    return false;

                var state = new TokenPrivileges { Count = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
                if (!AdjustTokenPrivileges(token, false, ref state, 0, IntPtr.Zero, IntPtr.Zero))
                    return false;

                // AdjustTokenPrivileges reports success even when nothing was assigned.
                largePagePrivilegeHeld = Marshal.GetLastWin32Error() == 0;
                return largePagePrivilegeHeld;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (token != IntPtr.Zero)
                    CloseHandle(token);
            }
        }

        /// <summary>
        /// A committed read-write region, large-page backed when the privilege allows it. Large
        /// pages collapse the TLB-miss cost of a random walk and pin the physical layout, which is
        /// what makes runs repeatable - so they are always attempted first.
        /// </summary>
        internal sealed class NativeBuffer : IDisposable
        {
            public IntPtr Pointer { get; private set; }
            public long Bytes { get; private set; }
            public bool LargePages { get; private set; }

            public static NativeBuffer Allocate(long bytes)
            {
                var buffer = new NativeBuffer();

                if (TryEnableLargePagePrivilege())
                {
                    ulong page = GetLargePageMinimum().ToUInt64();
                    if (page > 0)
                    {
                        ulong rounded = ((ulong)bytes + page - 1) / page * page;
                        buffer.Pointer = VirtualAlloc(IntPtr.Zero, (UIntPtr)rounded,
                            MEM_COMMIT | MEM_RESERVE | MEM_LARGE_PAGES, PAGE_READWRITE);
                        if (buffer.Pointer != IntPtr.Zero)
                        {
                            buffer.Bytes = bytes;
                            buffer.LargePages = true;
                            return buffer;
                        }
                    }
                }

                buffer.Pointer = VirtualAlloc(IntPtr.Zero, (UIntPtr)bytes,
                    MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
                if (buffer.Pointer == IntPtr.Zero)
                    throw new OutOfMemoryException();

                buffer.Bytes = bytes;
                return buffer;
            }

            public void Dispose()
            {
                if (Pointer != IntPtr.Zero)
                {
                    VirtualFree(Pointer, UIntPtr.Zero, MEM_RELEASE);
                    Pointer = IntPtr.Zero;
                }
            }
        }

        /// <summary>
        /// A random permutation of 0..lines-1, used by both tests to lay out a pointer chase that
        /// no prefetcher can follow. The seed is fixed on purpose: two runs on the same machine
        /// should differ because the hardware differed, not because the walk did.
        /// </summary>
        internal static int[] BuildShuffledCycle(int lines)
        {
            var order = new int[lines];
            for (int i = 0; i < lines; i++)
                order[i] = i;

            var random = new Random(12345);
            for (int i = lines - 1; i > 0; i--)
            {
                int j = random.Next(i + 1);
                int t = order[i]; order[i] = order[j]; order[j] = t;
            }

            return order;
        }

        private static byte[] QueryProcessorInfo(int relationship)
        {
            uint length = 0;
            GetLogicalProcessorInformationEx(relationship, IntPtr.Zero, ref length);
            if (length == 0)
                return null;

            IntPtr buffer = Marshal.AllocHGlobal((int)length);
            try
            {
                if (!GetLogicalProcessorInformationEx(relationship, buffer, ref length))
                    return null;

                var bytes = new byte[length];
                Marshal.Copy(buffer, bytes, 0, (int)length);
                return bytes;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        /// <summary>
        /// L3 size in bytes. With a non-zero <paramref name="affinityMask"/> it is the L3 serving
        /// those processors - on a part with unequal dies (a 7950X3D has 96 MB on one CCD and 32
        /// on the other) the cache that matters is the one belonging to the pinned core, not the
        /// biggest in the package. Zero when the query fails.
        /// </summary>
        internal static long GetL3Bytes(ulong affinityMask = 0)
        {
            try
            {
                var data = QueryProcessorInfo(RelationCache);
                if (data == null)
                    return 0;

                long best = 0;
                long matched = 0;
                int offset = 0;
                while (offset + 8 <= data.Length)
                {
                    int size = BitConverter.ToInt32(data, offset + 4);
                    if (size <= 0 || offset + size > data.Length)
                        break;

                    // CACHE_RELATIONSHIP: Level at +8, CacheSize (bytes) at +12,
                    // GROUP_AFFINITY at +40 (Level..Type 12 B, Reserved 18 B, GroupCount 2 B).
                    if (data[offset + 8] == 3)
                    {
                        long cache = BitConverter.ToUInt32(data, offset + 12);
                        if (cache > best)
                            best = cache;

                        if (affinityMask != 0 && offset + 40 + IntPtr.Size + 2 <= data.Length)
                        {
                            ulong mask = IntPtr.Size == 8
                                ? BitConverter.ToUInt64(data, offset + 40)
                                : BitConverter.ToUInt32(data, offset + 40);
                            ushort group = BitConverter.ToUInt16(data, offset + 40 + IntPtr.Size);
                            if (group == 0 && (mask & affinityMask) != 0 && cache > matched)
                                matched = cache;
                        }
                    }

                    offset += size;
                }

                return matched > 0 ? matched : best;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>L3 of the core the latency walk pins itself to.</summary>
        internal static long GetPinnedL3Bytes()
        {
            return GetL3Bytes(LastCoreFirstLpMask());
        }

        /// <summary>
        /// One affinity mask per physical core (group 0 only - desktop parts), each with all of
        /// the core's logical processors set. Null when the query fails.
        /// </summary>
        internal static ulong[] GetCoreMasks()
        {
            try
            {
                var data = QueryProcessorInfo(RelationProcessorCore);
                if (data == null)
                    return null;

                var masks = new List<ulong>();
                int offset = 0;
                while (offset + 8 <= data.Length)
                {
                    int size = BitConverter.ToInt32(data, offset + 4);
                    if (size <= 0 || offset + size > data.Length)
                        break;

                    // PROCESSOR_RELATIONSHIP: GROUP_AFFINITY array starts at +32;
                    // Mask is pointer-sized, Group follows it.
                    int maskOffset = offset + 32;
                    if (maskOffset + IntPtr.Size + 2 <= data.Length)
                    {
                        ulong mask = IntPtr.Size == 8
                            ? BitConverter.ToUInt64(data, maskOffset)
                            : BitConverter.ToUInt32(data, maskOffset);
                        ushort group = BitConverter.ToUInt16(data, maskOffset + IntPtr.Size);
                        if (group == 0 && mask != 0)
                            masks.Add(mask);
                    }

                    offset += size;
                }

                return masks.Count > 0 ? masks.ToArray() : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// First logical processor of the last physical core. CPU 0 is where Windows concentrates
        /// the clock interrupt and most DPCs, so it is the one core a latency kernel must avoid.
        /// </summary>
        internal static ulong LastCoreFirstLpMask()
        {
            var cores = GetCoreMasks();
            if (cores != null)
            {
                ulong mask = cores[cores.Length - 1];
                return mask & (~mask + 1);   // lowest set bit
            }

            int last = Math.Min(Environment.ProcessorCount, 64) - 1;
            return last > 0 ? 1UL << last : 1UL;
        }

        /// <summary>Raises the process to HIGH_PRIORITY_CLASS for the scope, then restores.</summary>
        internal sealed class PriorityScope : IDisposable
        {
            private readonly uint previous;
            private readonly bool raised;

            public PriorityScope()
            {
                previous = GetPriorityClass(GetCurrentProcess());
                if (previous != 0 && previous != HIGH_PRIORITY_CLASS)
                    raised = SetPriorityClass(GetCurrentProcess(), HIGH_PRIORITY_CLASS);
            }

            public void Dispose()
            {
                if (raised)
                    SetPriorityClass(GetCurrentProcess(), previous);
            }
        }

        /// <summary>
        /// TIME_CRITICAL for the timed region only - managed ThreadPriority stops at Highest,
        /// which base-priority-wise still yields to kernel work items.
        /// </summary>
        internal static void SetTimeCritical(bool on)
        {
            SetThreadPriority(GetCurrentThread(), on ? THREAD_PRIORITY_TIME_CRITICAL : THREAD_PRIORITY_HIGHEST);
        }
    }
}
