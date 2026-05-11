#!/usr/bin/env python3
from __future__ import annotations

import argparse
import json
import re
import sys
from dataclasses import asdict, dataclass, field
from html.parser import HTMLParser
from pathlib import Path
from typing import Any, Iterable
from urllib.parse import urljoin
from urllib.request import Request, urlopen

OUTFITS_URL = "https://ffxivcollect.com/outfits"
OUTFITS_API_URL = "https://ffxivcollect.com/api/outfits"
OUTFIT_LINK_RE = re.compile(r"(?:^|/)outfits/\d+(?:[?#].*)?$", re.IGNORECASE)
OUTFIT_SET_ID_RE = re.compile(r"(?:^|/)outfits/(\d+)(?:[?#].*)?$", re.IGNORECASE)
GARLAND_SOURCE_RE = re.compile(r"garlandtools\.org", re.IGNORECASE)
IMAGE_TEXT_RE = re.compile(r"^image$", re.IGNORECASE)
ITEM_ID_RE_LIST = [
    re.compile(r"(?:^|[/?#&=])item(?:/|=)(\d+)(?:\D|$)", re.IGNORECASE),
    re.compile(r"garlandtools\.org/(?:db/)?#item/(\d+)", re.IGNORECASE),
    re.compile(r"(?:^|/)items/(\d+)(?:\D|$)", re.IGNORECASE),
]

@dataclass(frozen=True)
class OutfitSource:
    setId: int
    setName: str
    sourceName: str
    sourceUrl: str | None = None
    sourceAliases: list[str] = field(default_factory=list)
    itemIds: list[int] = field(default_factory=list)

class OutfitPageParser(HTMLParser):
    def __init__(self) -> None:
        super().__init__(convert_charrefs=True)
        self.links: list[tuple[str | None, str]] = []
        self._href_stack: list[str | None] = []
        self._text_stack: list[list[str]] = []

    def handle_starttag(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
        if tag.lower() != "a":
            return
        self._href_stack.append(dict(attrs).get("href"))
        self._text_stack.append([])

    def handle_data(self, data: str) -> None:
        if self._text_stack:
            self._text_stack[-1].append(data)

    def handle_endtag(self, tag: str) -> None:
        if tag.lower() != "a" or not self._href_stack or not self._text_stack:
            return
        href = self._href_stack.pop()
        text = " ".join("".join(self._text_stack.pop()).split())
        if href or text:
            self.links.append((href, text))


class OutfitEventParser(HTMLParser):
    def __init__(self) -> None:
        super().__init__(convert_charrefs=True)
        self.events: list[tuple[str, str | None, str]] = []
        self._href_stack: list[str | None] = []
        self._text_stack: list[list[str]] = []

    def handle_starttag(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
        if tag.lower() != "a":
            return
        self._href_stack.append(dict(attrs).get("href"))
        self._text_stack.append([])

    def handle_data(self, data: str) -> None:
        if self._text_stack:
            self._text_stack[-1].append(data)
            return
        text = clean_text(data)
        if text:
            self.events.append(("text", None, text))

    def handle_endtag(self, tag: str) -> None:
        if tag.lower() != "a" or not self._href_stack or not self._text_stack:
            return
        href = self._href_stack.pop()
        text = clean_text("".join(self._text_stack.pop()))
        if href or text:
            self.events.append(("link", href, text))

def fetch_text(url: str) -> str:
    request = Request(url, headers={"User-Agent": "AkuItemSets outfit source generator"})
    with urlopen(request, timeout=60) as response:
        return response.read().decode("utf-8", errors="replace")

def fetch_json(url: str) -> Any:
    return json.loads(fetch_text(url))

def normalize_lookup_name(value: str) -> str:
    return "".join(ch.lower() for ch in value if ch.isalnum())

def clean_text(value: Any) -> str:
    return " ".join(str(value or "").split())

def extract_outfit_set_id_from_url(value: str | None) -> int:
    if not value:
        return 0
    match = OUTFIT_SET_ID_RE.search(value)
    if not match:
        return 0
    try:
        return int(match.group(1))
    except ValueError:
        return 0

def extract_outfit_set_id_from_api(outfit: dict[str, Any]) -> int:
    for key in ("set_id", "setId", "mirage_store_set_item_id", "mirageStoreSetItemId", "row_id", "rowId", "id"):
        raw = outfit.get(key)
        if isinstance(raw, int) and raw > 0:
            return raw
        if isinstance(raw, str) and raw.isdigit():
            return int(raw)
    for key in ("url", "link", "path"):
        set_id = extract_outfit_set_id_from_url(str(outfit.get(key) or ""))
        if set_id > 0:
            return set_id
    return 0

def extract_item_ids_from_text(value: str | None) -> list[int]:
    if not value:
        return []
    ids: list[int] = []
    for pattern in ITEM_ID_RE_LIST:
        for match in pattern.finditer(value):
            try:
                item_id = int(match.group(1))
            except ValueError:
                continue
            if item_id > 0 and item_id not in ids:
                ids.append(item_id)
    return ids

def extract_item_ids_from_api(value: Any) -> list[int]:
    ids: list[int] = []
    if isinstance(value, int):
        return [value] if value > 0 else []
    if isinstance(value, str):
        return extract_item_ids_from_text(value)
    if isinstance(value, list):
        for item in value:
            for item_id in extract_item_ids_from_api(item):
                if item_id not in ids:
                    ids.append(item_id)
        return ids
    if isinstance(value, dict):
        for key in ("id", "item_id", "itemId", "row_id", "rowId"):
            raw = value.get(key)
            if isinstance(raw, int) and raw > 0 and raw not in ids:
                ids.append(raw)
            elif isinstance(raw, str):
                for item_id in extract_item_ids_from_text(raw):
                    if item_id not in ids:
                        ids.append(item_id)
        for key in ("url", "link", "garland_url", "garlandUrl"):
            for item_id in extract_item_ids_from_text(value.get(key)):
                if item_id not in ids:
                    ids.append(item_id)
    return ids

def add_unique(records: list[OutfitSource], seen: set[tuple[int, str, str]], set_id: int, set_name: str, source_name: str, source_url: str | None, aliases: Iterable[str] = (), item_ids: Iterable[int] = ()) -> None:
    set_name = clean_text(set_name)
    source_name = clean_text(source_name)
    if not set_name or not source_name:
        return
    key = (set_id, normalize_lookup_name(set_name), normalize_lookup_name(source_name))
    source_aliases: list[str] = []
    for alias in aliases:
        alias = clean_text(alias)
        if alias and normalize_lookup_name(alias) != normalize_lookup_name(source_name) and alias not in source_aliases:
            source_aliases.append(alias)
    source_item_ids: list[int] = []
    for item_id in item_ids:
        if item_id > 0 and item_id not in source_item_ids:
            source_item_ids.append(item_id)
    if key in seen:
        for record in records:
            if record.setId == set_id and normalize_lookup_name(record.setName) == key[1] and normalize_lookup_name(record.sourceName) == key[2]:
                merged = list(record.itemIds)
                for item_id in source_item_ids:
                    if item_id not in merged:
                        merged.append(item_id)
                records[records.index(record)] = OutfitSource(record.setId or set_id, record.setName, record.sourceName, record.sourceUrl or source_url, record.sourceAliases, merged)
                break
        return
    seen.add(key)
    records.append(OutfitSource(set_id, set_name, source_name, source_url, source_aliases, source_item_ids))

def iter_api_outfits(payload: Any) -> Iterable[dict[str, Any]]:
    if isinstance(payload, list):
        for item in payload:
            if isinstance(item, dict):
                yield item
        return
    if isinstance(payload, dict):
        for key in ("results", "outfits", "data"):
            value = payload.get(key)
            if isinstance(value, list):
                for item in value:
                    if isinstance(item, dict):
                        yield item
                return

def source_values_from_api(source: Any) -> Iterable[tuple[str, str | None, list[str]]]:
    if isinstance(source, str):
        yield clean_text(source), None, []
        return
    if not isinstance(source, dict):
        return

    source_name = clean_text(source.get("text") or source.get("name") or source.get("title") or source.get("source") or source.get("description"))
    source_url = source.get("url") or source.get("link") or source.get("related_url")
    aliases = source.get("aliases") or source.get("source_aliases") or []
    if isinstance(aliases, str):
        aliases = [aliases]
    if source_name:
        yield source_name, str(source_url) if source_url else None, [clean_text(alias) for alias in aliases if clean_text(alias)]

def build_sources_from_api(payload: Any) -> list[OutfitSource]:
    records: list[OutfitSource] = []
    seen: set[tuple[int, str, str]] = set()
    for outfit in iter_api_outfits(payload):
        set_name = clean_text(outfit.get("name") or outfit.get("title"))
        set_id = extract_outfit_set_id_from_api(outfit)
        if not set_name:
            continue
        item_ids = extract_item_ids_from_api(outfit.get("items") or outfit.get("item_ids") or outfit.get("itemIds"))
        sources = outfit.get("sources") or outfit.get("source")
        if isinstance(sources, (str, dict)):
            sources = [sources]
        if not isinstance(sources, list):
            continue
        for source in sources:
            for source_name, source_url, aliases in source_values_from_api(source):
                add_unique(records, seen, set_id, set_name, source_name, source_url, aliases, item_ids)
    return records

def is_outfit_link(href: str | None) -> bool:
    return bool(href and OUTFIT_LINK_RE.search(href))

def is_probable_source_link(href: str | None, text: str) -> bool:
    if not href or not text or IMAGE_TEXT_RE.match(text):
        return False
    if is_outfit_link(href):
        return False
    return bool(GARLAND_SOURCE_RE.search(href))

def build_sources_from_html(html: str) -> list[OutfitSource]:
    records = build_sources_from_html_events(html)
    if records:
        return records
    records = build_sources_from_html_links(html)
    if records:
        return records
    return build_sources_from_html_text(html)

def build_sources_from_html_events(html: str) -> list[OutfitSource]:
    parser = OutfitEventParser()
    parser.feed(html)
    records: list[OutfitSource] = []
    seen: set[tuple[int, str, str]] = set()
    current_set_name: str | None = None
    current_set_id = 0
    source_parts: list[str] = []
    source_url: str | None = None

    source_categories = {
        "Achievement", "Cosmic Exploration", "Crafting", "Deep Dungeon", "Dungeon", "Eureka", "Event", "FATE", "Gathering", "Gold Saucer", "Hunts", "Island Sanctuary", "Occult Crescent", "Other", "Premium", "Purchase", "PvP", "Quest", "Raid", "Skybuilders", "Trial", "Tribal", "V&C Dungeon", "Wondrous Tails",
    }
    headers = {"Name", "Items", "Source", "Own", "Patch", "Image", "Filters"}

    def flush() -> None:
        nonlocal current_set_name, current_set_id, source_parts, source_url
        if current_set_name and source_parts:
            source_name = clean_text(" ".join(source_parts))
            add_unique(records, seen, current_set_id, current_set_name, source_name, source_url)
        current_set_name = None
        current_set_id = 0
        source_parts = []
        source_url = None

    for kind, href, text in parser.events:
        text = clean_text(text)
        if not text and not href:
            continue

        if is_outfit_link(href):
            flush()
            if text and not IMAGE_TEXT_RE.match(text):
                current_set_name = text
                current_set_id = extract_outfit_set_id_from_url(href)
            continue

        if not current_set_name:
            continue

        if IMAGE_TEXT_RE.match(text) or text in headers or text in source_categories:
            continue

        # The ownership percentage is the first reliable marker after the source column.
        # Once we see it, the row is complete and the following patch number must not be used as source text.
        if re.fullmatch(r"\d+(?:\.\d+)?%", text):
            flush()
            continue

        # Patch values can appear after ownership if the row was not flushed for any reason.
        if re.fullmatch(r"\d+(?:\.\d+)?", text):
            continue

        if text.lower().startswith("exclude "):
            continue

        source_parts.append(text)
        if kind == "link" and href and not source_url:
            source_url = urljoin(OUTFITS_URL, href)

    flush()
    return records

def build_sources_from_html_links(html: str) -> list[OutfitSource]:
    parser = OutfitPageParser()
    parser.feed(html)
    records: list[OutfitSource] = []
    seen: set[tuple[int, str, str]] = set()
    current_set_name: str | None = None
    current_set_id = 0
    current_item_ids: list[int] = []

    for href, text in parser.links:
        text = clean_text(text)
        if is_outfit_link(href):
            if text and not IMAGE_TEXT_RE.match(text):
                current_set_name = text
                current_set_id = extract_outfit_set_id_from_url(href)
                current_item_ids = []
            continue
        if current_set_name:
            for item_id in extract_item_ids_from_text(href):
                if item_id not in current_item_ids:
                    current_item_ids.append(item_id)
        if current_set_name and is_probable_source_link(href, text):
            add_unique(records, seen, current_set_id, current_set_name, text, urljoin(OUTFITS_URL, href) if href else None, item_ids=current_item_ids)
            current_set_name = None
            current_item_ids = []

    return records

def build_sources_from_html_text(html: str) -> list[OutfitSource]:
    # Fallback for saved/stripped pages. It walks the text in table order:
    # outfit name -> multiple Image links/items -> source -> ownership/patch.
    records: list[OutfitSource] = []
    seen: set[tuple[int, str, str]] = set()
    lines = [clean_text(line) for line in re.sub(r"<[^>]+>", "\n", html).splitlines()]
    lines = [line for line in lines if line]
    current_set_name: str | None = None

    source_categories = {
        "Achievement", "Cosmic Exploration", "Crafting", "Deep Dungeon", "Dungeon", "Eureka", "Event", "FATE", "Gathering", "Gold Saucer", "Hunts", "Island Sanctuary", "Occult Crescent", "Other", "Premium", "Purchase", "PvP", "Quest", "Raid", "Skybuilders", "Trial", "Tribal", "V&C Dungeon", "Wondrous Tails",
    }

    for line in lines:
        if line in source_categories or line in {"Name", "Items", "Source", "Own", "Patch", "Image", "Filters"}:
            continue
        if re.search(r"\d+(?:\.\d+)?%", line) or re.fullmatch(r"\d+(?:\.\d+)?", line):
            current_set_name = None
            continue
        if line.endswith("Attire") or " Attire of " in line or line.endswith("Costume") or line.endswith("Garb") or line.endswith("Set"):
            current_set_name = line
            continue
        if current_set_name and not line.lower().startswith("exclude "):
            add_unique(records, seen, 0, current_set_name, line, None)
            current_set_name = None

    return records

def main() -> int:
    ap = argparse.ArgumentParser(description="Build AkuItemSets outfit source lookup from FFXIV Collect outfits.")
    ap.add_argument("-o", "--output", default="../Data/outfit_sources.json", help="Output JSON path. Default: Data/outfit_sources.json")
    ap.add_argument("--input-html", help="Use a saved FFXIV Collect outfits HTML file instead of downloading it.")
    ap.add_argument("--input-json", help="Use a saved FFXIV Collect outfits API JSON file instead of downloading it.")
    ap.add_argument("--html-only", action="store_true", help="Skip the API attempt and parse the outfits page HTML directly.")
    ap.add_argument("--save-debug-html", help="Also save the downloaded outfits HTML to this path for debugging.")
    args = ap.parse_args()

    records: list[OutfitSource] = []
    if args.input_json:
        records = build_sources_from_api(json.loads(Path(args.input_json).read_text(encoding="utf-8")))
    elif args.input_html:
        records = build_sources_from_html(Path(args.input_html).read_text(encoding="utf-8"))
    elif not args.html_only:
        try:
            records = build_sources_from_api(fetch_json(OUTFITS_API_URL))
        except Exception as exc:
            print(f"API fetch failed, falling back to HTML: {exc}", file=sys.stderr)

    if not records:
        html = Path(args.input_html).read_text(encoding="utf-8") if args.input_html else fetch_text(OUTFITS_URL)
        if args.save_debug_html:
            Path(args.save_debug_html).write_text(html, encoding="utf-8")
            print(f"Saved downloaded HTML to {args.save_debug_html}")
        records = build_sources_from_html(html)

    records = sorted(records, key=lambda record: (record.setId or 0, normalize_lookup_name(record.setName), normalize_lookup_name(record.sourceName)))
    out = Path(args.output)
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(json.dumps([asdict(record) for record in records], ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    set_id_count = sum(1 for record in records if record.setId)
    item_link_count = sum(1 for record in records if record.itemIds)
    total_item_ids = sum(len(record.itemIds) for record in records)
    print(f"Wrote {len(records)} outfit source links to {out}")
    print(f"Records with direct set id links: {set_id_count}")
    print(f"Records with direct item id links: {item_link_count}")
    print(f"Total direct item id links: {total_item_ids}")
    for record in records[:10]:
        print(f"Sample: setId={record.setId} set={record.setName!r} source={record.sourceName!r} itemIds={record.itemIds[:12]}")
    if records and item_link_count == 0:
        print("Note: No records contain direct item IDs. This is okay for the current plugin build because it also matches FFXIV Collect outfit names against English item-piece tokens and can infer territory names from item pieces.")
    if not records:
        print("No records were generated. Retry with: python3 Scripts/build_outfit_sources.py --html-only --save-debug-html outfits_debug.html", file=sys.stderr)
    return 0 if records else 1

if __name__ == "__main__":
    raise SystemExit(main())
