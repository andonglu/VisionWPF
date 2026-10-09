using System;
using System.Collections.Generic;
using System.IO;
using HalconDotNet;

namespace VisionFlow.Tools
{
    /// <summary>
    /// 深度学习专用工具的公共帮助层（§14.1 / §14.4）：
    /// 模型路径解析、设备选择、模型参数读取、类型规范化、预处理配方与样本构造、
    /// HALCON 错误码（#9001 / #3359 / #7783）转中文。供 dl-detect 及后续 dl-classify / dl-segment 复用。
    /// </summary>
    internal static class DeepLearningToolShared
    {
        /// <summary>.hdl 模型路径解析：支持环境变量、绝对路径、相对当前工作目录或应用目录的相对路径；找不到时抛中文错误。</summary>
        public static string ResolveExistingModelPath(string path)
        {
            foreach (string candidate in EnumerateModelPathCandidates(path))
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            throw new InvalidOperationException($"深度学习模型文件不存在：{path}");
        }

        public static IEnumerable<string> EnumerateModelPathCandidates(string path)
        {
            string text = Environment.ExpandEnvironmentVariables((path ?? string.Empty).Trim().Trim('"'));
            if (text.Length == 0)
            {
                yield break;
            }

            if (Path.IsPathRooted(text))
            {
                yield return Path.GetFullPath(text);
                yield break;
            }

            yield return Path.GetFullPath(text, Environment.CurrentDirectory);
            yield return Path.GetFullPath(text, AppContext.BaseDirectory);
        }

        /// <summary>设备选择：Auto 优先 GPU，失败回退 CPU；返回实际使用的设备类型名。</summary>
        public static string ConfigureDevice(HTuple modelHandle, DeepLearningDevicePreference preference)
        {
            if (preference == DeepLearningDevicePreference.Cpu)
            {
                return SetModelDevice(modelHandle, "cpu");
            }
            if (preference == DeepLearningDevicePreference.Gpu)
            {
                return SetModelDevice(modelHandle, "gpu");
            }

            try
            {
                return SetModelDevice(modelHandle, "gpu");
            }
            catch (InvalidOperationException)
            {
                return SetModelDevice(modelHandle, "cpu");
            }
        }

        private static string SetModelDevice(HTuple modelHandle, string deviceType)
        {
            HOperatorSet.QueryAvailableDlDevices("runtime", deviceType, out HTuple devices);
            if (devices == null || devices.Length <= 0)
            {
                throw new InvalidOperationException($"未找到可用的深度学习 {deviceType.ToUpperInvariant()} 运行设备");
            }

            HTuple selected = devices[0];
            HOperatorSet.SetDlModelParam(modelHandle, "device", selected);
            HOperatorSet.GetDlDeviceParam(selected, "type", out HTuple actualType);
            return actualType.Length > 0 ? actualType[0].S : deviceType;
        }

        /// <summary>模型 type 参数规范化：classification / detection / segmentation，其余原样返回（非空时）。</summary>
        public static string NormalizeModelType(string modelType)
        {
            string text = (modelType ?? string.Empty).Trim();
            if (text.Equals("classification", StringComparison.OrdinalIgnoreCase))
            {
                return "classification";
            }
            if (text.Equals("detection", StringComparison.OrdinalIgnoreCase) || text.Equals("object_detection", StringComparison.OrdinalIgnoreCase))
            {
                return "detection";
            }
            if (text.Equals("segmentation", StringComparison.OrdinalIgnoreCase))
            {
                return "segmentation";
            }
            return text.Length == 0 ? "generic" : text;
        }

        public static string ReadStringParam(HTuple modelHandle, string key, string fallback)
        {
            try
            {
                HOperatorSet.GetDlModelParam(modelHandle, key, out HTuple value);
                return value.Length > 0 ? value[0].S : fallback;
            }
            catch (HalconException)
            {
                return fallback;
            }
        }

        public static int ReadIntParam(HTuple modelHandle, string key, int fallback)
        {
            try
            {
                HOperatorSet.GetDlModelParam(modelHandle, key, out HTuple value);
                return value.Length > 0 ? value[0].I : fallback;
            }
            catch (HalconException)
            {
                return fallback;
            }
        }

        public static double ReadDoubleParam(HTuple modelHandle, string key, double fallback)
        {
            try
            {
                HOperatorSet.GetDlModelParam(modelHandle, key, out HTuple value);
                return value.Length > 0 ? value[0].D : fallback;
            }
            catch (HalconException)
            {
                return fallback;
            }
        }

        public static string[] ReadStringArrayParam(HTuple modelHandle, string key)
        {
            try
            {
                HOperatorSet.GetDlModelParam(modelHandle, key, out HTuple value);
                return TupleToStrings(value);
            }
            catch (HalconException)
            {
                return Array.Empty<string>();
            }
        }

        public static int[] ReadIntArrayParam(HTuple modelHandle, string key)
        {
            try
            {
                HOperatorSet.GetDlModelParam(modelHandle, key, out HTuple value);
                if (value == null || value.Length == 0)
                {
                    return Array.Empty<int>();
                }

                var result = new int[value.Length];
                for (int i = 0; i < value.Length; i++)
                {
                    result[i] = value[i].I;
                }
                return result;
            }
            catch (HalconException)
            {
                return Array.Empty<int>();
            }
        }

        private static string[] TupleToStrings(HTuple tuple)
        {
            if (tuple == null || tuple.Length == 0)
            {
                return Array.Empty<string>();
            }

            var result = new string[tuple.Length];
            for (int i = 0; i < tuple.Length; i++)
            {
                result[i] = tuple[i].S ?? string.Empty;
            }
            return result;
        }

        /// <summary>
        /// 手工预处理配方（§14.1，22.11 无 preprocess_dl_samples，必须手工三步）：
        /// ① convert_image_type 'real'；② 按模型 image_range_min/max 线性缩放（scale_image）；
        /// ③ zoom_image_size 到模型 image_width/image_height。
        /// 返回预处理后的网络输入图（调用方负责释放）；类型 / 通道不符的 HALCON 错误转中文。
        /// </summary>
        public static HObject PreprocessForDlModel(HObject image, int netWidth, int netHeight, double rangeMin, double rangeMax)
        {
            HObject real = null;
            HObject scaled = null;
            HObject net = null;
            try
            {
                HOperatorSet.ConvertImageType(image, out real, "real");
                HOperatorSet.ScaleImage(real, out scaled, (rangeMax - rangeMin) / 255.0, rangeMin);
                HOperatorSet.ZoomImageSize(scaled, out net, netWidth, netHeight, "constant");
                HObject result = net;
                net = null;
                return result;
            }
            catch (HalconException ex)
            {
                throw TranslateDlException(ex, "图像预处理");
            }
            finally
            {
                real?.Dispose();
                scaled?.Dispose();
                net?.Dispose();
            }
        }

        /// <summary>
        /// 构造单图样本（§14.1）：create_dict + set_dict_object 'image'。
        /// 返回样本字典句柄（调用方负责 clear_dict）；缺 'image' 键的 #7783 统一在本层与推理层转中文。
        /// </summary>
        public static HTuple CreateImageSample(HObject preprocessedImage)
        {
            try
            {
                HOperatorSet.CreateDict(out HTuple sample);
                HOperatorSet.SetDictObject(preprocessedImage, sample, "image");
                return sample;
            }
            catch (HalconException ex)
            {
                throw TranslateDlException(ex, "样本构造");
            }
        }

        /// <summary>apply_dl_model 包装：统一把 #9001（灰度类型不符）/#3359（通道数不符）/#7783（样本缺键）转中文错误。</summary>
        public static HTuple ApplyDlModel(HTuple modelHandle, HTuple sample)
        {
            try
            {
                // 22.11 签名：apply_dl_model(ModelHandle, DLSampleBatch, Outputs, DLResultBatch)，无 Inputs 参数。
                HOperatorSet.ApplyDlModel(modelHandle, sample, new HTuple(), out HTuple result);
                return result;
            }
            catch (HalconException ex)
            {
                throw TranslateDlException(ex, "深度学习推理");
            }
        }

        /// <summary>
        /// 读取结果字典中的图像对象键（分割结果 segmentation_image / segmentation_confidence 为图像对象，§14.2；
        /// 用 get_dict_tuple 取会报 #1302）。键缺失（模型不是分割模型或输出结构不符）时转中文错误。
        /// 返回的图像对象所有权移交调用方（负责 Dispose）。
        /// </summary>
        public static HObject GetDictObjectOrThrow(HTuple dictHandle, string key, string stage)
        {
            try
            {
                HOperatorSet.GetDictObject(out HObject value, dictHandle, key);
                return value;
            }
            catch (HalconException ex)
            {
                throw new InvalidOperationException(
                    $"{stage}失败：结果字典缺少 {key}（HALCON #{ex.GetErrorCode()}），模型可能不是分割模型或输出结构不符");
            }
        }

        /// <summary>读取结果字典中的元组键；键不存在（例如 0 检出时部分键缺省）时返回空元组而不是报错。</summary>
        public static HTuple TryGetDictTuple(HTuple dictHandle, string key)
        {
            try
            {
                HOperatorSet.GetDictTuple(dictHandle, key, out HTuple value);
                return value ?? new HTuple();
            }
            catch (HalconException)
            {
                return new HTuple();
            }
        }

        public static void TryClearDlModel(HTuple modelHandle)
        {
            try
            {
                HOperatorSet.ClearDlModel(modelHandle);
            }
            catch (HalconException)
            {
            }
        }

        /// <summary>HALCON 深度学习错误码转中文（§14.4-5）；未识别的错误原样抛出。</summary>
        public static Exception TranslateDlException(HalconException ex, string stage)
        {
            long code = ex.GetErrorCode();
            switch (code)
            {
                case 9001:
                    return new InvalidOperationException($"{stage}失败：输入图像灰度类型不符，模型要求 real 类型预处理后的图像（HALCON #9001）");
                case 3359:
                    return new InvalidOperationException($"{stage}失败：图像通道数与模型要求不符（HALCON #3359），请检查输入图像与模型的 image_num_channels 是否一致");
                case 7783:
                    return new InvalidOperationException($"{stage}失败：推理样本缺少图像数据（HALCON #7783：输入字典缺少 'image' 键）");
                default:
                    return ex;
            }
        }
    }
}
