﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DL1Assistant
{
    internal sealed class RuntimeInventoryItem
    {
        internal IntPtr Address;
        internal IntPtr Definition;
        internal string InternalId;
        internal string LocalizedName;
        internal string Tab;
        internal int Count;
        internal int Quality;
        internal int Repairs;
        internal float Durability;
        internal float BaseDamage;
        internal bool Weapon;
        public override string ToString()
        {
            return (String.IsNullOrEmpty(LocalizedName) ? InternalId : LocalizedName) + "  [" + InternalId + "]";
        }
    }

    internal sealed class BlueprintRuntimeRecord
    {
        internal string Id;
        internal int MemoryIndex;
        internal bool InCollectedList;
        internal bool DefinitionFound;
    }

    internal sealed partial class GameClient
    {
        // IMIG -4.5P1 "开背包刷新" uses exactly this unique AOB in Steam 1.55.
        // Capture the original RAX only while the game's own inventory code runs.
        // No speculative pointer-chain writes and no repeated memory scans.
        private const long RVA_IMIG_BackpackCapture = 0xBB5C09L;
        private RuntimePatch _v012InventoryHook;
        private sealed class OriginalDamageV012
        {
            internal IntPtr Definition;
            internal string Id;
            internal float Value;
        }
        private readonly Dictionary<long, OriginalDamageV012> _v012OriginalDefinitionDamage =
            new Dictionary<long, OriginalDamageV012>();

        internal void EnsureInventoryCaptureV012()
        {
            EnsureConnected();
            if (_v012InventoryHook != null) return;
            byte[] orig = { 0x4C, 0x8B, 0x68, 0x60, 0x4D, 0x85, 0xED };
            RuntimePatch p = InstallCavePatch(RVA_IMIG_BackpackCapture, orig, delegate(IntPtr cave)
            {
                List<byte> code = new List<byte>();
                // mov [rip+21],rax ; original 7 bytes ; absolute return jump 14 ; dq 0
                code.AddRange(new byte[] { 0x48, 0x89, 0x05, 0x15, 0, 0, 0 });
                code.AddRange(orig);
                code.AddRange(AbsJump(Add(_dllBase, RVA_IMIG_BackpackCapture + orig.Length).ToInt64()));
                code.AddRange(new byte[8]);
                return code.ToArray();
            }, 0x1000);
            p.Data = Add(p.Cave, 28);
            _v012InventoryHook = p;
        }

        private IntPtr CapturedInventoryContainerV012()
        {
            EnsureInventoryCaptureV012();
            IntPtr p = new IntPtr(BitConverter.ToInt64(ReadBytes(_v012InventoryHook.Data, 8), 0));
            if (p == IntPtr.Zero) throw new InvalidOperationException("背包指针尚未捕获：请在游戏中打开一次背包，再点刷新。首次打开请保持 1 秒。此操作不会修改游戏数据。");
            int valid = 0;
            foreach (int off in new int[] { 0x58, 0x60, 0x68 })
            {
                try
                {
                    IntPtr sub = ReadPtr(Add(p, off));
                    if (sub == IntPtr.Zero) continue;
                    IntPtr list = Add(sub, 0x40);
                    int count = ReadI32(Add(list, 8));
                    IntPtr array = ReadPtr(list);
                    if (count >= 0 && count <= 8192 && (count == 0 || array != IntPtr.Zero)) valid++;
                }
                catch { }
            }
            if (valid < 2) throw new InvalidOperationException("IMIG 背包根指针已捕获，但列表结构尚未就绪。请关闭并重新打开游戏背包，然后再刷新；不会用未验证的指针写入。");
            return p;
        }

        internal List<RuntimeInventoryItem> ReadInventoryV012()
        {
            EnsureConnected();
            IntPtr container = CapturedInventoryContainerV012();
            List<RuntimeInventoryItem> result = new List<RuntimeInventoryItem>();
            HashSet<long> seen = new HashSet<long>();
            foreach (int off in new int[] { 0x60, 0x68, 0x58 })
            {
                string tab = off == 0x58 ? "材料/消耗品" : off == 0x60 ? "武器" : "特殊/装备";
                bool weapon = off != 0x58;
                IntPtr sub;
                try { sub = ReadPtr(Add(container, off)); } catch { continue; }
                if (sub == IntPtr.Zero) continue;
                IntPtr list = Add(sub, 0x40L);
                IntPtr array;
                int count;
                try { array = ReadPtr(list); count = ReadI32(Add(list, 8)); } catch { continue; }
                if (array == IntPtr.Zero || count < 0 || count > 8192) continue;
                for (int i = 0; i < count; ++i)
                {
                    try
                    {
                        IntPtr item = ReadPtr(Add(array, i * 8L));
                        if (item == IntPtr.Zero || !seen.Add(item.ToInt64())) continue;
                        IntPtr definition = ReadPtr(Add(item, 0x60));
                        if (definition == IntPtr.Zero) continue;
                        string id = ReadAsciiZ(ReadPtr(Add(definition, 0x18)), 160);
                        if (String.IsNullOrEmpty(id) || id.Length > 160 || !System.Text.RegularExpressions.Regex.IsMatch(id, "^[A-Za-z0-9_]+$")) continue;
                        RuntimeInventoryItem entry = new RuntimeInventoryItem();
                        entry.Address = item;
                        entry.Definition = definition;
                        entry.InternalId = id;
                        entry.Tab = tab;
                        entry.Weapon = weapon;
                        entry.Count = ReadI32(Add(item, 0x40));
                        entry.Quality = ReadBytes(Add(item, 0x68), 1)[0];
                        if (weapon)
                        {
                            entry.Durability = ReadFloat(Add(item, 0x44));
                            entry.Repairs = ReadI32(Add(item, 0x48));
                            long dmgOffset = id.StartsWith("Firearm_", StringComparison.OrdinalIgnoreCase) ? 0x354L : 0x240L;
                            try { entry.BaseDamage = ReadFloat(Add(definition, dmgOffset)); } catch { }
                        }
                        try
                        {
                            entry.LocalizedName = ReadWideStringV012(ReadPtr(Add(definition, 0x10)), 128);
                        }
                        catch { }
                        if (entry.Count < 0 || entry.Count > 100000000 || entry.Quality < 0 || entry.Quality > 5) continue;
                        result.Add(entry);
                    }
                    catch { } // One malformed game-owned item must not erase another row.
                }
            }
            return result;
        }

        private string ReadWideStringV012(IntPtr ptr, int maxChars)
        {
            if (ptr == IntPtr.Zero) return "";
            byte[] b = new byte[0];
            // Native language strings can end at a page boundary. Do not discard a
            // valid short name because reading all 256 bytes crossed into an invalid page.
            for (int probe = Math.Min(maxChars, 128); probe >= 8; probe /= 2)
            {
                try { b = ReadBytes(ptr, probe * 2); break; }
                catch { }
            }
            if (b.Length == 0) return "";
            int length = 0;
            for (; length + 1 < b.Length; length += 2) if (b[length] == 0 && b[length + 1] == 0) break;
            return Encoding.Unicode.GetString(b, 0, length).Trim();
        }

        // IMIG inventory editor: quantity +0x40, durability +0x44, repairs +0x48,
        // quality +0x68, base definition pointer +0x60. Validate live item ID before writing.
        internal string EditInventoryV012(RuntimeInventoryItem selected, int quantity, int quality,
            float durability, int repairs, float definitionDamage, bool writeDefinitionDamage)
        {
            EnsureConnected();
            if (selected == null || selected.Address == IntPtr.Zero) throw new InvalidOperationException("未选中物品。");
            IntPtr currentDefinition = ReadPtr(Add(selected.Address, 0x60));
            string id = ReadAsciiZ(ReadPtr(Add(currentDefinition, 0x18)), 160);
            if (currentDefinition != selected.Definition || !String.Equals(id, selected.InternalId, StringComparison.Ordinal))
                throw new InvalidOperationException("背包项目已变化，已拒绝写入。请刷新后重试。");
            if (quantity < 1 || quantity > 999999) throw new InvalidOperationException("数量必须为 1–999999。");
            if (quality < 0 || quality > 5) throw new InvalidOperationException("品质必须为 0–5（橙色=4）。");
            if (selected.Weapon && (Single.IsNaN(durability) || Single.IsInfinity(durability) || durability < 1 || durability > 100000 || repairs < 0 || repairs > 999))
                throw new InvalidOperationException("耐久必须为 1–100000，修理次数 0–999。");
            if (writeDefinitionDamage && (!selected.Weapon || Single.IsNaN(definitionDamage) || Single.IsInfinity(definitionDamage) || definitionDamage < 1 || definitionDamage > 50000))
                throw new InvalidOperationException("定义基础伤害可设置范围：1–50000，仅武器可用。");
            WriteI32(Add(selected.Address, 0x40), quantity);
            WriteBytes(Add(selected.Address, 0x68), new byte[] { (byte)quality });
            if (selected.Weapon)
            {
                WriteFloat(Add(selected.Address, 0x44), durability);
                WriteI32(Add(selected.Address, 0x48), repairs);
                if (writeDefinitionDamage)
                {
                    long dmgOffset = selected.InternalId.StartsWith("Firearm_", StringComparison.OrdinalIgnoreCase) ? 0x354L : 0x240L;
                    IntPtr location = Add(selected.Definition, dmgOffset);
                    long key = location.ToInt64();
                    if (!_v012OriginalDefinitionDamage.ContainsKey(key))
                        _v012OriginalDefinitionDamage[key] = new OriginalDamageV012 {
                            Definition = selected.Definition, Id = selected.InternalId,
                            Value = ReadFloat(location)
                        };
                    WriteFloat(location, definitionDamage);
                }
            }
            return "游戏内读写已完成：" + id + "；数量=" + quantity + "；品质=" + quality +
                (selected.Weapon ? "；耐久=" + durability.ToString("0.##") + "；修理=" + repairs : "") +
                (writeDefinitionDamage ? "；基础伤害=" + definitionDamage.ToString("0.##") + "（同型号共享，本次运行有效）" : "");
        }

        // Returns true only if the actual AddItem return pointer is a readable inventory item.
        // Gray/white item definitions do NOT imply orange loot: the per-instance quality is +0x68.
        private bool TryMakeGivenWeaponOrangeV012(long receipt, string expectedId)
        {
            if (receipt < 0x10000L || receipt > 0x7FFFFFFFFFFFL) return false;
            try
            {
                IntPtr item = new IntPtr(receipt);
                IntPtr definition = ReadPtr(Add(item, 0x60));
                string current = ReadAsciiZ(ReadPtr(Add(definition, 0x18)), 160);
                if (!String.Equals(current, expectedId, StringComparison.OrdinalIgnoreCase)) return false;
                WriteBytes(Add(item, 0x68), new byte[] { 4 });
                return ReadBytes(Add(item, 0x68), 1)[0] == 4;
            }
            catch { return false; }
        }

        internal List<BlueprintRuntimeRecord> GetBlueprintRuntimeV012(HashSet<int> collected)
        {
            EnsureConnected();
            EnsureBaseItemCatalogV010();
            List<BlueprintRuntimeRecord> result = new List<BlueprintRuntimeRecord>();
            foreach (KeyValuePair<string, IntPtr> pair in _v009BaseItems)
            {
                if (!pair.Key.StartsWith("Craftplan_", StringComparison.OrdinalIgnoreCase)) continue;
                int index = -1;
                try { index = ReadI32(Add(pair.Value, OFF_BaseItemIndex)); } catch { }
                result.Add(new BlueprintRuntimeRecord {
                    Id = pair.Key, MemoryIndex = index, DefinitionFound = true,
                    InCollectedList = index >= 0 && collected != null && collected.Contains(index)
                });
            }
            return result.OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // Only the game's own registered Collectable handler can update a learned state.
        // AddItem alone is not a reliable "learn blueprint" operation.
        internal string TryNativeUnlockBlueprintV012(string id)
        {
            EnsureConnected();
            IntPtr definition = ResolveBaseItemV009(id);
            if (definition == IntPtr.Zero) return "未加载定义: " + id;
            int index = ReadI32(Add(definition, OFF_BaseItemIndex));
            HashSet<int> before = GetCollectedIds();
            if (before.Contains(index)) return "已在 collected-ID 中：" + id;
            IntPtr collectable = FindCollectableObjectV010(index);
            if (collectable == IntPtr.Zero || collectable != definition)
                return "游戏未注册对应的蓝图 Collectable：" + id + " (ID " + index + ")；保持只读";
            // RVA 0x6F9E40 is the same BaseItem-index lookup used by IMIG;
            // require exact pointer identity before native award, not just a nonzero result.
            AwardCollectable(index);
            HashSet<int> after = GetCollectedIds();
            return after.Contains(index) ? "已写入并读回：" + id + " (" + index + ")" : "引擎执行后未读回学习标记：" + id + "；不报告已完成";
        }

        private void RestoreInventoryV012()
        {
            foreach (KeyValuePair<long, OriginalDamageV012> pair in _v012OriginalDefinitionDamage)
            {
                try
                {
                    // A definition may be released during DLC/map changes. Never
                    // restore a stale float into a newly allocated unrelated object.
                    string currentId = ReadAsciiZ(ReadPtr(Add(pair.Value.Definition, 0x18)), 160);
                    if (String.Equals(currentId, pair.Value.Id, StringComparison.Ordinal))
                        WriteFloat(new IntPtr(pair.Key), pair.Value.Value);
                }
                catch { }
            }
            _v012OriginalDefinitionDamage.Clear();
            RemovePatch(ref _v012InventoryHook);
        }
    }
}
