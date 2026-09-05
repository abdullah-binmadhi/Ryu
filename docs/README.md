# Ryu Documentation Index

This directory contains architectural specifications, implementation references, execution roadmaps, and diagnostic guides for **Ryu: Pure Native Apple Silicon Nintendo Switch Emulation Core**.

---

## 🏛️ Architectural Hierarchy & Source of Truth

To ensure alignment across all subsystems and engineering contributors, our documentation follows a strict single-source-of-truth hierarchy:

1. **[Native Metal Execution Roadmap](native-metal-execution-roadmap.md)** (`docs/native-metal-execution-roadmap.md`)
   - **Latest Operational Execution Roadmap.** Turns architectural requirements into ordered execution phases (Phases 0 through 5).
   - Tracks current live gating milestones: baseline checkpoint, M4 compute verification, state adapters (sampler deduplication, swizzles, depth bias), virtual input progression, M3/M4 presentation synchronization, and City Ruins 30 FPS performance.

2. **[Native Metal 4 Engine — Executable Execution Spec](native-engine-plan.md)** (`docs/native-engine-plan.md`)
   - **Architectural Source of Truth.** Details the design, compiler IR interface (`StructuredProgramInfo`), phase-gates, and the "Adapter, Not Clone" philosophy for the native Metal 4 pipeline.

3. **[Complete Context & Handoff for the Native Metal Backend](full-metal.md)** (`docs/full-metal.md`)
   - **Metal 4 Implementation & Binding Reference.** Detailed technical specifications for `MTL4ArgumentTable`, GPU addresses, resource IDs, multi-threaded command encoding across P-cores, and zero-copy UMA memory.

4. **[Architectural Specification: Native Apple Silicon Headless Engine](../README2.md)** (`README2.md`)
   - High-level system architecture: Astris-style CLI execution, Apple Hypervisor (`AppleHv`), Mach VM zero-copy UMA, Darwin Mach QoS thread scheduling, CVDisplayLink frame delivery, and vectorized audio DSP.

5. **[Architectural Evaluation & Insights](conversations.md)** (`docs/conversations.md`)
   - Historical evaluation and deep dive on Metal 4 vs Metal 3, Maxwell vs Apple Silicon TBDR, zero-cost MoltenVK adaptation techniques, and avoiding engine-level traps.

6. **[Metal Color & Diagnostics Investigation](metal-magenta-investigation.md)** (`docs/metal-magenta-investigation.md`)
   - Historical investigation and resolution notes on color format mismatches, clear-color pipelines, and attachment validation.

---

## 🛠️ Developer & Workflow Guides

- **[C# Coding Style](coding-guidelines/coding-style.md)**: Coding conventions and style rules for Ryu.
- **[Pull Request & Workflow Guide](workflow/pr-guide.md)**: PR submission, review checklists, and git discipline.
- **[Comprehensive User Guide](../USER_GUIDE.md)**: End-user launch profiles, controller setup, and firmware installation.
- **[Root Readme](../README.md)**: Quick start, build commands, and feature overview.
