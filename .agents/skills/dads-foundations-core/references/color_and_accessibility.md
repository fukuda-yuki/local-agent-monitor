# Color & Accessibility Reference

Use this reference to choose color roles, verify contrast ratios, adapt brand
colors, and ensure accessibility for users with color vision deficiencies.

## Contents

1. Color Role Directory — key, neutral, functional, accent, semantic colors
2. Color Palette Registry (v2.14.0) — primitive and neutral HEX values
3. Contrast Requirements (JIS X 8341-3 / WCAG 2.2)
4. Brand Color Adaptation Workflow
5. Color Universal Design (CUD) Guidelines

---

## 1. Color Role Directory

DADS structures colors into five key categories to govern brand tone, interface
structure, states, and semantics:

### A. Key Colors

- **Primary Color**: Defines the visual tone of the site. Used for branding
  (logos, headers) and primary actions (CTA buttons, active states).
  - *Constraint*: Must meet **4.5:1** contrast against background.
  - *Multi-brand*: If a site requires multiple primary colors, each primary
    color must independently spawn its own secondary, tertiary, and background
    lightness ranges.
- **Secondary Color**: Lighter or darker step of the Primary hue. Used for
  secondary UI actions (outline buttons, supporting options).
  - *Constraint*: **3:1** contrast for boundaries; **4.5:1** contrast if used
    for text.
- **Tertiary Color**: Set to the opposite lightness level of the Secondary
  Color. Used to supplement secondary elements.
  - *Constraint*: **3:1** contrast for boundaries; **4.5:1** contrast if used
    for text.
- **Background Color**: Custom background tones. Typically white (`#FFFFFF`) or
  black (`#000000`). If tinted backgrounds are used, all foreground text and
  interactive components must be verified against this specific background.

### B. Common Neutrals (Grays)

Neutrals govern body text, borders, dividers, and background surfaces.

- **Gray-420**: The boundary token. It is the minimum gray shade that ensures a
  **3:1** contrast ratio against White (`#FFFFFF`). Use for borders, input
  frames, and dividers on light backgrounds.
- **Gray-536**: The midpoint neutral. It ensures at least a **4.5:1** contrast
  ratio against White (`#FFFFFF`) and Black (`#000000`). It is safe for text on
  light or dark surfaces.
- **Gray-600**: The dark-surface boundary token. It ensures a **3:1** contrast
  ratio against Black (`#000000`). Use for borders and dividers on dark
  backgrounds.

### C. Functional Colors

- **Link Color (Unvisited)**: Default is Web Blue. Must be distinct from body
  text and meet contrast requirements.
- **Visited Link Color (Visited)**: Default is Purple/Magenta.
  - *CUD adjustment*: The visited purple must have a slightly adjusted red
    component to increase its lightness difference from unvisited blue, making
    it distinguishable for users with protanopia or deuteranopia.

### D. Accent Colors

- Violet, Blue, Light Blue, Cyan, Green, Lime, Yellow, Orange, Red, Magenta.
- Use sparingly for highlighting details or non-critical CTAs. Avoid using
  accent colors for primary interactive elements unless contrast and boundaries
  are explicitly validated (text: 4.5:1, non-text: 3:1).

### E. Semantic Colors

- **Success (Green)**: Indicates safety, completeness, or successful actions.
  - *Tokens*: `Success-1` (Light green for backgrounds), `Success-2` (Dark
    green for text/icons).
  - *HEX values*: Not included in the local registry below. Fetch the current
    Green palette from the DADS official site
    (<https://design.digital.go.jp/dads/>) before implementing Success tokens.
- **Error (Red)**: Indicates failure, critical warning, or danger.
  - *Tokens*: `Error-1` (Light red for backgrounds), `Error-2` (Dark red for
    text/icons).
- **Warning (Yellow/Orange)**: Indicates caution, restrictions, or notice.
  - *Tokens*: Yellow-based (`Warning-1`/`Warning-2`) or Orange-based
    (`Warning-1`/`Warning-2`).

---

## 2. Color Palette Registry (v2.14.0)

This registry contains the verified HEX codes for the DADS core palette. Use
these values to build design tokens and style variables.

### A. Primitive Colors

#### Blue

| 50 | 100 | 200 | 300 | 400 | 500 | 600 | 700 | 800 | 900 | 1000 | 1100 | 1200 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| #E8F1FE | #D9E6FF | #C5D7FB | #9DB7F9 | #7096F8 | #4979F5 | #3460FB | #264AF4 | #0031D8 | #0017C1 | #00118F | #000071 | #000060 |

#### Light Blue

| 50 | 100 | 200 | 300 | 400 | 500 | 600 | 700 | 800 | 900 | 1000 | 1100 | 1200 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| #F0F9FF | #DCF0FF | #C0E4FF | #97D3FF | #57B8FF | #39ABFF | #008BF2 | #0877D7 | #0066BE | #0055AD | #00428C | #00316A | #00234B |

#### Cyan

| 50 | 100 | 200 | 300 | 400 | 500 | 600 | 700 | 800 | 900 | 1000 | 1100 | 1200 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| #E6FCFF | #C8F8FF | #99F2FF | #79E2F2 | #2BC8E4 | #01B7D6 | #00A3BF | #008DA6 | #008299 | #006F83 | #006173 | #004C59 | #003741 |

#### Lime

| 50 | 100 | 200 | 300 | 400 | 500 | 600 | 700 | 800 | 900 | 1000 | 1100 | 1200 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| #EBFAD9 | #D0F5A2 | #C0F354 | #ADE830 | #9DDD15 | #8CC80C | #7E8400 | #6FA104 | #618E00 | #507500 | #3E5A00 | #2C4100 | #1E2000 |

#### Yellow

| 50 | 100 | 200 | 300 | 400 | 500 | 600 | 700 | 800 | 900 | 1000 | 1100 | 1200 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| #FBF5E0 | #FFF0B3 | #FFE380 | #FFD43D | #FFC700 | #EBB700 | #D2A400 | #B78F00 | #A58000 | #927200 | #806300 | #6E5600 | #604800 |

#### Orange

| 50 | 100 | 200 | 300 | 400 | 500 | 600 | 700 | 800 | 900 | 1000 | 1100 | 1200 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| #FFEEE2 | #FFDFCA | #FFC199 | #FFA66D | #FF8D44 | #FF7628 | #FB5B01 | #E25100 | #C74700 | #AC3E00 | #8B3200 | #6D2700 | #541600 |

#### Red

| 50 | 100 | 200 | 300 | 400 | 500 | 600 | 700 | 800 | 900 | 1000 | 1100 | 1200 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| #FDEEEE | #FFDADA | #FFBBBB | #FF9696 | #FF7171 | #FF5454 | #FE3939 | #FA0000 | #EC0000 | #CE0000 | #A90000 | #850000 | #620000 |

#### Magenta

| 50 | 100 | 200 | 300 | 400 | 500 | 600 | 700 | 800 | 900 | 1000 | 1100 | 1200 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| #F3E5F4 | #FFD0FF | #FFAEFF | #FF8EFF | #F661F6 | #F137F1 | #DB00DB | #C000C0 | #AA00AA | #8B008B | #6C006C | #500050 | #3B003B |

#### Purple

| 50 | 100 | 200 | 300 | 400 | 500 | 600 | 700 | 800 | 900 | 1000 | 1100 | 1200 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| #F1EAFA | #ECDDFF | #DDC2FF | #CDA6FF | #BB87FF | #A565F8 | #8843E1 | #6F23D0 | #5C10BE | #5109AD | #41048E | #30016C | #210048 |

### B. Neutral Colors

- **White:** `#FFFFFF`
- **Black:** `#000000`

#### Gray

| Tone | HEX Code | Opacity |
| --- | --- | --- |
| **Gray-50** | #F2F2F2 | 5% |
| **Gray-100** | #E6E6E6 | 10% |
| **Gray-200** | #CCCCCC | 20% |
| **Gray-300** | #B3B3B3 | 30% |
| **Gray-400** | #999999 | 40% |
| **Gray-420** | #949494 | 42% |
| **Gray-500** | #7F7F7F | 50% |
| **Gray-536** | #767676 | 53.6% |
| **Gray-600** | #666666 | 60% |
| **Gray-700** | #4D4D4D | 70% |
| **Gray-800** | #333333 | 80% |
| **Gray-900** | #1A1A1A | 90% |

> **Gray Accessibility Criteria**
>
> - **Gray-420**: Secures a **3:1** contrast ratio against White (#FFFFFF)
>   (shades darker than this meet the contrast requirement for non-text
>   elements).
> - **Gray-536**: Secures a **4.5:1** contrast ratio against White (#FFFFFF)
>   and Black (#000000) (meets the contrast requirement for text).
> - **Gray-600**: Secures a **3:1** contrast ratio against Black (#000000)
>   (shades lighter than this meet the contrast requirement for non-text
>   elements).

---

## 3. Contrast Requirements (JIS X 8341-3 / WCAG 2.2)

DADS enforces stricter contrast requirements than standard WCAG AA by removing
the exception for large text:

| Element Type | DADS Requirement | Standard WCAG 2.2 Requirement |
| --- | --- | --- |
| **All Text & Text Images** | **>= 4.5:1** (AA) / **>= 7.0:1** (AAA) | **>= 4.5:1** (AA) for normal text / **>= 3.0:1** (AA) for large text (>=24px or >=18px bold) |
| **Non-Text Elements** (Borders, Icons, Focus Rings) | **>= 3.0:1** (AA) | **>= 3.0:1** (AA) |
| **Disabled Elements** | Exempt | Exempt |

### Chart and Diagram Special Rules

- **Grid lines & Legend markers**: If a value-reading line in a chart falls
  below 3:1 contrast, you must place high-contrast text labels (>= 4.5:1)
  adjacent to the markers.
- **Interactive charts**: If a diagram dims inactive data, the dimmed state must
  still provide a high-contrast label or clear text hint showing it is
  interactive. Alternatively, provide a full text description/data table as an
  alternative.

---

## 4. Brand Color Adaptation Workflow

When translating a pre-existing brand color palette into a web-accessible
interface:

1. Check if the brand color meets **4.5:1** contrast against the surface.
2. If yes, use directly as Primary/Text Token.
3. If no, decide whether it can be reserved for Logo/Pure Decor only.
4. If it can be reserved, keep the brand color for logo only and select a
   web-friendly Primary color.
5. If it cannot be reserved, adjust lightness/saturation until
   contrast >= 4.5:1 and use the adjusted shade as Primary/Text Token.

Always define both foreground and background color tokens in CSS. Never rely on
the browser's default background (white) because users may override it.

---

## 5. Color Universal Design (CUD) Guidelines

To accommodate color vision variations:

1. **Hue Separation**: Do not rely on Red and Green alone to distinguish state
   (e.g. error vs success). Always combine them with text labels ("Error" /
   "Complete") or icons.
2. **Brightness/Lightness Contrast**: Ensure unvisited links (blue) and visited
   links (magenta) differ in lightness, not just color hue.
3. **Simulated Verification**: Review all interface designs using C-type
   (General), P-type (Protanopia), D-type (Deuteranopia), and T-type
   (Tritanopia) color simulators to confirm boundaries and elements remain
   visible.
