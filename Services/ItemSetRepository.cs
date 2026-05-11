using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Dalamud.Game;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace AkuItemSets.Services;

public sealed class ItemSetRepository
{
    private static readonly string[] RoleSuffixes = ["of Casting", "of Healing", "of Scouting", "of Aiming", "of Striking", "of Maiming", "of Fending", "of Slaying"];
    private static readonly string[] GenericOutfitWords = ["Attire", "Armor", "Armour", "Set", "Costume", "Garb", "Dress", "Suit", "Uniform"];
    private static readonly HashSet<string> IgnoredOutfitTokens = new(StringComparer.OrdinalIgnoreCase) { "a", "an", "and", "the", "of", "attire", "armor", "armour", "set", "costume", "garb", "dress", "suit", "uniform" };
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;
    private readonly IDalamudPluginInterface pluginInterface;
    private List<ItemSetDefinition>? cachedSets;
    private List<OutfitSourceRecord>? cachedOutfitSources;
    private Dictionary<string, List<uint>>? cachedTerritoryTypeIdsByEnglishPlaceName;
    private List<TerritoryNameLookup>? cachedTerritoryNameLookup;

    public ItemSetRepository(IDataManager dataManager, IPluginLog log, IDalamudPluginInterface pluginInterface)
    {
        this.dataManager = dataManager;
        this.log = log;
        this.pluginInterface = pluginInterface;
    }

    public IReadOnlyList<ItemSetDefinition> GetSets()
    {
        if (cachedSets != null)
        {
            return cachedSets;
        }

        var outfitSources = GetOutfitSources();
        var result = new List<ItemSetDefinition>();
        var seenSetIds = new HashSet<uint>();
        var lookupSheet = dataManager.GetExcelSheet<MirageStoreSetItemLookup>();
        var setItemSheet = dataManager.GetExcelSheet<MirageStoreSetItem>();
        var englishSetNamesBySetItemId = GetEnglishSetNamesBySetItemId();
        var englishPieceNamesBySetItemId = GetEnglishPieceNamesBySetItemId();
        var matchStats = new OutfitSourceMatchStats();

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
                var setName = GetExcelString(setItem, "Name");
                var englishSetName = englishSetNamesBySetItemId.TryGetValue(itemRef.RowId, out var mappedEnglishSetName) ? mappedEnglishSetName : setName;

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

                if (string.IsNullOrWhiteSpace(setName))
                {
                    setName = pieces[0].Name;
                }

                var iconId = GetExcelUInt(setItem, "Icon");
                if (iconId == 0)
                {
                    iconId = pieces[0].IconId;
                }

                IEnumerable<string> englishPieceNames = englishPieceNamesBySetItemId.TryGetValue(itemRef.RowId, out var mappedEnglishPieceNames) ? mappedEnglishPieceNames : Enumerable.Empty<string>();
                var outfitSource = FindOutfitSource(outfitSources, itemRef.RowId, pieces.Select(piece => piece.ItemId), setName, englishSetName, pieces.Select(piece => piece.Name), englishPieceNames, matchStats);
                if (outfitSource == null)
                {
                    matchStats.AddNoMatchSample(itemRef.RowId, setName, englishSetName, pieces, englishPieceNames);
                }

                var lootSourceName = GetBestSourceName(outfitSource);
                result.Add(new ItemSetDefinition(itemRef.RowId, setName, iconId, pieces, lootSourceName, outfitSource?.SourceUrl, outfitSource?.SourceAliases, outfitSource?.SourceContentFinderConditionIds, outfitSource?.SourceTerritoryTypeIds));
            }
        }

        cachedSets = result.OrderBy(set => set.SetName, StringComparer.CurrentCultureIgnoreCase).ToList();
        LogOutfitSourceMatchSummary(result.Count, outfitSources.Count, matchStats);
        return cachedSets;
    }

    private Dictionary<uint, string> GetEnglishSetNamesBySetItemId()
    {
        var result = new Dictionary<uint, string>();
        var englishLookupSheet = dataManager.GetExcelSheet<MirageStoreSetItemLookup>(ClientLanguage.English);

        foreach (var lookup in englishLookupSheet)
        {
            foreach (var itemRef in lookup.Item)
            {
                if (itemRef.RowId == 0 || result.ContainsKey(itemRef.RowId))
                {
                    continue;
                }

                if (itemRef.IsValid)
                {
                    var englishName = GetExcelString(itemRef.Value, "Name");
                    if (!string.IsNullOrWhiteSpace(englishName))
                    {
                        result[itemRef.RowId] = englishName;
                    }
                }
            }
        }

        return result;
    }

    private Dictionary<uint, List<string>> GetEnglishPieceNamesBySetItemId()
    {
        var result = new Dictionary<uint, List<string>>();
        var englishSetItemSheet = dataManager.GetExcelSheet<MirageStoreSetItem>(ClientLanguage.English);
        var englishItemSheet = dataManager.GetExcelSheet<Item>(ClientLanguage.English);

        foreach (var setRow in englishSetItemSheet)
        {
            var names = new List<string>();
            AddEnglishPieceName(names, setRow.Head, englishItemSheet);
            AddEnglishPieceName(names, setRow.Body, englishItemSheet);
            AddEnglishPieceName(names, setRow.Hands, englishItemSheet);
            AddEnglishPieceName(names, setRow.Legs, englishItemSheet);
            AddEnglishPieceName(names, setRow.Feet, englishItemSheet);
            AddEnglishPieceName(names, setRow.Earrings, englishItemSheet);
            AddEnglishPieceName(names, setRow.Necklace, englishItemSheet);
            AddEnglishPieceName(names, setRow.Bracelets, englishItemSheet);
            AddEnglishPieceName(names, setRow.Ring, englishItemSheet);
            if (names.Count > 0)
            {
                result[setRow.RowId] = names;
            }
        }

        return result;
    }

    private List<OutfitSourceRecord> GetOutfitSources()
    {
        if (cachedOutfitSources != null)
        {
            return cachedOutfitSources;
        }

        cachedOutfitSources = [];
        var path = GetOutfitSourcesPath(pluginInterface);
        log.Debug($"[AkuItemSets] Outfit source lookup path: {path}");
        if (!File.Exists(path))
        {
            log.Warning($"[AkuItemSets] outfit_sources.json not found. Expected path: {path}");
            return cachedOutfitSources;
        }

        try
        {
            cachedOutfitSources = JsonSerializer.Deserialize<List<OutfitSourceRecord>>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
            foreach (var record in cachedOutfitSources)
            {
                record.SetId = record.SetId > 0 ? record.SetId : ExtractOutfitSetId(record.SourceUrl);
                record.ItemIds = record.ItemIds.Where(id => id > 0).Distinct().ToList();
                record.NormalizedSetName = NormalizeLookupName(record.SetName);
                record.NormalizedOutfitName = NormalizeOutfitName(record.SetName);
                record.NormalizedBaseName = NormalizeLookupName(GetOutfitBaseName(record.SetName));
                record.NormalizedRoleSuffix = NormalizeLookupName(GetRoleSuffix(record.SetName));
                record.SourceTerritoryTypeIds = ResolveTerritoryTypeIds(record.SourceName, record.SourceAliases).ToList();
            }

            LogOutfitSourceLoadSummary(path, cachedOutfitSources);
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"[AkuItemSets] Could not read outfit_sources.json from {path}");
            cachedOutfitSources.Clear();
        }

        return cachedOutfitSources;
    }

    public static string GetOutfitSourcesPath(IDalamudPluginInterface pluginInterface)
    {
        if (!string.IsNullOrWhiteSpace(pluginInterface.AssemblyLocation.FullName))
        {
            var pluginDirectory = Path.GetDirectoryName(pluginInterface.AssemblyLocation.FullName);
            if (!string.IsNullOrWhiteSpace(pluginDirectory))
            {
                var pluginPath = Path.Combine(pluginDirectory, "Data", "outfit_sources.json");
                if (File.Exists(pluginPath))
                {
                    return pluginPath;
                }
            }
        }

        var assemblyDirectory = Path.GetDirectoryName(typeof(ItemSetRepository).Assembly.Location);
        if (!string.IsNullOrWhiteSpace(assemblyDirectory))
        {
            var assemblyPath = Path.Combine(assemblyDirectory, "Data", "outfit_sources.json");
            if (File.Exists(assemblyPath))
            {
                return assemblyPath;
            }
        }

        return Path.Combine(AppContext.BaseDirectory, "Data", "outfit_sources.json");
    }

    private OutfitSourceRecord? FindOutfitSource(IReadOnlyList<OutfitSourceRecord> outfitSources, uint setId, IEnumerable<uint> pieceItemIds, string setName, string englishSetName, IEnumerable<string> localizedPieceNames, IEnumerable<string> englishPieceNames, OutfitSourceMatchStats stats)
    {
        var pieceIdSet = pieceItemIds.Where(id => id > 0).ToHashSet();
        stats.TotalLookups++;

        var setIdMatch = ChooseBestOutfitSource(outfitSources.Where(record => record.SetId > 0 && record.SetId == setId));
        if (setIdMatch != null)
        {
            stats.SetIdMatches++;
            return setIdMatch;
        }

        var itemIdMatch = ChooseBestOutfitSource(outfitSources.Where(record => record.ItemIds.Count > 0 && record.ItemIds.Any(pieceIdSet.Contains)));
        if (itemIdMatch != null)
        {
            stats.ItemIdMatches++;
            return itemIdMatch;
        }

        var names = new[] { setName, englishSetName }.Concat(localizedPieceNames).Concat(englishPieceNames).Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var normalizedNames = names.Select(NormalizeLookupName).Where(name => name.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var normalizedOutfitNames = names.Select(NormalizeOutfitName).Where(name => name.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var exact = ChooseBestOutfitSource(outfitSources.Where(record => (!string.IsNullOrWhiteSpace(record.NormalizedSetName) && normalizedNames.Contains(record.NormalizedSetName)) || (!string.IsNullOrWhiteSpace(record.NormalizedOutfitName) && normalizedOutfitNames.Contains(record.NormalizedOutfitName))));
        if (exact != null)
        {
            stats.ExactNameMatches++;
            return exact;
        }

        var familyMatches = outfitSources
            .Where(record => !string.IsNullOrWhiteSpace(record.NormalizedBaseName) && record.NormalizedBaseName.Length >= 4)
            .Where(record => normalizedNames.Any(name =>
                name.StartsWith(record.NormalizedBaseName, StringComparison.OrdinalIgnoreCase)
                && (string.IsNullOrWhiteSpace(record.NormalizedRoleSuffix) || name.EndsWith(record.NormalizedRoleSuffix, StringComparison.OrdinalIgnoreCase))))
            .ToList();
        var familyMatch = ChooseBestOutfitSource(familyMatches);
        if (familyMatch != null)
        {
            stats.FamilyNameMatches++;
            return familyMatch;
        }

        var availableTokens = GetAvailableLookupTokens(names);
        var tokenMatches = outfitSources.Where(record =>
        {
            var requiredTokens = ItemSetRepository.GetRequiredOutfitTokens(record.SetName);
            return requiredTokens.Count > 0 && requiredTokens.All(availableTokens.Contains);
        }).ToList();
        var tokenMatch = ChooseBestOutfitSource(tokenMatches);
        if (tokenMatch != null)
        {
            stats.TokenNameMatches++;
            return tokenMatch;
        }

        var inferredTerritory = InferTerritorySourceFromPieceNames(englishPieceNames.Any() ? englishPieceNames : localizedPieceNames);
        if (inferredTerritory != null)
        {
            stats.InferredTerritoryMatches++;
            return inferredTerritory;
        }

        stats.AddNoMatchCandidateSummary(outfitSources, availableTokens);
        return null;
    }

    private static OutfitSourceRecord? ChooseBestOutfitSource(IEnumerable<OutfitSourceRecord> records)
        => records
            .OrderByDescending(record => record.SourceTerritoryTypeIds.Count > 0)
            .ThenBy(record => IsGenericStoreSource(record.SourceName))
            .ThenBy(record => record.SourceName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

    private static bool IsGenericStoreSource(string? sourceName)
        => sourceName != null
            && (sourceName.Equals("Online Store", StringComparison.OrdinalIgnoreCase)
                || sourceName.Equals("Mog Station", StringComparison.OrdinalIgnoreCase));


    private static string? GetBestSourceName(OutfitSourceRecord? source)
    {
        if (source == null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(source.SourceName))
        {
            return source.SourceName;
        }

        return source.SourceAliases.FirstOrDefault(alias => !string.IsNullOrWhiteSpace(alias));
    }

    private void LogOutfitSourceLoadSummary(string path, IReadOnlyList<OutfitSourceRecord> sources)
    {
        var withSetIds = sources.Count(record => record.SetId > 0);
        var withItemIds = sources.Count(record => record.ItemIds.Count > 0);
        var itemIdCount = sources.Sum(record => record.ItemIds.Count);
        var withTerritoryIds = sources.Count(record => record.SourceTerritoryTypeIds.Count > 0);
        log.Debug($"[AkuItemSets] Loaded {sources.Count} outfit source records from {path}. RecordsWithSetIds={withSetIds}, RecordsWithItemIds={withItemIds}, TotalItemIds={itemIdCount}, RecordsWithTerritoryIds={withTerritoryIds}");

        foreach (var sample in sources.Take(10))
        {
            log.Debug($"[AkuItemSets] Outfit source sample: setId={sample.SetId}, set='{sample.SetName}', source='{sample.SourceName}', itemIds=[{string.Join(",", sample.ItemIds.Take(12))}], aliases=[{string.Join(" | ", sample.SourceAliases.Take(5))}], territoryIds=[{string.Join(",", sample.SourceTerritoryTypeIds.Take(8))}]");
        }

        if (sources.Count > 0 && withItemIds == 0)
        {
            log.Debug("[AkuItemSets] outfit_sources.json loaded with no direct itemIds. This build will use English item-piece token matching instead.");
        }
    }

    private void LogOutfitSourceMatchSummary(int setCount, int sourceCount, OutfitSourceMatchStats stats)
    {
        log.Debug($"[AkuItemSets] Outfit source matching summary: sets={setCount}, sourceRecords={sourceCount}, lookups={stats.TotalLookups}, setIdMatches={stats.SetIdMatches}, itemIdMatches={stats.ItemIdMatches}, exactNameMatches={stats.ExactNameMatches}, familyNameMatches={stats.FamilyNameMatches}, tokenNameMatches={stats.TokenNameMatches}, inferredTerritoryMatches={stats.InferredTerritoryMatches}, noMatchSamples={stats.NoMatchSamples.Count}");

        foreach (var sample in stats.NoMatchSamples)
        {
            log.Debug(sample);
        }
    }

    private IEnumerable<uint> ResolveTerritoryTypeIds(string sourceName, IReadOnlyList<string>? aliases)
    {
        var ids = new HashSet<uint>();
        var territoryIdsByName = GetTerritoryTypeIdsByEnglishPlaceName();
        foreach (var name in new[] { sourceName }.Concat(aliases ?? []))
        {
            if (territoryIdsByName.TryGetValue(NormalizeLookupName(name), out var territoryIds))
            {
                foreach (var territoryId in territoryIds)
                {
                    ids.Add(territoryId);
                }
            }
        }

        return ids;
    }


    private OutfitSourceRecord? InferTerritorySourceFromPieceNames(IEnumerable<string> pieceNames)
    {
        var names = pieceNames.Where(name => !string.IsNullOrWhiteSpace(name)).ToList();
        if (names.Count == 0)
        {
            return null;
        }

        var combinedTokens = GetAvailableLookupTokens(names);
        if (combinedTokens.Count == 0)
        {
            return null;
        }

        foreach (var territory in GetTerritoryNameLookup())
        {
            if (territory.RequiredTokens.Count == 0)
            {
                continue;
            }

            if (territory.RequiredTokens.All(combinedTokens.Contains))
            {
                return new OutfitSourceRecord
                {
                    SetName = string.Empty,
                    SourceName = territory.EnglishName,
                    SourceAliases = territory.Aliases,
                    SourceTerritoryTypeIds = [territory.TerritoryTypeId],
                };
            }
        }

        return null;
    }

    private List<TerritoryNameLookup> GetTerritoryNameLookup()
    {
        if (cachedTerritoryNameLookup != null)
        {
            return cachedTerritoryNameLookup;
        }

        var rows = new Dictionary<uint, TerritoryNameLookup>();
        foreach (var language in new[] { ClientLanguage.English, ClientLanguage.German, ClientLanguage.French, ClientLanguage.Japanese })
        {
            foreach (var territory in dataManager.GetExcelSheet<TerritoryType>(language))
            {
                if (!territory.PlaceName.IsValid)
                {
                    continue;
                }

                var name = territory.PlaceName.Value.Name.ToString();
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                if (!rows.TryGetValue(territory.RowId, out var lookup))
                {
                    var englishName = GetEnglishTerritoryName(territory.RowId);
                    lookup = new TerritoryNameLookup(territory.RowId, string.IsNullOrWhiteSpace(englishName) ? name : englishName);
                    rows[territory.RowId] = lookup;
                }

                lookup.AddAlias(name);
            }
        }

        cachedTerritoryNameLookup = rows.Values
            .Select(row => row.WithRequiredTokens(GetRequiredTerritoryTokens(row.EnglishName)))
            .Where(row => row.RequiredTokens.Count > 0)
            .OrderByDescending(row => row.RequiredTokens.Count)
            .ThenByDescending(row => row.RequiredTokens.Sum(token => token.Length))
            .ToList();

        log.Debug($"[AkuItemSets] Built territory fallback lookup with {cachedTerritoryNameLookup.Count} territory names. Samples=[{string.Join(" | ", cachedTerritoryNameLookup.Take(10).Select(row => $"{row.TerritoryTypeId}:{row.EnglishName}=>{string.Join(',', row.RequiredTokens)}"))}]");
        return cachedTerritoryNameLookup;
    }

    private string GetEnglishTerritoryName(uint territoryId)
    {
        var sheet = dataManager.GetExcelSheet<TerritoryType>(ClientLanguage.English);
        if (sheet.TryGetRow(territoryId, out var territory) && territory.PlaceName.IsValid)
        {
            return territory.PlaceName.Value.Name.ToString();
        }

        return string.Empty;
    }

    private static List<string> GetRequiredTerritoryTokens(string territoryName)
    {
        var tokens = TokenizeLookupName(territoryName)
            .Where(token => !IgnoredOutfitTokens.Contains(token))
            .Where(token => token.Length >= 4 || token.Any(char.IsDigit))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (tokens.Count == 1 && tokens[0].Length < 6)
        {
            return [];
        }

        return tokens;
    }

    private Dictionary<string, List<uint>> GetTerritoryTypeIdsByEnglishPlaceName()
    {
        if (cachedTerritoryTypeIdsByEnglishPlaceName != null)
        {
            return cachedTerritoryTypeIdsByEnglishPlaceName;
        }

        cachedTerritoryTypeIdsByEnglishPlaceName = new Dictionary<string, List<uint>>(StringComparer.OrdinalIgnoreCase);
        foreach (var territory in dataManager.GetExcelSheet<TerritoryType>(ClientLanguage.English))
        {
            if (!territory.PlaceName.IsValid)
            {
                continue;
            }

            var placeName = territory.PlaceName.Value.Name.ToString();
            if (string.IsNullOrWhiteSpace(placeName))
            {
                continue;
            }

            var key = NormalizeLookupName(placeName);
            if (!cachedTerritoryTypeIdsByEnglishPlaceName.TryGetValue(key, out var ids))
            {
                ids = [];
                cachedTerritoryTypeIdsByEnglishPlaceName[key] = ids;
            }

            ids.Add(territory.RowId);
        }

        return cachedTerritoryTypeIdsByEnglishPlaceName;
    }


    private sealed class OutfitSourceMatchStats
    {
        public int TotalLookups { get; set; }
        public int SetIdMatches { get; set; }
        public int ItemIdMatches { get; set; }
        public int ExactNameMatches { get; set; }
        public int FamilyNameMatches { get; set; }
        public int TokenNameMatches { get; set; }
        public int InferredTerritoryMatches { get; set; }
        public List<string> NoMatchSamples { get; } = [];
        private int candidateSummaryCount;

        public void AddNoMatchSample(uint setId, string setName, string englishSetName, IReadOnlyList<ItemSetPiece> pieces, IEnumerable<string> englishPieceNames)
        {
            if (NoMatchSamples.Count >= 20)
            {
                return;
            }

            var localizedPieces = pieces.Select(piece => $"{piece.ItemId}:{piece.Name}").Take(9);
            var englishPieces = englishPieceNames.Take(9);
            NoMatchSamples.Add($"[AkuItemSets] Outfit source no-match sample: setId={setId}, setName='{setName}', englishSetName='{englishSetName}', localizedPieces=[{string.Join(" | ", localizedPieces)}], englishPieces=[{string.Join(" | ", englishPieces)}]");
        }

        public void AddNoMatchCandidateSummary(IReadOnlyList<OutfitSourceRecord> records, HashSet<string> availableTokens)
        {
            if (candidateSummaryCount >= 5 || availableTokens.Count == 0)
            {
                return;
            }

            candidateSummaryCount++;
            var candidates = records
                .Select(record => new { record.SetName, record.SourceName, Tokens = ItemSetRepository.GetRequiredOutfitTokens(record.SetName) })
                .Select(row => new { row.SetName, row.SourceName, Matched = row.Tokens.Count(availableTokens.Contains), Total = row.Tokens.Count, Tokens = row.Tokens })
                .Where(row => row.Total > 0 && row.Matched > 0)
                .OrderByDescending(row => row.Matched)
                .ThenBy(row => row.Total)
                .Take(5)
                .Select(row => $"{row.SetName} -> {row.SourceName} ({row.Matched}/{row.Total}; required=[{string.Join(",", row.Tokens)}])");

            NoMatchSamples.Add($"[AkuItemSets] Outfit source closest token candidates: available=[{string.Join(",", availableTokens.OrderBy(token => token).Take(30))}], candidates=[{string.Join(" | ", candidates)}]");
        }
    }

    private static HashSet<string> GetAvailableLookupTokens(IEnumerable<string> names)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            foreach (var token in TokenizeLookupName(name))
            {
                tokens.Add(token);
            }
        }

        return tokens;
    }

    private static List<string> GetRequiredOutfitTokens(string outfitSetName)
    {
        var roleSuffix = GetRoleSuffix(outfitSetName);
        var tokens = TokenizeLookupName(outfitSetName)
            .Where(token => !IgnoredOutfitTokens.Contains(token))
            .Where(token => token.Length > 1 || token.Any(char.IsDigit))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!string.IsNullOrWhiteSpace(roleSuffix))
        {
            foreach (var token in TokenizeLookupName(roleSuffix))
            {
                if (!IgnoredOutfitTokens.Contains(token) && !tokens.Contains(token, StringComparer.OrdinalIgnoreCase))
                {
                    tokens.Add(token);
                }
            }
        }

        return tokens;
    }

    private static IEnumerable<string> TokenizeLookupName(string value)
    {
        var current = new List<char>();
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                current.Add(char.ToLowerInvariant(ch));
                continue;
            }

            if (current.Count > 0)
            {
                yield return new string(current.ToArray());
                current.Clear();
            }
        }

        if (current.Count > 0)
        {
            yield return new string(current.ToArray());
        }
    }

    private static void AddPiece(List<ItemSetPiece> pieces, ItemSetSlot slot, RowRef<Item> itemRef)
    {
        if (itemRef.RowId == 0 || !itemRef.IsValid)
        {
            return;
        }

        var item = itemRef.Value;
        pieces.Add(new ItemSetPiece(slot, itemRef.RowId, item.Name.ToString(), item.Icon, item.ClassJobCategory.RowId, item.LevelEquip));
    }

    private static void AddEnglishPieceName(List<string> names, RowRef<Item> itemRef, ExcelSheet<Item> englishItemSheet)
    {
        if (itemRef.RowId == 0 || !englishItemSheet.TryGetRow(itemRef.RowId, out var item))
        {
            return;
        }

        var name = item.Name.ToString();
        if (!string.IsNullOrWhiteSpace(name))
        {
            names.Add(name);
        }
    }

    private static string GetExcelString<T>(T row, string propertyName)
    {
        var value = typeof(T).GetProperty(propertyName)?.GetValue(row);
        return value?.ToString() ?? string.Empty;
    }

    private static uint GetExcelUInt<T>(T row, string propertyName)
    {
        var value = typeof(T).GetProperty(propertyName)?.GetValue(row);
        return value switch
        {
            byte b => b,
            ushort us => us,
            uint ui => ui,
            int i when i > 0 => (uint)i,
            _ => 0,
        };
    }

    private static string GetRoleSuffix(string value)
    {
        foreach (var suffix in RoleSuffixes)
        {
            if (value.EndsWith(" " + suffix, StringComparison.OrdinalIgnoreCase))
            {
                return suffix;
            }
        }

        return string.Empty;
    }

    private static string GetOutfitBaseName(string value)
    {
        var roleSuffix = GetRoleSuffix(value);
        if (!string.IsNullOrWhiteSpace(roleSuffix) && value.EndsWith(" " + roleSuffix, StringComparison.OrdinalIgnoreCase))
        {
            value = value[..^($" {roleSuffix}".Length)].Trim();
        }

        foreach (var word in GenericOutfitWords)
        {
            if (value.EndsWith(" " + word, StringComparison.OrdinalIgnoreCase))
            {
                value = value[..^($" {word}".Length)].Trim();
                break;
            }
        }

        return value.Trim();
    }

    private static uint ExtractOutfitSetId(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return 0;
        }

        const string marker = "/outfits/";
        var index = url.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return 0;
        }

        index += marker.Length;
        var end = index;
        while (end < url.Length && char.IsDigit(url[end]))
        {
            end++;
        }

        return end > index && uint.TryParse(url[index..end], out var setId) ? setId : 0;
    }

    private static string NormalizeOutfitName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var ignoredTokens = new HashSet<string>(IgnoredOutfitTokens, StringComparer.OrdinalIgnoreCase)
        {
            "gear", "arms", "garb", "vestments", "fending", "maiming", "striking", "scouting", "aiming", "casting", "healing", "slaying",
        };

        return string.Concat(TokenizeLookupName(value).Where(token => !ignoredTokens.Contains(token)));
    }

    private static string NormalizeLookupName(string value)
        => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}


internal sealed class TerritoryNameLookup
{
    public TerritoryNameLookup(uint territoryTypeId, string englishName)
    {
        TerritoryTypeId = territoryTypeId;
        EnglishName = englishName;
    }

    public uint TerritoryTypeId { get; }
    public string EnglishName { get; }
    public List<string> Aliases { get; } = [];
    public List<string> RequiredTokens { get; private set; } = [];

    public void AddAlias(string alias)
    {
        if (!string.IsNullOrWhiteSpace(alias) && !Aliases.Contains(alias, StringComparer.OrdinalIgnoreCase))
        {
            Aliases.Add(alias);
        }
    }

    public TerritoryNameLookup WithRequiredTokens(List<string> requiredTokens)
    {
        RequiredTokens = requiredTokens;
        return this;
    }
}

public sealed class OutfitSourceRecord
{
    public uint SetId { get; set; }
    public string SetName { get; set; } = string.Empty;
    public string SourceName { get; set; } = string.Empty;
    public string? SourceUrl { get; set; }
    public List<string> SourceAliases { get; set; } = [];
    public List<uint> ItemIds { get; set; } = [];
    public List<uint> SourceContentFinderConditionIds { get; set; } = [];
    public List<uint> SourceTerritoryTypeIds { get; set; } = [];
    public string NormalizedSetName { get; set; } = string.Empty;
    public string NormalizedOutfitName { get; set; } = string.Empty;
    public string NormalizedBaseName { get; set; } = string.Empty;
    public string NormalizedRoleSuffix { get; set; } = string.Empty;
}
