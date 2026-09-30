using System;
using System.Collections;
using System.Collections.Generic;
using HalconDotNet;

namespace VisionFlow.Variables
{
    internal static class PreviewValueCopy
    {
        public static object Copy(object value, Dictionary<object, object> copies)
        {
            if (value == null || value is string || value.GetType().IsValueType)
            {
                return value;
            }
            if (copies.TryGetValue(value, out object existing))
            {
                return existing;
            }
            object copy;
            switch (value)
            {
                case HalconImage image:
                    copy = new HalconImage(image.Object);
                    break;
                case HalconRegion region:
                    copy = new HalconRegion(region.Object);
                    break;
                case HalconXld xld:
                    copy = new HalconXld(xld.Object);
                    break;
                case HObject _:
                    return value;
                case Array array:
                    var arrayCopy = (Array)array.Clone();
                    copies.Add(value, arrayCopy);
                    CopyArray(array, arrayCopy, new int[array.Rank], 0, copies);
                    return arrayCopy;
                case ICloneable cloneable:
                    copy = cloneable.Clone();
                    break;
                case IDictionary dictionary:
                    var dictionaryCopy = (IDictionary)CreateCollection(dictionary);
                    copies.Add(value, dictionaryCopy);
                    foreach (DictionaryEntry entry in dictionary)
                    {
                        dictionaryCopy.Add(entry.Key, Copy(entry.Value, copies));
                    }
                    return dictionaryCopy;
                case IList list when !list.IsReadOnly:
                    var listCopy = (IList)CreateCollection(list);
                    copies.Add(value, listCopy);
                    foreach (object item in list)
                    {
                        listCopy.Add(Copy(item, copies));
                    }
                    return listCopy;
                default:
                    return value;
            }
            copies.Add(value, copy);
            return copy;
        }

        private static object CreateCollection(object source)
        {
            Type type = source.GetType();
            object comparer = type.GetProperty("Comparer")?.GetValue(source);
            if (comparer != null)
            {
                foreach (var constructor in type.GetConstructors())
                {
                    var parameters = constructor.GetParameters();
                    if (parameters.Length == 1 && parameters[0].ParameterType.IsInstanceOfType(comparer))
                    {
                        return constructor.Invoke(new[] { comparer });
                    }
                }
            }
            if (type.GetConstructor(Type.EmptyTypes) == null)
            {
                throw new NotSupportedException($"预览集合类型 {type.FullName} 必须提供无参构造函数或实现 ICloneable");
            }
            return Activator.CreateInstance(type);
        }

        private static void CopyArray(Array source, Array target, int[] indices, int dimension,
            Dictionary<object, object> copies)
        {
            for (int i = source.GetLowerBound(dimension); i <= source.GetUpperBound(dimension); i++)
            {
                indices[dimension] = i;
                if (dimension + 1 == source.Rank)
                {
                    target.SetValue(Copy(source.GetValue(indices), copies), indices);
                }
                else
                {
                    CopyArray(source, target, indices, dimension + 1, copies);
                }
            }
        }
    }
}
