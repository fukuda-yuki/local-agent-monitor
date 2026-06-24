# Layout & Spacing Reference

Use this reference to design responsive layout grids, choose spacing tokens,
arrange components, and verify layout accessibility.

---

## 1. 12-Column Grid System

DADS structures pages around a unified **12-column grid system** to coordinate
desktop layouts.

### A. Grid Components

- **Margins**: The spacing between the outer edge of the grid columns and the
  viewport edge. Always preserve padding here to prevent content from touching
  the screen boundary.
- **Columns**: The actual vertical content tracks. The width of a single column
  should correspond to a multiple of standard text size.
- **Gutters (Column gaps)**: The spacing between columns.
  - *Constraint*: Gutters must be **at least 2 times the body text font size**
    (e.g. at least **32px** for a 16px base font size). Narrower gutters cause
    adjacent columns of text to blend together, leading to misreading.
- **Left Menu Layouts**: In administrative or dashboard pages with a persistent
  left navigation panel, the 12-column grid is calculated *to the right* of the
  menu, using the menu's right edge as the starting margin.

### B. Standard Layout Templates (Desktop)

1. **1-Column**: Content spans all 12 columns.
2. **2-Column**: Common splits:
   - **9 / 3** or **3 / 9**: Main content paired with side sidebar panels.
   - **8 / 4** or **4 / 8**: Focused content paired with sidebar widgets.
   - **6 / 6**: Equal two-column layouts.
3. **3-Column**: Common splits:
   - **3 / 6 / 3**: Centered main content with two supporting side panels.
   - **4 / 4 / 4**: Three equal columns.
4. **4-Column**: Spaced as **3 / 3 / 3 / 3** (four equal columns).

### C. Column Offsets (Centered Layouts)

For text-heavy reading pages (articles, policies), use offsets to keep text
columns narrow and centered:

- **8-Column Layout with 2-Column Offset**: Spans columns 3 through 10 (offset
  of 2 columns on left and right).
- **6-Column Layout with 3-Column Offset**: Spans columns 4 through 9 (offset
  of 3 columns on left and right).

---

## 2. Responsive Breakpoints

DADS establishes a simple breakpoint scale for layout conversions:

| Device Viewport | Viewport Range | Layout Behavior |
| --- | --- | --- |
| **Desktop** | **768px and above** | Standard 12-column grid templates. Side menus are permitted. |
| **Mobile & Tablet** | **Under 768px** | Stacks columns vertically into 1 column. Page margins shrink. |

---

## 3. Layout Accessibility Guidelines (JIS X 8341-3 / WCAG 2.2)

1. **Liquid Grid Containers**: Always implement fluid layouts (where columns or
   gutters dynamically scale with the viewport). Avoid fixed-width layouts that
   force horizontal scrolling on standard mobile screens.
2. **Horizontal Scroll Rules**: If a specific component (e.g., a data table)
   must scroll horizontally on small screens:
   - **Never** set `overflow-x: hidden` to mask the scrollbar.
   - Ensure the scrollbar remains visible and interactive for touch and mouse
     users.
3. **Meaningful DOM Sequence**:
   - Web browsers allow visual reordering using CSS Grid `grid-placement` or CSS
     Flexbox `order`. However, this does *not* alter the keyboard tab-focus
     sequence or screen reader sequence.
   - **Constraint**: Do not use CSS order or visual positioning to rearrange
     elements in a way that conflicts with their logical HTML source sequence. If
     visual arrangement must be changed, rearrange the actual DOM node order in
     the HTML template.

---

## 4. Spacing Rules & Box Model

Spacing organizes the density and visual flow of components.

### A. Spacing Types

- **Padding (Internal space)**: Creates breathing room inside a component border
  (e.g., spacing inside a button or card). Expands the element's background
  color.
- **Margin (External space)**: Creates separation between adjacent components.
  Does not affect component dimensions.
  - *Note*: Remember that adjacent vertical margins collapse in CSS block
    layouts.

### B. The 8px Grid Spacer Scale

All spacing margins and paddings must align with the **8px base grid** to
maintain visual balance:

| Token Name | Size (px) | Application Heuristics |
| --- | --- | --- |
| **Spacer-8** | 8 | Compact padding. Space between text and icon, or image and caption. |
| **Spacer-16** | 16 | Medium padding. Vertical spacing inside small cards. |
| **Spacer-24** | 24 | Standard spacing. Separation between paragraphs, or between categories and section headings. |
| **Spacer-32** | 32 | Generous layout spacing. Margin between body sections. |
| **Spacer-64** | 64 | Maximum layout separation. Spacing above high-priority page endings. |

---

## 5. Spacing Accessibility & Coding Constraints

1. **Proximity Principle (Visual Relationships)**:
   - Elements that belong together must be placed visually closer than elements
     that are independent.
   - *Example*: A figure caption must have small spacing (e.g. 8px) below the
     image. If the caption is separated by the same distance as the surrounding
     body paragraphs (e.g. 24px), users will struggle to associate the caption
     with the correct image.
2. **Visual Hierarchy (Headings Spacing)**:
   - Main headings (`h1` or `h2`) require larger surrounding margins than
     subheadings (`h3` or `h4`) to represent their hierarchical priority.
3. **Alignment Code Restrictions**:
   - **Never** use double-byte whitespace characters (全角スペース) or
     non-breaking spaces (`&nbsp;`) to nudge text elements into visual
     alignment.
   - Double-byte spaces distort screen reader pronunciations (reading them out
     as empty gaps or spelling out code letters), and they do not scale when a
     user magnifies the text size.
   - Always adjust spacing using CSS `margin` or `padding` properties.
4. **No Paragraph Justification**: Avoid `text-align: justify` for paragraph
   copy.
