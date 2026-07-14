import sys

from report import to_csv, to_psv, to_tsv

ROWS = [
    {"name": "widget", "price": 9.5, "in_stock": True, "note": None},
    {"name": "bolt, hex", "price": 0.25, "in_stock": False, "note": "bulk"},
]
COLUMNS = ["name", "price", "in_stock", "note"]

FORMATS = {"csv": to_csv, "tsv": to_tsv, "psv": to_psv}

if __name__ == "__main__":
    fmt = sys.argv[1] if len(sys.argv) > 1 else "csv"
    sys.stdout.write(FORMATS[fmt](ROWS, COLUMNS))
