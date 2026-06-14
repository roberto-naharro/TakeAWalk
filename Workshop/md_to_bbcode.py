#!/usr/bin/env python3
"""Read Markdown from stdin, write Steam BBCode to stdout.

Used by .github/workflows/workshop-deploy.yml to convert the CHANGELOG.md
section for the current release into Steam Workshop BBCode format.
"""
import sys
import re


def md_to_bb(text):
    # URLs first — before bold/code introduce extra brackets.
    text = re.sub(r'\[([^\[\]]+)\]\(([^)]+)\)', r'[url=\2]\1[/url]', text)
    text = re.sub(r'\*\*(.+?)\*\*', r'[b]\1[/b]', text)
    text = re.sub(r'`(.+?)`', r'[b]\1[/b]', text)
    return text


lines = sys.stdin.read().splitlines()
out = []
in_list = False

for line in lines:
    # h1–h3 heading → [h1]…[/h1]
    m = re.match(r'^#{1,3}\s+(.*)', line)
    if m:
        if in_list:
            out.append('[/list]')
            in_list = False
        out.append('[h1]' + m.group(1).strip() + '[/h1]')
        continue

    # bullet → [*] item (open [list] block if needed)
    m = re.match(r'^[\*\-]\s+(.*)', line)
    if m:
        if not in_list:
            out.append('[list]')
            in_list = True
        out.append('[*] ' + md_to_bb(m.group(1).strip()))
        continue

    # non-bullet line closes any open list
    if in_list:
        out.append('[/list]')
        in_list = False

    if line.strip() == '':
        out.append('')
        continue

    out.append(md_to_bb(line))

if in_list:
    out.append('[/list]')

print('\n'.join(out))
