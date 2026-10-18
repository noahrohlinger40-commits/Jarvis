"""Review Jarvis's logged planner calls, pick a test set, and score tool choice.

    python scripts\planner-eval.py show
    python scripts\planner-eval.py pick 3 7 12
    python scripts\planner-eval.py score --model qwen/qwen3-4b-2507

Run `score` once with the stock model (before) and once with the fine-tuned
one (after) and compare the summary lines. Stdlib only; output is ASCII.
"""
import argparse
import json
import os
import sys
import tempfile
import urllib.error
import urllib.request
from pathlib import Path

# Same private folder Jarvis writes to (JarvisDataPaths.cs), outside OneDrive.
ROOT = Path(os.environ["LOCALAPPDATA"]) / "Jarvis"
LOG = "data/planner-log.jsonl"
EVAL = "data/planner-eval.jsonl"


def path_of(p):
    p = Path(p)
    return p if p.is_absolute() else ROOT / p


def ascii_(s):
    return str(s).encode("ascii", "replace").decode("ascii")


def flat(s, n):
    s = " ".join(str(s).split())
    return s if len(s) <= n else s[:n] + "..."


def text_of(content):
    """Message content as plain text: string, null, or a list of parts."""
    if content is None:
        return ""
    if isinstance(content, str):
        return content
    return " ".join(p.get("text", "") for p in content if isinstance(p, dict))


def last_user_text(request):
    for m in reversed(request.get("messages") or []):
        if m.get("role") == "user":
            return text_of(m.get("content"))
    return ""


def tool_calls(message):
    return (message or {}).get("tool_calls") or []


def tool_names(message):
    return {c.get("function", {}).get("name", "?") for c in tool_calls(message)}


def args_valid(message):
    for c in tool_calls(message):
        a = c.get("function", {}).get("arguments")
        if isinstance(a, str) and not a.strip():
            continue  # no-argument tool; Jarvis accepts an empty string
        try:
            if not isinstance(a if isinstance(a, dict) else json.loads(a), dict):
                return False
        except (TypeError, ValueError):
            return False
    return True


def match(expected, got):
    return expected == got


def read_lines(p):
    return path_of(p).read_text(encoding="utf-8").splitlines()


def describe(message):
    calls = tool_calls(message)
    if calls:
        return ", ".join(
            "%s(%s)" % (c.get("function", {}).get("name", "?"), flat(c.get("function", {}).get("arguments", ""), 60))
            for c in calls)
    return "say: " + flat(text_of((message or {}).get("content")), 60)


def cmd_show(a):
    for n, line in enumerate(read_lines(a.file), 1):
        if not line.strip():
            continue
        try:
            r = json.loads(line)
            resp = r.get("response") or {}
            print(ascii_("%d %s %s | %s -> %s" % (
                n, r.get("route"), resp.get("finish_reason"),
                flat(last_user_text(r.get("request") or {}), 70), describe(resp.get("message")))))
        except (ValueError, AttributeError):
            print("%d (unreadable line)" % n)


def cmd_pick(a):
    src = read_lines(a.src)
    dest = path_of(a.dest)
    have = set(read_lines(dest)) if dest.exists() else set()
    new = []
    for n in a.numbers:
        if not 1 <= n <= len(src):
            print("line %d: not in %s (it has %d lines), skipped" % (n, a.src, len(src)))
        elif src[n - 1] not in have and src[n - 1].strip():
            have.add(src[n - 1])
            new.append(src[n - 1])
    dest.parent.mkdir(parents=True, exist_ok=True)
    with open(dest, "a", encoding="utf-8", newline="\n") as f:
        if dest.stat().st_size and not dest.read_bytes().endswith(b"\n"):
            f.write("\n")
        f.writelines(l + "\n" for l in new)
    print("added %d to %s (%d already there or skipped)" % (len(new), a.dest, len(a.numbers) - len(new)))


def ask(url, request, model):
    body = dict(request, temperature=0)
    if model:
        body["model"] = model
    req = urllib.request.Request(url, json.dumps(body).encode("utf-8"), {"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=120) as r:
        return json.loads(r.read().decode("utf-8"))


def cmd_score(a):
    base = a.base_url.rstrip("/")
    url = (base if base.endswith("/v1") else base + "/v1") + "/chat/completions"
    lines = [(n, l) for n, l in enumerate(read_lines(a.file), 1) if l.strip()]
    if a.limit:
        lines = lines[:a.limit]
    ok = valid = total = skipped = 0
    model = a.model
    for n, line in lines:
        rec = json.loads(line)
        resp = rec.get("response") or {}
        if resp.get("finish_reason") == "length":
            skipped += 1
            print("%d SKIP (logged reply was truncated)" % n)
            continue
        try:
            out = ask(url, rec["request"], a.model)
        except urllib.error.HTTPError as e:
            print(ascii_("Server at %s answered HTTP %d: %s" % (url, e.code, flat(e.read().decode("utf-8", "replace"), 200))))
            return 1
        except (urllib.error.URLError, OSError):
            print("Could not reach %s. Is LM Studio's server running with the model loaded?" % url)
            return 1
        model = out.get("model") or model
        new = out["choices"][0]["message"]
        want, got = tool_names(resp.get("message")), tool_names(new)
        passed = match(want, got)
        total += 1
        ok += passed
        valid += args_valid(new)
        show = lambda s: ", ".join(sorted(s)) or "(answered directly)"
        print(ascii_("%d %s %s | expected %s / got %s" % (
            n, "PASS" if passed else "FAIL", flat(last_user_text(rec["request"]), 50), show(want), show(got))))
    pct = 100 * ok // total if total else 0
    print("tool choice: %d/%d (%d%%)  valid arguments: %d/%d  skipped: %d  model: %s"
          % (ok, total, pct, valid, total, skipped, model or "(server default)"))
    return 0


def selftest():
    msg = {"tool_calls": [{"function": {"name": "open_app", "arguments": "{\"name\":\"Spotify\"}"}},
                          {"function": {"name": "set_volume", "arguments": "{}"}}]}
    assert tool_names(msg) == {"open_app", "set_volume"}
    assert tool_names({"content": "hi", "tool_calls": None}) == set()
    assert tool_names(None) == set()
    assert match({"a"}, {"a"}) and not match({"a"}, {"b"}) and not match({"a"}, set())
    assert match(set(), set())
    assert args_valid(msg) and args_valid({"content": "hi"})
    assert not args_valid({"tool_calls": [{"function": {"name": "x", "arguments": "{oops"}}]})
    assert not args_valid({"tool_calls": [{"function": {"name": "x", "arguments": "[1]"}}]})
    assert not args_valid({"tool_calls": [{"function": {"name": "x", "arguments": None}}]})
    assert args_valid({"tool_calls": [{"function": {"name": "x", "arguments": ""}}]})
    sysm = {"role": "system", "content": "s"}
    assert last_user_text({"messages": [sysm, {"role": "user", "content": "one"}, {"role": "user", "content": "two"}]}) == "two"
    assert last_user_text({"messages": [{"role": "user", "content": [{"type": "text", "text": "a"}, {"type": "text", "text": "b"}]}]}) == "a b"
    assert last_user_text({"messages": [sysm, {"role": "user", "content": None}]}) == ""
    assert last_user_text({"messages": [sysm]}) == ""

    def rec(user, calls, finish="tool_calls"):
        m = {"role": "assistant", "content": None if calls else "Sure thing", "tool_calls": calls}
        return json.dumps({"route": "Fast", "response": {"message": m, "finish_reason": finish},
                           "request": {"messages": [sysm, {"role": "user", "content": user}]}})

    call = [{"function": {"name": "open_app", "arguments": "{\"name\":\"Spotify\"}"}}]
    with tempfile.TemporaryDirectory() as d:
        log, ev = Path(d) / "log.jsonl", Path(d) / "eval.jsonl"
        log.write_text("\n".join([rec("open spotify\nplease", call), rec("how are you", []), rec("x", [], "length")]) + "\n",
                       encoding="utf-8")
        cmd_show(argparse.Namespace(file=str(log)))
        cmd_pick(argparse.Namespace(numbers=[1, 2, 2, 9], src=str(log), dest=str(ev)))
        cmd_pick(argparse.Namespace(numbers=[1, 3], src=str(log), dest=str(ev)))
        got = ev.read_text(encoding="utf-8").splitlines()
        assert got == log.read_text(encoding="utf-8").splitlines(), got
    print("selftest ok")


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest="cmd", required=True)
    s = sub.add_parser("show", help="one line per logged call")
    s.add_argument("file", nargs="?", default=LOG)
    s = sub.add_parser("pick", help="copy log lines into the eval file")
    s.add_argument("numbers", nargs="+", type=int, help="1-based line numbers")
    s.add_argument("--from", dest="src", default=LOG)
    s.add_argument("--to", dest="dest", default=EVAL)
    s = sub.add_parser("score", help="replay the eval file against a model")
    s.add_argument("file", nargs="?", default=EVAL)
    s.add_argument("--base-url", default="http://127.0.0.1:1234")
    s.add_argument("--model")
    s.add_argument("--limit", type=int)
    sub.add_parser("selftest", help="check the helpers, no network")
    a = p.parse_args()
    try:
        sys.stdout.reconfigure(errors="replace")
        if a.cmd == "selftest":
            return selftest()
        return {"show": cmd_show, "pick": cmd_pick, "score": cmd_score}[a.cmd](a)
    except FileNotFoundError as e:
        print("File not found: %s" % e.filename)
        return 1


if __name__ == "__main__":
    sys.exit(main())
