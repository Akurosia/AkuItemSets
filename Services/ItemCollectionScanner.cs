using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using Lumina.Excel.Sheets;
using CabinetSheet = Lumina.Excel.Sheets.Cabinet;

namespace AkuItemSets.Services;

public sealed class ItemCollectionScanner
{
    private const uint HqFlag = 1_000_000;
    private readonly Configuration configuration;
    private readonly IClientState clientState;
    private readonly IPlayerState playerState;
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;
    private readonly ItemSetRepository itemSetRepository;

    public ItemCollectionScanner(Configuration configuration, IClientState clientState, IPlayerState playerState, IDataManager dataManager, IPluginLog log, ItemSetRepository itemSetRepository)
    {
        this.configuration = configuration;
        this.clientState = clientState;
        this.playerState = playerState;
        this.dataManager = dataManager;
        this.log = log;
        this.itemSetRepository = itemSetRepository;
    }

    public CharacterCollectionSnapshot? CurrentSnapshot
    {
        get
        {
            var key = GetCurrentCharacterKey();
            return key != null && configuration.Characters.TryGetValue(key, out var snapshot)
                ? snapshot
                : null;
        }
    }

    public unsafe CharacterCollectionSnapshot? ScanCurrentCharacter()
    {
        var characterKey = GetCurrentCharacterKey();
        if (characterKey == null || !playerState.IsLoaded)
        {
            return null;
        }

        var snapshot = new CharacterCollectionSnapshot
        {
            CharacterKey = characterKey,
            CharacterName = playerState.CharacterName,
            WorldName = playerState.HomeWorld.Value.Name.ToString(),
            LastScanUtc = DateTimeOffset.UtcNow,
        };

        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager != null)
        {
            ScanContainers(inventoryManager, snapshot, ItemCollectionSource.Inventory, InventoryTypes.Inventory);
            ScanContainers(inventoryManager, snapshot, ItemCollectionSource.Armoury, InventoryTypes.Armoury);
            ScanContainers(inventoryManager, snapshot, ItemCollectionSource.Saddlebag, InventoryTypes.Saddlebag);
            ScanRetainers(inventoryManager, snapshot);
        }

        ScanGlamourDresser(snapshot);
        ScanArmoire(snapshot);

        configuration.Characters[characterKey] = snapshot;
        configuration.Save();
        return snapshot;
    }

    public string? GetCurrentCharacterKey()
    {
        if (!playerState.IsLoaded || playerState.HomeWorld.RowId == 0)
        {
            return null;
        }

        return $"{playerState.HomeWorld.RowId}:{playerState.CharacterName}";
    }

    private unsafe void ScanContainers(InventoryManager* manager, CharacterCollectionSnapshot snapshot, ItemCollectionSource source, IReadOnlyList<InventoryType> inventoryTypes)
    {
        foreach (var inventoryType in inventoryTypes)
        {
            var container = manager->GetInventoryContainer(inventoryType);
            if (container == null)
            {
                continue;
            }

            for (var slot = 0; slot < container->Size; slot++)
            {
                var item = container->GetInventorySlot(slot);
                AddInventoryItem(snapshot, item, source);
            }
        }
    }

    private unsafe void ScanRetainers(InventoryManager* manager, CharacterCollectionSnapshot snapshot)
    {
        foreach (var inventoryType in InventoryTypes.Retainer)
        {
            var container = manager->GetInventoryContainer(inventoryType);
            if (container == null)
            {
                continue;
            }

            for (var slot = 0; slot < container->Size; slot++)
            {
                var item = container->GetInventorySlot(slot);
                AddInventoryItem(snapshot, item, ItemCollectionSource.Retainer);
            }
        }
    }

    private unsafe void AddInventoryItem(CharacterCollectionSnapshot snapshot, InventoryItem* item, ItemCollectionSource source)
    {
        if (item == null || item->ItemId == 0)
        {
            return;
        }

        AddItem(snapshot, NormalizeItemId(item->ItemId), source, Math.Max(1, item->Quantity));
    }

    private unsafe void ScanGlamourDresser(CharacterCollectionSnapshot snapshot)
    {
        try
        {
            var manager = MirageManager.Instance();
            if (manager == null)
            {
                return;
            }

            var framework = Framework.Instance();
            var uiModule = framework == null ? null : framework->GetUIModule();
            var itemFinderModule = uiModule == null ? null : uiModule->GetItemFinderModule();
            if (itemFinderModule == null)
            {
                return;
            }

            var setByItemId = itemSetRepository.GetSets().ToDictionary(set => set.SetItemId);
            var itemIndex = 0u;
            foreach (var itemId in manager->PrismBoxItemIds)
            {
                var normalizedItemId = NormalizeItemId(itemId);
                if (normalizedItemId == 0)
                {
                    itemIndex++;
                    continue;
                }

                if (setByItemId.TryGetValue(normalizedItemId, out var set))
                {
                    var unlockBits = itemFinderModule->GlamourDresserItemSetUnlockBits[(int)itemIndex];
                    foreach (var piece in set.Pieces)
                    {
                        if (IsSetPieceUnlocked(unlockBits, piece.Slot))
                        {
                            AddItem(snapshot, piece.ItemId, ItemCollectionSource.GlamourDresserSet);
                        }
                    }

                    itemIndex++;
                    continue;
                }

                AddItem(snapshot, normalizedItemId, ItemCollectionSource.GlamourDresser);
                itemIndex++;
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not scan glamour dresser. It may not be cached yet.");
        }
    }

    private unsafe void ScanArmoire(CharacterCollectionSnapshot snapshot)
    {
        try
        {
            var uiState = UIState.Instance();
            var cabinet = uiState == null ? null : &uiState->Cabinet;
            if (cabinet == null || !cabinet->IsCabinetLoaded())
            {
                return;
            }

            foreach (var cabinetRow in dataManager.GetExcelSheet<CabinetSheet>())
            {
                if (cabinetRow.Item.RowId != 0 && cabinet->IsItemInCabinet(cabinetRow.RowId))
                {
                    AddItem(snapshot, cabinetRow.Item.RowId, ItemCollectionSource.Armoire);
                }
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not scan armoire. Open the armoire once in game if it is not cached.");
        }
    }

    private static void AddItem(CharacterCollectionSnapshot snapshot, uint itemId, ItemCollectionSource source, int count = 1)
    {
        if (!snapshot.Items.TryGetValue(itemId, out var ownership))
        {
            ownership = new ItemOwnership { ItemId = itemId };
            snapshot.Items[itemId] = ownership;
        }

        ownership.CountsBySource.TryGetValue(source, out var existing);
        ownership.CountsBySource[source] = existing + count;
    }

    private static uint NormalizeItemId(uint itemId)
        => itemId > HqFlag ? itemId - HqFlag : itemId;

    private static bool IsSetPieceUnlocked(ushort unlockBits, ItemSetSlot slot)
        => (unlockBits & (1 << GetSetPieceBitIndex(slot))) != 0;

    private static int GetSetPieceBitIndex(ItemSetSlot slot)
        => slot switch
        {
            ItemSetSlot.Head => 0,
            ItemSetSlot.Body => 1,
            ItemSetSlot.Hands => 2,
            ItemSetSlot.Legs => 3,
            ItemSetSlot.Feet => 4,
            ItemSetSlot.Earrings => 7,
            ItemSetSlot.Necklace => 8,
            ItemSetSlot.Bracelets => 9,
            ItemSetSlot.Ring => 10,
            _ => 0,
        };

    private static class InventoryTypes
    {
        public static readonly InventoryType[] Inventory =
        [
            InventoryType.Inventory1,
            InventoryType.Inventory2,
            InventoryType.Inventory3,
            InventoryType.Inventory4,
        ];

        public static readonly InventoryType[] Saddlebag =
        [
            InventoryType.SaddleBag1,
            InventoryType.SaddleBag2,
            InventoryType.PremiumSaddleBag1,
            InventoryType.PremiumSaddleBag2,
        ];

        public static readonly InventoryType[] Retainer =
        [
            InventoryType.RetainerPage1,
            InventoryType.RetainerPage2,
            InventoryType.RetainerPage3,
            InventoryType.RetainerPage4,
            InventoryType.RetainerPage5,
            InventoryType.RetainerPage6,
            InventoryType.RetainerPage7,
            InventoryType.RetainerEquippedItems,
        ];

        public static readonly InventoryType[] Armoury =
        [
            InventoryType.EquippedItems,
            InventoryType.ArmoryMainHand,
            InventoryType.ArmoryOffHand,
            InventoryType.ArmoryHead,
            InventoryType.ArmoryBody,
            InventoryType.ArmoryHands,
            InventoryType.ArmoryLegs,
            InventoryType.ArmoryFeets,
            InventoryType.ArmoryEar,
            InventoryType.ArmoryNeck,
            InventoryType.ArmoryWrist,
            InventoryType.ArmoryRings,
            InventoryType.ArmorySoulCrystal,
        ];
    }
}
