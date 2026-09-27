"""Static Archivo instances for the exported reports (PDF, Word, PowerPoint), from the variable font in google/fonts
(ofl/archivo/Archivo[wdth,wght].ttf, SIL Open Font License).

wdth 100, wght 400 / 600 / 800; Latin, Latin Extended and Vietnamese with the punctuation, arrows and maths signs the
reports use. Named for Office's style linking: "Archivo" Regular (400) and Bold (600), and "Archivo ExtraBold" (800).

    pip install fonttools
    python3 tools/make_doc_fonts.py "Archivo[wdth,wght].ttf" src/ScheduleRisk.Web/wwwroot/fonts
"""
import sys
from fontTools.ttLib import TTFont
from fontTools.varLib import instancer
from fontTools import subset

SRC = sys.argv[1]
OUT = sys.argv[2]
RANGES = [(0x20, 0x7E), (0xA0, 0x24F), (0x300, 0x36F), (0x370, 0x3FF), (0x1E00, 0x1EFF), (0x2000, 0x206F),
          (0x20A0, 0x20CF), (0x2100, 0x214F), (0x2190, 0x21FF), (0x2200, 0x22FF), (0x25A0, 0x25FF)]
FACES = [(400, "Archivo", "Regular", "Archivo", "Archivo-Regular", False),
         (600, "Archivo", "Bold", "Archivo Bold", "Archivo-Bold", True),
         (800, "Archivo ExtraBold", "Regular", "Archivo ExtraBold", "Archivo-ExtraBold", False)]

for weight, family, style, full, ps, bold in FACES:
    vf = TTFont(SRC)
    f = instancer.instantiateVariableFont(vf, {"wght": weight, "wdth": 100})
    opts = subset.Options()
    opts.layout_features = ["kern"]
    opts.hinting = False
    opts.name_IDs = [0, 1, 2, 3, 4, 5, 6, 13, 14]
    opts.name_languages = [0x409]
    opts.glyph_names = False
    opts.notdef_outline = True
    opts.drop_tables += ["STAT", "gasp", "DSIG"]
    s = subset.Subsetter(opts)
    s.populate(unicodes=[c for a, b in RANGES for c in range(a, b + 1)])
    s.subset(f)
    name = f["name"]
    for rec in list(name.names):
        if rec.nameID in (16, 17, 21, 22, 25) or rec.nameID >= 256:
            name.removeNames(nameID=rec.nameID)
    version = name.getDebugName(5) or "Version 2.001"
    for nid, val in ((1, family), (2, style), (3, f"{version.split(';')[0].replace('Version ', '')};{ps}"), (4, full), (5, version), (6, ps)):
        name.setName(val, nid, 3, 1, 0x409)
        name.setName(val, nid, 1, 0, 0)
    os2 = f["OS/2"]
    os2.usWeightClass = weight
    os2.fsSelection = (os2.fsSelection & ~0b1100001) | (0b100000 if bold else 0b1000000)  # BOLD or REGULAR, not ITALIC
    f["head"].macStyle = 1 if bold else 0
    f["post"].formatType = 3.0
    path = f"{OUT}/archivo-doc-{weight}.ttf"
    f.save(path)
    g = TTFont(path)
    print(path, "glyphs", len(g.getGlyphOrder()), "cmap", len(g.getBestCmap()), "tables", sorted(k for k in g.keys() if k != "GlyphOrder"))
