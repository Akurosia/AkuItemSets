using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AkuItemSets.Services;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace AkuItemSets.Windows;

public sealed class MainWindow : Window
{
    private readonly Configuration configuration;
    private readonly ItemSetRepository itemSetRepository;
    private readonly ItemCollectionScanner scanner;
    private readonly IPlayerState playerState;
    private readonly IDataManager dataManager;
    private readonly ITextureProvider textureProvider;
    private readonly Localization localization;
    private HashSet<uint>? armoireItemIds;

    public MainWindow(
        Configuration configuration,
        ItemSetRepository itemSetRepository,
        ItemCollectionScanner scanner,
        IPlayerState playerState,
        IDataManager dataManager,
        ITextureProvider textureProvider,
        Localization localization)
        : base("AkuItemSets##aku_item_sets")
    {
        this.configuration = configuration;
        this.itemSetRepository = itemSetRepository;
        this.scanner = scanner;
        this.playerState = playerState;
        this.dataManager = dataManager;
        this.textureProvider = textureProvider;
        this.localization = localization;

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
                DrawArmoireCandidates(snapshot);
            }
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

        ImGui.SameLine();
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
        var sets = itemSetRepository.GetSets()
            .Where(set => MatchesSearch(set, configuration.SearchText))
            .Where(MatchesFilter)
            .ToList();

        sets = ApplySort(sets).ToList();

        var visibleSets = configuration.HideCompletedSets
            ? sets.Where(set => !IsSetCompleteInCollectionStorage(snapshot, GetPiecesForActiveFilter(set))).ToList()
            : sets;

        var completeCount = sets.Count(set =>
        {
            var pieces = GetPiecesForActiveFilter(set);
            return IsSetCompleteInCollectionStorage(snapshot, pieces);
        });
        ImGui.TextUnformatted($"{completeCount}/{sets.Count} visible sets complete");

        using var table = ImRaii.Table("itemsets_table", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY);
        if (!table.Success)
        {
            return;
        }

        ImGui.TableSetupColumn(localization["table.set"], ImGuiTableColumnFlags.WidthStretch, 2.0f);
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
        var candidates = snapshot.Items.Values
            .Where(ownership => GetArmoireItemIds().Contains(ownership.ItemId))
            .Where(ownership => !ownership.CountsBySource.ContainsKey(ItemCollectionSource.Armoire))
            .SelectMany(ownership => ownership.CountsBySource
                .Where(source => IsArmoireCandidateSource(source.Key))
                .Select(source => new ArmoireCandidate(ownership.ItemId, source.Key, source.Value)))
            .OrderBy(candidate => candidate.Source)
            .ThenByDescending(candidate => candidate.ItemId)
            .ToList();

        ImGui.TextUnformatted($"{candidates.Count} {localization["armoire.candidatesFound"]}");
        ImGui.TextDisabled(localization["armoire.clickHint"]);
        ImGui.Spacing();

        using var table = ImRaii.Table("armoire_candidates_table", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY);
        if (!table.Success)
        {
            return;
        }

        ImGui.TableSetupColumn(localization["table.item"], ImGuiTableColumnFlags.WidthStretch, 2.5f);
        ImGui.TableSetupColumn(localization["table.source"], ImGuiTableColumnFlags.WidthFixed, 120);
        ImGui.TableSetupColumn(localization["table.count"], ImGuiTableColumnFlags.WidthFixed, 60);
        ImGui.TableSetupColumn(localization["table.id"], ImGuiTableColumnFlags.WidthFixed, 80);
        ImGui.TableHeadersRow();

        foreach (var candidate in candidates)
        {
            if (!dataManager.GetExcelSheet<Item>().TryGetRow(candidate.ItemId, out var item))
            {
                continue;
            }

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
            ImGui.TextUnformatted(GetSourceLabel(candidate.Source, localization));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(candidate.Count.ToString());
            ImGui.TableNextColumn();
            ImGui.TextDisabled(candidate.ItemId.ToString());
        }
    }

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

    private static bool MatchesSearch(ItemSetDefinition set, string searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText))
        {
            return true;
        }

        return set.SetName.Contains(searchText, StringComparison.CurrentCultureIgnoreCase)
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

    private sealed record ArmoireCandidate(uint ItemId, ItemCollectionSource Source, int Count);
}
