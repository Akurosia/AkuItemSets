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

    public MainWindow(
        Configuration configuration,
        ItemSetRepository itemSetRepository,
        ItemCollectionScanner scanner,
        IPlayerState playerState,
        IDataManager dataManager,
        ITextureProvider textureProvider)
        : base("AkuItemSets##aku_item_sets")
    {
        this.configuration = configuration;
        this.itemSetRepository = itemSetRepository;
        this.scanner = scanner;
        this.playerState = playerState;
        this.dataManager = dataManager;
        this.textureProvider = textureProvider;

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
            ImGui.TextUnformatted("Log in with a character, then scan to build this character's collection.");
            return;
        }

        ImGui.TextUnformatted($"{snapshot.CharacterName} @ {snapshot.WorldName}");
        ImGui.SameLine();
        ImGui.TextDisabled($"Last scan: {snapshot.LastScanUtc.LocalDateTime:g}");
        ImGui.Separator();

        using var tabs = ImRaii.TabBar("aku_item_sets_tabs");
        if (!tabs.Success)
        {
            return;
        }

        using (var tab = ImRaii.TabItem("Collection"))
        {
            if (tab.Success)
            {
                DrawToolbar();
                DrawSetTable(snapshot);
            }
        }

        using (var tab = ImRaii.TabItem("Scan status"))
        {
            if (tab.Success)
            {
                DrawScanStatus(snapshot);
            }
        }
    }

    private void DrawToolbar()
    {
        ImGui.SetNextItemWidth(220);
        var search = configuration.SearchText;
        if (ImGui.InputTextWithHint("##search", "Search set or item", ref search, 128))
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
        var hideCompleted = configuration.HideCompletedSets;
        if (ImGui.Checkbox("Hide complete", ref hideCompleted))
        {
            configuration.HideCompletedSets = hideCompleted;
            configuration.Save();
        }

        ImGui.SameLine();
        var showOnlyMissing = configuration.ShowOnlyMissingPieces;
        if (ImGui.Checkbox("Only missing pieces", ref showOnlyMissing))
        {
            configuration.ShowOnlyMissingPieces = showOnlyMissing;
            configuration.Save();
        }

        ImGui.SameLine();
        var includeAllClass = configuration.IncludeAllClassItemsInRoleFilters;
        if (ImGui.Checkbox("Include all-class", ref includeAllClass))
        {
            configuration.IncludeAllClassItemsInRoleFilters = includeAllClass;
            configuration.Save();
        }
    }

    private static void DrawScanStatus(CharacterCollectionSnapshot snapshot)
    {
        ImGui.TextUnformatted("Automatic scans run while you are logged in.");
        ImGui.TextDisabled("Categories that depend on game caches update after the game has loaded that storage.");
        ImGui.Spacing();

        using var table = ImRaii.Table("scan_status_table", 2, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable);
        if (!table.Success)
        {
            return;
        }

        ImGui.TableSetupColumn("Category", ImGuiTableColumnFlags.WidthFixed, 180);
        ImGui.TableSetupColumn("Last scanned", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableHeadersRow();

        foreach (var category in Enum.GetValues<ItemCollectionCategory>())
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(GetCategoryLabel(category));
            ImGui.TableNextColumn();
            if (snapshot.LastScanByCategory.TryGetValue(category, out var lastScan))
            {
                ImGui.TextUnformatted(lastScan.LocalDateTime.ToString("g"));
            }
            else
            {
                ImGui.TextDisabled("Not scanned yet");
            }
        }
    }

    private void DrawSetTable(CharacterCollectionSnapshot snapshot)
    {
        var sets = itemSetRepository.GetSets()
            .Where(set => MatchesSearch(set, configuration.SearchText))
            .Where(MatchesFilter)
            .ToList();

        var visibleSets = configuration.HideCompletedSets
            ? sets.Where(set => GetPiecesForActiveFilter(set).Any(piece => !snapshot.Items.ContainsKey(piece.ItemId))).ToList()
            : sets;

        var completeCount = sets.Count(set =>
        {
            var pieces = GetPiecesForActiveFilter(set);
            return pieces.Count > 0 && pieces.All(piece => snapshot.Items.ContainsKey(piece.ItemId));
        });
        ImGui.TextUnformatted($"{completeCount}/{sets.Count} visible sets complete");

        using var table = ImRaii.Table("itemsets_table", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY);
        if (!table.Success)
        {
            return;
        }

        ImGui.TableSetupColumn("Set", ImGuiTableColumnFlags.WidthStretch, 2.0f);
        ImGui.TableSetupColumn("Progress", ImGuiTableColumnFlags.WidthFixed, 90);
        ImGui.TableSetupColumn("Missing", ImGuiTableColumnFlags.WidthStretch, 1.5f);
        ImGui.TableSetupColumn("Owned", ImGuiTableColumnFlags.WidthStretch, 1.5f);
        ImGui.TableSetupColumn("Use", ImGuiTableColumnFlags.WidthFixed, 140);
        ImGui.TableHeadersRow();

        foreach (var set in visibleSets)
        {
            DrawSetRow(snapshot, set);
        }
    }

    private void DrawSetRow(CharacterCollectionSnapshot snapshot, ItemSetDefinition set)
    {
        var filteredPieces = GetPiecesForActiveFilter(set);
        var ownedPieces = filteredPieces.Where(piece => snapshot.Items.ContainsKey(piece.ItemId)).ToList();
        var missingPieces = filteredPieces.Where(piece => !snapshot.Items.ContainsKey(piece.ItemId)).ToList();
        var complete = missingPieces.Count == 0;

        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(set.SetName);
        ImGui.TextDisabled($"#{set.SetItemId}");

        ImGui.TableNextColumn();
        ImGui.TextUnformatted($"{ownedPieces.Count}/{filteredPieces.Count}");
        var fraction = filteredPieces.Count == 0 ? 0 : (float)ownedPieces.Count / filteredPieces.Count;
        ImGui.ProgressBar(fraction, new Vector2(-1, 0), complete ? "Done" : string.Empty);

        ImGui.TableNextColumn();
        DrawPieces(snapshot, missingPieces);

        ImGui.TableNextColumn();
        DrawPieces(snapshot, configuration.ShowOnlyMissingPieces ? Array.Empty<ItemSetPiece>() : ownedPieces);

        ImGui.TableNextColumn();
        if (ImGui.SmallButton($"Copy /isearch##{set.SetItemId}"))
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
        var texture = textureProvider.GetFromGameIcon(new GameIconLookup(piece.IconId)).GetWrapOrEmpty();
        var size = new Vector2(32, 32);
        ImGui.Image(texture.Handle, size);

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
                ImGui.TextUnformatted(string.Join(", ", ownership.CountsBySource.Keys));
            }
            else
            {
                ImGui.TextDisabled("Missing");
            }

            ImGui.Separator();
            ImGui.TextDisabled("Click to copy /isearch");
            ImGui.EndTooltip();
        }
    }

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

    private static string GetFilterLabel(CollectionFilterMode mode)
        => mode switch
        {
            CollectionFilterMode.All => "All",
            CollectionFilterMode.CurrentRole => "Current role",
            CollectionFilterMode.Tank => "Tank",
            CollectionFilterMode.Healer => "Healer",
            CollectionFilterMode.Melee => "Melee",
            CollectionFilterMode.PhysicalRanged => "Physical ranged",
            CollectionFilterMode.Caster => "Caster",
            CollectionFilterMode.Crafter => "Crafter",
            CollectionFilterMode.Gatherer => "Gatherer",
            _ => mode.ToString(),
        };

    private static string GetCategoryLabel(ItemCollectionCategory category)
        => category switch
        {
            ItemCollectionCategory.Inventory => "Inventory",
            ItemCollectionCategory.Armoury => "Armoury chest",
            ItemCollectionCategory.Saddlebag => "Saddlebag",
            ItemCollectionCategory.Retainers => "Retainers",
            ItemCollectionCategory.GlamourDresser => "Glamour dresser",
            ItemCollectionCategory.Armoire => "Armoire",
            _ => category.ToString(),
        };
}
