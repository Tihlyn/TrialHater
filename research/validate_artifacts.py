"""Check research provenance links and data consistency without a game client."""
import hashlib
import json
import re
from html.parser import HTMLParser
from pathlib import Path
from urllib.parse import unquote, urlparse

ROOT=Path(__file__).resolve().parent

class Links(HTMLParser):
    def __init__(self):
        super().__init__()
        self.hrefs=[]
        self.ids=set()
    def handle_starttag(self,tag,attrs):
        attrs=dict(attrs)
        if 'id' in attrs:self.ids.add(attrs['id'])
        if tag=='a' and 'href' in attrs:self.hrefs.append(attrs['href'])

report=(ROOT/'REPORT.md').read_text(encoding='utf-8')
definitions=dict(re.findall(r'^\[([^\]]+)\]: (\S+)',report,re.M))
uses=set(re.findall(r'\]\[([^\]]+)\]',report))
assert not uses-set(definitions),uses-set(definitions)
repos={r['url'].removesuffix('.git'):r for r in json.loads((ROOT/'data/repository-manifest.json').read_text(encoding='utf-8'))}
checked=0
for label,url in definitions.items():
    for repo_url,repo in repos.items():
        prefix=f"{repo_url}/blob/{repo['commit']}/"
        if url.startswith(prefix):
            target=ROOT/'sources'/repo['name']/unquote(url[len(prefix):])
            assert target.is_file(),(label,target)
            checked+=1

local_links=0
for filename in ('REPORT.html','CARD_CATALOGUE.html'):
    parser=Links()
    parser.feed((ROOT/filename).read_text(encoding='utf-8'))
    for href in parser.hrefs:
        parsed=urlparse(href)
        if parsed.scheme:continue
        if parsed.path:
            assert (ROOT/unquote(parsed.path)).exists(),(filename,href)
            local_links+=1
        elif parsed.fragment:
            assert unquote(parsed.fragment) in parser.ids,(filename,href)

for source in json.loads((ROOT/'data/source-manifest.json').read_text(encoding='utf-8')):
    data=(ROOT/'data'/source['file']).read_bytes()
    assert hashlib.sha256(data).hexdigest()==source['sha256'],source['file']

summary=json.loads((ROOT/'data/summary.json').read_text(encoding='utf-8'))
assert not summary['xivapi_stat_mismatches']
result=dict(report_word_count=len(report.split()),reference_links=len(definitions),
            pinned_source_files_verified=checked,local_artifact_links_verified=local_links,
            card_stats_cross_checked=summary['cards'],stat_mismatches=0,
            source_hashes_verified=True)
(ROOT/'validation/artifact-checks.json').write_text(json.dumps(result,indent=2)+'\n',encoding='utf-8')
print(json.dumps(result,indent=2))
