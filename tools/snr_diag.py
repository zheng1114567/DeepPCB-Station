"""漏检根因诊断：配准残差 vs 灰度对比不足（SNR）

对 train1000 的零重叠漏检 GT，量化三件事：
  1. 配准后 |test - alignedTemp| 在 GT 框内的峰值 / 分位数——是否够阈（默认 30）
  2. 同一 GT 在「不配准 / 整数配准 / 更细瓦片」下峰值差多少——配准还剩多少可榨
  3. 背景噪声底（无 GT 铜区残差 p99）——阈值 30 相对噪声底的裕度

复刻 C# TileShiftRegistration.Align 的整数 XOR 分块配准（tiles=8, maxShift=8,
minGain=0.1），与HalconPcbDefectDetector 默认路径一致。
"""
import sys, os, glob, json
import numpy as np
from PIL import Image

W = H = 640
TILES = 8
MAXSHIFT = 8
MINGAIN = 0.1


def align_int(test, temp, tiles=TILES, max_shift=MAXSHIFT, min_gain=MINGAIN):
    """复刻 AlignCore(wantFrac=false)：二值 XOR 最小 + 增益闸 + 硬块采样。"""
    tb = (temp >= 128)
    aligned = np.zeros_like(temp)
    shifts = []
    for tr in range(tiles):
        for tc in range(tiles):
            x0, x1 = tc * W // tiles, (tc + 1) * W // tiles
            y0, y1 = tr * H // tiles, (tr + 1) * H // tiles
            sx0, sx1 = max(x0, max_shift), min(x1, W - max_shift)
            sy0, sy1 = max(y0, max_shift), min(y1, H - max_shift)
            if sx1 <= sx0 or sy1 <= sy0:
                shifts.append((tr, tc, 0, 0, 0, 0)); continue

            def xor(dx, dy):
                a = tb[sy0:sy1, sx0:sx1]
                b = tb[sy0 + dy:sy1 + dy, sx0 + dx:sx1 + dx]
                return int(np.count_nonzero(a != b))

            at0 = xor(0, 0)
            best, bdx, bdy = at0, 0, 0
            for dy in range(-max_shift, max_shift + 1):
                for dx in range(-max_shift, max_shift + 1):
                    if dx == 0 and dy == 0:
                        continue
                    v = xor(dx, dy)
                    if v < best or (v == best and abs(dx) + abs(dy) < abs(bdx) + abs(bdy)):
                        best, bdx, bdy = v, dx, dy
            if at0 == 0 or best > at0 * (1 - min_gain):
                best, bdx, bdy = at0, 0, 0
            shifts.append((tr, tc, bdx, bdy, at0, best))
            tx = np.clip(np.arange(x0, x1) + bdx, 0, W - 1)
            ty = np.clip(np.arange(y0, y1) + bdy, 0, H - 1)
            aligned[y0:y1, x0:x1] = temp[np.ix_(ty, tx)]
    return aligned, shifts


def read_gray(p):
    return np.asarray(Image.open(p).convert('L'), dtype=np.int16)


def box_stats(diff, gx1, gy1, gx2, gy2, pad=0):
    x1, y1 = max(0, gx1 - pad), max(0, gy1 - pad)
    x2, y2 = min(W, gx2 + pad), min(H, gy2 + pad)
    if x2 <= x1 or y2 <= y1:
        return None
    sub = np.abs(diff[y1:y2, x1:x2])
    return {
        'max': int(sub.max()),
        'p99': int(np.percentile(sub, 99)),
        'p95': int(np.percentile(sub, 95)),
        'mean': float(sub.mean()),
        'n_above30': int(np.count_nonzero(sub >= 30)),
        'n_above24': int(np.count_nonzero(sub >= 24)),
        'n_above20': int(np.count_nonzero(sub >= 20)),
        'n_above16': int(np.count_nonzero(sub >= 16)),
        'n_above12': int(np.count_nonzero(sub >= 12)),
    }


def main():
    samples = sys.argv[1] if len(sys.argv) > 1 else 'data/train1000'
    miss_dump = sys.argv[2] if len(sys.argv) > 2 else None
    limit = int(sys.argv[3]) if len(sys.argv) > 3 else 120

    rows = []
    if miss_dump and os.path.exists(miss_dump):
        import csv
        with open(miss_dump, encoding='utf-8') as f:
            for r in csv.DictReader(f, delimiter='\t'):
                rows.append(r)
    else:
        print('need miss_dump tsv'); return

    out = []
    for r in rows[:limit]:
        stem = r['stem']
        tp = f'{samples}/{stem}_test.jpg'
        mp = f'{samples}/{stem}_temp.jpg'
        if not (os.path.exists(tp) and os.path.exists(mp)):
            continue
        test = read_gray(tp); temp = read_gray(mp)
        gx1, gy1, gx2, gy2 = int(r['GX1']), int(r['GY1']), int(r['GX2']), int(r['GY2'])

        aligned, shifts = align_int(test, temp)
        d_align = test - aligned
        d_raw = test - temp  # 不配准

        # 背景噪声底：DeepPCB 极性为基材亮(255)/铜暗(0)，铜掩膜取暗区；量铜外的残差
        copper = temp <= 100
        bg = d_align[~copper]
        noise_p99 = int(np.percentile(np.abs(bg), 99))

        shifted = [s for s in shifts if s[2] != 0 or s[3] != 0]

        rec = {
            'stem': stem, 'type': r['type'],
            'gx1': gx1, 'gy1': gy1, 'gx2': gx2, 'gy2': gy2,
            'area': (gx2 - gx1) * (gy2 - gy1),
            'raw': box_stats(d_raw, gx1, gy1, gx2, gy2),
            'align': box_stats(d_align, gx1, gy1, gx2, gy2),
            'noise_p99': noise_p99,
            'n_tiles_shifted': len(shifted),
        }
        out.append(rec)

    json.dump(out, open('reports/snr_diag.json', 'w', encoding='utf-8'), ensure_ascii=False, indent=1)

    # 汇总
    ok = [o for o in out if o['align']]
    print('diagnosed %d miss GT' % len(ok))
    def q(field, sub, key):
        v = sorted(o[sub][key] for o in ok if o[sub])
        if not v: return 'n/a'
        return 'p10=%d p25=%d med=%d p75=%d p90=%d' % (
            v[len(v)//10], v[len(v)//4], v[len(v)//2], v[3*len(v)//4], v[9*len(v)//10])
    print('GT 内 |diff| max  (不配准):', q('max', 'raw', 'max'))
    print('GT 内 |diff| max  (8x8配准):', q('max', 'align', 'max'))
    print('GT 内 >=30 像素数(配准) :', q('max', 'align', 'n_above30'))
    print('GT 内 >=24 像素数(配准) :', q('max', 'align', 'n_above24'))
    print('GT 内 >=16 像素数(配准) :', q('max', 'align', 'n_above16'))
    nz = sorted(o['noise_p99'] for o in ok)
    print('背景残差 p99 噪声底      : p10=%d med=%d p90=%d' % (nz[len(nz)//10], nz[len(nz)//2], nz[9*len(nz)//10]))
    # 分档：GT 内峰值分布
    import collections
    buck = collections.Counter()
    for o in ok:
        m = o['align']['max']
        buck['<16' if m < 16 else '16-23' if m < 24 else '24-29' if m < 30 else '30-49' if m < 50 else '>=50'] += 1
    print('GT 内峰值分档:', dict(buck))
    # 有多少是「峰值够但像素数不够 minBlobArea=22」
    enough_peak = [o for o in ok if o['align']['max'] >= 30]
    print('峰值>=30 的 GT: %d / %d' % (len(enough_peak), len(ok)))
    if enough_peak:
        c = collections.Counter('n30>=22' if o['align']['n_above30'] >= 22 else 'n30<22' for o in enough_peak)
        print('  其中 >=30 像素数>=22:', dict(c))
    # 配准带来的增益
    gain = [o['align']['max'] - o['raw']['max'] for o in ok if o['raw']]
    gain.sort()
    print('配准对GT 内峰值的增益 : p10=%d med=%d p90=%d (正=配准把峰值抬高了)' % (
        gain[len(gain)//10], gain[len(gain)//2], gain[9*len(gain)//10]))


if __name__ == '__main__':
    main()