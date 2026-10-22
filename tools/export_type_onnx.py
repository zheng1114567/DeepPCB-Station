"""导出分型分类器为 ONNX（opset 11，兼容 OnnxRuntime 1.16 / net48）。

输入：1x3x96x96 float32（RGB 通道顺序：R=实测 / G=模板 / B=|diff|*2），值域 0..1
输出：1x6 logits（顺序 open/short/mousebite/spur/copper/pin-hole）
"""
import os
import numpy as np
import torch
import torch.nn as nn
from torchvision import models

CLASSES = ['open', 'short', 'mousebite', 'spur', 'copper', 'pin-hole']
CKPT = 'reports/type_clf/type_clf.pt'
OUT = 'models/type_clf.onnx'


def main():
    ck = torch.load(CKPT, map_location='cpu', weights_only=False)
    m = models.resnet18(weights=None)
    m.fc = nn.Linear(m.fc.in_features, len(CLASSES))
    m.load_state_dict(ck['state_dict'])
    m.eval()

    dummy = torch.randn(1, 3, 96, 96)
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    torch.onnx.export(
        m, dummy, OUT,
        input_names=['input'], output_names=['logits'],
        opset_version=11, do_constant_folding=True,
    )
    print('exported', OUT, os.path.getsize(OUT) // 1024, 'KB')

    # 自检：ONNX 与 PyTorch 结果一致
    import onnxruntime as ort
    sess = ort.InferenceSession(OUT, providers=['CPUExecutionProvider'])
    x = np.random.rand(1, 3, 96, 96).astype(np.float32)
    o_onnx = sess.run(None, {'input': x})[0]
    with torch.no_grad():
        o_torch = m(torch.from_numpy(x)).numpy()
    print('max|diff|', float(np.abs(o_onnx - o_torch).max()))
    print('argmax match', (o_onnx.argmax(1) == o_torch.argmax(1)).all())


if __name__ == '__main__':
    main()
