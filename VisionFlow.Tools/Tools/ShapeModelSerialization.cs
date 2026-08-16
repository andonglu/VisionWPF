using System;
using System.IO;
using System.Runtime.InteropServices;
using HalconDotNet;

namespace VisionFlow.Tools
{
    public static class ShapeModelSerialization
    {
        public static byte[] Serialize(HTuple modelId)
        {
            string modelPath = CreateTempShapeModelPath();
            try
            {
                HOperatorSet.WriteShapeModel(modelId, modelPath);
                return File.ReadAllBytes(modelPath);
            }
            finally
            {
                DeleteTempFile(modelPath);
            }
        }

        public static HTuple Deserialize(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
            {
                throw new InvalidOperationException("模板模型数据为空");
            }

            if (TryReadShapeModelBytes(bytes, out HTuple modelId))
            {
                return modelId;
            }

            return DeserializeSerializedItem(bytes);
        }

        public static byte[] ReadShapeModelFileAsBytes(string modelPath)
        {
            return File.ReadAllBytes(modelPath);
        }

        public static byte[] SerializeNcc(HTuple modelId)
        {
            string modelPath = CreateTempModelPath(".ncm");
            try
            {
                HOperatorSet.WriteNccModel(modelId, modelPath);
                return File.ReadAllBytes(modelPath);
            }
            finally
            {
                DeleteTempFile(modelPath);
            }
        }

        public static HTuple DeserializeNcc(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
            {
                throw new InvalidOperationException("灰度模板模型数据为空");
            }

            string modelPath = CreateTempModelPath(".ncm");
            try
            {
                File.WriteAllBytes(modelPath, bytes);
                HOperatorSet.ReadNccModel(modelPath, out HTuple modelId);
                return modelId;
            }
            finally
            {
                DeleteTempFile(modelPath);
            }
        }

        public static byte[] SerializeDeformable(HTuple modelId)
        {
            string modelPath = CreateTempModelPath(".dfm");
            try
            {
                HOperatorSet.WriteDeformableModel(modelId, modelPath);
                return File.ReadAllBytes(modelPath);
            }
            finally
            {
                DeleteTempFile(modelPath);
            }
        }

        public static HTuple DeserializeDeformable(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
            {
                throw new InvalidOperationException("变形模板模型数据为空");
            }

            string modelPath = CreateTempModelPath(".dfm");
            try
            {
                File.WriteAllBytes(modelPath, bytes);
                HOperatorSet.ReadDeformableModel(modelPath, out HTuple modelId);
                return modelId;
            }
            finally
            {
                DeleteTempFile(modelPath);
            }
        }

        private static bool TryReadShapeModelBytes(byte[] bytes, out HTuple modelId)
        {
            modelId = null;
            string modelPath = CreateTempShapeModelPath();
            try
            {
                File.WriteAllBytes(modelPath, bytes);
                HOperatorSet.ReadShapeModel(modelPath, out modelId);
                return true;
            }
            catch
            {
                modelId = null;
                return false;
            }
            finally
            {
                DeleteTempFile(modelPath);
            }
        }

        private static HTuple DeserializeSerializedItem(byte[] bytes)
        {
            IntPtr pointer = Marshal.AllocHGlobal(bytes.Length);
            HTuple serializedItem = null;
            try
            {
                Marshal.Copy(bytes, 0, pointer, bytes.Length);
                HOperatorSet.CreateSerializedItemPtr(new HTuple(pointer.ToInt64()), bytes.Length, "true", out serializedItem);
                HOperatorSet.DeserializeShapeModel(serializedItem, out HTuple modelId);
                return modelId;
            }
            finally
            {
                if (serializedItem != null)
                {
                    HOperatorSet.ClearSerializedItem(serializedItem);
                }
                Marshal.FreeHGlobal(pointer);
            }
        }

        private static string CreateTempShapeModelPath()
        {
            return CreateTempModelPath(".shm");
        }

        private static string CreateTempModelPath(string extension)
        {
            return Path.Combine(Path.GetTempPath(), "visionflow-model-" + Guid.NewGuid().ToString("N") + extension);
        }

        private static void DeleteTempFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }
    }
}
