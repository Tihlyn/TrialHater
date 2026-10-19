"""Render the report and build an offline searchable catalogue."""
import html
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent
sys.path.insert(0, str(ROOT / '.tools'))
import markdown

STYLE = '''
:root{color-scheme:light;--ink:#152c36;--muted:#53656e;--accent:#126c71;--line:#dce5e5}
*{box-sizing:border-box}body{margin:0;background:#f4f6f4;color:var(--ink);font:16px/1.65 system-ui,Segoe UI,sans-serif}
header{background:#173a42;color:white;padding:40px max(24px,calc((100vw - 1180px)/2));border-bottom:5px solid #c8a85e}
header h1{margin:0 0 8px;font-size:32px}header p{margin:0;color:#d4e2e4}header a{color:#d6eeee}
main{max-width:1180px;margin:30px auto;padding:36px;background:white;border:1px solid var(--line);border-radius:12px}
h1,h2,h3{line-height:1.25;scroll-margin-top:20px}h2{margin-top:48px;padding-top:20px;border-top:1px solid var(--line)}h3{margin-top:30px}
a{color:var(--accent);text-underline-offset:3px;overflow-wrap:anywhere}table{border-collapse:collapse;width:100%;font-size:14px;margin:24px 0}
th{text-align:left;background:#e8efed}th,td{padding:11px 12px;border:1px solid var(--line);vertical-align:top}
tr:nth-child(even){background:#f8faf9}code{font-family:Consolas,monospace;font-size:.9em;overflow-wrap:anywhere}
pre{overflow:auto;padding:20px;background:#edf2f1;border-radius:8px}p,li{max-width:100ch}li{margin:6px 0}
.toc{padding:18px 24px;background:#f0f5f3;border-radius:8px}.toc ul{columns:2;padding-left:20px}.toc ul ul{display:none}
.controls{display:flex;flex-wrap:wrap;gap:12px;align-items:center;position:sticky;top:0;background:white;padding:14px 0;border-bottom:1px solid var(--line)}
input,select,button{font:inherit;padding:9px;border:1px solid #abc2c2;border-radius:6px}input[type=search]{flex:1;min-width:240px}
.muted{color:var(--muted)}.stats{font-variant-numeric:tabular-nums;white-space:nowrap}.tablewrap{overflow-x:auto}
.architecture{display:grid;grid-template-columns:repeat(3,1fr);gap:14px;margin:24px 0}.architecture div{padding:16px;border:1px solid #a9c8c6;background:#eff6f4;border-radius:8px}
@media(max-width:700px){main{margin:0;padding:20px;border-radius:0}.toc ul{columns:1}.architecture{grid-template-columns:1fr}header h1{font-size:26px}}
@media print{body,main{background:white}main{border:0;margin:0;padding:0;max-width:none}.controls,.toc{display:none}header{background:white;color:black;padding:0;border:0}header p{color:black}a{color:inherit}h2,h3{break-after:avoid}tr{break-inside:avoid}}
'''

def page(title, content, subtitle='Research snapshot · 19 October 2026'):
    return f'''<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>{html.escape(title)}</title><style>{STYLE}</style></head><body><header><h1>{html.escape(title)}</h1><p>{subtitle}</p></header><main>{content}</main></body></html>'''

def main():
    source = (ROOT/'REPORT.md').read_text(encoding='utf-8')
    diagram = '''<div class="architecture" aria-label="Architecture flow">
<div><strong>1. Observe</strong><br>Client sheets + addon state → versioned catalogue and immutable snapshot</div>
<div><strong>2. Decide</strong><br>Rules engine + hidden-hand model → deadline-bounded search</div>
<div><strong>3. Validate</strong><br>Fresh snapshot + legal action → client adapter</div>
<div><strong>4. Coordinate</strong><br>Selected goals → collection plan → session state machine</div>
<div><strong>5. Verify</strong><br>Result + inventory + ownership → reward journal</div>
<div><strong>6. Continue</strong><br>Journal → updated collection plan → next match or completion</div></div>'''
    source = re.sub(r'```mermaid\n.*?```', diagram, source, flags=re.S)
    source = source.split('\n',1)[1]
    rendered = markdown.markdown('[TOC]\n\n'+source, extensions=['tables','fenced_code','toc'], extension_configs={'toc':{'toc_depth':'2-2'}})
    rendered = rendered.replace('<table>', '<div class="tablewrap"><table>').replace('</table>', '</table></div>')
    (ROOT/'REPORT.html').write_text(page('Triple Triad · Research & automation design',rendered),encoding='utf-8')

    cards=json.loads((ROOT/'data/cards.json').read_text(encoding='utf-8'))
    embedded=json.dumps(cards,ensure_ascii=False).replace('<','\\u003c')
    content='''<p><a href="REPORT.html">Read the research report</a> · <a href="data/cards.csv">Download CSV</a> · <a href="data/npcs.csv">NPC list</a> · <a href="data/npc-decks.csv">NPC decks</a></p>
<p>475 card records, with every side value cross-checked between FFXIV Collect and XIVAPI. Highest patch label: 7.51. Stats use <strong>top / right / bottom / left</strong>; A means 10. Source text is a snapshot, not a guarantee that your character meets acquisition requirements.</p>
<div class="controls"><input id="search" type="search" placeholder="Search cards, numbers or acquisition sources" aria-label="Search cards">
<select id="stars" aria-label="Filter rarity"><option value="">All rarities</option><option>1</option><option>2</option><option>3</option><option>4</option><option>5</option></select>
<select id="type" aria-label="Filter affiliation"><option value="">All types</option><option>Normal</option><option>Primal</option><option>Scion</option><option>Society</option><option>Garlean</option></select>
<label><input id="npc" type="checkbox"> NPC source</label>
<select id="sort" aria-label="Sort cards"><option value="id">Card ID</option><option value="name">Name</option><option value="stars">Rarity</option><option value="low">Lowest side total</option><option value="high">Highest side total</option></select>
<button id="reset" type="button">Reset</button></div>
<p id="count" class="muted" aria-live="polite"></p><div class="tablewrap"><table><thead><tr><th>ID / album</th><th>Card</th><th>Stars / type</th><th>↑ → ↓ ←</th><th>Patch</th><th>Acquisition sources</th></tr></thead><tbody id="rows"></tbody></table></div>
<script id="card-data" type="application/json">'''+embedded+'''</script>
<script>
const cards=JSON.parse(document.getElementById('card-data').textContent);
const el=id=>document.getElementById(id);
const esc=s=>String(s??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const total=c=>c.top+c.right+c.bottom+c.left;
function render(){
 const query=el('search').value.toLocaleLowerCase().trim();
 const filtered=cards.filter(c=>(!query||[c.name,c.number,c.card_id,c.sources].join(' ').toLocaleLowerCase().includes(query))&&(!el('stars').value||c.stars===Number(el('stars').value))&&(!el('type').value||c.type===el('type').value)&&(!el('npc').checked||c.npc_ids));
 const key=el('sort').value;
 filtered.sort((a,b)=>key==='name'?a.name.localeCompare(b.name):key==='stars'?b.stars-a.stars||a.card_id-b.card_id:key==='low'?total(a)-total(b)||a.card_id-b.card_id:key==='high'?total(b)-total(a)||a.card_id-b.card_id:a.card_id-b.card_id);
 el('count').textContent=filtered.length+' of '+cards.length+' cards';
 el('rows').innerHTML=filtered.map(c=>'<tr><td>'+c.card_id+'<br><span class="muted">'+esc(c.number)+'</span></td><td><a target="_blank" rel="noopener" href="'+esc(c.url)+'">'+esc(c.name)+'</a></td><td>'+c.stars+'★<br>'+esc(c.type)+'</td><td class="stats">'+[c.top,c.right,c.bottom,c.left].map(v=>v===10?'A':v).join(' / ')+'</td><td>'+esc(c.patch)+'</td><td>'+esc(c.sources)+'</td></tr>').join('');
}
for(const id of ['search','stars','type','npc','sort'])el(id).addEventListener('input',render);
el('reset').addEventListener('click',()=>{for(const id of ['search','stars','type'])el(id).value='';el('npc').checked=false;el('sort').value='id';render()});
render();
</script>'''
    (ROOT/'CARD_CATALOGUE.html').write_text(page('Triple Triad · Card catalogue',content),encoding='utf-8')
    print(f'Rendered report and {len(cards)}-card offline catalogue; Markdown {markdown.__version__}')

if __name__=='__main__':
    main()
