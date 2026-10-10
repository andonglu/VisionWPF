using System;
using System.Collections.Generic;

namespace VisionFlow.Tools.Halcon
{
    /// <summary>HALCON 模型参数目录条目。</summary>
    public sealed class HalconParamEntry
    {
        public string Name { get; }
        public string[] Values { get; }
        public string Default { get; }
        public string Description { get; }
        /// <summary>适用的二维码码制；null 表示全部码制。一维码条目恒为 null。</summary>
        public string[] AppliesTo { get; }

        public HalconParamEntry(string name, string[] values, string defaultValue, string description, string[] appliesTo)
        {
            Name = name;
            Values = values ?? Array.Empty<string>();
            Default = defaultValue;
            Description = description ?? string.Empty;
            AppliesTo = appliesTo;
        }
    }

    /// <summary>
    /// HALCON 读码模型参数静态目录：名称、候选值、默认值与中文说明。
    /// 数据由临时探针枚举各码制 set_model_params 清单并从 HALCON 22.11 参考文档解析 Values/Default 生成。
    /// scope：datacode2d:&lt;码制名&gt;（如 "datacode2d:QR Code"）或 "barcode1d"。
    /// </summary>
    public static class HalconParamCatalog
    {
        private static readonly string[] All2D =
        {
            "QR Code",
            "Data Matrix ECC 200",
            "PDF417",
            "Aztec Code",
            "Micro QR Code",
            "DotCode",
            "GS1 QR Code",
            "GS1 DataMatrix",
        };

        private static readonly HalconParamEntry[] DataCode2DEntries =
        {
            new HalconParamEntry("abort", Array.Empty<string>(), "true", "允许从另一线程中止正在运行的搜索（find_data_code_2d / find_bar_code）。", null),
            new HalconParamEntry("additional_levels", Array.Empty<string>(), "0", "Aztec Code 在最小/最大模块尺寸推导的搜索层数之外额外增加的搜索层数，提高难检符号的检出率。", new[] { "Aztec Code" }),
            new HalconParamEntry("candidate_selection", new[] { "default", "extensive", "all" }, "default", "候选区域选择策略：extensive 增加候选区域数量以提高检出率，all 保留全部候选。", new[] { "Data Matrix ECC 200", "DotCode", "GS1 DataMatrix" }),
            new HalconParamEntry("contrast_min", Array.Empty<string>(), "30", "符号前景与背景的最小灰度对比度（差值 1~255），数值 5 以上可优化候选搜索。", new[] { "QR Code", "PDF417", "Aztec Code", "Micro QR Code", "GS1 QR Code" }),
            new HalconParamEntry("contrast_tolerance", new[] { "low", "high", "any" }, "low", "对局部对比度变化（眩光、反光等）的容忍度：low / high / any。", new[] { "Data Matrix ECC 200", "GS1 DataMatrix" }),
            new HalconParamEntry("decoding_scheme", new[] { "default", "raw" }, "default", "Data Matrix 解码方式；raw 用于读取符合 ISO/IEC 16022 但数据编码自定义的符号。", new[] { "Data Matrix ECC 200", "GS1 DataMatrix" }),
            new HalconParamEntry("default_parameters", new[] { "standard_recognition", "enhanced_recognition", "maximum_recognition" }, "standard_recognition", "将全部模型参数重置为 standard_recognition / enhanced_recognition / maximum_recognition 默认档，并重置训练状态。", null),
            new HalconParamEntry("discard_undecoded_candidates", Array.Empty<string>(), null, "是否丢弃无法解码的候选符号。", null),
            new HalconParamEntry("finder_pattern_tolerance", new[] { "low", "high" }, "low", "对定位图形缺损或部分遮挡的容忍度：low / high。", new[] { "Data Matrix ECC 200", "Aztec Code", "GS1 DataMatrix" }),
            new HalconParamEntry("format", Array.Empty<string>(), "compact full_range", "Aztec Code 格式，取值为 compact、full_range、rune 的空格组合。", new[] { "Aztec Code" }),
            new HalconParamEntry("max_allowed_error_correction", Array.Empty<string>(), "1.0", "DotCode 允许使用的最大纠错能力，调低可减少部分覆盖真实符号的误检。", new[] { "DotCode" }),
            new HalconParamEntry("mirrored", new[] { "no", "yes", "any" }, "any", "符号是否可能镜像（行列互换）：no / yes / any。", null),
            new HalconParamEntry("model_type", new[] { "any", "0" }, "any", "QR Code 模型类型：1（旧 Model 1）、2（Model 2）或 any/0（两者均可）。", new[] { "QR Code", "Micro QR Code", "GS1 QR Code" }),
            new HalconParamEntry("module_aspect", Array.Empty<string>(), null, "将 module_aspect_min 与 module_aspect_max 设为同一值（PDF417 模块高宽比）。", new[] { "PDF417" }),
            new HalconParamEntry("module_aspect_max", Array.Empty<string>(), "4.0", "模块最大高宽比（高/宽，PDF417），范围 0.5~20。", new[] { "PDF417" }),
            new HalconParamEntry("module_aspect_min", Array.Empty<string>(), "1.0", "模块最小高宽比（高/宽，PDF417），范围 0.5~20。", new[] { "PDF417" }),
            new HalconParamEntry("module_gap", Array.Empty<string>(), null, "将 module_gap_min 与 module_gap_max 设为同一值（模块间间隙）。", new[] { "QR Code", "Data Matrix ECC 200", "Aztec Code", "Micro QR Code", "DotCode", "GS1 QR Code", "GS1 DataMatrix" }),
            new HalconParamEntry("module_gap_max", new[] { "no", "small", "big" }, "no", "模块间最大间隙：no / small / big。", new[] { "QR Code", "Data Matrix ECC 200", "Aztec Code", "Micro QR Code", "DotCode", "GS1 QR Code", "GS1 DataMatrix" }),
            new HalconParamEntry("module_gap_min", new[] { "no", "small", "big", "big" }, "no", "模块间最小间隙：no / small / big。", new[] { "QR Code", "Data Matrix ECC 200", "Aztec Code", "Micro QR Code", "DotCode", "GS1 QR Code", "GS1 DataMatrix" }),
            new HalconParamEntry("module_grid", new[] { "fixed", "variable", "any" }, "fixed", "模块尺寸是否允许变化：fixed（等间距网格）/ variable / any。", new[] { "Data Matrix ECC 200", "GS1 DataMatrix" }),
            new HalconParamEntry("module_size", Array.Empty<string>(), null, "将 module_size_min 与 module_size_max 设为同一值（模块像素尺寸）。", new[] { "QR Code", "Data Matrix ECC 200", "Aztec Code", "Micro QR Code", "DotCode", "GS1 QR Code", "GS1 DataMatrix" }),
            new HalconParamEntry("module_size_max", Array.Empty<string>(), "20", "图像中模块的最大尺寸（像素），建议实际模块至少 3~4 像素。", new[] { "QR Code", "Data Matrix ECC 200", "Aztec Code", "Micro QR Code", "DotCode", "GS1 QR Code", "GS1 DataMatrix" }),
            new HalconParamEntry("module_size_min", Array.Empty<string>(), "6", "图像中模块的最小尺寸（像素），建议实际模块至少 3~4 像素。", new[] { "QR Code", "Data Matrix ECC 200", "Aztec Code", "Micro QR Code", "DotCode", "GS1 QR Code", "GS1 DataMatrix" }),
            new HalconParamEntry("module_width", Array.Empty<string>(), null, "将 module_width_min 与 module_width_max 设为同一值（PDF417 模块宽度）。", new[] { "PDF417" }),
            new HalconParamEntry("module_width_max", Array.Empty<string>(), "15", "PDF417 模块最大宽度（像素）。", new[] { "PDF417" }),
            new HalconParamEntry("module_width_min", Array.Empty<string>(), "3", "PDF417 模块最小宽度（像素）。", new[] { "PDF417" }),
            new HalconParamEntry("persistence", Array.Empty<string>(), "0", "是否把搜索中间结果持久保存在模型中：1=持久（省内存复用），0=临时，only_decoded_data 仅保留已解码数据。", null),
            new HalconParamEntry("polarity", new[] { "dark_on_light", "light_on_dark", "any" }, "dark_on_light", "符号极性：dark_on_light（亮底深色码）/ light_on_dark（深底亮色码）/ any。", null),
            new HalconParamEntry("position_pattern_min", Array.Empty<string>(), "3", "生成新候选至少需可见的 QR 定位图形数量（2~3，默认 3，enhanced 档为 2）。", new[] { "QR Code", "Micro QR Code", "GS1 QR Code" }),
            new HalconParamEntry("quality_isoiec15415_aperture_size", Array.Empty<string>(), "0.8", "ISO/IEC 15415 质量分级所用合成孔径（圆均值滤波）直径，单位像素。", new[] { "QR Code", "Data Matrix ECC 200", "PDF417", "Aztec Code", "Micro QR Code", "GS1 QR Code", "GS1 DataMatrix" }),
            new HalconParamEntry("quality_isoiec15415_decode_algorithm", Array.Empty<string>(), null, "质量分级中计算模块网格的算法：robust（哑光纸工艺控制推荐）/ standard。", new[] { "Data Matrix ECC 200", "GS1 DataMatrix" }),
            new HalconParamEntry("quality_isoiec15415_reflectance_reference", Array.Empty<string>(), "255", "符号对比度分级所用的反射率参考灰度值。", new[] { "QR Code", "Data Matrix ECC 200", "PDF417", "Aztec Code", "Micro QR Code", "GS1 QR Code", "GS1 DataMatrix" }),
            new HalconParamEntry("slant_max", Array.Empty<string>(), "0.1745", "L 形定位图形偏离直角的最大角度（弧度），用于 Data Matrix。", new[] { "Data Matrix ECC 200", "GS1 DataMatrix" }),
            new HalconParamEntry("small_modules_robustness", new[] { "low", "high" }, "low", "小模块符号的解码鲁棒性：high 提高小模块符号的解码成功率。", new[] { "QR Code", "Data Matrix ECC 200", "PDF417", "Aztec Code", "Micro QR Code", "GS1 QR Code", "GS1 DataMatrix" }),
            new HalconParamEntry("strict_model", new[] { "yes", "no" }, "yes", "是否拒绝可解码但不符合模型尺寸限制的符号：yes=严格拒绝。", null),
            new HalconParamEntry("strict_quiet_zone", new[] { "yes", "no" }, "no", "是否严格校验符号静区缺陷：yes=类似质量检验的方式验证。", new[] { "QR Code", "Data Matrix ECC 200", "PDF417", "Micro QR Code", "DotCode", "GS1 QR Code", "GS1 DataMatrix" }),
            new HalconParamEntry("string_encoding", new[] { "utf8", "locale", "latin1", "raw" }, "latin1", "符号内字符串的编码：utf8 / locale / latin1 / raw，必要时自动转码。", null),
            new HalconParamEntry("symbol_cols", Array.Empty<string>(), null, "将 symbol_cols_min 与 symbol_cols_max 设为同一值（模块列数，奇数）。", new[] { "Data Matrix ECC 200", "PDF417", "DotCode", "GS1 DataMatrix" }),
            new HalconParamEntry("symbol_cols_max", Array.Empty<string>(), "999", "符号最大模块列数（奇数，5~999）。", new[] { "Data Matrix ECC 200", "PDF417", "DotCode", "GS1 DataMatrix" }),
            new HalconParamEntry("symbol_cols_min", Array.Empty<string>(), "5", "符号最小模块列数（奇数，5~999）。", new[] { "Data Matrix ECC 200", "PDF417", "DotCode", "GS1 DataMatrix" }),
            new HalconParamEntry("symbol_rows", Array.Empty<string>(), null, "将 symbol_rows_min 与 symbol_rows_max 设为同一值（模块行数）。", new[] { "Data Matrix ECC 200", "PDF417", "DotCode", "GS1 DataMatrix" }),
            new HalconParamEntry("symbol_rows_max", Array.Empty<string>(), "998", "符号最大模块行数（PDF417 为偶数范围）。", new[] { "Data Matrix ECC 200", "PDF417", "DotCode", "GS1 DataMatrix" }),
            new HalconParamEntry("symbol_rows_min", Array.Empty<string>(), "4", "符号最小模块行数（PDF417 为偶数范围）。", new[] { "Data Matrix ECC 200", "PDF417", "DotCode", "GS1 DataMatrix" }),
            new HalconParamEntry("symbol_shape", new[] { "rectangle", "square", "any" }, null, "符号形状限制：rectangle / square / any；注意设置后尺寸限制可能联动变化。", new[] { "Data Matrix ECC 200", "GS1 DataMatrix" }),
            new HalconParamEntry("symbol_size", Array.Empty<string>(), null, "将 symbol_size_min 与 symbol_size_max 设为同一值（符号边长，模块数）。", new[] { "QR Code", "Data Matrix ECC 200", "Aztec Code", "Micro QR Code", "GS1 QR Code", "GS1 DataMatrix" }),
            new HalconParamEntry("symbol_size_max", Array.Empty<string>(), "151", "符号最大边长（模块数）。", new[] { "QR Code", "Data Matrix ECC 200", "Aztec Code", "Micro QR Code", "GS1 QR Code", "GS1 DataMatrix" }),
            new HalconParamEntry("symbol_size_min", Array.Empty<string>(), "11", "符号最小边长（模块数）。", new[] { "QR Code", "Data Matrix ECC 200", "Aztec Code", "Micro QR Code", "GS1 QR Code", "GS1 DataMatrix" }),
            new HalconParamEntry("timeout", Array.Empty<string>(), "false", "搜索超时时间（毫秒），超时后返回已得结果，用于保证最大节拍。", null),
            new HalconParamEntry("trained", Array.Empty<string>(), null, "把指定参数标记为已训练，下次训练不再覆盖（必要时仅扩展参数范围）。", null),
            new HalconParamEntry("version", Array.Empty<string>(), null, "将 version_min 与 version_max 设为同一值（Micro QR 符号版本）。", new[] { "QR Code", "Micro QR Code", "GS1 QR Code" }),
            new HalconParamEntry("version_max", Array.Empty<string>(), "4", "Micro QR 最大符号版本（1~4）。", new[] { "QR Code", "Micro QR Code", "GS1 QR Code" }),
            new HalconParamEntry("version_min", Array.Empty<string>(), "1", "Micro QR 最小符号版本（1~4，版本与符号 11x11~17x17 尺寸对应）。", new[] { "QR Code", "Micro QR Code", "GS1 QR Code" }),
        };

        private static readonly HalconParamEntry[] Barcode1DEntries =
        {
            new HalconParamEntry("barcode_height_min", Array.Empty<string>(), "-1", "条码最小高度（像素），-1 表示由其他参数自动推导。", null),
            new HalconParamEntry("barcode_width_max", Array.Empty<string>(), null, "条码最大宽度；设置该值会激活此参数的训练模式。", null),
            new HalconParamEntry("barcode_width_min", Array.Empty<string>(), null, "条码最小宽度；设置该值会激活此参数的训练模式。", null),
            new HalconParamEntry("check_char", new[] { "absent", "present", "preserved" }, "absent", "可选校验字符码制（Code 39、Codabar 等）的校验字符解释方式：absent / present / skip。", null),
            new HalconParamEntry("composite_code", new[] { "none", "CC-A/B" }, "none", "是否查找并解码 GS1 复合组件（CC-A/B），多数 GS1 一维码可附带 2D 复合码。", null),
            new HalconParamEntry("contrast_min", Array.Empty<string>(), "0", "符号前景与背景的最小灰度对比度（差值 1~255），数值 5 以上可优化候选搜索。", null),
            new HalconParamEntry("element_size_max", Array.Empty<string>(), null, "基本元素（条/空）最大尺寸；设置该值会激活此参数的训练模式。", null),
            new HalconParamEntry("element_size_min", Array.Empty<string>(), null, "基本元素（条/空）最小尺寸；设置该值会激活此参数的训练模式。", null),
            new HalconParamEntry("element_size_variable", new[] { "false", "true" }, "false", "码内最小元素尺寸是否可变（透视变形、表面形变导致）：false / true。", null),
            new HalconParamEntry("majority_voting", new[] { "false", "true" }, "false", "解码结果选择模式：false 时达到最少相同扫描线数即返回；true 时全部扫描线参与多数表决。", null),
            new HalconParamEntry("meas_thresh", Array.Empty<string>(), null, "扫描线内边缘测量的相对阈值；设置该值会激活此参数的训练模式。", null),
            new HalconParamEntry("meas_thresh_abs", Array.Empty<string>(), null, "扫描线内边缘测量的绝对阈值；设置该值会激活此参数的训练模式。", null),
            new HalconParamEntry("merge_scanlines", Array.Empty<string>(), "true", "扫描线不足时尝试合并现有扫描线再解码，提升部分遮挡/破损码的读取率。", null),
            new HalconParamEntry("min_code_length", Array.Empty<string>(), null, "解码字符的最小长度，低于该值的结果被丢弃，用于过滤误检。", null),
            new HalconParamEntry("min_identical_scanlines", Array.Empty<string>(), null, "认定解码成功所需返回相同数据的最少扫描线数，调大可降低误检。", null),
            new HalconParamEntry("num_scanlines", Array.Empty<string>(), "0", "单个候选码使用的最大扫描线数，0 表示内部自动（通常为 10 条）。", null),
            new HalconParamEntry("orientation", Array.Empty<string>(), null, "条码方向角（度）；设置该值会同时激活 orientation 与 orientation_tol 的训练模式。", null),
            new HalconParamEntry("orientation_tol", Array.Empty<string>(), "90.0", "条码方向容差（度），仅在设置了 orientation 时有效。", null),
            new HalconParamEntry("persistence", Array.Empty<string>(), "0", "是否把搜索中间结果持久保存在模型中：1=持久（省内存复用），0=临时，only_decoded_data 仅保留已解码数据。", null),
            new HalconParamEntry("quality_isoiec15416_reflectance_reference", Array.Empty<string>(), "255", "ISO/IEC 15416 质量分级（符号对比度、最小反射率等）所用的反射率参考灰度值。", null),
            new HalconParamEntry("quiet_zone", new[] { "false", "true", "tolerant" }, "false", "静区校验：启用后左右静区内出现意外条时拒绝该扫描线，可选仅校验一侧。", null),
            new HalconParamEntry("small_elements_robustness", new[] { "false", "true" }, "true", "对小元素（小于 2 像素）条码启用鲁棒读取，牺牲部分性能换取小码检出。", null),
            new HalconParamEntry("start_stop_tolerance", new[] { "high", "low" }, "high", "起始/终止符搜索的严格程度：low=严格，high=宽松（提高检出但增加误检）。", null),
            new HalconParamEntry("stop_after_result_num", Array.Empty<string>(), "0", "成功解码指定数量条码后停止，0 表示解码全部候选；适用于已知码数量场合。", null),
            new HalconParamEntry("timeout", Array.Empty<string>(), "false", "搜索超时时间（毫秒），超时后返回已得结果，用于保证最大节拍。", null),
            new HalconParamEntry("upce_encodation", new[] { "ucc-12", "zero-suppressed" }, "ucc-12", "UPC-E 输出格式：ucc-12（12 位）或 zero-suppressed（零抑制格式）。", null),
        };

        /// <summary>获取指定 scope 的目录条目；未知 scope 返回空数组。</summary>
        public static IReadOnlyList<HalconParamEntry> GetEntries(string scope)
        {
            if (string.Equals(scope, "barcode1d", StringComparison.Ordinal))
            {
                return Barcode1DEntries;
            }
            const string prefix = "datacode2d:";
            if (scope != null && scope.StartsWith(prefix, StringComparison.Ordinal))
            {
                string symbology = scope.Substring(prefix.Length);
                var result = new List<HalconParamEntry>();
                foreach (HalconParamEntry entry in DataCode2DEntries)
                {
                    if (entry.AppliesTo == null || Array.IndexOf(entry.AppliesTo, symbology) >= 0)
                    {
                        result.Add(entry);
                    }
                }
                return result;
            }
            return Array.Empty<HalconParamEntry>();
        }
    }
}
