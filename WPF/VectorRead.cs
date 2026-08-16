using System;
using System.Runtime.InteropServices;

namespace ZenTimings
{
    /// <summary>
    /// Read, write and copy loops assembled at runtime, for measuring what a cache can hand a core.
    /// Two widths are built and the faster one on this silicon is the one used.
    /// </summary>
    /// <remarks>
    /// C# cannot do this. A scalar loop moves 8 bytes per load and two loads a clock, so it tops
    /// out around 16 B/clk however it is unrolled - measured on a Zen+ core at 28.5 GB/s from L1d,
    /// 28.6 from L2 and 27.8 from L3. Three identical numbers: the loop was the limit, not the
    /// cache, and a column built on it would have said nothing about the hierarchy.
    ///
    /// The width is chosen by running both, because no fixed width measures every part honestly.
    /// Measured L1d reads on a 9800X3D, separate runs so the ratios carry some clock and noise:
    /// 128-bit 150 GB/s, 256-bit 322, 512-bit 672. The 512-bit
    /// figure is 130 B/clk, which is two full-width loads a cycle - and only at that width does the
    /// hierarchy appear, because 256-bit caps L1d at the same 64 B/clk the L2 can already feed
    /// through the prefetcher, leaving the two indistinguishable. But the gain is Zen 5's alone:
    /// Zen 4 issues one 512-bit load a cycle, exactly its two 256-bit ones, so it gains nothing,
    /// and Zen 1 to 3 have no AVX-512 at all. A CPU table would have to know all of that and would
    /// be wrong on the next part; running both and timing them cannot be.
    ///
    /// VPORQ rather than VORPS at 512 bits: the bitwise float forms are AVX512DQ, the integer ones
    /// are AVX512F, and gating on F while emitting DQ would fault on F-only hardware.
    ///
    /// Deliberately in its own file rather than added to <see cref="NtStore"/> - the streaming
    /// stores there carry the DRAM write score, they bypass the cache by definition and so can
    /// never be used here, and a fault in one kernel must not take the other down with it.
    /// </remarks>
    internal static unsafe class VectorRead
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

        [DllImport("kernel32.dll")]
        private static extern bool IsProcessorFeaturePresent(uint feature);

        private const uint PF_AVX512F_INSTRUCTIONS_AVAILABLE = 41;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate long ReadProc(byte* buffer, long bytes, long repeats);

        /// <summary>
        /// Bytes each pass of the loop body covers - four accumulators at the active width. A
        /// caller's range has to be a whole number of these.
        /// </summary>
        public static int BlockBytes
        {
            get { return wide ? 256 : 128; }
        }

        /// <summary>Bits per vector in the kernels actually in use: 256 or 512.</summary>
        public static int VectorBits
        {
            get { return wide ? 512 : 256; }
        }

        // rcx = buffer, rdx = bytes, r8 = repeats, result in rax. Bytes must be a non-zero
        // multiple of 128 - the inner loop tests after incrementing, so a shorter range would read
        // one block past the end - and repeats must be at least 1, because the outer loop is a
        // decrement-and-test and zero would wrap to a count that never ends.
        //
        // The repeat count is in here rather than in the caller because the managed-to-native
        // transition costs about as much as a whole pass over an L1-sized buffer: measured on a
        // 9800X3D, calling per pass put L1d BELOW L2, which no cache can do. One transition per
        // timed window instead of thousands.
        //
        // Four accumulators because one would serialise on its own dependency - the point is to
        // keep as many loads in flight as the core will take. ymm0-3 and r8 are volatile under the
        // Windows x64 ABI, so nothing has to be saved, and vzeroupper pays off the AVX-SSE
        // transition before returning to managed code.
        private static readonly byte[] ReadCode =
        {
            0xC5, 0xFC, 0x57, 0xC0,              // vxorps  ymm0, ymm0, ymm0
            0xC5, 0xF4, 0x57, 0xC9,              // vxorps  ymm1, ymm1, ymm1
            0xC5, 0xEC, 0x57, 0xD2,              // vxorps  ymm2, ymm2, ymm2
            0xC5, 0xE4, 0x57, 0xDB,              // vxorps  ymm3, ymm3, ymm3

            0x48, 0x31, 0xC0,                    // xor     rax, rax                   <- outer
            0xC5, 0xFC, 0x56, 0x04, 0x01,        // vorps   ymm0, ymm0, [rcx+rax]      <- inner
            0xC5, 0xF4, 0x56, 0x4C, 0x01, 0x20,  // vorps   ymm1, ymm1, [rcx+rax+32]
            0xC5, 0xEC, 0x56, 0x54, 0x01, 0x40,  // vorps   ymm2, ymm2, [rcx+rax+64]
            0xC5, 0xE4, 0x56, 0x5C, 0x01, 0x60,  // vorps   ymm3, ymm3, [rcx+rax+96]
            0x48, 0x05, 0x80, 0x00, 0x00, 0x00,  // add     rax, 128
            0x48, 0x39, 0xD0,                    // cmp     rax, rdx
            0x72, 0xDE,                          // jb      inner
            0x49, 0xFF, 0xC8,                    // dec     r8
            0x75, 0xD6,                          // jnz     outer

            0xC5, 0xFC, 0x56, 0xC1,              // vorps   ymm0, ymm0, ymm1
            0xC5, 0xEC, 0x56, 0xD3,              // vorps   ymm2, ymm2, ymm3
            0xC5, 0xFC, 0x56, 0xC2,              // vorps   ymm0, ymm0, ymm2
            0xC4, 0xE3, 0x7D, 0x19, 0xC1, 0x01,  // vextractf128 xmm1, ymm0, 1
            0xC5, 0xF8, 0x56, 0xC1,              // vorps   xmm0, xmm0, xmm1
            0xC4, 0xE1, 0xF9, 0x7E, 0xC0,        // vmovq   rax, xmm0
            0xC5, 0xF8, 0x77,                    // vzeroupper
            0xC3,                                // ret
        };

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void WriteProc(byte* buffer, long bytes, long repeats);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void CopyProc(byte* source, byte* destination, long bytes, long repeats);

        // rcx = buffer, rdx = bytes, r8 = repeats. Same contract as the read loop.
        //
        // Ordinary stores, NOT the streaming ones NtStore emits. A non-temporal store is defined to
        // bypass the cache, so measuring a cache with it would report the DRAM behind it. The value
        // comes out of the buffer's own first vector so the pattern is not all zeros.
        private static readonly byte[] WriteCode =
        {
            0xC5, 0xFC, 0x10, 0x01,              // vmovups ymm0, [rcx]
            0xC5, 0xFC, 0x10, 0xC8,              // vmovups ymm1, ymm0
            0xC5, 0xFC, 0x10, 0xD0,              // vmovups ymm2, ymm0
            0xC5, 0xFC, 0x10, 0xD8,              // vmovups ymm3, ymm0

            0x48, 0x31, 0xC0,                    // xor     rax, rax                   <- outer
            0xC5, 0xFC, 0x11, 0x04, 0x01,        // vmovups [rcx+rax], ymm0            <- inner
            0xC5, 0xFC, 0x11, 0x4C, 0x01, 0x20,  // vmovups [rcx+rax+32], ymm1
            0xC5, 0xFC, 0x11, 0x54, 0x01, 0x40,  // vmovups [rcx+rax+64], ymm2
            0xC5, 0xFC, 0x11, 0x5C, 0x01, 0x60,  // vmovups [rcx+rax+96], ymm3
            0x48, 0x05, 0x80, 0x00, 0x00, 0x00,  // add     rax, 128
            0x48, 0x39, 0xD0,                    // cmp     rax, rdx
            0x72, 0xDE,                          // jb      inner
            0x49, 0xFF, 0xC8,                    // dec     r8
            0x75, 0xD6,                          // jnz     outer

            0xC5, 0xF8, 0x77,                    // vzeroupper
            0xC3,                                // ret
        };

        // rcx = source, rdx = destination, r8 = bytes, r9 = repeats. Both ranges must be the same
        // length and a multiple of 128, and both must fit in the level together - the caller halves
        // the rung for that reason.
        private static readonly byte[] CopyCode =
        {
            0x48, 0x31, 0xC0,                    // xor     rax, rax                   <- outer
            0xC5, 0xFC, 0x10, 0x04, 0x01,        // vmovups ymm0, [rcx+rax]            <- inner
            0xC5, 0xFC, 0x10, 0x4C, 0x01, 0x20,  // vmovups ymm1, [rcx+rax+32]
            0xC5, 0xFC, 0x10, 0x54, 0x01, 0x40,  // vmovups ymm2, [rcx+rax+64]
            0xC5, 0xFC, 0x10, 0x5C, 0x01, 0x60,  // vmovups ymm3, [rcx+rax+96]
            0xC5, 0xFC, 0x11, 0x04, 0x02,        // vmovups [rdx+rax], ymm0
            0xC5, 0xFC, 0x11, 0x4C, 0x02, 0x20,  // vmovups [rdx+rax+32], ymm1
            0xC5, 0xFC, 0x11, 0x54, 0x02, 0x40,  // vmovups [rdx+rax+64], ymm2
            0xC5, 0xFC, 0x11, 0x5C, 0x02, 0x60,  // vmovups [rdx+rax+96], ymm3
            0x48, 0x05, 0x80, 0x00, 0x00, 0x00,  // add     rax, 128
            0x4C, 0x39, 0xC0,                    // cmp     rax, r8
            0x72, 0xC7,                          // jb      inner   (3 - 60)
            0x49, 0xFF, 0xC9,                    // dec     r9
            0x75, 0xBF,                          // jnz     outer   (0 - 65)

            0xC5, 0xF8, 0x77,                    // vzeroupper
            0xC3,                                // ret
        };

        // ---- 512-bit forms of the same three loops -------------------------------------------
        //
        // EVEX, four bytes: 62 | R'X'B'R" 00 mm | W vvvv 1 pp | z L'L b V' aaa. Here mm=01 (0F map),
        // L'L=10 (512-bit) and V'=1, so bytes two and four are fixed at F1 and 48; only the third
        // moves with vvvv. The disp8 in an EVEX memory operand is SCALED by the vector width, so
        // 0,1,2,3 address +0,+64,+128,+192 - the same four offsets the 256-bit loops reach with
        // 0,32,64,96. Block is 256 bytes rather than 128 for the same four accumulators.

        private static readonly byte[] Read512Code =
        {
            0x62, 0xF1, 0xFD, 0x48, 0xEF, 0xC0,        // vpxorq zmm0, zmm0, zmm0
            0x62, 0xF1, 0xF5, 0x48, 0xEF, 0xC9,        // vpxorq zmm1, zmm1, zmm1
            0x62, 0xF1, 0xED, 0x48, 0xEF, 0xD2,        // vpxorq zmm2, zmm2, zmm2
            0x62, 0xF1, 0xE5, 0x48, 0xEF, 0xDB,        // vpxorq zmm3, zmm3, zmm3

            0x48, 0x31, 0xC0,                          // xor    rax, rax                <- outer 24
            0x62, 0xF1, 0xFD, 0x48, 0xEB, 0x44, 0x01, 0x00,  // vporq zmm0,zmm0,[rcx+rax]   <- inner 27
            0x62, 0xF1, 0xF5, 0x48, 0xEB, 0x4C, 0x01, 0x01,  // vporq zmm1,zmm1,[rcx+rax+64]
            0x62, 0xF1, 0xED, 0x48, 0xEB, 0x54, 0x01, 0x02,  // vporq zmm2,zmm2,[rcx+rax+128]
            0x62, 0xF1, 0xE5, 0x48, 0xEB, 0x5C, 0x01, 0x03,  // vporq zmm3,zmm3,[rcx+rax+192]
            0x48, 0x05, 0x00, 0x01, 0x00, 0x00,        // add    rax, 256
            0x48, 0x39, 0xD0,                          // cmp    rax, rdx
            0x72, 0xD5,                                // jb     inner   (27 - 70)
            0x49, 0xFF, 0xC8,                          // dec    r8
            0x75, 0xCD,                                // jnz    outer   (24 - 75)

            0x62, 0xF1, 0xFD, 0x48, 0xEB, 0xC1,        // vporq  zmm0, zmm0, zmm1
            0x62, 0xF1, 0xED, 0x48, 0xEB, 0xD3,        // vporq  zmm2, zmm2, zmm3
            0x62, 0xF1, 0xFD, 0x48, 0xEB, 0xC2,        // vporq  zmm0, zmm0, zmm2
            0xC4, 0xE1, 0xF9, 0x7E, 0xC0,              // vmovq  rax, xmm0
            0xC5, 0xF8, 0x77,                          // vzeroupper
            0xC3,                                      // ret
        };

        private static readonly byte[] Write512Code =
        {
            0x62, 0xF1, 0x7C, 0x48, 0x10, 0x01,        // vmovups zmm0, [rcx]
            0x62, 0xF1, 0x7C, 0x48, 0x10, 0xC8,        // vmovups zmm1, zmm0
            0x62, 0xF1, 0x7C, 0x48, 0x10, 0xD0,        // vmovups zmm2, zmm0
            0x62, 0xF1, 0x7C, 0x48, 0x10, 0xD8,        // vmovups zmm3, zmm0

            0x48, 0x31, 0xC0,                          // xor     rax, rax               <- outer 24
            0x62, 0xF1, 0x7C, 0x48, 0x11, 0x44, 0x01, 0x00,  // vmovups [rcx+rax],zmm0     <- inner 27
            0x62, 0xF1, 0x7C, 0x48, 0x11, 0x4C, 0x01, 0x01,  // vmovups [rcx+rax+64],zmm1
            0x62, 0xF1, 0x7C, 0x48, 0x11, 0x54, 0x01, 0x02,  // vmovups [rcx+rax+128],zmm2
            0x62, 0xF1, 0x7C, 0x48, 0x11, 0x5C, 0x01, 0x03,  // vmovups [rcx+rax+192],zmm3
            0x48, 0x05, 0x00, 0x01, 0x00, 0x00,        // add     rax, 256
            0x48, 0x39, 0xD0,                          // cmp     rax, rdx
            0x72, 0xD5,                                // jb      inner   (27 - 70)
            0x49, 0xFF, 0xC8,                          // dec     r8
            0x75, 0xCD,                                // jnz     outer   (24 - 75)

            0xC5, 0xF8, 0x77,                          // vzeroupper
            0xC3,                                      // ret
        };

        private static readonly byte[] Copy512Code =
        {
            0x48, 0x31, 0xC0,                          // xor     rax, rax                <- outer 0
            0x62, 0xF1, 0x7C, 0x48, 0x10, 0x44, 0x01, 0x00,  // vmovups zmm0,[rcx+rax]      <- inner 3
            0x62, 0xF1, 0x7C, 0x48, 0x10, 0x4C, 0x01, 0x01,  // vmovups zmm1,[rcx+rax+64]
            0x62, 0xF1, 0x7C, 0x48, 0x10, 0x54, 0x01, 0x02,  // vmovups zmm2,[rcx+rax+128]
            0x62, 0xF1, 0x7C, 0x48, 0x10, 0x5C, 0x01, 0x03,  // vmovups zmm3,[rcx+rax+192]
            0x62, 0xF1, 0x7C, 0x48, 0x11, 0x44, 0x02, 0x00,  // vmovups [rdx+rax],zmm0
            0x62, 0xF1, 0x7C, 0x48, 0x11, 0x4C, 0x02, 0x01,  // vmovups [rdx+rax+64],zmm1
            0x62, 0xF1, 0x7C, 0x48, 0x11, 0x54, 0x02, 0x02,  // vmovups [rdx+rax+128],zmm2
            0x62, 0xF1, 0x7C, 0x48, 0x11, 0x5C, 0x02, 0x03,  // vmovups [rdx+rax+192],zmm3
            0x48, 0x05, 0x00, 0x01, 0x00, 0x00,        // add     rax, 256
            0x4C, 0x39, 0xC0,                          // cmp     rax, r8
            0x72, 0xB5,                                // jb      inner   (3 - 78)
            0x49, 0xFF, 0xC9,                          // dec     r9
            0x75, 0xAD,                                // jnz     outer   (0 - 83)

            0xC5, 0xF8, 0x77,                          // vzeroupper
            0xC3,                                      // ret
        };

        private static ReadProc read, read512;
        private static WriteProc write, write512;
        private static CopyProc copy, copy512;
        private static bool wide;
        private static bool tried;
        private static bool available;

        /// <summary>The loop was assembled and proved itself against a known pattern.</summary>
        public static bool Available
        {
            get
            {
                if (!tried)
                {
                    tried = true;

                    // The bytes are x64, and a CPU without AVX raises an invalid opcode on the
                    // first vxorps. app.config enables the legacy corrupted-state policy, so the
                    // self-test's catch sees it rather than the process dying on it.
                    available = IntPtr.Size == 8 && Build() && SelfTest();

                    // Only once the narrow set is known good: the wide one is an improvement, not
                    // a requirement, and a part that cannot run it must still get a measurement.
                    //
                    // Asked BEFORE any 512-bit instruction runs, because catch-the-fault detection
                    // is only graceful while the config that enables the legacy policy travels
                    // with the exe - and this session already met a machine where it had not. The
                    // kernel folds the XCR0 state check into this answer, and a Windows too old to
                    // know the constant says no, which just means the narrow set - never a crash.
                    // The self-test stays behind it as the net for a hypervisor that lies.
                    if (available && IsProcessorFeaturePresent(PF_AVX512F_INSTRUCTIONS_AVAILABLE))
                        wide = WideSelfTest() && WideIsFaster();
                }

                return available;
            }
        }

        /// <summary>
        /// Reads the range <paramref name="repeats"/> times and returns a value derived from it,
        /// so nothing can be skipped. <paramref name="bytes"/> must be a non-zero multiple of
        /// <see cref="BlockBytes"/>, and <paramref name="repeats"/> at least 1.
        /// </summary>
        public static long Read(byte* buffer, long bytes, long repeats)
        {
            long n = repeats < 1 ? 1 : repeats;
            return wide ? read512(buffer, bytes, n) : read(buffer, bytes, n);
        }

        /// <summary>Overwrites the range, staying in cache. Same contract as <see cref="Read"/>.</summary>
        public static void Write(byte* buffer, long bytes, long repeats)
        {
            long n = repeats < 1 ? 1 : repeats;
            if (wide) write512(buffer, bytes, n); else write(buffer, bytes, n);
        }

        /// <summary>
        /// Copies source to destination, both in cache. The two ranges must be the same length and
        /// must not overlap.
        /// </summary>
        public static void Copy(byte* source, byte* destination, long bytes, long repeats)
        {
            long n = repeats < 1 ? 1 : repeats;
            if (wide) copy512(source, destination, bytes, n);
            else copy(source, destination, bytes, n);
        }

        private static bool Build()
        {
            try
            {
                read = Emit<ReadProc>(ReadCode);
                write = Emit<WriteProc>(WriteCode);
                copy = Emit<CopyProc>(CopyCode);

                // The wide set is emitted whatever the CPU is - emitting bytes cannot fault, only
                // running them can, and that is what the self-test is for.
                read512 = Emit<ReadProc>(Read512Code);
                write512 = Emit<WriteProc>(Write512Code);
                copy512 = Emit<CopyProc>(Copy512Code);

                return read != null && write != null && copy != null;
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
                VirtualFree(page, UIntPtr.Zero, MEM_RELEASE);
                return null;
            }

            FlushInstructionCache(GetCurrentProcess(), page, (UIntPtr)code.Length);
            return Marshal.GetDelegateForFunctionPointer(page, typeof(T)) as T;
        }

        private static bool SelfTest()
        {
            // rax = OR of every even element for this set: the fold ends lanes {0,2} of ymm0.
            return Prove(read, write, copy, 4, 16, 2);
        }

        /// <summary>
        /// The same checks against the 512-bit set. False on a CPU without AVX-512, where the
        /// first vpxorq raises an invalid opcode that the catch inside <see cref="Prove"/> sees.
        /// </summary>
        private static bool WideSelfTest()
        {
            if (read512 == null || write512 == null || copy512 == null)
                return false;

            // rax = OR of every eighth element: no fold across lanes, only across accumulators.
            return Prove(read512, write512, copy512, 8, 32, 8);
        }

        /// <summary>
        /// Proves one kernel set against fills designed so that the classes of error hand-assembly
        /// actually produces cannot pass. An earlier version filled with a pattern whose period was
        /// one block; every block was then identical, OR is idempotent, and a loop with the wrong
        /// stride - or one reading a quarter of each block - measured 2-4x high while passing.
        /// </summary>
        /// <remarks>
        /// Read: every element the fold reaches carries a bit no other element has, everything else
        /// carries bit 63, and one extra block past the passed length is all bit 63. Equality then
        /// requires every reachable element read at least once (each bit appears exactly once) and
        /// nothing read outside them - a wrong displacement picks up bit 63, a wrong stride or trip
        /// count leaves bits missing, and a uniform shift runs into the poisoned tail.
        ///
        /// Write: the head vector is distinct values, the rest a sentinel neither the head nor the
        /// read fill can produce, the tail poisoned again. A skipped or shifted store leaves
        /// sentinel behind or disturbs the tail. The one gap: the store at offset zero of block
        /// zero rewrites the head with itself and cannot be observed - but no uniform encoding
        /// error skips only that one.
        ///
        /// Copy: fresh pseudo-random source, sentinel destination, then an element-exact compare
        /// of both buffers - the source too, which is what catches a load/store direction swap.
        /// Every kernel runs at repeats 2 as well as 1, so the outer loop's jump executes.
        /// </remarks>
        private static bool Prove(ReadProc r, WriteProc w, CopyProc c, int lanes, int blockElems,
            int step)
        {
            const long Poison = long.MinValue;                     // bit 63
            const long Sentinel = 0x5A5A5A5A5A5A5A5AL;

            // As many blocks as keep one distinct bit per reachable element below bit 63. More
            // reachable elements per block than there are usable bits would collide them - and a
            // test whose bits collide PASSES kernels it should fail, so refuse rather than run.
            int perBlock = blockElems / step;
            if (perBlock < 1 || perBlock > 63)
                return false;

            int blocks = Math.Max(1, 63 / perBlock);
            int count = blocks * blockElems;
            long bytes = (long)count * sizeof(long);
            long total = (count + blockElems) * sizeof(long);      // one poisoned block past the end

            IntPtr block = VirtualAlloc(IntPtr.Zero, (UIntPtr)total,
                MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
            if (block == IntPtr.Zero)
                return false;

            IntPtr other = IntPtr.Zero;
            try
            {
                long* values = (long*)block;

                for (int i = 0; i < count; i++)
                    values[i] = i % step == 0 ? 1L << (i / step) : Poison;
                for (int i = count; i < count + blockElems; i++)
                    values[i] = Poison;

                long expected = 0;
                for (int i = 0; i < count; i += step)
                    expected |= values[i];

                if (r((byte*)block, bytes, 1) != expected || r((byte*)block, bytes, 2) != expected)
                    return false;

                var head = new long[lanes];
                for (int i = 0; i < lanes; i++)
                {
                    head[i] = (i + 1) * 0x0101010101010101L;
                    values[i] = head[i];
                }
                for (int i = lanes; i < count; i++)
                    values[i] = Sentinel;

                w((byte*)block, bytes, 2);
                for (int i = 0; i < count; i++)
                    if (values[i] != head[i % lanes])
                        return false;
                for (int i = count; i < count + blockElems; i++)
                    if (values[i] != Poison)
                        return false;

                other = VirtualAlloc(IntPtr.Zero, (UIntPtr)total,
                    MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
                if (other == IntPtr.Zero)
                    return false;

                long* target = (long*)other;
                long x = 0x243F6A8885A308D3L;                      // fixed seed
                for (int i = 0; i < count; i++)
                {
                    x = x * 6364136223846793005L + 1442695040888963407L;
                    values[i] = x;
                }
                for (int i = 0; i < count + blockElems; i++)
                    target[i] = Sentinel;

                c((byte*)block, (byte*)other, bytes, 2);

                x = 0x243F6A8885A308D3L;
                for (int i = 0; i < count; i++)
                {
                    x = x * 6364136223846793005L + 1442695040888963407L;
                    if (values[i] != x || target[i] != x)
                        return false;
                }
                for (int i = count; i < count + blockElems; i++)
                    if (target[i] != Sentinel)
                        return false;

                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (other != IntPtr.Zero)
                    VirtualFree(other, UIntPtr.Zero, MEM_RELEASE);

                VirtualFree(block, UIntPtr.Zero, MEM_RELEASE);
            }
        }

        /// <summary>
        /// Times both read loops over a buffer that fits in any L1d and keeps the wide one only if
        /// it wins. No CPU table: Zen 5 issues two full-width loads a cycle and doubles, Zen 4
        /// issues one and ties, and a part that ties gains nothing from the wider code. Whatever
        /// the next generation does, this asks it rather than assuming.
        /// </summary>
        private static bool WideIsFaster()
        {
            const long Bytes = 16 * 1024;              // whole blocks at either width
            const long Repeats = 4096;

            IntPtr block = VirtualAlloc(IntPtr.Zero, (UIntPtr)Bytes,
                MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
            if (block == IntPtr.Zero)
                return false;

            try
            {
                byte* p = (byte*)block;
                for (long i = 0; i < Bytes; i += sizeof(long))
                    *(long*)(p + i) = i;

                double narrow = Fastest(read, p, Bytes, Repeats);
                double broad = Fastest(read512, p, Bytes, Repeats);

                // A clear margin, not a tie broken by noise: at equal issue width the two are
                // within a percent of each other and the narrow one is the safer default.
                return broad > 0 && narrow > 0 && broad < narrow * 0.9;
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

        /// <summary>Shortest of a few runs, in seconds. Interference only ever lengthens one.</summary>
        private static double Fastest(ReadProc proc, byte* buffer, long bytes, long repeats)
        {
            proc(buffer, bytes, 16);                   // resident, and the branch predictor warm

            double best = double.MaxValue;
            for (int i = 0; i < 5; i++)
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                proc(buffer, bytes, repeats);
                watch.Stop();

                double seconds = watch.Elapsed.TotalSeconds;
                if (seconds > 0 && seconds < best)
                    best = seconds;
            }

            return best == double.MaxValue ? 0 : best;
        }
    }
}
