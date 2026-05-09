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
    private static readonly TimeSpan AutoScanInterval = TimeSpan.FromSeconds(20);
    private readonly Configuration configuration;
    private readonly IClientState clientState;
    private readonly IPlayerState playerState;
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;
    private readonly ItemSetRepository itemSetRepository;
    private DateTimeOffset nextAutoScanUtc;

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

    public void AutoScanIfDue()
    {
        if (!clientState.IsLoggedIn || DateTimeOffset.UtcNow < nextAutoScanUtc)
        {
            return;
        }

        nextAutoScanUtc = DateTimeOffset.UtcNow + AutoScanInterval;
        ScanCurrentCharacter();
    }

    public unsafe CharacterCollectionSnapshot? ScanCurrentCharacter()
    {
        var characterKey = GetCurrentCharacterKey();
        if (characterKey == null || !playerState.IsLoaded)
        {
            return null;
        }

        var snapshot = configuration.Characters.TryGetValue(characterKey, out var existing)
            ? existing
            : new CharacterCollectionSnapshot();

        snapshot.CharacterKey = characterKey;
        snapshot.CharacterName = playerState.CharacterName;
        snapshot.WorldName = playerState.HomeWorld.Value.Name.ToString();

        var scanStartedUtc = DateTimeOffset.UtcNow;
        var scannedAnyCategory = false;

        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager != null)
        {
            scannedAnyCategory |= ScanContainerCategory(inventoryManager, snapshot, ItemCollectionCategory.Inventory, ItemCollectionSource.Inventory, InventoryTypes.Inventory, scanStartedUtc);
            scannedAnyCategory |= ScanContainerCategory(inventoryManager, snapshot, ItemCollectionCategory.Armoury, ItemCollectionSource.Armoury, InventoryTypes.Armoury, scanStartedUtc);
            scannedAnyCategory |= ScanContainerCategory(inventoryManager, snapshot, ItemCollectionCategory.Saddlebag, ItemCollectionSource.Saddlebag, InventoryTypes.Saddlebag, scanStartedUtc);
            scannedAnyCategory |= ScanRetainers(inventoryManager, snapshot, scanStartedUtc);
        }

        scannedAnyCategory |= ScanGlamourDresser(snapshot, scanStartedUtc);
        scannedAnyCategory |= ScanArmoire(snapshot, scanStartedUtc);

        if (scannedAnyCategory)
        {
            snapshot.LastScanUtc = scanStartedUtc;
        }

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

    private unsafe bool ScanContainerCategory(InventoryManager* manager, CharacterCollectionSnapshot snapshot, ItemCollectionCategory category, ItemCollectionSource source, IReadOnlyList<InventoryType> inventoryTypes, DateTimeOffset scanStartedUtc)
    {
        var collectedItems = CollectContainerItems(manager, inventoryTypes);
        if (collectedItems.Count == 0)
        {
            return false;
        }

        RemoveSources(snapshot, source);
        AddCollectedItems(snapshot, collectedItems, source);
        MarkCategoryScanned(snapshot, category, scanStartedUtc);
        return true;
    }

    private unsafe Dictionary<uint, int> CollectContainerItems(InventoryManager* manager, IReadOnlyList<InventoryType> inventoryTypes)
    {
        var collectedItems = new Dictionary<uint, int>();
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
                if (item == null || item->ItemId == 0)
                {
                    continue;
                }

                var itemId = NormalizeItemId(item->ItemId);
                collectedItems.TryGetValue(itemId, out var existing);
                collectedItems[itemId] = existing + Math.Max(1, item->Quantity);
            }
        }

        return collectedItems;
    }

    private unsafe bool ScanRetainers(InventoryManager* manager, CharacterCollectionSnapshot snapshot, DateTimeOffset scanStartedUtc)
    {
        var collectedItems = CollectContainerItems(manager, InventoryTypes.Retainer);
        if (collectedItems.Count == 0)
        {
            return false;
        }

        RemoveSources(snapshot, ItemCollectionSource.Retainer);
        AddCollectedItems(snapshot, collectedItems, ItemCollectionSource.Retainer);
        MarkCategoryScanned(snapshot, ItemCollectionCategory.Retainers, scanStartedUtc);
        return true;
    }

    private unsafe bool ScanGlamourDresser(CharacterCollectionSnapshot snapshot, DateTimeOffset scanStartedUtc)
    {
        try
        {
            var manager = MirageManager.Instance();
            if (manager == null)
            {
                return false;
            }

            var framework = Framework.Instance();
            var uiModule = framework == null ? null : framework->GetUIModule();
            var itemFinderModule = uiModule == null ? null : uiModule->GetItemFinderModule();
            if (itemFinderModule == null)
            {
                return false;
            }

            var collectedSingleItems = new Dictionary<uint, int>();
            var collectedSetItems = new Dictionary<uint, int>();
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
                            collectedSetItems.TryGetValue(piece.ItemId, out var existing);
                            collectedSetItems[piece.ItemId] = existing + 1;
                        }
                    }

                    itemIndex++;
                    continue;
                }

                collectedSingleItems.TryGetValue(normalizedItemId, out var singleExisting);
                collectedSingleItems[normalizedItemId] = singleExisting + 1;
                itemIndex++;
            }

            if (collectedSingleItems.Count == 0 && collectedSetItems.Count == 0)
            {
                return false;
            }

            RemoveSources(snapshot, ItemCollectionSource.GlamourDresser, ItemCollectionSource.GlamourDresserSet);
            AddCollectedItems(snapshot, collectedSingleItems, ItemCollectionSource.GlamourDresser);
            AddCollectedItems(snapshot, collectedSetItems, ItemCollectionSource.GlamourDresserSet);

            MarkCategoryScanned(snapshot, ItemCollectionCategory.GlamourDresser, scanStartedUtc);
            return true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not scan glamour dresser. It may not be cached yet.");
            return false;
        }
    }

    private unsafe bool ScanArmoire(CharacterCollectionSnapshot snapshot, DateTimeOffset scanStartedUtc)
    {
        try
        {
            var uiState = UIState.Instance();
            var cabinet = uiState == null ? null : &uiState->Cabinet;
            if (cabinet == null || !cabinet->IsCabinetLoaded())
            {
                return false;
            }

            var collectedItems = new Dictionary<uint, int>();
            foreach (var cabinetRow in dataManager.GetExcelSheet<CabinetSheet>())
            {
                if (cabinetRow.Item.RowId != 0 && cabinet->IsItemInCabinet(cabinetRow.RowId))
                {
                    collectedItems.TryGetValue(cabinetRow.Item.RowId, out var existing);
                    collectedItems[cabinetRow.Item.RowId] = existing + 1;
                }
            }

            if (collectedItems.Count == 0)
            {
                return false;
            }

            RemoveSources(snapshot, ItemCollectionSource.Armoire);
            AddCollectedItems(snapshot, collectedItems, ItemCollectionSource.Armoire);
            MarkCategoryScanned(snapshot, ItemCollectionCategory.Armoire, scanStartedUtc);
            return true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not scan armoire. Open the armoire once in game if it is not cached.");
            return false;
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

    private static void AddCollectedItems(CharacterCollectionSnapshot snapshot, IReadOnlyDictionary<uint, int> collectedItems, ItemCollectionSource source)
    {
        foreach (var (itemId, count) in collectedItems)
        {
            AddItem(snapshot, itemId, source, count);
        }
    }

    private static uint NormalizeItemId(uint itemId)
        => itemId > HqFlag ? itemId - HqFlag : itemId;

    private static bool IsSetPieceUnlocked(ushort unlockBits, ItemSetSlot slot)
        => (unlockBits & (1 << GetSetPieceBitIndex(slot))) != 0;

    private static int GetSetPieceBitIndex(ItemSetSlot slot)
        => slot switch
        {
            ItemSetSlot.Head => 2,
            ItemSetSlot.Body => 3,
            ItemSetSlot.Hands => 4,
            ItemSetSlot.Legs => 5,
            ItemSetSlot.Feet => 6,
            ItemSetSlot.Earrings => 7,
            ItemSetSlot.Necklace => 8,
            ItemSetSlot.Bracelets => 9,
            ItemSetSlot.Ring => 10,
            _ => 0,
        };

    private static void MarkCategoryScanned(CharacterCollectionSnapshot snapshot, ItemCollectionCategory category, DateTimeOffset scanStartedUtc)
        => snapshot.LastScanByCategory[category] = scanStartedUtc;

    private static void RemoveSources(CharacterCollectionSnapshot snapshot, params ItemCollectionSource[] sources)
    {
        var sourceSet = sources.ToHashSet();
        var emptyItemIds = new List<uint>();

        foreach (var (itemId, ownership) in snapshot.Items)
        {
            foreach (var source in sourceSet)
            {
                ownership.CountsBySource.Remove(source);
            }

            if (ownership.CountsBySource.Count == 0)
            {
                emptyItemIds.Add(itemId);
            }
        }

        foreach (var itemId in emptyItemIds)
        {
            snapshot.Items.Remove(itemId);
        }
    }

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
