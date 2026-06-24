# Typography & Text Styles Reference

Use this reference to set up font families, weights, sizes, line heights, select
typography tokens, and evaluate text accessibility.

---

## 1. Typeface & Weights

DADS adopts clean, high-legibility sans-serif fonts to establish a neutral and
clear government communication tone.

### A. Font Families

- **Standard Text**: `Noto Sans JP` (SIL Open Font License 1.1).
- **Tabulated / Code / Equal-width Text**: `Noto Sans Mono`.
- **CSS Specification**:
  ```css
  body {
    font-family:
      'Noto Sans JP',
      -apple-system,
      BlinkMacSystemFont,
      sans-serif;
  }

  code, pre, kbd, samp {
    font-family: 'Noto Sans Mono', monospace;
  }
  ```

### B. Font Weights

DADS strictly restricts font weights to two primary levels to prevent layout
complexity:

- **Normal (N)**: `font-weight: 400` (Maps to regular or normal CSS weights).
- **Bold (B)**: `font-weight: 700` (Maps to bold CSS weights).

---

## 2. Text Sizes & Line Heights

### A. Font Size Standards

- **Display Size (48px - 64px)**: High-impact hero segments, front-page
  headings.
- **Content Size (16px - 45px)**: Standard headings and body text.
- **Base Body Text**: **16px** is the standard minimum size for readable
  paragraph copy.
- **Auxiliary Size (14px)**: Permitted *only* in space-constrained zones
  (footers, metadata, breadcrumb tags).
- **Prohibited Size (<14px)**: Under no circumstances should text fall below
  14px in web interfaces.

### B. Line Height (`line-height`) Standards

CSS implementations must use unitless decimal multipliers (e.g. `1.5` instead of
`150%`).

- **1.0 (`100%`)**: Single-line UI elements (e.g., buttons, chips) to allow
  accurate vertical padding control.
- **1.2 - 1.3 (`120% - 130%`)**: Information-dense tables, lists, and
  administration dashboard fields.
- **1.4 (`140%`)**: Large text headings to keep visual hierarchy tight.
- **1.5 (`150%`)**: Absolute minimum line-height for standard body text block
  reading.
- **1.6 - 1.75 (`160% - 175%`)**: Standard body paragraph line-heights. Higher
  values are recommended to reduce cognitive strain for users with reading
  disabilities.

---

## 3. Typography Token Directory

Tokens follow the format: `[Category]-[Size][Weight]-[LineHeight]` (e.g.,
`Std-16N-170` = Standard category, 16px size, Normal weight, 1.7 line-height).

### A. Display (Dsp)

Used for visual impact. Line-height is always `1.4` (140%), letter-spacing
is `0`.

| Token Name | Size (px) | Weight | Line-Height | Letter-Spacing |
| --- | --- | --- | --- | --- |
| **Dsp-64B-140** / **Dsp-64N-140** | 64 | Bold / Normal | 1.4 | 0 |
| **Dsp-57B-140** / **Dsp-57N-140** | 57 | Bold / Normal | 1.4 | 0 |
| **Dsp-48B-140** / **Dsp-48N-140** | 48 | Bold / Normal | 1.4 | 0 |

### B. Standard (Std)

Used for standard headings and paragraph bodies. Font size >= 16px.
Letter-spacing varies from 0 to 2% (`0.02em`).

| Token Name (Bold / Normal) | Size (px) | Weight | Line-Height | Letter-Spacing |
| --- | --- | --- | --- | --- |
| **Std-45B-140** / **Std-45N-140** | 45 | Bold / Normal | 1.4 | 0 |
| **Std-36B-140** / **Std-36N-140** | 36 | Bold / Normal | 1.4 | 0.01em (1%) |
| **Std-32B-150** / **Std-32N-150** | 32 | Bold / Normal | 1.5 | 0.01em (1%) |
| **Std-28B-150** / **Std-28N-150** | 28 | Bold / Normal | 1.5 | 0.01em (1%) |
| **Std-26B-150** / **Std-26N-150** | 26 | Bold / Normal | 1.5 | 0.02em (2%) |
| **Std-24B-150** / **Std-24N-150** | 24 | Bold / Normal | 1.5 | 0.02em (2%) |
| **Std-22B-150** / **Std-22N-150** | 22 | Bold / Normal | 1.5 | 0.02em (2%) |
| **Std-20B-150** / **Std-20N-150** | 20 | Bold / Normal | 1.5 | 0.02em (2%) |
| **Std-18B-160** / **Std-18N-160** | 18 | Bold / Normal | 1.6 | 0.02em (2%) |
| **Std-17B-170** / **Std-17N-170** | 17 | Bold / Normal | 1.7 | 0.02em (2%) |
| **Std-16B-170** / **Std-16N-170** | 16 | Bold / Normal | 1.7 | 0.02em (2%) |
| **Std-16B-175** / **Std-16N-175** | 16 | Bold / Normal | 1.75 | 0.02em (2%) |

### C. Dense (Dns)

Used for tables and administrative panels. Letter-spacing is always `0`.

| Size (px) | Bold Tokens | Normal Tokens | Line-Heights |
| --- | --- | --- | --- |
| **17** | `Dns-17B-130`, `Dns-17B-120` | `Dns-17N-130`, `Dns-17N-120` | 1.3, 1.2 |
| **16** | `Dns-16B-130`, `Dns-16B-120` | `Dns-16N-130`, `Dns-16N-120` | 1.3, 1.2 |
| **14** | `Dns-14B-130`, `Dns-14B-120` | `Dns-14N-130`, `Dns-14N-120` | 1.3, 1.2 |

### D. Oneline (Oln)

Used for buttons, input forms, chips, and single-line elements. Line-height
is `1.0` (100%), letter-spacing is `0.02em` (2%).

| Token Name (Bold / Normal) | Size (px) | Weight | Line-Height |
| --- | --- | --- | --- |
| **Oln-17B-100** / **Oln-17N-100** | 17 | Bold / Normal | 1.0 |
| **Oln-16B-100** / **Oln-16N-100** | 16 | Bold / Normal | 1.0 |
| **Oln-14B-100** / **Oln-14N-100** | 14 | Bold / Normal | 1.0 |

### E. Mono

Used for code blocks and technical outputs. Line-height is `1.5` (150%),
letter-spacing is `0`.

| Token Name (Bold / Normal) | Size (px) | Weight | Line-Height |
| --- | --- | --- | --- |
| **Mono-17B-150** / **Mono-17N-150** | 17 | Bold / Normal | 1.5 |
| **Mono-16B-150** / **Mono-16N-150** | 16 | Bold / Normal | 1.5 |
| **Mono-14B-150** / **Mono-14N-150** | 14 | Bold / Normal | 1.5 |

---

## 4. Text Formatting Constraints

- **No Synthetic Italics**: Japanese typefaces do not have natural italic
  styles. Browsers implement `font-style: italic` by mechanically slanting the
  characters, which severely damages legibility. Avoid italics for Japanese
  paragraphs.
- **Ruby Text (Furigana)**: The browser default size is 50% of parent text. Keep
  parent-child alignments clean.
- **Underline Rules (`text-decoration-line`)**:
  - Always keep standard underlines (`underline`) on interactive links to
    distinguish them from standard text.
  - Avoid removing underlines on hover unless alternative indicators (bg color
    shifts, focus rings) are thoroughly tested.
  - Maintain standard line-through (`line-through`) for `<del>` and `<s>` tags
    to visually indicate deleted content.
  - Do not mix multiple decorative line styles (`double`, `dotted`, `dashed`,
    `wavy`) in close proximity, as it degrades reading concentration.
  - Keep underline colors matching the text color unless specifically
    highlighting spelling/form errors.

---

## 5. Typography Accessibility Guidelines (JIS X 8341-3 / WCAG 2.2)

1. **Scalable Layouts**: Never specify absolute dimensions that block font
   scaling. Users must be able to scale text up to **200%** without elements
   overlapping or clipping text. Use responsive liquid layout strategies.
2. **No Images of Text**: Never convert textual content into image format
   (except for brand logos). Screen readers and browser search features cannot
   read text embedded inside images.
3. **Text Block Width**: Keep paragraphs at a comfortable width of **40
   full-width characters (80 half-width characters)**.
   - *Alternative relief*: If design requires wider paragraphs, compensate by:
     - Providing a brief summary at the top.
     - Indicating the estimated reading time.
     - Displaying a floating Table of Contents or outline navigation.
     - Separating long blocks with frequent headers (`<h2>`/`<h3>`) and bulleted
       lists.
4. **Paragraph Spacing**: Ensure the space below paragraphs is at least **1.5
   times** the line height (2.25 times the font size) to help readers tracking
   lines of text.
5. **No Justification**: Do not use `text-align: justify` for Japanese web copy.
   The varying spacing creates distracting white space "rivers" down the page,
   making text reading difficult for dyslexic users.
