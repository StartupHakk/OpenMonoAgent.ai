import argparse

from commands import cmd_add, cmd_done, cmd_list


def build_parser():
    p = argparse.ArgumentParser(prog="todo")
    sub = p.add_subparsers(dest="cmd", required=True)

    add = sub.add_parser("add", help="add a task")
    add.add_argument("text")

    sub.add_parser("list", help="list tasks")

    done = sub.add_parser("done", help="mark a task done")
    done.add_argument("id", type=int)

    return p


def main():
    args = build_parser().parse_args()
    if args.cmd == "add":
        cmd_add(args.text)
    elif args.cmd == "list":
        cmd_list()
    elif args.cmd == "done":
        cmd_done(args.id)


if __name__ == "__main__":
    main()
