#!/usr/bin/env python3
"""
tools/visual_alignment_indicator.py

Multi-Method Alignment Indicator for Ryujinx Metal Graphics Backend.
Combines:
  1. Performance & Pacing Alignment (FPS, Frametime, GPUwait, FIFO)
  2. Computer Vision & Geometric Quality Metrics (Coverage, Tiling Discontinuity, Entropy)
  3. Semantic Art & Scene Recognition (Loading, Splash Logos, 3D Gameplay, Distorted Slabs)
Produces a unified health indicator score and actionable diagnostic report.
"""

import sys
import os
import re
import glob
import math
import numpy as np
from PIL import Image

def parse_performance_metrics(log_path):
    """Parses latest FPS, frametime, GPU wait, FIFO, and thermal state from Ryujinx log."""
    metrics = {
        "fps": 0.0,
        "frametime_ms": 0.0,
        "gpu_wait_ms": 0.0,
        "fifo_pct": 0.0,
        "cpu_pct": 0.0,
        "status": "UNKNOWN"
    }
    if not os.path.exists(log_path):
        return metrics

    try:
        import subprocess
        # Efficiently grab the last few FPS lines from the end of the log
        res = subprocess.run(
            ["grep", "-F", "[Ryu] FPS:", log_path],
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            errors="replace"
        )
        lines = res.stdout.strip().splitlines()
        if lines:
            fps_re = re.compile(r"FPS:\s*([\d\.]+)\s*\(([\d\.]+)ms\).*?FIFO:\s*([\d\.]+)%.*?GPUwait:\s*([\d\.]+)ms/s.*?CPU:\s*([\d\.]+)%")
            for line in reversed(lines[-10:]):
                m = fps_re.search(line)
                if m:
                    metrics["fps"] = float(m.group(1))
                    metrics["frametime_ms"] = float(m.group(2))
                    metrics["fifo_pct"] = float(m.group(3))
                    metrics["gpu_wait_ms"] = float(m.group(4))
                    metrics["cpu_pct"] = float(m.group(5))
                    metrics["status"] = "OK"
                    break
    except Exception as e:
        metrics["error"] = str(e)
        
    return metrics

def calculate_performance_score(perf):
    """Calculates 0-100 score for performance alignment."""
    if perf["status"] != "OK":
        return 0.0, "No performance data available"
    
    fps = perf["fps"]
    ft = perf["frametime_ms"]
    gpu_wait = perf["gpu_wait_ms"]
    
    # NieR runs at 30 FPS target (or 60 FPS in menus/cutscenes)
    # Score 100 if FPS >= 28.5 (for 30fps lock)
    if fps >= 28.5:
        fps_score = 100.0
    elif fps >= 20.0:
        fps_score = 60.0 + (fps - 20.0) * 4.0
    else:
        fps_score = max(0.0, fps * 3.0)
        
    # Frametime stability: penalty for > 36ms or high jitter
    ft_score = 100.0 if (30.0 <= ft <= 36.0) else max(20.0, 100.0 - abs(ft - 33.3) * 5.0)
    
    # GPU wait penalty
    wait_score = max(0.0, 100.0 - gpu_wait * 5.0)
    
    total = 0.50 * fps_score + 0.30 * ft_score + 0.20 * wait_score
    return round(total, 1), f"FPS: {fps:.1f} ({ft:.1f}ms), GPUwait: {gpu_wait:.0f}ms/s"

def analyze_image_quality(image_path):
    """Computes geometric coverage, tiling discontinuity, and entropy metrics."""
    if not os.path.exists(image_path):
        return None

    im = Image.open(image_path).convert("RGB")
    arr = np.array(im, dtype=np.float32)
    h, w, _ = arr.shape
    gray = np.mean(arr, axis=2)

    # 1. Screen Coverage (detect y <= 319 squashing/truncation)
    active_mask = gray > 12.0
    if np.any(active_mask):
        y_indices, x_indices = np.where(active_mask)
        y_span = float(y_indices.max() - y_indices.min() + 1)
        x_span = float(x_indices.max() - x_indices.min() + 1)
        cov_y = y_span / h
        cov_x = x_span / w
        active_ratio = float(np.sum(active_mask)) / (w * h)
        y_min, y_max = int(y_indices.min()), int(y_indices.max())
    else:
        cov_y, cov_x, active_ratio = 0.0, 0.0, 0.0
        y_min, y_max = 0, 0

    # 2. Color Entropy (3D RGB space downsampled)
    small = np.array(im.resize((w // 4, h // 4)))
    flat_colors = small.reshape(-1, 3)
    hist, _ = np.histogramdd(flat_colors, bins=(16, 16, 16))
    prob = hist / (hist.sum() + 1e-9)
    prob = prob[prob > 0]
    entropy = -float(np.sum(prob * np.log2(prob)))

    # Unique colors count
    unique_colors = len(np.unique(flat_colors, axis=0))

    # 3. Maxwell 64-Byte GOB / Block-Linear Tiling Discontinuity Ratio
    diff_x = np.abs(gray[:, 1:] - gray[:, :-1])
    diff_y = np.abs(gray[1:, :] - gray[:-1, :])

    # Check horizontal jump at 64, 128, 256, 512 pixel boundaries
    tiling_ratios = {}
    for step in [64, 128, 256, 512]:
        grid_cols = [x for x in range(step - 1, w - 1, step)]
        non_grid_cols = [x for x in range(w - 1) if (x % step != step - 1) and (x % step != 0)]
        mg = float(np.mean(diff_x[:, grid_cols])) if grid_cols else 0.0
        mng = float(np.mean(diff_x[:, non_grid_cols])) if non_grid_cols else 1.0
        tiling_ratios[step] = mg / (mng + 1e-4)

    # 4. Vertical block jump (16-px tile heights)
    grid_rows_16 = [y for y in range(15, h - 1, 16)]
    non_grid_rows_16 = [y for y in range(h - 1) if (y % 16 != 15) and (y % 16 != 0)]
    mg_y = float(np.mean(diff_y[grid_rows_16, :])) if grid_rows_16 else 0.0
    mng_y = float(np.mean(diff_y[non_grid_rows_16, :])) if non_grid_rows_16 else 1.0
    tile_ratio_y = mg_y / (mng_y + 1e-4)

    # 5. Checkerboard / Dither Pattern Detection
    # Difference between even and odd pixels
    dither_x = np.abs(gray[:, 1::2] - gray[:, 0::2])
    dither_y = np.abs(gray[1::2, :] - gray[0::2, :])
    checkerboard_score = float(np.mean(dither_x) + np.mean(dither_y))

    # Detect disjoint horizontal/vertical slabs:
    # A slab has sharp box edges and uniform interior
    dx_sharp = np.sum(diff_x > 80, axis=0)
    dy_sharp = np.sum(diff_y > 80, axis=1)
    sharp_col_count = int(np.sum(dx_sharp > 120))
    sharp_row_count = int(np.sum(dy_sharp > 120))

    is_slab_distortion = False
    if (sharp_col_count >= 4 and sharp_row_count >= 4) and unique_colors < 150:
        is_slab_distortion = True

    return {
        "width": w,
        "height": h,
        "cov_y": round(cov_y, 3),
        "cov_x": round(cov_x, 3),
        "y_min": y_min,
        "y_max": y_max,
        "active_ratio": round(active_ratio, 3),
        "entropy": round(entropy, 2),
        "unique_colors": unique_colors,
        "tiling_ratios": tiling_ratios,
        "tile_ratio_y": round(tile_ratio_y, 2),
        "checkerboard_score": round(checkerboard_score, 2),
        "sharp_cols": sharp_col_count,
        "sharp_rows": sharp_row_count,
        "is_slab_distortion": is_slab_distortion
    }

def calculate_quality_score(metrics):
    """Calculates 0-100 score for image quality and absence of graphics distortion."""
    if metrics is None:
        return 0.0, "Image not available"

    score = 100.0
    reasons = []

    # 1. Truncated vertical coverage penalty (e.g. y <= 319 cutoff)
    if metrics["cov_y"] < 0.35 and metrics["active_ratio"] > 0.05:
        penalty = 50.0
        score -= penalty
        reasons.append(f"Vertical truncation (cov_y={metrics['cov_y']:.2f}, y_max={metrics['y_max']})")

    # 2. Slab distortion penalty
    if metrics["is_slab_distortion"]:
        penalty = 60.0
        score -= penalty
        reasons.append(f"Severe rectangular slab distortion detected ({metrics['sharp_cols']} col / {metrics['sharp_rows']} row discontinuities)")

    # 3. Checkerboard / stipple corruption
    if metrics["checkerboard_score"] > 80.0:
        penalty = 40.0
        score -= penalty
        reasons.append(f"High-frequency checkerboard / stipple noise ({metrics['checkerboard_score']:.1f})")

    # 4. Tiling ratio anomaly (ratio > 2.0 at 64px or 128px)
    max_tiling = max(metrics["tiling_ratios"].values())
    if max_tiling > 2.5:
        penalty = 25.0
        score -= penalty
        reasons.append(f"Tiling grid discontinuity ratio ({max_tiling:.2f})")

    score = max(0.0, min(100.0, score))
    summary = "; ".join(reasons) if reasons else "Clean rendering, no geometric distortion"
    return round(score, 1), summary

def recognize_art_and_scene(metrics, image_path):
    """Recognizes game scene and art integrity."""
    if metrics is None:
        return 0.0, "UNKNOWN", "Missing frame"

    cov_y = metrics["cov_y"]
    act = metrics["active_ratio"]
    ent = metrics["entropy"]
    uc = metrics["unique_colors"]
    slabs = metrics["is_slab_distortion"]

    # Pattern A: Circular loading spinner
    # Low active ratio (< 0.05), centered, low entropy, circular ring
    if 0.003 <= act <= 0.04 and ent < 1.0 and not slabs:
        scene = "LOADING_SPINNER"
        integrity_score = 90.0
        desc = "Authentic NieR particle loading/saving ring spinner"
    # Pattern B: Boot Text Bar (Loading system data / Auto-save notice)
    elif 0.15 <= cov_y <= 0.25 and 0.10 <= act <= 0.25 and ent < 1.5 and not slabs:
        scene = "BOOT_TEXT_BANNER"
        integrity_score = 85.0
        desc = "Authentic NieR system message banner with geometric arc accents"
    # Pattern C: Distorted Slabs (Square Enix / Platinum logos broken into blocks)
    elif slabs:
        scene = "DISTORTED_SLABS"
        integrity_score = 15.0
        desc = "Distorted company logo splash broken into un-deswizzled Maxwell rectangular slabs"
    # Pattern D: Smooth Vignette Gradient
    elif ent >= 1.5 and uc > 50 and not slabs and cov_y > 0.90:
        scene = "POST_PROCESS_VIGNETTE"
        integrity_score = 80.0
        desc = "Smooth post-process vignette background shading"
    # Pattern E: Full 3D World / In-Game Scene
    elif ent >= 3.5 and uc > 300 and not slabs and cov_y > 0.90:
        scene = "IN_GAME_3D"
        integrity_score = 95.0
        desc = "Rich 3D gameplay scene with diverse color palette and continuous geometry"
    # Pattern F: Uniform / Blank Screen
    elif act < 0.002:
        scene = "BLACK_SCREEN"
        integrity_score = 40.0
        desc = "Blank black screen (transitional frame or draw inactive)"
    else:
        scene = "UNCLASSIFIED"
        integrity_score = 50.0
        desc = f"Scene with covY={cov_y:.2f}, ent={ent:.1f}, uniqueColors={uc}"

    return integrity_score, scene, desc

def evaluate_alignment(image_path, log_path):
    """Evaluates multi-method alignment across Performance, Quality, and Art."""
    perf = parse_performance_metrics(log_path)
    perf_score, perf_desc = calculate_performance_score(perf)

    q_metrics = analyze_image_quality(image_path)
    q_score, q_desc = calculate_quality_score(q_metrics)

    art_score, scene_tag, art_desc = recognize_art_and_scene(q_metrics, image_path)

    # Weighted alignment indicator:
    # 25% Performance (FPS/pacing), 40% Visual Quality (distortion-free), 35% Art/Scene integrity
    composite = 0.25 * perf_score + 0.40 * q_score + 0.35 * art_score
    composite = round(composite, 1)

    if composite >= 80.0 and q_score >= 70.0:
        verdict = "CLEAR_AS_INTENDED"
        verdict_badge = "🟢 ALIGNED (CLEAR AS INTENDED)"
    elif composite >= 50.0:
        verdict = "PARTIALLY_DEGRADED"
        verdict_badge = "🟡 PARTIALLY DEGRADED"
    else:
        verdict = "SEVERELY_DISTORTED"
        verdict_badge = "🔴 SEVERELY DISTORTED"

    report = {
        "verdict": verdict,
        "verdict_badge": verdict_badge,
        "composite_score": composite,
        "methods": {
            "method_1_performance": {
                "score": perf_score,
                "fps": perf["fps"],
                "frametime_ms": perf["frametime_ms"],
                "gpu_wait_ms": perf["gpu_wait_ms"],
                "summary": perf_desc
            },
            "method_2_image_quality": {
                "score": q_score,
                "cov_y": q_metrics["cov_y"] if q_metrics else 0,
                "is_slab_distortion": q_metrics["is_slab_distortion"] if q_metrics else False,
                "summary": q_desc
            },
            "method_3_art_recognition": {
                "score": art_score,
                "scene": scene_tag,
                "summary": art_desc
            }
        }
    }
    return report

def main():
    import argparse
    import time

    parser = argparse.ArgumentParser(description="Multi-Method Graphics Alignment Indicator for Ryujinx Metal")
    parser.add_argument("frame", nargs="?", default="captures/latest_frame.png", help="Path to PNG frame to analyze")
    parser.add_argument("--log", default="ryu.log", help="Path to Ryujinx execution log")
    parser.add_argument("--watch", action="store_true", help="Continuously monitor latest_frame.png as the game renders live")
    parser.add_argument("--interval", type=float, default=2.0, help="Polling interval in seconds for --watch mode")
    parser.add_argument("--json", action="store_true", help="Output results in JSON format")

    args = parser.parse_args()

    def print_report(rep, target_img):
        if args.json:
            import json
            print(json.dumps(rep, indent=2))
            return

        print("=" * 72)
        print(f" MULTI-METHOD GRAPHICS ALIGNMENT INDICATOR")
        print("=" * 72)
        print(f" Target Frame:  {os.path.basename(target_img)}")
        print(f" Status:        {rep['verdict_badge']}")
        print(f" Overall Score: {rep['composite_score']}/100")
        print("-" * 72)
        m1 = rep["methods"]["method_1_performance"]
        m2 = rep["methods"]["method_2_image_quality"]
        m3 = rep["methods"]["method_3_art_recognition"]
        print(f" [Method 1: Performance] Score: {m1['score']}/100")
        print(f"   Details: {m1['summary']}")
        print(f" [Method 2: Quality & Discontinuity] Score: {m2['score']}/100")
        print(f"   Details: {m2['summary']}")
        print(f" [Method 3: Art & Scene Recognition] Score: {m3['score']}/100")
        print(f"   Scene:   {m3['scene']}")
        print(f"   Details: {m3['summary']}")
        print("=" * 72)

    if not args.watch:
        report = evaluate_alignment(args.frame, args.log)
        print_report(report, args.frame)
        return

    print(f"[*] Starting live visual alignment monitoring on: {args.frame}")
    print(f"[*] Ryujinx log source: {args.log}")
    last_mtime = 0
    try:
        while True:
            if os.path.exists(args.frame):
                mtime = os.path.getmtime(args.frame)
                if mtime != last_mtime:
                    last_mtime = mtime
                    report = evaluate_alignment(args.frame, args.log)
                    print_report(report, args.frame)
            time.sleep(args.interval)
    except KeyboardInterrupt:
        print("\n[*] Monitoring stopped.")

if __name__ == "__main__":
    main()
