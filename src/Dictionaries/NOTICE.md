# Bundled language data and spell-check library

Switchcraft uses these components locally for word validation. Dictionary files are unmodified, including their affix rules. No suggestion-based spelling correction or network lookup is performed.

- Ukrainian dictionary: dict_uk / Andriy Rysin and contributors, via LibreOffice/dictionaries, MPL 1.1. Source files are supplied here as `uk_UA.dic` and `uk_UA.aff`; see `README_uk_UA.txt` and `MPL-1.1.txt`.
- Russian dictionary: Alexander I. Lebedev and contributors, via LibreOffice/dictionaries. Source files are supplied here as `ru_RU.dic` and `ru_RU.aff`; the full redistribution terms and copyright notice are in `README_ru_RU.txt`.
- Dictionary snapshot: https://github.com/LibreOffice/dictionaries/tree/32b006a2c22a4ac7e8ed3f03346f7b3d85a970a4
- WeCantSpell.Hunspell 7.0.1: unmodified NuGet library, used under the MPL 1.1 option of its MPL/GPL/LGPL license. Copyright and contributor notices are in `WeCantSpell.Hunspell-LICENSE.txt`; the full MPL is in `MPL-1.1.txt`. The source code is available under those terms at https://github.com/aarondandy/WeCantSpell.Hunspell/tree/ab5709d95b2d23541984d22baa0ab2d1e783582f .

The dictionaries and library retain their original licensing and ownership. Switchcraft's integration code does not change those terms.
