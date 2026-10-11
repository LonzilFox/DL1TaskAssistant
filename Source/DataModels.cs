using System;
using System.Collections.Generic;

namespace DL1Assistant
{
    internal sealed class BountyObjectiveCatalogItem
    {
        public string kind { get; set; }
        public int count { get; set; }
        public string nameToken { get; set; }
        public string display { get; set; }
        public string refName { get; set; }
        public string prerequisite { get; set; }
        public bool hidden { get; set; }
    }

    internal sealed class BountyCatalogItem
    {
        public int id { get; set; }
        public string source { get; set; }
        public string type { get; set; }
        public string displayName { get; set; }
        public string poolReward { get; set; }
        public string rewardsDescription { get; set; }
        public string rewardsIcon { get; set; }
        public string nameToken { get; set; }
        public string rewardDisplay { get; set; }
        public List<BountyObjectiveCatalogItem> objectives { get; set; }
    }

    internal sealed class BlueprintCatalogItem
    {
        public string internalId { get; set; }
        public string name { get; set; }
        public string token { get; set; }
        public int uniqueSaveId { get; set; }
        public string source { get; set; }
    }

    internal sealed class CollectibleItem
    {
        public int id { get; set; }
        public string name { get; set; }
        public string internalId { get; set; }
    }

    internal sealed class CollectibleCategory
    {
        public string key { get; set; }
        public string title { get; set; }
        public string scope { get; set; }
        public string description { get; set; }
        public bool supported { get; set; }
        public int plannedCount { get; set; }
        public List<CollectibleItem> items { get; set; }
    }

    internal sealed class CollectibleCatalogRoot
    {
        public string version { get; set; }
        public string source { get; set; }
        public List<CollectibleCategory> categories { get; set; }
    }

    internal sealed class TaskCatalogItem
    {
        public string key { get; set; }
        public string name { get; set; }
        public string source { get; set; }
    }

    internal sealed class TaskCatalogRoot
    {
        public string version { get; set; }
        public Dictionary<string, List<TaskCatalogItem>> categories { get; set; }
    }

    internal sealed class WeaponCatalogItem
    {
        public string category { get; set; }
        public string name { get; set; }
        public string internalId { get; set; }
        public string note { get; set; }

        public override string ToString()
        {
            return String.IsNullOrWhiteSpace(name) ? (internalId ?? "Weapon") : name;
        }
    }

    internal sealed class BountyProgressInfo
    {
        public int Index;
        public IntPtr Ptr;
        public int Type;
        public int Total;
        public int Remaining;
        public IntPtr Owner;
        public int Interval;
        public byte Flag82;
        public byte Flag83;
    }

    internal sealed class BountyInfo
    {
        public int Index;
        public IntPtr Ptr;
        public int Id;
        public int State;
        public int ProgressCount;
        public bool IsCurrent;
        public string Source;
        public string Type;
        public string TypeLabel;
        public string StatusLabel;
        public string DisplayName;
        public string NameToken;
        public string Reward;
        public BountyCatalogItem Catalog;
        public List<BountyProgressInfo> Progress;
    }
}
