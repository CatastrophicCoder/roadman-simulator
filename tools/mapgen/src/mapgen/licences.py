"""Licence names, as adapters and overrides record them, and their licence text URLs.

The single table used by `mapgen notice` (re-exported as `mapgen.notice.LICENCE_URLS`)
and by the building override validation. It has no other imports, so any module can use it.
"""

LICENCE_URLS: dict[str, str] = {
    "CC BY 4.0": "https://creativecommons.org/licenses/by/4.0/",
    "ODbL 1.0": "https://opendatacommons.org/licenses/odbl/1-0/",
    "CC BY-SA 4.0": "https://creativecommons.org/licenses/by-sa/4.0/",
}
