using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;
using CabinetSheet = Lumina.Excel.Sheets.Cabinet;

namespace AkuItemSets.Services;

public sealed class ItemCollectionScanner
{
    private const uint HqFlag = 1_000_000;
    private static readonly TimeSpan AutoScanInterval = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan StorageOpenScanInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan InventoryChangePollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan InventoryToDresserTransferGrace = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan SourceScanStabilityDelay = TimeSpan.FromMilliseconds(750);
    private readonly Configuration configuration;
    private readonly IClientState clientState;
    private readonly IPlayerState playerState;
    private readonly IDataManager dataManager;
    private readonly IGameGui gameGui;
    private readonly IPluginLog log;
    private readonly ItemSetRepository itemSetRepository;
    private readonly Dictionary<ItemCollectionCategory, bool> storageWasVisible = new();
    private readonly Dictionary<ItemCollectionCategory, DateTimeOffset> lastStorageOpenScanUtc = new();
    private readonly Dictionary<uint, DateTimeOffset> inventoryTransferGraceUntil = new();
    private readonly Dictionary<ItemCollectionSource, string> pendingSourceScanSignatures = new();
    private readonly Dictionary<ItemCollectionSource, DateTimeOffset> pendingSourceScanSinceUtc = new();
    private readonly Dictionary<ItemCollectionSource, string> observedSourceScanSignatures = new();
    private DateTimeOffset nextAutoScanUtc;
    private DateTimeOffset nextInventoryChangePollUtc;

    public ItemCollectionScanner(Configuration configuration, IClientState clientState, IPlayerState playerState, IDataManager dataManager, IGameGui gameGui, IPluginLog log, ItemSetRepository itemSetRepository)
    {
        this.configuration = configuration;
        this.clientState = clientState;
        this.playerState = playerState;
        this.dataManager = dataManager;
        this.gameGui = gameGui;
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
        if (!clientState.IsLoggedIn)
        {
            return;
        }

        ScanOpenedStorages();
        ScanChangedInventorySources();

        if (DateTimeOffset.UtcNow >= nextAutoScanUtc)
        {
            var snapshot = ScanCurrentCharacter();
            nextAutoScanUtc = DateTimeOffset.UtcNow + (snapshot == null ? TimeSpan.FromSeconds(5) : AutoScanInterval);
        }
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
            scannedAnyCategory |= ScanInventory(inventoryManager, snapshot, scanStartedUtc);
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

    private unsafe bool ScanInventory(InventoryManager* manager, CharacterCollectionSnapshot snapshot, DateTimeOffset scanStartedUtc)
    {
        if (!AreContainersLoaded(manager, InventoryTypes.Inventory))
        {
            return false;
        }

        var collectedItems = CollectContainerItems(manager, InventoryTypes.Inventory);
        if (!IsSourceScanStable(ItemCollectionSource.Inventory, collectedItems, scanStartedUtc))
        {
            return false;
        }

        var previousInventoryItems = snapshot.Items
            .Where(entry => entry.Value.CountsBySource.ContainsKey(ItemCollectionSource.Inventory))
            .Select(entry => entry.Key)
            .ToHashSet();
        var removedInventoryItems = previousInventoryItems.Except(collectedItems.Keys).ToList();
        if (removedInventoryItems.Count > 0 && IsStorageCategoryVisible(ItemCollectionCategory.GlamourDresser))
        {
            foreach (var itemId in removedInventoryItems)
            {
                inventoryTransferGraceUntil[itemId] = scanStartedUtc + InventoryToDresserTransferGrace;
            }
        }

        RemoveSources(snapshot, ItemCollectionSource.Inventory);
        AddCollectedItems(snapshot, collectedItems, ItemCollectionSource.Inventory);
        AddInventoryTransferGraceItems(snapshot, scanStartedUtc);
        MarkCategoryScanned(snapshot, ItemCollectionCategory.Inventory, scanStartedUtc);
        return true;
    }

    private unsafe bool ScanContainerCategory(InventoryManager* manager, CharacterCollectionSnapshot snapshot, ItemCollectionCategory category, ItemCollectionSource source, IReadOnlyList<InventoryType> inventoryTypes, DateTimeOffset scanStartedUtc)
    {
        if (!AreContainersLoaded(manager, inventoryTypes))
        {
            return false;
        }

        var collectedItems = CollectContainerItems(manager, inventoryTypes);
        if (!IsSourceScanStable(source, collectedItems, scanStartedUtc))
        {
            return false;
        }

        RemoveSources(snapshot, source);
        AddCollectedItems(snapshot, collectedItems, source);
        MarkCategoryScanned(snapshot, category, scanStartedUtc);
        return true;
    }

    private bool IsSourceScanStable(ItemCollectionSource source, IReadOnlyDictionary<uint, int> collectedItems, DateTimeOffset scanStartedUtc)
    {
        var signature = BuildSourceScanSignature(collectedItems);
        if (!pendingSourceScanSignatures.TryGetValue(source, out var previousSignature) || previousSignature != signature)
        {
            pendingSourceScanSignatures[source] = signature;
            pendingSourceScanSinceUtc[source] = scanStartedUtc;
            log.Debug($"[AkuItemSets] Source scan changed for {source}; waiting for stable scan before committing.");
            return false;
        }

        if (!pendingSourceScanSinceUtc.TryGetValue(source, out var firstSeenUtc) || scanStartedUtc - firstSeenUtc < SourceScanStabilityDelay)
        {
            return false;
        }

        pendingSourceScanSignatures.Remove(source);
        pendingSourceScanSinceUtc.Remove(source);
        return true;
    }

    private static string BuildSourceScanSignature(IReadOnlyDictionary<uint, int> collectedItems)
        => string.Join("|", collectedItems.OrderBy(entry => entry.Key).Select(entry => $"{entry.Key}:{entry.Value}"));

    private static unsafe bool AreContainersLoaded(InventoryManager* manager, IReadOnlyList<InventoryType> inventoryTypes)
    {
        foreach (var inventoryType in inventoryTypes)
        {
            var container = manager->GetInventoryContainer(inventoryType);
            if (container == null || !container->IsLoaded)
            {
                return false;
            }
        }

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
        if (!AreContainersLoaded(manager, InventoryTypes.Retainer))
        {
            return false;
        }

        var collectedItems = CollectContainerItems(manager, InventoryTypes.Retainer);
        if (!IsSourceScanStable(ItemCollectionSource.Retainer, collectedItems, scanStartedUtc))
        {
            return false;
        }

        RemoveSources(snapshot, ItemCollectionSource.Retainer);
        AddCollectedItems(snapshot, collectedItems, ItemCollectionSource.Retainer);
        MarkCategoryScanned(snapshot, ItemCollectionCategory.Retainers, scanStartedUtc);
        MarkActiveRetainerScanned(snapshot, scanStartedUtc);
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

            if (!TryGetItemFinderModule(out var itemFinderModule))
            {
                return false;
            }

            var collectedSingleItems = new Dictionary<uint, int>();
            var collectedSetItems = new Dictionary<uint, int>();
            var setByItemId = itemSetRepository.GetSets().ToDictionary(set => set.SetItemId);
            var itemIndex = 0u;
            foreach (var itemId in itemFinderModule->GlamourDresserItemIds)
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
                        if (IsSetPieceStoredInDresserSet(unlockBits, piece.Slot))
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
            ClearResolvedInventoryTransferGrace(snapshot);

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
            var uiState = FFXIVClientStructs.FFXIV.Client.Game.UI.UIState.Instance();
            if (uiState == null)
            {
                return false;
            }

            var cabinet = &uiState->Cabinet;
            if (!cabinet->IsCabinetLoaded())
            {
                log.Debug("[AkuItemSets] Armoire scan skipped: cabinet data is not cached yet.");
                return false;
            }

            var collectedItems = new Dictionary<uint, int>();
            foreach (var cabinetRow in dataManager.GetExcelSheet<CabinetSheet>())
            {
                if (cabinetRow.Item.RowId == 0)
                {
                    continue;
                }

                if (!cabinet->IsItemInCabinet(cabinetRow.RowId))
                {
                    continue;
                }

                collectedItems.TryGetValue(cabinetRow.Item.RowId, out var existing);
                collectedItems[cabinetRow.Item.RowId] = existing + 1;
            }

            RemoveSources(snapshot, ItemCollectionSource.Armoire);
            AddCollectedItems(snapshot, collectedItems, ItemCollectionSource.Armoire);
            MarkCategoryScanned(snapshot, ItemCollectionCategory.Armoire, scanStartedUtc);
            return true;
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Could not scan armoire. Open the armoire once in game if it is not cached.");
            return false;
        }
    }

    private static unsafe bool TryGetItemFinderModule(out ItemFinderModule* itemFinderModule)
    {
        itemFinderModule = null;
        var framework = Framework.Instance();
        var uiModule = framework == null ? null : framework->GetUIModule();
        itemFinderModule = uiModule == null ? null : uiModule->GetItemFinderModule();
        return itemFinderModule != null;
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

    private static bool IsSetPieceStoredInDresserSet(ushort unlockBits, ItemSetSlot slot)
        => (unlockBits & (1 << GetSetPieceBitIndex(slot))) == 0;

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

    private static unsafe void MarkActiveRetainerScanned(CharacterCollectionSnapshot snapshot, DateTimeOffset scanStartedUtc)
    {
        try
        {
            var retainerManager = RetainerManager.Instance();
            var activeRetainer = retainerManager == null ? null : retainerManager->GetActiveRetainer();
            var retainerName = activeRetainer == null ? string.Empty : activeRetainer->NameString;
            if (!string.IsNullOrWhiteSpace(retainerName))
            {
                snapshot.LastRetainerScanByName[retainerName] = scanStartedUtc;
            }
        }
        catch
        {
            // Retainer metadata is best-effort; the aggregate retainer timestamp still records a valid item scan.
        }
    }

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

    private void AddInventoryTransferGraceItems(CharacterCollectionSnapshot snapshot, DateTimeOffset now)
    {
        var expiredItemIds = inventoryTransferGraceUntil
            .Where(entry => entry.Value <= now)
            .Select(entry => entry.Key)
            .ToList();

        foreach (var itemId in expiredItemIds)
        {
            inventoryTransferGraceUntil.Remove(itemId);
        }

        if (!IsStorageCategoryVisible(ItemCollectionCategory.GlamourDresser))
        {
            return;
        }

        foreach (var itemId in inventoryTransferGraceUntil.Keys)
        {
            if (!snapshot.Items.TryGetValue(itemId, out var ownership)
                || (!ownership.CountsBySource.ContainsKey(ItemCollectionSource.GlamourDresser)
                    && !ownership.CountsBySource.ContainsKey(ItemCollectionSource.GlamourDresserSet)))
            {
                AddItem(snapshot, itemId, ItemCollectionSource.Inventory);
            }
        }
    }

    private void ClearResolvedInventoryTransferGrace(CharacterCollectionSnapshot snapshot)
    {
        var resolvedItemIds = inventoryTransferGraceUntil.Keys
            .Where(itemId => snapshot.Items.TryGetValue(itemId, out var ownership)
                && (ownership.CountsBySource.ContainsKey(ItemCollectionSource.GlamourDresser)
                    || ownership.CountsBySource.ContainsKey(ItemCollectionSource.GlamourDresserSet)))
            .ToList();

        foreach (var itemId in resolvedItemIds)
        {
            inventoryTransferGraceUntil.Remove(itemId);
        }
    }

    private void ScanOpenedStorages()
    {
        var now = DateTimeOffset.UtcNow;
        var shouldScan = false;

        foreach (var (category, addonNames) in StorageAddonNames)
        {
            var isVisible = IsStorageCategoryVisible(category);
            storageWasVisible.TryGetValue(category, out var wasVisible);
            lastStorageOpenScanUtc.TryGetValue(category, out var lastScan);

            if (isVisible && (!wasVisible || now - lastScan >= StorageOpenScanInterval))
            {
                lastStorageOpenScanUtc[category] = now;
                shouldScan = true;
            }

            storageWasVisible[category] = isVisible;
        }

        if (shouldScan)
        {
            ScanCurrentCharacter();
        }
    }

    private unsafe void ScanChangedInventorySources()
    {
        var now = DateTimeOffset.UtcNow;
        if (now < nextInventoryChangePollUtc)
        {
            return;
        }

        nextInventoryChangePollUtc = now + InventoryChangePollInterval;

        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
        {
            return;
        }

        var shouldScan = HasPendingContainerScan()
            | HasContainerSignatureChanged(inventoryManager, ItemCollectionSource.Inventory, InventoryTypes.Inventory)
            | HasContainerSignatureChanged(inventoryManager, ItemCollectionSource.Armoury, InventoryTypes.Armoury)
            | HasContainerSignatureChanged(inventoryManager, ItemCollectionSource.Saddlebag, InventoryTypes.Saddlebag);

        if (shouldScan)
        {
            ScanCurrentCharacter();
        }
    }

    private bool HasPendingContainerScan()
        => pendingSourceScanSignatures.ContainsKey(ItemCollectionSource.Inventory)
            || pendingSourceScanSignatures.ContainsKey(ItemCollectionSource.Armoury)
            || pendingSourceScanSignatures.ContainsKey(ItemCollectionSource.Saddlebag);

    private unsafe bool HasContainerSignatureChanged(InventoryManager* manager, ItemCollectionSource source, IReadOnlyList<InventoryType> inventoryTypes)
    {
        if (!AreContainersLoaded(manager, inventoryTypes))
        {
            return false;
        }

        var signature = BuildSourceScanSignature(CollectContainerItems(manager, inventoryTypes));
        if (!observedSourceScanSignatures.TryGetValue(source, out var previousSignature))
        {
            observedSourceScanSignatures[source] = signature;
            return false;
        }

        if (previousSignature == signature)
        {
            return false;
        }

        observedSourceScanSignatures[source] = signature;
        log.Debug($"[AkuItemSets] Detected live {source} container change; refreshing collection snapshot.");
        return true;
    }

    private bool IsAddonVisible(string addonName)
    {
        var addon = gameGui.GetAddonByName(addonName, 1);
        return !addon.IsNull && addon.IsVisible;
    }

    private bool IsStorageCategoryVisible(ItemCollectionCategory category)
        => StorageAddonNames.TryGetValue(category, out var addonNames) && addonNames.Any(IsAddonVisible);

    private static readonly IReadOnlyDictionary<ItemCollectionCategory, string[]> StorageAddonNames = new Dictionary<ItemCollectionCategory, string[]>
    {
        [ItemCollectionCategory.Inventory] =
        [
            "Inventory",
            "InventoryLarge",
            "InventoryExpansion",
            "InventoryGrid",
        ],
        [ItemCollectionCategory.Saddlebag] =
        [
            "InventoryBuddy",
        ],
        [ItemCollectionCategory.Retainers] =
        [
            "RetainerList",
            "InventoryRetainer",
            "InventoryRetainerLarge",
            "RetainerItemTransferList",
            "RetainerSell",
        ],
        [ItemCollectionCategory.GlamourDresser] =
        [
            "MiragePrismPrismBox",
            "MiragePrismPrismBoxCrystallize",
            "MiragePrismPrismSetConvert",
            "MiragePrismPrismItemDetail",
            "MiragePrismPrismSetPreview",
        ],
        [ItemCollectionCategory.Armoire] =
        [
            "CabinetWithdraw",
        ],
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
