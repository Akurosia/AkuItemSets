using System.Collections.Generic;
using Dalamud.Game;
using Dalamud.Plugin.Services;

namespace AkuItemSets.Services;

public sealed class Localization
{
    private readonly IClientState clientState;

    public Localization(IClientState clientState)
    {
        this.clientState = clientState;
    }

    public string this[string key]
    {
        get
        {
            var language = clientState.ClientLanguage;
            if (Strings.TryGetValue(language, out var localized) && localized.TryGetValue(key, out var text))
            {
                return text;
            }

            return Strings[ClientLanguage.English].GetValueOrDefault(key, key);
        }
    }

    private static readonly IReadOnlyDictionary<ClientLanguage, IReadOnlyDictionary<string, string>> Strings = new Dictionary<ClientLanguage, IReadOnlyDictionary<string, string>>
    {
        [ClientLanguage.English] = new Dictionary<string, string>
        {
            ["tab.collection"] = "Collection",
            ["tab.scanStatus"] = "Scan status",
            ["tab.armoireCandidates"] = "Armoire candidates",
            ["status.login"] = "Log in with a character, then scan to build this character's collection.",
            ["status.lastScan"] = "Last scan",
            ["status.autoScan"] = "Automatic scans run while you are logged in.",
            ["status.cacheHint"] = "Categories that depend on game caches update after the game has loaded that storage.",
            ["status.notScanned"] = "Not scanned yet",
            ["toolbar.searchHint"] = "Search set or item",
            ["toolbar.hideComplete"] = "Hide complete",
            ["toolbar.onlyMissing"] = "Only missing pieces",
            ["toolbar.includeAllClass"] = "Include all-class",
            ["table.set"] = "Set",
            ["table.progress"] = "Progress",
            ["table.missing"] = "Missing",
            ["table.owned"] = "Owned",
            ["table.use"] = "Use",
            ["table.item"] = "Item",
            ["table.source"] = "Source",
            ["table.count"] = "Count",
            ["table.id"] = "ID",
            ["action.copyIsearch"] = "Copy /isearch",
            ["hint.clickCopy"] = "Click to copy /isearch",
            ["armoire.candidatesFound"] = "armoire-eligible item locations found",
            ["armoire.clickHint"] = "Click an icon or name to copy /isearch.",
            ["filter.all"] = "All",
            ["filter.currentRole"] = "Current role",
            ["filter.tank"] = "Tank",
            ["filter.healer"] = "Healer",
            ["filter.melee"] = "Melee",
            ["filter.physicalRanged"] = "Physical ranged",
            ["filter.caster"] = "Caster",
            ["filter.crafter"] = "Crafter",
            ["filter.gatherer"] = "Gatherer",
            ["sort.name"] = "Name",
            ["sort.setId"] = "Set ID",
            ["sort.asc"] = "Asc",
            ["sort.desc"] = "Desc",
            ["category.inventory"] = "Inventory",
            ["category.armoury"] = "Armoury Chest",
            ["category.saddlebag"] = "Saddlebag",
            ["category.retainers"] = "Retainers",
            ["category.glamourDresser"] = "Glamour Dresser",
            ["category.armoire"] = "Armoire",
            ["source.inventory"] = "Inventory",
            ["source.armoury"] = "Armoury Chest",
            ["source.saddlebag"] = "Saddlebag",
            ["source.retainer"] = "Retainer",
            ["source.glamourDresser"] = "Glamour Dresser",
            ["source.glamourDresserSet"] = "Dresser set",
            ["source.armoire"] = "Armoire",
        },
        [ClientLanguage.German] = new Dictionary<string, string>
        {
            ["tab.collection"] = "Sammlung",
            ["tab.scanStatus"] = "Scanstatus",
            ["tab.armoireCandidates"] = "Fur Kostbarkeiten",
            ["status.login"] = "Logge dich mit einem Charakter ein, um die Sammlung zu erfassen.",
            ["status.lastScan"] = "Letzter Scan",
            ["status.autoScan"] = "Automatische Scans laufen, solange du eingeloggt bist.",
            ["status.cacheHint"] = "Kategorien mit Spiel-Cache werden aktualisiert, sobald dieser Speicher geladen wurde.",
            ["status.notScanned"] = "Noch nicht gescannt",
            ["toolbar.searchHint"] = "Set oder Gegenstand suchen",
            ["toolbar.hideComplete"] = "Vollstandige ausblenden",
            ["toolbar.onlyMissing"] = "Nur fehlende Teile",
            ["toolbar.includeAllClass"] = "Alle Klassen einbeziehen",
            ["table.set"] = "Set",
            ["table.progress"] = "Fortschritt",
            ["table.missing"] = "Fehlt",
            ["table.owned"] = "Besitz",
            ["table.use"] = "Aktion",
            ["table.item"] = "Gegenstand",
            ["table.source"] = "Quelle",
            ["table.count"] = "Anzahl",
            ["table.id"] = "ID",
            ["action.copyIsearch"] = "/isearch kopieren",
            ["hint.clickCopy"] = "Klicken, um /isearch zu kopieren",
            ["armoire.candidatesFound"] = "Gegenstandsorte fur das Kostbarkeitenkabinett gefunden",
            ["armoire.clickHint"] = "Icon oder Namen anklicken, um /isearch zu kopieren.",
            ["filter.all"] = "Alle",
            ["filter.currentRole"] = "Aktuelle Rolle",
            ["filter.tank"] = "Verteidiger",
            ["filter.healer"] = "Heiler",
            ["filter.melee"] = "Nahkampf",
            ["filter.physicalRanged"] = "Physischer Fernkampf",
            ["filter.caster"] = "Magischer Fernkampf",
            ["filter.crafter"] = "Handwerker",
            ["filter.gatherer"] = "Sammler",
            ["sort.name"] = "Name",
            ["sort.setId"] = "Set-ID",
            ["sort.asc"] = "Auf",
            ["sort.desc"] = "Ab",
            ["category.inventory"] = "Inventar",
            ["category.armoury"] = "Arsenal",
            ["category.saddlebag"] = "Chocobo-Satteltasche",
            ["category.retainers"] = "Gehilfen",
            ["category.glamourDresser"] = "Projektionskommode",
            ["category.armoire"] = "Kostbarkeitenkabinett",
            ["source.inventory"] = "Inventar",
            ["source.armoury"] = "Arsenal",
            ["source.saddlebag"] = "Chocobo-Satteltasche",
            ["source.retainer"] = "Gehilfe",
            ["source.glamourDresser"] = "Projektionskommode",
            ["source.glamourDresserSet"] = "Kommoden-Set",
            ["source.armoire"] = "Kostbarkeitenkabinett",
        },
        [ClientLanguage.French] = new Dictionary<string, string>
        {
            ["source.inventory"] = "Inventaire",
            ["source.armoury"] = "Arsenal",
            ["source.saddlebag"] = "Sacoche chocobo",
            ["source.retainer"] = "Servant",
            ["source.glamourDresser"] = "Commode mirage",
            ["source.glamourDresserSet"] = "Ensemble de la commode",
            ["source.armoire"] = "Bahut personnel",
            ["category.inventory"] = "Inventaire",
            ["category.armoury"] = "Arsenal",
            ["category.saddlebag"] = "Sacoche chocobo",
            ["category.retainers"] = "Servants",
            ["category.glamourDresser"] = "Commode mirage",
            ["category.armoire"] = "Bahut personnel",
        },
        [ClientLanguage.Japanese] = new Dictionary<string, string>
        {
            ["source.inventory"] = "所持品",
            ["source.armoury"] = "アーマリーチェスト",
            ["source.saddlebag"] = "チョコボかばん",
            ["source.retainer"] = "リテイナー",
            ["source.glamourDresser"] = "ミラージュドレッサー",
            ["source.glamourDresserSet"] = "ドレッサーセット",
            ["source.armoire"] = "愛蔵品キャビネット",
            ["category.inventory"] = "所持品",
            ["category.armoury"] = "アーマリーチェスト",
            ["category.saddlebag"] = "チョコボかばん",
            ["category.retainers"] = "リテイナー",
            ["category.glamourDresser"] = "ミラージュドレッサー",
            ["category.armoire"] = "愛蔵品キャビネット",
        },
    };
}
