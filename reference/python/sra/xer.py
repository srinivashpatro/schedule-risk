"""Tolerant reader/writer for Primavera P6 XER files.

XER is an undocumented tab-delimited table dump:
    ERMHDR <version> <export date> ...
    %T  <table name>
    %F  <field> <field> ...
    %R  <value> <value> ...
    %E
Unknown tables and columns are preserved verbatim so a file can be round-tripped.
"""


class XerTable:
    def __init__(self, name, fields):
        self.name = name
        self.fields = list(fields)
        self.rows = []  # list of lists, padded to len(fields)

    def dicts(self):
        f = self.fields
        return [dict(zip(f, r)) for r in self.rows]


class XerDocument:
    def __init__(self):
        self.header = []
        self.tables = {}  # name -> XerTable, insertion-ordered
        self.warnings = []
        self.encoding = "cp1252"

    def table(self, name):
        return self.tables.get(name)

    def rows(self, name):
        t = self.tables.get(name)
        return t.dicts() if t else []


def decode_bytes(data):
    if data.startswith(b"\xef\xbb\xbf"):
        return data[3:].decode("utf-8"), "utf-8-sig"
    try:
        return data.decode("utf-8"), "utf-8"
    except UnicodeDecodeError:
        return data.decode("cp1252", errors="replace"), "cp1252"


def parse_xer_text(text):
    doc = XerDocument()
    current = None
    for lineno, raw in enumerate(text.split("\n"), start=1):
        if raw.endswith("\r"):
            raw = raw[:-1]
        if not raw:
            continue
        parts = raw.split("\t")
        tag = parts[0]
        if tag == "ERMHDR":
            doc.header = parts[1:]
        elif tag == "%T":
            name = parts[1].strip() if len(parts) > 1 else ""
            current = XerTable(name, [])
            if name in doc.tables:
                doc.warnings.append(f"line {lineno}: duplicate table {name}; rows appended")
                current = doc.tables[name]
            else:
                doc.tables[name] = current
        elif tag == "%F":
            if current is None:
                doc.warnings.append(f"line {lineno}: %F before %T ignored")
                continue
            if not current.fields:
                current.fields = [p.strip() for p in parts[1:]]
        elif tag == "%R":
            if current is None:
                doc.warnings.append(f"line {lineno}: %R before %T ignored")
                continue
            vals = parts[1:]
            n = len(current.fields)
            if len(vals) < n:
                vals = vals + [""] * (n - len(vals))
            elif len(vals) > n:
                doc.warnings.append(f"line {lineno}: {current.name} row has {len(vals)} values for {n} fields; extra dropped")
                vals = vals[:n]
            current.rows.append(vals)
        elif tag == "%E":
            break
        else:
            doc.warnings.append(f"line {lineno}: unrecognised line tag {tag[:10]!r}")
    return doc


def read_xer(path):
    with open(path, "rb") as fh:
        data = fh.read()
    text, enc = decode_bytes(data)
    doc = parse_xer_text(text)
    doc.encoding = enc
    return doc


def write_xer_text(doc):
    lines = ["\t".join(["ERMHDR"] + doc.header)]
    for t in doc.tables.values():
        lines.append("%T\t" + t.name)
        lines.append("\t".join(["%F"] + t.fields))
        for r in t.rows:
            lines.append("\t".join(["%R"] + r))
    lines.append("%E")
    return "\r\n".join(lines) + "\r\n"
