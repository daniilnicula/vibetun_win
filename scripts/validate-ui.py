#!/usr/bin/env python3
"""Cross-platform integrity checks for UI translations and palettes (no SDK required)."""
import json
from pathlib import Path
import re
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1] / "src" / "VibeTun"
CATALOG = json.loads((ROOT / "Localization" / "strings.json").read_text(encoding="utf-8"))
PLACEHOLDER = re.compile(r"(?<!\{)\{(\d+)(?:,[^}:]+)?(?::[^}]+)?\}")

for key, translations in CATALOG.items():
    assert set(translations) == {"ru", "en"}, f"{key}: missing language"
    assert all(translations.values()), f"{key}: empty translation"
    ru, en = translations["ru"], translations["en"]
    assert sorted(PLACEHOLDER.findall(ru)) == sorted(PLACEHOLDER.findall(en)), f"{key}: format arguments differ"

references = set()
for path in ROOT.rglob("*.cs"):
    text = path.read_text(encoding="utf-8")
    references.update(re.findall(r'L\.[TF]\("([\w]+)"', text))
    # System.Windows.Localization is a WPF namespace. Use our explicit global alias
    # outside the service definition to prevent CS0104 in files importing WPF.
    if path.name != "Localization.cs":
        assert not re.search(r"(?<![\w.])Localization\.", text), f"{path}: use the L alias for the localization service"
    assert not re.search(r'L\.F\("[\w]+",\s*\)', text), f"{path}: empty format arguments"
    assert not re.search(r'string\?? \w+ = L\.[TF]\(', text), f"{path}: nonconstant optional parameter"

for path in ROOT.rglob("*.xaml"):
    tree = ET.parse(path)
    for element in tree.iter():
        for attribute, value in element.attrib.items():
            if value.startswith("{loc:Tr "):
                references.add(value[len("{loc:Tr "):-1])
            assert not re.search("[А-Яа-яЁё]", value), f"{path}: untranslated attribute {attribute}={value}"

assert not (references - CATALOG.keys()), f"Unknown translation keys: {references - CATALOG.keys()}"
xkey = "{http://schemas.microsoft.com/winfx/2006/xaml}Key"
palettes = {}
for name in ("Dark", "Light", "Black"):
    tree = ET.parse(ROOT / "Themes" / f"{name}.xaml")
    palettes[name] = {element.attrib[xkey] for element in tree.getroot() if xkey in element.attrib}
assert palettes["Dark"] == palettes["Light"] == palettes["Black"], "Theme resource keys do not match"
assert 'public int LogLimit { get; set; } = 100;' in (ROOT / "Models" / "Routing.cs").read_text()
assert 'public string Language { get; set; } = "ru";' in (ROOT / "Models" / "Routing.cs").read_text()

# CS0136: a local in a method body must not reuse the name of the method's own parameter. The
# Windows build reports it, but there is no SDK here, so the check runs before the code is pushed.
SIGNATURE = re.compile(
    r"^\s{4,8}(?:public|private|internal|protected)\s"
    r"(?:static\s+|async\s+|sealed\s+|override\s+|virtual\s+|partial\s+|new\s+|unsafe\s+|extern\s+)*"
    r"[^;=]*$")
DECLARATION = re.compile(
    r"\bvar\s+(\w+)\s*[=),]"
    r"|\bout\s+var\s+(\w+)"
    r"|\bforeach\s*\(\s*var\s+(\w+)\s+in\b"
    r"|\bfor\s*\(\s*var\s+(\w+)\s*="
    r"|\busing\s+var\s+(\w+)\s*="
    r"|\bvar\s*\(\s*(\w+)\s*,"
    r"|\bcatch\s*\(\s*[\w<>\.\[\]]+\s+(\w+)\s*\)")


def without_comments(source: str) -> list[str]:
    """Drop comments and literals, keeping one entry per source line."""
    lines = []
    for line in source.split("\n"):
        line = re.sub(r'@"(?:[^"]|"")*"', '""', line)
        line = re.sub(r'\$?"(?:[^"\\]|\\.)*"', '""', line)
        line = re.sub(r"'(?:[^'\\]|\\.)'", "''", line)
        lines.append(re.sub(r"//.*$", "", line))
    return lines


def parameter_names(text: str) -> set[str]:
    names = set()
    for part in re.split(r",(?![^<>()]*[>)])", text):
        part = re.sub(r"^(this|ref|out|in|params)\s+", "", part.strip())
        words = re.findall(r"[A-Za-z_]\w*", part.split("=")[0])
        if len(words) >= 2:
            names.add(words[-1])
    return names


def method_bodies(lines: list[str]):
    """(parameters, first line, last line) of every method body in the file."""
    index = 0
    while index < len(lines):
        if not SIGNATURE.match(lines[index]):
            index += 1
            continue

        # подпись может занимать несколько строк: «private async Task SendAsync(» и параметры ниже
        text, cursor = lines[index], index
        while text.count("(") > text.count(")") and cursor + 1 < len(lines):
            cursor += 1
            text += " " + lines[cursor].strip()
        text = text.strip()
        if "(" not in text or text.count("(") != text.count(")") or not text.endswith(")"):
            index += 1
            continue

        opening = cursor + 1
        while opening < len(lines) and lines[opening].strip() == "":
            opening += 1
        if opening >= len(lines) or lines[opening].strip() != "{":
            index += 1
            continue

        depth, closing = 0, opening
        while closing < len(lines):
            depth += lines[closing].count("{") - lines[closing].count("}")
            if depth <= 0:
                break
            closing += 1

        yield text[text.index("(") + 1:text.rindex(")")], opening + 1, closing + 1
        index = closing + 1


shadowing = []
for path in ROOT.rglob("*.cs"):
    lines = without_comments(path.read_text(encoding="utf-8"))
    for signature, start, end in method_bodies(lines):
        names = parameter_names(signature)
        if not names:
            continue
        for number in range(start - 1, min(end, len(lines))):
            for found in DECLARATION.finditer(lines[number]):
                name = next(group for group in found.groups() if group)
                if name in names:
                    shadowing.append(f"{path.relative_to(ROOT)}:{number + 1}: {name}")
assert not shadowing, f"CS0136 local reusing a parameter name: {', '.join(shadowing)}"

print(f"PASS: {len(CATALOG)} RU/EN entries, translation references, XAML, matching theme palettes, "
      f"defaults and no CS0136 parameter shadowing")
