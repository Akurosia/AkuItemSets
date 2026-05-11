using System;
using System.Collections.Generic;

namespace AkuItemSets;

public enum CollectionFilterMode
{
    All,
    CurrentRole,
    Tank,
    Healer,
    Melee,
    PhysicalRanged,
    Caster,
    Crafter,
    Gatherer,
}

public enum ItemSetSortMode
{
    Name,
    ItemSetId,
}

public enum SortDirection
{
    Ascending,
    Descending,
}

public enum ItemCollectionSource
{
    Inventory,
    Armoury,
    Saddlebag,
    Retainer,
    GlamourDresser,
    GlamourDresserSet,
    Armoire,
}

public enum ItemCollectionCategory
{
    Inventory,
    Armoury,
    Saddlebag,
    Retainers,
    GlamourDresser,
    Armoire,
}

[Serializable]
public sealed class CharacterCollectionSnapshot
{
    public string CharacterKey { get; set; } = string.Empty;
    public string CharacterName { get; set; } = string.Empty;
    public string WorldName { get; set; } = string.Empty;
    public DateTimeOffset LastScanUtc { get; set; }
    public Dictionary<ItemCollectionCategory, DateTimeOffset> LastScanByCategory { get; set; } = new();
    public Dictionary<string, DateTimeOffset> LastRetainerScanByName { get; set; } = new();
    public Dictionary<uint, ItemOwnership> Items { get; set; } = new();
}

[Serializable]
public sealed class ItemOwnership
{
    public uint ItemId { get; set; }
    public Dictionary<ItemCollectionSource, int> CountsBySource { get; set; } = new();
    public List<string> RetainerNames { get; set; } = new();

    public int TotalCount
    {
        get
        {
            var total = 0;
            foreach (var count in CountsBySource.Values)
            {
                total += count;
            }

            return total;
        }
    }
}

public sealed record ItemSetDefinition(
    uint SetItemId,
    string SetName,
    uint IconId,
    IReadOnlyList<ItemSetPiece> Pieces,
    string? LootSourceName = null,
    string? LootSourceUrl = null,
    IReadOnlyList<string>? LootSourceAliases = null,
    IReadOnlyList<uint>? LootSourceContentFinderConditionIds = null,
    IReadOnlyList<uint>? LootSourceTerritoryTypeIds = null);

public sealed record ItemSetPiece(
    ItemSetSlot Slot,
    uint ItemId,
    string Name,
    uint IconId,
    uint ClassJobCategoryId,
    ushort EquipLevel);

public enum ItemSetSlot
{
    Head,
    Body,
    Hands,
    Legs,
    Feet,
    Earrings,
    Necklace,
    Bracelets,
    Ring,
}
