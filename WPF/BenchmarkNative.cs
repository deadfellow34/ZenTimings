using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace ZenTimings
{
    /// <summary>Why the large-page attempt did not take. <see cref="None"/> when it did.</summary>
    /// <remarks>
    /// Worth separating: the right being ungranted is a one-off the user can fix, while no
    /// contiguous block free is a reboot away and says nothing about the setup. Both used to show
    /// as the same "4K pages".
    /// </remarks>
    public enum LargePageFailure
    {
        None = 0,
        NoPrivilege,
        Unsupported,
        Fragmented,
        Other,
    }

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

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessAffinityMask(IntPtr process, out UIntPtr processMask,
            out UIntPtr systemMask);

        /// <summary>
        /// Logical processors this process may run on. SetThreadAffinityMask silently refuses any
        /// mask outside it, so a pin that ignores this measures whatever core the scheduler had
        /// the thread on - Task Manager's affinity box and Process Lasso make that an everyday
        /// configuration, not an exotic one. Zero when the query fails; treat that as unrestricted.
        /// </summary>
        internal static ulong ProcessAffinityMask()
        {
            try
            {
                UIntPtr process, system;
                return GetProcessAffinityMask(GetCurrentProcess(), out process, out system)
                    ? process.ToUInt64()
                    : 0;
            }
            catch
            {
                return 0;
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatusEx
        {
            public uint Length;
            public uint MemoryLoad;
            public ulong TotalPhys;
            public ulong AvailPhys;
            public ulong TotalPageFile;
            public ulong AvailPageFile;
            public ulong TotalVirtual;
            public ulong AvailVirtual;
            public ulong AvailExtendedVirtual;
        }

        /// <summary>
        /// What Windows should still have left over once the buffers are out. The pad also absorbs
        /// the shuffled-cycle table, which is a sixteenth of the buffer and is not counted by
        /// either caller, so at the fixed 1 GB size what really remains is nearer 448 MB.
        /// </summary>
        private const ulong FreeMemoryHeadroom = 512UL * 1024 * 1024;

        /// <summary>
        /// True when that many bytes fit in free RAM. A commit the pagefile can cover succeeds
        /// with no physical memory behind it, so VirtualAlloc returning a pointer says nothing -
        /// and a benchmark that pages measures the disk. Also true when Windows will not answer:
        /// a failed query is not a reason to refuse the run.
        /// </summary>
        internal static bool FitsInFreeMemory(long bytes)
        {
            var status = new MemoryStatusEx();
            status.Length = (uint)Marshal.SizeOf(typeof(MemoryStatusEx));

            if (!GlobalMemoryStatusEx(ref status) || status.AvailPhys == 0)
                return true;

            return status.AvailPhys >= (ulong)bytes + FreeMemoryHeadroom;
        }

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
        private const uint REALTIME_PRIORITY_CLASS = 0x100;
        private const int THREAD_PRIORITY_TIME_CRITICAL = 15;
        private const int THREAD_PRIORITY_HIGHEST = 2;
        private const int RelationProcessorCore = 0;
        private const int RelationCache = 2;
        private const int ERROR_NOT_ENOUGH_MEMORY = 8;
        private const int ERROR_PRIVILEGE_NOT_HELD = 1314;
        private const int ERROR_NO_SYSTEM_RESOURCES = 1450;

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

            /// <summary>Set when the buffer fell back to 4K, so the result line can say why.</summary>
            public LargePageFailure Failure { get; private set; }

            /// <summary>What the large-page VirtualAlloc failed with; 0 when it was never reached.</summary>
            public int FailureCode { get; private set; }

            private static LargePageFailure Classify(int error)
            {
                switch (error)
                {
                    // Granted but not enabled cannot happen here - the privilege is turned on
                    // first - so this is the account having lost the right since.
                    case ERROR_PRIVILEGE_NOT_HELD:
                        return LargePageFailure.NoPrivilege;

                    // No 2 MB physical block free. Nothing to do about it beyond a reboot; large
                    // pages cannot be assembled out of scattered frames.
                    case ERROR_NO_SYSTEM_RESOURCES:
                    case ERROR_NOT_ENOUGH_MEMORY:
                        return LargePageFailure.Fragmented;

                    default:
                        return LargePageFailure.Other;
                }
            }

            public static NativeBuffer Allocate(long bytes)
            {
                var buffer = new NativeBuffer();

                if (!TryEnableLargePagePrivilege())
                {
                    buffer.Failure = LargePageFailure.NoPrivilege;
                }
                else
                {
                    ulong page = GetLargePageMinimum().ToUInt64();
                    if (page == 0)
                    {
                        buffer.Failure = LargePageFailure.Unsupported;
                    }
                    else
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

                        // Read straight away - the next managed call is free to overwrite it.
                        buffer.FailureCode = Marshal.GetLastWin32Error();
                        buffer.Failure = Classify(buffer.FailureCode);
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
        /// Bytes in the level-N data cache serving <paramref name="affinityMask"/>, or the largest
        /// of that level in the package when the mask is zero. Instruction caches are skipped -
        /// only the path a load takes matters here. On a part with unequal dies (a 7950X3D has
        /// 96 MB of L3 on one CCD and 32 on the other) the cache that matters is the one belonging
        /// to the pinned core, not the biggest in the package. Zero when the level is not reported.
        /// </summary>
        internal static long GetCacheBytes(int level, ulong affinityMask = 0)
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

                    // CACHE_RELATIONSHIP: Level +8, LineSize +10, CacheSize +12, Type +16,
                    // GROUP_AFFINITY +40. Type 1 is the instruction cache.
                    if (data[offset + 8] == level && BitConverter.ToUInt32(data, offset + 16) != 1)
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

        /// <summary>
        /// Physical cores in the package, across every processor group. <see cref="GetCoreMasks"/>
        /// stops at group 0 because SetThreadAffinityMask is group-relative; this is how a caller
        /// tells that it is looking at part of the machine. Zero when the query fails.
        /// </summary>
        internal static int PhysicalCoreCount()
        {
            try
            {
                var data = QueryProcessorInfo(RelationProcessorCore);
                if (data == null)
                    return 0;

                int count = 0;
                int offset = 0;
                while (offset + 8 <= data.Length)
                {
                    int size = BitConverter.ToInt32(data, offset + 4);
                    if (size <= 0 || offset + size > data.Length)
                        break;

                    count++;
                    offset += size;
                }

                return count;
            }
            catch
            {
                return 0;
            }
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
        /// One affinity mask per L3, which is one per CCX rather than one per CCD: Zen 1 and 2 put
        /// two on a die, and so do Strix Point and Krackan. It is still the split the sweep wants -
        /// workers drawn from one cache measure something different from a set spread over both,
        /// whether or not a fabric hop separates them. Null when the query fails.
        /// </summary>
        internal static ulong[] GetL3Groups()
        {
            try
            {
                var data = QueryProcessorInfo(RelationCache);
                if (data == null)
                    return null;

                var masks = new List<ulong>();
                int offset = 0;
                while (offset + 8 <= data.Length)
                {
                    int size = BitConverter.ToInt32(data, offset + 4);
                    if (size <= 0 || offset + size > data.Length)
                        break;

                    if (data[offset + 8] == 3 && offset + 40 + IntPtr.Size + 2 <= data.Length)
                    {
                        ulong mask = IntPtr.Size == 8
                            ? BitConverter.ToUInt64(data, offset + 40)
                            : BitConverter.ToUInt32(data, offset + 40);
                        ushort group = BitConverter.ToUInt16(data, offset + 40 + IntPtr.Size);

                        if (group == 0 && mask != 0 && !masks.Contains(mask))
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

        /// <summary>
        /// Raises the process for the scope, then restores. Realtime where the account can hold
        /// it, high otherwise.
        /// </summary>
        /// <remarks>
        /// Realtime is what keeps a driver's DPC out of a timed slice, and it is what the tools
        /// this one is compared against use. It is also the class that can make a machine
        /// unusable, so it never outlives the scope: a watchdog parked on an event puts the class
        /// back on its own if Dispose never runs. The watchdog sits above the measurement threads
        /// on purpose - at realtime with a worker pinned to every core, a lower one would not be
        /// scheduled to do it.
        /// </remarks>
        internal sealed class PriorityScope : IDisposable
        {
            /// <summary>Longer than any measurement, short enough that a hang is still a blip.</summary>
            private const int WatchdogMs = 180000;

            private readonly uint previous;
            private readonly bool raised;
            private readonly bool qosCleared;
            private readonly ManualResetEvent done;

            public bool Realtime { get; private set; }

            public PriorityScope()
            {
                // Priority does not clear EcoQoS: a process launched in efficiency mode keeps its
                // frequency cap even at REALTIME, and a cap moves the floor of every slice. The
                // opt-out is its own call, absent before Win10 1709 - a miss changes nothing.
                qosCleared = TrySetExecutionSpeedThrottling(false);

                previous = GetPriorityClass(GetCurrentProcess());
                if (previous == 0)
                    return;

                if (previous != REALTIME_PRIORITY_CLASS)
                {
                    raised = SetPriorityClass(GetCurrentProcess(), REALTIME_PRIORITY_CLASS);
                    Realtime = raised;

                    // Denied without SeIncreaseBasePriority - still worth taking high.
                    if (!raised && previous != HIGH_PRIORITY_CLASS)
                        raised = SetPriorityClass(GetCurrentProcess(), HIGH_PRIORITY_CLASS);
                }

                if (!raised)
                    return;

                // A throw past this point means no instance, so no Dispose and - worse - no
                // watchdog either. The class would stay REALTIME for the session, and the next
                // scope reads that back as the class to restore, so nothing ever puts it right.
                try
                {
                    done = new ManualResetEvent(false);
                    var watchdog = new Thread(Watch)
                    {
                        IsBackground = true,
                        Priority = ThreadPriority.Highest,
                        Name = "benchmark-priority-watchdog",
                    };
                    watchdog.Start();
                }
                catch
                {
                    SetPriorityClass(GetCurrentProcess(), previous);
                    raised = false;
                    Realtime = false;
                    throw;
                }
            }

            private void Watch()
            {
                // Blocking, not spinning: a watchdog that burns a core is the thing it exists to
                // prevent.
                if (!done.WaitOne(WatchdogMs))
                    SetPriorityClass(GetCurrentProcess(), previous);
            }

            public void Dispose()
            {
                if (raised)
                    SetPriorityClass(GetCurrentProcess(), previous);

                if (qosCleared)
                    TrySetExecutionSpeedThrottling(true);

                if (done != null)
                {
                    done.Set();

                    // Left to the finalizer on purpose: closing the handle here would race the
                    // watchdog still inside WaitOne.
                }
            }

            /// <summary>
            /// Off forces full execution speed; on hands the decision back to the system - the
            /// state before the scope, whatever the launcher had set, since there is no read API
            /// worth trusting across OS versions.
            /// </summary>
            private static bool TrySetExecutionSpeedThrottling(bool systemManaged)
            {
                try
                {
                    var state = new PROCESS_POWER_THROTTLING_STATE
                    {
                        Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                        ControlMask = systemManaged ? 0 : PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                        StateMask = 0,
                    };

                    return SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling,
                        ref state, Marshal.SizeOf(typeof(PROCESS_POWER_THROTTLING_STATE)));
                }
                catch
                {
                    return false;
                }
            }

            private const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;
            private const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;
            private const int ProcessPowerThrottling = 4;

            [StructLayout(LayoutKind.Sequential)]
            private struct PROCESS_POWER_THROTTLING_STATE
            {
                public uint Version;
                public uint ControlMask;
                public uint StateMask;
            }

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool SetProcessInformation(IntPtr hProcess,
                int informationClass, ref PROCESS_POWER_THROTTLING_STATE information, int size);
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
