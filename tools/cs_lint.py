"""Poor-man's C# structural lint (no compiler available in the build sandbox).

Checks bracket balance outside strings/comments, semicolon-less statements before
closing braces, and a few C# pitfalls. Not a substitute for `dotnet build`.
"""
import re
import sys
import pathlib


def strip(src):
    out = []
    i, n = 0, len(src)
    while i < n:
        c = src[i]
        if src.startswith("//", i):
            j = src.find("\n", i)
            i = n if j < 0 else j
            continue
        if src.startswith("/*", i):
            j = src.find("*/", i + 2)
            i = n if j < 0 else j + 2
            continue
        if src.startswith('"""', i):
            j = src.find('"""', i + 3)
            out.append('""')
            i = j + 3
            continue
        if c == '$' and i + 1 < n and src[i + 1] == '"' or (c == '$' and src.startswith('@"', i + 1)):
            # interpolated string: keep holes, drop text
            verbatim = src.startswith('@"', i + 1)
            i += 3 if verbatim else 2
            depth = 0
            out.append('"')
            while i < n:
                ch = src[i]
                if depth == 0:
                    if ch == '"':
                        if verbatim and i + 1 < n and src[i + 1] == '"':
                            i += 2
                            continue
                        i += 1
                        break
                    if ch == '\\' and not verbatim:
                        i += 2
                        continue
                    if ch == '{':
                        if i + 1 < n and src[i + 1] == '{':
                            i += 2
                            continue
                        depth = 1
                        out.append('(')
                        i += 1
                        continue
                    i += 1
                else:
                    if ch == '"':  # nested simple string in hole
                        j = i + 1
                        while j < n and src[j] != '"':
                            j += 2 if src[j] == '\\' else 1
                        out.append('""')
                        i = j + 1
                        continue
                    if ch == '{':
                        depth += 1
                    elif ch == '}':
                        depth -= 1
                        if depth == 0:
                            out.append(')')
                            i += 1
                            continue
                    out.append(ch)
                    i += 1
            out.append('"')
            continue
        if c == '@' and i + 1 < n and src[i + 1] == '"':
            j = i + 2
            while j < n:
                if src[j] == '"':
                    if j + 1 < n and src[j + 1] == '"':
                        j += 2
                        continue
                    break
                j += 1
            out.append('""')
            i = j + 1
            continue
        if c == '"':
            j = i + 1
            while j < n and src[j] != '"':
                j += 2 if src[j] == '\\' else 1
            out.append('""')
            i = j + 1
            continue
        if c == "'":
            j = i + 1
            while j < n and src[j] != "'":
                j += 2 if src[j] == '\\' else 1
            out.append("'x'")
            i = j + 1
            continue
        out.append(c)
        i += 1
    return "".join(out)


def check(path):
    src = pathlib.Path(path).read_text(encoding="utf-8")
    code = strip(src)
    errs = []
    stack = []
    pairs = {')': '(', ']': '[', '}': '{'}
    line = 1
    for ch in code:
        if ch == '\n':
            line += 1
        if ch in '([{':
            stack.append((ch, line))
        elif ch in ')]}':
            if not stack or stack[-1][0] != pairs[ch]:
                errs.append(f"{path}:{line}: unbalanced '{ch}' (open: {stack[-1] if stack else None})")
                return errs
            stack.pop()
    if stack:
        errs.append(f"{path}: unclosed {stack[-3:]}")
    for m in re.finditer(r"[^;{}\s,(\[]\s*\n\s*\}", code):
        pass
    # statement lines missing semicolons: line ends with ')' or identifier and next line starts with an identifier (heuristic)
    lines = code.split("\n")
    for k, l in enumerate(lines[:-1]):
        s = l.rstrip()
        nxt = lines[k + 1].strip()
        if re.search(r"(\)|\w|\]|\")$", s) and nxt.startswith("}") and not re.search(r"^\s*(public|private|internal|protected|static|namespace|class|record|enum|struct|get|set|init|case|default|else|try|finally|do|\[)", s) \
                and not s.strip().startswith(("if", "for", "while", "foreach", "switch", "using", "lock", "catch", "=>", "else")) and "=>" not in s[-3:]:
            # likely an enum member, initializer element or missing semicolon; report for review
            if not re.search(r"^\s*[A-Z]\w*\s*(=\s*[\w\"\.]+)?$", s) and "{" not in s:
                errs.append(f"{path}:{k + 1}: possible missing ';' -> {s.strip()[:80]}")
    return errs


if __name__ == "__main__":
    all_errs = []
    for p in sys.argv[1:]:
        all_errs += check(p)
    print("\n".join(all_errs) if all_errs else "structure OK")
