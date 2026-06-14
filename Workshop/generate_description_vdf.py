#!/usr/bin/env python3
"""Generate /tmp/description.vdf for steamcmd — description-only update.

Used by .github/workflows/workshop-update-description.yml.
Omits contentfolder/previewfile so only the Workshop page text is updated.
"""
import os

workspace = os.environ['GITHUB_WORKSPACE']
item_id = os.environ.get('WORKSHOP_ITEM_ID', '')
if not item_id:
    raise SystemExit("ERROR: WORKSHOP_ITEM_ID secret is not set.")

desc_path = os.path.join(workspace, 'Workshop', 'description.txt')
with open(desc_path, 'r') as f:
    description = f.read().replace('\\', '\\\\').replace('"', '\\"').replace('\r', '')

vdf = (
    '"workshopitem"\n'
    '{\n'
    '\t"appid"\t\t\t"255710"\n'
    '\t"publishedfileid"\t"' + item_id + '"\n'
    '\t"description"\t\t"' + description + '"\n'
    '\t"changenote"\t\t"Description update"\n'
    '}\n'
)

with open('/tmp/description.vdf', 'w') as f:
    f.write(vdf)

print("Generated /tmp/description.vdf:")
print(vdf)
