# -*- coding: utf-8 -*-
"""临时生成器：解析 HALCON 文档 HTML + 探针输出，生成 VisionFlow.Tools/Halcon/ParamCatalog.cs。"""
import json, re, html, io, os, sys

DOC_DIR = r"C:\Program Files\MVTec\HALCON-22.11-Steady\doc_en_US\html\reference\operators"
ROOT = r"D:\LRY\Code\visionproject\VisionFlow"

SYMS_2D = ["QR Code", "Data Matrix ECC 200", "PDF417", "Aztec Code",
           "Micro QR Code", "DotCode", "GS1 QR Code", "GS1 DataMatrix"]

# 手写中文说明（常用参数人工撰写；其余在 ZH_FALLBACK 中按英文名补充）
ZH = {
    'abort': '允许从另一线程中止正在运行的搜索（find_data_code_2d / find_bar_code）。',
    'additional_levels': 'Aztec Code 在最小/最大模块尺寸推导的搜索层数之外额外增加的搜索层数，提高难检符号的检出率。',
    'candidate_selection': '候选区域选择策略：extensive 增加候选区域数量以提高检出率，all 保留全部候选。',
    'contrast_min': '符号前景与背景的最小灰度对比度（差值 1~255），数值 5 以上可优化候选搜索。',
    'contrast_tolerance': '对局部对比度变化（眩光、反光等）的容忍度：low / high / any。',
    'decoding_scheme': 'Data Matrix 解码方式；raw 用于读取符合 ISO/IEC 16022 但数据编码自定义的符号。',
    'default_parameters': '将全部模型参数重置为 standard_recognition / enhanced_recognition / maximum_recognition 默认档，并重置训练状态。',
    'discard_undecoded_candidates': '是否丢弃无法解码的候选符号。',
    'finder_pattern_tolerance': '对定位图形缺损或部分遮挡的容忍度：low / high。',
    'format': 'Aztec Code 格式，取值为 compact、full_range、rune 的空格组合。',
    'max_allowed_error_correction': 'DotCode 允许使用的最大纠错能力，调低可减少部分覆盖真实符号的误检。',
    'mirrored': '符号是否可能镜像（行列互换）：no / yes / any。',
    'model_type': 'QR Code 模型类型：1（旧 Model 1）、2（Model 2）或 any/0（两者均可）。',
    'module_aspect': '将 module_aspect_min 与 module_aspect_max 设为同一值（PDF417 模块高宽比）。',
    'module_aspect_max': '模块最大高宽比（高/宽，PDF417），范围 0.5~20。',
    'module_aspect_min': '模块最小高宽比（高/宽，PDF417），范围 0.5~20。',
    'module_gap': '将 module_gap_min 与 module_gap_max 设为同一值（模块间间隙）。',
    'module_gap_max': '模块间最大间隙：no / small / big。',
    'module_gap_min': '模块间最小间隙：no / small / big。',
    'module_grid': '模块尺寸是否允许变化：fixed（等间距网格）/ variable / any。',
    'module_size': '将 module_size_min 与 module_size_max 设为同一值（模块像素尺寸）。',
    'module_size_max': '图像中模块的最大尺寸（像素），建议实际模块至少 3~4 像素。',
    'module_size_min': '图像中模块的最小尺寸（像素），建议实际模块至少 3~4 像素。',
    'module_width': '将 module_width_min 与 module_width_max 设为同一值（PDF417 模块宽度）。',
    'module_width_max': 'PDF417 模块最大宽度（像素）。',
    'module_width_min': 'PDF417 模块最小宽度（像素）。',
    'persistence': '是否把搜索中间结果持久保存在模型中：1=持久（省内存复用），0=临时，only_decoded_data 仅保留已解码数据。',
    'polarity': '符号极性：dark_on_light（亮底深色码）/ light_on_dark（深底亮色码）/ any。',
    'position_pattern_min': '生成新候选至少需可见的 QR 定位图形数量（2~3，默认 3，enhanced 档为 2）。',
    'quality_isoiec15415_aperture_size': 'ISO/IEC 15415 质量分级所用合成孔径（圆均值滤波）直径，单位像素。',
    'quality_isoiec15415_decode_algorithm': '质量分级中计算模块网格的算法：robust（哑光纸工艺控制推荐）/ standard。',
    'quality_isoiec15415_reflectance_reference': '符号对比度分级所用的反射率参考灰度值。',
    'slant_max': 'L 形定位图形偏离直角的最大角度（弧度），用于 Data Matrix。',
    'small_modules_robustness': '小模块符号的解码鲁棒性：high 提高小模块符号的解码成功率。',
    'strict_model': '是否拒绝可解码但不符合模型尺寸限制的符号：yes=严格拒绝。',
    'strict_quiet_zone': '是否严格校验符号静区缺陷：yes=类似质量检验的方式验证。',
    'string_encoding': '符号内字符串的编码：utf8 / locale / latin1 / raw，必要时自动转码。',
    'symbol_cols': '将 symbol_cols_min 与 symbol_cols_max 设为同一值（模块列数，奇数）。',
    'symbol_cols_max': '符号最大模块列数（奇数，5~999）。',
    'symbol_cols_min': '符号最小模块列数（奇数，5~999）。',
    'symbol_rows': '将 symbol_rows_min 与 symbol_rows_max 设为同一值（模块行数）。',
    'symbol_rows_max': '符号最大模块行数（PDF417 为偶数范围）。',
    'symbol_rows_min': '符号最小模块行数（PDF417 为偶数范围）。',
    'symbol_shape': '符号形状限制：rectangle / square / any；注意设置后尺寸限制可能联动变化。',
    'symbol_size': '将 symbol_size_min 与 symbol_size_max 设为同一值（符号边长，模块数）。',
    'symbol_size_max': '符号最大边长（模块数）。',
    'symbol_size_min': '符号最小边长（模块数）。',
    'timeout': '搜索超时时间（毫秒），超时后返回已得结果，用于保证最大节拍。',
    'trained': '把指定参数标记为已训练，下次训练不再覆盖（必要时仅扩展参数范围）。',
    'version': '将 version_min 与 version_max 设为同一值（Micro QR 符号版本）。',
    'version_max': 'Micro QR 最大符号版本（1~4）。',
    'version_min': 'Micro QR 最小符号版本（1~4，版本与符号 11x11~17x17 尺寸对应）。',
    # ---- 1D ----
    'barcode_height_min': '条码最小高度（像素），-1 表示由其他参数自动推导。',
    'barcode_width_max': '条码最大宽度；设置该值会激活此参数的训练模式。',
    'barcode_width_min': '条码最小宽度；设置该值会激活此参数的训练模式。',
    'check_char': '可选校验字符码制（Code 39、Codabar 等）的校验字符解释方式：absent / present / skip。',
    'composite_code': '是否查找并解码 GS1 复合组件（CC-A/B），多数 GS1 一维码可附带 2D 复合码。',
    'element_size_max': '基本元素（条/空）最大尺寸；设置该值会激活此参数的训练模式。',
    'element_size_min': '基本元素（条/空）最小尺寸；设置该值会激活此参数的训练模式。',
    'element_size_variable': '码内最小元素尺寸是否可变（透视变形、表面形变导致）：false / true。',
    'majority_voting': '解码结果选择模式：false 时达到最少相同扫描线数即返回；true 时全部扫描线参与多数表决。',
    'meas_thresh': '扫描线内边缘测量的相对阈值；设置该值会激活此参数的训练模式。',
    'meas_thresh_abs': '扫描线内边缘测量的绝对阈值；设置该值会激活此参数的训练模式。',
    'merge_scanlines': '扫描线不足时尝试合并现有扫描线再解码，提升部分遮挡/破损码的读取率。',
    'min_code_length': '解码字符的最小长度，低于该值的结果被丢弃，用于过滤误检。',
    'min_identical_scanlines': '认定解码成功所需返回相同数据的最少扫描线数，调大可降低误检。',
    'num_scanlines': '单个候选码使用的最大扫描线数，0 表示内部自动（通常为 10 条）。',
    'orientation': '条码方向角（度）；设置该值会同时激活 orientation 与 orientation_tol 的训练模式。',
    'orientation_tol': '条码方向容差（度），仅在设置了 orientation 时有效。',
    'quality_isoiec15416_reflectance_reference': 'ISO/IEC 15416 质量分级（符号对比度、最小反射率等）所用的反射率参考灰度值。',
    'quiet_zone': '静区校验：启用后左右静区内出现意外条时拒绝该扫描线，可选仅校验一侧。',
    'small_elements_robustness': '对小元素（小于 2 像素）条码启用鲁棒读取，牺牲部分性能换取小码检出。',
    'start_stop_tolerance': '起始/终止符搜索的严格程度：low=严格，high=宽松（提高检出但增加误检）。',
    'stop_after_result_num': '成功解码指定数量条码后停止，0 表示解码全部候选；适用于已知码数量场合。',
    'upce_encodation': 'UPC-E 输出格式：ucc-12（12 位）或 zero-suppressed（零抑制格式）。',
}


# 1D 文档中部分参数的候选值散落于正文，按文档确认的手工覆盖
VALUES_OVERRIDE = {
    'check_char': ['absent', 'present', 'preserved'],
    'composite_code': ['none', 'CC-A/B'],
    'majority_voting': ['false', 'true'],
    'quiet_zone': ['false', 'true', 'tolerant'],
    'small_elements_robustness': ['false', 'true'],
    'start_stop_tolerance': ['high', 'low'],
    'upce_encodation': ['ucc-12', 'zero-suppressed'],
}


def parse_doc(path):
    s = io.open(path, encoding='utf-8', errors='replace').read()
    s = re.sub(r'<span data-if="(?!hdevelop")[^"]*"[^>]*>.*?</span>', '', s, flags=re.S)
    s = re.sub(r'<span[^>]*>(.*?)</span>', r'\1', s, flags=re.S)
    entries = {}
    for m in re.finditer(r"<dt><b><i>'([^']+)'</i>:</b></dt>\s*<dd>(.*?)</dd>", s, flags=re.S):
        name, body = m.group(1), m.group(2)
        vm = re.search(r'<i>Values:</i>', body)
        cut = vm.start() if vm else len(body)
        desc = html.unescape(re.sub(r'<[^>]+>', '', body[:cut]))
        desc = re.sub(r'\s+', ' ', desc).strip()
        values = []
        if vm:
            para = body[vm.end():]
            end = para.find('</p>')
            if end >= 0:
                para = para[:end]
            values = re.findall(r"'([^']*)'", para)
        default = None
        dm = re.search(r"<i>Default[^:<]*(?:\s*\([^)]*\))?[^:]*:</i>", body)
        if dm:
            rest = body[dm.end():dm.end() + 400]
            q = re.search(r"'([^']*)'", rest)
            if q:
                default = q.group(1)
            else:
                plain = html.unescape(re.sub(r'<[^>]+>', '', rest))
                n = re.match(r'\s*\(?\s*(-?[\d.]+)', plain)
                if n:
                    default = n.group(1)
        entries[name] = {"desc_en": desc, "values": values, "default": default}
    return entries


def cs_str(s):
    if s is None:
        return 'null'
    return '"' + s.replace('\\', '\\\\').replace('"', '\\"') + '"'


def main():
    dc = parse_doc(DOC_DIR + r"\set_data_code_2d_param.html")
    bc = parse_doc(DOC_DIR + r"\set_bar_code_param.html")
    probe = {}
    for line in io.open(os.path.join(ROOT, 'temp', 'probe_params.txt'), encoding='utf-8'):
        line = line.strip()
        if line and ': ' in line:
            scope, names = line.split(': ', 1)
            probe[scope] = names.split(',')

    lines = []
    lines.append('using System;')
    lines.append('using System.Collections.Generic;')
    lines.append('')
    lines.append('namespace VisionFlow.Tools.Halcon')
    lines.append('{')
    lines.append('    /// <summary>HALCON 模型参数目录条目。</summary>')
    lines.append('    public sealed class HalconParamEntry')
    lines.append('    {')
    lines.append('        public string Name { get; }')
    lines.append('        public string[] Values { get; }')
    lines.append('        public string Default { get; }')
    lines.append('        public string Description { get; }')
    lines.append('        /// <summary>适用的二维码码制；null 表示全部码制。一维码条目恒为 null。</summary>')
    lines.append('        public string[] AppliesTo { get; }')
    lines.append('')
    lines.append('        public HalconParamEntry(string name, string[] values, string defaultValue, string description, string[] appliesTo)')
    lines.append('        {')
    lines.append('            Name = name;')
    lines.append('            Values = values ?? Array.Empty<string>();')
    lines.append('            Default = defaultValue;')
    lines.append('            Description = description ?? string.Empty;')
    lines.append('            AppliesTo = appliesTo;')
    lines.append('        }')
    lines.append('    }')
    lines.append('')
    lines.append('    /// <summary>')
    lines.append('    /// HALCON 读码模型参数静态目录：名称、候选值、默认值与中文说明。')
    lines.append('    /// 数据由临时探针枚举各码制 set_model_params 清单并从 HALCON 22.11 参考文档解析 Values/Default 生成。')
    lines.append('    /// scope：datacode2d:&lt;码制名&gt;（如 "datacode2d:QR Code"）或 "barcode1d"。')
    lines.append('    /// </summary>')
    lines.append('    public static class HalconParamCatalog')
    lines.append('    {')
    lines.append('        private static readonly string[] All2D =')
    lines.append('        {')
    for sym in SYMS_2D:
        lines.append('            ' + cs_str(sym) + ',')
    lines.append('        };')
    lines.append('')
    lines.append('        private static readonly HalconParamEntry[] DataCode2DEntries =')
    lines.append('        {')

    missing_zh = []
    # 2D 条目：按名称合并各码制
    per_sym = {sym: set(probe[sym]) for sym in SYMS_2D}
    all_names = sorted(set().union(*per_sym.values()))
    for name in all_names:
        syms = [sym for sym in SYMS_2D if name in per_sym[sym]]
        info = dc.get(name, {})
        values = info.get('values') or []
        default = info.get('default')
        zh = ZH.get(name)
        if zh is None:
            missing_zh.append(name)
            desc_en = info.get('desc_en', '')
            zh = desc_en[:80]
        applies = 'null' if len(syms) == len(SYMS_2D) else 'new[] { ' + ', '.join(cs_str(s) for s in syms) + ' }'
        vals = 'Array.Empty<string>()' if not values else 'new[] { ' + ', '.join(cs_str(v) for v in values) + ' }'
        lines.append('            new HalconParamEntry(' + cs_str(name) + ', ' + vals + ', ' + cs_str(default) + ', ' + cs_str(zh) + ', ' + applies + '),')
    lines.append('        };')
    lines.append('')
    lines.append('        private static readonly HalconParamEntry[] Barcode1DEntries =')
    lines.append('        {')
    for name in sorted(probe['barcode1d']):
        info = bc.get(name, {})
        values = VALUES_OVERRIDE.get(name) or info.get('values') or []
        default = info.get('default')
        zh = ZH.get(name)
        if zh is None:
            missing_zh.append('1d:' + name)
            zh = (info.get('desc_en') or '')[:80]
        vals = 'Array.Empty<string>()' if not values else 'new[] { ' + ', '.join(cs_str(v) for v in values) + ' }'
        lines.append('            new HalconParamEntry(' + cs_str(name) + ', ' + vals + ', ' + cs_str(default) + ', ' + cs_str(zh) + ', null),')
    lines.append('        };')
    lines.append('')
    lines.append('        /// <summary>获取指定 scope 的目录条目；未知 scope 返回空数组。</summary>')
    lines.append('        public static IReadOnlyList<HalconParamEntry> GetEntries(string scope)')
    lines.append('        {')
    lines.append('            if (string.Equals(scope, "barcode1d", StringComparison.Ordinal))')
    lines.append('            {')
    lines.append('                return Barcode1DEntries;')
    lines.append('            }')
    lines.append('            const string prefix = "datacode2d:";')
    lines.append('            if (scope != null && scope.StartsWith(prefix, StringComparison.Ordinal))')
    lines.append('            {')
    lines.append('                string symbology = scope.Substring(prefix.Length);')
    lines.append('                var result = new List<HalconParamEntry>();')
    lines.append('                foreach (HalconParamEntry entry in DataCode2DEntries)')
    lines.append('                {')
    lines.append('                    if (entry.AppliesTo == null || Array.IndexOf(entry.AppliesTo, symbology) >= 0)')
    lines.append('                    {')
    lines.append('                        result.Add(entry);')
    lines.append('                    }')
    lines.append('                }')
    lines.append('                return result;')
    lines.append('            }')
    lines.append('            return Array.Empty<HalconParamEntry>();')
    lines.append('        }')
    lines.append('    }')
    lines.append('}')

    out_path = os.path.join(ROOT, 'VisionFlow.Tools', 'Halcon', 'ParamCatalog.cs')
    io.open(out_path, 'w', encoding='utf-8', newline='\r\n').write('\n'.join(lines) + '\n')
    print('written:', out_path)
    print('2D entries:', len(all_names), '1D entries:', len(probe['barcode1d']))
    print('missing zh:', missing_zh)
    nodoc = [n for n in all_names if n not in dc]
    print('2D names not in doc:', nodoc)


if __name__ == '__main__':
    main()
