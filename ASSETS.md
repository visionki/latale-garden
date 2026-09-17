# 美术素材与提示词

界面素材使用 **内置 image_gen 工具**从用户选定的 GUI 效果图拆出；应用图标的生成、透明处理和构建方式见文末。没有使用 CLI/API 回退。

| 文件 | 用途 |
|---|---|
| `Assets/design-reference.png` | 用户认可的原设计效果图 |
| `Assets/hero.png` | 实装左侧风景与 LaTale 标志，嵌入 EXE |
| `Assets/launch-button.png` | 无字粉色按钮底图，嵌入 EXE；三角与按钮文字由真实 WPF 控件绘制 |
| `Assets/landscape.png` | 去除所有界面文字与标志的独立风景层，供后续换布局使用 |

右侧面板、标题栏、操作图标、开关、状态、目录、设置和日志均由 WPF 实时绘制。它们不是整张图片上的假按钮。文字可以更新，按钮可以获得焦点和响应键盘。

早期界面中透明标志的生成尝试没有产出真正 alpha 通道，未将那些带棋盘格的图片纳入交付。最终将界面左侧标志保留在完整风景层；按钮采用与右侧面板匹配的浅奶油底图。应用图标使用独立的透明资源。

## 实装风景与标志：最终提示词

Reference: `Assets/design-reference.png`

```text
Production UI extraction. Recreate ONLY the complete left landscape panel of this exact approved launcher as a standalone square illustration texture. Preserve the beautiful floating-island village, huge tree, rainbow, cat airship, flowers, path, painterly style and original composition. IMPORTANT: KEEP the approved LaTale logo at the upper-left and its subtitle '彩虹岛 · 台服' exactly as part of this opaque illustrated layer, occupying about 40% of image width and 22% of image height, with a modest 5% inset. REMOVE the central handwritten welcome sentence and underline so the software can render that text separately. Remove the bottom '非官方辅助启动器' label and the English writing on the wooden sign. Remove the entire right cream panel, window frame, surrounding blue margin and all controls. Output only the square illustrated left panel edge-to-edge with integrated logo in the upper left, no transparency, no checkerboard, no rounded corners, no empty margin. Keep an uncluttered sky region below the logo for software text. Match the reference's color and design closely.
```

## 按钮底图：最终提示词

Reference: `Assets/design-reference.png`

```text
Extract the coral pink launch button from the approved launcher as a production UI asset on a FLAT SOLID IVORY background, exact color #FFF8ED. Opaque image, no transparency or checkerboard. Recreate the original rounded rectangle, glossy upper highlight, fine white/cream border, coral pink gradient and tiny gold leaf ornaments just outside its four corners. Keep the faint paw motif at the far right. REMOVE the triangle icon and remove ALL text: the center must be blank for real software text. Single horizontal button centered, very tightly framed. Canvas aspect ratio 3:1; the button should occupy 95 percent of canvas width and 88 percent of canvas height, with only a few pixels of the flat ivory background around it. Output ONLY this single blank button with its small corner ornaments. No other UI, no headings, no labels, no surrounding window, no extra shadows on the ivory background. Clean polished game launcher asset matching the reference.
```

## 独立无字风景：提示词

Reference: `Assets/design-reference.png`

```text
Use case: precise-object-edit. Image 1 is the approved LaTale launcher design. Extract and clean ONLY its left-hand fantasy landscape as a standalone production background asset. Reproduce the approved scene very closely: huge tree and tree-house on the right, floating islands with red-roof villages, distant blue mountain cliffs, turquoise water and waterfalls, rainbow and blue sky, little flowers, path, charming little cats and airship. Remove ALL overlaid UI: the LaTale logo, Chinese subtitle, welcome lettering and underline, the bottom label, the complete cream panel on the right, window edges, toolbar and outer blue margin. Also remove the English lettering on the wooden sign, leaving the wooden sign blank with its cat ornament. Inpaint all removed lettering naturally into the landscape. Keep the left scene composition and painterly fantasy visual style. Deliver only the seamless rectangular landscape image, approximately square 1024x1024, edge to edge, no rounded corners, no shadow, no padding, no text, no logos, no UI. Preserve calm sky across the upper left for a separately overlaid logo.
```

## 应用图标（v1.1.2）

采用用户选定的“L＋小角色”方案。最初概念由内置 image_gen 根据[台服官方 Logo](https://landing.mangot5.com/template/la/images/logo.png)生成；用户明确同意本地抠图后，保留选定图案，移除原图中画入的棋盘背景并清理边缘。没有使用 CLI/API 回退。此图标用于非官方 LaTale Garden 启动器。

| 文件 | 用途 |
| --- | --- |
| `Assets/app-icon-source.png` | 保留选定造型的透明 PNG 原图，供重新构建图标 |
| `Assets/app-icon.png` | 256 像素窗口图标，嵌入 EXE |
| `Assets/app.ico` | 含 16、20、24、32、40、48、64、128、256 像素的透明图标；用于 EXE 和托盘 |
| `build-icon.ps1` | 使用 Windows 自带的 System.Drawing 生成上述 PNG / ICO；由 build.ps1 自动调用 |

图标原图有真实 alpha 通道，未把灰色棋盘作为背景分发。各尺寸已在深浅背景检查，并验证 Windows 能从 EXE 读取图标、WPF 窗口与系统托盘能加载内嵌资源。

以下为选定概念的原始生成提示词。提示词中的“透明”要求未由生图输出满足，最终透明处理使用上述用户授权的本地方法完成。
## C — LaTale 字母徽记

输出：`C-l-mascot.png`

```text
Use case: logo-brand.
Asset type: one Windows desktop/taskbar application icon concept for the independent fan launcher "LaTale Garden".
Input image 1 is the authentic Taiwan PC LaTale official logo, supplied as a STYLE AND MASCOT REFERENCE, not a canvas to edit. Closely study its distinctive flat playful cartoon art: almost-black thick contour, thin warm-white keyline, slightly lopsided cheerful lettering, orange/yellow/turquoise accents, and the tiny cream-colored rounded character perched above the Chinese title.
Create a square high-resolution icon with a genuinely transparent alpha background. One isolated compact mark, centered, occupying about 84% of the canvas, enough safe margin for all contours. It must read clearly at 32px and 48px. Strong silhouette, restrained interior details, mostly flat solid fills with at most one simple cel-shaded accent. Professional carefully drawn smooth curves with a little of the reference's playful irregularity.
Do NOT use a glossy rounded-square mobile app tile, glass, bevels, metallic gold, 3D render, gradients, sparkly fantasy scenery, a rainbow arch, cottage, forest, wreaths, abundant flowers, photorealism, mockup desk, product presentation, drop shadow, background checkerboard, labels or watermark. Transparent pixels outside the silhouette, not a drawn transparency pattern. Match the actual reference's nostalgic 2D PC game character, not a generic cute mobile game.
Primary request: a compact emblem combining one large uppercase letter "L" and the original tiny cream mascot from the TOP RIGHT of the reference logo. The "L" is oversized, jaunty, thick, slightly slanted cartoon lettering inspired by the reference's distinctive LaTale title, with a playful curling end on its lower stroke. Its broad letter fill is bright turquoise with one small sunny-yellow accent, thin warm-white keyline and dark black outer contour. The original cream rounded bean-like mascot with two short coral ear tips, dot eyes and small smile peeks over and rests on the upper part of the L, occupying no more than the upper third. Preserve that mascot's very simple flat original look, no big sparkling eyes or ordinary cat anatomy. Design the L and mascot as ONE compact silhouette, no loose disconnected decorations.
Text (verbatim): "L". No other letters, no full wordmark, no shield, no plaque, no surrounding square. It should unmistakably feel related to the supplied official logo while remaining readable as a desktop icon.
```
