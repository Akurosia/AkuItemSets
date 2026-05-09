using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace AkuItemSets.Services;

public sealed class ItemSetRepository
{
    private readonly IDataManager dataManager;
    private List<ItemSetDefinition>? cachedSets;

    public ItemSetRepository(IDataManager dataManager)
    {
        this.dataManager = dataManager;
    }

    public IReadOnlyList<ItemSetDefinition> GetSets()
    {
        if (cachedSets != null)
        {
            return cachedSets;
        }

        var result = new List<ItemSetDefinition>();
        var seenSetIds = new HashSet<uint>();
        var lookupSheet = dataManager.GetExcelSheet<MirageStoreSetItemLookup>();
        var setItemSheet = dataManager.GetExcelSheet<MirageStoreSetItem>();

        foreach (var lookup in lookupSheet)
        {
            foreach (var itemRef in lookup.Item)
            {
                if (itemRef.RowId == 0 || !seenSetIds.Add(itemRef.RowId))
                {
                    continue;
                }

                if (!setItemSheet.TryGetRow(itemRef.RowId, out var setRow) || !itemRef.IsValid)
                {
                    continue;
                }

                var setItem = itemRef.Value;
                var pieces = new List<ItemSetPiece>();
                AddPiece(pieces, ItemSetSlot.Head, setRow.Head);
                AddPiece(pieces, ItemSetSlot.Body, setRow.Body);
                AddPiece(pieces, ItemSetSlot.Hands, setRow.Hands);
                AddPiece(pieces, ItemSetSlot.Legs, setRow.Legs);
                AddPiece(pieces, ItemSetSlot.Feet, setRow.Feet);
                AddPiece(pieces, ItemSetSlot.Earrings, setRow.Earrings);
                AddPiece(pieces, ItemSetSlot.Necklace, setRow.Necklace);
                AddPiece(pieces, ItemSetSlot.Bracelets, setRow.Bracelets);
                AddPiece(pieces, ItemSetSlot.Ring, setRow.Ring);

                if (pieces.Count == 0)
                {
                    continue;
                }

                result.Add(new ItemSetDefinition(
                    itemRef.RowId,
                    setItem.Name.ToString(),
                    setItem.Icon,
                    pieces));
            }
        }

        cachedSets = result
            .OrderBy(set => set.SetName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return cachedSets;
    }

    private static void AddPiece(List<ItemSetPiece> pieces, ItemSetSlot slot, RowRef<Item> itemRef)
    {
        if (itemRef.RowId == 0 || !itemRef.IsValid)
        {
            return;
        }

        var item = itemRef.Value;
        pieces.Add(new ItemSetPiece(
            slot,
            itemRef.RowId,
            item.Name.ToString(),
            item.Icon,
            item.ClassJobCategory.RowId,
            item.LevelEquip));
    }
}
