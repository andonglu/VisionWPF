using System;
using System.Runtime.InteropServices;
using HalconDotNet;

namespace VisionFlow.Tools
{
    public static class ShapeModelSerialization
    {
        public static byte[] Serialize(HTuple modelId)
        {
            HOperatorSet.SerializeShapeModel(modelId, out HTuple serializedItem);
            try
            {
                HOperatorSet.GetSerializedItemPtr(serializedItem, out HTuple pointer, out HTuple size);
                byte[] bytes = new byte[size.I];
                Marshal.Copy(new IntPtr(pointer.L), bytes, 0, bytes.Length);
                return bytes;
            }
            finally
            {
                HOperatorSet.ClearSerializedItem(serializedItem);
            }
        }

        public static HTuple Deserialize(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
            {
                throw new InvalidOperationException("模板模型数据为空");
            }

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

        public static byte[] ReadShapeModelFileAsBytes(string modelPath)
        {
            HOperatorSet.ReadShapeModel(modelPath, out HTuple modelId);
            try
            {
                return Serialize(modelId);
            }
            finally
            {
                HOperatorSet.ClearShapeModel(modelId);
            }
        }
    }
}
