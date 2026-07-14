from store import load, save


def cmd_add(text):
    tasks = load()
    tasks.append({"id": len(tasks) + 1, "text": text, "done": False})
    save(tasks)
    print("added %d" % tasks[-1]["id"])


def cmd_list():
    for t in load():
        mark = "x" if t["done"] else " "
        print("[%s] %d %s" % (mark, t["id"], t["text"]))


def cmd_done(task_id):
    tasks = load()
    for t in tasks:
        if t["id"] == task_id:
            t["done"] = True
    save(tasks)
    print("done %d" % task_id)
