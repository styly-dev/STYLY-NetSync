# BinaryVariable Sample

Demonstrates the `byte[]` NetworkVariable feature by syncing binary data between clients.

## Scene Setup

Open `BinaryVariable.unity`. The scene contains:

- **NetSyncManager** — network connection (configure server address before playing)
- **Primitive_0_Cube / Primitive_1_Sphere / Primitive_2_Cylinder** — sync targets
- **Canvas** — UI for screenshot sync (RawImage + Capture Button)
- **EventSystem** — required for UI interaction

## Samples

### 1. Naive Primitive Sync (string-based)

**Script:** `NaivePrimitiveSync.cs` (disabled by default)

Syncs each primitive's size and color using individual `string` NetworkVariables. This requires **6 separate variables**:

| Variable | Type | Example |
|---|---|---|
| `naive_size_0` | string (float) | `"1.5"` |
| `naive_color_0` | string (hex RGBA) | `"FF0000FF"` |
| `naive_size_1` | string (float) | `"2.0"` |
| ... | ... | ... |

**Usage:**
1. Enable the `NaivePrimitiveSync` GameObject
2. Enter Play Mode and connect to the server
3. Edit size/color values in the Inspector
4. Click **Send All** button in the Inspector

### 2. Binary Primitive Sync (byte[]-based)

**Script:** `BinaryPrimitiveSync.cs` + `PrimitiveParams.cs`

Syncs all three primitives' parameters via a **single `byte[]` NetworkVariable**. A `PrimitiveParams` ScriptableObject is serialized to JSON bytes and sent as one variable.

| Variable | Type | Content |
|---|---|---|
| `binary_primitives` | byte[] | JSON of all 3 entries (size + color) |

**Usage:**
1. Enter Play Mode and connect to the server
2. Edit the `DefaultPrimitiveParams` asset in the Inspector (size/color per primitive)
3. Click **Send** button in the Inspector

**Advantages over the naive approach:**
- 1 variable instead of 6
- Adding new parameters requires only a field addition to `PrimitiveParams` — no new variables needed
- All parameters update atomically in a single message

### 3. Screenshot Sync

**Script:** `ScreenshotSync.cs`

Captures a screenshot, resizes it to fit within the 64KB NetworkVariable limit, and syncs it as JPEG bytes. Other clients display the received image on a RawImage.

| Variable | Type | Content |
|---|---|---|
| `screenshot` | byte[] | JPEG image data |

**Usage:**
1. Enter Play Mode and connect to the server
2. Click the **Capture Screenshot** button in the Game view
3. The screenshot appears on the RawImage for all connected clients

**Size constraints:**
- Max resolution: 320px (longest side, aspect ratio preserved)
- JPEG quality: 50
- Max payload: 64KB (`MAX_VAR_VALUE_LENGTH`)

## File Structure

```
BinaryVariable/
├── BinaryVariable.unity           # Sample scene
├── README.md                      # This file
├── PrimitiveParams.cs             # ScriptableObject with Serialize/Deserialize
├── DefaultPrimitiveParams.asset   # Default parameter values
├── NaivePrimitiveSync.cs          # String-based sync (6 variables)
├── BinaryPrimitiveSync.cs         # byte[]-based sync (1 variable)
├── ScreenshotSync.cs              # Screenshot capture & sync
└── Editor/
    ├── NaivePrimitiveSyncEditor.cs   # Inspector with Send All button
    └── BinaryPrimitiveSyncEditor.cs  # Inspector with Send button
```
