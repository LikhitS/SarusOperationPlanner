"""Style lint for Sarus documents (second editing pass).

Checks visible text of .html, .md and .txt files against the Sarus writing rules:
banned filler phrases, emojis, bullet density, bold lead-ins on list items, sentences that
start the same way back to back, and runs of sentences of near-identical length.

usage: python lint.py file [file ...]      exit code 1 if anything is flagged
"""
import html
import re
import statistics
import sys

BANNED = [
    r"seamless(ly)?", r"robust", r"leverag(e|es|ed|ing)", r"comprehensive", r"ensur(e|es|ed|ing)",
    r"crucial", r"vital", r"key (feature|benefit|point|takeaway)s?", r"moreover", r"furthermore",
    r"in conclusion", r"it'?s worth noting", r"it is worth noting", r"whether you'?re", r"not just\b.*\bbut\b",
    r"cutting[- ]edge", r"state[- ]of[- ]the[- ]art", r"best[- ]in[- ]class", r"game[- ]chang(er|ing)",
    r"empower(s|ed|ing)?", r"streamlin(e|es|ed|ing)", r"delv(e|es|ing)", r"unlock(s|ed|ing)?", r"harness(es|ing)? (the|its|our|your)",
    r"plays? a (key|crucial|vital|pivotal) role", r"a wide (range|variety) of", r"in today'?s", r"here'?s (what|how|why)",
    r"let's", r"dive (in|into)", r"peace of mind", r"look no further", r"at the end of the day",
    r"pivotal", r"elevat(e|es|ed|ing)", r"effortless(ly)?", r"world[- ]class", r"next[- ]level",
    r"in order to", r"utili[sz](e|es|ed|ing|ation)", r"simply", r"very", r"really", r"basically",
    r"navigate the", r"landscape", r"journey", r"holistic", r"synergy", r"tailored", r"boasts?",
]
EMOJI = re.compile("[\U0001F300-\U0001FAFF☀-➿⬀-⯿✅❌⏳]")


def visible_text(raw, is_html):
    if not is_html:
        return raw
    raw = re.sub(r"(?is)<(script|style|svg|code|kbd|title)\b.*?</\1>", " ", raw)
    raw = re.sub(r"(?is)<(td|th)\b[^>]*>", " \n", raw)
    raw = re.sub(r"(?is)</(p|li|h[1-6]|tr|div|dd|dt|figcaption)>", ".\n", raw)
    return html.unescape(re.sub(r"(?s)<[^>]+>", " ", raw))


def prose_paragraphs(raw, is_html):
    if is_html:
        paras = re.findall(r"(?is)<p\b[^>]*>(.*?)</p>", raw)
        return [html.unescape(re.sub(r"(?s)<[^>]+>", "", p)) for p in paras]
    return [p for p in re.split(r"\n\s*\n", raw) if p.strip() and not p.lstrip().startswith(("-", "*", "|", "#"))]


def sentences(text):
    text = re.sub(r"\s+", " ", text)
    parts = re.split(r"(?<=[.!?])\s+(?=[A-Z0-9\"'(])", text)
    return [s.strip() for s in parts if len(s.split()) >= 3]


def lint(path):
    raw = open(path, encoding="utf-8").read()
    is_html = path.lower().endswith((".html", ".htm"))
    text = visible_text(raw, is_html)
    issues = []

    for pat in BANNED:
        for m in re.finditer(r"(?i)\b" + pat + r"\b", text):
            ctx = text[max(0, m.start() - 40):m.end() + 40].replace("\n", " ")
            issues.append("banned phrase '%s': ...%s..." % (m.group(0), ctx.strip()))
    for m in EMOJI.finditer(text):
        issues.append("emoji %r" % m.group(0))

    if is_html:
        items = re.findall(r"(?is)<li\b[^>]*>(.*?)</li>", raw)
        steps = len(re.findall(r"(?is)<ol[^>]*class=\"steps\"[^>]*>.*?</ol>", raw))
        paras = len(re.findall(r"(?is)<p\b", raw))
        lead = sum(1 for i in items if re.match(r"(?is)\s*<(b|strong)>[^<]{1,60}</(b|strong)>\s*[:.\-]?", i))
        if items and lead / len(items) > 0.3:
            issues.append("bold lead-ins on %d of %d list items (max 30%%)" % (lead, len(items)))
        cells = re.findall(r"(?is)<td\b[^>]*>(.*?)</td>", raw)
        bold_cells = sum(1 for c in cells if re.match(r"(?is)\s*<(b|strong)>[^<]{1,80}</(b|strong)>", c))
        if bold_cells > 3:
            issues.append("%d table cells open with a bold phrase (max 3)" % bold_cells)
        bullets = len(re.findall(r"(?is)<ul\b", raw))
        if bullets > max(2, paras // 4):
            issues.append("%d bullet lists against %d paragraphs (prefer prose)" % (bullets, paras))
    else:
        lines = raw.splitlines()
        bl = sum(1 for l in lines if re.match(r"\s*[-*] ", l))
        if lines and bl > len(lines) * 0.35:
            issues.append("%d of %d lines are bullets (prefer prose)" % (bl, len(lines)))

    for para in prose_paragraphs(raw, is_html):
        ss = sentences(para)
        for a, b in zip(ss, ss[1:]):
            wa, wb = a.split()[0].lower(), b.split()[0].lower()
            if wa == wb and wa not in ("the",):
                issues.append("two sentences in a row start with '%s': %s" % (a.split()[0], b[:70]))
        lens = [len(s.split()) for s in ss]
        for i in range(len(lens) - 3):
            win = lens[i:i + 4]
            if max(win) - min(win) <= 4 and statistics.mean(win) > 8:
                issues.append("4 sentences of similar length (%s): %s" % (win, ss[i][:70]))
                break
    allsent = [len(s.split()) for p in prose_paragraphs(raw, is_html) for s in sentences(p)]
    stats = ""
    if len(allsent) > 5:
        stats = "sentences %d, mean %.1f words, stdev %.1f" % (len(allsent), statistics.mean(allsent), statistics.pstdev(allsent))
        if statistics.pstdev(allsent) < 5:
            issues.append("sentence length too uniform (" + stats + ")")
    return issues, stats


def main():
    bad = 0
    for p in sys.argv[1:]:
        issues, stats = lint(p)
        print("== %s: %d issue(s) %s" % (p, len(issues), ("| " + stats) if stats else ""))
        for i in issues:
            print("   " + i)
        bad += len(issues)
    sys.exit(1 if bad else 0)


if __name__ == "__main__":
    main()
