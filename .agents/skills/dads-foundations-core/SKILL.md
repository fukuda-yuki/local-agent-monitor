---
name: dads-foundations-core
description: >-
  DADS (Digital Agency Design System) foundation design reference for web UI.
  USE FOR: applying DADS color, typography, layout, spacing, icon, link, and
  accessibility rules to HTML/CSS/Razor output. Checking contrast ratios, font
  sizes, spacing tokens, grid systems, link states, icon placements, and
  accessibility compliance against DADS specifications.
  DO NOT USE FOR: React/Tailwind component implementation (use official DADS
  React repo as reference), backend-only tasks, non-UI tasks.
  PRIORITY: DADS official site (design.digital.go.jp) and official
  implementations are the authoritative source. This skill is an LLM-oriented
  digest derived from the official documentation.
license: MIT
metadata:
  author: 45deg (original), adapted for this repository
  version: "1.0.0"
  upstream: https://github.com/45deg/skills/tree/main/skills/dads-foundations-core
  dads-version: "2.14.0"
---

# DADS Design Foundations

Use this skill to apply, check, and audit Japanese government-style web UI
design decisions according to the Digital Agency Design System (DADS)
foundations.

## Read Order

1. Read **Operating Rules** and **Core Gotchas** below to align with
   foundational design constraints.
2. Follow the **Procedural Workflows** when constructing a layout, setting up
   colors, or typography.
3. For deep-dive specifications, route to the appropriate reference file in
   `references/` according to the **Reference Routing** map below.

## Reference Routing

When you need granular specs, token names, hex-level behaviors, or detailed
tables, open the following files:

- **Color Codes & Contrast**: Read
  [color_and_accessibility.md](references/color_and_accessibility.md).
- **Typography Scales & Decors**: Read
  [typography_and_text_styles.md](references/typography_and_text_styles.md).
- **Grid Systems & Spacing Spans**: Read
  [layout_and_spacing.md](references/layout_and_spacing.md).
- **Icon Placements & Link States**: Read
  [icons_and_links.md](references/icons_and_links.md).

---

## Operating Rules

- **Strict Contrast Constraints**: Text and text-images must maintain at least a
  **4.5:1** contrast ratio against backgrounds. Non-text interactive boundaries
  and icons must maintain at least **3:1** contrast.
- **Minimum Font Sizes**: The standard body text size is **16px**. Under no
  circumstances should text fall below **14px**. Avoid using 14px unless
  strictly constrained by space (e.g., footer metadata).
- **Spacing Units**: All margins and paddings must align with the **8px grid
  scale** (e.g., 8px, 16px, 24px, 32px, 64px).
- **No Color-Only Signaling**: Color must never be the sole indicator of state,
  warning, meaning, or hierarchy. Pair it with text labels, icons, or underline
  decorations.
- **Link Integrity**: Link text must *always* use an underline in combination
  with a distinct color (preferably traditional blue/visited-magenta).
- **Icon Label Pairing**: Icons are decorative helpers and must be paired with
  visible text labels. If a standalone icon is unavoidable, it must carry clear
  alternative text and a target size of at least **44x44px**.

---

## Procedural Workflows

### 1. Color Selection & Theme Customization

When establishing the color scheme for a government-style website:

1. **Define the Key Colors**:
   - Select a **Primary Color** for branding, primary buttons, active headers,
     and global navigation. Ensure it has at least a **4.5:1** contrast ratio
     against white background.
   - Derive a **Secondary Color** (higher or lower lightness within the same
     hue) for secondary actions or states (contrast >= 3:1 for elements,
     >= 4.5:1 if used for text).
   - Derive a **Tertiary Color** (opposite lightness level of Secondary Color)
     for supportive elements.
   - Establish a **Background Color** (normally white/black, but if colored,
     adjust foreground elements to maintain the contrast ratio).
2. **Incorporate Neutral Grays**: Use grey scales (such as `Gray-420` for light
   borders, `Gray-536` for text, `Gray-600` for dark borders) to separate
   components without creating visual noise.
3. **Map Functional & Semantic Colors**:
   - Unvisited link: Blue. Visited link: Visited purple/magenta (ensure
     light/dark contrast and adjust red component slightly to assist colorblind
     users).
   - Success states: Green.
   - Error states: Red.
   - Warning states: Yellow/Orange.

### 2. Typography & Hierarchy Setup

When formatting text-based layouts:

1. **Assign Typeface**: Set body text family to `Noto Sans JP` (with fallbacks
   `-apple-system, BlinkMacSystemFont, sans-serif`) and code text to
   `Noto Sans Mono` (fallback `monospace`).
2. **Apply Text Styles**: Choose the appropriate style category based on
   viewport and information density:
   - **Display (Dsp)**: Used for high-impact titles or hero headers.
     Line-height: `140%`, letter-spacing: `0`.
   - **Standard (Std)**: Used for body text and standard headers. Font
     size >= 16px. Letter spacing: `0%` to `2%`. Line-height: `150%` to `175%`
     (min `1.5x` for body text to reduce cognitive load).
   - **Dense (Dns)**: Used for space-constrained UI (e.g., tables, admin
     portals). Line-height: `120%` to `130%`.
   - **Oneline (Oln)**: Used for single-line UI controls (e.g., buttons, chips)
     to prevent vertical padding misalignment. Line-height: `100%`,
     letter-spacing: `2%`.
   - **Mono**: Used for code blocks or technical listings. Line-height: `150%`,
     letter-spacing: `0`.
3. **Set Weights**: Limit weight parameters to `Normal (400 / N)` and
   `Bold (700 / B)`.

### 3. Grid Layout & Breakpoint Partitioning

When structuring layout columns:

1. **Choose Breakpoints**: Align layout designs to two viewports:
   - **Desktop**: 768px and above.
   - **Mobile/Tablet**: Under 768px.
2. **Structure Columns**:
   - Use a **12-column grid system** for desktop layouts (1-column, 2-column,
     3-column, or 4-column configurations).
   - Position side navigation or menus as fixed or fluid panels, and define grid
     gutters on the right side of the menu.
3. **Configure Spacing Gaps**:
   - Set the **Gutter width** to at least **2 times the body font size** (e.g.,
     32px for 16px text) to prevent text overlap and misreading.
   - Set page margins to ensure clear margins on smaller screens.
   - Use **Column Offsets** (e.g., offsetting a centered 8-column text block by
     2 columns) for articles/reading materials to focus user attention.
4. **Implement Liquid Behavior**: Ensure column widths scale dynamically. If
   horizontal scrollbars appear on small screens or zoomed viewports, never hide
   them.

### 4. Spacing & Visual Grouping

When organizing elements within a container:

1. **Define Relationships by Distance**:
   - Group related elements closer together (e.g., place an image caption
     directly under the image with small spacing, like 8px).
   - Separate unrelated groups with larger spacing (e.g., 24px or 32px) to form
     a clear visual hierarchy.
2. **Adjust Spacing to Element Importance**: Assign larger margins to primary
   headings (`Std-36B` or `Std-45B`) and smaller margins to lower-level
   headings to visually structure the document sequence.

### 5. Links & Icons Implementation

When building navigation elements:

1. **Verify Link Contrast & Underline**: Ensure all links are visibly
   underlined. Validate that unvisited and visited links are easily
   distinguished (especially under colorblindness simulations).
2. **Apply Focus/Hover Feedbacks**:
   - Focus: Must show a visible black outline and yellow background.
   - Hover: Change color slightly (brighter/darker) and make the underline
     thicker. Never change font-weight or font-size on hover (prevents layout
     shift).
3. **Position Icons**:
   - Block-level: Use **Front Icon** (start) or **End Icon** (end).
   - Inline-level: Use **Lead Icon** (before text) or **Tail Icon** (after
     text).
4. **Annotate Special Links**:
   - For links opening in a new tab: Append an external link icon and set the
     alt-text/screen reader description to "新しいタブを開きます" (Opens in a
     new tab).
   - For downloads: Append a document icon (e.g. PDF) and state the file
     type/size in the text (e.g., `(PDF: 100KB)`).

---

## Core Gotchas

- **Formatting Spaces Pitfall**: **Never** use double-byte spaces (全角スペース)
  or `&nbsp;` to align elements. This breaks text reading order on screen
  readers. Use CSS grid, flex, margins, or paddings instead.
- **CSS Grid/Flex logical order shift**: Avoid using the CSS `order` or
  `grid-placement` properties to change the visual order of items drastically
  away from the HTML source DOM order. This creates a confusing experience for
  screen reader users and keyboard navigators.
- **Forced Color Mode (High Contrast)**: Drop shadows and colored backgrounds
  are ignored in Forced Color Modes. If an overlay dialog depends on shadow to
  separate itself from the page background, it will blend in completely.
  **Always** apply a solid border (or transparent border that resolves in high
  contrast) to overlay surfaces.
- **Italics in Japanese**: Japanese fonts do not possess a native italic
  typeface. Applying `font-style: italic` forces the browser to synthetic-slant
  the text, degrading legibility. **Do not** use italics for Japanese text
  blocks.
- **Text Block Width**: Limit text container widths to approximately **40
  full-width characters (80 half-width characters)** to support readers with
  cognitive or reading difficulties. If design requires wider columns,
  compensate by providing an executive summary, page index/table of contents, or
  clear visual dividers.
- **Text-align Justify**: Avoid using `text-align: justify` for paragraphs. The
  variable character spacing creates "rivers of white space" that distract and
  confuse dyslexic readers.
- **Interactive Target Sizes**: For touch-screen compatibility, ensure any
  standalone button or icon has a clickable region of at least **24x24px** (with
  4px surrounding padding to reach **32x32px**), or ideally **44x44px** for
  high-priority actions.
