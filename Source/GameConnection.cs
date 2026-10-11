using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Text;

namespace DL1Assistant
{
    internal static partial class Native
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ReadProcessMemory(IntPtr process, IntPtr address, [Out] byte[] buffer, int size, out IntPtr read);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WriteProcessMemory(IntPtr process, IntPtr address, byte[] buffer, int size, out IntPtr written);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address, UIntPtr size, uint allocType, uint protect);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool VirtualFreeEx(IntPtr process, IntPtr address, UIntPtr size, uint freeType);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr CreateRemoteThread(IntPtr process, IntPtr attrs, UIntPtr stackSize, IntPtr start, IntPtr parameter, uint flags, out uint threadId);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    }

    internal sealed partial class GameClient : IDisposable
    {
        internal const string ExpectedHash = "76F20D27C45C0B807D2F099022AD0ABC42CA90A8ADE7F761641518AF346D26A0";

        private const long OFF_GameRootGlobal = 0x1C16348L;
        private const long OFF_RootToBountyMgr = 0xA30L;
        private const long OFF_ManagerArray = 0x2C8L;
        private const long OFF_ManagerCount = 0x2D0L;
        private const long OFF_ManagerCurrent = 0x6D0L;
        // Exact 1.55 BountyManager reevaluation queue. Manager::Update iterates this
        // vector on the game thread and calls EvaluateBounty for each queued bounty.
        private const long OFF_ManagerEvalArray = 0x6F0L;
        private const long OFF_ManagerEvalCount = 0x6F8L;
        private const long OFF_ManagerEvalCapacity = 0x6FCL;
        private const long OFF_BountyFlags = 0x30L;
        private const long OFF_BountyId = 0xC4L;
        private const long OFF_ProgressArray = 0x1F0L;
        private const long OFF_ProgressCount = 0x1F8L;
        private const long OFF_BountyKind = 0x318L;
        private const long OFF_BountyState = 0x324L;
        private const long OFF_ProgressType = 0x68L;
        private const long OFF_ProgressTotal = 0x6CL;
        private const long OFF_ProgressOwner = 0x60L;
        private const long OFF_ProgressRemaining = 0x70L;
        private const long OFF_ProgressInterval = 0x78L;
        private const long OFF_ProgressFlag82 = 0x82L;
        private const long OFF_ProgressFlag83 = 0x83L;
        private const long SIZE_Progress = 0xF8L;

        private const long RVA_ActivateBounty = 0xF49CC0L;
        private const long RVA_EvaluateBounty = 0xF61C40L;
        private const long RVA_SetActiveBounty = 0xF499E0L;
        private const long RVA_AddBountyStateEvent = 0xF4DD10L;
        private const long RVA_RemoveBountyFromStateLists = 0xF4DE70L;
        // Native objective progress updater for this exact 1.55 build.
        // RCX = BountyProgress record, EDX = amount to consume. Unlike a raw write
        // to +0x70, this function runs the game's own progress callbacks and completion chain.
        private const long RVA_UpdateBountyProgress = 0xF5B670L;

        // Current-build hidden MenuCollectableCheat path. These two helpers were already
        // verified against this exact SHA in the earlier diagnostic build: Find by ID,
        // then award through the game's own Collectable handler.
        private const long RVA_FindCollectableObject = 0x6F9E40L;
        private const long RVA_AwardCollectable = 0x834170L;

        // Current Steam 1.55 player chain, recovered from this exact DLL.
        private const long OFF_RootPlayerBranch = 0x540L;
        private const long OFF_PlayerBranch2 = 0xB8L;
        private const long OFF_PlayerGuard = 0x50L;
        private const long OFF_PlayerObject = 0x58L;
        private const long OFF_PlayerCheatInterface = 0x8F8L;
        // RTTI on PlayerDI proves +0x8F8 is IPlayerCheat, not the general inventory owner.
        // IInventoryOwner / IInventoryObserver is the +0x930 base subobject.
        private const long OFF_PlayerInventoryOwnerInterface = 0x930L;
        private const long OFF_HumanInvisibleToEnemies = 0x10B4L;
        private const long OFF_PlayerCollectableContainer = 0x938L;
        private const long OFF_CollectableList = 0x50L;
        private const long OFF_CollectableArray = 0x40L;
        private const long OFF_CollectableCount = 0x48L;

        // Current movement parameter system.
        private const long OFF_GlobalParameterHolder = 0x1C122C8L;
        private const long OFF_ParameterHolderToTable = 0xD0L;
        private const long OFF_PlayerToParameterHolder = 0xE58L;
        private const int PARAM_MoveStrafeMaxSpeed = 0x26F;
        private const int PARAM_MoveForwardMinSpeed = 0x270;
        private const int PARAM_MoveSprintSpeed = 0x271;
        private const int PARAM_MoveOverSpeedMaxSpeed = 0x274;
        private const int PARAM_MoveSprintAcceleration = 0x288;
        // Exact 1.55 parameter-name table:
        // 0x27F JumpMinHeightNoFitnessFactor, 0x280 JumpMinHeight,
        // 0x282 JumpMaxHeightNoFitnessFactor.
        private const int PARAM_JumpMinHeightNoFitnessFactor = 0x27F;
        private const int PARAM_JumpMinHeight = 0x280;
        private const int PARAM_JumpMaxHeightNoFitnessFactor = 0x282;
        private const int PARAM_MovementSpeedMul = 0x29B;
        private const int PARAM_JumpMaxHeight = 0x29D;

        private const long RVA_DebugGiveWeapon = 0x8325F0L;

        // The engine import exists at this IAT slot, but static inspection of the current
        // engine_x64_rwdi.dll shows the exported IGame base implementation is just
        // `xor eax,eax; ret`.  v0.0.8 therefore DOES NOT call it from a remote thread.
        private const long RVA_IAT_GetIQuestManager = 0x137E110L;
        // Primary Quests::QuestManager vtable in this exact gamedll image.
        private const long RVA_QuestManagerPrimaryVtable = 0x173D400L;
        // Static analysis of the exact 1.55 constructor at gamedll+0xE005E0 proves that
        // QuestManager is a function-local static stored inline at gamedll+0x1D1C790.
        // gamedll+0xE00560 is the compiler-generated singleton getter returning this
        // address.  We do not need to call it: the diagnostic path can verify the
        // object's primary vtable and read it directly.
        private const long RVA_QuestManagerSingleton = 0x1D1C790L;

        // FLiNG Super Run entry patches captured from the user's exact 1.55 runtime.
        // v0.0.8 always reports the live bytes at these sites even if both A/B samples
        // happen to be taken with the trainer in the same state.
        private const long RVA_FlingRunEngineHook = 0x3878D9L;
        private const long RVA_FlingRunGameHook = 0xB812DFL;

        // Current 1.55 build: AddBountyStateEvent(manager, bounty, event).
        // The third argument is an EVENT ID, not a target state number.
        // Disassembly of gamedll+0xF4DD10 proves the mapping below:
        //   event 2 -> state 1 (available / not accepted)
        //   event 3 -> state 2 (active)
        //   event 5 -> state 3 (completed / pending settlement)
        //   event 6 -> state 4 (settled)
        private const int BOUNTY_EVENT_AVAILABLE = 2;
        private const int BOUNTY_EVENT_ACTIVE = 3;

        private const uint PROCESS_ACCESS = 0x0000043A;
        private const uint MEM_COMMIT_RESERVE = 0x3000;
        private const uint MEM_RELEASE = 0x8000;
        private const uint PAGE_EXECUTE_READWRITE = 0x40;
        private const uint WAIT_TIMEOUT_MS = 8000;

        private IntPtr _handle = IntPtr.Zero;
        private Process _process;
        private IntPtr _dllBase = IntPtr.Zero;
        private IntPtr _root = IntPtr.Zero;
        private IntPtr _bountyManager = IntPtr.Zero;
        private string _dllPath = "";
        private int _dllSize;
        private sealed class ProbeModuleSnapshot
        {
            internal string Name;
            internal string Path;
            internal long Base;
            internal int TextRva;
            internal byte[] Text;
        }

        private readonly Dictionary<int, BountyCatalogItem> _bountyCatalog;
        private readonly Dictionary<string, byte[]> _featureProbePlayer = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Dictionary<int, float>> _featureProbeParams = new Dictionary<string, Dictionary<int, float>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Dictionary<string, ProbeModuleSnapshot>> _featureProbeModules = new Dictionary<string, Dictionary<string, ProbeModuleSnapshot>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Dictionary<long, byte[]>> _featureProbeInventory = new Dictionary<string, Dictionary<long, byte[]>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Dictionary<long, byte[]>> _featureProbePlayerGraph = new Dictionary<string, Dictionary<long, byte[]>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Dictionary<long, byte[]>> _bountyProbeGraphs = new Dictionary<string, Dictionary<long, byte[]>>(StringComparer.OrdinalIgnoreCase);

        private readonly object _featureSync = new object();
        private System.Threading.Timer _featureKeeper;
        private volatile bool _disconnecting;
        private int _featureKeeperBusy;
        private bool _wantInvisibleToEnemies;
        private IntPtr _lastInvisibleAddress = IntPtr.Zero;
        private byte[] _lastInvisibleOriginal;
        private int _runSpeedFactor = 1;
        private int _jumpHeightFactor = 1;
        private readonly Dictionary<long, float> _movementOriginals = new Dictionary<long, float>();
        private readonly Dictionary<int, HashSet<long>> _movementAddresses = new Dictionary<int, HashSet<long>>();

        internal GameClient(Dictionary<int, BountyCatalogItem> catalog)
        {
            _bountyCatalog = catalog ?? new Dictionary<int, BountyCatalogItem>();
        }

        internal bool IsConnected { get { return _handle != IntPtr.Zero && _process != null && !_process.HasExited; } }
        internal int ProcessId { get { return _process == null ? 0 : _process.Id; } }
        internal string DllPath { get { return _dllPath; } }
        internal IntPtr DllBase { get { return _dllBase; } }
        internal IntPtr BountyManager { get { return _bountyManager; } }

        private static IntPtr Add(IntPtr p, long off) { return new IntPtr(p.ToInt64() + off); }

        private static string HashFile(string path)
        {
            using (FileStream fs = File.OpenRead(path))
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToUpperInvariant();
        }

        private static Exception Win32(string what)
        {
            return new InvalidOperationException(what + " failed. Win32=" + Marshal.GetLastWin32Error());
        }

        internal void Connect()
        {
            Disconnect();
            if (!Environment.Is64BitProcess)
                throw new InvalidOperationException("任务助手必须由 64 位 Windows PowerShell 启动。请使用 StartAssistant.bat。");

            Process p = Process.GetProcessesByName("DyingLightGame").FirstOrDefault();
            if (p == null) throw new InvalidOperationException("没有找到 DyingLightGame.exe。请先进入实际游戏存档。");

            ProcessModule module = null;
            try
            {
                foreach (ProcessModule m in p.Modules)
                    if (String.Equals(m.ModuleName, "gamedll_x64_rwdi.dll", StringComparison.OrdinalIgnoreCase)) { module = m; break; }
            }
            catch { throw new InvalidOperationException("无法枚举游戏模块。请尝试以管理员身份运行任务助手。"); }
            if (module == null) throw new InvalidOperationException("gamedll_x64_rwdi.dll 尚未加载。请完全进入存档后再连接。");

            string hash = HashFile(module.FileName);
            if (!String.Equals(hash, ExpectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("gamedll 版本不匹配，已拒绝写内存。\r\n当前：" + hash + "\r\n目标：" + ExpectedHash);

            IntPtr h = Native.OpenProcess(PROCESS_ACCESS, false, p.Id);
            if (h == IntPtr.Zero) throw Win32("OpenProcess");
            _handle = h;
            _process = p;
            _dllBase = module.BaseAddress;
            _dllPath = module.FileName;
            _dllSize = module.ModuleMemorySize;
            _root = ReadPtr(Add(_dllBase, OFF_GameRootGlobal));
            if (_root == IntPtr.Zero) { Disconnect(); throw new InvalidOperationException("游戏根对象尚未初始化。请完全进入存档后再连接。"); }
            _bountyManager = ReadPtr(Add(_root, OFF_RootToBountyMgr));
            _disconnecting = false;
            StartFeatureKeeper();
        }

        internal void Disconnect()
        {
            _disconnecting = true;
            StopFeatureKeeper();
            try { RestoreGameplayFeatures(); } catch { }
            if (_handle != IntPtr.Zero) Native.CloseHandle(_handle);
            _handle = IntPtr.Zero;
            _process = null;
            _dllBase = IntPtr.Zero;
            _root = IntPtr.Zero;
            _bountyManager = IntPtr.Zero;
            _dllPath = "";
            _dllSize = 0;
            lock (_featureSync)
            {
                _featureProbePlayer.Clear();
                _featureProbeParams.Clear();
                _featureProbeModules.Clear();
                _featureProbeInventory.Clear();
                _featureProbePlayerGraph.Clear();
                _bountyProbeGraphs.Clear();
            }
        }

        private void StartFeatureKeeper()
        {
            lock (_featureSync)
            {
                if (_featureKeeper != null) { try { _featureKeeper.Dispose(); } catch { } }
                _featureKeeper = new System.Threading.Timer(FeatureKeeperTick, null, 750, 750);
            }
        }

        private void StopFeatureKeeper()
        {
            System.Threading.Timer timer = null;
            lock (_featureSync) { timer = _featureKeeper; _featureKeeper = null; }
            if (timer != null)
            {
                try
                {
                    using (ManualResetEvent done = new ManualResetEvent(false))
                    {
                        if (timer.Dispose(done)) done.WaitOne(1500);
                    }
                }
                catch { try { timer.Dispose(); } catch { } }
            }
        }

        private void FeatureKeeperTick(object state)
        {
            if (_disconnecting || !IsConnected) return;
            if (Interlocked.Exchange(ref _featureKeeperBusy, 1) != 0) return;
            try
            {
                int run, jump;
                lock (_featureSync) { run = _runSpeedFactor; jump = _jumpHeightFactor; }
                // Re-resolve current global/player parameter instances after map/state rebuilds.
                // This worker never touches WinForms and is non-reentrant, avoiding resize/UI stalls.
                // Only active options touch game memory; no WinForms redraw or UI timer.
                if (run > 1)
                {
                    UpdateSprintOperandV012(run);
                    ApplyMovementFactor(PARAM_MoveStrafeMaxSpeed, run);
                    ApplyMovementFactor(PARAM_MoveForwardMinSpeed, run);
                    ApplyMovementFactor(PARAM_MoveSprintSpeed, run);
                    ApplyMovementFactor(PARAM_MoveOverSpeedMaxSpeed, run);
                    ApplyMovementFactor(PARAM_MoveSprintAcceleration, run);
                    ApplyMovementFactor(PARAM_MovementSpeedMul, run);
                }
                if (jump > 1)
                {
                    ApplyMovementFactor(PARAM_JumpMinHeightNoFitnessFactor, jump);
                    ApplyMovementFactor(PARAM_JumpMinHeight, jump);
                    ApplyMovementFactor(PARAM_JumpMaxHeightNoFitnessFactor, jump);
                    ApplyMovementFactor(PARAM_JumpMaxHeight, jump);
                }
            }
            catch { }
            finally { Interlocked.Exchange(ref _featureKeeperBusy, 0); }
        }

        private void RestoreGameplayFeatures()
        {
            if (_handle == IntPtr.Zero) return;
            try { RestoreV009Features(); } catch { }
            if (_lastInvisibleAddress != IntPtr.Zero && _lastInvisibleOriginal != null)
            {
                try { WriteBytes(_lastInvisibleAddress, _lastInvisibleOriginal); } catch { }
            }
            _lastInvisibleAddress = IntPtr.Zero;
            _lastInvisibleOriginal = null;
            _wantInvisibleToEnemies = false;

            KeyValuePair<long, float>[] originals;
            lock (_featureSync)
            {
                originals = _movementOriginals.ToArray();
                _movementOriginals.Clear();
                _movementAddresses.Clear();
                _runSpeedFactor = 1;
                _jumpHeightFactor = 1;
            }
            foreach (KeyValuePair<long, float> pair in originals)
            {
                try { WriteBytes(new IntPtr(pair.Key), BitConverter.GetBytes(pair.Value)); } catch { }
            }
        }

        private void EnsureConnected()
        {
            if (!IsConnected) throw new InvalidOperationException("尚未连接游戏。");
        }

        private IntPtr EnsureBountyManager()
        {
            EnsureConnected();
            _root = ReadPtr(Add(_dllBase, OFF_GameRootGlobal));
            if (_root == IntPtr.Zero) throw new InvalidOperationException("游戏根对象尚未初始化。");
            _bountyManager = ReadPtr(Add(_root, OFF_RootToBountyMgr));
            if (_bountyManager == IntPtr.Zero)
                throw new InvalidOperationException("当前场景的悬赏管理器尚未初始化。请打开一次任务/悬赏界面，或进入支持悬赏的地图后再刷新。");
            return _bountyManager;
        }

        private byte[] ReadBytes(IntPtr address, int size)
        {
            EnsureConnected();
            byte[] buf = new byte[size];
            IntPtr read;
            if (!Native.ReadProcessMemory(_handle, address, buf, size, out read)) throw Win32("ReadProcessMemory 0x" + address.ToInt64().ToString("X"));
            if (read.ToInt64() != size) throw new InvalidOperationException("读取长度异常：" + read.ToInt64() + "/" + size);
            return buf;
        }

        private int ReadI32(IntPtr address) { return BitConverter.ToInt32(ReadBytes(address, 4), 0); }
        private uint ReadU32(IntPtr address) { return BitConverter.ToUInt32(ReadBytes(address, 4), 0); }
        private float ReadFloat(IntPtr address) { return BitConverter.ToSingle(ReadBytes(address, 4), 0); }
        private IntPtr ReadPtr(IntPtr address) { return new IntPtr(BitConverter.ToInt64(ReadBytes(address, 8), 0)); }

        private void WriteI32(IntPtr address, int value) { WriteBytes(address, BitConverter.GetBytes(value)); }
        private void WriteFloat(IntPtr address, float value) { WriteBytes(address, BitConverter.GetBytes(value)); }

        private void WriteBytes(IntPtr address, byte[] data)
        {
            IntPtr written;
            if (!Native.WriteProcessMemory(_handle, address, data, data.Length, out written)) throw Win32("WriteProcessMemory 0x" + address.ToInt64().ToString("X"));
            if (written.ToInt64() != data.Length) throw new InvalidOperationException("写入长度异常。");
        }

        private static void AddBytes(List<byte> dst, params byte[] data) { dst.AddRange(data); }
        private static void AddI64(List<byte> dst, long v) { dst.AddRange(BitConverter.GetBytes(v)); }
        private static void AddI32(List<byte> dst, int v) { dst.AddRange(BitConverter.GetBytes(v)); }

        private void InvokeRemote(byte[] code)
        {
            EnsureConnected();
            IntPtr remote = Native.VirtualAllocEx(_handle, IntPtr.Zero, new UIntPtr((uint)code.Length), MEM_COMMIT_RESERVE, PAGE_EXECUTE_READWRITE);
            if (remote == IntPtr.Zero) throw Win32("VirtualAllocEx");
            bool safeFree = true;
            IntPtr thread = IntPtr.Zero;
            try
            {
                WriteBytes(remote, code);
                uint tid;
                thread = Native.CreateRemoteThread(_handle, IntPtr.Zero, UIntPtr.Zero, remote, IntPtr.Zero, 0, out tid);
                if (thread == IntPtr.Zero) throw Win32("CreateRemoteThread");
                safeFree = false;
                uint wait = Native.WaitForSingleObject(thread, WAIT_TIMEOUT_MS);
                if (wait == 0) safeFree = true;
                else if (wait == 0x102) throw new InvalidOperationException("游戏原生函数执行超时。已停止后续操作。");
                else throw new InvalidOperationException("等待游戏原生函数失败：0x" + wait.ToString("X8"));
            }
            finally
            {
                if (thread != IntPtr.Zero) Native.CloseHandle(thread);
                if (safeFree) Native.VirtualFreeEx(_handle, remote, UIntPtr.Zero, MEM_RELEASE);
            }
        }

        private void InvokeThis(IntPtr fn, IntPtr self)
        {
            List<byte> c = new List<byte>();
            AddBytes(c, 0x48, 0x83, 0xEC, 0x28);
            AddBytes(c, 0x48, 0xB9); AddI64(c, self.ToInt64());
            AddBytes(c, 0x48, 0xB8); AddI64(c, fn.ToInt64());
            AddBytes(c, 0xFF, 0xD0);
            AddBytes(c, 0x48, 0x83, 0xC4, 0x28, 0x33, 0xC0, 0xC3);
            InvokeRemote(c.ToArray());
        }

        private void InvokeThisInt(IntPtr fn, IntPtr self, int value)
        {
            List<byte> c = new List<byte>();
            AddBytes(c, 0x48, 0x83, 0xEC, 0x28);
            AddBytes(c, 0x48, 0xB9); AddI64(c, self.ToInt64());
            AddBytes(c, 0xBA); AddI32(c, value);
            AddBytes(c, 0x48, 0xB8); AddI64(c, fn.ToInt64());
            AddBytes(c, 0xFF, 0xD0);
            AddBytes(c, 0x48, 0x83, 0xC4, 0x28, 0x33, 0xC0, 0xC3);
            InvokeRemote(c.ToArray());
        }

        private void InvokeManagerId(IntPtr fn, IntPtr manager, int id)
        {
            List<byte> c = new List<byte>();
            AddBytes(c, 0x48, 0x83, 0xEC, 0x28);
            AddBytes(c, 0x48, 0xB9); AddI64(c, manager.ToInt64());
            AddBytes(c, 0xBA); AddI32(c, id);
            AddBytes(c, 0x48, 0xB8); AddI64(c, fn.ToInt64());
            AddBytes(c, 0xFF, 0xD0);
            AddBytes(c, 0x48, 0x83, 0xC4, 0x28, 0x33, 0xC0, 0xC3);
            InvokeRemote(c.ToArray());
        }

        private void InvokeThisPtr(IntPtr fn, IntPtr self, IntPtr arg)
        {
            List<byte> c = new List<byte>();
            AddBytes(c, 0x48, 0x83, 0xEC, 0x28);
            AddBytes(c, 0x48, 0xB9); AddI64(c, self.ToInt64());
            AddBytes(c, 0x48, 0xBA); AddI64(c, arg.ToInt64());
            AddBytes(c, 0x48, 0xB8); AddI64(c, fn.ToInt64());
            AddBytes(c, 0xFF, 0xD0);
            AddBytes(c, 0x48, 0x83, 0xC4, 0x28, 0x33, 0xC0, 0xC3);
            InvokeRemote(c.ToArray());
        }

        private void InvokeThisPtrInt(IntPtr fn, IntPtr self, IntPtr arg, int value)
        {
            List<byte> c = new List<byte>();
            AddBytes(c, 0x48, 0x83, 0xEC, 0x28);
            AddBytes(c, 0x48, 0xB9); AddI64(c, self.ToInt64());
            AddBytes(c, 0x48, 0xBA); AddI64(c, arg.ToInt64());
            AddBytes(c, 0x41, 0xB8); AddI32(c, value);
            AddBytes(c, 0x48, 0xB8); AddI64(c, fn.ToInt64());
            AddBytes(c, 0xFF, 0xD0);
            AddBytes(c, 0x48, 0x83, 0xC4, 0x28, 0x33, 0xC0, 0xC3);
            InvokeRemote(c.ToArray());
        }

        private IntPtr GetPlayerObject()
        {
            EnsureConnected();
            _root = ReadPtr(Add(_dllBase, OFF_GameRootGlobal));
            if (_root == IntPtr.Zero) throw new InvalidOperationException("游戏根对象为空。");
            IntPtr branch = ReadPtr(Add(_root, OFF_RootPlayerBranch));
            if (branch == IntPtr.Zero) throw new InvalidOperationException("玩家分支尚未初始化。");
            IntPtr branch2 = ReadPtr(Add(branch, OFF_PlayerBranch2));
            if (branch2 == IntPtr.Zero) throw new InvalidOperationException("玩家对象尚未初始化。");
            if (ReadPtr(Add(branch2, OFF_PlayerGuard)) == IntPtr.Zero)
                throw new InvalidOperationException("当前玩家 Guard 为空，可能正在换图/加载。");
            IntPtr player = ReadPtr(Add(branch2, OFF_PlayerObject));
            if (player == IntPtr.Zero) throw new InvalidOperationException("当前玩家对象为空。");
            return player;
        }

        private void ApplyInvisibleToCurrentPlayer(bool enabled)
        {
            if (!enabled)
            {
                if (_lastInvisibleAddress != IntPtr.Zero && _lastInvisibleOriginal != null)
                {
                    try { WriteBytes(_lastInvisibleAddress, _lastInvisibleOriginal); } catch { }
                }
                _lastInvisibleAddress = IntPtr.Zero;
                _lastInvisibleOriginal = null;
                return;
            }
            IntPtr player = GetPlayerObject();
            IntPtr address = Add(player, OFF_HumanInvisibleToEnemies);
            if (_lastInvisibleAddress != address)
            {
                _lastInvisibleAddress = address;
                _lastInvisibleOriginal = ReadBytes(address, 1);
            }
            byte[] current = ReadBytes(address, 1);
            if (current[0] != 1) WriteBytes(address, new byte[] { 1 });
        }

        internal void SetInvisibleToEnemiesLegacy(bool enabled)
        {
            throw new InvalidOperationException("当前 1.55 的完全隐身消费点尚未验证。v0.0.8 已停止 player+0x10B4 写入；FLiNG 的隐身也只是部分敌人忽略，不作为目标实现。当前继续逆 AI Target / ignore_all_enemies 路线。 ");
        }

        private List<IntPtr> ResolveParameterTables()
        {
            List<IntPtr> result = new List<IntPtr>();
            HashSet<long> seen = new HashSet<long>();
            try
            {
                IntPtr holder = ReadPtr(Add(_dllBase, OFF_GlobalParameterHolder));
                if (holder != IntPtr.Zero)
                {
                    IntPtr table = ReadPtr(Add(holder, OFF_ParameterHolderToTable));
                    if (table != IntPtr.Zero && seen.Add(table.ToInt64())) result.Add(table);
                }
            }
            catch { }
            try
            {
                IntPtr player = GetPlayerObject();
                IntPtr holder = ReadPtr(Add(player, OFF_PlayerToParameterHolder));
                if (holder != IntPtr.Zero)
                {
                    IntPtr table = ReadPtr(Add(holder, OFF_ParameterHolderToTable));
                    if (table != IntPtr.Zero && seen.Add(table.ToInt64())) result.Add(table);
                }
            }
            catch { }
            return result;
        }

        private IntPtr ResolveParameterValueAddress(IntPtr table, int id)
        {
            if (table == IntPtr.Zero) return IntPtr.Zero;
            IntPtr array = ReadPtr(table);
            if (array == IntPtr.Zero) return IntPtr.Zero;
            IntPtr entry = ReadPtr(Add(array, (long)id * 8L));
            if (entry == IntPtr.Zero) return IntPtr.Zero;
            IntPtr value = Add(entry, 8L);
            float f = ReadFloat(value);
            if (Single.IsNaN(f) || Single.IsInfinity(f) || Math.Abs(f) > 100000F) return IntPtr.Zero;
            return value;
        }

        private void RememberMovementAddress(int id, IntPtr address)
        {
            if (address == IntPtr.Zero) return;
            long key = address.ToInt64();
            lock (_featureSync)
            {
                if (!_movementOriginals.ContainsKey(key))
                {
                    float value;
                    if (!_v012SprintOperandBaseline.TryGetValue(key, out value)) value = ReadFloat(address);
                    _movementOriginals[key] = value;
                }
                HashSet<long> set;
                if (!_movementAddresses.TryGetValue(id, out set))
                {
                    set = new HashSet<long>();
                    _movementAddresses[id] = set;
                }
                set.Add(key);
            }
        }

        private void ApplyMovementFactor(int id, int factor)
        {
            factor = Math.Max(1, Math.Min(10, factor));
            foreach (IntPtr table in ResolveParameterTables())
            {
                IntPtr address = IntPtr.Zero;
                try { address = ResolveParameterValueAddress(table, id); } catch { }
                if (address == IntPtr.Zero) continue;
                RememberMovementAddress(id, address);
                float original;
                lock (_featureSync) original = _movementOriginals[address.ToInt64()];
                float desired = original * factor;
                float current = ReadFloat(address);
                if (Math.Abs(current - desired) > 0.0001F) WriteFloat(address, desired);
            }
        }

        private void RestoreMovementId(int id)
        {
            long[] addresses = new long[0];
            lock (_featureSync)
            {
                HashSet<long> set;
                if (_movementAddresses.TryGetValue(id, out set)) addresses = set.ToArray();
                _movementAddresses.Remove(id);
            }
            foreach (long key in addresses)
            {
                float original;
                bool found;
                lock (_featureSync) found = _movementOriginals.TryGetValue(key, out original);
                if (found)
                {
                    try { WriteFloat(new IntPtr(key), original); } catch { }
                    lock (_featureSync) _movementOriginals.Remove(key);
                }
            }
        }

        internal string SetRunSpeedFactorLegacy(int factor)
        {
            EnsureConnected();
            factor = Math.Max(1, Math.Min(10, factor));
            lock (_featureSync) _runSpeedFactor = factor;
            if (factor == 1)
            {
                RestoreMovementId(PARAM_MoveSprintSpeed);
                RestoreMovementId(PARAM_MoveOverSpeedMaxSpeed);
            }
            else
            {
                ApplyMovementFactor(PARAM_MoveSprintSpeed, factor);
                ApplyMovementFactor(PARAM_MoveOverSpeedMaxSpeed, factor);
            }
            return DescribeMovementParam(PARAM_MoveSprintSpeed, factor) + "；" + DescribeMovementParam(PARAM_MoveOverSpeedMaxSpeed, factor) + "。750ms 非重入守护会重新解析新参数实例。";
        }

        internal string SetJumpHeightFactorLegacy(int factor)
        {
            EnsureConnected();
            factor = Math.Max(1, Math.Min(10, factor));
            lock (_featureSync) _jumpHeightFactor = factor;
            int[] ids = new int[] { PARAM_JumpMinHeightNoFitnessFactor, PARAM_JumpMinHeight, PARAM_JumpMaxHeightNoFitnessFactor, PARAM_JumpMaxHeight };
            if (factor == 1) foreach (int id in ids) RestoreMovementId(id);
            else foreach (int id in ids) ApplyMovementFactor(id, factor);
            return String.Join("；", ids.Select(id => DescribeMovementParam(id, factor)).ToArray()) + "。已补入此前漏掉的 JumpMaxHeight(0x29D)，并由 750ms 非重入守护重解析。";
        }

        private string DescribeMovementParam(int id, int factor)
        {
            int count = 0;
            lock (_featureSync)
            {
                HashSet<long> s;
                if (_movementAddresses.TryGetValue(id, out s)) count = s.Count;
            }
            if (factor == 1) return "参数 0x" + id.ToString("X") + " 已恢复 x1";
            if (count == 0) return "参数 0x" + id.ToString("X") + " 未解析到活动实例";
            return "参数 0x" + id.ToString("X") + " x" + factor + " · " + count + " 个活动实例";
        }

        private IntPtr AllocateRemoteStringA(string text)
        {
            if (text == null) text = "";
            byte[] ascii = System.Text.Encoding.ASCII.GetBytes(text + "\0");
            const int objectSize = 0x20;

            // MSVC std::string used by this game build:
            // _Bx[16] at +0, _Mysize at +0x10, _Myres at +0x18.
            // Short strings are stored inline; long strings point to a heap buffer.
            if (text.Length <= 15)
            {
                IntPtr remote = Native.VirtualAllocEx(_handle, IntPtr.Zero, new UIntPtr(objectSize),
                    MEM_COMMIT_RESERVE, PAGE_EXECUTE_READWRITE);
                if (remote == IntPtr.Zero) throw Win32("VirtualAllocEx std::string");
                byte[] block = new byte[objectSize];
                Buffer.BlockCopy(ascii, 0, block, 0, Math.Min(ascii.Length, 16));
                Buffer.BlockCopy(BitConverter.GetBytes((long)text.Length), 0, block, 0x10, 8);
                Buffer.BlockCopy(BitConverter.GetBytes((long)15), 0, block, 0x18, 8);
                WriteBytes(remote, block);
                return remote;
            }
            else
            {
                int totalSize = objectSize + ascii.Length;
                IntPtr remote = Native.VirtualAllocEx(_handle, IntPtr.Zero, new UIntPtr((uint)totalSize),
                    MEM_COMMIT_RESERVE, PAGE_EXECUTE_READWRITE);
                if (remote == IntPtr.Zero) throw Win32("VirtualAllocEx std::string");
                byte[] block = new byte[totalSize];
                long buffer = remote.ToInt64() + objectSize;
                Buffer.BlockCopy(BitConverter.GetBytes(buffer), 0, block, 0x00, 8);
                Buffer.BlockCopy(BitConverter.GetBytes((long)text.Length), 0, block, 0x10, 8);
                Buffer.BlockCopy(BitConverter.GetBytes((long)Math.Max(31, text.Length)), 0, block, 0x18, 8);
                Buffer.BlockCopy(ascii, 0, block, objectSize, ascii.Length);
                WriteBytes(remote, block);
                return remote;
            }
        }

        private long InvokeCallReturnRax(IntPtr fn, IntPtr rcx, IntPtr rdx, int r8d)
        {
            EnsureConnected();
            const int codeReserve = 0x80;
            IntPtr remote = Native.VirtualAllocEx(_handle, IntPtr.Zero, new UIntPtr(codeReserve),
                MEM_COMMIT_RESERVE, PAGE_EXECUTE_READWRITE);
            if (remote == IntPtr.Zero) throw Win32("VirtualAllocEx call/result");
            IntPtr thread = IntPtr.Zero;
            bool safeFree = true;
            try
            {
                IntPtr result = Add(remote, 0x70);
                List<byte> c = new List<byte>();
                AddBytes(c, 0x48, 0x83, 0xEC, 0x28);
                AddBytes(c, 0x48, 0xB9); AddI64(c, rcx.ToInt64());
                AddBytes(c, 0x48, 0xBA); AddI64(c, rdx.ToInt64());
                AddBytes(c, 0x41, 0xB8); AddI32(c, r8d);
                AddBytes(c, 0x48, 0xB8); AddI64(c, fn.ToInt64());
                AddBytes(c, 0xFF, 0xD0);
                AddBytes(c, 0x48, 0xA3); AddI64(c, result.ToInt64()); // mov [abs], rax
                AddBytes(c, 0x48, 0x83, 0xC4, 0x28, 0x33, 0xC0, 0xC3);
                WriteBytes(remote, c.ToArray());

                uint tid;
                thread = Native.CreateRemoteThread(_handle, IntPtr.Zero, UIntPtr.Zero, remote, IntPtr.Zero, 0, out tid);
                if (thread == IntPtr.Zero) throw Win32("CreateRemoteThread");
                safeFree = false;
                uint wait = Native.WaitForSingleObject(thread, WAIT_TIMEOUT_MS);
                if (wait == 0) safeFree = true;
                else if (wait == 0x102) throw new InvalidOperationException("游戏原生函数执行超时。已停止后续操作。");
                else throw new InvalidOperationException("等待游戏原生函数失败：0x" + wait.ToString("X8"));
                return BitConverter.ToInt64(ReadBytes(result, 8), 0);
            }
            finally
            {
                if (thread != IntPtr.Zero) Native.CloseHandle(thread);
                if (safeFree) Native.VirtualFreeEx(_handle, remote, UIntPtr.Zero, MEM_RELEASE);
            }
        }

        internal string GiveInventoryItem(string internalId, int count)
        {
            throw new InvalidOperationException("v0.0.8 已停用按字符串直接调用 Inventory::AddItem。当前 1.55 反汇编证明 AddItem 的 RDX 是原生 Item/Definition 对象，而不是 std::string；继续传字符串可能破坏游戏状态。");
        }

        internal string GiveWeaponLegacy(string internalId)
        {
            throw new InvalidOperationException("v0.0.8 已停用旧 B_GiveWeapon 字符串调用。B_GiveWeapon 只是把 RDX 原样转发给 Inventory::AddItem；RDX 必须先由游戏解析成原生 Item/Definition 对象。请用“武器 / 库存”深度 A/B：A 后在游戏内正常购买/拾取 1 件物品再 B，继续定位原生 item/definition 容器。");
        }

        internal string AddHellraidCoinsLegacy(int amount)
        {
            throw new InvalidOperationException("Hellraid Coins 与武器共用同一原生 Inventory ItemDefinition ABI。v0.0.8 在解析出 ItemDefinition 前停止直接写入，避免错误指针调用。");
        }

        private IntPtr FindCollectableObjectV010(int id)
        {
            EnsureConnected();
            IntPtr remoteId = Native.VirtualAllocEx(_handle, IntPtr.Zero, new UIntPtr(8), MEM_COMMIT_RESERVE, PAGE_EXECUTE_READWRITE);
            if (remoteId == IntPtr.Zero) throw Win32("VirtualAllocEx Collectable ID");
            try
            {
                WriteBytes(remoteId, BitConverter.GetBytes(id));
                long result = InvokeCallReturnRax(Add(_dllBase, RVA_FindCollectableObject), remoteId, IntPtr.Zero, 0);
                return new IntPtr(result);
            }
            finally
            {
                Native.VirtualFreeEx(_handle, remoteId, UIntPtr.Zero, MEM_RELEASE);
            }
        }

        internal void AwardCollectable(int id)
        {
            EnsureConnected();
            // Reject IDs that the game's own Collectable lookup does not recognize. This prevents a
            // BaseItem memory index or another unrelated numeric ID from being passed to AwardCollectable.
            if (FindCollectableObjectV010(id) == IntPtr.Zero)
                throw new InvalidOperationException("游戏 Collectable 表不存在 ID " + id + "，已拒绝写入。");
            IntPtr findFn = Add(_dllBase, RVA_FindCollectableObject);
            IntPtr awardFn = Add(_dllBase, RVA_AwardCollectable);
            List<byte> c = new List<byte>();
            // Thread-entry stack is 8 mod 16. Reserve 38h so both game calls are aligned,
            // keep 20h shadow space, and place the collectible ID in a stable local slot.
            AddBytes(c, 0x48, 0x83, 0xEC, 0x38);
            AddBytes(c, 0xC7, 0x44, 0x24, 0x28); AddI32(c, id);
            AddBytes(c, 0x48, 0x8D, 0x4C, 0x24, 0x28);
            AddBytes(c, 0x48, 0xB8); AddI64(c, findFn.ToInt64());
            AddBytes(c, 0xFF, 0xD0);
            AddBytes(c, 0x48, 0x8B, 0xD0);
            AddBytes(c, 0x33, 0xC9);
            AddBytes(c, 0x41, 0xB8, 0x01, 0x00, 0x00, 0x00);
            AddBytes(c, 0x48, 0xB8); AddI64(c, awardFn.ToInt64());
            AddBytes(c, 0xFF, 0xD0);
            AddBytes(c, 0x33, 0xC0, 0x48, 0x83, 0xC4, 0x38, 0xC3);
            InvokeRemote(c.ToArray());
        }

        internal HashSet<int> GetCollectedIds()
        {
            EnsureConnected();
            IntPtr player = GetPlayerObject();
            IntPtr container = ReadPtr(Add(player, OFF_PlayerCollectableContainer));
            if (container == IntPtr.Zero) return new HashSet<int>();
            IntPtr list = ReadPtr(Add(container, OFF_CollectableList));
            if (list == IntPtr.Zero) return new HashSet<int>();
            int count = ReadI32(Add(list, OFF_CollectableCount));
            if (count < 0 || count > 10000) throw new InvalidOperationException("收集品列表数量异常：" + count);
            IntPtr array = ReadPtr(Add(list, OFF_CollectableArray));
            HashSet<int> result = new HashSet<int>();
            if (count == 0 || array == IntPtr.Zero) return result;
            byte[] raw = ReadBytes(array, checked(count * 8));
            for (int i = 0; i < count; i++) result.Add(BitConverter.ToInt32(raw, i * 8));
            return result;
        }

        private static string BountyTypeLabel(string type)
        {
            switch ((type ?? "").Trim())
            {
                case "Basic": return "基础悬赏";
                case "Daily": return "每日悬赏";
                case "Special": return "特殊悬赏";
                case "Community": return "社区悬赏";
                case "PLS": return "活动悬赏";
                default: return String.IsNullOrWhiteSpace(type) ? "悬赏" : type;
            }
        }

        private static string BountyStatusLabel(int state)
        {
            switch (state)
            {
                case 1: return "未接取";
                case 2: return "进行中";
                case 3: return "已完成 · 待领取";
                case 4: return "已完成 · 已结算";
                default: return "未知状态 " + state;
            }
        }

        private List<BountyProgressInfo> ReadBountyProgress(IntPtr obj)
        {
            int pc = ReadI32(Add(obj, OFF_ProgressCount));
            if (pc < 0 || pc > 128) throw new InvalidOperationException("悬赏目标数量异常：" + pc);
            IntPtr pa = ReadPtr(Add(obj, OFF_ProgressArray));
            if (pc > 0 && pa == IntPtr.Zero) throw new InvalidOperationException("悬赏目标数组为空。");
            List<BountyProgressInfo> result = new List<BountyProgressInfo>();
            for (int i = 0; i < pc; i++)
            {
                IntPtr e = Add(pa, i * SIZE_Progress);
                result.Add(new BountyProgressInfo
                {
                    Index = i,
                    Ptr = e,
                    Type = ReadI32(Add(e, OFF_ProgressType)),
                    Total = ReadI32(Add(e, OFF_ProgressTotal)),
                    Remaining = ReadI32(Add(e, OFF_ProgressRemaining)),
                    Owner = ReadPtr(Add(e, OFF_ProgressOwner)),
                    Interval = ReadI32(Add(e, OFF_ProgressInterval)),
                    Flag82 = ReadBytes(Add(e, OFF_ProgressFlag82), 1)[0],
                    Flag83 = ReadBytes(Add(e, OFF_ProgressFlag83), 1)[0]
                });
            }
            return result;
        }

        internal List<BountyInfo> GetBounties()
        {
            EnsureBountyManager();
            IntPtr arr = ReadPtr(Add(_bountyManager, OFF_ManagerArray));
            int count = ReadI32(Add(_bountyManager, OFF_ManagerCount));
            if (count < 0 || count > 2048) throw new InvalidOperationException("悬赏数组数量异常：" + count);
            IntPtr current = ReadPtr(Add(_bountyManager, OFF_ManagerCurrent));
            List<BountyInfo> result = new List<BountyInfo>();
            for (int i = 0; i < count; i++)
            {
                IntPtr obj = ReadPtr(Add(arr, i * 8L));
                if (obj == IntPtr.Zero) continue;
                uint flags = ReadU32(Add(obj, OFF_BountyFlags));
                int kind = ReadI32(Add(obj, OFF_BountyKind));
                if ((flags & 0x4000) == 0 || kind != 1) continue;
                int id = ReadI32(Add(obj, OFF_BountyId));
                int state = ReadI32(Add(obj, OFF_BountyState));
                BountyCatalogItem cat;
                _bountyCatalog.TryGetValue(id, out cat);
                List<BountyProgressInfo> progress;
                try { progress = ReadBountyProgress(obj); }
                catch { progress = new List<BountyProgressInfo>(); }
                string type = cat == null ? "" : (cat.type ?? "");
                string displayName = cat == null ? ("Bounty " + id) : (String.IsNullOrWhiteSpace(cat.displayName) ? ("Bounty " + id) : cat.displayName);
                string reward = cat == null ? "" : (!String.IsNullOrWhiteSpace(cat.rewardDisplay) ? cat.rewardDisplay : (cat.poolReward ?? ""));
                result.Add(new BountyInfo
                {
                    Index = i,
                    Ptr = obj,
                    Id = id,
                    State = state,
                    ProgressCount = progress.Count,
                    IsCurrent = obj == current,
                    Source = cat == null ? "" : (cat.source ?? ""),
                    Type = type,
                    TypeLabel = BountyTypeLabel(type),
                    StatusLabel = BountyStatusLabel(state),
                    DisplayName = displayName,
                    NameToken = cat == null ? "" : (cat.nameToken ?? ""),
                    Reward = reward,
                    Catalog = cat,
                    Progress = progress
                });
            }
            return result.OrderBy(x => x.State == 2 ? 0 : x.State == 1 ? 1 : x.State == 3 ? 2 : 3)
                         .ThenBy(x => x.Type == "Basic" ? 0 : x.Type == "Daily" ? 1 : 2)
                         .ThenBy(x => x.Id).ToList();
        }

        private BountyInfo FindBounty(int id)
        {
            List<BountyInfo> matches = GetBounties().Where(x => x.Id == id).ToList();
            if (matches.Count == 0) throw new InvalidOperationException("没有在当前游戏会话中找到悬赏 ID " + id + "。");
            if (matches.Count == 1) return matches[0];
            BountyInfo current = matches.FirstOrDefault(x => x.IsCurrent);
            return current ?? matches.FirstOrDefault(x => x.State == 2) ?? matches[0];
        }

        private BountyInfo FindBountyTarget(int id, IntPtr ptr, int preferredState)
        {
            List<BountyInfo> list = GetBounties();
            List<BountyInfo> matches = list.Where(x => x.Id == id).ToList();
            if (matches.Count == 0) throw new InvalidOperationException("没有在当前游戏会话中找到选中的悬赏 ID " + id + "。");

            BountyInfo exact = ptr == IntPtr.Zero ? null : matches.FirstOrDefault(x => x.Ptr == ptr);

            // The numeric bounty ID is not a runtime identity. The manager can keep an
            // old object in one state bucket while creating/reusing another object for the
            // same ID in a new bucket.  This is exactly what made v0.0.8 report
            // "进行中 -> 未接取": it returned the stale exact pointer before considering
            // the state-2 replacement.
            if (preferredState >= 0)
            {
                if (exact != null && exact.State == preferredState) return exact;

                // For completion, an exact object that has just moved to a terminal state
                // is the same logical operation target and must win over any stale state-2
                // duplicate still present in the manager for a short time.
                if (preferredState == 2 && exact != null && (exact.State == 3 || exact.State == 4)) return exact;

                List<BountyInfo> preferred = matches.Where(x => x.State == preferredState).ToList();
                if (preferred.Count == 1) return preferred[0];
                if (preferred.Count > 1)
                {
                    List<BountyInfo> preferredCurrent = preferred.Where(x => x.IsCurrent).ToList();
                    if (preferredCurrent.Count == 1) return preferredCurrent[0];
                    throw new InvalidOperationException("悬赏 ID " + id + " 同时存在多个 state=" + preferredState + " 的运行时对象。请刷新后重新选择目标。");
                }

                // No replacement in the requested state. Return the exact object only now,
                // so the caller can report its real current state instead of accidentally
                // operating another duplicate row.
                if (exact != null) return exact;
            }
            else if (exact != null)
            {
                return exact;
            }

            if (matches.Count == 1) return matches[0];
            List<BountyInfo> current = matches.Where(x => x.IsCurrent).ToList();
            if (current.Count == 1) return current[0];
            throw new InvalidOperationException("悬赏 ID " + id + " 存在多个运行时对象，原选择对象已经失效。请刷新列表后重新选择，助手不会猜测目标对象。");
        }

        private BountyInfo TryFindExactBounty(int id, IntPtr ptr)
        {
            if (ptr == IntPtr.Zero) return null;
            return GetBounties().FirstOrDefault(x => x.Id == id && x.Ptr == ptr);
        }

        private BountyInfo FindUniqueReplacementBounty(int id, int state)
        {
            List<BountyInfo> candidates = GetBounties().Where(x => x.Id == id && x.State == state).ToList();
            if (candidates.Count == 0) return null;
            if (candidates.Count == 1) return candidates[0];
            List<BountyInfo> current = candidates.Where(x => x.IsCurrent).ToList();
            if (current.Count == 1) return current[0];
            throw new InvalidOperationException("悬赏 ID " + id + " 同时存在多个 state=" + state + " 的运行时对象。为避免误操作其他周期，已停止。");
        }

        private bool IsBountyQueuedForEvaluation(IntPtr obj, out int count)
        {
            count = 0;
            if (obj == IntPtr.Zero) return false;
            IntPtr manager = EnsureBountyManager();
            int c = ReadI32(Add(manager, OFF_ManagerEvalCount));
            int cap = ReadI32(Add(manager, OFF_ManagerEvalCapacity));
            if (c < 0 || c > 4096 || cap < c || cap > 16384) return false;
            count = c;
            IntPtr arr = ReadPtr(Add(manager, OFF_ManagerEvalArray));
            if (c == 0 || arr == IntPtr.Zero) return false;
            for (int i = 0; i < c; i++)
            {
                IntPtr q = ReadPtr(Add(arr, i * 8L));
                if (q == obj) return true;
            }
            return false;
        }

        private BountyInfo WaitForBountyGameThreadEvaluation(int id, IntPtr pinnedPtr, int milliseconds, Action<string> log, out bool sawQueued)
        {
            sawQueued = false;
            int loops = Math.Max(1, milliseconds / 100);
            for (int i = 0; i < loops; i++)
            {
                BountyInfo exact = TryFindExactBounty(id, pinnedPtr);
                if (exact == null || exact.State != 2) return exact;
                int queuedCount;
                bool queued = IsBountyQueuedForEvaluation(exact.Ptr, out queuedCount);
                if (queued) sawQueued = true;
                if (i == 0 && log != null)
                    log("等待游戏线程悬赏评估队列：queued=" + queued + "，queueCount=" + queuedCount + "。");
                Thread.Sleep(100);
            }
            return TryFindExactBounty(id, pinnedPtr);
        }

        private void EvaluateBounty(IntPtr obj) { InvokeThis(Add(_dllBase, RVA_EvaluateBounty), obj); }
        private void ActivateBounty(int id)
        {
            IntPtr manager = EnsureBountyManager();
            InvokeManagerId(Add(_dllBase, RVA_ActivateBounty), manager, id);
        }

        internal void AcceptBounty(int id, IntPtr selectedPtr)
        {
            BountyInfo b = FindBountyTarget(id, selectedPtr, 1);
            if (b.State == 2) return;
            if (b.State != 1) throw new InvalidOperationException("只有“未接取”的悬赏可以接取；当前状态：" + b.StatusLabel + "。");
            ActivateBounty(id);
            Thread.Sleep(160);
            BountyInfo after = FindBountyTarget(id, b.Ptr, 2);
            if (after.State != 2)
            {
                // Activation can replace the manager entry. Resolve the state-2 instance
                // with the same ID instead of accidentally picking a duplicate state-1 row.
                after = GetBounties().FirstOrDefault(x => x.Id == id && x.State == 2);
            }
            if (after == null || after.State != 2) throw new InvalidOperationException("游戏没有把该悬赏切换到进行中状态。");
        }

        // Pick the next objective conservatively. Runtime progress order normally matches
        // the script catalog. We prefer a visible objective whose prerequisite refs have
        // already completed, then a hidden prerequisite step. If the catalog is incomplete,
        // fall back to the first unfinished ordinary count rather than touching FailureCount.
        private static BountyProgressInfo SelectNextBountyProgress(BountyInfo bounty, List<BountyProgressInfo> progress)
        {
            List<BountyProgressInfo> unfinished = progress.Where(x => x.Type != 4 && x.Remaining > 0).ToList();
            if (unfinished.Count == 0) return null;
            List<BountyObjectiveCatalogItem> cats = bounty.Catalog == null ? null : bounty.Catalog.objectives;
            if (cats == null || cats.Count == 0) return unfinished[0];

            HashSet<string> completedRefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (BountyProgressInfo p in progress)
            {
                if (p.Remaining > 0 || p.Index < 0 || p.Index >= cats.Count) continue;
                BountyObjectiveCatalogItem c = cats[p.Index];
                if (c != null && !String.IsNullOrWhiteSpace(c.refName)) completedRefs.Add(c.refName.Trim());
            }

            Func<BountyProgressInfo, bool> prerequisiteSatisfied = delegate(BountyProgressInfo p)
            {
                if (p.Index < 0 || p.Index >= cats.Count) return true;
                BountyObjectiveCatalogItem c = cats[p.Index];
                if (c == null || String.IsNullOrWhiteSpace(c.prerequisite)) return true;
                string own = (c.refName ?? "").Trim();
                string[] refs = c.prerequisite.Split(new char[] { ';', ',', '|' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (string raw in refs)
                {
                    string r = raw.Trim();
                    if (r.Length == 0) continue;
                    // Some shipped bounty scripts use the same RefName on the objective
                    // and its prerequisite marker. Treat that self-reference as metadata,
                    // otherwise it would form an impossible circular gate.
                    if (own.Length > 0 && String.Equals(r, own, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!completedRefs.Contains(r)) return false;
                }
                return true;
            };

            BountyProgressInfo next = unfinished.FirstOrDefault(p =>
            {
                BountyObjectiveCatalogItem c = p.Index >= 0 && p.Index < cats.Count ? cats[p.Index] : null;
                return (c == null || !c.hidden) && prerequisiteSatisfied(p);
            });
            if (next != null) return next;

            next = unfinished.FirstOrDefault(p => prerequisiteSatisfied(p));
            return next ?? unfinished[0];
        }

        private static string BountyProgressName(BountyInfo bounty, BountyProgressInfo progress)
        {
            List<BountyObjectiveCatalogItem> cats = bounty.Catalog == null ? null : bounty.Catalog.objectives;
            if (cats == null || progress.Index < 0 || progress.Index >= cats.Count) return "目标 #" + (progress.Index + 1);
            BountyObjectiveCatalogItem c = cats[progress.Index];
            if (c == null) return "目标 #" + (progress.Index + 1);
            if (!String.IsNullOrWhiteSpace(c.display)) return c.display + (c.hidden ? "（隐藏步骤）" : "");
            if (!String.IsNullOrWhiteSpace(c.nameToken)) return c.nameToken.Trim('&').Replace('_', ' ') + (c.hidden ? "（隐藏步骤）" : "");
            if (!String.IsNullOrWhiteSpace(c.refName)) return c.refName + (c.hidden ? "（隐藏步骤）" : "");
            return "目标 #" + (progress.Index + 1);
        }

        // Forced terminal-state mutation from v0.0.2 is intentionally removed.
        // v0.0.8 only advances progress through the native progress updater and observes
        // whatever state transition the game itself performs.

        // Complete a bounty one objective at a time. Unlike 旧版任务助手, this never activates
        // an unaccepted bounty, never repairs FailureCount, and never calls a reward-claim
        // action. The game's evaluator runs after each individual objective so chained and
        // hidden prerequisite steps can become active in their normal order. We stop as soon
        // as the game reports the bounty complete/reward-pending state.
        internal int CompleteBountyStagedLegacy(int id, IntPtr selectedPtr, Action<string> log)
        {
            EnsureConnected();
            BountyInfo initial = selectedPtr == IntPtr.Zero ? null : TryFindExactBounty(id, selectedPtr);
            if (initial == null) initial = FindUniqueReplacementBounty(id, 2);
            if (initial == null)
                throw new InvalidOperationException("选中的悬赏运行时对象已经失效。请刷新列表后重新选择。");
            if (initial.State != 2)
                throw new InvalidOperationException("只有“进行中”(state=2) 的悬赏才能完成；当前状态：" + initial.StatusLabel + "。");

            IntPtr pinnedPtr = initial.Ptr;
            int changed = 0;
            bool nativeProgressIssued = false;
            bool evaluatedFinalState = false;

            for (int guard = 0; guard < 128; guard++)
            {
                BountyInfo b = TryFindExactBounty(id, pinnedPtr);
                if (b == null)
                {
                    BountyInfo terminal = FindUniqueReplacementBounty(id, 3) ?? FindUniqueReplacementBounty(id, 4);
                    if (terminal != null)
                    {
                        if (log != null) log("悬赏 “" + terminal.DisplayName + "” 已由游戏原生流程进入 " + terminal.StatusLabel + "。");
                        return changed;
                    }
                    BountyInfo continuation = FindUniqueReplacementBounty(id, 2);
                    if (continuation != null)
                    {
                        pinnedPtr = continuation.Ptr;
                        b = continuation;
                        if (log != null) log("游戏切换了同一悬赏的运行时阶段，继续锁定 ptr=0x" + pinnedPtr.ToInt64().ToString("X") + "。");
                    }
                    else
                    {
                        BountyInfo available = FindUniqueReplacementBounty(id, 1);
                        if (nativeProgressIssued && available != null)
                        {
                            if (log != null) log("本轮原生进度/评估链结束后悬赏回到 state=1；按可重复悬赏完成周期处理，不强制写状态。");
                            return changed;
                        }
                        throw new InvalidOperationException("悬赏运行时对象已消失，且没有可确认的后续/完成状态。为避免误操作已停止。");
                    }
                }

                if (b.State == 3 || b.State == 4)
                {
                    if (log != null) log("悬赏 “" + b.DisplayName + "” 已进入 " + b.StatusLabel + "；没有强制写 state/event。");
                    return changed;
                }
                if (b.State == 1)
                {
                    BountyInfo continuation = FindUniqueReplacementBounty(id, 2);
                    if (continuation != null && continuation.Ptr != pinnedPtr)
                    {
                        pinnedPtr = continuation.Ptr;
                        if (log != null) log("旧阶段回到可接取池，同时出现新的进行中阶段；继续处理新 ptr=0x" + pinnedPtr.ToInt64().ToString("X") + "。");
                        continue;
                    }
                    if (nativeProgressIssued)
                    {
                        if (log != null) log("游戏原生完成链已结束本轮并返回 state=1；按可重复悬赏周期处理为本轮完成。");
                        return changed;
                    }
                    throw new InvalidOperationException("该悬赏尚未接取。请先点“接取选中”。");
                }
                if (b.State != 2)
                    throw new InvalidOperationException("悬赏当前不是进行中状态：state=" + b.State + "。");

                List<BountyProgressInfo> progress = ReadBountyProgress(b.Ptr);
                BountyProgressInfo next = SelectNextBountyProgress(b, progress);
                if (next == null)
                {
                    // Progress::ApplyDelta's completion callback can enqueue the bounty into
                    // BountyManager+0x6F0. Manager::Update then evaluates that queue on the
                    // game's own thread. Prefer that native thread path first; only if the
                    // bounty was never observed in the queue do we use one guarded direct
                    // EvaluateBounty call as an experimental fallback. event 5/6 stays banned.
                    bool sawQueued;
                    BountyInfo settled = WaitForBountyGameThreadEvaluation(id, pinnedPtr, 1400, log, out sawQueued);
                    if (settled == null)
                    {
                        BountyInfo terminalGone = FindUniqueReplacementBounty(id, 3) ?? FindUniqueReplacementBounty(id, 4);
                        if (terminalGone != null) return changed;
                        BountyInfo availableGone = FindUniqueReplacementBounty(id, 1);
                        if (availableGone != null && nativeProgressIssued) return changed;
                    }
                    else if (settled.State == 3 || settled.State == 4)
                    {
                        if (log != null) log("游戏线程原生评估后已进入 " + settled.StatusLabel + "。");
                        return changed;
                    }
                    else if (settled.State == 1 && nativeProgressIssued)
                    {
                        if (log != null) log("游戏线程原生评估后回到 state=1；按可重复悬赏完成周期处理。");
                        return changed;
                    }

                    if (sawQueued)
                    {
                        // If the game itself queued and consumed this bounty yet it is still
                        // state=2, another native completion condition is false. Re-running
                        // EvaluateBounty from a foreign thread cannot make that condition true
                        // and only adds crash risk, so stop and capture the condition snapshot.
                        throw new InvalidOperationException("所有普通目标已清零，且已观察到游戏线程悬赏评估队列处理，但最终仍为 state=2。说明还有额外原生完成条件未满足；已停止，不会重复 EvaluateBounty 或强制 state/event。");
                    }

                    if (!evaluatedFinalState)
                    {
                        evaluatedFinalState = true;
                        if (log != null) log("普通目标已清零，但 1.4 秒内未观察到游戏线程评估队列；仅执行一次实验性 EvaluateBounty 兜底（不会发送 event=5/6）。");
                        EvaluateBounty(b.Ptr);
                        Thread.Sleep(520);
                    }
                    else Thread.Sleep(180);

                    settled = TryFindExactBounty(id, pinnedPtr);
                    if (settled != null && (settled.State == 3 || settled.State == 4))
                    {
                        if (log != null) log("EvaluateBounty 后已进入 " + settled.StatusLabel + "。");
                        return changed;
                    }
                    if (settled != null && settled.State == 1 && nativeProgressIssued)
                    {
                        if (log != null) log("EvaluateBounty 后游戏自行回到 state=1；按可重复悬赏完成周期处理。");
                        return changed;
                    }
                    BountyInfo terminal = FindUniqueReplacementBounty(id, 3) ?? FindUniqueReplacementBounty(id, 4);
                    if (terminal != null) return changed;
                    BountyInfo available = FindUniqueReplacementBounty(id, 1);
                    if (available != null && nativeProgressIssued) return changed;
                    BountyInfo nextStage = FindUniqueReplacementBounty(id, 2);
                    if (nextStage != null && nextStage.Ptr != pinnedPtr)
                    {
                        pinnedPtr = nextStage.Ptr;
                        evaluatedFinalState = false;
                        continue;
                    }
                    if (settled != null && settled.State == 2)
                        throw new InvalidOperationException("所有普通目标已清零，游戏线程未自动评估；一次 EvaluateBounty 兜底后仍停在 state=2。说明还有额外原生完成条件未满足；已停止，不会强制 state/event。");
                    throw new InvalidOperationException("目标已清零并完成安全评估流程，但最终运行时状态无法确认。已停止；不会补发 event=5/6。");
                }

                int beforeRemaining = next.Remaining;
                if (beforeRemaining <= 0) continue;
                string objectiveName = BountyProgressName(b, next);
                if (log != null)
                    log("原生推进悬赏 “" + b.DisplayName + "” ：" + objectiveName + "，remaining=" + beforeRemaining + "，调用 Progress::ApplyDelta(" + beforeRemaining + ")。" + ProgressDebugInline(next));

                InvokeThisInt(Add(_dllBase, RVA_UpdateBountyProgress), next.Ptr, beforeRemaining);
                nativeProgressIssued = true;
                changed++;

                // Read immediately before allowing a longer settle window. This distinguishes
                // a call that never touched +0x70 from a value that changed correctly and was
                // later reconstructed from a higher-level event/counter source.
                int immediateRemaining = ReadI32(Add(next.Ptr, OFF_ProgressRemaining));
                if (log != null) log("Progress::ApplyDelta 即时读回：" + beforeRemaining + " -> " + immediateRemaining + "。");
                Thread.Sleep(420);

                BountyInfo after = TryFindExactBounty(id, pinnedPtr);
                if (after != null && after.State == 2)
                {
                    List<BountyProgressInfo> afterProgress = ReadBountyProgress(after.Ptr);
                    BountyProgressInfo same = afterProgress.FirstOrDefault(x => x.Ptr == next.Ptr) ?? afterProgress.FirstOrDefault(x => x.Index == next.Index);
                    if (same != null && same.Remaining >= beforeRemaining)
                    {
                        if (immediateRemaining < beforeRemaining)
                            throw new InvalidOperationException("原生 Progress::ApplyDelta 曾即时生效（" + beforeRemaining + " -> " + immediateRemaining + "），但随后被游戏重新同步为 " + same.Remaining + "。这类目标存在更高层 authoritative event/counter；已停止，不会直接写 Remaining。" + ProgressDebugInline(same));
                        throw new InvalidOperationException("游戏原生进度函数没有减少该目标（before=" + beforeRemaining + "，immediate=" + immediateRemaining + "，after=" + same.Remaining + "）。已停止，不会直接写 Remaining。" + ProgressDebugInline(same));
                    }
                }
            }

            throw new InvalidOperationException("悬赏目标链超过安全上限，已停止继续操作。");
        }

        private static string ProgressDebugInline(BountyProgressInfo p)
        {
            if (p == null) return "";
            return " [type=" + p.Type + ", total=" + p.Total + ", interval=" + p.Interval +
                   ", flag82=" + p.Flag82 + ", flag83=" + p.Flag83 + ", owner=0x" + p.Owner.ToInt64().ToString("X") + "]";
        }

        // Repairs bounties that 旧版任务助手 may have completed by zeroing every
        // ordinary objective in one batch. We only touch state 3 (completed, reward
        // still pending). State 4 is intentionally refused to avoid reopening an
        // already-settled reward.
        //
        // The repair uses the game's own ActivateBounty path first, which runs the
        // bounty object's reset/activation callbacks. We then restore any ordinary
        // runtime counters that still remained at zero and replay the normal evaluator
        // one objective at a time. The end state must return to 3; no reward-claim
        // function is invoked here.
        internal int RepairLegacyCompletedBounty(int id, IntPtr selectedPtr, Action<string> log)
        {
            EnsureConnected();
            BountyInfo before = FindBountyTarget(id, selectedPtr, 3);
            if (before.State == 4)
                throw new InvalidOperationException("该悬赏已经结算奖励。为避免重复奖励，不会重新打开 state=4 的悬赏。");
            if (before.State != 3)
                throw new InvalidOperationException("修复只适用于“已完成 · 待领取”(state=3) 的悬赏；当前状态：" + before.StatusLabel + "。");

            if (log != null)
                log("开始修复旧版悬赏 “" + before.DisplayName + "”：先调用游戏原生激活/重置链，不直接改奖励状态。");

            ActivateBounty(id);
            Thread.Sleep(520);

            BountyInfo active = FindBountyTarget(id, before.Ptr, 2);
            if (active.State != 2)
                throw new InvalidOperationException("游戏原生激活函数没有把待领取悬赏恢复为进行中，state=" + active.State + "。已停止，未继续写目标。");

            List<BountyProgressInfo> progress = ReadBountyProgress(active.Ptr);
            int restored = 0;
            foreach (BountyProgressInfo p in progress)
            {
                if (p.Type == 4) continue; // FailureCount was never mass-zeroed by 旧版任务助手.
                int target = p.Total;
                if (target <= 0 && active.Catalog != null && active.Catalog.objectives != null &&
                    p.Index >= 0 && p.Index < active.Catalog.objectives.Count)
                {
                    BountyObjectiveCatalogItem cat = active.Catalog.objectives[p.Index];
                    if (cat != null) target = Math.Max(0, cat.count);
                }
                if (target > 0 && p.Remaining != target)
                {
                    WriteI32(Add(p.Ptr, OFF_ProgressRemaining), target);
                    restored++;
                }
            }

            if (log != null)
                log("原生激活后已恢复 " + restored + " 个普通目标计数；接下来按目标链逐项重新完成，使游戏重新记录完整进度。");

            Thread.Sleep(180);
            int replayed = CompleteBountyStaged(id, active.Ptr, log);

            BountyInfo after = FindBountyTarget(id, active.Ptr, 3);
            if (after.State != 3)
                throw new InvalidOperationException("目标重放结束后悬赏没有回到“已完成 · 待领取”，当前 state=" + after.State + "。已停止，不会领取奖励。");

            if (log != null)
                log("旧版悬赏进度修复结束：state=3（待领取），未调用奖励领取函数。");
            return replayed;
        }

        internal void ResetBounty(int id, IntPtr selectedPtr, Action<string> log)
        {
            EnsureBountyManager();
            BountyInfo bounty = FindBountyTarget(id, selectedPtr, -1);
            if (log != null) log("开始重置悬赏 “" + bounty.DisplayName + "”：" + bounty.StatusLabel + " → 未接取。");

            // If this bounty is currently tracked, detach it through the game's own
            // manager callback instead of only clearing +0x6D0 ourselves.
            IntPtr current = ReadPtr(Add(_bountyManager, OFF_ManagerCurrent));
            if (current == bounty.Ptr)
            {
                InvokeThisPtr(Add(_dllBase, RVA_SetActiveBounty), _bountyManager, IntPtr.Zero);
                Thread.Sleep(120);
            }

            // Restore objective counters to their initial values. FailureCount is a
            // guard condition and must remain positive, never zero.
            List<BountyProgressInfo> progress = ReadBountyProgress(bounty.Ptr);
            int restored = 0;
            foreach (BountyProgressInfo p in progress)
            {
                int target = p.Total;
                if (target <= 0 && bounty.Catalog != null && bounty.Catalog.objectives != null &&
                    p.Index >= 0 && p.Index < bounty.Catalog.objectives.Count)
                {
                    BountyObjectiveCatalogItem cat = bounty.Catalog.objectives[p.Index];
                    if (cat != null) target = cat.count;
                }
                if (p.Type == 4) target = Math.Max(1, target);
                else target = Math.Max(0, target);
                if (p.Remaining != target)
                {
                    WriteI32(Add(p.Ptr, OFF_ProgressRemaining), target);
                    restored++;
                }
            }

            // The game itself uses this pair around state transitions.  F4DE70 removes
            // the bounty object from the old state bucket; F4DD10 event=2 inserts it in
            // the unaccepted bucket and writes state 1.  This is materially safer than
            // changing +0x324 alone because the manager's per-state lists stay coherent.
            InvokeThisPtrInt(Add(_dllBase, RVA_RemoveBountyFromStateLists), _bountyManager, bounty.Ptr, 0);
            Thread.Sleep(80);
            InvokeThisPtrInt(Add(_dllBase, RVA_AddBountyStateEvent), _bountyManager, bounty.Ptr, BOUNTY_EVENT_AVAILABLE);
            Thread.Sleep(180);

            BountyInfo after = FindBountyTarget(id, bounty.Ptr, 1);
            if (after.State != 1)
                throw new InvalidOperationException("游戏原生状态链没有把悬赏恢复到未接取，当前 state=" + after.State + "。已停止。 ");
            if (log != null) log("悬赏已重置：恢复目标计数 " + restored + " 项，state=1（未接取），未领取任何奖励。现在可重新接取并完整重做。 ");
        }

        internal int CompleteAllAccepted(Action<string> log)
        {
            List<BountyInfo> accepted = GetBounties().Where(x => x.State == 2).ToList();
            int completed = 0;
            foreach (BountyInfo bounty in accepted)
            {
                try
                {
                    if (log != null) log("处理已接悬赏 ID " + bounty.Id + " / ptr=0x" + bounty.Ptr.ToInt64().ToString("X") + "。不会自动接取未接悬赏，也不会自动领取奖励。");
                    CompleteBountyStaged(bounty.Id, bounty.Ptr, log);
                    completed++;
                }
                catch (Exception ex)
                {
                    if (log != null) log("  ID " + bounty.Id + " 停止：" + ex.Message);
                }
            }
            return completed;
        }


        internal void SetActiveBounty(int id, IntPtr selectedPtr)
        {
            EnsureBountyManager();
            BountyInfo b = FindBountyTarget(id, selectedPtr, 2);
            if (b.State != 2) throw new InvalidOperationException("只有进行中的悬赏才能设为当前追踪。");
            InvokeThisPtr(Add(_dllBase, RVA_SetActiveBounty), _bountyManager, b.Ptr);
            Thread.Sleep(120);
        }

        private byte[] TryReadBytes(IntPtr address, int size)
        {
            try { return ReadBytes(address, size); }
            catch { return null; }
        }

        private byte[] TryReadFlexible(IntPtr address, int desired)
        {
            int size = desired;
            while (size >= 0x100)
            {
                byte[] b = TryReadBytes(address, size);
                if (b != null) return b;
                size /= 2;
                size &= ~0xFF;
            }
            return TryReadBytes(address, Math.Min(desired, 0x80));
        }

        private static string HexBytes(byte[] b, int max)
        {
            if (b == null) return "<unreadable>";
            int n = Math.Min(max, b.Length);
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < n; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(b[i].ToString("X2"));
            }
            if (b.Length > n) sb.Append(" ...");
            return sb.ToString();
        }

        private ProcessModule FindProcessModule(string moduleName)
        {
            if (_process == null) return null;
            try
            {
                foreach (ProcessModule m in _process.Modules)
                    if (String.Equals(m.ModuleName, moduleName, StringComparison.OrdinalIgnoreCase)) return m;
            }
            catch { }
            return null;
        }

        private static bool TryReadPeTextSection(string path, out int rva, out int size)
        {
            rva = 0; size = 0;
            try
            {
                using (FileStream fs = File.OpenRead(path))
                using (BinaryReader br = new BinaryReader(fs))
                {
                    fs.Position = 0x3C;
                    int pe = br.ReadInt32();
                    fs.Position = pe;
                    if (br.ReadUInt32() != 0x00004550U) return false;
                    br.ReadUInt16();
                    ushort sections = br.ReadUInt16();
                    fs.Position = pe + 20;
                    ushort optionalSize = br.ReadUInt16();
                    long table = pe + 24L + optionalSize;
                    for (int i = 0; i < sections; i++)
                    {
                        fs.Position = table + i * 40L;
                        byte[] nameRaw = br.ReadBytes(8);
                        string name = Encoding.ASCII.GetString(nameRaw).TrimEnd('\0');
                        uint virtualSize = br.ReadUInt32();
                        uint virtualAddress = br.ReadUInt32();
                        uint rawSize = br.ReadUInt32();
                        if (String.Equals(name, ".text", StringComparison.OrdinalIgnoreCase))
                        {
                            long wanted = Math.Max((long)virtualSize, (long)rawSize);
                            if (wanted <= 0 || wanted > 0x3000000L) return false;
                            rva = unchecked((int)virtualAddress);
                            size = unchecked((int)wanted);
                            return true;
                        }
                    }
                }
            }
            catch { }
            return false;
        }

        private ProbeModuleSnapshot SnapshotModuleText(string moduleName)
        {
            ProcessModule mod = FindProcessModule(moduleName);
            if (mod == null) return null;
            int rva, size;
            if (!TryReadPeTextSection(mod.FileName, out rva, out size)) return null;
            byte[] text = TryReadBytes(Add(mod.BaseAddress, rva), size);
            if (text == null) return null;
            ProbeModuleSnapshot s = new ProbeModuleSnapshot();
            s.Name = moduleName; s.Path = mod.FileName; s.Base = mod.BaseAddress.ToInt64(); s.TextRva = rva; s.Text = text;
            return s;
        }

        private Dictionary<string, ProbeModuleSnapshot> SnapshotProbeModules()
        {
            Dictionary<string, ProbeModuleSnapshot> result = new Dictionary<string, ProbeModuleSnapshot>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in new string[] { "DyingLightGame.exe", "gamedll_x64_rwdi.dll", "engine_x64_rwdi.dll", "rd3d11_x64_rwdi.dll", "filesystem_x64_rwdi.dll" })
            {
                ProbeModuleSnapshot s = SnapshotModuleText(name);
                if (s != null) result[name] = s;
            }
            return result;
        }

        private static void AppendChangedRuns(StringBuilder sb, byte[] a, byte[] b, long displayBase, int maxRuns, string prefix)
        {
            if (a == null || b == null) { sb.AppendLine("<snapshot missing>"); return; }
            int n = Math.Min(a.Length, b.Length);
            int runs = 0;
            for (int i = 0; i < n && runs < maxRuns; )
            {
                if (a[i] == b[i]) { i++; continue; }
                int start = i;
                while (i < n && a[i] != b[i] && i - start < 64) i++;
                sb.Append(prefix).Append("0x").Append((displayBase + start).ToString("X")).Append(" A=");
                for (int j = start; j < i; j++) sb.Append(a[j].ToString("X2")).Append(' ');
                sb.Append(" B=");
                for (int j = start; j < i; j++) sb.Append(b[j].ToString("X2")).Append(' ');
                sb.AppendLine();
                runs++;
            }
            if (runs == 0) sb.AppendLine("<no byte changes>");
            else if (runs >= maxRuns) sb.AppendLine("<change list truncated>");
        }

        private void AppendModuleChangedRuns(StringBuilder sb, ProbeModuleSnapshot a, ProbeModuleSnapshot b)
        {
            if (a == null || b == null || a.Text == null || b.Text == null) { sb.AppendLine("  <snapshot missing>"); return; }
            int n = Math.Min(a.Text.Length, b.Text.Length);
            int runs = 0;
            for (int i = 0; i < n && runs < 256; )
            {
                if (a.Text[i] == b.Text[i]) { i++; continue; }
                int start = i;
                while (i < n && a.Text[i] != b.Text[i] && i - start < 64) i++;
                long address = b.Base + b.TextRva + start;
                sb.Append("  @0x").Append(address.ToString("X")).Append(" A=");
                for (int j = start; j < i; j++) sb.Append(a.Text[j].ToString("X2")).Append(' ');
                sb.Append(" B=");
                for (int j = start; j < i; j++) sb.Append(b.Text[j].ToString("X2")).Append(' ');
                sb.AppendLine();
                if (start + 5 <= b.Text.Length && b.Text[start] == 0xE9)
                {
                    int rel = BitConverter.ToInt32(b.Text, start + 1);
                    long target = address + 5L + rel;
                    byte[] cave = TryReadFlexible(new IntPtr(target), 0x180);
                    sb.Append("    E9 target=0x").Append(target.ToString("X")).Append(" caveHead=").AppendLine(HexBytes(cave, 0x180));
                }
                runs++;
            }
            if (runs == 0) sb.AppendLine("<no byte changes>");
            else if (runs >= 256) sb.AppendLine("<change list truncated>");
        }

        private void AppendKnownFlingRunHookStatus(StringBuilder sb)
        {
            sb.AppendLine("[known FLiNG Super Run live patch sites]");
            AppendKnownPatchSite(sb, "engine_x64_rwdi.dll", RVA_FlingRunEngineHook, 0x20,
                "F3 0F 58 8B C0 00 00 00 F3 0F 59 D6");
            AppendKnownPatchSite(sb, "gamedll_x64_rwdi.dll", RVA_FlingRunGameHook, 0x20,
                "F3 45 0F 59 6C 24 24");
        }

        private void AppendKnownPatchSite(StringBuilder sb, string moduleName, long rva, int size, string original)
        {
            ProcessModule mod = FindProcessModule(moduleName);
            if (mod == null) { sb.AppendLine(moduleName + "+0x" + rva.ToString("X") + " <module not loaded>"); return; }
            IntPtr address = Add(mod.BaseAddress, rva);
            byte[] live = TryReadBytes(address, size);
            sb.AppendLine(moduleName + "+0x" + rva.ToString("X") + " @0x" + address.ToInt64().ToString("X") + " original=" + original);
            sb.AppendLine("  live=" + HexBytes(live, size));
            if (live != null && live.Length >= 5 && live[0] == 0xE9)
            {
                int rel = BitConverter.ToInt32(live, 1);
                long target = address.ToInt64() + 5L + rel;
                byte[] cave = TryReadFlexible(new IntPtr(target), 0x200);
                sb.AppendLine("  status=HOOKED E9 target=0x" + target.ToString("X") + " caveHead=" + HexBytes(cave, 0x200));
            }
            else sb.AppendLine("  status=not E9-hooked at capture time");
        }

        private static bool LooksLikeHeapPointer(long q)
        {
            // User-space heap allocations in the observed 1.55 process are well below loaded
            // module addresses (0x7FF...).  Excluding module/code pointers keeps inventory
            // graph snapshots small and focused on mutable data objects.
            return q >= 0x100000000L && q < 0x00007F0000000000L;
        }

        private Dictionary<long, byte[]> SnapshotPlayerGraph(IntPtr player)
        {
            Dictionary<long, byte[]> result = new Dictionary<long, byte[]>();
            byte[] root = TryReadBytes(player, 0x2000);
            if (root == null) return result;
            result[player.ToInt64()] = root;
            HashSet<long> seen = new HashSet<long>();
            List<long> first = new List<long>();
            for (int off = 0; off + 8 <= root.Length && first.Count < 128; off += 8)
            {
                long q = BitConverter.ToInt64(root, off);
                if (!LooksLikeHeapPointer(q) || !seen.Add(q)) continue;
                byte[] child = TryReadFlexible(new IntPtr(q), 0x800);
                if (child == null) continue;
                result[q] = child; first.Add(q);
            }
            foreach (long parent in first.Take(64).ToArray())
            {
                byte[] child = result[parent];
                for (int off = 0; off + 8 <= Math.Min(child.Length, 0x400) && result.Count < 240; off += 8)
                {
                    long q = BitConverter.ToInt64(child, off);
                    if (!LooksLikeHeapPointer(q) || !seen.Add(q)) continue;
                    byte[] grand = TryReadFlexible(new IntPtr(q), 0x600);
                    if (grand != null) result[q] = grand;
                }
            }
            return result;
        }

        private static IEnumerable<string> PrintableAscii(byte[] data, int maxStrings)
        {
            if (data == null) yield break;
            int found = 0;
            for (int i = 0; i < data.Length && found < maxStrings; )
            {
                if (data[i] < 0x20 || data[i] > 0x7E) { i++; continue; }
                int start = i;
                while (i < data.Length && data[i] >= 0x20 && data[i] <= 0x7E && i - start < 96) i++;
                int len = i - start;
                if (len >= 4)
                {
                    yield return Encoding.ASCII.GetString(data, start, len);
                    found++;
                }
            }
        }

        private Dictionary<long, byte[]> SnapshotInventoryGraph(IntPtr player)
        {
            Dictionary<long, byte[]> result = new Dictionary<long, byte[]>();
            IntPtr cheat = Add(player, OFF_PlayerCheatInterface);
            IntPtr inventoryOwner = Add(player, OFF_PlayerInventoryOwnerInterface);
            byte[] cheatRoot = TryReadBytes(cheat, 0x900);
            byte[] ownerRoot = TryReadBytes(inventoryOwner, 0x900);
            HashSet<long> seen = new HashSet<long>();
            List<long> first = new List<long>();
            foreach (KeyValuePair<long, byte[]> rootPair in new KeyValuePair<long, byte[]>[] {
                new KeyValuePair<long, byte[]>(cheat.ToInt64(), cheatRoot),
                new KeyValuePair<long, byte[]>(inventoryOwner.ToInt64(), ownerRoot) })
            {
                byte[] root = rootPair.Value;
                if (root == null) continue;
                result[rootPair.Key] = root;
                for (int off = 0; off + 8 <= root.Length && first.Count < 128; off += 8)
                {
                    long q = BitConverter.ToInt64(root, off);
                    if (!LooksLikeHeapPointer(q) || !seen.Add(q)) continue;
                    byte[] child = TryReadFlexible(new IntPtr(q), 0x800);
                    if (child == null) continue;
                    result[q] = child; first.Add(q);
                }
            }
            if (result.Count == 0) return result;
            // One additional pointer level catches the actual item arrays/maps without
            // recursively walking the entire process.
            foreach (long parent in first.Take(48).ToArray())
            {
                byte[] child = result[parent];
                for (int off = 0; off + 8 <= Math.Min(child.Length, 0x300) && result.Count < 160; off += 8)
                {
                    long q = BitConverter.ToInt64(child, off);
                    if (!LooksLikeHeapPointer(q) || !seen.Add(q)) continue;
                    byte[] grand = TryReadBytes(new IntPtr(q), 0x600);
                    if (grand != null) result[q] = grand;
                }
            }
            return result;
        }

        private string TryDescribeRemoteObject(long objectAddress)
        {
            try
            {
                if (objectAddress == 0 || _process == null) return "?";
                byte[] head = TryReadBytes(new IntPtr(objectAddress), 8);
                if (head == null) return "?";
                long vtable = BitConverter.ToInt64(head, 0);
                ProcessModule module = null;
                foreach (ProcessModule m in _process.Modules)
                {
                    long b = m.BaseAddress.ToInt64();
                    long e = b + Math.Max(1, m.ModuleMemorySize);
                    if (vtable >= b && vtable < e) { module = m; break; }
                }
                if (module == null) return "?";
                byte[] colPtrRaw = TryReadBytes(new IntPtr(vtable - 8), 8);
                if (colPtrRaw == null) return "?";
                long col = BitConverter.ToInt64(colPtrRaw, 0);
                long moduleBase = module.BaseAddress.ToInt64();
                if (col < moduleBase || col >= moduleBase + Math.Max(1, module.ModuleMemorySize)) return "?";
                byte[] colRaw = TryReadBytes(new IntPtr(col), 24);
                if (colRaw == null || colRaw.Length < 20) return "?";
                uint typeRva = BitConverter.ToUInt32(colRaw, 12);
                if (typeRva == 0 || typeRva >= (uint)Math.Max(1, module.ModuleMemorySize)) return "?";
                byte[] nameRaw = TryReadBytes(new IntPtr(moduleBase + typeRva + 16), 192);
                if (nameRaw == null) return "?";
                int len = 0; while (len < nameRaw.Length && nameRaw[len] != 0) len++;
                if (len <= 0) return "?";
                string name = Encoding.ASCII.GetString(nameRaw, 0, len);
                return module.ModuleName + "!" + name;
            }
            catch { return "?"; }
        }

        private void AppendInventoryGraphDiff(StringBuilder sb, Dictionary<long, byte[]> a, Dictionary<long, byte[]> b, IntPtr player)
        {
            sb.AppendLine("[inventory deep graph changes]");
            if (a == null) { sb.AppendLine("<baseline inventory graph missing>"); return; }
            if (b == null) { sb.AppendLine("<current inventory graph missing>"); return; }
            sb.AppendLine("A objects=" + a.Count + " B objects=" + b.Count + " IPlayerCheat=player+0x8F8=0x" + Add(player, OFF_PlayerCheatInterface).ToInt64().ToString("X") + " IInventoryOwner=player+0x930=0x" + Add(player, OFF_PlayerInventoryOwnerInterface).ToInt64().ToString("X"));
            int changedObjects = 0;
            foreach (long address in a.Keys.Intersect(b.Keys).OrderBy(x => x))
            {
                byte[] aa = a[address], bb = b[address];
                bool different = false;
                int n = Math.Min(aa.Length, bb.Length);
                for (int i = 0; i < n; i++) if (aa[i] != bb[i]) { different = true; break; }
                if (!different && aa.Length == bb.Length) continue;
                sb.AppendLine("object 0x" + address.ToString("X") + " class=" + TryDescribeRemoteObject(address) + " sizeA=0x" + aa.Length.ToString("X") + " sizeB=0x" + bb.Length.ToString("X"));
                AppendChangedRuns(sb, aa, bb, address, 24, "  @");
                changedObjects++;
                if (changedObjects >= 24) { sb.AppendLine("<changed object list truncated>"); break; }
            }
            List<long> added = b.Keys.Except(a.Keys).Take(40).ToList();
            if (added.Count > 0)
            {
                sb.AppendLine("new readable heap objects in B: " + String.Join(", ", added.Select(x => "0x" + x.ToString("X")).ToArray()));
                foreach (long address in added.Take(16))
                {
                    byte[] raw; if (!b.TryGetValue(address, out raw)) continue;
                    sb.AppendLine("  new 0x" + address.ToString("X") + " class=" + TryDescribeRemoteObject(address) + " head=" + HexBytes(raw, 0x100));
                    string[] strings = PrintableAscii(raw, 8).ToArray();
                    if (strings.Length > 0) sb.AppendLine("    ascii=" + String.Join(" | ", strings));
                }
            }
            if (changedObjects == 0 && added.Count == 0) sb.AppendLine("<no stable inventory-graph changes>");
        }

        private void AppendPlayerGraphDiff(StringBuilder sb, Dictionary<long, byte[]> a, Dictionary<long, byte[]> b, IntPtr player)
        {
            sb.AppendLine("[deep player object graph changes]");
            if (a == null || b == null) { sb.AppendLine("<baseline/current graph missing>"); return; }
            sb.AppendLine("A objects=" + a.Count + " B objects=" + b.Count + " player=0x" + player.ToInt64().ToString("X"));
            int changed = 0;
            foreach (long address in a.Keys.Intersect(b.Keys).OrderBy(x => x))
            {
                byte[] aa = a[address], bb = b[address];
                int n = Math.Min(aa.Length, bb.Length); bool different = aa.Length != bb.Length;
                for (int i = 0; i < n && !different; i++) if (aa[i] != bb[i]) different = true;
                if (!different) continue;
                sb.AppendLine("object 0x" + address.ToString("X") + " sizeA=0x" + aa.Length.ToString("X") + " sizeB=0x" + bb.Length.ToString("X"));
                AppendChangedRuns(sb, aa, bb, address, 20, "  @");
                if (++changed >= 32) { sb.AppendLine("<changed object list truncated>"); break; }
            }
            List<long> added = b.Keys.Except(a.Keys).Take(24).ToList();
            if (added.Count > 0)
            {
                sb.AppendLine("new readable heap objects in B: " + String.Join(", ", added.Select(x => "0x" + x.ToString("X")).ToArray()));
                foreach (long address in added.Take(8))
                {
                    byte[] raw; if (!b.TryGetValue(address, out raw)) continue;
                    sb.AppendLine("  new 0x" + address.ToString("X") + " head=" + HexBytes(raw, 0x80));
                }
            }
            if (changed == 0 && added.Count == 0) sb.AppendLine("<no stable deep player-graph changes>");
        }

        private IntPtr GetVerifiedQuestManagerSingleton()
        {
            IntPtr manager = Add(_dllBase, RVA_QuestManagerSingleton);
            try
            {
                long actualVtable = ReadPtr(manager).ToInt64();
                long expectedVtable = Add(_dllBase, RVA_QuestManagerPrimaryVtable).ToInt64();
                return actualVtable == expectedVtable ? manager : IntPtr.Zero;
            }
            catch { return IntPtr.Zero; }
        }

        private List<IntPtr> FindQuestManagerCandidatesByVtable()
        {
            List<IntPtr> result = new List<IntPtr>();
            HashSet<long> seenObjects = new HashSet<long>();
            HashSet<long> scannedOwners = new HashSet<long>();
            long expected = Add(_dllBase, RVA_QuestManagerPrimaryVtable).ToInt64();

            // The exact current-build singleton is the authoritative first candidate.
            IntPtr singleton = GetVerifiedQuestManagerSingleton();
            if (singleton != IntPtr.Zero)
            {
                result.Add(singleton);
                seenObjects.Add(singleton.ToInt64());
            }

            // Keep the vtable scan as an independent read-only cross-check.
            List<IntPtr> owners = new List<IntPtr>();
            owners.Add(_root);
            byte[] rootRaw = TryReadBytes(_root, 0x1200);
            if (rootRaw != null)
            {
                for (int off = 0; off + 8 <= rootRaw.Length; off += 8)
                {
                    long q = BitConverter.ToInt64(rootRaw, off);
                    if (LooksLikeHeapPointer(q)) owners.Add(new IntPtr(q));
                }
            }

            foreach (IntPtr owner in owners.Where(x => x != IntPtr.Zero).Take(220))
            {
                if (!scannedOwners.Add(owner.ToInt64())) continue;
                byte[] block = TryReadFlexible(owner, 0x5000);
                if (block == null) continue;

                // The owner itself can be QuestManager.
                if (block.Length >= 8 && BitConverter.ToInt64(block, 0) == expected && seenObjects.Add(owner.ToInt64())) result.Add(owner);

                // Or it can contain a pointer to the live QuestManager.
                for (int off = 0; off + 8 <= block.Length && result.Count < 32; off += 8)
                {
                    long q = BitConverter.ToInt64(block, off);
                    if (!LooksLikeHeapPointer(q) || seenObjects.Contains(q)) continue;
                    byte[] head = TryReadBytes(new IntPtr(q), 8);
                    if (head == null) continue;
                    if (BitConverter.ToInt64(head, 0) == expected)
                    {
                        seenObjects.Add(q);
                        result.Add(new IntPtr(q));
                    }
                }
            }
            return result;
        }

        private void AppendKeyHitsInRegion(StringBuilder sb, string label, IntPtr region, int size, byte[] needle)
        {
            sb.AppendLine("[" + label + "] base=0x" + region.ToInt64().ToString("X") + " size=0x" + size.ToString("X"));
            if (region == IntPtr.Zero) { sb.AppendLine("<null>"); return; }
            byte[] data = TryReadFlexible(region, size);
            if (data == null) { sb.AppendLine("<unreadable>"); return; }
            List<long> hits = FindPattern(data, needle, region.ToInt64(), 32);
            sb.AppendLine("hits=" + hits.Count);
            foreach (long hit in hits)
            {
                long rel = hit - region.ToInt64();
                sb.AppendLine("  hit=0x" + hit.ToString("X") + " rel=+0x" + rel.ToString("X"));
                int local = unchecked((int)rel);
                int from = Math.Max(0, local - 0x40);
                int count = Math.Min(0x100, data.Length - from);
                if (count > 0)
                {
                    byte[] window = new byte[count]; Buffer.BlockCopy(data, from, window, 0, count);
                    sb.AppendLine("  window@+0x" + from.ToString("X") + "=" + HexBytes(window, 0x100));
                }
            }
        }

        private void AppendQuestManagerPointerChildren(StringBuilder sb, IntPtr manager, byte[] needle)
        {
            sb.AppendLine("[IQuestManager pointer children key search]");
            if (manager == IntPtr.Zero) { sb.AppendLine("<manager unavailable>"); return; }
            byte[] ptrs = TryReadFlexible(manager, 0x1000);
            if (ptrs == null) { sb.AppendLine("<manager unreadable>"); return; }
            HashSet<long> seen = new HashSet<long>();
            int searched = 0, hitChildren = 0;
            for (int off = 0; off + 8 <= ptrs.Length && searched < 96; off += 8)
            {
                long q = BitConverter.ToInt64(ptrs, off);
                if (!LooksLikeHeapPointer(q) || !seen.Add(q)) continue;
                byte[] child = TryReadFlexible(new IntPtr(q), 0x4000);
                if (child == null) continue;
                searched++;
                List<long> hits = FindPattern(child, needle, q, 8);
                if (hits.Count == 0) continue;
                hitChildren++;
                sb.AppendLine("manager+0x" + off.ToString("X") + " -> 0x" + q.ToString("X") + " hits=" + hits.Count);
                foreach (long h in hits) sb.AppendLine("  hit=0x" + h.ToString("X") + " child+0x" + (h-q).ToString("X"));
            }
            sb.AppendLine("searched children=" + searched + " children with hits=" + hitChildren);
        }

        private Dictionary<long, byte[]> SnapshotBountyGraph(BountyInfo bounty)
        {
            Dictionary<long, byte[]> result = new Dictionary<long, byte[]>();
            if (bounty == null || bounty.Ptr == IntPtr.Zero) return result;

            Action<IntPtr, int> addBlock = delegate(IntPtr address, int size)
            {
                if (address == IntPtr.Zero || result.Count >= 220) return;
                long key = address.ToInt64();
                if (result.ContainsKey(key)) return;
                byte[] raw = TryReadFlexible(address, size);
                if (raw != null) result[key] = raw;
            };

            IntPtr manager = EnsureBountyManager();
            addBlock(manager, 0x1200);
            addBlock(bounty.Ptr, 0x900);

            List<BountyProgressInfo> progress = ReadBountyProgress(bounty.Ptr);
            foreach (BountyProgressInfo p in progress) addBlock(p.Ptr, 0x300);

            // Follow one level of heap pointers from the bounty and progress objects. This is
            // deliberately bounded: we want authoritative progress/count containers, not an
            // unbounded walk of the whole process.
            List<KeyValuePair<long, byte[]>> seeds = result.ToList();
            HashSet<long> seen = new HashSet<long>(result.Keys);
            foreach (KeyValuePair<long, byte[]> block in seeds)
            {
                byte[] raw = block.Value;
                int limit = Math.Min(raw.Length, 0x500);
                for (int off = 0; off + 8 <= limit && result.Count < 220; off += 8)
                {
                    long q = BitConverter.ToInt64(raw, off);
                    if (!LooksLikeHeapPointer(q) || !seen.Add(q)) continue;
                    byte[] child = TryReadFlexible(new IntPtr(q), 0x500);
                    if (child != null) result[q] = child;
                }
            }
            return result;
        }

        private static void AppendBountyGraphDiff(StringBuilder sb, Dictionary<long, byte[]> a, Dictionary<long, byte[]> b)
        {
            sb.AppendLine("[bounty object graph changes]");
            if (a == null) { sb.AppendLine("<baseline graph missing>"); return; }
            if (b == null) { sb.AppendLine("<current graph missing>"); return; }
            sb.AppendLine("A objects=" + a.Count + " B objects=" + b.Count);
            int changed = 0;
            foreach (long address in a.Keys.Intersect(b.Keys).OrderBy(x => x))
            {
                byte[] aa = a[address], bb = b[address];
                int n = Math.Min(aa.Length, bb.Length);
                bool diff = aa.Length != bb.Length;
                if (!diff)
                {
                    for (int i = 0; i < n; i++) if (aa[i] != bb[i]) { diff = true; break; }
                }
                if (!diff) continue;
                sb.AppendLine("object 0x" + address.ToString("X") + " size A=0x" + aa.Length.ToString("X") + " B=0x" + bb.Length.ToString("X"));
                AppendChangedRuns(sb, aa, bb, address, 48, "  @");
                changed++;
                if (changed >= 80) { sb.AppendLine("<changed object list truncated>"); break; }
            }
            foreach (long address in b.Keys.Except(a.Keys).OrderBy(x => x).Take(32))
                sb.AppendLine("new object @0x" + address.ToString("X") + " size=0x" + b[address].Length.ToString("X"));
            foreach (long address in a.Keys.Except(b.Keys).OrderBy(x => x).Take(32))
                sb.AppendLine("missing object @0x" + address.ToString("X") + " oldSize=0x" + a[address].Length.ToString("X"));
            if (changed == 0 && !b.Keys.Except(a.Keys).Any() && !a.Keys.Except(b.Keys).Any()) sb.AppendLine("<no graph changes>");
        }

        internal string CaptureBountyProgressProbe(int id, IntPtr selectedPtr, bool compare)
        {
            EnsureConnected();
            BountyInfo bounty = FindBountyTarget(id, selectedPtr, 2);
            string key = id + ":" + bounty.Ptr.ToInt64().ToString("X");
            Dictionary<long, byte[]> graph = SnapshotBountyGraph(bounty);
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("DL1TaskAssistant v0.1.1 bounty progress A/B probe");
            sb.AppendLine("id=" + bounty.Id);
            sb.AppendLine("name=" + bounty.DisplayName);
            sb.AppendLine("state=" + bounty.State + " current=" + bounty.IsCurrent);
            sb.AppendLine("bountyPtr=0x" + bounty.Ptr.ToInt64().ToString("X"));
            sb.AppendLine("manager=0x" + EnsureBountyManager().ToInt64().ToString("X"));
            sb.AppendLine("mode=" + (compare ? "B/compare" : "A/baseline"));
            sb.AppendLine();
            sb.AppendLine("[current progress records]");
            List<BountyProgressInfo> progress = ReadBountyProgress(bounty.Ptr);
            foreach (BountyProgressInfo p in progress)
            {
                sb.AppendLine("#" + p.Index + " ptr=0x" + p.Ptr.ToInt64().ToString("X") + " type=" + p.Type + " total=" + p.Total + " remaining=" + p.Remaining + " done=" + Math.Max(0, p.Total - p.Remaining) +
                    " interval=" + p.Interval + " flag82=" + p.Flag82 + " flag83=" + p.Flag83 + " owner=0x" + p.Owner.ToInt64().ToString("X"));
                if (p.Owner != IntPtr.Zero)
                    sb.AppendLine("   ownerHead=" + HexBytes(TryReadBytes(p.Owner, 0x60), 0x60));
            }

            if (!compare)
            {
                lock (_featureSync) _bountyProbeGraphs[key] = graph;
                sb.AppendLine();
                sb.AppendLine("Baseline captured. Now progress exactly ONE smallest legitimate unit of this selected bounty, do not switch bounty or claim/reset it, then capture B immediately.");
                sb.AppendLine("Captured graph objects=" + graph.Count);
            }
            else
            {
                Dictionary<long, byte[]> old = null;
                lock (_featureSync) _bountyProbeGraphs.TryGetValue(key, out old);
                sb.AppendLine();
                AppendBountyGraphDiff(sb, old, graph);
                sb.AppendLine();
                sb.AppendLine("Interpretation target: fields that changed consistently with exactly one legitimate objective increment are candidates for authoritative progress/state. The old Remaining display field alone is NOT assumed authoritative.");
            }
            return sb.ToString();
        }

        internal string CaptureBountyFailureDiagnostic(int id, IntPtr selectedPtr, string reason)
        {
            EnsureConnected();
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("DL1TaskAssistant v0.1.1 bounty completion failure diagnostic");
            sb.AppendLine("reason=" + (reason ?? ""));
            sb.AppendLine("pid=" + ProcessId);
            sb.AppendLine("gamedll=0x" + _dllBase.ToInt64().ToString("X"));
            sb.AppendLine("manager=0x" + EnsureBountyManager().ToInt64().ToString("X"));
            sb.AppendLine("requestedId=" + id + " selectedPtr=0x" + selectedPtr.ToInt64().ToString("X"));
            sb.AppendLine();
            List<BountyInfo> sameId = GetBounties().Where(x => x.Id == id).ToList();
            sb.AppendLine("[same-id runtime entries] count=" + sameId.Count);
            foreach (BountyInfo b in sameId)
                sb.AppendLine("ptr=0x" + b.Ptr.ToInt64().ToString("X") + " state=" + b.State + " current=" + b.IsCurrent + " kind=" + b.Type + " progressCount=" + b.ProgressCount);

            BountyInfo selected = sameId.FirstOrDefault(x => x.Ptr == selectedPtr) ?? sameId.FirstOrDefault(x => x.State == 2) ?? sameId.FirstOrDefault();
            if (selected == null) return sb.ToString();
            sb.AppendLine();
            sb.AppendLine("[selected bounty]");
            sb.AppendLine("ptr=0x" + selected.Ptr.ToInt64().ToString("X") + " state=" + selected.State + " name=" + selected.DisplayName);
            sb.AppendLine("head=" + HexBytes(TryReadBytes(selected.Ptr, 0x400), 0x400));
            List<BountyProgressInfo> progress = ReadBountyProgress(selected.Ptr);
            sb.AppendLine();
            sb.AppendLine("[progress records]");
            foreach (BountyProgressInfo p in progress)
            {
                sb.AppendLine("#" + p.Index + " ptr=0x" + p.Ptr.ToInt64().ToString("X") + " type=" + p.Type + " total=" + p.Total + " remaining=" + p.Remaining +
                    " interval=" + p.Interval + " flag82=" + p.Flag82 + " flag83=" + p.Flag83 + " owner=0x" + p.Owner.ToInt64().ToString("X"));
                sb.AppendLine("   raw=" + HexBytes(TryReadBytes(p.Ptr, 0xF8), 0xF8));
                if (p.Owner != IntPtr.Zero)
                    sb.AppendLine("   ownerHead=" + HexBytes(TryReadBytes(p.Owner, 0xC0), 0xC0));
            }
            sb.AppendLine();
            sb.AppendLine("[completion-condition snapshot]");
            sb.AppendLine("mode318=" + ReadI32(Add(selected.Ptr, 0x318)) +
                " flag31C=" + ReadBytes(Add(selected.Ptr, 0x31C), 1)[0] +
                " flag31D=" + ReadBytes(Add(selected.Ptr, 0x31D), 1)[0] +
                " field320=" + ReadI32(Add(selected.Ptr, 0x320)) +
                " state324=" + ReadI32(Add(selected.Ptr, 0x324)) +
                " flag32C=" + ReadBytes(Add(selected.Ptr, 0x32C), 1)[0]);
            sb.AppendLine("condition@+0x78=" + HexBytes(TryReadBytes(Add(selected.Ptr, 0x78), 0x30), 0x30));
            sb.AppendLine("condition@+0x2B8=" + HexBytes(TryReadBytes(Add(selected.Ptr, 0x2B8), 0x40), 0x40));
            sb.AppendLine("condition@+0x2E8=" + HexBytes(TryReadBytes(Add(selected.Ptr, 0x2E8), 0x40), 0x40));

            IntPtr mgr = EnsureBountyManager();
            int evalCount = ReadI32(Add(mgr, OFF_ManagerEvalCount));
            int evalCap = ReadI32(Add(mgr, OFF_ManagerEvalCapacity));
            IntPtr evalArr = ReadPtr(Add(mgr, OFF_ManagerEvalArray));
            sb.AppendLine();
            sb.AppendLine("[manager reevaluation queue]");
            sb.AppendLine("array=0x" + evalArr.ToInt64().ToString("X") + " count=" + evalCount + " capacity=" + evalCap);
            if (evalArr != IntPtr.Zero && evalCount > 0 && evalCount <= 256)
            {
                for (int i = 0; i < evalCount; i++)
                {
                    IntPtr q = ReadPtr(Add(evalArr, i * 8L));
                    sb.AppendLine("#" + i + " ptr=0x" + q.ToInt64().ToString("X") + (q == selected.Ptr ? "  <selected>" : ""));
                }
            }

            sb.AppendLine();
            sb.AppendLine("[manager head]");
            sb.AppendLine(HexBytes(TryReadBytes(mgr, 0x900), 0x900));
            sb.AppendLine();
            sb.AppendLine("NOTE: v0.1.0 never forces AddBountyStateEvent(event=5/6) and does not remote-call EvaluateBounty. Completion writes the measured Progress+0x70 counters and appends the bounty to BountyManager+0x6F0 for game-thread evaluation.");
            return sb.ToString();
        }

        private Dictionary<int, float> SnapshotMovementParams()
        {
            int[] ids = new int[]
            {
                0x26F, 0x270, 0x271, 0x272, 0x273, 0x274,
                0x27F, 0x280, 0x282, 0x286, 0x288, 0x294, 0x296, 0x29B, 0x29D
            };
            Dictionary<int, float> result = new Dictionary<int, float>();
            foreach (int id in ids)
            {
                foreach (IntPtr table in ResolveParameterTables())
                {
                    try
                    {
                        IntPtr address = ResolveParameterValueAddress(table, id);
                        if (address != IntPtr.Zero)
                        {
                            result[id] = ReadFloat(address);
                            break;
                        }
                    }
                    catch { }
                }
            }
            return result;
        }

        private static string MovementName(int id)
        {
            switch (id)
            {
                case 0x26F: return "MoveStrafeMaxSpeed";
                case 0x270: return "MoveForwardMinSpeed";
                case 0x271: return "MoveSprintSpeed";
                case 0x272: return "MoveStrafeMinSpeed";
                case 0x273: return "MoveOverSpeedSprintDeceleration";
                case 0x274: return "MoveOverSpeedMaxSpeed";
                case 0x27F: return "JumpMinHeightNoFitnessFactor";
                case 0x280: return "JumpMinHeight";
                case 0x282: return "JumpMaxHeightNoFitnessFactor";
                case 0x286: return "LongJumpMaxJumpHeightMul";
                case 0x288: return "MoveSprintAcceleration";
                case 0x294: return "WallrunSpeed";
                case 0x296: return "MoveSpeedFitnessFactor";
                case 0x29B: return "MovementSpeedMul";
                case 0x29D: return "JumpMaxHeight";
                default: return "Param";
            }
        }

        internal string CaptureFeatureProbe(string kind, bool compare)
        {
            EnsureConnected();
            if (String.IsNullOrWhiteSpace(kind)) kind = "Unknown";
            IntPtr player = GetPlayerObject();
            byte[] playerBytes = TryReadBytes(player, 0x2000);
            Dictionary<int, float> param = SnapshotMovementParams();
            Dictionary<string, ProbeModuleSnapshot> modules = SnapshotProbeModules();
            bool inventoryKind = kind.IndexOf("武器", StringComparison.OrdinalIgnoreCase) >= 0 || kind.IndexOf("库存", StringComparison.OrdinalIgnoreCase) >= 0 || kind.IndexOf("Hellraid", StringComparison.OrdinalIgnoreCase) >= 0;
            bool deepPlayerKind = !inventoryKind && (kind.IndexOf("奔跑", StringComparison.OrdinalIgnoreCase) >= 0 || kind.IndexOf("跳跃", StringComparison.OrdinalIgnoreCase) >= 0 || kind.IndexOf("隐身", StringComparison.OrdinalIgnoreCase) >= 0 || kind.IndexOf("Stealth", StringComparison.OrdinalIgnoreCase) >= 0);
            Dictionary<long, byte[]> inventory = inventoryKind ? SnapshotInventoryGraph(player) : null;
            Dictionary<long, byte[]> playerGraph = deepPlayerKind ? SnapshotPlayerGraph(player) : null;
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("DL1TaskAssistant v0.1.1 deep feature A/B probe");
            sb.AppendLine("kind=" + kind);
            sb.AppendLine("pid=" + ProcessId);
            sb.AppendLine("gamedll=0x" + _dllBase.ToInt64().ToString("X"));
            sb.AppendLine("root=0x" + _root.ToInt64().ToString("X"));
            sb.AppendLine("player=0x" + player.ToInt64().ToString("X"));
            sb.AppendLine("mode=" + (compare ? "B/compare" : "A/baseline"));
            sb.AppendLine();

            if (!compare)
            {
                lock (_featureSync)
                {
                    _featureProbePlayer[kind] = playerBytes == null ? null : (byte[])playerBytes.Clone();
                    _featureProbeParams[kind] = new Dictionary<int, float>(param);
                    _featureProbeModules[kind] = modules;
                    if (inventoryKind) _featureProbeInventory[kind] = inventory;
                    if (deepPlayerKind) _featureProbePlayerGraph[kind] = playerGraph;
                }
                sb.AppendLine("Baseline captured. For run/jump toggle ONLY the corresponding FLiNG option; for inventory acquire exactly one item normally in-game, then run B.");
                sb.AppendLine("Captured module .text snapshots: " + String.Join(", ", modules.Keys.ToArray()));
                if (inventoryKind) sb.AppendLine("Captured inventory/IPlayerCheat/IInventoryOwner heap graph objects: " + (inventory == null ? 0 : inventory.Count));
                if (deepPlayerKind) sb.AppendLine("Captured deep player graph objects: " + (playerGraph == null ? 0 : playerGraph.Count));
            }
            else
            {
                byte[] oldBytes = null;
                Dictionary<int, float> oldParams = null;
                Dictionary<string, ProbeModuleSnapshot> oldModules = null;
                Dictionary<long, byte[]> oldInventory = null;
                Dictionary<long, byte[]> oldPlayerGraph = null;
                lock (_featureSync)
                {
                    _featureProbePlayer.TryGetValue(kind, out oldBytes);
                    _featureProbeParams.TryGetValue(kind, out oldParams);
                    _featureProbeModules.TryGetValue(kind, out oldModules);
                    if (inventoryKind) _featureProbeInventory.TryGetValue(kind, out oldInventory);
                    if (deepPlayerKind) _featureProbePlayerGraph.TryGetValue(kind, out oldPlayerGraph);
                }
                if (oldBytes == null) sb.AppendLine("ERROR: baseline A missing.");
                else if (playerBytes == null) sb.AppendLine("ERROR: current player memory unreadable.");
                else
                {
                    sb.AppendLine("[player byte changes, offsets from player]");
                    AppendChangedRuns(sb, oldBytes, playerBytes, 0, 256, "+");
                }

                sb.AppendLine();
                sb.AppendLine("[movement parameter changes]");
                if (oldParams == null) sb.AppendLine("<baseline parameters missing>");
                else
                {
                    int movementChanges = 0;
                    foreach (int id in oldParams.Keys.Union(param.Keys).OrderBy(x => x))
                    {
                        float a, b;
                        bool ha = oldParams.TryGetValue(id, out a);
                        bool hb = param.TryGetValue(id, out b);
                        if (!ha || !hb || Math.Abs(a - b) > 0.0001F)
                        {
                            sb.Append("0x").Append(id.ToString("X")).Append(' ').Append(MovementName(id))
                              .Append(": A=").Append(ha ? a.ToString("R") : "<missing>")
                              .Append(" B=").Append(hb ? b.ToString("R") : "<missing>").AppendLine();
                            movementChanges++;
                        }
                    }
                    if (movementChanges == 0) sb.AppendLine("<no movement-parameter changes>");
                }

                sb.AppendLine();
                sb.AppendLine("[loaded module .text changes - FLiNG/code-hook detector]");
                if (oldModules == null) sb.AppendLine("<baseline module snapshots missing>");
                else
                {
                    foreach (string moduleName in oldModules.Keys.Union(modules.Keys).OrderBy(x => x))
                    {
                        ProbeModuleSnapshot a, b;
                        bool ha = oldModules.TryGetValue(moduleName, out a);
                        bool hb = modules.TryGetValue(moduleName, out b);
                        sb.AppendLine(moduleName + ":");
                        if (!ha || !hb || a == null || b == null) { sb.AppendLine("  <snapshot missing in A or B>"); continue; }
                        sb.AppendLine("  A base=0x" + a.Base.ToString("X") + " textRva=0x" + a.TextRva.ToString("X") + " size=0x" + a.Text.Length.ToString("X"));
                        sb.AppendLine("  B base=0x" + b.Base.ToString("X") + " textRva=0x" + b.TextRva.ToString("X") + " size=0x" + b.Text.Length.ToString("X"));
                        AppendModuleChangedRuns(sb, a, b);
                    }
                }

                if (inventoryKind)
                {
                    sb.AppendLine();
                    AppendInventoryGraphDiff(sb, oldInventory, inventory, player);
                }
                if (deepPlayerKind)
                {
                    sb.AppendLine();
                    AppendPlayerGraphDiff(sb, oldPlayerGraph, playerGraph, player);
                }
            }

            if (kind.IndexOf("奔跑", StringComparison.OrdinalIgnoreCase) >= 0 || kind.IndexOf("Run", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                sb.AppendLine();
                AppendKnownFlingRunHookStatus(sb);
            }

            sb.AppendLine();
            sb.AppendLine("[current movement parameters]");
            foreach (KeyValuePair<int, float> x in param.OrderBy(x => x.Key))
                sb.Append("0x").Append(x.Key.ToString("X")).Append(' ').Append(MovementName(x.Key))
                  .Append('=').Append(x.Value.ToString("R")).AppendLine();

            sb.AppendLine();
            sb.AppendLine("player+0x8F8=" + HexBytes(TryReadBytes(Add(player, 0x8F8), 0x80), 0x80));
            sb.AppendLine("player+0x10B4=" + HexBytes(TryReadBytes(Add(player, 0x10B4), 0x40), 0x40));
            sb.AppendLine("NOTE: v0.1.0 does not write player+0x10B4. Stealth is implemented through the supplied XXXeLxxx 1.55 code hook at gamedll+0x6C4D22.");
            sb.AppendLine("NOTE: module A/B covers DyingLightGame.exe/renderer/filesystem when loaded. Super Run additionally reports its two known FLiNG patch sites on every capture, even when A/B byte-diff is empty.");
            if (inventoryKind) sb.AppendLine("NOTE: RTTI proves player+0x8F8 is the IPlayerCheat base subobject, NOT the general inventory pointer; IInventoryOwner/IInventoryObserver is at player+0x930. Inventory::AddItem RDX is an Item/Definition object, so raw strings remain disabled.");
            return sb.ToString();
        }

        private static List<long> FindPattern(byte[] haystack, byte[] needle, long baseAddress, int max)
        {
            List<long> result = new List<long>();
            if (haystack == null || needle == null || needle.Length == 0) return result;
            for (int i = 0; i <= haystack.Length - needle.Length && result.Count < max; i++)
            {
                bool ok = true;
                for (int j = 0; j < needle.Length; j++) if (haystack[i + j] != needle[j]) { ok = false; break; }
                if (ok) result.Add(baseAddress + i);
            }
            return result;
        }

        private List<long> SearchReadableWindows(byte[] needle, int max)
        {
            List<long> hits = new List<long>();
            List<IntPtr> roots = new List<IntPtr>();
            try { roots.Add(_root); } catch { }
            try { roots.Add(GetPlayerObject()); } catch { }
            if (_bountyManager != IntPtr.Zero) roots.Add(_bountyManager);

            // Search the root/player objects and the first 0x1200 bytes of pointer-like children.
            foreach (IntPtr owner in roots.Where(x => x != IntPtr.Zero).Distinct())
            {
                byte[] raw = TryReadBytes(owner, 0x2000);
                if (raw != null) hits.AddRange(FindPattern(raw, needle, owner.ToInt64(), max - hits.Count));
                if (hits.Count >= max) break;

                byte[] ptrs = TryReadBytes(owner, 0x1200);
                if (ptrs == null) continue;
                HashSet<long> seen = new HashSet<long>();
                for (int off = 0; off + 8 <= ptrs.Length && hits.Count < max; off += 8)
                {
                    long q = BitConverter.ToInt64(ptrs, off);
                    if (q < 0x10000 || !seen.Add(q)) continue;
                    byte[] child = TryReadBytes(new IntPtr(q), 0x1000);
                    if (child == null) continue;
                    hits.AddRange(FindPattern(child, needle, q, max - hits.Count));
                }
            }
            return hits.Take(max).ToList();
        }

        private void AppendRootPointers(StringBuilder sb)
        {
            sb.AppendLine("[root pointer-like children]");
            byte[] raw = TryReadBytes(_root, 0x1200);
            if (raw == null) { sb.AppendLine("<root unreadable>"); return; }
            int shown = 0;
            for (int off = 0; off + 8 <= raw.Length && shown < 160; off += 8)
            {
                long q = BitConverter.ToInt64(raw, off);
                if (q < 0x10000) continue;
                byte[] head = TryReadBytes(new IntPtr(q), 0x40);
                if (head == null) continue;
                sb.Append("root+0x").Append(off.ToString("X3")).Append(" -> 0x")
                  .Append(q.ToString("X")).Append("  head=").Append(HexBytes(head, 32)).AppendLine();
                shown++;
            }
        }

        private void AppendQuestManagerCoreLayout(StringBuilder sb, IntPtr manager, byte[] needle)
        {
            sb.AppendLine("[QuestManager exact singleton core layout]");
            if (manager == IntPtr.Zero) { sb.AppendLine("<singleton vtable verification failed>"); return; }
            byte[] core = TryReadFlexible(manager, 0x220);
            if (core == null) { sb.AppendLine("<unreadable>"); return; }
            sb.AppendLine("manager=0x" + manager.ToInt64().ToString("X") + " head=" + HexBytes(core, 0x220));
            int[] fields = new int[] { 0x08, 0x20, 0x38, 0x50, 0x68, 0x70, 0x98, 0xA0, 0xA8, 0xC0, 0xC8, 0xE0, 0xE8, 0x100, 0x108, 0x118, 0x138, 0x140, 0x150, 0x158, 0x160, 0x170, 0x178, 0x180, 0x190, 0x1A8, 0x1C0, 0x1C8, 0x1D0, 0x1D8, 0x1E8, 0x1F0 };
            foreach (int off in fields)
            {
                if (off + 8 > core.Length) continue;
                long q = BitConverter.ToInt64(core, off);
                if (!LooksLikeHeapPointer(q)) continue;
                byte[] child = TryReadFlexible(new IntPtr(q), 0x3000);
                if (child == null) continue;
                List<long> hits = needle == null || needle.Length == 0 ? new List<long>() : FindPattern(child, needle, q, 16);
                sb.AppendLine("  +0x" + off.ToString("X") + " -> 0x" + q.ToString("X") + " size=0x" + child.Length.ToString("X") + " keyHits=" + hits.Count + " head=" + HexBytes(child, 0x80));
                foreach (long h in hits) sb.AppendLine("    hit=0x" + h.ToString("X") + " child+0x" + (h-q).ToString("X"));
            }
            sb.AppendLine("static RE note: current-build callers treat QuestManager+0x20 and +0x38/+0x40 as quest containers/lists; this dump is read-only and records their live shape.");
        }

        internal string BuildQuestRuntimeProbe(string category, string name, string key)
        {
            EnsureConnected();
            _root = ReadPtr(Add(_dllBase, OFF_GameRootGlobal));
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("DL1TaskAssistant v0.1.1 deep Quest runtime probe");
            sb.AppendLine("category=" + category);
            sb.AppendLine("name=" + name);
            sb.AppendLine("key=" + key);
            sb.AppendLine("pid=" + ProcessId);
            sb.AppendLine("gamedll=0x" + _dllBase.ToInt64().ToString("X") + " size=0x" + _dllSize.ToString("X"));
            sb.AppendLine("root=0x" + _root.ToInt64().ToString("X"));
            try { sb.AppendLine("player=0x" + GetPlayerObject().ToInt64().ToString("X")); } catch (Exception ex) { sb.AppendLine("player=<" + ex.Message + ">"); }
            try { sb.AppendLine("bountyManager=0x" + EnsureBountyManager().ToInt64().ToString("X")); } catch (Exception ex) { sb.AppendLine("bountyManager=<" + ex.Message + ">"); }

            IntPtr importTarget = IntPtr.Zero;
            try { importTarget = ReadPtr(Add(_dllBase, RVA_IAT_GetIQuestManager)); } catch { }
            List<IntPtr> questManagers = FindQuestManagerCandidatesByVtable();
            IntPtr questManager = questManagers.Count > 0 ? questManagers[0] : IntPtr.Zero;
            IntPtr exactSingleton = GetVerifiedQuestManagerSingleton();
            sb.AppendLine();
            sb.AppendLine("[QuestManager exact current-build locator - no native call]");
            sb.AppendLine("IGame::GetIQuestManager IAT slot=gamedll+0x" + RVA_IAT_GetIQuestManager.ToString("X") + " target=0x" + importTarget.ToInt64().ToString("X"));
            sb.AppendLine("NOTE: current engine base export is a null-return stub; v0.1.0 does not call it.");
            sb.AppendLine("constructor/static RE: gamedll+0xE00560 returns inline singleton gamedll+0x" + RVA_QuestManagerSingleton.ToString("X"));
            sb.AppendLine("exact singleton=0x" + exactSingleton.ToInt64().ToString("X") + " vtableVerified=" + (exactSingleton != IntPtr.Zero));
            sb.AppendLine("expected Quests::QuestManager primary vtable=0x" + Add(_dllBase, RVA_QuestManagerPrimaryVtable).ToInt64().ToString("X"));
            sb.AppendLine("live candidates=" + questManagers.Count);
            foreach (IntPtr q in questManagers) sb.AppendLine("  0x" + q.ToInt64().ToString("X") + " head=" + HexBytes(TryReadBytes(q, 0x80), 0x80));

            byte[] ascii = String.IsNullOrWhiteSpace(key) ? new byte[0] : Encoding.ASCII.GetBytes(key);
            sb.AppendLine();
            AppendQuestManagerCoreLayout(sb, exactSingleton, ascii);
            byte[] utf16 = String.IsNullOrWhiteSpace(key) ? new byte[0] : Encoding.Unicode.GetBytes(key);
            if (ascii.Length > 0)
            {
                List<long> a = SearchReadableWindows(ascii, 64);
                List<long> u = SearchReadableWindows(utf16, 64);
                sb.AppendLine();
                sb.AppendLine("[legacy broad runtime key hits ASCII] " + a.Count);
                foreach (long x in a) sb.AppendLine("0x" + x.ToString("X"));
                sb.AppendLine("[legacy broad runtime key hits UTF16] " + u.Count);
                foreach (long x in u) sb.AppendLine("0x" + x.ToString("X"));

                IntPtr c00 = IntPtr.Zero;
                try { c00 = ReadPtr(Add(_root, 0xC00)); } catch { }
                sb.AppendLine();
                AppendKeyHitsInRegion(sb, "root+0xC00 candidate quest container", c00, 0x8000, ascii);
                sb.AppendLine();
                AppendKeyHitsInRegion(sb, "IQuestManager direct object window", questManager, 0x8000, ascii);
                sb.AppendLine();
                AppendQuestManagerPointerChildren(sb, questManager, ascii);

                try
                {
                    byte[] file = File.ReadAllBytes(_dllPath);
                    List<long> fa = FindPattern(file, ascii, 0, 64);
                    sb.AppendLine();
                    sb.AppendLine("[gamedll file offsets ASCII] " + fa.Count);
                    foreach (long x in fa) sb.AppendLine("file+0x" + x.ToString("X"));
                }
                catch (Exception ex) { sb.AppendLine("gamedll file scan failed: " + ex.Message); }
            }

            sb.AppendLine();
            AppendRootPointers(sb);
            sb.AppendLine();
            sb.AppendLine("[interpretation hints from the uploaded v0.0.4 probes]");
            sb.AppendLine("Prologue had live ASCII hits; one was inside the object referenced by root+0xC00. BringDawud also had a live hit. The sampled repeat/challenge keys had no exact live key hit, so key strings alone cannot be used as universal Quest identity.");
            sb.AppendLine("No quest state is modified by this probe.");
            return sb.ToString();
        }

        internal string BuildQuestOverviewProbe()
        {
            EnsureConnected();
            _root = ReadPtr(Add(_dllBase, OFF_GameRootGlobal));
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("DL1TaskAssistant v0.1.1 Quest overview probe");
            sb.AppendLine("pid=" + ProcessId);
            sb.AppendLine("gamedll=0x" + _dllBase.ToInt64().ToString("X") + " size=0x" + _dllSize.ToString("X"));
            sb.AppendLine("root=0x" + _root.ToInt64().ToString("X"));
            IntPtr getter = IntPtr.Zero; try { getter = ReadPtr(Add(_dllBase, RVA_IAT_GetIQuestManager)); } catch { }
            List<IntPtr> managers = FindQuestManagerCandidatesByVtable();
            IntPtr exactSingleton = GetVerifiedQuestManagerSingleton();
            sb.AppendLine("GetIQuestManager IAT target=0x" + getter.ToInt64().ToString("X") + " (base export is null-return stub; not called)");
            sb.AppendLine("QuestManager exact singleton=gamedll+0x" + RVA_QuestManagerSingleton.ToString("X") + " -> 0x" + exactSingleton.ToInt64().ToString("X") + " vtableVerified=" + (exactSingleton != IntPtr.Zero));
            sb.AppendLine("QuestManager vtable candidates=" + managers.Count);
            foreach (IntPtr manager in managers) sb.AppendLine("QuestManager candidate=0x" + manager.ToInt64().ToString("X") + " head=" + HexBytes(TryReadBytes(manager,0x200),0x200));
            AppendQuestManagerCoreLayout(sb, exactSingleton, new byte[0]);
            IntPtr c00=IntPtr.Zero; try { c00=ReadPtr(Add(_root,0xC00)); } catch { }
            sb.AppendLine("root+0xC00=0x" + c00.ToInt64().ToString("X") + " head=" + HexBytes(TryReadBytes(c00,0x200),0x200));
            sb.AppendLine();
            AppendRootPointers(sb);
            sb.AppendLine();
            sb.AppendLine("TIP: main/side quests are already complete on the current save, so active-Quest sampling is no longer required. Current reverse-engineering priority is the live bounty progress chain.");
            return sb.ToString();
        }

        internal string BuildBlueprintProbe()
        {
            EnsureConnected();
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("DL1TaskAssistant v0.1.1 Blueprint / CraftPlan probe");
            sb.AppendLine("pid=" + ProcessId);
            sb.AppendLine("gamedll=" + _dllPath);
            try
            {
                byte[] file = File.ReadAllBytes(_dllPath);
                HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i + 10 < file.Length; i++)
                {
                    if (file[i] != (byte)'C' && file[i] != (byte)'c') continue;
                    int j = i;
                    while (j < file.Length && j - i < 160 && file[j] >= 32 && file[j] < 127) j++;
                    if (j - i < 8) continue;
                    string s = Encoding.ASCII.GetString(file, i, j - i);
                    if (s.IndexOf("Craftplan_", StringComparison.OrdinalIgnoreCase) == 0 ||
                        s.IndexOf("CraftPlan_", StringComparison.OrdinalIgnoreCase) == 0)
                        names.Add(s);
                }
                sb.AppendLine("static CraftPlan identifiers=" + names.Count);
                foreach (string x in names.OrderBy(x => x)) sb.AppendLine(x);
            }
            catch (Exception ex) { sb.AppendLine("static scan failed: " + ex.Message); }

            sb.AppendLine();
            sb.AppendLine("Current build also contains Crafting::Blueprint, SearchBlueprints and SEventCraftPlanObtained symbols.");
            sb.AppendLine("This probe does NOT mark blueprints collected yet; send this file back so the runtime blueprint container can be mapped without guessing.");
            return sb.ToString();
        }

        public void Dispose() { Disconnect(); }
    }
}
