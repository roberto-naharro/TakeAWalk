#!/usr/bin/env python3
"""Generate /tmp/item.vdf for steamcmd workshop_build_item.

Used by .github/workflows/workshop-deploy.yml.
Reads GITHUB_WORKSPACE, CHANGE_NOTE, and WORKSHOP_ITEM_ID from the environment.
"""
import os

workspace = os.environ['GITHUB_WORKSPACE']
note = os.environ.get('CHANGE_NOTE', 'Update')
item_id = os.environ.get('WORKSHOP_ITEM_ID', '')
if not item_id:
    raise SystemExit("ERROR: WORKSHOP_ITEM_ID secret is not set. Do the first publish manually with ./publish.sh, then add the ID as a GitHub secret.")

desc_path = os.path.join(workspace, 'Workshop', 'description.txt')
with open(desc_path, 'r') as f:
    description = f.read().replace('\\', '\\\\').replace('"', '\\"').replace('\r', '')


def vdf_escape_line(s):
    return s.replace('\\', '\\\\').replace('"', '\\"').replace('\r', '').replace('\n', ' ')


# previewfile is optional: steamcmd fails with "File Not Found" if the path is set but the file is
# missing, so only emit the line when PreviewImage.png actually exists.
preview_path = os.path.join(workspace, 'Workshop', 'PreviewImage.png')
preview_line = ''
if os.path.isfile(preview_path):
    preview_line = '\t"previewfile"\t\t"' + workspace + '/Workshop/PreviewImage.png"\n'
else:
    print("NOTE: " + preview_path + " not found, publishing without a preview image.")

vdf = (
    '"workshopitem"\n'
    '{\n'
    '\t"appid"\t\t\t"255710"\n'
    '\t"publishedfileid"\t"' + item_id + '"\n'
    '\t"contentfolder"\t\t"' + workspace + '/dist/TakeAWalk"\n'
    + preview_line +
    '\t"description"\t\t"' + description + '"\n'
    '\t"changenote"\t\t"' + vdf_escape_line(note)[:7900] + '"\n'
    '}\n'
)

with open('/tmp/item.vdf', 'w') as f:
    f.write(vdf)

print("Generated /tmp/item.vdf:")
print(vdf)
