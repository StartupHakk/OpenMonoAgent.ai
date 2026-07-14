import re


def slugify(text):
    """Lowercase, non-alphanumerics collapsed to single dashes, trimmed."""
    return re.sub(r"[^a-z0-9]+", "-", text.lower()).strip("-")
