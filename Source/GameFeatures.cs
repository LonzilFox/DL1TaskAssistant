using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace DL1Assistant
{
    internal static partial class Native
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool VirtualProtectEx(IntPtr process, IntPtr address, UIntPtr size, uint newProtect, out uint oldProtect);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool FlushInstructionCache(IntPtr process, IntPtr address, UIntPtr size);
    }

    internal sealed partial class GameClient
    {
        // Exact 1.55 hooks cross-checked against the supplied XXXeLxxx / IMIG CTs and this DLL SHA.
        private const long RVA_CE_Stealth = 0x6C4D22L;
        private const long RVA_IMIG_SprintSpeed = 0xD4D286L;
        private const long RVA_CE_WalkSpeed = 0xC58EB1L;
        private const long RVA_CE_JumpAob = 0xD4EC66L;
        private const long RVA_CE_JumpPatch = 0xD4EC6AL;
        private const long RVA_CE_InstantKillNearby = 0x30E15FL;
        private const long RVA_CE_VolatileVisibility = 0xB82843L;
        private const long RVA_CE_ChallengeTimer = 0x538D59L;
        private const long RVA_CE_MissionTimer = 0x48C638L;
        private const long RVA_CE_EasyCraft1 = 0x37D60FL;
        private const long RVA_CE_EasyCraft2 = 0x37CCA3L;
        private const long RVA_CE_EasyCraft3 = 0x37C618L;

        // IMIG -4.5P1 (2025-07-31) current backpack/base-item layout; all three AOBs
        // are unique in the user's exact 1.55 gamedll.
        private const long RVA_BaseItemManagerObject = 0x1D0B2F0L;
        private const long OFF_BaseItemTree = 0x68L;
        private const long OFF_BaseItemName = 0x18L;
        private const long OFF_BaseItemLocalizedName = 0x10L;
        private const long OFF_BaseItemIndex = 0x58L;

        private sealed class RuntimePatch
        {
            internal IntPtr Target;
            internal byte[] Original;
            internal IntPtr Cave;
            internal int Length;
            internal IntPtr Factor;
            internal IntPtr Data;
            internal IntPtr Aux;
            internal bool Enabled;
        }

        private RuntimePatch _v009Run;
        private RuntimePatch _v011WalkSpeed;
        private RuntimePatch _v009Jump;
        private RuntimePatch _v009Stealth;
        private RuntimePatch _v009VolatileVisibility;
        private RuntimePatch _v009ChallengeTimer;
        private RuntimePatch _v009MissionTimer;
        private RuntimePatch _v009Kill;
        private RuntimePatch _v009EasyCraft3;
        private byte[] _v009EasyCraft1Original;
        private byte[] _v009EasyCraft2Original;
        private bool _v009EasyCraft;
        private readonly Dictionary<string, IntPtr> _v009BaseItems = new Dictionary<string, IntPtr>(StringComparer.OrdinalIgnoreCase);
        private bool _v010BaseItemCatalogComplete;

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private static byte[] AbsJump(long destination)
        {
            List<byte> b = new List<byte>();
            b.Add(0xFF); b.Add(0x25); b.AddRange(new byte[] { 0, 0, 0, 0 });
            b.AddRange(BitConverter.GetBytes(destination));
            return b.ToArray();
        }

        private IntPtr AllocateNear(IntPtr target, int size)
        {
            long t = target.ToInt64();
            long aligned = t & ~0xFFFFL;
            const long maxDistance = 0x70000000L;
            for (long distance = 0x10000L; distance <= maxDistance; distance += 0x10000L)
            {
                long[] candidates = new long[] { aligned + distance, aligned - distance };
                foreach (long candidate in candidates)
                {
                    if (candidate < 0x10000L) continue;
                    long rel = candidate - (t + 5L);
                    if (rel < Int32.MinValue || rel > Int32.MaxValue) continue;
                    IntPtr p = Native.VirtualAllocEx(_handle, new IntPtr(candidate), new UIntPtr((uint)size), MEM_COMMIT_RESERVE, PAGE_EXECUTE_READWRITE);
                    if (p != IntPtr.Zero) return p;
                }
            }
            throw new InvalidOperationException("无法在 Hook 点 ±2GB 内分配代码洞；为避免错误跳转已停止。");
        }

        private void WriteCode(IntPtr address, byte[] bytes)
        {
            uint old;
            if (!Native.VirtualProtectEx(_handle, address, new UIntPtr((uint)bytes.Length), PAGE_EXECUTE_READWRITE, out old))
                throw Win32("VirtualProtectEx 0x" + address.ToInt64().ToString("X"));
            try
            {
                WriteBytes(address, bytes);
                Native.FlushInstructionCache(_handle, address, new UIntPtr((uint)bytes.Length));
            }
            finally
            {
                uint ignored;
                Native.VirtualProtectEx(_handle, address, new UIntPtr((uint)bytes.Length), old, out ignored);
            }
        }

        private RuntimePatch InstallCavePatch(long rva, byte[] expected, Func<IntPtr, byte[]> caveBuilder, int caveSize)
        {
            IntPtr target = Add(_dllBase, rva);
            byte[] live = ReadBytes(target, expected.Length);
            if (!BytesEqual(live, expected))
                throw new InvalidOperationException("Hook 点字节不匹配：gamedll+0x" + rva.ToString("X") + "\r\n期望：" + BitConverter.ToString(expected) + "\r\n当前：" + BitConverter.ToString(live));
            IntPtr cave = AllocateNear(target, Math.Max(0x1000, caveSize));
            try
            {
                byte[] code = caveBuilder(cave);
                WriteCode(cave, code);
                long rel64 = cave.ToInt64() - (target.ToInt64() + 5L);
                if (rel64 < Int32.MinValue || rel64 > Int32.MaxValue) throw new InvalidOperationException("代码洞距离超出 rel32 范围。");
                byte[] patch = new byte[expected.Length];
                patch[0] = 0xE9;
                Array.Copy(BitConverter.GetBytes((int)rel64), 0, patch, 1, 4);
                for (int i = 5; i < patch.Length; i++) patch[i] = 0x90;
                WriteCode(target, patch);
                return new RuntimePatch { Target = target, Original = expected, Cave = cave, Length = expected.Length, Enabled = true };
            }
            catch
            {
                Native.VirtualFreeEx(_handle, cave, UIntPtr.Zero, MEM_RELEASE);
                throw;
            }
        }

        private void RemovePatch(ref RuntimePatch patch)
        {
            RuntimePatch p = patch;
            patch = null;
            if (p == null) return;
            try
            {
                if (p.Target != IntPtr.Zero && p.Original != null)
                {
                    byte[] live = ReadBytes(p.Target, p.Original.Length);
                    bool ours = live.Length >= 5 && live[0] == 0xE9;
                    if (ours)
                    {
                        int rel = BitConverter.ToInt32(live, 1);
                        long destination = p.Target.ToInt64() + 5L + rel;
                        ours = destination == p.Cave.ToInt64();
                    }
                    if (ours) WriteCode(p.Target, p.Original);
                    // If another trainer replaced this site after us, leave its bytes alone.
                }
            }
            catch { }
            try { if (p.Cave != IntPtr.Zero) Native.VirtualFreeEx(_handle, p.Cave, UIntPtr.Zero, MEM_RELEASE); } catch { }
        }

        private void SetPatchFactor(RuntimePatch patch, float value)
        {
            if (patch == null || patch.Factor == IntPtr.Zero) throw new InvalidOperationException("倍率 Hook 尚未初始化。");
            WriteBytes(patch.Factor, BitConverter.GetBytes(value));
            // A new multiplier needs new native execution evidence.  Reset the
            // one-shot hit flag on each apply rather than reporting a stale hit.
            if (patch.Data != IntPtr.Zero) WriteBytes(patch.Data, new byte[4]);
        }

        // WalkSpeed keeps its original consumer instruction and an in-cave float.
        // SprintSpeed captures its operand separately, avoiding multiplication twice.
        // Layout: mark[10], original[5/6], mulss[8], absolute jump[14], float[4], hit[4].
        // The hit marker is set by the game code itself and can be read without polling.
        private RuntimePatch InstallSpeedMultiplierV011(long rva, byte[] original, byte xmmRegister, int factor)
        {
            RuntimePatch patch = InstallCavePatch(rva, original, delegate(IntPtr cave)
            {
                int dataOffset = 10 + original.Length + 8 + 14;
                int hitOffset = dataOffset + 4;
                List<byte> code = new List<byte>();
                code.AddRange(new byte[] { 0xC7, 0x05 }); // mov dword ptr [rip+hit], 1
                code.AddRange(BitConverter.GetBytes(hitOffset - 10));
                code.AddRange(new byte[] { 1, 0, 0, 0 });
                code.AddRange(original);
                // mulss xmm0 / xmm6, dword ptr [rip+14] (float after the absolute jump)
                code.AddRange(new byte[] { 0xF3, 0x0F, 0x59, (byte)(xmmRegister == 0 ? 0x05 : 0x35), 0x0E, 0, 0, 0 });
                code.AddRange(AbsJump(Add(_dllBase, rva + original.Length).ToInt64()));
                code.AddRange(BitConverter.GetBytes((float)factor));
                code.AddRange(new byte[4]);
                return code.ToArray();
            }, 0x1000);
            patch.Factor = Add(patch.Cave, 10 + original.Length + 8 + 14);
            patch.Data = Add(patch.Factor, 4);
            return patch;
        }

        // IMIG SprintSpeed1 uses RDX as the actual movement-parameter pointer. Capture
        // its address as well as a hit flag. A hit flag alone cannot prove velocity grew:
        // the engine may clamp the final speed against MoveOverSpeedMaxSpeed.
        private RuntimePatch InstallSprintOperandHookV012(int factor)
        {
            byte[] original = { 0xF3, 0x0F, 0x59, 0x72, 0x08 };
            RuntimePatch p = InstallCavePatch(RVA_IMIG_SprintSpeed, original, delegate(IntPtr cave)
            {
                List<byte> code = new List<byte>();
                // cave 0..6: mov [rip+33],rdx -> last seen RDX at cave+40.
                code.AddRange(new byte[] { 0x48, 0x89, 0x15, 0x21, 0, 0, 0 });
                // cave 7..16: mov dword [rip+19],1 -> hit flag at cave+36.
                code.AddRange(new byte[] { 0xC7, 0x05, 0x13, 0, 0, 0, 1, 0, 0, 0 });
                // Run the game's original mulss exactly once. The actual speed factor
                // is applied to RDX+8 instead; do not multiply both RDX+8 and xmm6,
                // otherwise x2 becomes x4 on this path (x10 can become x100).
                code.AddRange(original);
                code.AddRange(AbsJump(Add(_dllBase, RVA_IMIG_SprintSpeed + original.Length).ToInt64()));
                code.AddRange(new byte[12]); // at cave+36: int hit, at cave+40: qword RDX
                return code.ToArray();
            }, 0x1000);
            p.Data = Add(p.Cave, 36);
            p.Aux = Add(p.Cave, 40);
            return p;
        }

        // Keep one baseline per validated RDX+8 location.  If movement-table IDs
        // alias this location, both paths must share the same baseline (no x^2).
        private readonly Dictionary<long, float> _v012SprintOperandBaseline = new Dictionary<long, float>();

        private string UpdateSprintOperandV012(int factor)
        {
            if (_v009Run == null || _v009Run.Aux == IntPtr.Zero) return "Sprint 读取点尚未捕获";
            IntPtr owner;
            try { owner = ReadPtr(_v009Run.Aux); } catch { return "Sprint 捕获区暂不可读"; }
            if (owner == IntPtr.Zero) return "请先在游戏中真正冲刺一次";
            IntPtr address = Add(owner, 8);
            try
            {
                float observed = ReadFloat(address);
                if (Single.IsNaN(observed) || Single.IsInfinity(observed) || observed < 0.001F || observed > 10000F)
                    return "Sprint 参数数值异常，未写入";
                float baseline;
                lock (_featureSync)
                {
                    if (!_v012SprintOperandBaseline.TryGetValue(address.ToInt64(), out baseline))
                    {
                        if (_v012SprintOperandBaseline.Count >= 128) return "速度对象变化过于频繁，已停止直接写入";
                        // If an earlier parameter write reached the same address, reuse its baseline.
                        if (!_movementOriginals.TryGetValue(address.ToInt64(), out baseline)) baseline = observed;
                        _v012SprintOperandBaseline[address.ToInt64()] = baseline;
                    }
                }
                float desired = baseline * factor;
                if (Math.Abs(observed - desired) > 0.001F) WriteFloat(address, desired);
                return "Sprint 参数地址=0x" + address.ToInt64().ToString("X") +
                    "；原始=" + baseline.ToString("0.###") + "；读回=" + ReadFloat(address).ToString("0.###");
            }
            catch { return "Sprint 参数对象已卸载，等待再次冲刺"; }
        }

        private void RestoreSprintOperandV012()
        {
            lock (_featureSync)
            {
                foreach (KeyValuePair<long, float> pair in _v012SprintOperandBaseline)
                    try { WriteFloat(new IntPtr(pair.Key), pair.Value); } catch { }
                _v012SprintOperandBaseline.Clear();
            }
        }

        private string SpeedPathStatusV011(RuntimePatch patch, string label)
        {
            if (patch == null) return label + " 未启用";
            byte[] code = ReadBytes(patch.Target, patch.Original.Length);
            if (code[0] != 0xE9) return label + " Hook 已被覆盖";
            long actual = patch.Target.ToInt64() + 5 + BitConverter.ToInt32(code, 1);
            if (actual != patch.Cave.ToInt64()) return label + " Hook 已被其他程序替换";
            bool hit = BitConverter.ToInt32(ReadBytes(patch.Data, 4), 0) == 1;
            return label + (hit ? " 已命中" : " 未命中(需要在游戏里跑动)");
        }

        internal string GetRunSpeedStatusV011()
        {
            EnsureConnected();
            return SpeedPathStatusV011(_v011WalkSpeed, "XXXeLxxx WalkSpeed") + "；" +
                SpeedPathStatusV011(_v009Run, "IMIG SprintSpeed1") + "；" +
                (_runSpeedFactor <= 1 ? "倍率 x1" : UpdateSprintOperandV012(_runSpeedFactor)) + "；" +
                DescribeMovementParam(PARAM_MoveOverSpeedMaxSpeed, _runSpeedFactor) + "；" +
                DescribeMovementParam(PARAM_MovementSpeedMul, _runSpeedFactor);
        }

        internal string SetRunSpeedFactor(int factor)
        {
            EnsureConnected();
            factor = Math.Max(1, Math.Min(10, factor));
            if (factor == 1)
            {
                lock (_featureSync) _runSpeedFactor = 1;
                RemovePatch(ref _v011WalkSpeed);
                RemovePatch(ref _v009Run);
                RestoreMovementId(PARAM_MoveStrafeMaxSpeed);
                RestoreMovementId(PARAM_MoveForwardMinSpeed);
                RestoreMovementId(PARAM_MoveSprintSpeed);
                RestoreMovementId(PARAM_MoveOverSpeedMaxSpeed);
                RestoreMovementId(PARAM_MoveSprintAcceleration);
                RestoreMovementId(PARAM_MovementSpeedMul);
                RestoreSprintOperandV012();
                return "已恢复原版跑速；两条执行路径均已清理。";
            }
            // XXXeLxxx +0xC58EB1 handles base movement output (xmm0);
            // IMIG +0xD4D286 handles sprint output (xmm6). Neither relies on
            // guessed writable speed parameters. They may affect distinct routes.
            bool madeWalk = false;
            bool madeSprint = false;
            try
            {
                if (_v011WalkSpeed == null)
                {
                    _v011WalkSpeed = InstallSpeedMultiplierV011(RVA_CE_WalkSpeed,
                        new byte[] { 0xF3, 0x41, 0x0F, 0x59, 0x41, 0x08 }, 0, factor);
                    madeWalk = true;
                }
                if (_v009Run == null)
                {
                    _v009Run = InstallSprintOperandHookV012(factor);
                    madeSprint = true;
                }
                SetPatchFactor(_v011WalkSpeed, factor);
                // Sprint hook captures the real operand and retains the original
                // instruction. Its multiplier is applied to that operand once.
                WriteBytes(_v009Run.Data, new byte[4]);
            }
            catch
            {
                // A partial failure must never leave a newly installed code cave
                // behind or enable the background keeper for a factor that failed.
                if (madeSprint) RemovePatch(ref _v009Run);
                if (madeWalk) RemovePatch(ref _v011WalkSpeed);
                throw;
            }
            lock (_featureSync) _runSpeedFactor = factor;
            try
            {
                ApplyMovementFactor(PARAM_MoveStrafeMaxSpeed, factor);
                ApplyMovementFactor(PARAM_MoveForwardMinSpeed, factor);
                ApplyMovementFactor(PARAM_MoveSprintSpeed, factor);
                ApplyMovementFactor(PARAM_MoveOverSpeedMaxSpeed, factor);
                ApplyMovementFactor(PARAM_MoveSprintAcceleration, factor);
                ApplyMovementFactor(PARAM_MovementSpeedMul, factor);
            }
            catch { /* Hook remains available when a parameter table is not loaded. */ }
            return "x" + factor + " 已启用（两处运算 Hook + 速度上限/加速度/移动乘数）；" +
                GetRunSpeedStatusV011() + "。命中与实测位移是两回事，建议在平地按相同路程测速。";
        }

        internal string SetJumpHeightFactor(int factor)
        {
            EnsureConnected();
            factor = Math.Max(1, Math.Min(10, factor));
            if (factor == 1)
            {
                RemovePatch(ref _v009Jump);
                return "已恢复原版跳跃高度。";
            }
            if (_v009Jump == null)
            {
                // Validate only before the first install. Once our E9 is present, re-reading the
                // original AOB would reject x2 -> x3/x4... changes until x1 removed the hook.
                byte[] aob = ReadBytes(Add(_dllBase, RVA_CE_JumpAob), 9);
                byte[] expectedAob = new byte[] { 0x28, 0x15, 0x00, 0x00, 0xF3, 0x0F, 0x59, 0x70, 0x08 };
                if (!BytesEqual(aob, expectedAob)) throw new InvalidOperationException("当前 1.55 Jump AOB 不匹配，已停止写入。");
                byte[] original = new byte[] { 0xF3, 0x0F, 0x59, 0x70, 0x08 };
                RuntimePatch created = null;
                created = InstallCavePatch(RVA_CE_JumpPatch, original, delegate(IntPtr cave)
                {
                    List<byte> c = new List<byte>();
                    c.AddRange(new byte[] { 0xF3, 0x0F, 0x10, 0x35, 0x13, 0x00, 0x00, 0x00 }); // movss xmm6,[rip+factor]
                    c.AddRange(original);
                    c.AddRange(AbsJump(Add(_dllBase, RVA_CE_JumpPatch + original.Length).ToInt64()));
                    c.AddRange(BitConverter.GetBytes((float)factor));
                    return c.ToArray();
                }, 0x1000);
                created.Factor = Add(created.Cave, 27);
                _v009Jump = created;
            }
            SetPatchFactor(_v009Jump, factor);
            return "当前 1.55 CE Jump 消费点 gamedll+0xD4EC6A 已启用，倍率 x" + factor + "。";
        }

        internal void SetInvisibleToEnemies(bool enabled)
        {
            EnsureConnected();
            if (!enabled)
            {
                RemovePatch(ref _v009VolatileVisibility);
                RemovePatch(ref _v009Stealth);
                return;
            }
            if (_v009Stealth != null && _v009VolatileVisibility != null) return;
            RuntimePatch stealthCreated = null;
            RuntimePatch volatileCreated = null;
            try
            {
                if (_v009Stealth == null)
                {
                    byte[] original = new byte[] { 0x0F, 0xB6, 0x83, 0x28, 0x01, 0x00, 0x00 };
                    stealthCreated = InstallCavePatch(RVA_CE_Stealth, original, delegate(IntPtr cave)
                    {
                        List<byte> c = new List<byte>();
                        // XXXeLxxx 1.55 Stealth [day/night]: write the exact dword used by
                        // the current table, then replay the original byte load.
                        c.AddRange(new byte[] { 0xC7, 0x83, 0x28, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0xB2 });
                        c.AddRange(original);
                        c.AddRange(AbsJump(Add(_dllBase, RVA_CE_Stealth + original.Length).ToInt64()));
                        return c.ToArray();
                    }, 0x1000);
                    _v009Stealth = stealthCreated;
                }

                if (_v009VolatileVisibility == null)
                {
                    byte[] originalVisibility = new byte[] { 0x41, 0x0F, 0x2F, 0x86, 0xC4, 0x18, 0x00, 0x00 };
                    volatileCreated = InstallCavePatch(RVA_CE_VolatileVisibility, originalVisibility, delegate(IntPtr cave)
                    {
                        List<byte> c = new List<byte>();
                        // XXXeLxxx 1.55 Volatile Visibility Bar: [r14+18C4] = 0.0f.
                        // This supplements Stealth for night/volatile perception rather than
                        // pretending the obsolete player+0x10B4 field is a universal switch.
                        c.AddRange(new byte[] { 0x41, 0xC7, 0x86, 0xC4, 0x18, 0x00, 0x00, 0, 0, 0, 0 });
                        c.AddRange(originalVisibility);
                        c.AddRange(AbsJump(Add(_dllBase, RVA_CE_VolatileVisibility + originalVisibility.Length).ToInt64()));
                        return c.ToArray();
                    }, 0x1000);
                    _v009VolatileVisibility = volatileCreated;
                }
            }
            catch
            {
                if (volatileCreated != null) RemovePatch(ref _v009VolatileVisibility);
                if (stealthCreated != null) RemovePatch(ref _v009Stealth);
                throw;
            }
        }

        internal bool IsCompleteStealthEnabled { get { return _v009Stealth != null && _v009VolatileVisibility != null; } }

        internal void SetFreezeChallengeTimer(bool enabled)
        {
            EnsureConnected();
            if (!enabled) { RemovePatch(ref _v009ChallengeTimer); return; }
            if (_v009ChallengeTimer != null) return;
            byte[] original = new byte[] { 0xF3, 0x0F, 0x5C, 0x83, 0xF0, 0x00, 0x00, 0x00 };
            _v009ChallengeTimer = InstallCavePatch(RVA_CE_ChallengeTimer, original, delegate(IntPtr cave)
            {
                List<byte> c = new List<byte>();
                // Current 1.55 CT semantics: clamp the per-update decrement source to 0.01,
                // then execute the game's original subtraction. This targets challenge time
                // only and never calls ILevel::TimerStop (which froze the whole simulation).
                c.AddRange(new byte[] { 0xC7, 0x83, 0xF0, 0x00, 0x00, 0x00, 0x0A, 0xD7, 0x23, 0x3C });
                c.AddRange(original);
                c.AddRange(AbsJump(Add(_dllBase, RVA_CE_ChallengeTimer + original.Length).ToInt64()));
                return c.ToArray();
            }, 0x1000);
        }

        internal void SetFreezeMissionTimer(bool enabled)
        {
            EnsureConnected();
            if (!enabled) { RemovePatch(ref _v009MissionTimer); return; }
            if (_v009MissionTimer != null) return;
            byte[] original = new byte[] { 0xF3, 0x0F, 0x11, 0x40, 0xD8 };
            RuntimePatch created = null;
            created = InstallCavePatch(RVA_CE_MissionTimer, original, delegate(IntPtr cave)
            {
                List<byte> c = new List<byte>();
                // XXXeLxxx 1.55 Freeze Mission Time: load 1.1f into xmm0 then replay
                // movss [rax-28],xmm0 so downstream register semantics match the table.
                c.AddRange(new byte[] { 0xF3, 0x0F, 0x10, 0x05, 0x13, 0x00, 0x00, 0x00 });
                c.AddRange(original);
                c.AddRange(AbsJump(Add(_dllBase, RVA_CE_MissionTimer + original.Length).ToInt64()));
                c.AddRange(BitConverter.GetBytes(1.1f));
                return c.ToArray();
            }, 0x1000);
            created.Factor = Add(created.Cave, 27);
            _v009MissionTimer = created;
        }

        internal void SetNearbyEnemySlaughter(bool enabled)
        {
            EnsureConnected();
            if (!enabled) { RemovePatch(ref _v009Kill); return; }
            if (_v009Kill != null) return;
            byte[] original = new byte[] { 0x0F, 0x2F, 0xB7, 0xC8, 0x00, 0x00, 0x00 };
            _v009Kill = InstallCavePatch(RVA_CE_InstantKillNearby, original, delegate(IntPtr cave)
            {
                List<byte> c = new List<byte>();
                c.AddRange(new byte[] { 0xC7, 0x87, 0xC8, 0x00, 0x00, 0x00, 0, 0, 0, 0 });
                c.AddRange(original);
                c.AddRange(AbsJump(Add(_dllBase, RVA_CE_InstantKillNearby + original.Length).ToInt64()));
                return c.ToArray();
            }, 0x1000);
        }

        internal void SetEasyCraft(bool enabled)
        {
            EnsureConnected();
            if (!enabled)
            {
                if (_v009EasyCraft1Original != null)
                {
                    try
                    {
                        IntPtr a = Add(_dllBase, RVA_CE_EasyCraft1);
                        if (BytesEqual(ReadBytes(a, 5), new byte[] { 0xBB, 0x0A, 0, 0, 0 })) WriteCode(a, _v009EasyCraft1Original);
                    }
                    catch { }
                }
                if (_v009EasyCraft2Original != null)
                {
                    try
                    {
                        IntPtr a = Add(_dllBase, RVA_CE_EasyCraft2);
                        if (BytesEqual(ReadBytes(a, 5), new byte[] { 0xBE, 0x32, 0, 0, 0 })) WriteCode(a, _v009EasyCraft2Original);
                    }
                    catch { }
                }
                RemovePatch(ref _v009EasyCraft3);
                _v009EasyCraft1Original = null; _v009EasyCraft2Original = null; _v009EasyCraft = false;
                return;
            }
            if (_v009EasyCraft) return;
            byte[] o1 = new byte[] { 0x3B, 0xC3, 0x0F, 0x4C, 0xD8 };
            byte[] o2 = new byte[] { 0x3B, 0xC6, 0x0F, 0x4C, 0xF0 };
            byte[] o3 = new byte[] { 0x8B, 0x70, 0x40, 0x41, 0x8B, 0xFF };
            if (!BytesEqual(ReadBytes(Add(_dllBase, RVA_CE_EasyCraft1), o1.Length), o1) ||
                !BytesEqual(ReadBytes(Add(_dllBase, RVA_CE_EasyCraft2), o2.Length), o2))
                throw new InvalidOperationException("Easy Craft 当前 1.55 入口字节不匹配。");
            _v009EasyCraft1Original = o1; _v009EasyCraft2Original = o2;
            try
            {
                WriteCode(Add(_dllBase, RVA_CE_EasyCraft1), new byte[] { 0xBB, 0x0A, 0, 0, 0 });
                WriteCode(Add(_dllBase, RVA_CE_EasyCraft2), new byte[] { 0xBE, 0x32, 0, 0, 0 });
                _v009EasyCraft3 = InstallCavePatch(RVA_CE_EasyCraft3, o3, delegate(IntPtr cave)
                {
                    List<byte> c = new List<byte>();
                    c.AddRange(new byte[] { 0xC7, 0x40, 0x40, 0x32, 0, 0, 0, 0x8B, 0x70, 0x40, 0x41, 0x8B, 0xFF });
                    c.AddRange(AbsJump(Add(_dllBase, RVA_CE_EasyCraft3 + o3.Length).ToInt64()));
                    return c.ToArray();
                }, 0x1000);
                _v009EasyCraft = true;
            }
            catch
            {
                try { WriteCode(Add(_dllBase, RVA_CE_EasyCraft1), o1); } catch { }
                try { WriteCode(Add(_dllBase, RVA_CE_EasyCraft2), o2); } catch { }
                RemovePatch(ref _v009EasyCraft3);
                _v009EasyCraft1Original = null;
                _v009EasyCraft2Original = null;
                _v009EasyCraft = false;
                throw;
            }
        }

        private string ReadAsciiZ(IntPtr address, int max)
        {
            if (address == IntPtr.Zero) return null;
            byte[] raw = TryReadBytes(address, Math.Max(1, max));
            if (raw == null) return null;
            int n = Array.IndexOf(raw, (byte)0);
            if (n < 0) n = raw.Length;
            if (n <= 0) return "";
            for (int i = 0; i < n; i++) if (raw[i] < 0x20 || raw[i] > 0x7E) return null;
            return Encoding.ASCII.GetString(raw, 0, n);
        }

        private string ReadUnicodeZ(IntPtr address, int maxChars)
        {
            if (address == IntPtr.Zero) return null;
            byte[] raw = TryReadBytes(address, Math.Max(2, maxChars * 2));
            if (raw == null) return null;
            int bytes = 0;
            while (bytes + 1 < raw.Length)
            {
                if (raw[bytes] == 0 && raw[bytes + 1] == 0) break;
                bytes += 2;
            }
            if (bytes <= 0) return "";
            try { return Encoding.Unicode.GetString(raw, 0, bytes).Trim(); }
            catch { return null; }
        }

        private void EnsureBaseItemCatalogV010()
        {
            if (_v010BaseItemCatalogComplete) return;
            foreach (IntPtr item in EnumerateBaseItemsV009())
            {
                try
                {
                    string name = ReadAsciiZ(ReadPtr(Add(item, OFF_BaseItemName)), 192);
                    if (!String.IsNullOrWhiteSpace(name) && !_v009BaseItems.ContainsKey(name)) _v009BaseItems[name] = item;
                }
                catch { }
            }
            // A normal loaded save exposes far more than a handful of BaseItems. If the manager
            // was queried too early during loading, leave the cache incomplete so a later refresh retries.
            _v010BaseItemCatalogComplete = _v009BaseItems.Count >= 100;
        }

        internal Dictionary<string, List<CollectibleItem>> DiscoverSpecialCollectiblesV010()
        {
            EnsureConnected();
            EnsureBaseItemCatalogV010();
            Dictionary<string, List<CollectibleItem>> result = new Dictionary<string, List<CollectibleItem>>(StringComparer.OrdinalIgnoreCase);
            result["following_missing_persons"] = new List<CollectibleItem>();
            result["following_strange_rocks"] = new List<CollectibleItem>();
            HashSet<int> missingIds = new HashSet<int>();
            HashSet<int> rockIds = new HashSet<int>();

            foreach (KeyValuePair<string, IntPtr> pair in _v009BaseItems)
            {
                string internalId = pair.Key;
                IntPtr item = pair.Value;
                if (String.IsNullOrWhiteSpace(internalId) || item == IntPtr.Zero) continue;
                string norm = internalId.Replace("_", "").Replace("-", "").Replace(" ", "").ToLowerInvariant();

                bool missing = norm.Contains("missingperson") || norm.Contains("missingpeople") || norm.Contains("missingid") ||
                    (norm.Contains("missing") && (norm.Contains("dogtag") || norm.Contains("idcard") || norm.Contains("identity"))) ||
                    (norm.Contains("following") && (norm.Contains("dogtag") || norm.Contains("idcard")));
                bool rock = norm.Contains("strangerock") || norm.Contains("unknownrock") || norm.Contains("alienrock") ||
                    norm.Contains("strangestone") || norm.Contains("alienstone") || norm.Contains("collectablerock") ||
                    (norm.Contains("strange") && (norm.Contains("rock") || norm.Contains("stone")));
                if (!missing && !rock) continue;

                int index = -1;
                string localized = null;
                try
                {
                    index = ReadI32(Add(item, OFF_BaseItemIndex));
                    IntPtr localizedPtr = ReadPtr(Add(item, OFF_BaseItemLocalizedName));
                    localized = ReadUnicodeZ(localizedPtr, 160);
                }
                catch { continue; }
                if (index <= 0) continue;
                // IMIG documents BaseItem+0x58 as a memory index. It is not automatically safe to
                // feed that number into the Collectable backend. Only keep the candidate if the
                // game's own FindCollectableObject resolves this exact numeric ID.
                try { if (FindCollectableObjectV010(index) == IntPtr.Zero) continue; }
                catch { continue; }
                string display = String.IsNullOrWhiteSpace(localized) ? internalId : (localized + " · " + internalId);
                if (missing && missingIds.Add(index)) result["following_missing_persons"].Add(new CollectibleItem { id = index, name = display });
                if (rock && rockIds.Add(index)) result["following_strange_rocks"].Add(new CollectibleItem { id = index, name = display });
            }

            foreach (List<CollectibleItem> list in result.Values) list.Sort(delegate(CollectibleItem a, CollectibleItem b) { return a.id.CompareTo(b.id); });
            return result;
        }

        private IEnumerable<IntPtr> EnumerateBaseItemsV009()
        {
            IntPtr manager = Add(_dllBase, RVA_BaseItemManagerObject);
            IntPtr tree = IntPtr.Zero;
            try { tree = ReadPtr(Add(manager, OFF_BaseItemTree)); } catch { }
            if (tree == IntPtr.Zero) yield break;
            HashSet<long> groups = new HashSet<long>();
            HashSet<long> items = new HashSet<long>();
            for (int groupIndex = 0; groupIndex < 70; groupIndex++)
            {
                int key = groupIndex - 2;
                IntPtr node = tree;
                IntPtr candidate = IntPtr.Zero;
                int guard = 0;
                while (node != IntPtr.Zero && guard++ < 512)
                {
                    int nodeKey;
                    try { nodeKey = ReadI32(Add(node, -0x20)); } catch { break; }
                    if (nodeKey < key)
                    {
                        try { node = ReadPtr(Add(node, 0x10)); } catch { node = IntPtr.Zero; }
                    }
                    else
                    {
                        candidate = node;
                        try { node = ReadPtr(node); } catch { node = IntPtr.Zero; }
                    }
                }
                if (candidate == IntPtr.Zero) continue;
                int finalKey;
                try { finalKey = ReadI32(Add(candidate, -0x20)); } catch { continue; }
                if (finalKey < key) continue;
                long groupAddr = candidate.ToInt64() - 0x18L;
                if (!groups.Add(groupAddr)) continue;
                IntPtr group = new IntPtr(groupAddr);
                IntPtr array;
                int count;
                try { array = ReadPtr(group); count = ReadI32(Add(group, 8)); } catch { continue; }
                if (array == IntPtr.Zero || count <= 0 || count > 20000) continue;
                for (int j = 0; j < count; j++)
                {
                    IntPtr item;
                    try { item = ReadPtr(Add(array, j * 8L)); } catch { continue; }
                    if (item == IntPtr.Zero || !items.Add(item.ToInt64())) continue;
                    yield return item;
                }
            }
        }

        private IntPtr ResolveBaseItemV009(string internalId)
        {
            if (String.IsNullOrWhiteSpace(internalId)) return IntPtr.Zero;
            IntPtr cached;
            if (_v009BaseItems.TryGetValue(internalId, out cached) && cached != IntPtr.Zero) return cached;
            if (_v010BaseItemCatalogComplete) return IntPtr.Zero;
            foreach (IntPtr item in EnumerateBaseItemsV009())
            {
                IntPtr namePtr;
                try { namePtr = ReadPtr(Add(item, OFF_BaseItemName)); } catch { continue; }
                string name = ReadAsciiZ(namePtr, 160);
                if (String.IsNullOrEmpty(name)) continue;
                if (!_v009BaseItems.ContainsKey(name)) _v009BaseItems[name] = item;
                if (String.Equals(name, internalId, StringComparison.OrdinalIgnoreCase)) return item;
            }
            return IntPtr.Zero;
        }

        internal List<string> GetBlueprintInternalIds()
        {
            EnsureConnected();
            EnsureBaseItemCatalogV010();
            List<string> result = new List<string>();
            foreach (string name in _v009BaseItems.Keys)
                if (!String.IsNullOrEmpty(name) && name.StartsWith("Craftplan_", StringComparison.OrdinalIgnoreCase)) result.Add(name);
            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }

        // Native ABI proven by the exact 1.55 disassembly:
        //   B_GiveWeapon -> IPlayerCheat::AddItem(+0x30)
        //   PlayerDI+0x8F8 vtable +0x30 = gamedll+0xBAE930
        //   InventoryOwner+0x68 = gamedll+0xCD29D0
        //   and the latter reads [RDX+8] as *length*, [RDX] as char*.
        // Therefore RDX is a non-owning { char* buffer, uint32 length } Name/ID view,
        // NOT the BaseItem instance. Passing a BaseItem was the v0.1.0 regression.
        private long NativeGiveItemIdV011(string internalId, int count)
        {
            EnsureConnected();
            if (String.IsNullOrWhiteSpace(internalId) || internalId.Length > 180 ||
                System.Text.RegularExpressions.Regex.IsMatch(internalId, "[^A-Za-z0-9_\\-]"))
                throw new InvalidOperationException("无效的游戏物品内部名：" + internalId);
            if (count < 1 || count > 100000) throw new InvalidOperationException("物品数量超出安全范围。");
            IntPtr definition = ResolveBaseItemV009(internalId);
            if (definition == IntPtr.Zero)
                throw new InvalidOperationException("当前 BaseItem 目录没有此物品 ID：" + internalId);
            // Buffer layout at remote+0x00: pointer to ASCII (remote+0x20), length at +0x08.
            byte[] name = Encoding.ASCII.GetBytes(internalId + "\0");
            IntPtr remote = Native.VirtualAllocEx(_handle, IntPtr.Zero, new UIntPtr((uint)(0x20 + name.Length)),
                MEM_COMMIT_RESERVE, PAGE_EXECUTE_READWRITE);
            if (remote == IntPtr.Zero) throw Win32("VirtualAllocEx ItemId");
            bool safeFreeItemId = true;
            try
            {
                byte[] blob = new byte[0x20 + name.Length];
                Buffer.BlockCopy(BitConverter.GetBytes(remote.ToInt64() + 0x20), 0, blob, 0, 8);
                Buffer.BlockCopy(BitConverter.GetBytes(internalId.Length), 0, blob, 8, 4);
                Buffer.BlockCopy(name, 0, blob, 0x20, name.Length);
                WriteBytes(remote, blob);
                // Native wrapper hardcodes count=1; for count>1 call the same verified
                // player IPlayerCheat::AddItem slot (R8D=count) with the SAME ItemId view.
                if (count == 1)
                    return InvokeCallReturnRax(Add(_dllBase, RVA_DebugGiveWeapon), IntPtr.Zero, remote, 1);
                IntPtr cheat = Add(GetPlayerObject(), OFF_PlayerCheatInterface);
                IntPtr vtable = ReadPtr(cheat);
                IntPtr addItem = ReadPtr(Add(vtable, 0x30));
                long slotRva = addItem.ToInt64() - _dllBase.ToInt64();
                if (slotRva != 0xBAE930L)
                    throw new InvalidOperationException("IPlayerCheat AddItem vtable ABI 不一致；RVA=0x" + slotRva.ToString("X"));
                return InvokeCallReturnRax(addItem, cheat, remote, count);
            }
            catch (InvalidOperationException ex)
            {
                // InvokeCallReturnRax deliberately preserves its remote code on timeout.
                // The still-running native thread may also hold this NameId pointer.
                if (ex.Message.IndexOf("超时", StringComparison.OrdinalIgnoreCase) >= 0)
                    safeFreeItemId = false;
                throw;
            }
            finally
            {
                if (safeFreeItemId) Native.VirtualFreeEx(_handle, remote, UIntPtr.Zero, MEM_RELEASE);
            }
        }

        private string GiveBaseItemV009(string internalId, int count)
        {
            long receipt = NativeGiveItemIdV011(internalId, count);
            if (receipt == 0)
                throw new InvalidOperationException("游戏库存 AddItem 返回 0；没有确认物品已进入背包：" + internalId);
            return internalId + " ×" + count + "，库存函数返回 0x" + receipt.ToString("X") +
                "。已使用 NameId{char*,len} ABI；请在背包中实际核对新增条目。";
        }

        internal string GiveWeapon(string internalId)
        {
            long receipt = NativeGiveItemIdV011(internalId, 1);
            if (receipt == 0) throw new InvalidOperationException("AddItem 返回 0，未能添加武器：" + internalId);
            bool orange = TryMakeGivenWeaponOrangeV012(receipt, internalId);
            return internalId + "；AddItem 返回 0x" + receipt.ToString("X") +
                (orange ? "；新物品品质 4 / 橙色已写入并读回。" :
                 "；返回值不是可验证的背包实例，未强行写入品质。请打开背包/库存页面，刷新后将新物品设为橙色。");
        }

        internal string AddHellraidCoins(int amount)
        {
            return GiveBaseItemV009("cash_hellraid_coin", Math.Max(1, Math.Min(100000, amount)));
        }

        internal int UnlockBlueprintsV009(IEnumerable<string> ids, Action<string> log)
        {
            // This is an inventory item API, not the persistent learned-recipe API.
            // Do not report 'all blueprints unlocked' based on a native call return.
            throw new InvalidOperationException("当前游戏的已学 CraftPlan 写入 ABI 尚未确认，禁止批量向库存发送 Craftplan_*（这不等于学习蓝图）。请使用蓝图进度 A/B 诊断。 ");
        }

        private void QueueBountyForNativeEvaluationV009(IntPtr manager, IntPtr bounty)
        {
            IntPtr array = ReadPtr(Add(manager, OFF_ManagerEvalArray));
            int count = ReadI32(Add(manager, OFF_ManagerEvalCount));
            int capacity = ReadI32(Add(manager, OFF_ManagerEvalCapacity));
            if (array == IntPtr.Zero || count < 0 || capacity <= 0 || count >= capacity)
                throw new InvalidOperationException("BountyManager 原生评估队列当前不可安全追加（count=" + count + ", capacity=" + capacity + "）。");
            for (int i = 0; i < count; i++)
            {
                IntPtr q = ReadPtr(Add(array, i * 8L));
                if (q == bounty)
                {
                    // Native queue-membership helper stores this byte on the bounty when
                    // the object belongs to manager+0x6F0. Keep our manual enqueue coherent.
                    try { WriteBytes(Add(bounty, 0x31D), new byte[] { 1 }); } catch { }
                    return;
                }
            }
            WriteBytes(Add(array, count * 8L), BitConverter.GetBytes(bounty.ToInt64()));
            WriteI32(Add(manager, OFF_ManagerEvalCount), count + 1);
            try { WriteBytes(Add(bounty, 0x31D), new byte[] { 1 }); } catch { }
        }

        internal int CompleteBountyStaged(int id, IntPtr selectedPtr, Action<string> log)
        {
            EnsureConnected();
            IntPtr manager = EnsureBountyManager();
            BountyInfo b = FindBountyTarget(id, selectedPtr, 2);
            if (b.State != 2) throw new InvalidOperationException("只有进行中的悬赏可以完成；当前：" + b.StatusLabel + "。");
            List<BountyProgressInfo> progress = ReadBountyProgress(b.Ptr);
            List<Tuple<IntPtr, int>> backup = new List<Tuple<IntPtr, int>>();
            foreach (BountyProgressInfo p in progress)
            {
                if (p.Type == 4 || p.Remaining <= 0) continue;
                backup.Add(Tuple.Create(p.Ptr, p.Remaining));
            }
            if (backup.Count == 0) throw new InvalidOperationException("没有可推进的普通目标。");
            try
            {
                foreach (Tuple<IntPtr, int> x in backup)
                {
                    WriteI32(Add(x.Item1, OFF_ProgressRemaining), 0);
                    if (log != null) log("目标清零：0x" + x.Item1.ToInt64().ToString("X") + " remaining " + x.Item2 + " → 0");
                }
                QueueBountyForNativeEvaluationV009(manager, b.Ptr);
                if (log != null) log("已把悬赏加入 BountyManager+0x6F0 原生评估队列；由游戏线程完成 Evaluate/结算状态流。");
                for (int i = 0; i < 14; i++)
                {
                    Thread.Sleep(120);
                    BountyInfo now = TryFindExactBounty(id, b.Ptr) ?? FindUniqueReplacementBounty(id, 3) ?? FindUniqueReplacementBounty(id, 4) ?? FindUniqueReplacementBounty(id, 1);
                    if (now == null) continue;
                    if (now.State == 3 || now.State == 4 || now.State == 1)
                    {
                        if (log != null) log("游戏线程已完成本轮悬赏状态流：state=" + now.State + "（" + now.StatusLabel + "）。");
                        return backup.Count;
                    }
                }
                BountyInfo settled = TryFindExactBounty(id, b.Ptr);
                if (settled != null && settled.State == 2)
                    throw new InvalidOperationException("目标已清零并送入游戏线程评估队列，但 1.7 秒后仍为 state=2；将恢复本工具改动的目标值。");
                return backup.Count;
            }
            catch
            {
                foreach (Tuple<IntPtr, int> x in backup)
                {
                    try
                    {
                        int cur = ReadI32(Add(x.Item1, OFF_ProgressRemaining));
                        if (cur == 0) WriteI32(Add(x.Item1, OFF_ProgressRemaining), x.Item2);
                    }
                    catch { }
                }
                throw;
            }
        }

        private void RestoreV009Features()
        {
            try { RestoreInventoryV012(); } catch { }
            RemovePatch(ref _v011WalkSpeed);
            RemovePatch(ref _v009Run);
            RestoreSprintOperandV012();
            RemovePatch(ref _v009Jump);
            RemovePatch(ref _v009VolatileVisibility);
            RemovePatch(ref _v009Stealth);
            RemovePatch(ref _v009ChallengeTimer);
            RemovePatch(ref _v009MissionTimer);
            RemovePatch(ref _v009Kill);
            try { SetEasyCraft(false); } catch { }
            _v009BaseItems.Clear();
            _v010BaseItemCatalogComplete = false;
        }
    }
}
