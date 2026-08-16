using System;
using System.Runtime.InteropServices;

namespace ZenTimings
{
    /// <summary>
    /// Non-temporal stores, assembled at runtime because the framework has no way to emit them.
    /// </summary>
    /// <remarks>
    /// An ordinary store cannot write a cache line it does not own, so it fetches the line from
    /// memory first and writes it back afterwards - 128 bytes of bus traffic to deliver 64. That
    /// halves every write figure and it is not a small effect: dropping seven of every eight store
    /// instructions from the write kernel moved it by 2%, so the ownership traffic is the whole
    /// limit. movntdq skips the fetch and goes through the write-combining buffers instead.
    ///
    /// .NET Framework has no System.Runtime.Intrinsics, and unsafe C# still cannot name the
    /// instruction, so the two loops are written out as machine code and called through a
    /// delegate. The page is mapped writable, filled, and only then turned executable - never both
    /// at once, which is both correct and a great deal less alarming to a scanner.
    ///
    /// Nothing here is trusted on the strength of having been assembled correctly: <see
    /// cref="Available"/> is false unless a self-test has written and read back known bytes. Every
    /// caller keeps its ordinary loop for that case.
    /// </remarks>
    internal static unsafe class NtStore
    {
        private const uint MEM_COMMIT = 0x1000;
        private const uint MEM_RESERVE = 0x2000;
        private const uint MEM_RELEASE = 0x8000;
        private const uint PAGE_READWRITE = 0x04;
        private const uint PAGE_EXECUTE_READ = 0x20;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr VirtualAlloc(IntPtr address, UIntPtr size, uint type, uint protect);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualFree(IntPtr address, UIntPtr size, uint type);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualProtect(IntPtr address, UIntPtr size, uint protect, out uint old);

        [DllImport("kernel32.dll")]
        private static extern bool FlushInstructionCache(IntPtr process, IntPtr address, UIntPtr size);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void FillProc(long* destination, long bytes, long value);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void CopyProc(long* source, long* destination, long bytes);

        // rcx = destination, rdx = bytes, r8 = value. Bytes must be a multiple of 16 and the
        // destination 16-byte aligned; the callers guarantee both by aligning every slice and
        // block to a whole cache line.
        private static readonly byte[] FillCode =
        {
            0x66, 0x49, 0x0F, 0x6E, 0xC0,        // movq       xmm0, r8
            0x66, 0x0F, 0x6C, 0xC0,              // punpcklqdq xmm0, xmm0
            0x48, 0x31, 0xC0,                    // xor        rax, rax
            0x48, 0x39, 0xD0,                    // cmp        rax, rdx          <- loop
            0x73, 0x0B,                          // jae        done
            0x66, 0x0F, 0xE7, 0x04, 0x01,        // movntdq    [rcx+rax], xmm0
            0x48, 0x83, 0xC0, 0x10,              // add        rax, 16
            0xEB, 0xF0,                          // jmp        loop
            0x0F, 0xAE, 0xF8,                    // sfence                       <- done
            0xC3,                                // ret
        };

        // rcx = source, rdx = destination, r8 = bytes.
        private static readonly byte[] CopyCode =
        {
            0x48, 0x31, 0xC0,                    // xor        rax, rax
            0x4C, 0x39, 0xC0,                    // cmp        rax, r8           <- loop
            0x73, 0x10,                          // jae        done
            0xF3, 0x0F, 0x6F, 0x04, 0x01,        // movdqu     xmm0, [rcx+rax]
            0x66, 0x0F, 0xE7, 0x04, 0x02,        // movntdq    [rdx+rax], xmm0
            0x48, 0x83, 0xC0, 0x10,              // add        rax, 16
            0xEB, 0xEB,                          // jmp        loop
            0x0F, 0xAE, 0xF8,                    // sfence                       <- done
            0xC3,                                // ret
        };

        private static FillProc fill;
        private static CopyProc copy;
        private static bool tried;
        private static bool available;

        /// <summary>Both loops were assembled and proved themselves against known bytes.</summary>
        public static bool Available
        {
            get
            {
                if (!tried)
                {
                    tried = true;

                    // The bytes are x64. A 32-bit process decodes them as different instructions
                    // - ones that today happen to return without storing, which is luck, not a
                    // contract - and the first movd leaves MMX state behind that turns the
                    // thread's x87 doubles into NaNs.
                    available = IntPtr.Size == 8 && Build() && SelfTest();
                }

                return available;
            }
        }

        /// <summary>Writes <paramref name="value"/> across the range. 64-byte aligned, whole lines.</summary>
        public static void Fill(long* destination, int from, int to, long value)
        {
            fill(destination + from, (long)(to - from) * sizeof(long), value);
        }

        public static void Copy(long* source, long* destination, int from, int to)
        {
            copy(source + from, destination + from, (long)(to - from) * sizeof(long));
        }

        private static bool Build()
        {
            try
            {
                fill = Emit<FillProc>(FillCode);
                copy = Emit<CopyProc>(CopyCode);
                return fill != null && copy != null;
            }
            catch
            {
                return false;
            }
        }

        private static T Emit<T>(byte[] code) where T : class
        {
            IntPtr page = VirtualAlloc(IntPtr.Zero, (UIntPtr)code.Length,
                MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
            if (page == IntPtr.Zero)
                return null;

            Marshal.Copy(code, 0, page, code.Length);

            uint old;
            if (!VirtualProtect(page, (UIntPtr)code.Length, PAGE_EXECUTE_READ, out old))
            {
                // Exploit protection can refuse the W->X flip. Give the page back rather than
                // leave a writable copy of the stub mapped for the life of the process.
                VirtualFree(page, UIntPtr.Zero, MEM_RELEASE);
                return null;
            }

            FlushInstructionCache(GetCurrentProcess(), page, (UIntPtr)code.Length);
            return Marshal.GetDelegateForFunctionPointer(page, typeof(T)) as T;
        }

        /// <summary>
        /// Runs both loops over a small buffer and checks every element. Assembled by hand, so
        /// "it did not crash" is not the bar - the bytes have to be right.
        /// </summary>
        /// <remarks>
        /// The buffer has to come from VirtualAlloc. movntdq faults outright on an address that is
        /// not 16-byte aligned, and a managed array is only promised eight - pinning one would be
        /// testing the instruction on memory it is not allowed to touch.
        /// </remarks>
        private static bool SelfTest()
        {
            const int Count = 256;

            IntPtr block = VirtualAlloc(IntPtr.Zero, (UIntPtr)(Count * 2 * sizeof(long)),
                MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
            if (block == IntPtr.Zero)
                return false;

            try
            {
                const long Pattern = unchecked((long)0x0123456789ABCDEF);
                long* p = (long*)block;

                for (int i = 0; i < Count * 2; i++)
                    p[i] = 0;

                Fill(p, 0, Count, Pattern);
                for (int i = 0; i < Count; i++)
                    if (p[i] != Pattern) return false;

                // Untouched neighbours: a wrong length would have run past the end.
                for (int i = Count; i < Count * 2; i++)
                    if (p[i] != 0) return false;

                Copy(p, p + Count, 0, Count);
                for (int i = 0; i < Count; i++)
                    if (p[Count + i] != Pattern) return false;

                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                VirtualFree(block, UIntPtr.Zero, MEM_RELEASE);
            }
        }
    }
}
