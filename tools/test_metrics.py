import numpy as np
from PIL import Image
import os

def analyze_frame(path):
    if not os.path.exists(path):
        return None
    im = Image.open(path).convert("RGB")
    arr = np.array(im, dtype=np.float32)
    h, w, c = arr.shape
    
    gray = np.mean(arr, axis=2)
    active = gray > 10.0
    if np.any(active):
        y_indices, x_indices = np.where(active)
        coverage_y = float(y_indices.max() - y_indices.min() + 1) / h
        active_ratio = float(np.sum(active)) / (w * h)
    else:
        coverage_y, active_ratio = 0.0, 0.0
        
    small = np.array(im.resize((w//4, h//4)))
    flat_colors = small.reshape(-1, 3)
    hist, _ = np.histogramdd(flat_colors, bins=(16, 16, 16))
    prob = hist / (hist.sum() + 1e-9)
    prob = prob[prob > 0]
    entropy = -float(np.sum(prob * np.log2(prob)))
    
    diff_x = np.abs(gray[:, 1:] - gray[:, :-1])
    diff_y = np.abs(gray[1:, :] - gray[:-1, :])
    
    grid_cols = [x for x in range(63, w-1, 64)]
    non_grid_cols = [x for x in range(w-1) if (x % 64 != 63) and (x % 64 != 0)]
    
    mean_grid = float(np.mean(diff_x[:, grid_cols])) if grid_cols else 0.0
    mean_non_grid = float(np.mean(diff_x[:, non_grid_cols])) if non_grid_cols else 1.0
    tiling_ratio = mean_grid / (mean_non_grid + 1e-4)

    # 16-pixel vertical blockiness (block linear tile height)
    grid_rows = [y for y in range(15, h-1, 16)]
    non_grid_rows = [y for y in range(h-1) if (y % 16 != 15) and (y % 16 != 0)]
    mean_grid_y = float(np.mean(diff_y[grid_rows, :])) if grid_rows else 0.0
    mean_non_grid_y = float(np.mean(diff_y[non_grid_rows, :])) if non_grid_rows else 1.0
    tiling_ratio_y = mean_grid_y / (mean_non_grid_y + 1e-4)

    return {
        "file": os.path.basename(path),
        "covY": round(coverage_y, 3),
        "active": round(active_ratio, 3),
        "entropy": round(entropy, 2),
        "tiling_ratio_x": round(tiling_ratio, 2),
        "tiling_ratio_y": round(tiling_ratio_y, 2),
        "mean_grid": round(mean_grid, 3),
        "mean_non_grid": round(mean_non_grid, 3)
    }

import sys, glob

frames = sys.argv[1:] if len(sys.argv) > 1 else sorted(glob.glob("captures/*.png"))
if not frames:
    frames = ["captures/latest_frame.png"]

for f in frames:
    r = analyze_frame(f)
    if r:
        fn = r['file']
        cov = r['covY']
        act = r['active']
        ent = r['entropy']
        tx = r['tiling_ratio_x']
        ty = r['tiling_ratio_y']
        mg = r['mean_grid']
        mng = r['mean_non_grid']
        print(f"{fn:16s} | covY: {cov:0.3f} | act: {act:0.3f} | ent: {ent:0.2f} | tileX: {tx:0.2f} | tileY: {ty:0.2f} (gridX={mg:0.2f}, nonGridX={mng:0.2f})")
