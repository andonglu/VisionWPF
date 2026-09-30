#!/usr/bin/env python3
# 将 24x24 的工具箱 SVG 图标转换为 WPF 资源字典（VisionFlow.WpfApp/Themes/ToolIcons.xaml）。
# 用法：python tools/svg_to_tool_icons.py <svg目录> <输出xaml路径>
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

PALETTE = {
    "#00133b": "TextFillColorPrimaryBrush",
    "#3366cc": "AccentFillColorDefaultBrush",
    "#5f94ff": "AccentFillColorSecondaryBrush",
    "#19914b": "SystemFillColorSuccessBrush",
    "#e08a00": "SystemFillColorCautionBrush",
    "#d83340": "SystemFillColorCriticalBrush",
}


def brush(value, opacity=None):
    """生成 Fill/Stroke 属性值：主题色 -> DynamicResource；带透明度 -> 字面 ARGB。"""
    if value is None or value == "none":
        return "{x:Null}"
    v = value.lower()
    if opacity is not None:
        alpha = round(float(opacity) * 255)
        return f"#{alpha:02X}{v[1:].upper()}"
    if v in PALETTE:
        return "{DynamicResource " + PALETTE[v] + "}"
    return value


def attrs(el, root, tag):
    d = dict(root)
    d.update(el.attrib)
    sw = d.get("stroke-width", "1.8")
    out = [f'Stroke="{brush(d.get("stroke"))}"',
           f'StrokeThickness="{sw}"',
           'StrokeStartLineCap="Round"',
           'StrokeEndLineCap="Round"',
           'StrokeLineJoin="Round"',
           f'Fill="{brush(d.get("fill"), d.get("fill-opacity"))}"']
    dash = d.get("stroke-dasharray")
    if dash:
        nums = [float(x) / float(sw) for x in re.split(r"[ ,]+", dash.strip()) if x]
        out.append('StrokeDashArray="' + " ".join(f"{n:g}" for n in nums) + '"')
    return " ".join(out)


def points_to_data(points, close):
    nums = re.split(r"[ ,]+", points.strip())
    pairs = [f"{nums[i]},{nums[i + 1]}" for i in range(0, len(nums), 2)]
    return "M" + " L".join(pairs) + (" Z" if close else "")


def convert_element(el, root, indent, lines):
    tag = el.tag.split("}")[-1]
    pad = " " * indent
    if tag == "g":
        m = re.match(r"rotate\(\s*(-?[\d.]+)[ ,]+(-?[\d.]+)[ ,]+(-?[\d.]+)\s*\)",
                     el.attrib.get("transform", ""))
        lines.append(f'{pad}<Canvas Width="24" Height="24">')
        if m:
            lines.append(f'{pad}    <Canvas.RenderTransform>')
            lines.append(f'{pad}        <RotateTransform Angle="{m.group(1)}" CenterX="{m.group(2)}" CenterY="{m.group(3)}" />')
            lines.append(f'{pad}    </Canvas.RenderTransform>')
        for child in el:
            convert_element(child, root, indent + 4, lines)
        lines.append(f'{pad}</Canvas>')
        return
    a = attrs(el, root, tag)
    at = el.attrib
    if tag == "path":
        lines.append(f'{pad}<Path Data="{at["d"]}" {a} />')
    elif tag == "rect":
        rx = at.get("rx", "0")
        lines.append(f'{pad}<Rectangle Canvas.Left="{at.get("x", "0")}" Canvas.Top="{at.get("y", "0")}" '
                     f'Width="{at["width"]}" Height="{at["height"]}" RadiusX="{rx}" RadiusY="{rx}" {a} />')
    elif tag == "line":
        lines.append(f'{pad}<Line X1="{at["x1"]}" Y1="{at["y1"]}" X2="{at["x2"]}" Y2="{at["y2"]}" {a} />')
    elif tag == "circle":
        r = float(at["r"])
        lines.append(f'{pad}<Ellipse Canvas.Left="{float(at["cx"]) - r:g}" Canvas.Top="{float(at["cy"]) - r:g}" '
                     f'Width="{2 * r:g}" Height="{2 * r:g}" {a} />')
    elif tag == "ellipse":
        rx, ry = float(at["rx"]), float(at["ry"])
        lines.append(f'{pad}<Ellipse Canvas.Left="{float(at["cx"]) - rx:g}" Canvas.Top="{float(at["cy"]) - ry:g}" '
                     f'Width="{2 * rx:g}" Height="{2 * ry:g}" {a} />')
    elif tag in ("polygon", "polyline"):
        data = points_to_data(at["points"], tag == "polygon")
        lines.append(f'{pad}<Path Data="{data}" {a} />')
    elif tag == "text":
        # 居中小字号文本（如 ×n）：用固定宽度 TextBlock 近似 text-anchor="middle"
        x, y = float(at.get("x", "12")), float(at.get("y", "12"))
        fs = float(at.get("font-size", "7"))
        fill = brush(at.get("fill", "#00133B"))
        lines.append(f'{pad}<TextBlock Canvas.Left="{x - 8:g}" Canvas.Top="{y - fs:g}" Width="16" '
                     f'FontSize="{fs:g}" TextAlignment="Center" Padding="0" '
                     f'Foreground="{fill}" Text="{el.text or ""}" />')
    else:
        raise ValueError(f"未支持的元素 <{tag}>")


def convert(svg_path):
    tree = ET.parse(svg_path)
    root_el = tree.getroot()
    root = {k: v for k, v in root_el.attrib.items()
            if k in ("fill", "stroke", "stroke-width", "stroke-dasharray")}
    tool_id = svg_path.stem[len("tool-"):]
    lines = [f'    <Viewbox x:Key="ToolIcon.{tool_id}" x:Shared="False" Width="24" Height="24" Stretch="Uniform">',
             '        <Canvas Width="24" Height="24">']
    for el in root_el:
        convert_element(el, root, 12, lines)
    lines += ['        </Canvas>', '    </Viewbox>']
    return lines


def main():
    src, dst = Path(sys.argv[1]), Path(sys.argv[2])
    out = ['<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"',
           '                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">',
           '    <!-- 由 tools/svg_to_tool_icons.py 从 24x24 SVG 生成，请勿手改；主色经 DynamicResource 跟随主题。 -->']
    for svg in sorted(src.glob("tool-*.svg")):
        out.append("")
        out.extend(convert(svg))
    # 插件工具等未登记图标的通用回退
    out += ["",
            '    <Viewbox x:Key="ToolIcon._default" x:Shared="False" Width="24" Height="24" Stretch="Uniform">',
            '        <Canvas Width="24" Height="24">',
            '            <Rectangle Canvas.Left="4" Canvas.Top="4" Width="16" Height="16" RadiusX="2" RadiusY="2" '
            'Stroke="{DynamicResource TextFillColorPrimaryBrush}" StrokeThickness="1.8" Fill="{x:Null}" />',
            '            <Line X1="8" Y1="10" X2="16" Y2="10" Stroke="{DynamicResource TextFillColorPrimaryBrush}" StrokeThickness="1.8" StrokeStartLineCap="Round" StrokeEndLineCap="Round" Fill="{x:Null}" />',
            '            <Line X1="8" Y1="14" X2="13" Y2="14" Stroke="{DynamicResource TextFillColorPrimaryBrush}" StrokeThickness="1.8" StrokeStartLineCap="Round" StrokeEndLineCap="Round" Fill="{x:Null}" />',
            '        </Canvas>',
            '    </Viewbox>',
            '</ResourceDictionary>']
    dst.write_text("\n".join(out) + "\n", encoding="utf-8")
    print(f"converted {len(list(src.glob('tool-*.svg')))} icons -> {dst}")


if __name__ == "__main__":
    main()
