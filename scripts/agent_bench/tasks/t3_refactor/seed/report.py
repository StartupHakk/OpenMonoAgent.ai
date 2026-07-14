"""Table exporters. Grown by copy-paste; behaviour is what the callers rely on."""


def to_csv(rows, columns):
    out = ",".join(c.upper() for c in columns)
    out += "\n"
    for row in rows:
        cells = []
        for col in columns:
            v = row.get(col)
            if v is None:
                cells.append("")
            elif isinstance(v, bool):
                cells.append("yes" if v else "no")
            elif isinstance(v, float):
                cells.append("%.2f" % v)
            else:
                s = str(v)
                if "," in s:
                    s = '"' + s + '"'
                cells.append(s)
        out += ",".join(cells)
        out += "\n"
    return out


def to_tsv(rows, columns):
    out = "\t".join(c.upper() for c in columns)
    out += "\n"
    for row in rows:
        cells = []
        for col in columns:
            v = row.get(col)
            if v is None:
                cells.append("")
            elif isinstance(v, bool):
                cells.append("yes" if v else "no")
            elif isinstance(v, float):
                cells.append("%.2f" % v)
            else:
                s = str(v)
                if "\t" in s:
                    s = '"' + s + '"'
                cells.append(s)
        out += "\t".join(cells)
        out += "\n"
    return out


def to_psv(rows, columns):
    out = "|".join(c.upper() for c in columns)
    out += "\n"
    for row in rows:
        cells = []
        for col in columns:
            v = row.get(col)
            if v is None:
                cells.append("")
            elif isinstance(v, bool):
                cells.append("yes" if v else "no")
            elif isinstance(v, float):
                cells.append("%.2f" % v)
            else:
                s = str(v)
                if "|" in s:
                    s = '"' + s + '"'
                cells.append(s)
        out += "|".join(cells)
        out += "\n"
    return out
