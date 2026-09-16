#!/usr/bin/env python3
"""
Rebuild a PDF without its active content, keeping the form it carries.

The sheets this reference publishes are somebody's artwork, re-hosted here
with permission. Three of the four are form-fillable, and that is the point of
them: a reader types into the fields and prints the result. So this cannot be
the usual "flatten it to images" sanitiser, which would take away the thing
the file exists to do.

What it removes is everything that can *act*: a document-level open action, the
JavaScript name tree, embedded files, launch and additional actions. What it
keeps is the page content, the fonts, the images and the AcroForm field
structure.

## The one exception, and why it is safe

Field formatting is expressed in PDF as JavaScript, so a blanket strip would
leave the number fields unable to format. The official character sheet carries
33 script entries; 32 of them are two calls:

    AFNumber_Format(0, 0, 0, 0, "", true)
    AFNumber_Keystroke(0, 0, 0, 0, "", true)

Those are Adobe's own field-formatting API: declarative, literal-argumented,
and holding no capability. They are kept, and only when the whole entry parses
as exactly one call to one allowlisted function with literal arguments.

The matching is deliberately strict, because the obvious implementation is the
broken one: anything that merely *contains* an approved call would wave through

    AFNumber_Format(0,0,0,0,"",true); app.launchURL("http://example.invalid")

The 33rd entry in that sheet is an /OpenAction running `this.print(...)`, so the
document tries to print itself when opened. Nobody asked for that, and it does
not survive.

Usage:
    python3 sanitize_pdf.py <in.pdf> <out.pdf>

Exits non-zero and writes nothing if the rebuild fails. That is deliberate:
passing the original through because sanitising did not work would make
crashing this script the attacker's goal rather than evading it.
"""

import re
import sys

import pikepdf
from pikepdf import Name

# Adobe's field formatting and calculation API. A closed list: adding to it is
# an edit to this file, which means it is reviewed.
ALLOWED_FUNCTIONS = {
    "AFNumber_Format", "AFNumber_Keystroke",
    "AFPercent_Format", "AFPercent_Keystroke",
    "AFDate_Format", "AFDate_Keystroke",
    "AFDate_FormatEx", "AFDate_KeystrokeEx",
    "AFTime_Format", "AFTime_Keystroke",
    "AFSpecial_Format", "AFSpecial_Keystroke",
    "AFSpecial_KeystrokeEx",
    "AFSimple_Calculate",
    "AFRange_Validate",
    "AFMergeChange",
}

# One call. A function name from the list, then arguments that are only
# literals: numbers, quoted strings, booleans, null. No sequencing, no member
# access, no expressions, no concatenation.
LITERAL = r'\s*(?:-?\d+(?:\.\d+)?|"[^"]*"|' + r"'[^']*'" + r"|true|false|null)\s*"
ONE_CALL = re.compile(
    r"^\s*(?P<name>[A-Za-z_]\w*)\s*\(" + f"(?:{LITERAL}(?:,{LITERAL})*)?" + r"\)\s*;?\s*$"
)

# A backslash in a script entry is refused outright rather than parsed.
#
# String literals here have no legitimate need for an escape (these are format
# masks and separators like "" and ",") and admitting escapes would mean the
# allowlist has to agree with a JavaScript engine about what a string ends on.
# That is precisely the parser-differential this design exists to avoid, so the
# conservative answer is the correct one: no backslash, anywhere.
BACKSLASH = "\\"

ACTION_KEYS = [Name.OpenAction, Name.AA]
CATALOG_NAME_KEYS = [Name.JavaScript, Name.EmbeddedFiles]


def is_known_safe(script: str) -> bool:
    """True only for a single call to one allowlisted function, literals only."""
    if BACKSLASH in script:
        return False
    match = ONE_CALL.match(script.strip())
    return bool(match) and match.group("name") in ALLOWED_FUNCTIONS


def script_text(value) -> str:
    """
    A /JS entry is either a string or a stream; both mean the same thing.

    pikepdf raises its own `PdfError`, not AttributeError or TypeError, when
    `read_bytes` is reached on a string object. Getting this wrong made the
    sanitiser refuse the one sheet in the corpus that actually carries script.
    It refused *safely*, by failing closed, which is the behaviour working as
    designed and is why the bug could not have shipped something unsafe. But it
    would have looked like the file being rejected on its merits.
    """
    try:
        return bytes(value.read_bytes()).decode("utf-8", "replace")
    except (AttributeError, TypeError, pikepdf.PdfError):
        return str(value)


def scrub_action(action, removed: list) -> bool:
    """True if the action may stay. Recurses into /Next, which chains them."""
    kind = action.get(Name.S)

    if kind in (Name.Launch, Name.ImportData, Name.SubmitForm, Name.GoToR, Name.Movie, Name.Sound, Name.RichMediaExecute):
        removed.append(f"action {kind}")
        return False

    if kind == Name.JavaScript:
        script = script_text(action.get(Name.JS, ""))
        if not is_known_safe(script):
            removed.append(f"script {' '.join(script.split())[:70]}")
            return False

    if Name.Next in action:
        if not scrub_action(action[Name.Next], removed):
            del action[Name.Next]

    return True


def scrub_annotation_actions(obj, removed: list) -> None:
    """A widget's /A fires on activation and its /AA on field events."""
    if Name.A in obj:
        if not scrub_action(obj[Name.A], removed):
            del obj[Name.A]

    if Name.AA in obj:
        extra = obj[Name.AA]
        for key in list(extra.keys()):
            if not scrub_action(extra[key], removed):
                removed_key = str(key)
                del extra[removed_key]
        if len(extra) == 0:
            del obj[Name.AA]


def sanitize(source: str, target: str) -> list:
    removed: list = []

    with pikepdf.open(source) as pdf:
        root = pdf.Root

        for key in ACTION_KEYS:
            if key in root:
                removed.append(f"catalog {key}")
                del root[key]

        if Name.Names in root:
            names = root[Name.Names]
            for key in CATALOG_NAME_KEYS:
                if key in names:
                    removed.append(f"name tree {key}")
                    del names[key]

        if Name.AcroForm in root and Name.XFA in root[Name.AcroForm]:
            removed.append("XFA")
            del root[Name.AcroForm][Name.XFA]

        for page in pdf.pages:
            if Name.AA in page:
                removed.append("page AA")
                del page[Name.AA]
            for annot in page.get(Name.Annots, []):
                scrub_annotation_actions(annot, removed)

        for obj in pdf.objects:
            try:
                if Name.EF in obj:
                    removed.append("embedded file")
                    del obj[Name.EF]
            except (TypeError, AttributeError):
                continue

        # linearize=False keeps the output byte-stable for a given input, so a
        # rebuild that changes nothing produces no diff.
        pdf.save(target, linearize=False)

    return removed


def main() -> int:
    if len(sys.argv) != 3:
        print(__doc__.strip(), file=sys.stderr)
        return 2

    source, target = sys.argv[1], sys.argv[2]

    try:
        removed = sanitize(source, target)
    except Exception as error:  # noqa: BLE001, fail closed on anything
        print(f"REFUSED {source}: {error}", file=sys.stderr)
        return 1

    print(f"{source} -> {target}")
    for item in removed:
        print(f"    removed: {item}")
    if not removed:
        print("    removed: nothing; the file carried no active content")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
