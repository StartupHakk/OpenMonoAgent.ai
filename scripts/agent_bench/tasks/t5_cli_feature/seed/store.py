import json
import os

PATH = os.environ.get("TODO_DB", "todo.json")


def load():
    if not os.path.exists(PATH):
        return []
    with open(PATH, encoding="utf-8") as f:
        return json.load(f)


def save(tasks):
    with open(PATH, "w", encoding="utf-8") as f:
        json.dump(tasks, f, indent=2)
