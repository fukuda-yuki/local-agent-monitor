# Icons & Links Reference

Use this reference to choose icon placements, set up link colors and states,
structure file download links, and evaluate touch targets and screen-reader
accessibility.

---

## 1. Icon Placement & Sizing

DADS structures icon placements based on their position in the DOM and layout
box model:

### A. Icon Classifications

- **Front Icon**: Placed at the very beginning of a block-level container.
- **Lead Icon**: Placed at the beginning of an inline text sequence (just before
  the text).
- **Tail Icon**: Placed at the end of an inline text sequence (just after the
  text).
- **End Icon**: Placed at the very end of a block-level container.

### B. Icon Pairing & Alternative Text Rules

- **The Pairing Rule**: "Icons are designed as visual helpers. They must
  **always** be paired with a visible, descriptive text label."
- **Alt Text for Paired Icons**: If an icon is paired with a text label, the
  icon is decorative.
  - For `<img>` tags, use an empty alternative text attribute (`alt=""`).
  - For inline SVG or font icons, hide them from screen readers using
    `aria-hidden="true"`.
  - **Caution**: **Never** assign an `aria-label` to the icon tag if it is
    paired with visible text. Doing so causes screen readers to double-read the
    label.
- **Alt Text for Standalone Icons**: Standalone icons (permitted *only* when
  severe space limits exist) must have an alternative text attribute equivalent
  to a text label.
  - Standalone icons must maintain a touch target size of **at least 44x44px**.
  - DADS provides dedicated icons with text labels embedded in the SVG graphic
    for standalone buttons.

### C. Link Range for Icons

- If an icon is placed inside a link component, it must be contained within the
  same `<a>` tag as the text label.
- **Caution**: Do not wrap the icon and the text label in separate `<a>` tags.
  Separate tags create duplicate tab-stops for keyboard users and duplicate
  announcements for screen readers.

### D. Icon Contrast Limits

- **Lead and Tail Icons**: Because inline icons are treated as part of the text
  sequence, they must meet a background contrast ratio of **at least 4.5:1**.
- **Front and End Icons**: Icons that only serve as block-level separators or
  decoration are permitted to meet a background contrast ratio of **3:1**.

---

## 2. Link Design & States

Links are the basic navigational elements of the web. They must be easily
identifiable and distinct from normal body text.

### A. Basic Link Structure

- **Color + Underline**: "A link must use an underline in combination with a
  distinct color to indicate it is interactive."
- **No Color-Only Links**: Do not rely on color alone to indicate links.
  Color-blind users or users with low-contrast screens will not be able to
  identify them.

### B. Standard Link Colors

DADS adopts traditional link colors to align with user mental models:

- **Unvisited Link (Default)**: Dark Web Blue.
- **Visited Link (Visited)**: Visited Magenta/Purple.
  - *Note*: DADS modifies visited magenta slightly to increase its brightness
    difference from unvisited blue, ensuring colorblind users can distinguish
    the two states.

### C. Link Interactive States

Configure link states in CSS in this exact order: `link` -> `visited` ->
`hover` -> `active` -> `focus`.

| Link State | Visual Presentation | CSS Target Properties |
| --- | --- | --- |
| **Default** (Unvisited) | Underlined text. Primary link color. | `a:link` |
| **Visited** | Underlined text. Visited magenta color. | `a:visited` |
| **Hover** | Link color shifts (brighter/darker). Underline becomes thicker. | `a:hover` |
| **Active** | Underline stays. Color shifts to Accent Orange. | `a:active` |
| **Focus** (Keyboard navigation) | High-contrast black border (focus ring) and yellow background. | `a:focus-visible` |

- **No Hover Layout Shift**: "Do **not** increase `font-weight` or `font-size`
  on hover. Changing font weight or size causes the surrounding text layout to
  shift, creating visual jank."

---

## 3. Link Text Quality & Target Sizes

### A. Link Text Descriptiveness (JIS X 8341-3 / WCAG 2.2)

- **Clear Destination**: Link text must clearly describe the destination page.
  - *Correct*: `デジタル社会の実現に向けた重点計画の詳細` (Details of the
    Priority Plan for the Realization of a Digital Society).
  - *Incorrect*: `もっと詳しく` (Read more) or `こちら` (Click here).
- **Standalone Accessibility**: Screen readers allow users to list all links on
  a page. If links are named "here" or "details", the user cannot distinguish
  between them.
- **ARIA Override**: If template constraints force the use of repetitive text
  (e.g. "Read details" under multiple news articles), you must apply
  `aria-label` to specify the unique context:
  ```html
  <a href="/news/1" aria-label="デジタル庁ニュースリリース第1号の詳細を読む">詳細を読む</a>
  ```

### B. Touch Target Sizes

- "To ensure touch-screen usability, interactive link regions must be **at least
  24x24px** (with surrounding padding to reach **32x32px** or **44x44px**)."
- If the font size is 16px (making the line height 16px), add at least **4px of
  vertical padding** to meet target size requirements.

---

## 4. Special Link Configurations

### A. New Tab Links (External Links)

When a link opens in a new tab/window:

1. Append an **External Link Icon** (`arrow-up-right` inside a square) at the
   end of the text sequence.
2. The icon must be included inside the `<a>` tag (no underline on the icon).
3. The icon must have an alt-text or screen reader announcement:
   `"新しいタブを開きます"` (Opens in a new tab).

### B. File Download Links (Non-Web Resources)

When a link points directly to a file download (e.g., PDF, ZIP):

1. Append a **File Icon** corresponding to the file type at the end of the text
   sequence.
2. The text label must explicitly state the file format and size.
   - *Example*: `デジタル庁紹介パンフレット （PDF: 100KB）`
