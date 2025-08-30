---
applyTo: "**/*.cs"
title: "C# Coding Instructions for STYLY-NetSync"
description: "Unity C# coding guidelines and critical rules for the STYLY-NetSync project"
---

# Instruction
* Always write comments and documentation in English, even if the surrounding chat or code is in another language.

# Unity C# Coding Rules (IMPORTANT)
- Never use null propagation (`?.` / `??`) with `UnityEngine.Object` (e.g., `Transform`, `GameObject`, `Component`).
  - ❌ **Wrong:** `return transform?.transform;`
  - ✅ **Correct:** `return transform != null ? transform : null;`
- If Claude produced any `?.` on Unity objects, immediately refactor to explicit null checks.
- Do **not** call any `UnityEngine` APIs from background threads — all Unity object access must run on the main Unity thread.
- Don't generate .meta file for Unity C# script. Meta files will be automatically generated once Unity editor is opened.
