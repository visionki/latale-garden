# 美术素材与提示词

这些素材使用 **内置 image_gen 工具**从用户选定的 GUI 效果图拆出，没有使用 CLI/API 回退。

| 文件 | 用途 |
|---|---|
| `Assets/design-reference.png` | 用户认可的原设计效果图 |
| `Assets/hero.png` | 实装左侧风景与 LaTale 标志，嵌入 EXE |
| `Assets/launch-button.png` | 无字粉色按钮底图，嵌入 EXE；三角与按钮文字由真实 WPF 控件绘制 |
| `Assets/landscape.png` | 去除所有界面文字与标志的独立风景层，供后续换布局使用 |

右侧面板、标题栏、图标、开关、状态、目录、设置和日志均由 WPF 实时绘制。它们不是整张图片上的假按钮。文字可以更新，按钮可以获得焦点和响应键盘。

透明标志的生成尝试没有产出真正 alpha 通道，未将那些带棋盘格的图片纳入交付。最终将标志保留在完整风景层；按钮采用与右侧面板匹配的浅奶油底图，避免伪透明边缘。

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
