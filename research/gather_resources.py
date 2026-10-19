"""Reproducible public-data snapshot and normalized research catalogue (stdlib only)."""
import csv
import hashlib
import json
import re
import subprocess
import urllib.request
from collections import Counter
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parent
DATA = ROOT / 'data'
URLS = {
    'collect-cards.raw.json': 'https://ffxivcollect.com/api/triad/cards',
    'collect-npcs.raw.json': 'https://ffxivcollect.com/api/triad/npcs',
    'collect-packs.raw.json': 'https://ffxivcollect.com/api/triad/packs',
    'collect-decks-sample.raw.json': 'https://ffxivcollect.com/api/triad/decks?limit=100',
    'xivapi-card-names.raw.json': 'https://v2.xivapi.com/api/sheet/TripleTriadCard?limit=500&fields=Name',
    'xivapi-card-stats.raw.json': 'https://v2.xivapi.com/api/sheet/TripleTriadCardResident?limit=500&fields=Top,Right,Bottom,Left,Order,Rarity,TripleTriadCardType',
    'xivapi-rules.raw.json': 'https://v2.xivapi.com/api/sheet/TripleTriadRule?limit=100&fields=Name',
    'xivapi-npc-decks.raw.json': 'https://v2.xivapi.com/api/sheet/TripleTriad?limit=500&fields=TripleTriadCardFixed,TripleTriadCardVariable,TripleTriadRule,Fee,Reward,Quest',
}

def read(name):
    return json.loads((DATA / name).read_text(encoding='utf-8'))

def write_json(name, obj):
    (DATA / name).write_text(json.dumps(obj, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')

def write_csv(name, rows):
    with (DATA / name).open('w', encoding='utf-8-sig', newline='') as out:
        writer = csv.DictWriter(out, fieldnames=list(rows[0]))
        writer.writeheader()
        writer.writerows(rows)

def main():
    import argparse
    parser = argparse.ArgumentParser()
    parser.add_argument('--refresh', action='store_true')
    args = parser.parse_args()
    DATA.mkdir(exist_ok=True)
    manifest = []
    for name, url in URLS.items():
        path = DATA / name
        if args.refresh or not path.exists():
            req = urllib.request.Request(url, headers={'User-Agent': 'TripleTriadResearch/1.0'})
            with urllib.request.urlopen(req, timeout=60) as response:
                payload = response.read()
            json.loads(payload)
            path.write_bytes(payload)
        raw = path.read_bytes()
        manifest.append(dict(file=name, url=url, bytes=len(raw), sha256=hashlib.sha256(raw).hexdigest(),
                             file_written_utc=datetime.fromtimestamp(path.stat().st_mtime, timezone.utc).isoformat()))

    cards = read('collect-cards.raw.json')['results']
    npcs = read('collect-npcs.raw.json')['results']
    rows = []
    for card in sorted(cards, key=lambda c: c['id']):
        stats = card['stats']['numeric']
        rows.append(dict(card_id=card['id'], number=card['number'], name=card['name'], stars=card['stars'],
                         type=card['type']['name'], top=stats['top'], right=stats['right'], bottom=stats['bottom'], left=stats['left'],
                         patch=card['patch'], sell_mgp=card['sell_price'],
                         sources=' | '.join(f"{s['type']}: {s['text']}" for s in card['sources']),
                         npc_ids=';'.join(str(s['related_id']) for s in card['sources'] if s['type']=='NPC'),
                         url=card['link']))
    write_csv('cards.csv', rows)
    write_json('cards.json', rows)
    npc_rows = []
    for npc in sorted(npcs, key=lambda n: n['id']):
        loc = npc.get('location') or {}
        quest = npc.get('quest') or {}
        npc_rows.append(dict(triple_triad_id=npc['id'], resident_id=npc['resident_id'], name=npc['name'],
                             zone=loc.get('name'), x=loc.get('x'), y=loc.get('y'), patch=npc['patch'],
                             excluded=npc['excluded'], rules=' | '.join(npc['rules']),
                             collect_rule_ids=';'.join(map(str,npc['rule_ids'])),
                             quest_id=quest.get('id'), quest_name=quest.get('name'),
                             reward_card_ids=';'.join(str(c['id']) for c in npc['rewards']), url=npc['link']))
    write_csv('npcs.csv', npc_rows)
    npc_by_id = {n['id']: n for n in npcs}
    card_by_id = {c['id']: c for c in cards}
    deck_rows = []
    for row in read('xivapi-npc-decks.raw.json')['rows']:
        fields = row['fields']
        fixed = [c['value'] for c in fields['TripleTriadCardFixed'] if c['value']]
        variable = [c['value'] for c in fields['TripleTriadCardVariable'] if c['value']]
        if not fixed and not variable:
            continue
        npc = npc_by_id.get(row['row_id'], {})
        deck_rows.append(dict(triple_triad_id=row['row_id'], name=npc.get('name','UNMAPPED'),
                              fee_mgp=fields.get('Fee'), fixed_card_ids=';'.join(map(str,fixed)),
                              variable_pool_ids=';'.join(map(str,variable)),
                              fixed_cards=' | '.join(card_by_id.get(i,{}).get('name',str(i)) for i in fixed),
                              variable_pool=' | '.join(card_by_id.get(i,{}).get('name',str(i)) for i in variable),
                              rule_ids=';'.join(str(r['value']) for r in fields['TripleTriadRule'] if r['value'])))
    write_csv('npc-decks.csv', deck_rows)
    presets = [
        ('Tutorial', [1,3,6,7,10], 'Starter cards; baseline only.'),
        ('Normal corners', [75,118,88,89,91], 'Three distinct double-eight orientations, a flexible four-star, and Squall. Seed for optimization, not a universal optimum.'),
        ('Ascension primals', [40,41,42,53,55], 'Five Primals; two four-stars and three three-stars. Re-evaluate against opposing Primals and Reverse.'),
        ('Ascension scions', [46,49,50,165,166], 'Five Scions with two four-stars. Seed only.'),
        ('Reverse budget', [2,4,6,7,81], 'Low-value cards; avoid exposed high sides. Seed only, not benchmarked.'),
    ]
    examples = []
    for name, ids, note in presets:
        selected = [card_by_id[i] for i in ids]
        assert len(set(ids)) == 5
        assert sum(c['stars']==5 for c in selected) <= 1
        assert sum(c['stars']>=4 for c in selected) <= 2
        examples.append(dict(name=name,card_ids=ids,cards=[c['name'] for c in selected],
                             legal_rarity_and_unique_ids=True,ownership_required=True,benchmark_status='not benchmarked',notes=note))
    write_json('example-decks.json', examples)
    community = []
    for deck in read('collect-decks-sample.raw.json')['results']:
        dc = deck['cards']
        ids = [c['id'] for c in dc]
        valid = len(ids)==5 and len(set(ids))==5 and sum(c['stars']==5 for c in dc)<=1 and sum(c['stars']>=4 for c in dc)<=2
        community.append(dict(id=deck['id'], purpose=json.dumps(deck.get('purpose'),ensure_ascii=False),
                              card_ids=';'.join(map(str,ids)), cards=' | '.join(c['name'] for c in dc),
                              rating=deck.get('rating'), legal_rarity_and_unique_ids=valid,
                              url=f"https://ffxivcollect.com/triad/decks/{deck['id']}"))
    write_csv('community-decks-sample.csv',community)
    npc_cards = {c['id'] for n in npcs for c in n['rewards']}
    xivstats = {r['row_id']: r['fields'] for r in read('xivapi-card-stats.raw.json')['rows']}
    mismatches = []
    for c in cards:
        s = xivstats.get(c['id'])
        if s is None:
            mismatches.append({'id': c['id'], 'issue': 'not in XIVAPI snapshot'})
        elif any(c['stats']['numeric'][side] != s[side.title()] for side in ('top','right','bottom','left')):
            mismatches.append({'id': c['id'], 'issue': 'side mismatch'})
    summary = dict(cards=len(cards), cards_reported=read('collect-cards.raw.json')['count'],
                   npcs=len(npcs), npc_reward_cards=len(npc_cards), npc_deck_rows=len(deck_rows),
                   stars=dict(sorted(Counter(c['stars'] for c in cards).items())),
                   types=dict(Counter(c['type']['name'] for c in cards)),
                   highest_patch=max((c['patch'] for c in cards), key=lambda s: float(re.match(r'[0-9.]+', s).group())),
                   xivapi_versions={n:{k:read(n).get(k) for k in ('version','schema')} for n in URLS if n.startswith('xivapi')},
                   xivapi_stat_mismatches=mismatches)
    assert len({c['id'] for c in cards}) == len(cards)
    assert all(1 <= c['stars'] <= 5 and all(1 <= v <= 10 for v in c['stats']['numeric'].values()) for c in cards)
    assert npc_cards.issubset({c['id'] for c in cards})
    write_json('summary.json', summary)
    write_json('source-manifest.json', manifest)
    repos = []
    for path in sorted((ROOT/'sources').iterdir()):
        if not (path/'.git').exists():
            continue
        def git(*args):
            return subprocess.check_output(['git','-C',str(path),*args],text=True,encoding='utf-8').strip()
        repos.append(dict(name=path.name,url=git('remote','get-url','origin'),commit=git('rev-parse','HEAD'),
                          commit_date=git('show','-s','--format=%cI','HEAD'),subject=git('show','-s','--format=%s','HEAD')))
    write_json('repository-manifest.json', repos)
    print(json.dumps(summary, ensure_ascii=True, indent=2))

if __name__ == '__main__':
    main()
