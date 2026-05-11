using System;
using System.Collections.Generic;
using Dalamud.Configuration;

namespace AkuItemSets;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public Dictionary<string, CharacterCollectionSnapshot> Characters { get; set; } = new();
    public string SearchText { get; set; } = string.Empty;
    public CollectionFilterMode FilterMode { get; set; } = CollectionFilterMode.All;
    public ItemSetSortMode SortMode { get; set; } = ItemSetSortMode.ItemSetId;
    public SortDirection SortDirection { get; set; } = SortDirection.Descending;
    public bool HideCompletedSets { get; set; }
    public bool ShowOnlyMissingPieces { get; set; }
    public bool IncludeAllClassItemsInRoleFilters { get; set; }
    public bool ShowOnlyCurrentInstanceLoot { get; set; }

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
