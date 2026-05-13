using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using AkuItemSets.Services;
using Dalamud.Bindings.ImGui;
using Dalamud.Game;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace AkuItemSets.Windows;

public sealed class MainWindow : Window
{
    private readonly Configuration configuration;
    private readonly ItemSetRepository itemSetRepository;
    private readonly ItemCollectionScanner scanner;
    private readonly IPlayerState playerState;
    private readonly IClientState clientState;
    private readonly IDataManager dataManager;
    private readonly ITextureProvider textureProvider;
    private readonly Localization localization;
    private readonly IGameGui gameGui;
    private readonly IPluginLog log;
    private HashSet<uint>? armoireItemIds;
    private bool armoireCandidatesTabActive;
    private string armoireClassJobFilter = "All";
    private bool loggedCurrentInstanceEmptyResult;
    private Dictionary<uint, FallbackOutfitSource>? fallbackOutfitSourcesBySetId;

    public MainWindow(
        Configuration configuration,
        ItemSetRepository itemSetRepository,
        ItemCollectionScanner scanner,
        IPlayerState playerState,
        IClientState clientState,
        IDataManager dataManager,
        ITextureProvider textureProvider,
        Localization localization,
        IGameGui gameGui,
        IPluginLog log)
        : base("AkuItemSets##aku_item_sets")
    {
        this.configuration = configuration;
        this.itemSetRepository = itemSetRepository;
        this.scanner = scanner;
        this.playerState = playerState;
        this.clientState = clientState;
        this.dataManager = dataManager;
        this.textureProvider = textureProvider;
        this.localization = localization;
        this.gameGui = gameGui;
        this.log = log;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(760, 460),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override void Draw()
    {
        var snapshot = scanner.CurrentSnapshot;
        if (snapshot == null)
        {
            ImGui.TextUnformatted(localization["status.login"]);
            return;
        }

        ImGui.TextUnformatted($"{snapshot.CharacterName} @ {snapshot.WorldName}");
        ImGui.SameLine();
        ImGui.TextDisabled($"{localization["status.lastScan"]}: {snapshot.LastScanUtc.LocalDateTime:g}");
        ImGui.Separator();
        armoireCandidatesTabActive = false;

        using var tabs = ImRaii.TabBar("aku_item_sets_tabs");
        if (!tabs.Success)
        {
            return;
        }

        using (var tab = ImRaii.TabItem(localization["tab.collection"]))
        {
            if (tab.Success)
            {
                DrawToolbar();
                DrawSetTable(snapshot);
            }
        }

        using (var tab = ImRaii.TabItem(localization["tab.scanStatus"]))
        {
            if (tab.Success)
            {
                DrawScanStatus(snapshot, localization);
            }
        }

        using (var tab = ImRaii.TabItem(localization["tab.armoireCandidates"]))
        {
            if (tab.Success)
            {
                armoireCandidatesTabActive = true;
                DrawArmoireCandidates(snapshot);
            }
        }

        if (armoireCandidatesTabActive)
        {
            DrawArmoireCandidateStorageOverlay(snapshot);
        }
    }

    private void DrawToolbar()
    {
        ImGui.SetNextItemWidth(220);
        var search = configuration.SearchText;
        if (ImGui.InputTextWithHint("##search", localization["toolbar.searchHint"], ref search, 128))
        {
            configuration.SearchText = search;
            configuration.Save();
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(160);
        var filter = configuration.FilterMode;
        if (ImGui.BeginCombo("##filter", GetFilterLabel(filter)))
        {
            foreach (var value in Enum.GetValues<CollectionFilterMode>())
            {
                var selected = filter == value;
                if (ImGui.Selectable(GetFilterLabel(value), selected))
                {
                    configuration.FilterMode = value;
                    configuration.Save();
                }

                if (selected)
                {
                    ImGui.SetItemDefaultFocus();
                }
            }

            ImGui.EndCombo();
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(120);
        var sortMode = configuration.SortMode;
        if (ImGui.BeginCombo("##sort", GetSortLabel(sortMode)))
        {
            foreach (var value in Enum.GetValues<ItemSetSortMode>())
            {
                var selected = sortMode == value;
                if (ImGui.Selectable(GetSortLabel(value), selected))
                {
                    configuration.SortMode = value;
                    configuration.Save();
                }

                if (selected)
                {
                    ImGui.SetItemDefaultFocus();
                }
            }

            ImGui.EndCombo();
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(80);
        var sortDirection = configuration.SortDirection;
        if (ImGui.BeginCombo("##sortDirection", GetSortDirectionLabel(sortDirection)))
        {
            foreach (var value in Enum.GetValues<SortDirection>())
            {
                var selected = sortDirection == value;
                if (ImGui.Selectable(GetSortDirectionLabel(value), selected))
                {
                    configuration.SortDirection = value;
                    configuration.Save();
                }

                if (selected)
                {
                    ImGui.SetItemDefaultFocus();
                }
            }

            ImGui.EndCombo();
        }

        ImGui.NewLine();
        var hideCompleted = configuration.HideCompletedSets;
        if (ImGui.Checkbox(localization["toolbar.hideComplete"], ref hideCompleted))
        {
            configuration.HideCompletedSets = hideCompleted;
            configuration.Save();
        }

        ImGui.SameLine();
        var showOnlyMissing = configuration.ShowOnlyMissingPieces;
        if (ImGui.Checkbox(localization["toolbar.onlyMissing"], ref showOnlyMissing))
        {
            configuration.ShowOnlyMissingPieces = showOnlyMissing;
            configuration.Save();
        }

        ImGui.SameLine();
        var includeAllClass = configuration.IncludeAllClassItemsInRoleFilters;
        if (ImGui.Checkbox(localization["toolbar.includeAllClass"], ref includeAllClass))
        {
            configuration.IncludeAllClassItemsInRoleFilters = includeAllClass;
            configuration.Save();
        }

        ImGui.SameLine();
        var onlyCurrentInstance = configuration.ShowOnlyCurrentInstanceLoot;
        if (ImGui.Checkbox(localization["toolbar.onlyCurrentInstance"], ref onlyCurrentInstance))
        {
            configuration.ShowOnlyCurrentInstanceLoot = onlyCurrentInstance;
            configuration.Save();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(GetCurrentInstanceLootTooltip());
            ImGui.EndTooltip();
        }
    }

    private static void DrawScanStatus(CharacterCollectionSnapshot snapshot, Localization localization)
    {
        ImGui.TextUnformatted(localization["status.autoScan"]);
        ImGui.TextDisabled(localization["status.cacheHint"]);
        ImGui.Spacing();

        using var table = ImRaii.Table("scan_status_table", 2, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable);
        if (!table.Success)
        {
            return;
        }

        ImGui.TableSetupColumn("Category", ImGuiTableColumnFlags.WidthFixed, 180);
        ImGui.TableSetupColumn(localization["status.lastScan"], ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableHeadersRow();

        foreach (var category in Enum.GetValues<ItemCollectionCategory>())
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(GetCategoryLabel(category, localization));
            ImGui.TableNextColumn();
            if (snapshot.LastScanByCategory.TryGetValue(category, out var lastScan))
            {
                ImGui.TextUnformatted(lastScan.LocalDateTime.ToString("g"));
            }
            else
            {
                ImGui.TextDisabled(localization["status.notScanned"]);
            }

            if (category == ItemCollectionCategory.Retainers)
            {
                DrawRetainerScanRows(snapshot);
            }
        }
    }

    private static void DrawRetainerScanRows(CharacterCollectionSnapshot snapshot)
    {
        foreach (var (retainerName, lastScan) in snapshot.LastRetainerScanByName.OrderBy(entry => entry.Key, StringComparer.CurrentCultureIgnoreCase))
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.Indent(16);
            ImGui.TextUnformatted(retainerName);
            ImGui.Unindent(16);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(lastScan.LocalDateTime.ToString("g"));
        }
    }

    private void DrawSetTable(CharacterCollectionSnapshot snapshot)
    {
        var allSets = itemSetRepository.GetSets().ToList();
        var searchedSets = allSets.Where(set => MatchesSearch(set, configuration.SearchText)).ToList();
        var instanceFilteredSets = searchedSets.Where(MatchesCurrentInstanceLootFilter).ToList();
        var sets = instanceFilteredSets.Where(MatchesFilter).ToList();

        if (configuration.ShowOnlyCurrentInstanceLoot && !loggedCurrentInstanceEmptyResult && sets.Count == 0)
        {
            loggedCurrentInstanceEmptyResult = true;
            log.Debug($"[AkuItemSets] Current-instance loot filter returned 0 rows. allSets={allSets.Count}, searchedSets={searchedSets.Count}, afterInstanceFilter={instanceFilteredSets.Count}, currentTerritory={(uint)clientState.TerritoryType}, currentNames=[{string.Join(" | ", GetTerritoryNames((uint)clientState.TerritoryType).Distinct(StringComparer.CurrentCultureIgnoreCase))}]");
        }
        else if (!configuration.ShowOnlyCurrentInstanceLoot)
        {
            loggedCurrentInstanceEmptyResult = false;
        }

        sets = ApplySort(sets).ToList();

        var visibleSets = configuration.HideCompletedSets
            ? sets.Where(set => !IsSetCompleteInCollectionStorage(snapshot, GetPiecesForActiveFilter(set))).ToList()
            : sets;

        var completeCount = sets.Count(set =>
        {
            var pieces = GetPiecesForActiveFilter(set);
            return IsSetCompleteInCollectionStorage(snapshot, pieces);
        });
        var sourceCount = sets.Count(set => !string.IsNullOrWhiteSpace(set.LootSourceName) || (set.LootSourceTerritoryTypeIds?.Count ?? 0) > 0);
        ImGui.TextUnformatted($"{completeCount}/{sets.Count} visible sets complete | {sourceCount} with source");
        if (sets.Count > 0 && sourceCount == 0)
        {
            ImGui.TextDisabled("No sources are attached to the visible rows. Check /xllog for '[AkuItemSets] Outfit source matching summary'.");
        }

        if (configuration.ShowOnlyCurrentInstanceLoot && sets.Count == 0)
        {
            ImGui.TextDisabled($"{localization["toolbar.onlyCurrentInstance"]}: 0 matches for current territory #{(uint)clientState.TerritoryType}. Check /xllog for '[AkuItemSets] Outfit source' debug lines.");
        }

        using var table = ImRaii.Table("itemsets_table_source_v3", 6, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY);
        if (!table.Success)
        {
            return;
        }

        ImGui.TableSetupColumn(localization["table.set"], ImGuiTableColumnFlags.WidthStretch, 2.0f);
        ImGui.TableSetupColumn(localization["table.obtainable"], ImGuiTableColumnFlags.WidthStretch, 1.6f);
        ImGui.TableSetupColumn(localization["table.progress"], ImGuiTableColumnFlags.WidthFixed, 90);
        ImGui.TableSetupColumn(localization["table.missing"], ImGuiTableColumnFlags.WidthStretch, 1.5f);
        ImGui.TableSetupColumn(localization["table.owned"], ImGuiTableColumnFlags.WidthStretch, 1.5f);
        ImGui.TableSetupColumn(localization["table.use"], ImGuiTableColumnFlags.WidthFixed, 140);
        ImGui.TableHeadersRow();

        foreach (var set in visibleSets)
        {
            DrawSetRow(snapshot, set);
        }
    }

    private void DrawArmoireCandidates(CharacterCollectionSnapshot snapshot)
    {
        var itemSheet = dataManager.GetExcelSheet<Item>();
        var rows = GetArmoireCandidates(snapshot)
            .Select(candidate => itemSheet.TryGetRow(candidate.ItemId, out var item)
                ? new ArmoireCandidateDisplayRow(candidate, item, GetClassJobCategoryLabel(item.ClassJobCategory.RowId))
                : null)
            .Where(row => row != null)
            .Select(row => row!)
            .ToList();

        var classJobOptions = rows
            .SelectMany(row => GetClassJobFilterOptions(row.ClassJobCategoryLabel))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(label => label, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        if (armoireClassJobFilter != "All" && !classJobOptions.Contains(armoireClassJobFilter, StringComparer.CurrentCultureIgnoreCase))
        {
            armoireClassJobFilter = "All";
        }

        ImGui.TextUnformatted($"{rows.Count} {localization["armoire.candidatesFound"]}");
        ImGui.TextDisabled(localization["armoire.clickHint"]);
        ImGui.Spacing();

        ImGui.SetNextItemWidth(260);
        if (ImGui.BeginCombo("Class/Job##armoire_class_job_filter", armoireClassJobFilter))
        {
            if (ImGui.Selectable("All", armoireClassJobFilter == "All"))
            {
                armoireClassJobFilter = "All";
            }

            foreach (var option in classJobOptions)
            {
                var selected = string.Equals(armoireClassJobFilter, option, StringComparison.CurrentCultureIgnoreCase);
                if (ImGui.Selectable(option, selected))
                {
                    armoireClassJobFilter = option;
                }

                if (selected)
                {
                    ImGui.SetItemDefaultFocus();
                }
            }

            ImGui.EndCombo();
        }

        if (armoireClassJobFilter != "All")
        {
            rows = rows
                .Where(row => MatchesClassJobFilter(row.ClassJobCategoryLabel, armoireClassJobFilter))
                .ToList();
        }

        ImGui.SameLine();
        ImGui.TextDisabled($"{rows.Count} shown");
        ImGui.Spacing();

        using var table = ImRaii.Table("armoire_candidates_table", 6, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY);
        if (!table.Success)
        {
            return;
        }

        ImGui.TableSetupColumn(localization["table.item"], ImGuiTableColumnFlags.WidthStretch, 2.5f);
        ImGui.TableSetupColumn("Class/Job", ImGuiTableColumnFlags.WidthFixed, 170);
        ImGui.TableSetupColumn(localization["table.source"], ImGuiTableColumnFlags.WidthFixed, 120);
        ImGui.TableSetupColumn("Location", ImGuiTableColumnFlags.WidthFixed, 180);
        ImGui.TableSetupColumn(localization["table.count"], ImGuiTableColumnFlags.WidthFixed, 60);
        ImGui.TableSetupColumn(localization["table.id"], ImGuiTableColumnFlags.WidthFixed, 80);
        ImGui.TableHeadersRow();

        foreach (var row in rows)
        {
            var candidate = row.Candidate;
            var item = row.Item;
            var itemName = item.Name.ToString();

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            DrawSearchableItem(item.Icon, itemName);
            ImGui.SameLine();
            if (ImGui.Selectable($"{itemName}##armoire_candidate_{candidate.ItemId}_{candidate.Source}", false))
            {
                ImGui.SetClipboardText($"/isearch \"{itemName}\"");
            }

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.ClassJobCategoryLabel);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(GetSourceLabel(candidate.Source, localization));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(GetCandidateLocationText(candidate));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(candidate.Count.ToString());
            ImGui.TableNextColumn();
            ImGui.TextDisabled(candidate.ItemId.ToString());
        }
    }



    private static IEnumerable<string> GetClassJobFilterOptions(string classJobCategoryLabel)
    {
        yield return classJobCategoryLabel;

        foreach (var part in classJobCategoryLabel.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return part;
        }
    }

    private static bool MatchesClassJobFilter(string classJobCategoryLabel, string filter)
    {
        if (string.Equals(classJobCategoryLabel, filter, StringComparison.CurrentCultureIgnoreCase))
        {
            return true;
        }

        return classJobCategoryLabel
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(part => string.Equals(part, filter, StringComparison.CurrentCultureIgnoreCase));
    }

    private string GetClassJobCategoryLabel(uint classJobCategoryId)
    {
        if (classJobCategoryId == 0 || !dataManager.GetExcelSheet<ClassJobCategory>().TryGetRow(classJobCategoryId, out var category))
        {
            return "Unknown";
        }

        if (IsAllClassCategory(category))
        {
            return "All Classes";
        }

        var jobs = GetClassJobCategoryAbbreviations(category);
        return jobs.Count == 0 ? $"Category {classJobCategoryId}" : string.Join("/", jobs);
    }

    private static List<string> GetClassJobCategoryAbbreviations(ClassJobCategory category)
    {
        var jobs = new List<string>();
        if (category.GLA) jobs.Add("GLA");
        if (category.PLD) jobs.Add("PLD");
        if (category.MRD) jobs.Add("MRD");
        if (category.WAR) jobs.Add("WAR");
        if (category.DRK) jobs.Add("DRK");
        if (category.GNB) jobs.Add("GNB");
        if (category.CNJ) jobs.Add("CNJ");
        if (category.WHM) jobs.Add("WHM");
        if (category.SCH) jobs.Add("SCH");
        if (category.AST) jobs.Add("AST");
        if (category.SGE) jobs.Add("SGE");
        if (category.PGL) jobs.Add("PGL");
        if (category.MNK) jobs.Add("MNK");
        if (category.LNC) jobs.Add("LNC");
        if (category.DRG) jobs.Add("DRG");
        if (category.ROG) jobs.Add("ROG");
        if (category.NIN) jobs.Add("NIN");
        if (category.SAM) jobs.Add("SAM");
        if (category.RPR) jobs.Add("RPR");
        if (category.VPR) jobs.Add("VPR");
        if (category.ARC) jobs.Add("ARC");
        if (category.BRD) jobs.Add("BRD");
        if (category.MCH) jobs.Add("MCH");
        if (category.DNC) jobs.Add("DNC");
        if (category.THM) jobs.Add("THM");
        if (category.BLM) jobs.Add("BLM");
        if (category.ACN) jobs.Add("ACN");
        if (category.SMN) jobs.Add("SMN");
        if (category.RDM) jobs.Add("RDM");
        if (category.BLU) jobs.Add("BLU");
        if (category.PCT) jobs.Add("PCT");
        if (category.CRP) jobs.Add("CRP");
        if (category.BSM) jobs.Add("BSM");
        if (category.ARM) jobs.Add("ARM");
        if (category.GSM) jobs.Add("GSM");
        if (category.LTW) jobs.Add("LTW");
        if (category.WVR) jobs.Add("WVR");
        if (category.ALC) jobs.Add("ALC");
        if (category.CUL) jobs.Add("CUL");
        if (category.MIN) jobs.Add("MIN");
        if (category.BTN) jobs.Add("BTN");
        if (category.FSH) jobs.Add("FSH");
        return jobs;
    }

    private unsafe string GetCandidateLocationText(ArmoireCandidate candidate)
    {
        var manager = InventoryManager.Instance();
        if (manager == null)
        {
            return "-";
        }

        var locations = candidate.Source switch
        {
            ItemCollectionSource.Inventory => FindCandidateLocations(manager, candidate.ItemId, InventoryContainerTypes.Inventory, GetInventoryPageLabel, 5),
            ItemCollectionSource.Armoury => FindCandidateLocations(manager, candidate.ItemId, InventoryContainerTypes.Armoury, GetArmouryPageLabel, 5),
            ItemCollectionSource.Saddlebag => FindCandidateLocations(manager, candidate.ItemId, InventoryContainerTypes.Saddlebag, GetSaddlebagPageLabel, 5),
            ItemCollectionSource.Retainer => FindCandidateLocations(manager, candidate.ItemId, InventoryContainerTypes.Retainer, GetRetainerPageLabel, 5),
            _ => [],
        };

        return locations.Count == 0 ? "-" : string.Join(", ", locations);
    }

    private unsafe List<string> FindCandidateLocations(InventoryManager* manager, uint itemId, IReadOnlyList<InventoryType> inventoryTypes, Func<InventoryType, int, string> pageLabelFactory, int columns)
    {
        var locations = new List<string>();
        for (var pageIndex = 0; pageIndex < inventoryTypes.Count; pageIndex++)
        {
            var inventoryType = inventoryTypes[pageIndex];
            var container = manager->GetInventoryContainer(inventoryType);
            if (container == null || !container->IsLoaded)
            {
                continue;
            }

            for (var slot = 0; slot < container->Size; slot++)
            {
                var item = container->GetInventorySlot(slot);
                if (item == null || NormalizeItemId(item->ItemId) != itemId)
                {
                    continue;
                }

                var rawSlot = slot;
                var containerSize = container->Size;
                log.Debug($"[AkuItemSets] RAW inventoryType={inventoryType} itemId={itemId} rawItemId={item->ItemId} rawSlot={rawSlot} containerSize={containerSize} columns={columns}");

                var displaySlot = Math.Max(0, containerSize - 1 - rawSlot);
                var row = (displaySlot / columns) + 1;
                var column = (displaySlot % columns) + 1;
                log.Debug($"[AkuItemSets] CALC inventoryType={inventoryType} itemId={itemId} displaySlot={displaySlot} row={row} column={column}");

                locations.Add($"{pageLabelFactory(inventoryType, pageIndex)} X:{row} Y:{column}");
            }
        }

        return locations;
    }

    private static string GetInventoryPageLabel(InventoryType inventoryType, int pageIndex)
        => $"[{pageIndex + 1}]";

    private static string GetSaddlebagPageLabel(InventoryType inventoryType, int pageIndex)
        => inventoryType switch
        {
            InventoryType.SaddleBag1 => "[Saddlebag 1]",
            InventoryType.SaddleBag2 => "[Saddlebag 2]",
            InventoryType.PremiumSaddleBag1 => "[Premium Saddlebag 1]",
            InventoryType.PremiumSaddleBag2 => "[Premium Saddlebag 2]",
            _ => $"[{pageIndex + 1}]",
        };

    private static string GetRetainerPageLabel(InventoryType inventoryType, int pageIndex)
        => inventoryType == InventoryType.RetainerEquippedItems ? "[Retainer Equipped]" : $"[Retainer {pageIndex + 1}]";

    private static string GetArmouryPageLabel(InventoryType inventoryType, int pageIndex)
        => inventoryType switch
        {
            InventoryType.EquippedItems => "[Equipped]",
            InventoryType.ArmoryMainHand => "[Main Hand]",
            InventoryType.ArmoryOffHand => "[Off Hand]",
            InventoryType.ArmoryHead => "[Head]",
            InventoryType.ArmoryBody => "[Body]",
            InventoryType.ArmoryHands => "[Hands]",
            InventoryType.ArmoryLegs => "[Legs]",
            InventoryType.ArmoryFeets => "[Feet]",
            InventoryType.ArmoryEar => "[Earrings]",
            InventoryType.ArmoryNeck => "[Necklace]",
            InventoryType.ArmoryWrist => "[Bracelets]",
            InventoryType.ArmoryRings => "[Rings]",
            InventoryType.ArmorySoulCrystal => "[Soul Crystal]",
            _ => $"[{pageIndex + 1}]",
        };

    private unsafe void DrawArmoireCandidateStorageOverlay(CharacterCollectionSnapshot snapshot)
    {
        var allCandidates = GetArmoireCandidates(snapshot);
        if (allCandidates.Count == 0)
        {
            return;
        }

        foreach (var target in ArmoireCandidateOverlayTargets)
        {
            if (!TryGetVisibleAddon(target.AddonName, out var unit))
            {
                continue;
            }

            var candidateItemIds = allCandidates
                .Where(candidate => target.Sources.Contains(candidate.Source))
                .Select(candidate => candidate.ItemId)
                .ToHashSet();

            if (candidateItemIds.Count == 0)
            {
                continue;
            }

            if (target.InventoryTypes.Length > 0 && TryDrawInventoryBackedHighlights(unit, target.InventoryTypes, candidateItemIds))
            {
                continue;
            }

            DrawCandidateSlotHighlights(unit, candidateItemIds);
        }
    }

    private unsafe bool TryGetVisibleAddon(string addonName, out AtkUnitBase* unit)
    {
        unit = null;

        var addon = gameGui.GetAddonByName(addonName, 1);
        if (addon.Address == nint.Zero)
        {
            return false;
        }

        unit = (AtkUnitBase*)addon.Address;
        if (!unit->IsVisible || unit->RootNode == null)
        {
            unit = null;
            return false;
        }

        return true;
    }

    private static unsafe void AbsolutePosition(AtkResNode* node, out float x, out float y, out float scaleX, out float scaleY)
    {
        x = 0.0f;
        y = 0.0f;
        scaleX = 1.0f;
        scaleY = 1.0f;

        var current = node;
        while (current != null)
        {
            x = current->X + (x * current->ScaleX);
            y = current->Y + (y * current->ScaleY);
            scaleX *= current->ScaleX;
            scaleY *= current->ScaleY;
            current = current->ParentNode;
        }
    }

    private unsafe bool TryDrawInventoryBackedHighlights(AtkUnitBase* unit, IReadOnlyList<InventoryType> inventoryTypes, IReadOnlySet<uint> candidateItemIds)
    {
        var manager = InventoryManager.Instance();
        if (manager == null)
        {
            return false;
        }

        var slotItemIds = CollectInventorySlotItemIds(manager, inventoryTypes);
        if (slotItemIds.Count == 0)
        {
            return false;
        }

        var slotRects = FindSlotRects(unit);
        if (slotRects.Count == 0)
        {
            DrawOverlayStatus(unit, "AkuItemSets: no slot rectangles found");
            return false;
        }

        var nonEmptyItemIds = slotItemIds.Where(itemId => itemId != 0).ToList();
        var itemIdsForRects = slotRects.Count <= nonEmptyItemIds.Count + 5
            ? nonEmptyItemIds
            : slotItemIds;

        var highlightedAny = false;
        var count = Math.Min(slotRects.Count, itemIdsForRects.Count);
        for (var i = 0; i < count; i++)
        {
            if (!candidateItemIds.Contains(itemIdsForRects[i]))
            {
                continue;
            }

            DrawSlotHighlight(slotRects[i]);
            highlightedAny = true;
        }

        if (!highlightedAny)
        {
            DrawOverlayStatus(unit, $"AkuItemSets: {candidateItemIds.Count} candidates, {slotRects.Count} slots");
        }

        return true;
    }

    private static unsafe List<uint> CollectInventorySlotItemIds(InventoryManager* manager, IReadOnlyList<InventoryType> inventoryTypes)
    {
        var itemIds = new List<uint>();
        foreach (var inventoryType in inventoryTypes)
        {
            var container = manager->GetInventoryContainer(inventoryType);
            if (container == null || !container->IsLoaded)
            {
                continue;
            }

            for (var slot = 0; slot < container->Size; slot++)
            {
                var item = container->GetInventorySlot(slot);
                itemIds.Add(item == null ? 0 : NormalizeItemId(item->ItemId));
            }
        }

        return itemIds;
    }

    private unsafe List<SlotRect> FindSlotRects(AtkUnitBase* unit)
    {
        var rects = new List<SlotRect>();
        var visitedNodes = new HashSet<nint>();

        if (unit->RootNode != null)
        {
            CollectSlotRects(unit->RootNode, visitedNodes, rects);
        }

        if (unit->UldManager.NodeList != null && unit->UldManager.NodeListCount > 0)
        {
            for (var i = 0; i < unit->UldManager.NodeListCount; i++)
            {
                CollectSlotRects(unit->UldManager.NodeList[i], visitedNodes, rects);
            }
        }

        var candidates = rects
            .GroupBy(rect => $"{MathF.Round(rect.Position.X)}:{MathF.Round(rect.Position.Y)}")
            .Select(group => group.OrderByDescending(rect => rect.Size.X * rect.Size.Y).First())
            .ToList();

        if (candidates.Count == 0)
        {
            return candidates;
        }

        var medianSize = candidates
            .Select(rect => (rect.Size.X + rect.Size.Y) / 2.0f)
            .OrderBy(value => value)
            .ElementAt(candidates.Count / 2);

        var sizeTolerance = MathF.Max(5.0f, medianSize * 0.18f);
        candidates = candidates
            .Where(rect => MathF.Abs(((rect.Size.X + rect.Size.Y) / 2.0f) - medianSize) <= sizeTolerance)
            .ToList();

        if (candidates.Count <= 1)
        {
            return candidates;
        }

        var clusteredRows = candidates
            .GroupBy(rect => (int)MathF.Round(rect.Center.Y / MathF.Max(1.0f, medianSize * 0.65f)))
            .Select(group => group.OrderBy(rect => rect.Position.X).ToList())
            .Where(group => group.Count >= 2)
            .OrderBy(group => group.Average(rect => rect.Position.Y))
            .ToList();

        if (clusteredRows.Count == 0)
        {
            return candidates
                .OrderBy(rect => rect.Position.Y)
                .ThenBy(rect => rect.Position.X)
                .ToList();
        }

        var densestRows = clusteredRows
            .Where(row => row.Count >= Math.Min(4, clusteredRows.Max(candidate => candidate.Count)))
            .SelectMany(row => row)
            .ToList();

        return densestRows
            .OrderBy(rect => rect.Center.Y)
            .ThenBy(rect => rect.Position.X)
            .ToList();
    }

    private unsafe void CollectSlotRects(AtkResNode* startNode, HashSet<nint> visitedNodes, List<SlotRect> rects)
    {
        for (var node = startNode; node != null; node = node->NextSiblingNode)
        {
            if (!visitedNodes.Add((nint)node))
            {
                continue;
            }

            TryAddSlotRect(node, rects);

            if (node->Type == NodeType.Component)
            {
                var componentNode = (AtkComponentNode*)node;
                if (componentNode->Component != null && componentNode->Component->UldManager.NodeList != null)
                {
                    for (var i = 0; i < componentNode->Component->UldManager.NodeListCount; i++)
                    {
                        CollectSlotRects(componentNode->Component->UldManager.NodeList[i], visitedNodes, rects);
                    }
                }
            }

            if (node->ChildNode != null)
            {
                CollectSlotRects(node->ChildNode, visitedNodes, rects);
            }
        }
    }

    private static unsafe void TryAddSlotRect(AtkResNode* node, List<SlotRect> rects)
    {
        if (node == null || !node->IsVisible())
        {
            return;
        }

        AbsolutePosition(node, out var x, out var y, out var scaleX, out var scaleY);
        var size = new Vector2(node->Width * scaleX, node->Height * scaleY);
        if (size.X < 28.0f || size.Y < 28.0f || size.X > 72.0f || size.Y > 72.0f)
        {
            return;
        }

        var ratio = size.X / size.Y;
        if (ratio < 0.65f || ratio > 1.35f)
        {
            return;
        }

        rects.Add(new SlotRect(new Vector2(x, y) + ImGui.GetMainViewport().Pos, size));
    }

    private unsafe void DrawCandidateSlotHighlights(AtkUnitBase* unit, IReadOnlySet<uint> candidateItemIds)
    {
        if (unit->RootNode == null)
        {
            return;
        }

        var candidateIconIds = candidateItemIds
            .Select(GetItemIconId)
            .Where(iconId => iconId != 0)
            .ToHashSet();

        if (candidateIconIds.Count == 0)
        {
            return;
        }

        var visitedNodes = new HashSet<nint>();
        DrawCandidateSlotHighlights(unit->RootNode, candidateIconIds, visitedNodes);

        if (unit->UldManager.NodeList == null || unit->UldManager.NodeListCount <= 0)
        {
            return;
        }

        for (var i = 0; i < unit->UldManager.NodeListCount; i++)
        {
            DrawCandidateSlotHighlights(unit->UldManager.NodeList[i], candidateIconIds, visitedNodes);
        }
    }

    private unsafe void DrawCandidateSlotHighlights(AtkResNode* startNode, IReadOnlySet<uint> candidateIconIds, HashSet<nint> visitedNodes)
    {
        for (var node = startNode; node != null; node = node->NextSiblingNode)
        {
            if (!visitedNodes.Add((nint)node))
            {
                continue;
            }

            TryDrawCandidateSlotHighlight(node, candidateIconIds, visitedNodes);

            if (node->ChildNode != null)
            {
                DrawCandidateSlotHighlights(node->ChildNode, candidateIconIds, visitedNodes);
            }
        }
    }

    private unsafe void TryDrawCandidateSlotHighlight(AtkResNode* node, IReadOnlySet<uint> candidateIconIds, HashSet<nint> visitedNodes)
    {
        if (node == null || !node->IsVisible() || node->Type != NodeType.Component)
        {
            return;
        }

        var componentNode = (AtkComponentNode*)node;
        if (componentNode->Component == null)
        {
            return;
        }

        if (componentNode->Component->UldManager.NodeList != null && componentNode->Component->UldManager.NodeListCount > 0)
        {
            for (var i = 0; i < componentNode->Component->UldManager.NodeListCount; i++)
            {
                DrawCandidateSlotHighlights(componentNode->Component->UldManager.NodeList[i], candidateIconIds, visitedNodes);
            }
        }

        var componentType = componentNode->Component->GetComponentType();
        var iconComponent = componentType switch
        {
            ComponentType.DragDrop => ((AtkComponentDragDrop*)componentNode->Component)->AtkComponentIcon,
            ComponentType.Icon => (AtkComponentIcon*)componentNode->Component,
            _ => null,
        };

        if (iconComponent == null || iconComponent->IconId == 0 || !candidateIconIds.Contains(iconComponent->IconId))
        {
            return;
        }

        var highlightNode = iconComponent->OuterResNode != null
            ? iconComponent->OuterResNode
            : iconComponent->OwnerNode != null
                ? (AtkResNode*)iconComponent->OwnerNode
                : node;

        DrawSlotHighlight(highlightNode);
    }

    private static unsafe void DrawSlotHighlight(AtkResNode* node)
    {
        AbsolutePosition(node, out var x, out var y, out var scaleX, out var scaleY);
        DrawSlotHighlight(new SlotRect(new Vector2(x, y) + ImGui.GetMainViewport().Pos, new Vector2(node->Width * scaleX, node->Height * scaleY)));
    }

    private static void DrawSlotHighlight(SlotRect rect)
    {
        if (rect.Size.X < 16.0f || rect.Size.Y < 16.0f || rect.Size.X > 96.0f || rect.Size.Y > 96.0f)
        {
            return;
        }

        var pulsePhase = (float)((DateTime.UtcNow.TimeOfDay.TotalSeconds % 1.4) / 1.4);
        var pulse = 0.6f + (0.4f * MathF.Sin(pulsePhase * MathF.PI * 2.0f));
        var drawList = ImGui.GetForegroundDrawList();
        var edge = ImGui.GetColorU32(new Vector4(0.35f, 0.95f, 0.45f, pulse));
        var fill = ImGui.GetColorU32(new Vector4(0.35f, 0.95f, 0.45f, pulse * 0.22f));
        var min = rect.Position - new Vector2(2.0f, 2.0f);
        var max = rect.Position + rect.Size + new Vector2(2.0f, 2.0f);

        drawList.AddRectFilled(min, max, fill, 5.0f);
        drawList.AddRect(min, max, edge, 5.0f, ImDrawFlags.None, 3.0f);
    }

    private unsafe void DrawOverlayStatus(AtkUnitBase* unit, string message)
    {
        if (unit->RootNode == null)
        {
            return;
        }

        AbsolutePosition(unit->RootNode, out var x, out var y, out _, out _);
        var position = new Vector2(x, y) + ImGui.GetMainViewport().Pos + new Vector2(8.0f, 28.0f);
        var drawList = ImGui.GetForegroundDrawList();
        var textSize = ImGui.CalcTextSize(message);
        drawList.AddRectFilled(position - new Vector2(6.0f, 4.0f), position + textSize + new Vector2(6.0f, 4.0f), ImGui.GetColorU32(new Vector4(0.02f, 0.02f, 0.02f, 0.78f)), 4.0f);
        drawList.AddText(position, ImGui.GetColorU32(new Vector4(0.35f, 0.95f, 0.45f, 1.0f)), message);
    }

    private uint GetItemIconId(uint itemId)
    {
        return dataManager.GetExcelSheet<Item>().TryGetRow(itemId, out var item)
            ? (uint)item.Icon
            : 0;
    }

    private static uint NormalizeItemId(uint itemId)
        => itemId >= 1_000_000
            ? itemId % 1_000_000
            : itemId;

    private List<ArmoireCandidate> GetArmoireCandidates(CharacterCollectionSnapshot snapshot)
        => snapshot.Items.Values
            .Where(ownership => GetArmoireItemIds().Contains(ownership.ItemId))
            .Where(ownership => !ownership.CountsBySource.ContainsKey(ItemCollectionSource.Armoire))
            .SelectMany(ownership => ownership.CountsBySource
                .Where(source => IsArmoireCandidateSource(source.Key))
                .Select(source => new ArmoireCandidate(ownership.ItemId, source.Key, source.Value)))
            .OrderBy(candidate => candidate.Source)
            .ThenByDescending(candidate => candidate.ItemId)
            .ToList();

    private void DrawSetRow(CharacterCollectionSnapshot snapshot, ItemSetDefinition set)
    {
        var filteredPieces = GetPiecesForActiveFilter(set);
        var collectedPieces = filteredPieces.Where(piece => IsPieceInCollectionStorage(snapshot, piece.ItemId)).ToList();
        var ownedPieces = filteredPieces.Where(piece => snapshot.Items.ContainsKey(piece.ItemId)).ToList();
        var missingPieces = filteredPieces.Where(piece => !snapshot.Items.ContainsKey(piece.ItemId)).ToList();
        var complete = IsSetCompleteInCollectionStorage(snapshot, filteredPieces);

        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        if (filteredPieces.Count > 0 && filteredPieces.All(piece => GetArmoireItemIds().Contains(piece.ItemId)))
        {
            ImGui.TextColored(new Vector4(0.35f, 0.95f, 0.45f, 1.0f), set.SetName);
        }
        else
        {
            ImGui.TextUnformatted(set.SetName);
        }

        ImGui.TextDisabled($"#{set.SetItemId}");
        var lootSourceText = GetLootSourceDisplayText(set);
        if (!string.IsNullOrWhiteSpace(lootSourceText))
        {
            ImGui.TextDisabled($"{localization["table.obtainable"]}: {lootSourceText}");
        }

        ImGui.TableNextColumn();
        DrawLootSource(set);

        ImGui.TableNextColumn();
        ImGui.TextUnformatted($"{collectedPieces.Count}/{filteredPieces.Count}");
        var fraction = filteredPieces.Count == 0 ? 0 : (float)collectedPieces.Count / filteredPieces.Count;
        ImGui.ProgressBar(fraction, new Vector2(-1, 0), complete ? "Done" : string.Empty);

        ImGui.TableNextColumn();
        DrawPieces(snapshot, missingPieces);

        ImGui.TableNextColumn();
        DrawPieces(snapshot, configuration.ShowOnlyMissingPieces ? Array.Empty<ItemSetPiece>() : ownedPieces);

        ImGui.TableNextColumn();
        if (ImGui.SmallButton($"{localization["action.copyIsearch"]}##{set.SetItemId}"))
        {
            ImGui.SetClipboardText($"/isearch \"{set.SetName}\"");
        }
    }

    private string GetLootSourceDisplayText(ItemSetDefinition set)
    {
        var localized = GetLocalizedLootSourceName(set);
        if (!string.IsNullOrWhiteSpace(localized))
        {
            return localized;
        }

        if (!string.IsNullOrWhiteSpace(set.LootSourceName))
        {
            return set.LootSourceName;
        }

        return GetFallbackOutfitSourceName(set.SetItemId);
    }

    private void DrawLootSource(ItemSetDefinition set)
    {
        var sourceName = GetLootSourceDisplayText(set);
        if (string.IsNullOrWhiteSpace(sourceName))
        {
            ImGui.TextDisabled("-");
            return;
        }

        ImGui.TextWrapped(sourceName);
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(sourceName);
            if (!string.IsNullOrWhiteSpace(set.LootSourceName) && !string.Equals(sourceName, set.LootSourceName, StringComparison.CurrentCultureIgnoreCase))
            {
                ImGui.TextDisabled(set.LootSourceName);
            }

            if (!string.IsNullOrWhiteSpace(set.LootSourceUrl))
            {
                ImGui.TextDisabled(set.LootSourceUrl);
            }

            ImGui.EndTooltip();
        }
    }

    private void DrawPieces(CharacterCollectionSnapshot snapshot, IReadOnlyCollection<ItemSetPiece> pieces)
    {
        if (pieces.Count == 0)
        {
            ImGui.TextDisabled("-");
            return;
        }

        foreach (var piece in pieces)
        {
            DrawPieceIcon(snapshot, piece);
            ImGui.SameLine(0, 4);
        }

        ImGui.NewLine();
    }

    private void DrawPieceIcon(CharacterCollectionSnapshot snapshot, ItemSetPiece piece)
    {
        DrawSearchableItem(piece.IconId, piece.Name);

        if (ImGui.IsItemClicked())
        {
            ImGui.SetClipboardText($"/isearch \"{piece.Name}\"");
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(piece.Name);
            ImGui.TextDisabled($"{piece.Slot} | Item #{piece.ItemId}");
            if (snapshot.Items.TryGetValue(piece.ItemId, out var ownership))
            {
                ImGui.TextUnformatted(string.Join(", ", ownership.CountsBySource.Keys.Select(source => GetSourceLabel(source, localization))));
            }
            else
            {
                ImGui.TextDisabled(localization["table.missing"]);
            }

            ImGui.Separator();
            ImGui.TextDisabled(localization["hint.clickCopy"]);
            ImGui.EndTooltip();
        }
    }

    private void DrawSearchableItem(uint iconId, string itemName)
    {
        var texture = textureProvider.GetFromGameIcon(new GameIconLookup(iconId)).GetWrapOrEmpty();
        var size = new Vector2(32, 32);
        ImGui.Image(texture.Handle, size);
        if (ImGui.IsItemClicked())
        {
            ImGui.SetClipboardText($"/isearch \"{itemName}\"");
        }
    }

    private static bool IsSetCompleteInCollectionStorage(CharacterCollectionSnapshot snapshot, IReadOnlyCollection<ItemSetPiece> pieces)
        => pieces.Count > 0 && pieces.All(piece => IsPieceInCollectionStorage(snapshot, piece.ItemId));

    private static bool IsPieceInCollectionStorage(CharacterCollectionSnapshot snapshot, uint itemId)
        => snapshot.Items.TryGetValue(itemId, out var ownership)
            && (ownership.CountsBySource.ContainsKey(ItemCollectionSource.GlamourDresser)
                || ownership.CountsBySource.ContainsKey(ItemCollectionSource.GlamourDresserSet)
                || ownership.CountsBySource.ContainsKey(ItemCollectionSource.Armoire));

    private bool MatchesFilter(ItemSetDefinition set)
        => GetPiecesForActiveFilter(set).Count > 0;

    private bool MatchesCurrentInstanceLootFilter(ItemSetDefinition set)
    {
        if (!configuration.ShowOnlyCurrentInstanceLoot)
        {
            return true;
        }

        var currentTerritoryId = (uint)clientState.TerritoryType;
        if (currentTerritoryId == 0)
        {
            return false;
        }

        if (set.LootSourceTerritoryTypeIds?.Contains(currentTerritoryId) == true)
        {
            return true;
        }

        var fallback = GetFallbackOutfitSource(set.SetItemId);
        if (fallback?.TerritoryTypeIds.Contains(currentTerritoryId) == true)
        {
            return true;
        }

        var currentNames = GetTerritoryNames(currentTerritoryId).Select(NormalizeLookupName).Where(name => name.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (currentNames.Count == 0)
        {
            return false;
        }

        return GetLootSourceNames(set).Select(NormalizeLookupName).Any(currentNames.Contains)
            || GetLocalizedLootSourceNames(set).Select(NormalizeLookupName).Any(currentNames.Contains);
    }

    private string GetCurrentInstanceLootTooltip()
    {
        var currentTerritoryId = (uint)clientState.TerritoryType;
        if (currentTerritoryId == 0)
        {
            return localization["toolbar.onlyCurrentInstanceNoTerritory"];
        }

        var currentName = GetLocalizedTerritoryName(currentTerritoryId);
        return string.Format(localization["toolbar.onlyCurrentInstanceHint"], string.IsNullOrWhiteSpace(currentName) ? $"#{currentTerritoryId}" : currentName);
    }

    private string GetLocalizedLootSourceName(ItemSetDefinition set)
    {
        foreach (var contentFinderConditionId in set.LootSourceContentFinderConditionIds ?? [])
        {
            var name = GetLocalizedContentFinderConditionName(contentFinderConditionId);
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
        }

        var fallback = GetFallbackOutfitSource(set.SetItemId);
        foreach (var contentFinderConditionId in fallback?.ContentFinderConditionIds ?? [])
        {
            var name = GetLocalizedContentFinderConditionName(contentFinderConditionId);
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
        }

        foreach (var territoryId in set.LootSourceTerritoryTypeIds ?? [])
        {
            var name = GetLocalizedTerritoryName(territoryId);
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
        }

        foreach (var territoryId in fallback?.TerritoryTypeIds ?? [])
        {
            var name = GetLocalizedTerritoryName(territoryId);
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
        }

        if (!string.IsNullOrWhiteSpace(set.LootSourceName))
        {
            return set.LootSourceName;
        }

        if (!string.IsNullOrWhiteSpace(fallback?.SourceName))
        {
            return fallback.SourceName;
        }

        return set.LootSourceAliases?.FirstOrDefault(alias => !string.IsNullOrWhiteSpace(alias)) ?? string.Empty;
    }

    private string GetLocalizedContentFinderConditionName(uint contentFinderConditionId)
    {
        if (!dataManager.GetExcelSheet<ContentFinderCondition>().TryGetRow(contentFinderConditionId, out var condition))
        {
            return string.Empty;
        }

        return condition.Name.ToString();
    }

    private IEnumerable<string> GetLocalizedLootSourceNames(ItemSetDefinition set)
    {
        foreach (var contentFinderConditionId in set.LootSourceContentFinderConditionIds ?? [])
        {
            var name = GetLocalizedContentFinderConditionName(contentFinderConditionId);
            if (!string.IsNullOrWhiteSpace(name))
            {
                yield return name;
            }
        }

        var fallback = GetFallbackOutfitSource(set.SetItemId);
        foreach (var contentFinderConditionId in fallback?.ContentFinderConditionIds ?? [])
        {
            var name = GetLocalizedContentFinderConditionName(contentFinderConditionId);
            if (!string.IsNullOrWhiteSpace(name))
            {
                yield return name;
            }
        }

        foreach (var territoryId in set.LootSourceTerritoryTypeIds ?? [])
        {
            var name = GetLocalizedTerritoryName(territoryId);
            if (!string.IsNullOrWhiteSpace(name))
            {
                yield return name;
            }
        }

        foreach (var territoryId in fallback?.TerritoryTypeIds ?? [])
        {
            var name = GetLocalizedTerritoryName(territoryId);
            if (!string.IsNullOrWhiteSpace(name))
            {
                yield return name;
            }
        }
    }

    private string GetFallbackOutfitSourceName(uint setId)
    {
        return GetFallbackOutfitSource(setId)?.SourceName ?? string.Empty;
    }

    private FallbackOutfitSource? GetFallbackOutfitSource(uint setId)
    {
        var sources = GetFallbackOutfitSourcesBySetId();
        return sources.TryGetValue(setId, out var source) ? source : null;
    }

    private Dictionary<uint, FallbackOutfitSource> GetFallbackOutfitSourcesBySetId()
    {
        if (fallbackOutfitSourcesBySetId != null)
        {
            return fallbackOutfitSourcesBySetId;
        }

        fallbackOutfitSourcesBySetId = new Dictionary<uint, FallbackOutfitSource>();
        var path = ItemSetRepository.GetOutfitSourcesPath(Plugin.PluginInterface);
        if (!File.Exists(path))
        {
            log.Warning($"[AkuItemSets] UI source fallback could not find outfit_sources.json at {path}");
            return fallbackOutfitSourcesBySetId;
        }

        try
        {
            var records = JsonSerializer.Deserialize<List<OutfitSourceRecord>>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
            foreach (var group in records.Where(record => record.SetId > 0).GroupBy(record => record.SetId))
            {
                var best = group
                    .OrderBy(record => IsGenericStoreSource(record.SourceName))
                    .ThenBy(record => record.SourceName, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                var sourceName = best?.SourceName;
                if (!string.IsNullOrWhiteSpace(sourceName))
                {
                    fallbackOutfitSourcesBySetId[group.Key] = new FallbackOutfitSource(
                        sourceName,
                        best?.SourceContentFinderConditionIds ?? [],
                        best?.SourceTerritoryTypeIds ?? []);
                }
            }

            log.Debug($"[AkuItemSets] UI source fallback loaded {fallbackOutfitSourcesBySetId.Count} set-id source names from {path}");
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"[AkuItemSets] UI source fallback could not read outfit_sources.json from {path}");
        }

        return fallbackOutfitSourcesBySetId;
    }

    private static bool IsGenericStoreSource(string? sourceName)
        => sourceName != null
            && (sourceName.Equals("Online Store", StringComparison.OrdinalIgnoreCase)
                || sourceName.Equals("Mog Station", StringComparison.OrdinalIgnoreCase));

    private string GetLocalizedTerritoryName(uint territoryId)
    {
        if (!dataManager.GetExcelSheet<TerritoryType>().TryGetRow(territoryId, out var territory) || !territory.PlaceName.IsValid)
        {
            return string.Empty;
        }

        return territory.PlaceName.Value.Name.ToString();
    }

    private IEnumerable<string> GetTerritoryNames(uint territoryId)
    {
        foreach (var language in new[] { ClientLanguage.English, ClientLanguage.German, ClientLanguage.French, ClientLanguage.Japanese })
        {
            var sheet = dataManager.GetExcelSheet<TerritoryType>(language);
            if (sheet.TryGetRow(territoryId, out var territory) && territory.PlaceName.IsValid)
            {
                var name = territory.PlaceName.Value.Name.ToString();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    yield return name;
                }
            }
        }
    }

    private static IEnumerable<string> GetLootSourceNames(ItemSetDefinition set)
    {
        if (!string.IsNullOrWhiteSpace(set.LootSourceName))
        {
            yield return set.LootSourceName;
        }

        foreach (var alias in set.LootSourceAliases ?? [])
        {
            if (!string.IsNullOrWhiteSpace(alias))
            {
                yield return alias;
            }
        }
    }

    private static string NormalizeLookupName(string value)
        => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private IReadOnlyList<ItemSetPiece> GetPiecesForActiveFilter(ItemSetDefinition set)
    {
        if (configuration.FilterMode == CollectionFilterMode.All)
        {
            return set.Pieces;
        }

        if (configuration.FilterMode == CollectionFilterMode.CurrentRole)
        {
            var currentRole = GetCurrentRoleFilterMode();
            return currentRole == CollectionFilterMode.All
                ? set.Pieces
                : set.Pieces.Where(piece => IsAllowedForRole(piece.ClassJobCategoryId, currentRole)).ToList();
        }

        return set.Pieces.Where(piece => IsAllowedForRole(piece.ClassJobCategoryId, configuration.FilterMode)).ToList();
    }

    private bool IsAllowedForRole(uint classJobCategoryId, CollectionFilterMode mode)
    {
        if (!dataManager.GetExcelSheet<ClassJobCategory>().TryGetRow(classJobCategoryId, out var category))
        {
            return true;
        }

        if (!configuration.IncludeAllClassItemsInRoleFilters && IsAllClassCategory(category))
        {
            return false;
        }

        return mode switch
        {
            CollectionFilterMode.Tank => category.GLA || category.PLD || category.MRD || category.WAR || category.DRK || category.GNB,
            CollectionFilterMode.Healer => category.CNJ || category.WHM || category.SCH || category.AST || category.SGE,
            CollectionFilterMode.Melee => category.PGL || category.MNK || category.LNC || category.DRG || category.ROG || category.NIN || category.SAM || category.RPR || category.VPR,
            CollectionFilterMode.PhysicalRanged => category.ARC || category.BRD || category.MCH || category.DNC,
            CollectionFilterMode.Caster => category.THM || category.BLM || category.ACN || category.SMN || category.RDM || category.BLU || category.PCT,
            CollectionFilterMode.Crafter => category.CRP || category.BSM || category.ARM || category.GSM || category.LTW || category.WVR || category.ALC || category.CUL,
            CollectionFilterMode.Gatherer => category.MIN || category.BTN || category.FSH,
            _ => true,
        };
    }

    private static bool IsAllClassCategory(ClassJobCategory category)
        => category.GLA
            && category.PLD
            && category.MRD
            && category.WAR
            && category.DRK
            && category.GNB
            && category.CNJ
            && category.WHM
            && category.SCH
            && category.AST
            && category.SGE
            && category.PGL
            && category.MNK
            && category.LNC
            && category.DRG
            && category.ROG
            && category.NIN
            && category.SAM
            && category.RPR
            && category.VPR
            && category.ARC
            && category.BRD
            && category.MCH
            && category.DNC
            && category.THM
            && category.BLM
            && category.ACN
            && category.SMN
            && category.RDM
            && category.BLU
            && category.PCT
            && category.CRP
            && category.BSM
            && category.ARM
            && category.GSM
            && category.LTW
            && category.WVR
            && category.ALC
            && category.CUL
            && category.MIN
            && category.BTN
            && category.FSH;

    private CollectionFilterMode GetCurrentRoleFilterMode()
    {
        if (!playerState.IsLoaded || !playerState.ClassJob.IsValid)
        {
            return CollectionFilterMode.All;
        }

        return playerState.ClassJob.Value.Abbreviation.ToString() switch
        {
            "GLA" or "PLD" or "MRD" or "WAR" or "DRK" or "GNB" => CollectionFilterMode.Tank,
            "CNJ" or "WHM" or "SCH" or "AST" or "SGE" => CollectionFilterMode.Healer,
            "PGL" or "MNK" or "LNC" or "DRG" or "ROG" or "NIN" or "SAM" or "RPR" or "VPR" => CollectionFilterMode.Melee,
            "ARC" or "BRD" or "MCH" or "DNC" => CollectionFilterMode.PhysicalRanged,
            "THM" or "BLM" or "ACN" or "SMN" or "RDM" or "BLU" or "PCT" => CollectionFilterMode.Caster,
            "CRP" or "BSM" or "ARM" or "GSM" or "LTW" or "WVR" or "ALC" or "CUL" => CollectionFilterMode.Crafter,
            "MIN" or "BTN" or "FSH" => CollectionFilterMode.Gatherer,
            _ => CollectionFilterMode.All,
        };
    }

    private bool MatchesSearch(ItemSetDefinition set, string searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText))
        {
            return true;
        }

        var sourceDisplayText = GetLootSourceDisplayText(set);
        var fallbackSource = GetFallbackOutfitSource(set.SetItemId);

        return set.SetName.Contains(searchText, StringComparison.CurrentCultureIgnoreCase)
            || (!string.IsNullOrWhiteSpace(sourceDisplayText) && sourceDisplayText.Contains(searchText, StringComparison.CurrentCultureIgnoreCase))
            || (!string.IsNullOrWhiteSpace(set.LootSourceName) && set.LootSourceName.Contains(searchText, StringComparison.CurrentCultureIgnoreCase))
            || (set.LootSourceAliases?.Any(alias => alias.Contains(searchText, StringComparison.CurrentCultureIgnoreCase)) == true)
            || (!string.IsNullOrWhiteSpace(fallbackSource?.SourceName) && fallbackSource.SourceName.Contains(searchText, StringComparison.CurrentCultureIgnoreCase))
            || GetLocalizedLootSourceNames(set).Any(name => name.Contains(searchText, StringComparison.CurrentCultureIgnoreCase))
            || (fallbackSource?.TerritoryTypeIds.Any(id => GetTerritoryNames(id).Any(name => name.Contains(searchText, StringComparison.CurrentCultureIgnoreCase))) == true)
            || set.Pieces.Any(piece => piece.Name.Contains(searchText, StringComparison.CurrentCultureIgnoreCase));
    }

    private string GetFilterLabel(CollectionFilterMode mode)
        => mode switch
        {
            CollectionFilterMode.All => localization["filter.all"],
            CollectionFilterMode.CurrentRole => localization["filter.currentRole"],
            CollectionFilterMode.Tank => localization["filter.tank"],
            CollectionFilterMode.Healer => localization["filter.healer"],
            CollectionFilterMode.Melee => localization["filter.melee"],
            CollectionFilterMode.PhysicalRanged => localization["filter.physicalRanged"],
            CollectionFilterMode.Caster => localization["filter.caster"],
            CollectionFilterMode.Crafter => localization["filter.crafter"],
            CollectionFilterMode.Gatherer => localization["filter.gatherer"],
            _ => mode.ToString(),
        };

    private IEnumerable<ItemSetDefinition> ApplySort(IEnumerable<ItemSetDefinition> sets)
        => configuration.SortMode switch
        {
            ItemSetSortMode.ItemSetId when configuration.SortDirection == SortDirection.Descending
                => sets.OrderByDescending(set => set.SetItemId).ThenBy(set => set.SetName, StringComparer.CurrentCultureIgnoreCase),
            ItemSetSortMode.ItemSetId
                => sets.OrderBy(set => set.SetItemId).ThenBy(set => set.SetName, StringComparer.CurrentCultureIgnoreCase),
            _ when configuration.SortDirection == SortDirection.Descending
                => sets.OrderByDescending(set => set.SetName, StringComparer.CurrentCultureIgnoreCase).ThenByDescending(set => set.SetItemId),
            _
                => sets.OrderBy(set => set.SetName, StringComparer.CurrentCultureIgnoreCase).ThenBy(set => set.SetItemId),
        };

    private string GetSortLabel(ItemSetSortMode mode)
        => mode switch
        {
            ItemSetSortMode.Name => localization["sort.name"],
            ItemSetSortMode.ItemSetId => localization["sort.setId"],
            _ => mode.ToString(),
        };

    private string GetSortDirectionLabel(SortDirection direction)
        => direction switch
        {
            SortDirection.Ascending => localization["sort.asc"],
            SortDirection.Descending => localization["sort.desc"],
            _ => direction.ToString(),
        };

    private HashSet<uint> GetArmoireItemIds()
    {
        if (armoireItemIds != null)
        {
            return armoireItemIds;
        }

        armoireItemIds = dataManager.GetExcelSheet<Cabinet>()
            .Where(row => row.Item.RowId != 0)
            .Select(row => row.Item.RowId)
            .ToHashSet();

        foreach (var set in itemSetRepository.GetSets())
        {
            if (!armoireItemIds.Contains(set.SetItemId))
            {
                continue;
            }

            foreach (var piece in set.Pieces)
            {
                armoireItemIds.Add(piece.ItemId);
            }
        }

        return armoireItemIds;
    }

    private static string GetCategoryLabel(ItemCollectionCategory category, Localization localization)
        => category switch
        {
            ItemCollectionCategory.Inventory => localization["category.inventory"],
            ItemCollectionCategory.Armoury => localization["category.armoury"],
            ItemCollectionCategory.Saddlebag => localization["category.saddlebag"],
            ItemCollectionCategory.Retainers => localization["category.retainers"],
            ItemCollectionCategory.GlamourDresser => localization["category.glamourDresser"],
            ItemCollectionCategory.Armoire => localization["category.armoire"],
            _ => category.ToString(),
        };

    private static bool IsArmoireCandidateSource(ItemCollectionSource source)
        => source is ItemCollectionSource.Inventory
            or ItemCollectionSource.Armoury
            or ItemCollectionSource.Saddlebag
            or ItemCollectionSource.Retainer
            or ItemCollectionSource.GlamourDresser
            or ItemCollectionSource.GlamourDresserSet;

    private static string GetSourceLabel(ItemCollectionSource source, Localization localization)
        => source switch
        {
            ItemCollectionSource.Inventory => localization["source.inventory"],
            ItemCollectionSource.Armoury => localization["source.armoury"],
            ItemCollectionSource.Saddlebag => localization["source.saddlebag"],
            ItemCollectionSource.Retainer => localization["source.retainer"],
            ItemCollectionSource.GlamourDresser => localization["source.glamourDresser"],
            ItemCollectionSource.GlamourDresserSet => localization["source.glamourDresserSet"],
            ItemCollectionSource.Armoire => localization["source.armoire"],
            _ => source.ToString(),
        };

    private static readonly IReadOnlyList<ArmoireCandidateOverlayTarget> ArmoireCandidateOverlayTargets =
    [
        new("Inventory", [ItemCollectionSource.Inventory], InventoryContainerTypes.Inventory),
        new("InventoryLarge", [ItemCollectionSource.Inventory], InventoryContainerTypes.Inventory),
        new("InventoryExpansion", [ItemCollectionSource.Inventory], InventoryContainerTypes.Inventory),
        new("InventoryGrid", [ItemCollectionSource.Inventory], InventoryContainerTypes.Inventory),
        new("ArmouryBoard", [ItemCollectionSource.Armoury], InventoryContainerTypes.Armoury),
        new("InventoryBuddy", [ItemCollectionSource.Saddlebag], InventoryContainerTypes.Saddlebag),
        new("InventoryRetainer", [ItemCollectionSource.Retainer], InventoryContainerTypes.Retainer),
        new("InventoryRetainerLarge", [ItemCollectionSource.Retainer], InventoryContainerTypes.Retainer),
        new("MiragePrismPrismBox", [ItemCollectionSource.GlamourDresser, ItemCollectionSource.GlamourDresserSet], []),
    ];

    private sealed record ArmoireCandidate(uint ItemId, ItemCollectionSource Source, int Count);

    private sealed record ArmoireCandidateDisplayRow(ArmoireCandidate Candidate, Item Item, string ClassJobCategoryLabel);

    private sealed record ArmoireCandidateOverlayTarget(string AddonName, ItemCollectionSource[] Sources, InventoryType[] InventoryTypes);

    private sealed record SlotRect(Vector2 Position, Vector2 Size)
    {
        public Vector2 Center => Position + (Size / 2.0f);
    }

    private sealed record FallbackOutfitSource(string SourceName, IReadOnlyList<uint> ContentFinderConditionIds, IReadOnlyList<uint> TerritoryTypeIds);

    private static class InventoryContainerTypes
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
