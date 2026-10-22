"""门控验证：规则置信度高的那批，规则分型真的准吗？还是虚假自信？

若高置信段「规则准确率 ≥ DL 准确率」，则分级触发只省算力、不掉分；
反之门控会把 DL 的增益吃回去，阈值必须往下压。

数据：reports/features_official/*.tsv（规则 PredType + conf + gtType）
      reports/type_clf/override_official.json（DL 六分类标签）
"""
import csv, glob, json, os
import numpy as np

TYPE_ID = {'OPEN': 1, 'SHORT': 2, 'MOUSEBITE': 3, 'SPUR': 4, 'COPPER': 5, 'PINHOLE': 6}
NAME = {1: 'open', 2: 'short', 3: 'mousebite', 4: 'spur', 5: 'copper', 6: 'pin-hole'}


def main():
    tsv = sorted(glob.glob('reports/features_official/*.tsv'))[-1]
    print('tsv', tsv)
    with open('reports/type_clf/override_official.json', encoding='utf-8') as f:
        dl_map = json.load(f)

    rows = []
    with open(tsv, encoding='utf-8') as f:
        for r in csv.DictReader(f, delimiter='\t'):
            gt = int(r['gtType']) if r['gtType'] else 0
            if gt == 0:
                continue
            x, y, w, h = int(r['x']), int(r['y']), int(r['w']), int(r['h'])
            key = f"{r['pair']}|{x}|{y}|{x + w - 1}|{y + h - 1}"
            dl = dl_map.get(key)
            if dl is None:
                continue
            rule_id = TYPE_ID.get(r['pred'].upper(), 0)
            rows.append({
                'conf': float(r['conf']),
                'rule_ok': int(rule_id == gt),
                'dl_ok': int(int(dl) == gt),
                'gt': gt,
                'rule': rule_id,
                'dl': int(dl),
            })
    n = len(rows)
    conf = np.array([r['conf'] for r in rows])
    rok = np.array([r['rule_ok'] for r in rows])
    dok = np.array([r['dl_ok'] for r in rows])
    print(f'samples {n}  rule acc {rok.mean():.4f}  dl acc {dok.mean():.4f}')

    bins = [(0, 0.5), (0.5, 0.7), (0.7, 0.8), (0.8, 0.9), (0.9, 0.95), (0.95, 0.99), (0.99, 1.001)]
    print('\n--- 按规则置信度分箱 ---')
    print(f"{'区间':>12} {'n':>5} {'规则acc':>8} {'DL acc':>8} {'差值':>7}")
    table = []
    for lo, hi in bins:
        m = (conf >= lo) & (conf < hi)
        if m.sum() == 0:
            continue
        a, b = rok[m].mean(), dok[m].mean()
        table.append({'lo': lo, 'hi': hi, 'n': int(m.sum()),
                      'rule_acc': round(float(a), 4), 'dl_acc': round(float(b), 4),
                      'delta': round(float(a - b), 4)})
        print(f"{lo:.2f}-{hi:.2f} {int(m.sum()):>5} {a:>8.4f} {b:>8.4f} {a-b:>+7.4f}")

    print('\n--- 门控模拟：conf >= t 用规则，其余调 DL ---')
    print(f"{'t':>6} {'DL调用率':>9} {'总acc':>8} {'vs全DL':>8}")
    gate = []
    for t in [0.0, 0.5, 0.7, 0.8, 0.85, 0.9, 0.95, 0.99]:
        use_rule = conf >= t
        acc = np.where(use_rule, rok, dok).mean()
        call = float((~use_rule).mean())
        gate.append({'t': t, 'dl_call': round(call, 4), 'acc': round(float(acc), 4),
                     'vs_all_dl': round(float(acc - dok.mean()), 4)})
        print(f"{t:>6.2f} {call:>9.1%} {acc:>8.4f} {acc-dok.mean():>+8.4f}")

    out = {'n': n, 'rule_acc': round(float(rok.mean()), 4), 'dl_acc': round(float(dok.mean()), 4),
           'bins': table, 'gate': gate}
    os.makedirs('reports/type_clf', exist_ok=True)
    with open('reports/type_clf/gating.json', 'w', encoding='utf-8') as f:
        json.dump(out, f, ensure_ascii=False, indent=2)
    print('\nsaved reports/type_clf/gating.json')


if __name__ == '__main__':
    main()
